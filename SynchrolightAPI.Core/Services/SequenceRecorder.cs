using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Models;

namespace SynchrolightAPI.Services;

/// <summary>
/// シーケンス記録サービス。
/// API操作をリアルタイムにタイムスタンプ付きで記録し、SequenceStepリストとして返す。
/// </summary>
public class SequenceRecorder
{
    private readonly ILogger<SequenceRecorder> _logger;
    private readonly object _lock = new();
    private Stopwatch? _stopwatch;
    private List<SequenceStep>? _steps;

    public bool IsRecording { get; private set; }

    public SequenceRecorder(ILogger<SequenceRecorder> logger)
    {
        _logger = logger;
    }

    /// <summary>記録を開始する。既に記録中の場合はリセットして再開。</summary>
    public void Start()
    {
        lock (_lock)
        {
            _steps = new List<SequenceStep>();
            _stopwatch = Stopwatch.StartNew();
            IsRecording = true;
            _logger.LogInformation("シーケンス記録開始");
        }
    }

    /// <summary>記録を停止し、記録済みステップリストを返す。</summary>
    public List<SequenceStep> Stop()
    {
        lock (_lock)
        {
            _stopwatch?.Stop();
            IsRecording = false;
            var result = _steps?.ToList() ?? [];
            var count = result.Count;
            var elapsedMs = _stopwatch?.ElapsedMilliseconds ?? 0;
            _steps = null;
            _stopwatch = null;
            _logger.LogInformation("シーケンス記録停止: {Count}ステップ / {Elapsed:F1}秒", count, elapsedMs / 1000.0);
            return result;
        }
    }

    /// <summary>グローバル色変更を記録する。</summary>
    public void RecordColor(byte r, byte g, byte b, byte field = 0x00)
    {
        AddStep(new SequenceStep
        {
            CommandType = SequenceCommandType.Color,
            R = r,
            G = g,
            B = b,
            Field = field,
        });
    }

    /// <summary>消灯を記録する。</summary>
    public void RecordOff(byte field = 0x00)
    {
        AddStep(new SequenceStep
        {
            CommandType = SequenceCommandType.Off,
            Field = field,
        });
    }

    /// <summary>エフェクト開始を記録する。</summary>
    public void RecordEffectStart(EffectType type, byte r, byte g, byte b, byte field,
        int? cycleDurationMs, int? fadeSteps)
    {
        AddStep(new SequenceStep
        {
            CommandType = SequenceCommandType.Effect,
            R = r,
            G = g,
            B = b,
            Field = field,
            EffectType = type,
            EffectCycleDurationMs = cycleDurationMs,
            FadeSteps = fadeSteps,
        });
    }

    /// <summary>エフェクト停止を記録する。</summary>
    public void RecordEffectStop()
    {
        AddStep(new SequenceStep
        {
            CommandType = SequenceCommandType.EffectStop,
        });
    }

    /// <summary>記録状態を取得する。</summary>
    public (bool IsRecording, int StepCount, int ElapsedMs) GetStatus()
    {
        lock (_lock)
        {
            return (IsRecording,
                    _steps?.Count ?? 0,
                    (int)(_stopwatch?.ElapsedMilliseconds ?? 0));
        }
    }

    private void AddStep(SequenceStep step)
    {
        lock (_lock)
        {
            if (!IsRecording) return;
            step.TimeOffsetMs = (int)_stopwatch!.ElapsedMilliseconds;
            _steps!.Add(step);
            _logger.LogDebug("REC: {Type} at {Time}ms", step.CommandType, step.TimeOffsetMs);
        }
    }
}
