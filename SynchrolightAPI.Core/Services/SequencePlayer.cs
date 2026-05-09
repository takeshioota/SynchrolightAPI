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

    /// <summary>
    /// シーケンスを再生する。CancellationToken でキャンセルするまで実行。
    /// </summary>
    /// <param name="sequence">再生するシーケンス</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <param name="startFromIndex">再生開始ステップインデックス（ソート後の順序）</param>
    public async Task PlayAsync(Sequence sequence, CancellationToken ct,
        int startFromIndex = 0, bool loop = false)
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

        _logger.LogInformation("シーケンス再生開始: {Name} ({StepCount}ステップ, 開始={Start}, ループ={Loop})",
            sequence.Name, sortedSteps.Count, startFromIndex, loop);

        try
        {
            do
            {
                // ジャンプ対応: 開始ステップの時刻を基準とする
                var baseTimeMs = startFromIndex < sortedSteps.Count
                    ? sortedSteps[startFromIndex].TimeOffsetMs
                    : 0;
                var sw = Stopwatch.StartNew();

                for (int i = startFromIndex; i < sortedSteps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    CurrentStepIndex = i;
                    var step = sortedSteps[i];

                    // 指定時刻まで待機（基準時刻からの相対）
                    var waitMs = (step.TimeOffsetMs - baseTimeMs) - (int)sw.ElapsedMilliseconds;
                    if (waitMs > 0)
                    {
                        await Task.Delay(waitMs, ct);
                    }

                    await ExecuteStepAsync(step, ct);
                }

                // ループ時は先頭に戻る（startFromIndexは初回のみ使用）
                startFromIndex = 0;

            } while (loop && !ct.IsCancellationRequested);

            _logger.LogInformation("シーケンス再生完了: {Name}", sequence.Name);
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
        }
    }

    /// <summary>単一ステップを即時実行する。</summary>
    public async Task ExecuteStepAsync(SequenceStep step, CancellationToken ct)
    {
        var options = step.RetransmitCount.HasValue
            ? SendOptions.Default with { RetransmitCount = step.RetransmitCount }
            : SendOptions.Default;

        switch (step.CommandType)
        {
            case SequenceCommandType.Color:
                var packet = LightProtocol.BuildA2_GlobalColor(step.Field, step.R, step.G, step.B);
                await _transport.EnqueueAsync(packet, options, ct);
                _logger.LogDebug("SEQ: Color ({R},{G},{B}) at {Time}ms", step.R, step.G, step.B, step.TimeOffsetMs);
                break;

            case SequenceCommandType.Off:
                var offPacket = LightProtocol.BuildA2_GlobalColor(step.Field, 0, 0, 0);
                await _transport.EnqueueAsync(offPacket, options, ct);
                _logger.LogDebug("SEQ: Off at {Time}ms", step.TimeOffsetMs);
                break;

            case SequenceCommandType.Effect:
                // EffectEngine.RunAsync 内部で BeginEffect が呼ばれ、
                // 前エフェクトが自動的に中断される
                if (step.EffectType.HasValue)
                {
                    var effectParams = new EffectParams(
                        Type: step.EffectType.Value,
                        Color: new Rgb(step.R, step.G, step.B),
                        Field: step.Field,
                        CycleDuration: step.EffectCycleDurationMs.HasValue
                            ? TimeSpan.FromMilliseconds(step.EffectCycleDurationMs.Value)
                            : null,
                        FadeSteps: step.FadeSteps ?? 20,
                        Continuous: true
                    );
                    // エフェクトをバックグラウンドで実行（次のステップに進む）
                    _ = _effectEngine.RunAsync(effectParams, ct);
                    _logger.LogDebug("SEQ: Effect {Type} started at {Time}ms", step.EffectType, step.TimeOffsetMs);
                }
                break;

            case SequenceCommandType.EffectStop:
                _scheduler.Abort();
                _logger.LogDebug("SEQ: EffectStop at {Time}ms", step.TimeOffsetMs);
                break;
        }
    }
}
