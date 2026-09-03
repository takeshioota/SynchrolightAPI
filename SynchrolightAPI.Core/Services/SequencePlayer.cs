using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Services;

/// <summary>
/// シーケンスの時間ベース再生エンジン。
/// 仕様: 3.4 シーケンス選択＆再生、時間ベースのタイミング
/// </summary>
public class SequencePlayer
{
    private readonly ITransport _transport;
    private readonly EffectEngine _effectEngine;
    private readonly EffectScheduler _scheduler;
    private readonly ILogger<SequencePlayer> _logger;

    public SequencePlayer(
        ITransport transport,
        EffectEngine effectEngine,
        EffectScheduler scheduler,
        ILogger<SequencePlayer> logger)
    {
        _transport = transport;
        _effectEngine = effectEngine;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <summary>キューをフラッシュして即座に停止</summary>
    public void FlushAndStop()
    {
        _scheduler.Abort();
    }

    /// <summary>現在再生中かどうか</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>再生中のステップインデックス (-1 = 未再生)</summary>
    public volatile int CurrentStepIndex = -1;

    /// <summary>再生中のシーケンスの総ステップ数</summary>
    public int TotalStepCount { get; private set; }

    // 現在のステップが「発火した時点での Stopwatch.ElapsedMilliseconds」を記録する。
    // この値と現在の Stopwatch.ElapsedMilliseconds の差分が「現ステップに入ってからの経過時間」になる。
    // ただし、ジャンプや一時停止からの再開時に初期経過時間（initialElapsedMsInStartStep）が
    // ある場合は、それも加算する必要があるため _initialElapsedOffsetMs に保持しておく。
    private Stopwatch? _runningSw;
    private int _currentStepFiredAtSwMs = -1;
    private int _initialElapsedOffsetMs;

    /// <summary>
    /// 現在実行中ステップに入ってからの経過ミリ秒。
    /// 一時停止時に「次回再開のために現ステップで何ミリ秒進んでいたか」を取得するために使用する。
    /// 再生中でない場合は 0 を返す。
    /// </summary>
    public int CurrentStepElapsedMs
    {
        get
        {
            var sw = _runningSw;
            if (sw == null || _currentStepFiredAtSwMs < 0) return 0;
            // 現ステップ発火後の経過 + 開始ステップに入ったときの初期オフセット
            var elapsed = (int)sw.ElapsedMilliseconds - _currentStepFiredAtSwMs;
            // 最初のステップでは「ジャンプ/再開時の初期経過」も加算する
            if (CurrentStepIndex == _startFromIndexInRun)
            {
                elapsed += _initialElapsedOffsetMs;
            }
            return Math.Max(0, elapsed);
        }
    }

    // PlayAsync が現在の run で受け取った startFromIndex を保持する
    private int _startFromIndexInRun;

    // 最後に fire-and-forget で起動した連続エフェクトのタスク。
    // シーケンスの全ステップ実行後、このタスクが未完了であれば await して
    // シーケンスを IsPlaying=true のまま維持する。
    private Task? _pendingContinuousEffectTask;

    /// <summary>
    /// シーケンスを再生する。CancellationToken でキャンセルするまで実行。
    /// </summary>
    /// <param name="sequence">再生するシーケンス</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <param name="startFromIndex">再生開始ステップインデックス（ソート後の順序）</param>
    /// <param name="loop">ループ再生するかどうか</param>
    /// <param name="initialElapsedMsInStartStep">開始ステップでの初期経過時間（再開時のステップ途中位置）</param>
    public async Task PlayAsync(Sequence sequence, CancellationToken ct,
        int startFromIndex = 0, bool loop = false, int initialElapsedMsInStartStep = 0)
    {
        if (sequence.Steps.Count == 0)
        {
            _logger.LogWarning("空のシーケンス: {Name}", sequence.Name);
            return;
        }

        IsPlaying = true;
        var sortedSteps = sequence.Steps.OrderBy(s => s.TimeOffsetMs).ToList();
        TotalStepCount = sortedSteps.Count;
        CurrentStepIndex = startFromIndex;
        _startFromIndexInRun = startFromIndex;
        _initialElapsedOffsetMs = Math.Max(0, initialElapsedMsInStartStep);

        _logger.LogInformation("シーケンス再生開始: {Name} ({StepCount}ステップ, 開始={Start}, 初期経過={Elapsed}ms, ループ={Loop})",
            sequence.Name, sortedSteps.Count, startFromIndex, initialElapsedMsInStartStep, loop);

        try
        {
            do
            {
                // ジャンプ/途中再開対応:
                //   通常時      baseTimeMs = sortedSteps[startFromIndex].TimeOffsetMs
                //   途中再開時 baseTimeMs = sortedSteps[startFromIndex].TimeOffsetMs + initialElapsedMsInStartStep
                // とすることで、開始ステップは waitMs が負になり即時発火しつつ、
                // 後続ステップは「初期経過時間を差し引いた残り時間」だけ待機して発火する。
                var baseTimeMs = startFromIndex < sortedSteps.Count
                    ? sortedSteps[startFromIndex].TimeOffsetMs + initialElapsedMsInStartStep
                    : 0;
                var sw = Stopwatch.StartNew();
                _runningSw = sw;

                for (int i = startFromIndex; i < sortedSteps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var step = sortedSteps[i];

                    // 指定時刻まで待機（基準時刻からの相対）
                    var waitMs = (step.TimeOffsetMs - baseTimeMs) - (int)sw.ElapsedMilliseconds;
                    if (waitMs > 0)
                    {
                        await Task.Delay(waitMs, ct);
                    }

                    // ステップを実行する直前に CurrentStepIndex を更新する。
                    // 待機前に更新すると、現在実行中のステップではなく次に実行予定のステップを
                    // 指してしまい、UI のハイライト表示が実機の点灯より先行する現象が発生する。
                    CurrentStepIndex = i;
                    _currentStepFiredAtSwMs = (int)sw.ElapsedMilliseconds;
                    await ExecuteStepAsync(step, ct);
                }

                // ループ時は先頭に戻る（startFromIndexは初回のみ使用、initialElapsedMsもリセット）
                startFromIndex = 0;
                initialElapsedMsInStartStep = 0;
                _startFromIndexInRun = 0;
                _initialElapsedOffsetMs = 0;

            } while (loop && !ct.IsCancellationRequested);

            // 連続エフェクト（Flash 等）がバックグラウンドで動作中の場合、
            // そのタスクを await してシーケンスを IsPlaying=true のまま維持する。
            // これにより連続フラッシュがシーケンス完了後も安定して動作し、
            // ユーザーが明示的に停止するまで IsPlaying 状態を保つ。
            var pendingEffect = _pendingContinuousEffectTask;
            _pendingContinuousEffectTask = null;
            if (pendingEffect != null && !pendingEffect.IsCompleted)
            {
                _logger.LogInformation("シーケンス全ステップ実行完了 → 連続エフェクト動作中のため待機: {Name}", sequence.Name);
                await pendingEffect;
                _logger.LogInformation("連続エフェクト終了: {Name}", sequence.Name);
            }
            else
            {
                // 最終ステップを UI 側ポーリング（既定 200ms 間隔）が確実に検出できるよう、
                // 完了状態への遷移前に短時間待機する。
                await Task.Delay(300, ct);
                _logger.LogInformation("シーケンス再生完了: {Name}", sequence.Name);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("シーケンス再生中断: {Name}", sequence.Name);
            // BUG-20260814-01(No.98): ここで _scheduler.Abort() を呼ぶと共有 _activeCts を
            // キャンセルしてしまう。停止 → 設定色ホールド(StartColorHold) の順で走るとき、
            // このシーケンス teardown は別タスク上で「遅延着弾」し、後発の StartColorHold が
            // 確立した _activeCts（設定色の連続送信）を横取りキャンセルする TOCTOU となる。
            // その結果、Effect(FadeIn/FadeOut/Flash) を動作中に停止すると、設定色ホールドが即死し
            // 停止直前の中間フレーム色（減光/黒/Flash枠）が残る＝設定色と違う色が点灯していた。
            // シーケンスをキャンセルする全経路（StopSequenceInternal 等）は必ず直後に
            // FlushAndStop()=Abort で停止＋flush 済のため、ここでの Abort は冗長。
            // 停止直後に紛れ込んだ残フレームを掃くキュー掃除のみ行い、共有 CTS には触れない。
            _transport.FlushQueue();
        }
        finally
        {
            IsPlaying = false;
            CurrentStepIndex = -1;
            TotalStepCount = 0;
            _runningSw = null;
            _currentStepFiredAtSwMs = -1;
            _initialElapsedOffsetMs = 0;
            _pendingContinuousEffectTask = null;
            _lastStepColor = null; // 2026-05-30 追加
        }
    }

    // 2026-05-30 追加: スムーズ遷移で「前ステップの色」を記録する
    private Rgb? _lastStepColor;

    /// <summary>単一ステップを即時実行する。</summary>
    public async Task ExecuteStepAsync(SequenceStep step, CancellationToken ct)
    {
        // 2026-05-30 追加: TransitionMs によるスムーズ遷移 (A1)
        var transitionMs = step.TransitionMs ?? 0;
        if (transitionMs > 0 && _lastStepColor.HasValue
            && (step.CommandType == SequenceCommandType.Color || step.CommandType == SequenceCommandType.Color2))
        {
            var from = _lastStepColor.Value;
            var to = new Rgb(step.R, step.G, step.B);

            _logger.LogDebug("SEQ: Transition ({FR},{FG},{FB})→({TR},{TG},{TB}) {Ms}ms",
                from.R, from.G, from.B, to.R, to.G, to.B, transitionMs);

            // スムーズ遷移はエフェクトと同じ堅牢な補間送信（≈20ms/50fps・各フレーム保持中も再送）に統一する。
            // 旧実装は 30ms・1発送信・ループ内で毎回 BeginEffect（＝毎フレーム キューflush）だったため、
            // 時間方向のカクつきとチラつき/不安定の原因になっていた。BeginEffect は遷移開始時に1回だけ呼ぶ。
            var effectCt = _scheduler.BeginEffect(ct);
            await _scheduler.SendInterpolationAsync(
                step.Field, from, to, 20, TimeSpan.FromMilliseconds(transitionMs), effectCt);
            _lastStepColor = to;
            // 遷移完了後、最終色で連続送信を継続
            _ = _scheduler.ContinuousSendWithoutResetAsync(step.Field, to, effectCt);
            _pendingContinuousEffectTask = null;
            return;
        }

        var options = step.RetransmitCount.HasValue
            ? SendOptions.Default with { RetransmitCount = step.RetransmitCount }
            : SendOptions.Default;

        switch (step.CommandType)
        {
            case SequenceCommandType.Color:
                {
                    var color = new Rgb(step.R, step.G, step.B);
                    // BeginEffect で前操作を停止し、SendFrameAsync で最初のパケット送信を保証。
                    // その後 fire-and-forget で連続送信を開始する。
                    var effectCt = _scheduler.BeginEffect(ct);
                    await _scheduler.SendFrameAsync(step.Field, color, effectCt);
                    _ = _scheduler.ContinuousSendWithoutResetAsync(step.Field, color, effectCt);
                    _pendingContinuousEffectTask = null;  // Color に切り替わったのでエフェクト追跡をクリア
                    _lastStepColor = color; // 2026-05-30 追加
                    _logger.LogDebug("SEQ: Color ({R},{G},{B}) at {Time}ms", step.R, step.G, step.B, step.TimeOffsetMs);
                }
                break;

            case SequenceCommandType.Off:
                {
                    var effectCt = _scheduler.BeginEffect(ct);
                    await _scheduler.SendFrameAsync(step.Field, Rgb.Black, effectCt);
                    _ = _scheduler.ContinuousSendWithoutResetAsync(step.Field, Rgb.Black, effectCt);
                    _pendingContinuousEffectTask = null;  // Off に切り替わったのでエフェクト追跡をクリア
                    _lastStepColor = Rgb.Black; // 2026-05-30 追加
                    _logger.LogDebug("SEQ: Off at {Time}ms", step.TimeOffsetMs);
                }
                break;

            case SequenceCommandType.Effect:
                if (step.EffectType.HasValue)
                {
                    // RunAsync 内で BeginEffect が呼ばれ、前操作を自動停止する
                    var effectParams = new EffectParams(
                        Type: step.EffectType.Value,
                        Color: new Rgb(step.R, step.G, step.B),
                        Field: step.Field,
                        CycleDuration: step.EffectCycleDurationMs.HasValue
                            ? TimeSpan.FromMilliseconds(step.EffectCycleDurationMs.Value)
                            : null,
                        FlashInterval: step.EffectType.Value == EffectType.Flash && step.EffectCycleDurationMs.HasValue
                            ? TimeSpan.FromMilliseconds(step.EffectCycleDurationMs.Value / 2)
                            : null,
                        FadeSteps: step.FadeSteps ?? 20,
                        Continuous: step.Continuous
                    );
                    // エフェクトをバックグラウンドで実行（次のステップに進む）。
                    // 連続エフェクトの場合はタスクを保持し、全ステップ実行後に await する。
                    var effectTask = _effectEngine.RunAsync(effectParams, ct);
                    _pendingContinuousEffectTask = step.Continuous ? effectTask : null;
                    _lastStepColor = new Rgb(step.R, step.G, step.B); // 2026-05-30 追加
                    _logger.LogDebug("SEQ: Effect {Type} ({R},{G},{B}) continuous={Continuous} started at {Time}ms",
                        step.EffectType, step.R, step.G, step.B, step.Continuous, step.TimeOffsetMs);
                }
                break;

            case SequenceCommandType.EffectStop:
                _scheduler.Abort();
                _pendingContinuousEffectTask = null;
                _logger.LogDebug("SEQ: EffectStop at {Time}ms", step.TimeOffsetMs);
                break;

            case SequenceCommandType.InternalProgram:
                if (step.FrameNo.HasValue)
                {
                    // 前操作を停止し、A1パケットを連続送信してセルフモード突入を防止
                    var a1Packet = LightProtocol.BuildA1_PlaySequence(step.FrameNo.Value);
                    var effectCt = _scheduler.BeginEffect(ct);
                    await _transport.EnqueueAsync(a1Packet, options, effectCt);
                    _ = _scheduler.ContinuousSendPacketWithoutResetAsync(a1Packet, effectCt);
                    _pendingContinuousEffectTask = null;
                    _logger.LogDebug("SEQ: InternalProgram frame={FrameNo} at {Time}ms",
                        step.FrameNo.Value, step.TimeOffsetMs);
                }
                break;

            // 2026-05-30 追加: 2色交互点灯 (A2)
            case SequenceCommandType.Color2:
                {
                    var color1 = new Rgb(step.R, step.G, step.B);
                    var color2Alt = new Rgb(step.R2 ?? 0, step.G2 ?? 0, step.B2 ?? 0);
                    var bpm = step.Bpm ?? 120;
                    var cycleDurationMs = bpm > 0 ? 60000 / bpm : 500;
                    var halfCycleMs = Math.Max(50, cycleDurationMs / 2);

                    _logger.LogDebug("SEQ: Color2 ({R1},{G1},{B1})↔({R2},{G2},{B2}) BPM={BPM} at {Time}ms",
                        color1.R, color1.G, color1.B, color2Alt.R, color2Alt.G, color2Alt.B, bpm, step.TimeOffsetMs);

                    // 前操作を停止し、交互点灯ループを開始
                    var effectCt = _scheduler.BeginEffect(ct);
                    var color2Task = Task.Run(async () =>
                    {
                        var isFirst = true;
                        while (!effectCt.IsCancellationRequested)
                        {
                            var c = isFirst ? color1 : color2Alt;
                            try
                            {
                                await _scheduler.SendFrameAsync(step.Field, c, effectCt);
                            }
                            catch { break; }
                            isFirst = !isFirst;
                            try { await Task.Delay(halfCycleMs, effectCt); }
                            catch (OperationCanceledException) { break; }
                        }
                    }, effectCt);
                    _pendingContinuousEffectTask = color2Task;
                    _lastStepColor = color1;
                }
                break;

            // V4.5: レインボー（CommandType=Rainbow/RainbowStop/RainbowPause）
            // 注意: ライブ用 EffectRunnerService.StartRainbow()/PauseRainbow() は内部で
            //       StopSequenceInternal()（StartPacketHold 経由）を呼び、再生中シーケンスを
            //       停止してしまう。ここでは呼ばず、scheduler/transport プリミティブのみで実装する。
            case SequenceCommandType.Rainbow:
                {
                    var rbColors = (step.RainbowColors != null && step.RainbowColors.Count > 0)
                        ? step.RainbowColors.Select(c => (c.R, c.G, c.B)).ToArray()
                        : DefaultRainbowColors;
                    var mode = step.RainbowMode ?? 0;
                    var cycle = step.RainbowCycleDurationMs ?? 1000;
                    // BeginEffect で前操作を停止し、自前のレインボーループを fire-and-forget で開始。
                    var effectCt = _scheduler.BeginEffect(ct);
                    var rainbowTask = RunRainbowLoopAsync(
                        mode, rbColors, cycle,
                        step.RainbowBlinkPeriodMs, step.RainbowDutyRatio,
                        step.RainbowFadeInMs, step.RainbowFadeOutMs, step.Field, effectCt);
                    _pendingContinuousEffectTask = rainbowTask;
                    _logger.LogDebug("SEQ: Rainbow mode={Mode} colors={N} cycle={Cycle}ms at {Time}ms",
                        mode, rbColors.Length, cycle, step.TimeOffsetMs);
                }
                break;

            case SequenceCommandType.RainbowStop:
                _scheduler.Abort();
                _pendingContinuousEffectTask = null;
                _logger.LogDebug("SEQ: RainbowStop at {Time}ms", step.TimeOffsetMs);
                break;

            case SequenceCommandType.RainbowPause:
                {
                    // InternalProgram と同様に、0xA9 0x04（前回色保持）を連続送信して
                    // 端末がセルフモードに戻らないようにする。シーケンスは停止しない。
                    var pausePacket = LightProtocol.BuildA9_RainbowPause();
                    var effectCt = _scheduler.BeginEffect(ct);
                    await _transport.EnqueueAsync(pausePacket, options, effectCt);
                    _ = _scheduler.ContinuousSendPacketWithoutResetAsync(pausePacket, effectCt);
                    _pendingContinuousEffectTask = null;
                    _logger.LogDebug("SEQ: RainbowPause (0xA9 0x04) at {Time}ms", step.TimeOffsetMs);
                }
                break;
        }
    }

    /// <summary>レインボーループ用の既定カラーパレット（7色）。ステップに色指定が無い場合に使用。</summary>
    private static readonly (byte r, byte g, byte b)[] DefaultRainbowColors =
    {
        (0xFF, 0x00, 0x00), (0xFF, 0x7F, 0x00), (0xFF, 0xFF, 0x00),
        (0x00, 0xFF, 0x00), (0x00, 0x00, 0xFF), (0x4B, 0x00, 0x82), (0x94, 0x00, 0xD3),
    };

    /// <summary>
    /// レインボーの継続送信ループ（シーケンスステップ用）。
    /// EffectRunnerService.RunRainbowLoopAsync と同等のロジックを、再生中シーケンスを
    /// 停止しないよう scheduler/transport プリミティブのみで実装したもの。
    /// 1. カラーパレット送信（0xA9 0x02）
    /// 2. cycleDurationMs ごとに colorFrameNo を進めつつ、モード別 0xA9 0x03 を 20ms 間隔で継続送信
    /// </summary>
    private async Task RunRainbowLoopAsync(
        int mode,
        (byte r, byte g, byte b)[] colors,
        int cycleDurationMs,
        int? blinkPeriodMs,
        int? dutyRatio,
        int? fadeInMs,
        int? fadeOutMs,
        byte field,
        CancellationToken effectCt)
    {
        try
        {
            int colorCount = Math.Max(1, colors.Length);

            // モード別パケット生成（初回ラッチとループで共用）
            byte[] BuildModePacket(byte frame) => mode switch
            {
                0 => LightProtocol.BuildA9_RainbowSolid(frame),
                1 => LightProtocol.BuildA9_RainbowBlink(
                         frame,
                         (ushort)Math.Clamp(blinkPeriodMs ?? 500, 100, 3600),
                         (byte)Math.Clamp(dutyRatio ?? 5, 1, 9)),
                2 => LightProtocol.BuildA9_RainbowFadeInOut(
                         frame,
                         (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000),
                         (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                3 => LightProtocol.BuildA9_RainbowFadeIn(
                         frame,
                         (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000)),
                4 => LightProtocol.BuildA9_RainbowFadeOut(
                         frame,
                         (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                5 => LightProtocol.BuildA9_RainbowRandom(frame),
                _ => LightProtocol.BuildA9_RainbowSolid(frame),
            };

            // BUG-20260823-01: 新Rainbow開始時に前エフェクトの残色が一瞬光る不具合の対策（一括再生経路）。
            // EffectRunnerService.RunRainbowLoopAsync と同じく、パレットと先頭フレームを高優先で送出し
            // 残フレームを飛び越えて即座に新色へ上書きする。
            var highPriority = new SendOptions(HighPriority: true, RetransmitCount: 2);

            // BUG-20260901-04: Flash→Rainbow 遷移で、Flashピーク色が端末にラッチされたまま
            // 下の「パレット反映待ち(50ms)」の間 残り「一瞬フリーズ＋前色残り」に見える不具合の対策。
            // パレット送信の前に高優先の黒(消灯)を1発入れ、端末が保持中の残色を即座に打ち消す。
            // これにより 50ms 窓は「前色ラッチ」→「一瞬の暗転」となり、エフェクト間の自然な遷移になる。
            // field は当該ステップの場次に合わせる（前ステップの点灯色と同じ場次で上書きするため）。
            await _transport.EnqueueAsync(
                LightProtocol.BuildA2_GlobalColor(field, 0, 0, 0), highPriority, effectCt);

            // Step 1: カラーパレット送信（0xA9 0x02）— 高優先で確実に先着させる
            var colorSetupPacket = LightProtocol.BuildA9_SetRainbowColors(colors);
            await _transport.EnqueueAsync(colorSetupPacket, highPriority, effectCt);
            await Task.Delay(50, effectCt); // パレット設定の反映待ち

            // 先頭フレーム(0)を高優先で1発ラッチし、保持されている旧色を確定的に上書きする
            await _transport.EnqueueAsync(BuildModePacket(0), highPriority, effectCt);

            // BUG(FI/FO光はじめ不定): FI/FO 系で「色切替間隔 < フェード時間」だと、フェード進行中に
            // colorFrameNo が進み端末がフェード途中で次色へ強制遷移するため、毎回不定の位相でフェードが
            // 始まり「光はじめが一定でない」不具合になる（遅いフェード設定ほど顕著）。
            // 仕様3.18注記3「フレーム移行時間はフェード時間以上（未満禁止）」に従い、色切替間隔を
            // フェード包絡長以上へクランプする（フェード値は BuildModePacket と同じ 256-3000ms で評価）。
            int fiMs = Math.Clamp(fadeInMs ?? 1000, 256, 3000);
            int foMs = Math.Clamp(fadeOutMs ?? 1000, 256, 3000);
            int minCycleMs = mode switch
            {
                2 => fiMs + foMs, // FadeInOut: フェードイン＋フェードアウトで1周期
                3 => fiMs,        // FadeIn
                4 => foMs,        // FadeOut
                _ => 0,           // Solid/Blink/Random は対象外
            };
            int effectiveCycleMs = (cycleDurationMs > 0 && minCycleMs > 0)
                ? Math.Max(cycleDurationMs, minCycleMs)
                : cycleDurationMs;
            if (effectiveCycleMs != cycleDurationMs)
            {
                _logger.LogInformation(
                    "SEQ Rainbow FI/FO: 色切替間隔を {Req}ms → {Eff}ms にクランプ（フェード時間以上, 仕様3.18注記3）",
                    cycleDurationMs, effectiveCycleMs);
            }

            // Step 2: モード別コマンドを送信する。
            // BUG-20260903-01/03 対策で送信方式を再設計:
            //   旧実装は同一 A9 03 を 20ms 毎(=約50回/秒)連投していたため、
            //     ・端末の受信/描画バッファを飽和させ 30〜40秒後に色循環が加速する（BUG-01）
            //     ・色替えが 20ms 境界に量子化され FI/FO にズレが出る（BUG-03）
            //   新実装は「フレーム変化時のみ、周期境界の正確な瞬間に送信」＋
            //   「RainbowRefreshMs 間隔の低レート維持送信」（セルフモード防止・RF欠落補償）とする。
            //   これによりパケット量を大幅削減（cycle=1000ms で約50→約2回/秒）しつつ色替えを正確化する。
            const int RainbowRefreshMs = 500; // 維持送信間隔（実機で調整可）
            var sw = Stopwatch.StartNew();
            byte lastFrame = 0;               // 先頭フレーム(0)は上で高優先ラッチ済み
            long lastSendMs = 0;
            long framesChanged = 0, packetsSent = 0, lastSummaryMs = 0;
            var changeOpts = new SendOptions(RetransmitCount: 2); // 色替え瞬間の到達信頼性
            while (!effectCt.IsCancellationRequested)
            {
                long now = sw.ElapsedMilliseconds;
                byte target = effectiveCycleMs > 0
                    ? LightProtocol.RainbowFrameAt(now, effectiveCycleMs, colorCount)
                    : lastFrame;

                if (target != lastFrame)
                {
                    // 周期境界を跨いだ瞬間に、次フレームを再送付きで正確に送る
                    await _transport.EnqueueAsync(BuildModePacket(target), changeOpts, effectCt);
                    lastFrame = target; lastSendMs = now; framesChanged++; packetsSent++;
                    _logger.LogDebug("SEQ Rainbow frame→{Frame} at {Elapsed}ms", target, now);
                }
                else if (now - lastSendMs >= RainbowRefreshMs)
                {
                    // 変化が無い間は低レートで同一フレームを維持送信（セルフモード防止・欠落補償）
                    await _transport.EnqueueAsync(BuildModePacket(lastFrame), SendOptions.Default, effectCt);
                    lastSendMs = now; packetsSent++;
                }

                // 10秒ごとに送信要約（BUG-01の実機切り分け＝PC側送信が終始一定であることの確認用）
                if (now - lastSummaryMs >= 10_000)
                {
                    _logger.LogInformation(
                        "SEQ Rainbow 送信要約: 経過={Sec}s, フレーム変化={Frames}, 送信パケット={Packets}, cycle={Cycle}ms",
                        now / 1000, framesChanged, packetsSent, effectiveCycleMs);
                    lastSummaryMs = now;
                }

                // 「次の周期境界」か「次の維持送信」の早い方まで眠る（最小5ms・キャンセルは即時throw）
                long toNextFrame = effectiveCycleMs > 0 ? effectiveCycleMs - (now % effectiveCycleMs) : RainbowRefreshMs;
                long toRefresh = RainbowRefreshMs - (now - lastSendMs);
                int sleep = (int)Math.Max(5, Math.Min(toNextFrame, toRefresh));
                await Task.Delay(sleep, effectCt);
            }
        }
        catch (OperationCanceledException) { /* 次操作への遷移／停止による正常キャンセル */ }
    }
}
