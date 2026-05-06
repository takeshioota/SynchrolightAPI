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
    /// <returns>シーケンスが見つからない場合 false</returns>
    public bool StartSequence(string name)
    {
        var sequence = _sequenceStore.Load(name);
        if (sequence == null) return false;

        lock (_lock)
        {
            StopEffectInternal();
            StopSequenceInternal();

            var cts = new CancellationTokenSource();
            _sequenceCts = cts;
            _currentSequenceName = name;

            _sequenceTask = Task.Run(async () =>
            {
                try
                {
                    await _sequencePlayer.PlayAsync(sequence, cts.Token);
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

        _logger.LogInformation("API: シーケンス再生開始 {Name}", name);
        return true;
    }

    /// <summary>実行中のシーケンスを停止する。</summary>
    public void StopSequence()
    {
        lock (_lock)
        {
            StopSequenceInternal();
        }
    }

    /// <summary>シーケンスの再生状態を取得する。</summary>
    public (bool IsPlaying, string? Name) GetSequenceStatus()
    {
        lock (_lock)
        {
            bool playing = _sequenceTask != null && !_sequenceTask.IsCompleted;
            return (playing, playing ? _currentSequenceName : null);
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
}
