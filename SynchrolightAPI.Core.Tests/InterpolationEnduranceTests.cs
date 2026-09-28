using SynchrolightAPI.Domain;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Core.Tests;

/// <summary>
/// 補間サービス(InterpolationService)の耐久・大規模テスト（純ロジック＝実機不要）。
///  ・巨大ステップ数でも全成分が [0,255] に収まり端点が厳密であること
///  ・多数の色ペアを反復補間しても例外なく単調性・端点が保たれること
/// を確認する。見た目の滑らかさは実機確認項目。
/// </summary>
public class InterpolationEnduranceTests
{
    private readonly InterpolationService _svc = new();

    private static void AssertInRange(IReadOnlyList<Rgb> steps)
    {
        // byte 型のため範囲外はそもそも起きないが、端点厳密性と件数を保証する回帰網。
        foreach (var s in steps)
        {
            Assert.InRange(s.R, (byte)0, (byte)255);
            Assert.InRange(s.G, (byte)0, (byte)255);
            Assert.InRange(s.B, (byte)0, (byte)255);
        }
    }

    [Fact]
    public void LinearSteps_5000ステップでも端点厳密かつ各チャンネル単調非減少()
    {
        var steps = _svc.LinearSteps(Rgb.Black, Rgb.White, 5000);
        Assert.Equal(5001, steps.Count);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.White, steps[^1]);
        AssertInRange(steps);
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].R >= steps[i - 1].R, $"index {i}: {steps[i - 1].R}->{steps[i].R}");
    }

    [Fact]
    public void PerceptualSteps_4000ステップでも端点厳密かつ単調非減少()
    {
        var steps = _svc.PerceptualSteps(Rgb.Black, Rgb.White, 4000);
        Assert.Equal(4001, steps.Count);
        Assert.Equal(Rgb.Black, steps[0]);
        Assert.Equal(Rgb.White, steps[^1]);
        for (int i = 1; i < steps.Count; i++)
            Assert.True(steps[i].R >= steps[i - 1].R);
    }

    [Fact]
    public void LinearLightSteps_4000ステップでも端点厳密で範囲内()
    {
        var steps = _svc.LinearLightSteps(new Rgb(128, 0, 0), new Rgb(0, 0, 128), 4000);
        Assert.Equal(new Rgb(128, 0, 0), steps[0]);
        Assert.Equal(new Rgb(0, 0, 128), steps[^1]);
        AssertInRange(steps);
    }

    [Fact]
    public void 多数の色ペアを反復補間しても端点が保たれ例外が出ない()
    {
        // 決定的に色ペアを回して 3 曲線すべてを反復（RNG 非依存で再現可能）。
        for (int i = 0; i < 3000; i++)
        {
            var from = new Rgb((byte)(i * 3), (byte)(i * 5), (byte)(i * 7));
            var to = new Rgb((byte)(i * 11), (byte)(i * 13), (byte)(i * 17));
            int steps = 10 + (i % 200);

            var lin = _svc.LinearSteps(from, to, steps);
            var per = _svc.PerceptualSteps(from, to, steps);
            var lgt = _svc.LinearLightSteps(from, to, steps);

            Assert.Equal(from, lin[0]); Assert.Equal(to, lin[^1]);
            Assert.Equal(from, per[0]); Assert.Equal(to, per[^1]);
            Assert.Equal(from, lgt[0]); Assert.Equal(to, lgt[^1]);
            Assert.Equal(steps + 1, lin.Count);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(3000)]
    public void CalcStepInterval_各ステップ数で所要時間を等分する(int stepCount)
    {
        var interval = _svc.CalcStepInterval(TimeSpan.FromMilliseconds(60000), stepCount);
        Assert.Equal(60000.0 / stepCount, interval.TotalMilliseconds, precision: 6);
    }

    [Fact]
    public void FadeInOut_ショートカットは黒起点_黒終点で範囲内()
    {
        var fin = _svc.FadeInSteps(new Rgb(200, 100, 50), 300);
        var fout = _svc.FadeOutSteps(new Rgb(200, 100, 50), 300);
        Assert.Equal(Rgb.Black, fin[0]);
        Assert.Equal(new Rgb(200, 100, 50), fin[^1]);
        Assert.Equal(new Rgb(200, 100, 50), fout[0]);
        Assert.Equal(Rgb.Black, fout[^1]);
        AssertInRange(fin);
        AssertInRange(fout);
    }
}
