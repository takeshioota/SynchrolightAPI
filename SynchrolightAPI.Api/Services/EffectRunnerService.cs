using SynchrolightAPI.Domain;
using SynchrolightAPI.Models;
using SynchrolightAPI.Services;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// エフェクト/シーケンスのバックグラウンド実行を管理するシングルトン。
/// HTTP リクエスト間で CancellationTokenSource と実行状態を保持する。
/// </summary>
public class EffectRunnerService
{
    private readonly EffectEngine _effectEngine;
    private readonly EffectScheduler _scheduler;
    private readonly SequencePlayer _sequencePlayer;
    private readonly SequenceStore _sequenceStore;
    private readonly ILogger<EffectRunnerService> _logger;
    private readonly object _lock = new();

    // Effect state
    private CancellationTokenSource? _effectCts;
    private EffectParams? _currentEffect;
    private Task? _effectTask;

    // Sequence state
    private CancellationTokenSource? _sequenceCts;
    private string? _currentSequenceName;
    private Task? _sequenceTask;

    // Inline sequence state (ジャンプ用に保持)
    private Sequence? _inlineSequence;

    // Paused sequence state (割り込み点灯からの再開用)
    private string? _pausedSequenceName;
    private Sequence? _pausedInlineSequence;
    private int _pausedStepIndex = -1;
    // 一時停止時に「現ステップで既に経過していた時間（ms）」を保存して、
    // 再開時にステップの途中位置から続きを再生できるようにする。
    private int _pausedElapsedMsInStep;


    public EffectRunnerService(
        EffectEngine effectEngine,
        EffectScheduler scheduler,
        SequencePlayer sequencePlayer,
        SequenceStore sequenceStore,
        ILogger<EffectRunnerService> logger)
    {
        _effectEngine = effectEngine;
        _scheduler = scheduler;
        _sequencePlayer = sequencePlayer;
        _sequenceStore = sequenceStore;
        _logger = logger;
    }

    /// <summary>エフェクトを開始する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartEffect(EffectParams p)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = p;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _effectEngine.RunAsync(p, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "エフェクトタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: エフェクト開始 {Type}", p.Type);
    }

    /// <summary>指定色を連続送信する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartColorHold(byte field, Rgb color)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _scheduler.SendContinuousColorAsync(field, color, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "カラーホールドタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: カラーホールド開始 ({R},{G},{B})", color.R, color.G, color.B);
    }

    /// <summary>任意パケットを連続送信する。実行中のエフェクト/シーケンスは自動停止。</summary>
    public void StartPacketHold(byte[] packet)
    {
        lock (_lock)
        {
            StopSequenceInternal();
            StopEffectInternal();

            var cts = new CancellationTokenSource();
            _effectCts = cts;
            _currentEffect = null;

            _effectTask = Task.Run(async () =>
            {
                try
                {
                    await _scheduler.SendContinuousPacketAsync(packet, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "パケットホールドタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_effectCts == cts)
                        {
                            _currentEffect = null;
                            _effectTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: パケットホールド開始");
    }

    /// <summary>実行中のエフェクトを停止する。</summary>
    public void StopEffect()
    {
        lock (_lock)
        {
            StopEffectInternal();
        }
    }

    /// <summary>エフェクトの実行状態を取得する。</summary>
    public (bool IsRunning, EffectParams? Current) GetEffectStatus()
    {
        lock (_lock)
        {
            bool running = _effectTask != null && !_effectTask.IsCompleted;
            return (running, running ? _currentEffect : null);
        }
    }

    /// <summary>シーケンスを開始する。実行中のエフェクト/シーケンスは自動停止。</summary>
    /// <param name="name">保存済みシーケンス名</param>
    /// <param name="startFromIndex">再生開始ステップインデックス（既定 0）</param>
    /// <param name="initialElapsedMsInStartStep">開始ステップでの初期経過時間（途中再開用、既定 0）</param>
    /// <returns>シーケンスが見つからない場合 false</returns>
    public bool StartSequence(string name, int startFromIndex = 0, int initialElapsedMsInStartStep = 0)
    {
        var sequence = _sequenceStore.Load(name);
        if (sequence == null) return false;

        lock (_lock)
        {
            StopEffectInternal();
            StopSequenceInternal();
            _inlineSequence = null;
            ClearPausedStateInternal();

            var cts = new CancellationTokenSource();
            _sequenceCts = cts;
            _currentSequenceName = name;

            _sequenceTask = Task.Run(async () =>
            {
                try
                {
                    await _sequencePlayer.PlayAsync(sequence, cts.Token, startFromIndex, loop: false, initialElapsedMsInStartStep);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "シーケンスタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_sequenceCts == cts)
                        {
                            _currentSequenceName = null;
                            _sequenceTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: シーケンス再生開始 {Name} (開始ステップ={Start}, 初期経過={Elapsed}ms)",
            name, startFromIndex, initialElapsedMsInStartStep);
        return true;
    }

    /// <summary>
    /// 実行中のシーケンスを一時停止する。停止位置（ステップインデックス・現ステップ内経過時間・
    /// シーケンス参照）を内部に保存して、後続の ResumeSequence で続きから再開できるようにする。
    /// </summary>
    /// <returns>一時停止できた場合 true、再生中シーケンスがない場合 false</returns>
    public bool PauseSequence()
    {
        lock (_lock)
        {
            if (_sequenceTask == null || _sequenceTask.IsCompleted)
            {
                return false;
            }

            // 現在の状態を保存（ステップ index と現ステップで経過した時間の両方）
            _pausedSequenceName = _currentSequenceName;
            _pausedInlineSequence = _inlineSequence;
            _pausedStepIndex = _sequencePlayer.CurrentStepIndex;
            _pausedElapsedMsInStep = _sequencePlayer.CurrentStepElapsedMs;

            // 再生を停止（保存した状態は残る）
            StopSequenceInternal();
        }

        _logger.LogInformation("API: シーケンス一時停止 (step={Index}, 経過={Elapsed}ms)",
            _pausedStepIndex, _pausedElapsedMsInStep);
        return true;
    }

    /// <summary>
    /// PauseSequence で保存した位置（ステップ index ＋ 現ステップ内経過時間）から
    /// シーケンス再生を再開する。再開対象がない場合は false を返す。
    /// </summary>
    public bool ResumeSequence()
    {
        string? name;
        Sequence? inlineSeq;
        int startIdx;
        int elapsedMs;

        lock (_lock)
        {
            name = _pausedSequenceName;
            inlineSeq = _pausedInlineSequence;
            startIdx = Math.Max(0, _pausedStepIndex);
            elapsedMs = Math.Max(0, _pausedElapsedMsInStep);

            _pausedSequenceName = null;
            _pausedInlineSequence = null;
            _pausedStepIndex = -1;
            _pausedElapsedMsInStep = 0;
        }

        if (name != null)
        {
            var ok = StartSequence(name, startIdx, elapsedMs);
            if (ok)
            {
                _logger.LogInformation("API: シーケンス再開 {Name} (step={Index}, 経過={Elapsed}ms)",
                    name, startIdx, elapsedMs);
            }
            return ok;
        }
        if (inlineSeq != null)
        {
            StartSequenceInline(inlineSeq, startIdx, loop: false, elapsedMs);
            _logger.LogInformation("API: インラインシーケンス再開 (step={Index}, 経過={Elapsed}ms)",
                startIdx, elapsedMs);
            return true;
        }

        return false;
    }

    /// <summary>エディタ内容をインライン再生する（連続再生）。</summary>
    public void StartSequenceInline(Sequence sequence, int startFromIndex = 0, bool loop = false, int initialElapsedMsInStartStep = 0)
    {
        lock (_lock)
        {
            StopEffectInternal();
            StopSequenceInternal();

            _inlineSequence = sequence;
            _currentSequenceName = null;
            ClearPausedStateInternal();

            var cts = new CancellationTokenSource();
            _sequenceCts = cts;

            _sequenceTask = Task.Run(async () =>
            {
                try
                {
                    await _sequencePlayer.PlayAsync(sequence, cts.Token, startFromIndex, loop, initialElapsedMsInStartStep);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "インラインシーケンスタスク異常終了");
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_sequenceCts == cts)
                        {
                            _sequenceTask = null;
                        }
                    }
                }
            });
        }

        _logger.LogInformation("API: インラインシーケンス再生開始 (開始={Start})", startFromIndex);
    }

    /// <summary>単一ステップを即時実行する（ステップ再生）。</summary>
    public Task PlaySingleStepAsync(SequenceStep step)
    {
        if (step.CommandType == SequenceCommandType.Effect && step.EffectType.HasValue)
        {
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
                Continuous: true
            );
            StartEffect(effectParams);
        }
        else if (step.CommandType == SequenceCommandType.EffectStop)
        {
            lock (_lock)
            {
                StopEffectInternal();
                StopSequenceInternal();
                _inlineSequence = null;
            }
            _logger.LogDebug("ステップ再生: EffectStop");
        }
        else if (step.CommandType is SequenceCommandType.Color or SequenceCommandType.Off)
        {
            var color = step.CommandType == SequenceCommandType.Off
                ? Rgb.Black
                : new Rgb(step.R, step.G, step.B);
            StartColorHold(step.Field, color);
        }

        return Task.CompletedTask;
    }

    /// <summary>再生中にジャンプする。</summary>
    /// <returns>ジャンプ成功の場合 true</returns>
    public bool JumpToStep(int stepIndex)
    {
        lock (_lock)
        {
            if (_inlineSequence == null) return false;

            var sortedCount = _inlineSequence.Steps.Count;
            if (stepIndex < 0 || stepIndex >= sortedCount) return false;
        }

        // lock外で呼ぶ（StartSequenceInline内部でlockを取得するため）
        StartSequenceInline(_inlineSequence!, stepIndex);
        _logger.LogInformation("API: ジャンプ → ステップ {Index}", stepIndex);
        return true;
    }

    /// <summary>実行中のシーケンスを停止する。</summary>
    public void StopSequence()
    {
        lock (_lock)
        {
            StopSequenceInternal();
            _inlineSequence = null;
            ClearPausedStateInternal();
        }
    }

    /// <summary>シーケンスの再生状態を取得する（ステップ情報付き）。</summary>
    public (bool IsPlaying, string? Name, int CurrentStepIndex, int TotalStepCount) GetSequenceStatus()
    {
        lock (_lock)
        {
            bool playing = _sequenceTask != null && !_sequenceTask.IsCompleted;
            return (
                playing,
                playing ? _currentSequenceName : null,
                _sequencePlayer.CurrentStepIndex,
                _sequencePlayer.TotalStepCount
            );
        }
    }

    // --- internal helpers (caller must hold _lock) ---

    private void StopEffectInternal()
    {
        if (_effectCts != null)
        {
            _effectCts.Cancel();
            _effectCts.Dispose();
            _effectCts = null;
        }
        _scheduler.Abort();
        _currentEffect = null;
        _effectTask = null;
    }

    private void StopSequenceInternal()
    {
        if (_sequenceCts != null)
        {
            _sequenceCts.Cancel();
            _sequenceCts.Dispose();
            _sequenceCts = null;
        }
        _sequencePlayer.FlushAndStop();
        _currentSequenceName = null;
        _sequenceTask = null;
    }

    private void ClearPausedStateInternal()
    {
        _pausedSequenceName = null;
        _pausedInlineSequence = null;
        _pausedStepIndex = -1;
        _pausedElapsedMsInStep = 0;
    }
}
