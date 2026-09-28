using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Services;

/// <summary>
/// 色補間の曲線種別。SendInterpolationAsync が中間フレームの生成方法を切り替える。
/// </summary>
public enum FadeCurve
{
    /// <summary>byte 空間の線形補間（色相遷移の既定・SevenColor 等）。</summary>
    LinearByte,

    /// <summary>知覚（γ の逆空間）で線形。Fade IN/OUT/Breathing の暗部カクつき対策。</summary>
    Perceptual,

    /// <summary>
    /// 線形光（実輝度）空間で線形。色→色クロスフェード（OL / Color 遷移）で、
    /// 色が溶け合う間の実輝度を弦（直線）どおりに保ち、byte 線形で生じる中間の輝度沈み
    /// （ガンマの凸性による）を解消する（BUG-20260923-01）。
    /// ※輝度は一定になるが、輝度維持のため遷移中は反対チャンネルが残り続け、赤/緑が
    ///   くすんで（色あせて）見える副作用があった → 現在は VividLuminance に置き換え。
    /// </summary>
    LinearLight,

    /// <summary>
    /// 鮮やかさ保持＋輝度保持（BUG-20260923-01 デグレ対応・候補A）。
    /// byte 空間線形補間の「鮮やかな色経路（＝端点付近で純色に素早く戻る）」を保ったまま、
    /// 各フレームの明るさだけを目標実輝度（端点の実輝度和を線形補間した値）へ正規化する。
    /// LinearLight の「輝度は一定だが遷移中ずっと色あせる（赤/緑がくすむ）」副作用を解消しつつ、
    /// 中間の輝度沈みも出さない。OL / Color 遷移のクロスフェードで使用。
    /// </summary>
    VividLuminance,
}

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
    /// 2色間を<strong>線形光（実輝度）空間</strong>で補間するステップを生成する（BUG-20260923-01）。
    /// 各チャンネルを byte → 実輝度 lin=(byte/255)^γ に decode → lin を線形補間 → byte=255×lin^(1/γ) に
    /// encode する。実輝度が時間に対して直線（弦）で動くため、色が溶け合う間の輝度沈み（byte 線形補間＋
    /// ガンマの凸性で必ず中間が暗くなる現象）を解消する。両端が同輝度なら輝度は一定のまま色だけ混じる。
    /// 端点は元の色を厳密に再現する（t=0→from, t=1→to）。OL / Color 遷移のクロスフェード用。
    /// </summary>
    public IReadOnlyList<Rgb> LinearLightSteps(Rgb from, Rgb to, int stepCount, double gamma = DefaultGamma)
    {
        if (stepCount < 1) stepCount = 1;
        if (gamma <= 0) gamma = 1.0;

        // 端点を実輝度（0..1）へ decode
        double fr = Math.Pow(from.R / 255.0, gamma);
        double fg = Math.Pow(from.G / 255.0, gamma);
        double fb = Math.Pow(from.B / 255.0, gamma);
        double tr = Math.Pow(to.R / 255.0, gamma);
        double tg = Math.Pow(to.G / 255.0, gamma);
        double tb = Math.Pow(to.B / 255.0, gamma);

        double invGamma = 1.0 / gamma;
        var steps = new Rgb[stepCount + 1];
        for (int i = 0; i <= stepCount; i++)
        {
            double t = (double)i / stepCount;
            byte r = LinearLightLerp(fr, tr, t, invGamma);
            byte g = LinearLightLerp(fg, tg, t, invGamma);
            byte b = LinearLightLerp(fb, tb, t, invGamma);
            steps[i] = new Rgb(r, g, b);
        }
        return steps;
    }

    /// <summary>
    /// 2色間を<strong>鮮やかさ保持＋輝度保持</strong>で補間するステップを生成する（BUG-20260923-01 デグレ対応・候補A）。
    /// byte 空間線形補間の「色味（＝端点付近で反対 ch が素早く落ち、純色に戻る鮮やかな色経路）」を保ったまま、
    /// 各フレームの明るさだけを目標実輝度（端点の実輝度和 <c>Σ(byte/255)^γ</c> を t で線形補間した値）へ正規化する。
    /// これにより (1) 中間の輝度沈み（byte 線形＋ガンマ凸性で暗くなる現象・BUG-20260923-01 本体）を解消し、
    /// かつ (2) LinearLight のように輝度維持のため反対 ch を残して赤/緑がくすむ副作用も出さない。
    /// 端点は元の色を厳密に再現する（t=0→from, t=1→to）。単色↔黒（1ch のみ）の遷移は LinearLight と一致する。
    /// 明るさ正規化で 1.0 を超えた ch はクリップするため、端点輝度差が大きい遷移（例: 白→濃色）の中間は
    /// 目標よりわずかに暗くなり得るが、報告対象の濃色どうし（等輝度に近い）では発生しない。
    /// </summary>
    public IReadOnlyList<Rgb> VividLuminanceSteps(Rgb from, Rgb to, int stepCount, double gamma = DefaultGamma)
    {
        if (stepCount < 1) stepCount = 1;
        if (gamma <= 0) gamma = 1.0;

        double invGamma = 1.0 / gamma;
        // 端点の実輝度和（各 ch を実輝度に decode して合計）。目標輝度の線形補間に使う。
        double fromLum = DecodeLinear(from.R, gamma) + DecodeLinear(from.G, gamma) + DecodeLinear(from.B, gamma);
        double toLum   = DecodeLinear(to.R, gamma)   + DecodeLinear(to.G, gamma)   + DecodeLinear(to.B, gamma);

        var steps = new Rgb[stepCount + 1];
        for (int i = 0; i <= stepCount; i++)
        {
            double t = (double)i / stepCount;

            // 1) byte 空間線形補間の色味（鮮やかな色経路。ch 比＝彩度は端点付近で保たれる）
            double blR = from.R + (to.R - from.R) * t;
            double blG = from.G + (to.G - from.G) * t;
            double blB = from.B + (to.B - from.B) * t;

            // 2) その色の実輝度と、目標実輝度（端点輝度和の線形補間＝等輝度端点なら一定）
            double linR = DecodeLinear(blR, gamma);
            double linG = DecodeLinear(blG, gamma);
            double linB = DecodeLinear(blB, gamma);
            double curLum = linR + linG + linB;
            double targetLum = fromLum + (toLum - fromLum) * t;

            // 3) 明るさ（実輝度和）だけを目標へ正規化。色味＝ch 比は byte 線形のまま保つ。
            //    黒フレーム（curLum=0）は据置（そのまま黒）。
            double scale = curLum > 0.0 ? targetLum / curLum : 0.0;

            byte r = EncodeToByte(linR * scale, invGamma);
            byte g = EncodeToByte(linG * scale, invGamma);
            byte b = EncodeToByte(linB * scale, invGamma);
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

    /// <summary>
    /// 実輝度（線形光）空間で線形補間した値を byte（gamma-encode）へ戻す。
    /// fromLin/toLin は端点の実輝度（0..1）、t は 0..1 の進行度、invGamma = 1/γ。
    /// </summary>
    private static byte LinearLightLerp(double fromLin, double toLin, double t, double invGamma)
    {
        double lin = fromLin + (toLin - fromLin) * t;          // 実輝度で線形補間（弦どおり）
        double encoded = Math.Pow(Math.Max(0.0, lin), invGamma); // gamma-encode して byte 値へ
        return (byte)Math.Clamp(Math.Round(encoded * 255.0), 0, 255);
    }

    /// <summary>byte 値（0..255・小数可）を実輝度 0..1 へ decode する（VividLuminanceSteps 用）。</summary>
    private static double DecodeLinear(double byteValue, double gamma)
        => Math.Pow(Math.Clamp(byteValue, 0.0, 255.0) / 255.0, gamma);

    /// <summary>実輝度 0..1（正規化後は 1.0 超をクリップ）を gamma-encode して byte 値へ戻す（VividLuminanceSteps 用）。</summary>
    private static byte EncodeToByte(double linear, double invGamma)
        => (byte)Math.Clamp(Math.Round(Math.Pow(Math.Clamp(linear, 0.0, 1.0), invGamma) * 255.0), 0, 255);
}
