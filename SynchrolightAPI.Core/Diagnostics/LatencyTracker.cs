using System.Collections.Concurrent;

namespace SynchrolightAPI.Diagnostics;

/// <summary>
/// パケット送信レイテンシの計測・統計
/// </summary>
public class LatencyTracker
{
    private readonly ConcurrentQueue<LatencySample> _samples = new();
    private const int MaxSamples = 10_000;

    /// <summary>レイテンシサンプルを記録</summary>
    public void Record(string portName, double latencyMs)
    {
        _samples.Enqueue(new LatencySample(DateTimeOffset.UtcNow, portName, latencyMs));

        // 上限超過時に古いサンプルを除去
        while (_samples.Count > MaxSamples)
        {
            _samples.TryDequeue(out _);
        }
    }

    /// <summary>統計情報を取得（オプションで時間窓指定）</summary>
    public LatencyStatistics GetStatistics(TimeSpan? window = null)
    {
        var cutoff = window.HasValue ? DateTimeOffset.UtcNow - window.Value : DateTimeOffset.MinValue;
        var values = _samples
            .Where(s => s.Timestamp >= cutoff)
            .Select(s => s.LatencyMs)
            .ToArray();

        if (values.Length == 0)
            return LatencyStatistics.Empty;

        Array.Sort(values);
        return new LatencyStatistics(
            Count: values.Length,
            MinMs: values[0],
            MaxMs: values[^1],
            MeanMs: values.Average(),
            P50Ms: Percentile(values, 0.50),
            P95Ms: Percentile(values, 0.95),
            P99Ms: Percentile(values, 0.99)
        );
    }

    /// <summary>全サンプルをクリア</summary>
    public void Reset() => _samples.Clear();

    private static double Percentile(double[] sorted, double p)
    {
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

public record LatencySample(DateTimeOffset Timestamp, string PortName, double LatencyMs);

public record LatencyStatistics(
    int Count, double MinMs, double MaxMs, double MeanMs,
    double P50Ms, double P95Ms, double P99Ms)
{
    public static readonly LatencyStatistics Empty = new(0, 0, 0, 0, 0, 0, 0);
}
