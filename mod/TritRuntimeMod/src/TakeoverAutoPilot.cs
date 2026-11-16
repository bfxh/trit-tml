#nullable enable

using System;
using System.Globalization;

namespace TritRuntimeMod;

/// <summary>
/// 接管自动档：长期置信度跟踪 + 自动升级 / 降级（Phase 3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只自动管理 L0 ↔ L1</b>（ObserveOnly ↔ PatchAfterOrig）。L2（ReplaceOrig）风险高，
/// 始终由玩家显式设置，自动档不碰。
/// </para>
/// <para>
/// <b>升级证据从哪来</b>：ObserveOnly 下内核本来不产生预测，拿不到对拍样本 ——
/// 所以 <c>autoTakeover</c> 开启时，L0 会按
/// <see cref="TritSettings.AutoCalibrationIntervalTicks"/> 做<b>低频校准</b>：
/// 推一次实体状态、取一次 ActionPlan、只对拍不应用。默认 1200 tick（约 20 秒）一次，
/// 换到 30 天窗口里只有极小的占用；把 <c>autoTakeover</c> 或
/// <c>autoCalibrationIntervalTicks</c> 置 0 就回到严格零开销。
/// </para>
/// <para>
/// <b>窗口口径</b>：<see cref="TritSettings.AutoWindowStartUtc"/> 起算的墙钟天数 +
/// 样本数 + 一致率三项同时达标才升级。升级或降级都会清空窗口，必须重新积累。
/// </para>
/// <para>
/// <b>降级口径</b>：只看自动升级出来的 L1（<see cref="TritSettings.AutoManaged"/>）。
/// 单 tick 样本 ≥ <see cref="MinDowngradeSamples"/> 且一致率跌破
/// <see cref="TritSettings.AutoDowngradeRatio"/> 就当 tick 回落 ObserveOnly，
/// 不等待、不累计、不重试。玩家手动开启的接管不会被自动降级覆盖。
/// </para>
/// </remarks>
internal sealed class TakeoverAutoPilot
{
    /// <summary>单 tick 低于该样本数不做降级判断（避免一两个样本的噪声打掉接管）。</summary>
    public const int MinDowngradeSamples = 20;

    /// <summary>校准计数落盘的最短间隔（秒）。崩溃最多丢一小时窗口，不会每次校准都写文件。</summary>
    private const long PersistIntervalSeconds = 3600;

    private readonly TritSettings settings;

    /// <summary>本会话的校准 tick 计数（用于取模，不持久化）。</summary>
    private long calibrationTicks;

    /// <summary>上次写设置文件的 Unix 秒。</summary>
    private long lastPersistUtc;

    /// <summary>窗口计数有未落盘的改动。</summary>
    private bool dirty;

    public TakeoverAutoPilot(TritSettings settings)
    {
        this.settings = settings;
        lastPersistUtc = NowUtc();
    }

    /// <summary>本会话是否能做 L0 校准（决定内核配置里要不要带 takeover）。</summary>
    public bool CalibrationEnabled =>
        settings.AutoTakeover &&
        settings.Takeover == TritSettings.TakeoverMode.ObserveOnly &&
        settings.AutoCalibrationIntervalTicks > 0;

    /// <summary>当前窗口一致率；没有样本时返回 0。</summary>
    public double WindowRate =>
        settings.AutoSamples > 0 ? (double)settings.AutoConsistent / settings.AutoSamples : 0d;

    /// <summary>当前窗口已积累的墙钟天数。</summary>
    public int WindowElapsedDays
    {
        get
        {
            if (settings.AutoWindowStartUtc <= 0)
            {
                return 0;
            }

            long elapsed = NowUtc() - settings.AutoWindowStartUtc;
            return elapsed <= 0 ? 0 : (int)(elapsed / 86400);
        }
    }

    /// <summary>本 tick 是否轮到校准（每 <see cref="TritSettings.AutoCalibrationIntervalTicks"/> tick 一次）。</summary>
    public bool IsCalibrationTick()
    {
        if (!CalibrationEnabled)
        {
            return false;
        }

        calibrationTicks++;
        return calibrationTicks % settings.AutoCalibrationIntervalTicks == 0;
    }

    /// <summary>
    /// 评估一次升级：墙钟天数、样本数、一致率三项同时达标才把 ObserveOnly 升到 PatchAfterOrig。
    /// </summary>
    /// <returns>是否本次发生了升级。</returns>
    public bool TryUpgrade()
    {
        if (!settings.AutoTakeover) return false;
        if (settings.Takeover != TritSettings.TakeoverMode.ObserveOnly) return false;
        if (settings.AutoWindowStartUtc <= 0) return false;
        if (NowUtc() - settings.AutoWindowStartUtc < (long)settings.AutoUpgradeDays * 86400) return false;
        if (settings.AutoSamples < settings.AutoMinSamples) return false;
        if (WindowRate < settings.AutoUpgradeRatio) return false;

        string rate = WindowRate.ToString("P1", CultureInfo.InvariantCulture);
        int days = WindowElapsedDays;
        settings.Takeover = TritSettings.TakeoverMode.PatchAfterOrig;
        settings.AutoManaged = true;
        ResetWindow();

        Persist(
            $"[trit] 自动升级：窗口 {days}/{settings.AutoUpgradeDays} 天、一致率 {rate}，" +
            "接管模式 ObserveOnly → PatchAfterOrig（可用 trit takeover auto off 关闭自动档）",
            warning: false);
        return true;
    }

    /// <summary>
    /// 记录一次 L0 校准的对拍结果，并顺带评估是否可以升级。
    /// </summary>
    public void RecordCalibration(int recorded, int consistent)
    {
        if (!settings.AutoTakeover || recorded <= 0) return;
        if (settings.Takeover != TritSettings.TakeoverMode.ObserveOnly) return;

        if (settings.AutoWindowStartUtc <= 0)
        {
            settings.AutoWindowStartUtc = NowUtc();
        }

        settings.AutoSamples += recorded;
        settings.AutoConsistent += consistent;
        dirty = true;

        // 校准本身让本会话已有内核预测配置，升级可以立刻生效，不必重进世界。
        if (!TryUpgrade())
        {
            MaybePersistCounters();
        }
    }

    /// <summary>
    /// 每个 L1 tick 结束后评估降级。命中条件立即回落 ObserveOnly。
    /// </summary>
    /// <returns>是否本次发生了降级。</returns>
    public bool RecordTakeoverTick(int recorded, int consistent)
    {
        if (!settings.AutoTakeover) return false;
        if (settings.Takeover != TritSettings.TakeoverMode.PatchAfterOrig) return false;
        if (!settings.AutoManaged) return false;
        if (recorded < MinDowngradeSamples) return false;

        double rate = (double)consistent / recorded;
        if (rate >= settings.AutoDowngradeRatio) return false;

        settings.Takeover = TritSettings.TakeoverMode.ObserveOnly;
        settings.AutoManaged = false;
        ResetWindow();

        Persist(
            $"[trit] 自动降级：本 tick 对拍 {recorded} 条、一致率 {rate.ToString("P1", CultureInfo.InvariantCulture)} " +
            $"低于 {settings.AutoDowngradeRatio.ToString("P1", CultureInfo.InvariantCulture)}，" +
            "接管已回落 ObserveOnly（1 tick 内生效）",
            warning: true);
        return true;
    }

    /// <summary>清空校准窗口（手动改档位、自动升降级后调用）。</summary>
    public void ResetWindow()
    {
        settings.AutoWindowStartUtc = 0;
        settings.AutoSamples = 0;
        settings.AutoConsistent = 0;
        dirty = true;
    }

    /// <summary>把手头未落盘的窗口计数写回设置文件（退出存档 / 卸载时调用）。</summary>
    public void Flush()
    {
        if (!dirty)
        {
            return;
        }

        try
        {
            if (settings.Save(out _))
            {
                dirty = false;
                lastPersistUtc = NowUtc();
            }
        }
        catch (Exception ex)
        {
            TritRuntimeMod.Instance?.Logger?.Warn("[trit] 自动档窗口计数写回失败：" + ex.Message);
        }
    }

    /// <summary>命令层 <c>trit takeover status</c> 的单行摘要。</summary>
    public string Describe()
    {
        if (!settings.AutoTakeover)
        {
            return "自动档=关";
        }

        if (settings.Takeover != TritSettings.TakeoverMode.ObserveOnly)
        {
            return settings.AutoManaged
                ? "自动档=开（当前档位由自动升级设置，跌破阈值会 1 tick 内回落）"
                : "自动档=开（当前档位由玩家设置，不会被自动降级）";
        }

        if (!CalibrationEnabled)
        {
            return "自动档=开（校准已关闭：autoCalibrationIntervalTicks=0，L0 不会自行升级）";
        }

        string rate = settings.AutoSamples > 0
            ? WindowRate.ToString("P1", CultureInfo.InvariantCulture)
            : "--";
        return "自动档=开 " +
               $"校准每 {settings.AutoCalibrationIntervalTicks} tick " +
               $"窗口 {WindowElapsedDays}/{settings.AutoUpgradeDays} 天 " +
               $"样本 {settings.AutoSamples}/{settings.AutoMinSamples} " +
               $"一致率 {rate}";
    }

    private void MaybePersistCounters()
    {
        if (!dirty || NowUtc() - lastPersistUtc < PersistIntervalSeconds)
        {
            return;
        }

        Flush();
    }

    private void Persist(string message, bool warning)
    {
        bool saved;
        string saveWarning;
        try
        {
            saved = settings.Save(out saveWarning);
        }
        catch (Exception ex)
        {
            saved = false;
            saveWarning = ex.Message;
        }

        if (saved)
        {
            dirty = false;
            lastPersistUtc = NowUtc();
        }

        if (warning)
        {
            TritRuntimeMod.Instance?.Logger?.Warn(message);
        }
        else
        {
            TritRuntimeMod.Instance?.Logger?.Info(message);
        }

        if (!saved && !string.IsNullOrWhiteSpace(saveWarning))
        {
            TritRuntimeMod.Instance?.Logger?.Warn("[trit] 自动档设置写回失败（仅本次会话）：" + saveWarning);
        }
    }

    private static long NowUtc() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
