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

    // ─── 停止時の「現在の光度」保持（20260904）───────────────────────────
    // フェード等のエフェクトは本スケジューラが 1 フレームずつ A2(単色) を送出する。
    // 停止した瞬間に表示されていた色（＝直近に送出した A2 色）を記録しておき、UI 側が
    // 停止時にこの色で保持することで「停止時の光度をキープ」を実現する（設定色フルへ跳ねない）。
    private readonly object _lastColorLock = new();
    private (byte field, Rgb color)? _lastColor;

    /// <summary>直近に送出した単色(A2)を取得する。未送出なら false。</summary>
    public bool TryGetLastColor(out byte field, out Rgb color)
    {
        lock (_lastColorLock)
        {
            if (_lastColor.HasValue)
            {
                (field, color) = _lastColor.Value;
                return true;
            }
        }
        field = 0;
        color = Rgb.Black;
        return false;
    }

    private void RecordLastColor(byte field, Rgb color)
    {
        lock (_lastColorLock) { _lastColor = (field, color); }
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
        TimeSpan duration, CancellationToken effectCt, FadeCurve curve = FadeCurve.LinearByte)
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

        // 補間曲線を種別で切替える：
        //  Perceptual（Fade IN/OUT/Breathing）＝ガンマ空間で補間し消灯付近の知覚バンディング（カクつき）を解消。
        //  VividLuminance（OL / Color 遷移）＝byte 線形の鮮やかな色経路を保ちつつ明るさだけ目標輝度へ正規化し、
        //    輝度沈み（BUG-20260923-01 本体）と赤/緑の色あせ（同デグレ）を同時に解消。
        //  LinearLight＝実輝度空間で補間（旧・輝度沈みは消えるが遷移中色あせる。VividLuminance に置換済）。
        //  LinearByte（既定・SevenColor 等の色相遷移）＝byte 空間の線形補間。
        var steps = curve switch
        {
            FadeCurve.Perceptual    => _interpolation.PerceptualSteps(from, to, frames),
            FadeCurve.VividLuminance => _interpolation.VividLuminanceSteps(from, to, frames),
            FadeCurve.LinearLight   => _interpolation.LinearLightSteps(from, to, frames),
            _                       => _interpolation.LinearSteps(from, to, frames),
        };
        var interval = _interpolation.CalcStepInterval(duration, frames);

        foreach (var rgb in steps)
        {
            effectCt.ThrowIfCancellationRequested();
            // BUG-20260928-01: 補間フレームは一意なので must-send（コアレス破棄しない）で送る。
            await SendFrameForDurationAsync(field, rgb, interval, effectCt, dropIfLate: false);
        }
    }

    /// <summary>
    /// 単発フレームを送信する（BeginEffect 後に使用）。
    /// Flash の ON/OFF など、補間なしの個別フレーム送信に使用。
    /// </summary>
    public async Task SendFrameAsync(byte field, Rgb color, CancellationToken effectCt)
    {
        effectCt.ThrowIfCancellationRequested();
        RecordLastColor(field, color);
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
    /// 補間フレーム（フェード／OL／Color 遷移／SevenColor の途中経過）用の送信オプション（BUG-20260928-01）。
    /// <para>
    /// 補間フレームは <see cref="SendOptions.LatestOnly"/>=true で専用の「最新値」チャネル
    /// （容量わずか・DropOldest）へ送る。消化に余裕がある通常時は全フレームが順序どおり流れて滑らか。
    /// 消化が一時的に追いつかない過負荷時のみ、古い中間フレームを捨てて<strong>常に最新へ追従</strong>する。
    /// </para>
    /// <para>
    /// 旧実装は「一意フレームを 1 つも捨てない must-send（通常キュー FullMode=Wait）」だったが、
    /// 消化（約42〜50/s）が生産（約50fps）をわずかに下回ると滞留が累積して最大数秒遅れ、
    /// 「数秒前の色が出続ける＝演出が止まる」事象になっていた。最新値方式は滞留を容量上限（≒80ms）に
    /// 抑えるため凍結しない。過負荷時に間引くのは「過去フレームへ化ける（光が飛ぶ）」ではなく
    /// 「最新へ前進（＝少し急ぎ足のフェード）」なので、旧 must-send 化以前のジャンプ回帰も招かない。
    /// RetransmitCount=1 は TxWorker の処理を軽く保ち消化レートを上げるため。
    /// ※同色を繰り返すキープアライブ／保持／Flash ループは従来どおり短い Deadline でコアレスする（破棄されても無害）。
    /// </para>
    /// </summary>
    private static SendOptions InterpolationFrameSendOptions() => new(RetransmitCount: 1, LatestOnly: true);

    /// <summary>
    /// 指定色を高優先キューで送出する（ラッチ）。通常キューの滞留(backlog)を飛び越えて
    /// 確実に発色させるために、エフェクトのフェード完了直後などに1発だけ呼ぶ。
    /// 高優先キューは TxWorker が通常キューより先に消化するため、混雑時でも即時に届く。
    /// </summary>
    public async Task LatchColorHighPriorityAsync(byte field, Rgb color, CancellationToken effectCt)
    {
        effectCt.ThrowIfCancellationRequested();
        RecordLastColor(field, color);
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        var options = new SendOptions(HighPriority: true, RetransmitCount: 2);
        await _transport.EnqueueAsync(packet, options, effectCt);
    }

    /// <summary>
    /// 指定色を指定時間連続送信する（BeginEffect 後に使用）。
    /// holdDuration の間、ContinuousSendIntervalMs 間隔でパケットを送信し続ける。
    /// Flash の ON/OFF フェーズや補間ステップの保持に使用する。
    /// <paramref name="dropIfLate"/>=true（既定）は 80ms コアレス期限つき（同色再送＝キープアライブ用途で、
    /// 渋滞時に破棄されても無害）。false は期限なし＝must-send（BUG-20260928-01：補間の一意フレームが
    /// 破棄されて「光が飛ぶ」のを防ぐ。破棄せずバックプレッシャで順序どおり確実送出）。
    /// </summary>
    public async Task SendFrameForDurationAsync(
        byte field, Rgb color, TimeSpan holdDuration, CancellationToken effectCt,
        bool dropIfLate = true)
    {
        RecordLastColor(field, color);
        var packet = LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < holdDuration)
        {
            effectCt.ThrowIfCancellationRequested();
            var options = dropIfLate ? ContinuousSendOptions() : InterpolationFrameSendOptions();
            await _transport.EnqueueAsync(packet, options, effectCt);
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
        var offColor = new Rgb(0, 0, 0);
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
            RecordLastColor(field, isOn ? onColor : offColor);
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

        RecordLastColor(field, color);
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
        RecordLastColor(field, color);
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
    /// 複数の事前ビルド済みパケットを「1まとまり（バースト）」で連続送信して保持する。
    /// BeginEffect で新操作を開始し、キャンセルまで「全パケットを間を空けず連続送出 → burstGapMs 待機」を繰り返す。
    /// 1パケットずつ巡回送信（従来の RotateInterval 方式）より、全ブロック（例: レインボー7色）の更新が
    /// 1バースト内（≒数十ms）に収まり、ブロック（端末）ごとの点灯/色替えタイミングのずれを小さくできる。
    /// RetransmitCount=1 で TxWorker の1パケット処理を軽くしてバーストを素早く送り切る
    /// （到達信頼性は burstGapMs 間隔で反復されるバーストそのものが担保する）。
    /// </summary>
    public async Task SendContinuousBurstAsync(IReadOnlyList<byte[]> packets, int burstGapMs, int retransmit, CancellationToken outerCt, int interPacketGapMs = 0)
    {
        _logger.LogDebug("SendContinuousBurstAsync: バースト連続送信開始 ({N}パケット, gap={Gap}ms, retransmit={Rt}, interGap={Inter}ms)", packets.Count, burstGapMs, retransmit, interPacketGapMs);
        var effectCt = BeginEffect(outerCt);
        int gap = Math.Max(1, burstGapMs);
        int interGap = Math.Max(0, interPacketGapMs);
        // retransmit>1: 各パケットを back-to-back で複数回送る＝RF取りこぼし対策。
        // A3(全色1パケット)では再送してもブロック間のずれは増えない（同一パケットの反復のため）。
        var opts = new SendOptions(RetransmitCount: Math.Max(1, retransmit));
        while (!effectCt.IsCancellationRequested)
        {
            foreach (var p in packets)
            {
                await _transport.EnqueueAsync(p, opts, effectCt);
                // 送出レート検証モード(interGap>0): 1パケット発行ごとに均一な間隔を空け、送信機に届くAC
                // パケットの発行間隔を可変にする。Windowsタイマー分解能(~15.6ms)では Task.Delay が 0〜10ms を
                // 刻めないため Stopwatch スピン待機で正確に待つ。この間はバースト間キープアライブ(gap)は使わず
                // 発行間隔そのもので連続ループする＝均一レートのストリームになる。
                if (interGap > 0)
                    SpinWaitMs(interGap, effectCt);
            }
            if (interGap <= 0)
                await Task.Delay(gap, effectCt);
        }
    }

    /// <summary>
    /// Stopwatch ベースの高精度スピン待機（0〜十数ms 用）。OSのタイマー分解能(~15.6ms)に依存せず
    /// ミリ秒単位で待てる。短時間専用（長い待機に使うと1コアを占有する）。キャンセルで即抜ける。
    /// </summary>
    private static void SpinWaitMs(int ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        var sw = Stopwatch.StartNew();
        double target = ms;
        while (sw.Elapsed.TotalMilliseconds < target)
        {
            if (ct.IsCancellationRequested) return;
            System.Threading.Thread.SpinWait(64);
        }
    }

    /// <summary>
    /// 即時カラー送信。新操作として開始し、実行中の操作を自動中断する。
    /// </summary>
    public async Task SendColorAsync(byte field, Rgb color, CancellationToken outerCt)
    {
        _logger.LogDebug("SendColorAsync: 即時カラー送信 ({R},{G},{B})", color.R, color.G, color.B);
        ResetActiveOperation(outerCt);
        RecordLastColor(field, color);
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
