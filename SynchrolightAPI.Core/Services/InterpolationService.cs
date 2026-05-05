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
    /// 2色間の線形補間ステップを生成する。
    /// </summary>
    /// <param name="from">開始色</param>
    /// <param name="to">終了色</param>
    /// <param name="stepCount">ステップ数（2以上）。例: 20ステップ = 開始色含め21フレーム</param>
    /// <returns>補間されたRGB配列（from含む、to含む）</returns>
    public IReadOnlyList<Rgb> LinearSteps(Rgb from, Rgb to, int stepCount)
    {
        if (stepCount < 1) throw new ArgumentOutOfRangeException(nameof(stepCount), "stepCount must be >= 1");

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
        if (stepCount < 1) throw new ArgumentOutOfRangeException(nameof(stepCount));
        return TimeSpan.FromMilliseconds(duration.TotalMilliseconds / stepCount);
    }

    private static byte Lerp(byte a, byte b, float t)
        => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);
}
