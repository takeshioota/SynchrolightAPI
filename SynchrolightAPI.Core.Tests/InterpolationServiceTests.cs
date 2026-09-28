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

    // ─────────── LinearLightSteps（線形光＝輝度保持補間・BUG-20260923-01）───────────

    [Fact]
    public void LinearLightSteps_要素数はstepCount足す1()
    {
        Assert.Equal(21, _svc.LinearLightSteps(Rgb.Black, Rgb.White, 20).Count);
    }

    [Fact]
    public void LinearLightSteps_端点は開始色と終了色を厳密に再現()
    {
        var steps = _svc.LinearLightSteps(new Rgb(128, 0, 0), new Rgb(0, 0, 128), 16);
        Assert.Equal(new Rgb(128, 0, 0), steps[0]);
        Assert.Equal(new Rgb(0, 0, 128), steps[16]);
    }

    [Fact]
    public void LinearLightSteps_単色フェードアウトの中点は線形中点より明るい()
    {
        // 実輝度で線形補間: (255)→(0) の中点 lin=0.5 → byte=255×0.5^(1/2.2)=186（byte 線形の128より明るい）。
        var ll = _svc.LinearLightSteps(new Rgb(255, 0, 0), Rgb.Black, 2)[1];
        Assert.Equal(186, ll.R);
        Assert.True(ll.R > _svc.LinearSteps(new Rgb(255, 0, 0), Rgb.Black, 2)[1].R);
    }

    [Fact]
    public void LinearLightSteps_色相クロスフェードの中点は線形中点より明るい_輝度沈み解消()
    {
        // 濃色どうしの溶け合い: byte 線形だと中点(64,0,64)で輝度が沈むが、
        // 線形光補間は中点を(93,0,93)へ持ち上げて実輝度を弦どおりに保つ。
        var ll = _svc.LinearLightSteps(new Rgb(128, 0, 0), new Rgb(0, 0, 128), 2)[1];
        var lin = _svc.LinearSteps(new Rgb(128, 0, 0), new Rgb(0, 0, 128), 2)[1];
        Assert.True(ll.R > lin.R && ll.B > lin.B, $"linearLight=({ll.R},{ll.G},{ll.B}) linear=({lin.R},{lin.G},{lin.B})");
        Assert.Equal(0, ll.G);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LinearLightSteps_stepCountが1未満なら1にクランプ(int stepCount)
    {
        Assert.Equal(2, _svc.LinearLightSteps(Rgb.Black, Rgb.White, stepCount).Count);
    }

    [Fact]
    public void LinearLightSteps_gammaが0以下でも例外なく端点再現()
    {
        var steps = _svc.LinearLightSteps(new Rgb(10, 20, 30), new Rgb(40, 50, 60), 4, gamma: 0);
        Assert.Equal(new Rgb(10, 20, 30), steps[0]);
        Assert.Equal(new Rgb(40, 50, 60), steps[4]);
    }

    // ─── VividLuminanceSteps（鮮やかさ保持＋輝度保持・BUG-20260923-01 デグレ対応・候補A）───

    [Fact]
    public void VividLuminanceSteps_要素数はstepCount足す1()
    {
        Assert.Equal(21, _svc.VividLuminanceSteps(Rgb.Black, Rgb.White, 20).Count);
    }

    [Fact]
    public void VividLuminanceSteps_端点は開始色と終了色を厳密に再現()
    {
        var steps = _svc.VividLuminanceSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10);
        Assert.Equal(new Rgb(0, 255, 0), steps[0]);
        Assert.Equal(new Rgb(255, 0, 0), steps[10]);
    }

    [Fact]
    public void VividLuminanceSteps_中点は輝度が沈まない_byte線形中点より明るい()
    {
        // 緑→赤 の中点: byte 線形は(128,128,0)で輝度が沈むが、鮮やかさ＋輝度保持は(186,186,0)へ持ち上げる。
        var vivid = _svc.VividLuminanceSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10)[5];
        var linear = _svc.LinearSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10)[5];
        Assert.Equal(186, vivid.R);
        Assert.Equal(186, vivid.G);
        Assert.True(vivid.R > linear.R && vivid.G > linear.G,
            $"vivid=({vivid.R},{vivid.G},{vivid.B}) linear=({linear.R},{linear.G},{linear.B})");
    }

    [Fact]
    public void VividLuminanceSteps_端点付近は反対chが素早く落ちLinearLightより鮮やか()
    {
        // 緑→赤 の t=0.9: LinearLight は緑=90 残って橙っぽいが、候補Aは緑=28 まで落ち純赤に近い（＝色あせ解消）。
        var vivid = _svc.VividLuminanceSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10)[9];
        var linearLight = _svc.LinearLightSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10)[9];
        Assert.True(vivid.G < linearLight.G, $"vivid.G={vivid.G} linearLight.G={linearLight.G}");
        Assert.True(vivid.R >= linearLight.R, $"vivid.R={vivid.R} linearLight.R={linearLight.R}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void VividLuminanceSteps_stepCountが1未満なら1にクランプ(int stepCount)
    {
        Assert.Equal(2, _svc.VividLuminanceSteps(Rgb.Black, Rgb.White, stepCount).Count);
    }

    [Fact]
    public void VividLuminanceSteps_単色と黒の1ch遷移はLinearLightと一致()
    {
        // 1ch のみ（色味に選択肢が無い）の遷移では、鮮やかさ保持は LinearLight と同一結果になる（＝Fade 系は不変）。
        var vivid = _svc.VividLuminanceSteps(new Rgb(255, 0, 0), Rgb.Black, 8);
        var ll = _svc.LinearLightSteps(new Rgb(255, 0, 0), Rgb.Black, 8);
        Assert.Equal(ll, vivid);
    }

    [Fact]
    public void VividLuminanceSteps_等輝度端点なら全フレームの実輝度和がほぼ一定()
    {
        // 緑(実輝度和1.0)→赤(実輝度和1.0)。クリップの起きない等輝度遷移では全フレームの実輝度和が1.0近傍＝沈まない。
        var steps = _svc.VividLuminanceSteps(new Rgb(0, 255, 0), new Rgb(255, 0, 0), 10);
        foreach (var s in steps)
        {
            double lum = Lin(s.R) + Lin(s.G) + Lin(s.B);
            Assert.InRange(lum, 0.94, 1.06);
        }
        static double Lin(byte b) => Math.Pow(b / 255.0, InterpolationService.DefaultGamma);
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
