#nullable enable

using System;
using Terraria;

namespace TritRuntimeMod;

/// <summary>
/// 热点钩子：只观测，不回写。
/// </summary>
/// <remarks>
/// <para>
/// <b>钩子形态已用编译器 + 反射实测确认</b>：本机 tModLoader 1.4.4.9 的 <c>TerrariaHooks.dll</c>
/// 生成的是 <c>Terraria.On_*</c> / <c>Terraria.IL_*</c>（各 1036 个）而不是旧文档里的
/// <c>On.Terraria.*</c> 写法。签名逐项来自反射：
/// </para>
/// <list type="bullet">
/// <item><c>On_NPC.AI(orig, NPC self)</c></item>
/// <item><c>On_Projectile.Update(orig, Projectile self, int i)</c></item>
/// <item><c>On_NetMessage.SendData(orig, int msgType, int remoteClient, int ignoreClient, NetworkText text, int number, float n2, float n3, float n4, int n5, int n6, int n7)</c></item>
/// </list>
/// <para>
/// <b>曾经挂的 <c>On_Main.Update</c> 已移除</b>：tick 驱动改由
/// <see cref="TritWorldSystem.PostUpdateEverything"/> 承担。原因不是风格偏好——
/// 专用服务器实测证明 <c>Main.Update(GameTime)</c> 在服务器端从不触发，
/// 挂它会导致"报告装好了、实际全程空转"。
/// </para>
/// <para>
/// 铁律：<c>orig</c> 每次都必须恰好调用一次。任何观测异常都被吞掉并计数，
/// 连续失败达到阈值后本层停止工作——宁可失去观测，也不能拖垮游戏。
/// </para>
/// </remarks>
internal sealed class TritHooks
{
    private readonly TritRuntimeMod mod;
    private readonly TritSettings settings;
    private readonly WorldBridge bridge;

    // 钩子里的 `Guarded(name, bridge.NoteXxx)` 是每实体每 tick 一次；方法组每转换一次
    // 就新建一个委托，等于给热路径加了一个托管分配。这里预先缓存，稳态零分配。
    private readonly Action noteNpcAi;
    private readonly Action noteProjectileUpdate;

    private On_NPC.hook_AI? npcAi;
    private On_Projectile.hook_Update? projectileUpdate;
    private On_NetMessage.hook_SendData? netSendData;

    private int failures;
    private bool tripped;

    /// <summary>同类周期性日志的最小间隔（秒）。</summary>
    private const double MinLogIntervalSeconds = 15.0;

    // 钩子是每秒 60 × 实体数 次的热路径：200 个 NPC / 1000 条弹幕就是每秒几万次。
    // 观测一旦持续抛异常，不节流就是每秒几万条带完整栈回溯的日志。
    private readonly ErrorThrottle hookErrorLog = new(MinLogIntervalSeconds);

    /// <summary>是否启用 KernelFirst —— 只在 ReplaceOrig 模式（Android）下为 true。</summary>
    private bool KernelFirstActive => settings.Takeover == TritSettings.TakeoverMode.ReplaceOrig;

    public TritHooks(TritRuntimeMod mod, TritSettings settings, WorldBridge bridge)
    {
        this.mod = mod;
        this.settings = settings;
        this.bridge = bridge;
        noteNpcAi = bridge.NoteNpcAi;
        noteProjectileUpdate = bridge.NoteProjectileUpdate;
    }

    /// <summary>本层是否已经因连续异常而停止工作。</summary>
    public bool Tripped => tripped;

    /// <summary>已挂上的钩子数量。</summary>
    public int Installed { get; private set; }

    /// <summary>安装全部钩子。任一个装不上都不影响其余钩子与游戏本身。</summary>
    public void Install()
    {
        TryInstall("On_NPC.AI", () =>
        {
            npcAi = (orig, self) =>
            {
                // 接管调用同样要吞异常：KernelFirst 失败最多退回原版，
                // 绝不能让 orig 漏调（本文件的铁律是 orig 恰好一次）。
                if (KernelFirstActive && TryKernelFirst(self))
                {
                    // KernelFirst 接管成功 —— 不调 orig(self)，只做观测计数
                    Guarded("On_NPC.AI", noteNpcAi);
                    return;
                }

                // 垂直通道学习：Δvy 必须夹在 orig 两侧采样（只看 AI 的贡献，
                // 重力加在 AI 里还是 Main.UpdateNPC 里都不影响这个差值）。
                float vyBefore = self.velocity.Y;
                bool onGroundBefore = self.collideY;

                // PatchAfterOrig / ObserveOnly / KernelFirst fallback: 正常走 orig + 观测
                orig(self);
                ObserveNpcAi(self, vyBefore, onGroundBefore);
                Guarded("On_NPC.AI", noteNpcAi);
            };
            On_NPC.AI += npcAi;
        });

        TryInstall("On_Projectile.Update", () =>
        {
            projectileUpdate = (orig, self, i) =>
            {
                // 弹幕永远走 orig：Projectile.Update 本身就是位置积分 / 重力 / 碰撞 / timeLeft，
                // 跳过它会让弹幕原地悬停（NPC 只跳过 NPC.AI，位置由 Main.UpdateNPC 负责，故不冲突）。
                orig(self, i);
                Guarded("On_Projectile.Update", noteProjectileUpdate);
            };
            On_Projectile.Update += projectileUpdate;
        });

        TryInstall("On_NetMessage.SendData", () =>
        {
            netSendData = (orig, msgType, remoteClient, ignoreClient, text, number, number2, number3, number4, number5, number6, number7) =>
            {
                // 纯观测：先记账再原样放行。绝不在网络路径上做阻塞或改写。
                if (!tripped)
                {
                    try
                    {
                        bridge.NoteNetMessage(msgType);
                    }
                    catch
                    {
                        // 记账失败不允许影响发包
                    }
                }

                orig(msgType, remoteClient, ignoreClient, text, number, number2, number3, number4, number5, number6, number7);
            };
            On_NetMessage.SendData += netSendData;
        });

        mod.Logger.Info($"[trit] 钩子安装完成：{Installed}/3（tick 驱动由 ModSystem 提供，不占钩子名额）");
    }

    /// <summary>卸载全部钩子。tML 对 On_* 钩子会自行清理，这里显式摘除是为了可重入（模组重载）。</summary>
    public void Uninstall()
    {
        if (npcAi is not null)
        {
            try
            {
                On_NPC.AI -= npcAi;
            }
            catch (Exception ex)
            {
                mod.Logger.Warn("[trit] 摘除 On_NPC.AI 失败：" + ex.Message);
            }

            npcAi = null;
        }

        if (projectileUpdate is not null)
        {
            try
            {
                On_Projectile.Update -= projectileUpdate;
            }
            catch (Exception ex)
            {
                mod.Logger.Warn("[trit] 摘除 On_Projectile.Update 失败：" + ex.Message);
            }

            projectileUpdate = null;
        }

        if (netSendData is not null)
        {
            try
            {
                On_NetMessage.SendData -= netSendData;
            }
            catch (Exception ex)
            {
                mod.Logger.Warn("[trit] 摘除 On_NetMessage.SendData 失败：" + ex.Message);
            }

            netSendData = null;
        }

        Installed = 0;
    }

    private void TryInstall(string name, Action install)
    {
        try
        {
            install();
            Installed++;
        }
        catch (Exception ex)
        {
            // 签名不符 / 目标方法不存在 / 被其他模组抢先 patch —— 都只降级，不抛给 tML。
            mod.Logger.Error($"[trit] 钩子 {name} 安装失败，该观测点降级为关闭：{ex}");
        }
    }

    private void Guarded(string name, Action body)
    {
        if (tripped)
        {
            return;
        }

        try
        {
            body();
            failures = 0;
        }
        catch (Exception ex)
        {
            ReportFailure(name, ex);
        }
    }

    /// <summary>KernelFirst 调用：异常只降级（返回 false → 本 tick 走原版）。</summary>
    private bool TryKernelFirst(NPC self)
    {
        if (tripped)
        {
            return false;
        }

        try
        {
            return mod.Takeover?.TryApplyKernelFirstToNpc(self) == true;
        }
        catch (Exception ex)
        {
            ReportFailure("On_NPC.AI(takeover)", ex);
            return false;
        }
    }

    /// <summary>垂直通道的 Δvy 观测：失败只是模型少一个样本，不影响原版 AI。</summary>
    private void ObserveNpcAi(NPC self, float velocityYBefore, bool onGroundBefore)
    {
        if (tripped)
        {
            return;
        }

        try
        {
            mod.Takeover?.ObserveNpcAiDelta(self, velocityYBefore, onGroundBefore);
        }
        catch (Exception ex)
        {
            ReportFailure("On_NPC.AI(observe)", ex);
        }
    }

    /// <summary>连续失败计数 + 节流日志 + 熔断（Guarded / 接管调用共用）。</summary>
    private void ReportFailure(string name, Exception ex)
    {
        // 计数语义不变：**连续**失败才熔断。成功一次就归零是刻意的——
        // 偶发的单条异常不该把整层观测停掉。代价是间歇性失败永远不会触发熔断，
        // 于是"每次失败都打日志"会一直持续下去，所以日志必须另有节流兜底。
        failures++;

        if (hookErrorLog.ShouldLog(out int dropped))
        {
            mod.Logger.Error(
                $"[trit] 钩子 {name} 观测异常（连续第 {failures} 次）：{ex}" +
                (dropped > 0 ? $"（自上次报告以来又吞掉 {dropped} 次）" : string.Empty));
        }

        int threshold = settings.MaxConsecutiveHookFailures;
        if (threshold > 0 && failures >= threshold)
        {
            // 只置位不摘钩：在回调内部改动钩子链是不安全的。
            // 置位后所有 Guarded 立即空转，orig 仍会被正常调用。
            // 熔断这条**不节流**——它是状态变更，整个会话只可能发生一次。
            tripped = true;
            mod.Logger.Error($"[trit] 连续 {failures} 次异常，三进制观测层已停用（游戏逻辑不受影响）。");
        }
    }
}
