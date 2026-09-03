using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// 補間サービス(InterpolationService)の単体テスト。
/// 対象仕様: 2.3 補間送信 / 3.3 Fade滑らか化 / 消灯近傍のガンマ(知覚)補間。
/// 純ロジック（実機不要）。実際の“なめらかさ”の見た目は実機確認項目とする。
/// </summary>
public class InterpolationServiceTests
{
    private readonly InterpolationService _svc = new();

    // ───────────────────────── LinearSteps ─────────────────────────

    [Fact]
    public void LinearSteps_要素数はstepCount足す1()
    {
        var steps = _svc.LinearSteps(Rgb.Black, Rgb.White, 20);
        Assert.Equal(21, steps.Count);
    }

    [Fact]
    public void LinearSteps_端点は開始色と終了色に一致()
    {
        var steps = _svc.LinearSteps(Rgb.Black, Rgb.White, 10);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.White, steps[10]);
    }

    [Fact]
    public void LinearSteps_中点は線形の中間値()
    {
        var steps = _svc.LinearSteps(Rgb.Black, Rgb.White, 2);
        // Lerp(0,255,0.5)=Round(127.5)=128（銀行家丸め）
        Assert.Equal(new Rgb(128, 128, 128), steps[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LinearSteps_stepCountが1未満なら1にクランプ(int stepCount)
    {
        // NO.39 防御: 0/負でも例外を出さず 2 要素（from,to）を返す
        var steps = _svc.LinearSteps(Rgb.Black, Rgb.White, stepCount);
        Assert.Equal(2, steps.Count);
    }

    // ───────────────────────── FadeIn / FadeOut ─────────────────────────

    [Fact]
    public void FadeInSteps_黒から指定色()
    {
        var steps = _svc.FadeInSteps(Rgb.Red, 5);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.Red, steps[5]);
    }

    [Fact]
    public void FadeOutSteps_指定色から黒()
    {
        var steps = _svc.FadeOutSteps(Rgb.Blue, 5);
        Assert.Equal(Rgb.Blue, steps[0]);
        Assert.Equal(Rgb.Black, steps[5]);
    }

    // ───────────────────────── PerceptualSteps（ガンマ補間）─────────────────────────

    [Fact]
    public void PerceptualSteps_端点は開始色と終了色を厳密に再現()
    {
        var steps = _svc.PerceptualSteps(Rgb.Black, Rgb.White, 16);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.White, steps[16]);
    }

    [Fact]
    public void PerceptualSteps_黒からの中点は線形中点より暗い()
    {
        // ガンマ空間補間は暗部をゆっくり通過する → 中点は線形(128)より小さい輝度
        var perceptual = _svc.PerceptualSteps(Rgb.Black, Rgb.White, 2)[1];
        var linear = _svc.LinearSteps(Rgb.Black, Rgb.White, 2)[1];
        Assert.True(perceptual.R < linear.R, $"perceptual={perceptual.R} linear={linear.R}");
    }

    [Fact]
    public void PerceptualSteps_各チャンネルは単調増加()
    {
        var steps = _svc.PerceptualSteps(Rgb.Black, Rgb.White, 32);
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].R >= steps[i - 1].R, $"index {i}: {steps[i - 1].R} -> {steps[i].R}");
    }

    [Fact]
    public void PerceptualSteps_gammaが0以下でも例外なし()
    {
        var steps = _svc.PerceptualSteps(Rgb.Black, Rgb.White, 4, gamma: 0);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.White, steps[4]);
    }

    // ───────────────────────── CalcStepInterval ─────────────────────────

    [Fact]
    public void CalcStepInterval_所要時間をステップ数で割る()
    {
        var interval = _svc.CalcStepInterval(TimeSpan.FromMilliseconds(1000), 20);
        Assert.Equal(50, interval.TotalMilliseconds);
    }

    [Fact]
    public void CalcStepInterval_stepCount1未満は1にクランプ()
    {
        var interval = _svc.CalcStepInterval(TimeSpan.FromMilliseconds(1000), 0);
        Assert.Equal(1000, interval.TotalMilliseconds);
    }
}
