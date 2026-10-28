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

    private On_NPC.hook_AI? npcAi;
    private On_Projectile.hook_Update? projectileUpdate;
    private On_NetMessage.hook_SendData? netSendData;

    private int failures;
    private bool tripped;

    public TritHooks(TritRuntimeMod mod, TritSettings settings, WorldBridge bridge)
    {
        this.mod = mod;
        this.settings = settings;
        this.bridge = bridge;
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
                orig(self);
                Guarded("On_NPC.AI", bridge.NoteNpcAi);
            };
            On_NPC.AI += npcAi;
        });

        TryInstall("On_Projectile.Update", () =>
        {
            projectileUpdate = (orig, self, i) =>
            {
                orig(self, i);
                Guarded("On_Projectile.Update", bridge.NoteProjectileUpdate);
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
            failures++;
            mod.Logger.Error($"[trit] 钩子 {name} 观测异常（连续第 {failures} 次）：{ex}");

            int threshold = settings.MaxConsecutiveHookFailures;
            if (threshold > 0 && failures >= threshold)
            {
                // 只置位不摘钩：在回调内部改动钩子链是不安全的。
                // 置位后所有 Guarded 立即空转，orig 仍会被正常调用。
                tripped = true;
                mod.Logger.Error($"[trit] 连续 {failures} 次异常，三进制观测层已停用（游戏逻辑不受影响）。");
            }
        }
    }
}
