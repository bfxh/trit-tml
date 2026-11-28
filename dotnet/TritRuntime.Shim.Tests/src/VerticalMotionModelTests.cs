#nullable enable

using TritRuntime.Kernel;
using Xunit;

namespace TritRuntime.Shim.Tests;

/// <summary>
/// 垂直通道接管：仿射动力学模型 <c>vy_next = Alpha * vy + Beta</c> 的学习与收敛。
/// </summary>
public sealed class VerticalMotionModelTests
{
    /// <summary>常量重力（最常见情形的特例）：Alpha = 1、Beta = Δvy。</summary>
    [Fact]
    public void ConstantGravity_LearnsAffineModel()
    {
        var model = new VerticalMotionModel();
        float vy = 5f;

        for (int i = 0; i < 3; i++)
        {
            float next = vy + 0.4f;
            model.Observe(vy, next);
            vy = next;
        }

        Assert.True(model.CanPredict);
        Assert.InRange(model.Alpha, 0.9999f, 1.0001f);
        Assert.InRange(model.Beta, 0.3999f, 0.4001f);
        Assert.InRange(model.Predict(vy), vy + 0.3999f, vy + 0.4001f);
    }

    /// <summary>悬停阻尼 / 入水阻力：Beta ≈ 0、Alpha &lt; 1。</summary>
    [Fact]
    public void Damping_LearnsNegativeSlopeFreeModel()
    {
        var model = new VerticalMotionModel();
        float vy = 4f;

        for (int i = 0; i < 4; i++)
        {
            float next = vy * 0.9f - 0.1f;
            model.Observe(vy, next);
            vy = next;
        }

        Assert.True(model.CanPredict);
        Assert.InRange(model.Alpha, 0.8999f, 0.9001f);
        Assert.InRange(model.Beta, -0.1001f, -0.0999f);
    }

    /// <summary>终端速度：速度不再变化 → 退化成 Alpha = 1、Beta = 0，精确外推。</summary>
    [Fact]
    public void TerminalVelocity_FallsBackToConstant()
    {
        var model = new VerticalMotionModel();

        for (int i = 0; i < 3; i++)
        {
            model.Observe(6f, 6f);
        }

        Assert.True(model.CanPredict);
        Assert.InRange(model.Alpha, 0.9999f, 1.0001f);
        Assert.InRange(model.Beta, -0.0001f, 0.0001f);
        Assert.InRange(model.Predict(6f), 5.9999f, 6.0001f);
    }

    /// <summary>冲量样本（明显不符）必须立即让模型失效，重新确认后才恢复。</summary>
    [Fact]
    public void OutlierSample_InvalidatesUntilReconfirmed()
    {
        var model = new VerticalMotionModel();
        float vy = 5f;

        for (int i = 0; i < 3; i++)
        {
            float next = vy + 0.4f;
            model.Observe(vy, next);
            vy = next;
        }

        Assert.True(model.CanPredict);

        // 起跳/受击一类的冲量：Δvy 远超常量重力。
        model.Observe(vy, vy - 5.5f);
        Assert.False(model.CanPredict);
        vy -= 5.5f;

        for (int i = 0; i < 4; i++)
        {
            float next = vy + 0.4f;
            model.Observe(vy, next);
            vy = next;
        }

        Assert.True(model.CanPredict);
        Assert.InRange(model.Alpha, 0.9999f, 1.0001f);
        Assert.InRange(model.Beta, 0.3999f, 0.4001f);
    }

    /// <summary>Reset 之后必须重新学习。</summary>
    [Fact]
    public void Reset_ClearsModel()
    {
        var model = new VerticalMotionModel();
        model.Observe(5f, 5.4f);
        model.Observe(5.4f, 5.8f);
        model.Observe(5.8f, 6.2f);
        Assert.True(model.CanPredict);

        model.Reset();
        Assert.False(model.CanPredict);
        Assert.Equal(0f, model.Alpha);
        Assert.Equal(0f, model.Beta);
    }

    /// <summary>NaN/Inf 观测不能污染模型，后续正常样本仍可重新收敛。</summary>
    [Fact]
    public void NonFiniteSample_ResetsAndRecovers()
    {
        var model = new VerticalMotionModel();
        model.Observe(5f, 5.4f);
        model.Observe(5.4f, 5.8f);
        model.Observe(5.8f, 6.2f);
        Assert.True(model.CanPredict);

        model.Observe(float.NaN, 1f);
        Assert.False(model.CanPredict);

        float vy = 6.2f;
        for (int i = 0; i < 3; i++)
        {
            float next = vy + 0.4f;
            model.Observe(vy, next);
            vy = next;
        }

        Assert.True(model.CanPredict);
        Assert.InRange(model.Beta, 0.3999f, 0.4001f);
        Assert.True(float.IsFinite(model.Predict(vy)));
    }
}
