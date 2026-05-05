using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// 通信制御の補間オーケストレーター。
/// 補間操作のライフサイクル（開始・中断・切替）を一元管理し、
/// 新操作受信時に前操作を自律的に中断する。
/// </summary>
public class EffectScheduler
{
    private readonly ITransport _transport;
    private readonly InterpolationService _interpolation;
    private readonly ILogger<EffectScheduler> _logger;
    private CancellationTokenSource? _activeCts;
    private readonly object _lock = new();

    public EffectScheduler(
        ITransport transport,
        InterpolationService interpolation,
        ILogger<EffectScheduler> logger)
    {
        _transport = transport;
        _interpolation = interpolation;
        _logger = logger;
    }

    /// <summary>
    /// 新しいエフェクト操作を開始する。
    /// 実行中の操作があれば自動キャンセル＋キューフラッシュを行う。
    /// 戻り値の CancellationToken をエフェクト全体で使用すること。
    /// </summary>
    public CancellationToken BeginEffect(CancellationToken outerCt)
    {
        _logger.LogDebug("BeginEffect: 新操作開始");
        return ResetActiveOperation(outerCt).Token;
    }

    /// <summary>
    /// 補間セグメントを送信する（BeginEffect 後に使用）。
    /// from→to を stepCount ステップで duration かけて送信する。
    /// BeginOperation は呼ばないため、同一エフェクト内の連続セグメントで
    /// 不要なキューフラッシュが発生しない。
    /// </summary>
    public async Task SendInterpolationAsync(
        byte field, Rgb from, Rgb to, int stepCount,
        TimeSpan duration, CancellationToken effectCt)
    {
        var steps = _interpolation.LinearSteps(from, to, stepCount);
        var interval = _interpolation.CalcStepInterval(duration, stepCount);

        foreach (var rgb in steps)
        {
            effectCt.ThrowIfCancellationRequested();
            var packet = LightProtocol.BuildA2_GlobalColor(field, rgb.R, rgb.G, rgb.B);
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
            await Task.Delay(interval, effectCt);
        }
    }

    /// <summary>
    /// 単発フレームを送信する（BeginEffect 後に使用）。
    /// Flash の ON/OFF など、補間なしの個別フレーム送信に使用。
    /// </summary>
    public async Task SendFrameAsync(byte field, Rgb color, CancellationToken effectCt)
    {
        effectCt.ThrowIfCancellationRequested();
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
    }

    /// <summary>
    /// 即時カラー送信。新操作として開始し、実行中の操作を自動中断する。
    /// </summary>
    public async Task SendColorAsync(byte field, Rgb color, CancellationToken outerCt)
    {
        _logger.LogDebug("SendColorAsync: 即時カラー送信 ({R},{G},{B})", color.R, color.G, color.B);
        ResetActiveOperation(outerCt);
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        await _transport.EnqueueAsync(packet, SendOptions.Default, outerCt);
    }

    /// <summary>
    /// 現在の操作を中断し、キューをフラッシュする。
    /// </summary>
    public void Abort()
    {
        lock (_lock)
        {
            if (_activeCts != null)
            {
                _logger.LogDebug("Abort: アクティブ操作を中断");
                _activeCts.Cancel();
                _activeCts.Dispose();
                _activeCts = null;
            }
        }
        _transport.FlushQueue();
    }

    private CancellationTokenSource ResetActiveOperation(CancellationToken outerCt)
    {
        lock (_lock)
        {
            if (_activeCts != null)
            {
                _activeCts.Cancel();
                _activeCts.Dispose();
            }
            _transport.FlushQueue();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            _activeCts = cts;
            return cts;
        }
    }
}
