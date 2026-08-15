using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Services;

/// <summary>
/// RGB色の補間ステップを生成するサービス。
/// Fade IN/OUT で滑らかな色遷移を実現するための中間値コマンド列を生成する。
/// 仕様: 2.3 補間送信 / 3.3 Fade滑らか化
/// </summary>
public class InterpolationService
{
    /// <summary>
    /// フェード知覚滑らか化のデフォルトガンマ。
    /// 人間の知覚輝度は P≈(byte/255)^(1/γ) の非線形で、線形補間（byte=色×t）だと
    /// 消灯付近（byte→0）で dP/dt が急増し「カクカク」見える。補間を γ 空間で行うことで
    /// 知覚輝度が時間に対して一定変化になり、暗部のカクつきを解消する。
    /// 実機で微調整可能（大きいほど暗部をゆっくり通過する）。
    /// </summary>
    public const double DefaultGamma = 2.2;

    /// <summary>
    /// 2色間の線形補間ステップを生成する。
    /// </summary>
    /// <param name="from">開始色</param>
    /// <param name="to">終了色</param>
    /// <param name="stepCount">ステップ数（2以上）。例: 20ステップ = 開始色含め21フレーム</param>
    /// <returns>補間されたRGB配列（from含む、to含む）</returns>
    public IReadOnlyList<Rgb> LinearSteps(Rgb from, Rgb to, int stepCount)
    {
        // NO.39: 防御的に下限1へクランプ（例外で Fade が落ちて「動作しない」のを防止）。
        if (stepCount < 1) stepCount = 1;

        var steps = new Rgb[stepCount + 1];
        for (int i = 0; i <= stepCount; i++)
        {
            float t = (float)i / stepCount;
            byte r = Lerp(from.R, to.R, t);
            byte g = Lerp(from.G, to.G, t);
            byte b = Lerp(from.B, to.B, t);
            steps[i] = new Rgb(r, g, b);
        }
        return steps;
    }

    /// <summary>
    /// 2色間をガンマ（知覚）空間で補間するステップを生成する。
    /// 各チャンネルを byte(t) = 255 × ( from' + (to' − from')·t )^γ
    /// （from'/to' = (from/to÷255)^(1/γ)）で補間し、知覚輝度が時間に対して
    /// 一定変化になるようにする（消灯付近のカクつき対策）。
    /// 端点は元の色を厳密に再現する（t=0→from, t=1→to）。
    /// Fade IN/OUT/Breathing 用。SevenColor 等の色相遷移には使用しない。
    /// </summary>
    public IReadOnlyList<Rgb> PerceptualSteps(Rgb from, Rgb to, int stepCount, double gamma = DefaultGamma)
    {
        if (stepCount < 1) stepCount = 1;
        if (gamma <= 0) gamma = 1.0;

        double invGamma = 1.0 / gamma;
        // 端点を知覚空間（0..1）へ変換
        double fr = Math.Pow(from.R / 255.0, invGamma);
        double fg = Math.Pow(from.G / 255.0, invGamma);
        double fb = Math.Pow(from.B / 255.0, invGamma);
        double tr = Math.Pow(to.R / 255.0, invGamma);
        double tg = Math.Pow(to.G / 255.0, invGamma);
        double tb = Math.Pow(to.B / 255.0, invGamma);

        var steps = new Rgb[stepCount + 1];
        for (int i = 0; i <= stepCount; i++)
        {
            double t = (double)i / stepCount;
            byte r = GammaLerp(fr, tr, t, gamma);
            byte g = GammaLerp(fg, tg, t, gamma);
            byte b = GammaLerp(fb, tb, t, gamma);
            steps[i] = new Rgb(r, g, b);
        }
        return steps;
    }

    /// <summary>
    /// Fade IN: Black → 指定色への補間ステップ
    /// </summary>
    public IReadOnlyList<Rgb> FadeInSteps(Rgb targetColor, int stepCount)
        => LinearSteps(Rgb.Black, targetColor, stepCount);

    /// <summary>
    /// Fade OUT: 指定色 → Blackへの補間ステップ
    /// </summary>
    public IReadOnlyList<Rgb> FadeOutSteps(Rgb fromColor, int stepCount)
        => LinearSteps(fromColor, Rgb.Black, stepCount);

    /// <summary>
    /// Fade所要時間とステップ数から、各ステップの送信間隔を計算する。
    /// 例: 1秒 / 20ステップ = 50ms間隔
    /// </summary>
    public TimeSpan CalcStepInterval(TimeSpan duration, int stepCount)
    {
        // NO.39: 防御的に下限1へクランプ（0除算/例外で Fade が落ちるのを防止）。
        if (stepCount < 1) stepCount = 1;
        return TimeSpan.FromMilliseconds(duration.TotalMilliseconds / stepCount);
    }

    private static byte Lerp(byte a, byte b, float t)
        => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>
    /// 知覚空間で線形に補間した値を輝度（線形）へ戻して byte 化する。
    /// fromP/toP は端点の知覚値（0..1）、t は 0..1 の進行度。
    /// </summary>
    private static byte GammaLerp(double fromP, double toP, double t, double gamma)
    {
        double p = fromP + (toP - fromP) * t;   // 知覚空間で線形補間
        double linear = Math.Pow(p, gamma);      // 輝度（線形）へ戻す
        return (byte)Math.Clamp(Math.Round(linear * 255.0), 0, 255);
    }
}
