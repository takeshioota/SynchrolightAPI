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
            _scheduler.Abort();
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
            var toR = step.R;
            var toG = step.G;
            var toB = step.B;
            var intervalMs = 30;
            var totalSteps = Math.Max(1, transitionMs / intervalMs);

            _logger.LogDebug("SEQ: Transition ({FR},{FG},{FB})→({TR},{TG},{TB}) {Ms}ms",
                from.R, from.G, from.B, toR, toG, toB, transitionMs);

            for (int i = 1; i <= totalSteps; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = (double)i / totalSteps;
                var r = (byte)(from.R + (toR - from.R) * t);
                var g = (byte)(from.G + (toG - from.G) * t);
                var b = (byte)(from.B + (toB - from.B) * t);

                var effectCt = _scheduler.BeginEffect(ct);
                await _scheduler.SendFrameAsync(step.Field, new Rgb(r, g, b), effectCt);
                if (i < totalSteps)
                    await Task.Delay(intervalMs, ct);
            }
            _lastStepColor = new Rgb(toR, toG, toB);
            // 遷移完了後、最終色で連続送信を開始
            var finalEffectCt = _scheduler.BeginEffect(ct);
            _ = _scheduler.ContinuousSendWithoutResetAsync(step.Field, new Rgb(toR, toG, toB), finalEffectCt);
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
        }
    }
}
