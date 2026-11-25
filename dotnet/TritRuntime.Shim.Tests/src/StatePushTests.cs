#nullable enable

using System.Collections.Generic;
using TritRuntime.Kernel;
using TritRuntime.Wire;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// Phase 2 TritEnginePort：STATE_PUSH 线格式 + 引擎预测覆盖历史外推。
/// </summary>
public sealed class StatePushTests
{
    /// <summary>接管配置：每 tick 预测，阈值与桌面默认一致。</summary>
    private const string TakeoverConfig =
        "{\"takeover\":{\"mode\":\"patch_after_orig\",\"confidence_threshold\":0.95,\"prediction_interval_ticks\":1}}";

    private const uint FieldVelocityX = 0x007;
    private const uint FieldVelocityY = 0x008;
    private const uint FieldDirection = 0x009;

    private static uint Lane(byte kind, int slot, uint field) =>
        ((uint)kind << 28) | ((uint)slot << 12) | field;

    /// <summary>type 3 = 僵尸；有目标、站在地面、距离较远 → 只驱动水平与朝向。</summary>
    private static EntitySnapshot Zombie(int slot = 5) => new()
    {
        Kind = EntityKinds.Npc,
        Slot = slot,
        Type = 3,
        VelocityX = 0.4f,
        VelocityY = 0f,
        PositionX = 100f,
        PositionY = 200f,
        Direction = 1,
        Life = 100,
        LifeMax = 100,
        Target = 7,
        WalkSpeed = 1.4f,
        TargetDistance = 100f,
        TileBelow = true,
        OnGround = true,
    };

    [Fact]
    public void ManagedKernelAcceptsStatePushWithoutEffect()
    {
        using var shim = RuntimeShim.Attach(new RuntimeHostOptions { ForceManaged = true });

        Assert.True(shim.PushEntityStates(new[] { Zombie() }));
        Assert.Empty(shim.GetActionPlan());
    }

    [Fact]
    public void EngineOverridesHistoryForPushedZombie()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return; // 本机没有原生构建产物
        }

        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = path,
            ConfigJson = TakeoverConfig,
            AllowDefaultProbing = false,
        });

        var vx = Lane(EntityKinds.Npc, 5, FieldVelocityX);
        var vy = Lane(EntityKinds.Npc, 5, FieldVelocityY);
        var dir = Lane(EntityKinds.Npc, 5, FieldDirection);
        shim.ApplyBatch(
            new[] { vx, vy, dir },
            new[] { TritDelta.Pos, TritDelta.Pos, TritDelta.Pos });

        // 先喂 5 条一致样本：历史外推此时会预测 vy（置信度 1.0）。
        Assert.True(shim.RecordOutcome(new[] { (vy, true), (vy, true), (vy, true), (vy, true), (vy, true) }));
        var historical = shim.GetActionPlan().ToDictionary(p => p.Key, p => p);
        Assert.Equal((byte)TritDelta.Pos, historical[vy].Delta);

        // 推送僵尸状态：引擎对 vy 弃权（站在地面且距离远），对 vx / direction 给 Pos。
        Assert.True(shim.PushEntityStates(new[] { Zombie() }));

        var plan = shim.GetActionPlan().ToDictionary(p => p.Key, p => p);
        Assert.Equal((byte)TritDelta.Pos, plan[vx].Delta);
        Assert.Equal(1.0f, plan[vx].Confidence);
        Assert.Equal((byte)TritDelta.Pos, plan[dir].Delta);
        Assert.Equal(1.0f, plan[dir].Confidence);

        // 引擎弃权也覆盖历史结论 —— vy 不应再出现在计划里。
        Assert.False(plan.ContainsKey(vy));
    }

    [Fact]
    public void EmptyPushClearsPreviousEngineState()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = path,
            ConfigJson = TakeoverConfig,
            AllowDefaultProbing = false,
        });

        var vx = Lane(EntityKinds.Npc, 5, FieldVelocityX);
        shim.ApplyBatch(new[] { vx }, new[] { TritDelta.Pos });
        Assert.True(shim.PushEntityStates(new[] { Zombie() }));
        Assert.Equal(1.0f, shim.GetActionPlan().Single(p => p.Key == vx).Confidence);

        // 空列表 = 清空状态表，之后回到纯历史外推（没有样本 → 空计划）。
        Assert.True(shim.PushEntityStates(Array.Empty<EntitySnapshot>()));
        Assert.Empty(shim.GetActionPlan());
    }

    [Fact]
    public void TargetOffsetSteersFlyerTowardPlayer()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = path,
            ConfigJson = TakeoverConfig,
            AllowDefaultProbing = false,
        });

        var vx = Lane(EntityKinds.Npc, 5, FieldVelocityX);
        shim.ApplyBatch(new[] { vx }, new[] { TritDelta.Pos });

        // Demon Eye（type 2）：当前朝右飞，但目标在左上方（dx > 0、dy < 0）。
        Assert.True(shim.PushEntityStates(new[]
        {
            new EntitySnapshot
            {
                Kind = EntityKinds.Npc,
                Slot = 5,
                Type = 2,
                VelocityX = 2f,
                Direction = 1,
                Target = 7,
                TargetDx = 120f,
                TargetDy = -80f,
                WalkSpeed = 1.8f,
            },
        }));

        var plan = shim.GetActionPlan().ToDictionary(p => p.Key, p => p);
        Assert.Equal((byte)TritDelta.Neg, plan[vx].Delta);
        Assert.Equal(1.0f, plan[vx].Confidence);
    }

    /// <summary>
    /// ACTION_PLAN 尾部速度表：僵尸（地面追击）带上 walk_speed，供 KernelFirst 写绝对速度。
    /// </summary>
    [Fact]
    public void ActionPlanCarriesEngineSpeedMagnitude()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = path,
            ConfigJson = TakeoverConfig,
            AllowDefaultProbing = false,
        });

        var vx = Lane(EntityKinds.Npc, 5, FieldVelocityX);
        shim.ApplyBatch(new[] { vx }, new[] { TritDelta.Pos });
        Assert.True(shim.PushEntityStates(new[] { Zombie() }));

        var plan = new List<(uint Key, byte Delta, float Confidence)>();
        var speeds = new List<(byte Kind, int Slot, float Magnitude)>();
        shim.GetActionPlanInto(plan, speeds);

        var entry = Assert.Single(speeds);
        Assert.Equal(EntityKinds.Npc, entry.Kind);
        Assert.Equal(5, entry.Slot);
        Assert.Equal(1.4f, entry.Magnitude, 2); // 量化步长 1/256
    }

    /// <summary>飞行追击家族不下发速度大小（引擎只对方向有把握）。</summary>
    [Fact]
    public void ActionPlanOmitsSpeedForFlyer()
    {
        var path = NativeLibraryLocator.Find();
        if (path is null)
        {
            return;
        }

        using var shim = RuntimeShim.Attach(new RuntimeHostOptions
        {
            LibraryPath = path,
            ConfigJson = TakeoverConfig,
            AllowDefaultProbing = false,
        });

        var vx = Lane(EntityKinds.Npc, 5, FieldVelocityX);
        shim.ApplyBatch(new[] { vx }, new[] { TritDelta.Pos });
        Assert.True(shim.PushEntityStates(new[]
        {
            new EntitySnapshot
            {
                Kind = EntityKinds.Npc,
                Slot = 5,
                Type = 2, // Demon Eye
                VelocityX = 2f,
                Direction = 1,
                Target = 7,
                TargetDx = 120f,
                TargetDy = -80f,
                WalkSpeed = 1.8f,
            },
        }));

        var plan = new List<(uint Key, byte Delta, float Confidence)>();
        var speeds = new List<(byte Kind, int Slot, float Magnitude)>();
        shim.GetActionPlanInto(plan, speeds);

        Assert.NotEmpty(plan);
        Assert.Empty(speeds);
    }
}
