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
    /// 連続送信間隔（ms）。SNO端末は約1秒（仕様により1〜3秒の幅あり）でセルフモードに
    /// 復帰する。20ms 間隔 = 1秒あたり50回送信で、確実にセルフモード突入を防止する。
    /// </summary>
    private const int ContinuousSendIntervalMs = 20;

    /// <summary>
    /// 連続送信フレームのデッドライン（ms）。エンキューからこの時間内に送信されなければ
    /// TxWorker が破棄する（SendEnvelope.IsExpired）。これによりキュー滞留（backlog）を
    /// 防ぎ、常に最新色が届く。高頻度エフェクト（Chase 等）で通常キュー(256)が飽和し
    /// StopEffect の FlushQueue で送信途中の色が落ちる問題への対策。
    /// Flash ループ（RunFlashLoopAsync）と同じ考え方をフェード/保持経路にも適用する。
    /// </summary>
    private const int ContinuousSendDeadlineMs = 80;

    /// <summary>
    /// 補間フレーム数の下限。ごく短時間の Fade でも最低限の色解像度を確保する
    /// （従来 UI 側の下限と同じ 10）。
    /// </summary>
    private const int MinInterpolationFrames = 10;

    /// <summary>
    /// 補間フレーム数の上限。所要時間から算出したフレーム数が過大になっても
    /// 過剰送信（キュー飽和）を防ぐための安全上限。20ms×1500 = 30 秒相当。
    /// </summary>
    private const int MaxInterpolationFrames = 1500;

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
        TimeSpan duration, CancellationToken effectCt, bool perceptual = false)
    {
        // カクつき対策（Fade 滑らか化）: 可視フレーム数を所要時間から ~20ms/フレーム
        // （= ContinuousSendIntervalMs, ≈50fps）で算出する。従来は呼び出し側 stepCount
        // （UI 上限 150 / 既定 20）をそのまま使っていたため、3 秒超の Fade では
        // interval = duration / stepCount が 20ms を超え、1 色の保持が長くなって
        // 時間方向にカクついていた。stepCount は色解像度の下限として尊重し、上限
        // MaxInterpolationFrames で過剰送信（キュー飽和）を防ぐ。1 色の保持は最短でも
        // ContinuousSendIntervalMs のため、送信レートは連続送信と同じ 50 回/秒 を超えない。
        int timeBasedFrames = (int)Math.Round(duration.TotalMilliseconds / ContinuousSendIntervalMs);
        int frames = Math.Clamp(Math.Max(stepCount, timeBasedFrames),
                                MinInterpolationFrames, MaxInterpolationFrames);

        // perceptual=true（Fade IN/OUT/Breathing）はガンマ空間で補間し、消灯付近の
        // 知覚バンディング（カクつき）を解消する。色相遷移（SevenColor 等）は線形のまま。
        var steps = perceptual
            ? _interpolation.PerceptualSteps(from, to, frames)
            : _interpolation.LinearSteps(from, to, frames);
        var interval = _interpolation.CalcStepInterval(duration, frames);

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
    /// 連続送信フレーム用の送信オプションを生成する（呼び出し毎にフレッシュな Deadline）。
    /// RetransmitCount=1 で TxWorker の1パケット処理を軽くしてキュー消化を速める
    /// （多ポート×再送3回だと消化が投入(50回/秒)に追いつかずキューが飽和するため）。
    /// パケットロス耐性は 20ms 間隔の連続再送そのものが担保する。
    /// </summary>
    private static SendOptions ContinuousSendOptions() =>
        new(Deadline: DateTimeOffset.UtcNow.AddMilliseconds(ContinuousSendDeadlineMs), RetransmitCount: 1);

    /// <summary>
    /// 指定色を高優先キューで送出する（ラッチ）。通常キューの滞留(backlog)を飛び越えて
    /// 確実に発色させるために、エフェクトのフェード完了直後などに1発だけ呼ぶ。
    /// 高優先キューは TxWorker が通常キューより先に消化するため、混雑時でも即時に届く。
    /// </summary>
    public async Task LatchColorHighPriorityAsync(byte field, Rgb color, CancellationToken effectCt)
    {
        effectCt.ThrowIfCancellationRequested();
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        var options = new SendOptions(HighPriority: true, RetransmitCount: 2);
        await _transport.EnqueueAsync(packet, options, effectCt);
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
            await _transport.EnqueueAsync(packet, ContinuousSendOptions(), effectCt);
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
    /// GC ポーズやキュー飽和で遅延した場合は、現在時刻から正しい
    /// ON/OFF 状態を再計算してスキップし、高速トグルによる追いつきを防止する。
    /// パケットには短い Deadline を設定し、キューに滞留した古いパケットを
    /// TxWorker が自動破棄する。再送も1回に抑えて TxWorker の処理負荷を軽減する。
    /// </summary>
    public async Task RunFlashLoopAsync(
        byte field, Rgb onColor, TimeSpan interval, CancellationToken effectCt)
    {
        var onPacket = LightProtocol.BuildA2_GlobalColor(field, onColor.R, onColor.G, onColor.B);
        var offPacket = LightProtocol.BuildA2_GlobalColor(field, 0, 0, 0);
        var intervalMs = (long)interval.TotalMilliseconds;
        if (intervalMs <= 0) intervalMs = 1;

        // Flash 用の送信オプション:
        // - Deadline: エンキューから 50ms 以内に送信されなければ破棄（キュー滞留防止）
        // - RetransmitCount=1: 再送なし（TxWorker の処理時間を短縮し、キュー消化を高速化）
        var flashSendOptions = new SendOptions(
            Deadline: DateTimeOffset.UtcNow.AddMilliseconds(50),
            RetransmitCount: 1);

        var sw = Stopwatch.StartNew();

        while (!effectCt.IsCancellationRequested)
        {
            effectCt.ThrowIfCancellationRequested();

            // 現在時刻から ON/OFF 状態を計算（遅延蓄積時も一発で正しい状態に復帰）
            var elapsed = sw.ElapsedMilliseconds;
            var toggleCount = elapsed / intervalMs;
            var isOn = (toggleCount % 2) == 0;
            var nextToggleMs = (toggleCount + 1) * intervalMs;

            var packet = isOn ? onPacket : offPacket;
            // Deadline を毎回更新（エンキュー時点からの相対期限とするため）
            var options = flashSendOptions with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(50) };
            await _transport.EnqueueAsync(packet, options, effectCt);

            // 次のトグルまでの残り時間と送信間隔の短い方で待機
            var untilToggle = (int)(nextToggleMs - sw.ElapsedMilliseconds);
            var delayMs = Math.Clamp(Math.Min(ContinuousSendIntervalMs, untilToggle), 1, ContinuousSendIntervalMs);
            await Task.Delay(delayMs, effectCt);
        }
    }

    /// <summary>
    /// 指定色を連続的に再送信する。BeginEffect で新しい操作を開始し、
    /// キャンセルされるまで ~150ms 間隔で A2 パケットを送信し続ける。
    /// デバイスのセルフモード突入（SNO端末: 約1秒タイムアウト）を防止する。
    /// </summary>
    public async Task SendContinuousColorAsync(byte field, Rgb color, CancellationToken outerCt)
    {
        _logger.LogDebug("SendContinuousColorAsync: 連続カラー送信開始 ({R},{G},{B})", color.R, color.G, color.B);
        var effectCt = BeginEffect(outerCt);

        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        while (!effectCt.IsCancellationRequested)
        {
            await _transport.EnqueueAsync(packet, ContinuousSendOptions(), effectCt);
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
            await _transport.EnqueueAsync(packet, ContinuousSendOptions(), effectCt);
            await Task.Delay(ContinuousSendIntervalMs, effectCt);
        }
    }

    /// <summary>
    /// BeginEffect 済みの CancellationToken を使用して任意パケットを連続送信する。
    /// SequencePlayer から使用：既に BeginEffect で前操作を停止済みの場合に、
    /// 再度リセット＋FlushQueue を発生させずに連続送信を開始する。
    /// </summary>
    public async Task ContinuousSendPacketWithoutResetAsync(
        byte[] packet, CancellationToken effectCt)
    {
        while (!effectCt.IsCancellationRequested)
        {
            await _transport.EnqueueAsync(packet, ContinuousSendOptions(), effectCt);
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
            await _transport.EnqueueAsync(packet, ContinuousSendOptions(), effectCt);
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
            // BUG-20260902-01（所有権ガード）: 既にキャンセル済みの outerCt で入ってくるのは、
            // 停止された古い操作（再生中シーケンスが撒いた fire-and-forget のエフェクト/連続送信タスク等）が
            // unwind 途中にスケジューラへ再入したケース。ここで現行 _activeCts を無条件に横取りキャンセルすると、
            // 再生停止直後に張り直した色ホールド（StartColorHold の 20ms 連続送信）まで殺してしまい、
            // 連続送信が途切れて端末が 1〜3 秒後にセルフモードへ落ち消灯する（＝再生停止後に数秒で消灯）。
            // 正規の新規操作は必ず live な outerCt で始まるため、cancelled な場合は現行操作を尊重して
            // _activeCts には一切触れず、呼び出し元にはキャンセル済みトークンを返して即座に打ち切らせる。
            if (outerCt.IsCancellationRequested)
            {
                _logger.LogDebug("ResetActiveOperation: outerCt が既にキャンセル済み（stale 再入）→ 現行操作を保護し no-op");
                return CancellationTokenSource.CreateLinkedTokenSource(outerCt); // 既にキャンセル状態
            }
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
