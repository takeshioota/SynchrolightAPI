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
                        step.RainbowFadeInMs, step.RainbowFadeOutMs, effectCt);
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
        CancellationToken effectCt)
    {
        try
        {
            int colorCount = Math.Max(1, colors.Length);

            // Step 1: カラーパレット送信（0xA9 0x02）
            var colorSetupPacket = LightProtocol.BuildA9_SetRainbowColors(colors);
            await _transport.EnqueueAsync(colorSetupPacket, SendOptions.Default, effectCt);
            await Task.Delay(50, effectCt); // パレット設定の反映待ち

            // Step 2: モード別コマンドを colorFrameNo サイクルしながら継続送信
            var sw = Stopwatch.StartNew();
            byte currentFrame = 0;
            while (!effectCt.IsCancellationRequested)
            {
                if (cycleDurationMs > 0)
                {
                    var elapsed = sw.ElapsedMilliseconds;
                    currentFrame = (byte)(elapsed / cycleDurationMs % colorCount);
                }

                byte[] packet = mode switch
                {
                    0 => LightProtocol.BuildA9_RainbowSolid(currentFrame),
                    1 => LightProtocol.BuildA9_RainbowBlink(
                             currentFrame,
                             (ushort)Math.Clamp(blinkPeriodMs ?? 500, 100, 3600),
                             (byte)Math.Clamp(dutyRatio ?? 5, 1, 9)),
                    2 => LightProtocol.BuildA9_RainbowFadeInOut(
                             currentFrame,
                             (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000),
                             (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                    3 => LightProtocol.BuildA9_RainbowFadeIn(
                             currentFrame,
                             (ushort)Math.Clamp(fadeInMs ?? 1000, 256, 3000)),
                    4 => LightProtocol.BuildA9_RainbowFadeOut(
                             currentFrame,
                             (ushort)Math.Clamp(fadeOutMs ?? 1000, 256, 3000)),
                    5 => LightProtocol.BuildA9_RainbowRandom(currentFrame),
                    _ => LightProtocol.BuildA9_RainbowSolid(currentFrame),
                };

                await _transport.EnqueueAsync(packet, SendOptions.Default, effectCt);
                await Task.Delay(20, effectCt); // 20ms 間隔
            }
        }
        catch (OperationCanceledException) { /* 次操作への遷移／停止による正常キャンセル */ }
    }
}
