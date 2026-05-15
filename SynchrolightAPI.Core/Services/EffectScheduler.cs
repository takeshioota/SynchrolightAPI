using System.Diagnostics;
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
    /// <summary>
    /// 連続送信間隔（ms）。デバイスの 200ms タイムアウトに対して十分な余裕を持たせた値。
    /// 20ms 間隔 = 1秒あたり50回送信。確実にセルフモード突入を防止する。
    /// </summary>
    private const int ContinuousSendIntervalMs = 20;

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
    /// 各ステップの保持期間中も 150ms 間隔で連続送信し、デバイスのセルフモード突入を防止する。
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
            await SendFrameForDurationAsync(field, rgb, interval, effectCt);
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
    /// 指定色を指定時間連続送信する（BeginEffect 後に使用）。
    /// holdDuration の間、ContinuousSendIntervalMs 間隔でパケットを送信し続ける。
    /// Flash の ON/OFF フェーズや補間ステップの保持に使用する。
    /// </summary>
    public async Task SendFrameForDurationAsync(
        byte field, Rgb color, TimeSpan holdDuration, CancellationToken effectCt)
    {
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < holdDuration)
        {
            effectCt.ThrowIfCancellationRequested();
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
            var remainingMs = (holdDuration - sw.Elapsed).TotalMilliseconds;
            var delayMs = (int)Math.Min(ContinuousSendIntervalMs, Math.Max(0, remainingMs));
            if (delayMs > 0)
                await Task.Delay(delayMs, effectCt);
        }
    }

    /// <summary>
    /// 連続フラッシュループ（BeginEffect 後に使用）。
    /// 単一の Stopwatch で絶対時刻ベースの ON/OFF トグルを行い、
    /// 短周期（100ms 以下）でも安定した点滅を実現する。
    /// パケットは事前ビルドし、ループ内でのオブジェクト生成を排除する。
    /// </summary>
    public async Task RunFlashLoopAsync(
        byte field, Rgb onColor, TimeSpan interval, CancellationToken effectCt)
    {
        var onPacket = LightProtocol.BuildA2_GlobalColor(field, onColor.R, onColor.G, onColor.B);
        var offPacket = LightProtocol.BuildA2_GlobalColor(field, 0, 0, 0);
        var intervalMs = (long)interval.TotalMilliseconds;

        var sw = Stopwatch.StartNew();
        long nextToggleMs = intervalMs;
        var isOn = true;

        while (!effectCt.IsCancellationRequested)
        {
            effectCt.ThrowIfCancellationRequested();

            var packet = isOn ? onPacket : offPacket;
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);

            // 絶対時刻でトグル判定（相対 delay の累積誤差を排除）
            if (sw.ElapsedMilliseconds >= nextToggleMs)
            {
                isOn = !isOn;
                nextToggleMs += intervalMs;
            }

            // 次のトグルまでの残り時間と送信間隔の短い方で待機
            var untilToggle = (int)(nextToggleMs - sw.ElapsedMilliseconds);
            var delayMs = Math.Clamp(Math.Min(ContinuousSendIntervalMs, untilToggle), 1, ContinuousSendIntervalMs);
            await Task.Delay(delayMs, effectCt);
        }
    }

    /// <summary>
    /// 指定色を連続的に再送信する。BeginEffect で新しい操作を開始し、
    /// キャンセルされるまで ~150ms 間隔で A2 パケットを送信し続ける。
    /// デバイスのセルフモード突入（200ms タイムアウト）を防止する。
    /// </summary>
    public async Task SendContinuousColorAsync(byte field, Rgb color, CancellationToken outerCt)
    {
        _logger.LogDebug("SendContinuousColorAsync: 連続カラー送信開始 ({R},{G},{B})", color.R, color.G, color.B);
        var effectCt = BeginEffect(outerCt);

        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        while (!effectCt.IsCancellationRequested)
        {
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
            await Task.Delay(ContinuousSendIntervalMs, effectCt);
        }
    }

    /// <summary>
    /// BeginEffect 済みの CancellationToken を使用して連続カラー送信する。
    /// SequencePlayer から使用：既に BeginEffect で前操作を停止済みの場合に、
    /// 再度リセット＋FlushQueue を発生させずに連続送信を開始する。
    /// </summary>
    public async Task ContinuousSendWithoutResetAsync(
        byte field, Rgb color, CancellationToken effectCt)
    {
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        while (!effectCt.IsCancellationRequested)
        {
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
            await Task.Delay(ContinuousSendIntervalMs, effectCt);
        }
    }

    /// <summary>
    /// 任意の事前ビルド済みパケットを連続送信する。BeginEffect で新しい操作を開始し、
    /// キャンセルされるまで ~150ms 間隔で送信し続ける。
    /// グループ指定（AA コマンド等）の連続送信に使用する。
    /// </summary>
    public async Task SendContinuousPacketAsync(byte[] packet, CancellationToken outerCt)
    {
        _logger.LogDebug("SendContinuousPacketAsync: 連続パケット送信開始");
        var effectCt = BeginEffect(outerCt);

        while (!effectCt.IsCancellationRequested)
        {
            await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
            await Task.Delay(ContinuousSendIntervalMs, effectCt);
        }
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
