using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Services;

/// <summary>
/// エフェクト種別
/// </summary>
public enum EffectType
{
    /// <summary>点滅: ON→OFF→ON→OFF→...</summary>
    Flash,
    /// <summary>Fade IN: Black→色 を繰り返し</summary>
    FadeIn,
    /// <summary>Fade OUT: 色→Black を繰り返し</summary>
    FadeOut,
    /// <summary>呼吸: Fade IN→Fade OUT を繰り返し</summary>
    Breathing,
    /// <summary>7色変化: 赤→緑→青→黄→シアン→マゼンタ→白 を繰り返し</summary>
    SevenColor,
}

/// <summary>
/// エフェクト実行パラメータ
/// </summary>
public record EffectParams(
    EffectType Type,
    Rgb Color,
    byte Field = 0x00,
    /// <summary>エフェクト1サイクルの所要時間</summary>
    TimeSpan? CycleDuration = null,
    /// <summary>Flash時のON/OFF間隔</summary>
    TimeSpan? FlashInterval = null,
    /// <summary>Fade補間ステップ数</summary>
    int FadeSteps = 20,
    /// <summary>連続再生するか（false=単発）</summary>
    bool Continuous = true
);

/// <summary>
/// エフェクトエンジン: Flash連続/Fade IN/Fade OUT/呼吸/7色変化を制御。
/// EffectScheduler に補間送信を委譲し、1回の指示で自律的に補間フレームを送信する。
/// 仕様: 3.1 基本機能、3.2 連続エフェクト、3.3 Fade滑らか化
/// </summary>
public class EffectEngine
{
    private readonly EffectScheduler _scheduler;
    private readonly ILogger<EffectEngine> _logger;

    private static readonly Rgb[] SevenColors =
    [
        Rgb.Red,
        new(0xFF, 0xFF, 0x00),  // 黄
        Rgb.Green,
        new(0x00, 0xFF, 0xFF),  // シアン
        Rgb.Blue,
        new(0xFF, 0x00, 0xFF),  // マゼンタ
        Rgb.White,
    ];

    public EffectEngine(
        EffectScheduler scheduler,
        ILogger<EffectEngine> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <summary>
    /// エフェクトを実行する。CancellationToken でキャンセルするまで継続（Continuous=true時）。
    /// 非連続（Continuous=false）の場合、エフェクト完了後に最終色を自動的に連続送信する。
    /// 内部で EffectScheduler.BeginEffect を呼び、前エフェクトを自動中断する。
    /// </summary>
    public async Task RunAsync(EffectParams p, CancellationToken ct)
    {
        _logger.LogInformation("エフェクト開始: {Type} color=({R},{G},{B}) continuous={Continuous}",
            p.Type, p.Color.R, p.Color.G, p.Color.B, p.Continuous);

        var effectCt = _scheduler.BeginEffect(ct);

        try
        {
            switch (p.Type)
            {
                case EffectType.Flash:
                    await RunFlashAsync(p, effectCt);
                    break;
                case EffectType.FadeIn:
                    await RunFadeInAsync(p, effectCt);
                    break;
                case EffectType.FadeOut:
                    await RunFadeOutAsync(p, effectCt);
                    break;
                case EffectType.Breathing:
                    await RunBreathingAsync(p, effectCt);
                    break;
                case EffectType.SevenColor:
                    await RunSevenColorAsync(p, effectCt);
                    break;
            }

            // エフェクトが正常完了（キャンセルではなく自然終了）した場合、
            // 最終色を連続送信してデバイスのセルフモード突入を防止する。
            var finalColor = GetFinalColor(p);
            _logger.LogInformation("エフェクト完了 → 最終色保持: ({R},{G},{B})", finalColor.R, finalColor.G, finalColor.B);
            // 高優先ラッチ: 通常キューが混雑（Chase 等）していても最終色を先に届け、確実に発色させる。
            await _scheduler.LatchColorHighPriorityAsync(p.Field, finalColor, effectCt);
            await _scheduler.ContinuousSendWithoutResetAsync(p.Field, finalColor, effectCt);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("エフェクト停止: {Type}", p.Type);
        }
    }

    /// <summary>エフェクト完了後に保持すべき最終色を決定する。</summary>
    private static Rgb GetFinalColor(EffectParams p) => p.Type switch
    {
        EffectType.FadeIn => p.Color,       // 黒→色 → 色を保持
        EffectType.FadeOut => Rgb.Black,     // 色→黒 → 黒を保持
        EffectType.Flash => Rgb.Black,       // ON/OFF → 黒を保持
        EffectType.Breathing => Rgb.Black,   // 吸→吐 → 黒を保持
        EffectType.SevenColor => Rgb.White,  // 最後の色(白)を保持
        _ => Rgb.Black,
    };

    private async Task RunFlashAsync(EffectParams p, CancellationToken effectCt)
    {
        var interval = p.FlashInterval ?? TimeSpan.FromMilliseconds(250);

        if (p.Continuous)
        {
            // 連続フラッシュ: 絶対時刻ベースの単一ループで精密に ON/OFF を切り替える。
            // 短周期（100ms 以下）でもタイミング誤差が蓄積しない。
            await _scheduler.RunFlashLoopAsync(p.Field, p.Color, interval, effectCt);
        }
        else
        {
            // 単発フラッシュ: 1 サイクル（ON→OFF）のみ実行
            await _scheduler.SendFrameForDurationAsync(p.Field, p.Color, interval, effectCt);
            await _scheduler.SendFrameForDurationAsync(p.Field, Rgb.Black, interval, effectCt);
        }
    }

    private async Task RunFadeInAsync(EffectParams p, CancellationToken effectCt)
    {
        var duration = p.CycleDuration ?? TimeSpan.FromSeconds(1);

        do
        {
            await _scheduler.SendInterpolationAsync(
                p.Field, Rgb.Black, p.Color, p.FadeSteps, duration, effectCt, perceptual: true);
        } while (p.Continuous && !effectCt.IsCancellationRequested);
    }

    private async Task RunFadeOutAsync(EffectParams p, CancellationToken effectCt)
    {
        var duration = p.CycleDuration ?? TimeSpan.FromSeconds(1);

        do
        {
            await _scheduler.SendInterpolationAsync(
                p.Field, p.Color, Rgb.Black, p.FadeSteps, duration, effectCt, perceptual: true);
        } while (p.Continuous && !effectCt.IsCancellationRequested);
    }

    private async Task RunBreathingAsync(EffectParams p, CancellationToken effectCt)
    {
        var halfDuration = (p.CycleDuration ?? TimeSpan.FromSeconds(2)) / 2;

        do
        {
            await _scheduler.SendInterpolationAsync(
                p.Field, Rgb.Black, p.Color, p.FadeSteps, halfDuration, effectCt, perceptual: true);
            await _scheduler.SendInterpolationAsync(
                p.Field, p.Color, Rgb.Black, p.FadeSteps, halfDuration, effectCt, perceptual: true);
        } while (p.Continuous && !effectCt.IsCancellationRequested);
    }

    private async Task RunSevenColorAsync(EffectParams p, CancellationToken effectCt)
    {
        var transitionDuration = (p.CycleDuration ?? TimeSpan.FromSeconds(7)) / SevenColors.Length;

        do
        {
            for (int i = 0; i < SevenColors.Length; i++)
            {
                effectCt.ThrowIfCancellationRequested();
                var from = SevenColors[i];
                var to = SevenColors[(i + 1) % SevenColors.Length];
                await _scheduler.SendInterpolationAsync(
                    p.Field, from, to, p.FadeSteps, transitionDuration, effectCt);
            }
        } while (p.Continuous && !effectCt.IsCancellationRequested);
    }
}
