using System.Collections.Concurrent;
using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Core.Tests.TestSupport;

/// <summary>
/// テスト用 ITransport 実装（実機・シリアルポート不要）。
/// エンキューされたパケットと SendOptions を記録し、FlushQueue 呼び出し回数を数える。
/// EffectScheduler / EffectEngine / SequencePlayer の挙動をハードウェア無しで検証するために使う。
/// スレッドセーフ（並行ストレステストで複数タスクから同時エンキューされる）。
/// </summary>
public sealed class RecordingTransport : ITransport
{
    public readonly record struct Entry(byte[] Packet, SendOptions Options);

    private readonly ConcurrentQueue<Entry> _entries = new();
    private int _flushCount;

    /// <summary>これまでにエンキューされた全パケット（記録順）。</summary>
    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    /// <summary>FlushQueue が呼ばれた回数。</summary>
    public int FlushCount => Volatile.Read(ref _flushCount);

    /// <summary>直近にエンキューされたパケット（無ければ null）。</summary>
    public Entry? Last => _entries.TryPeekLast(out var e) ? e : null;

    public IReadOnlyList<PortInfo> ListPorts() => new[] { new PortInfo("COM-TEST", true) };

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default) => Task.CompletedTask;

    public Task DisconnectAsync() => Task.CompletedTask;

    public Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
        => EnqueueAsync(packet, SendOptions.Default, ct);

    public Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
        => EnqueueAsync((byte[])packet, SendOptions.Default, ct);

    public Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _entries.Enqueue(new Entry((byte[])packet.Clone(), options));
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default)
        => EnqueueAsync((byte[])packet, options, ct);

    public void FlushQueue() => Interlocked.Increment(ref _flushCount);

    public TransportStatus GetStatus()
        => new(_entries.Count, 0, 1, 0, null);

    /// <summary>A2 単色パケット (f[0]=0xA2) のうち直近の RGB を返す。無ければ null。</summary>
    public (byte r, byte g, byte b)? LastA2Color()
    {
        foreach (var e in _entries.Reverse())
            if (e.Packet.Length >= 5 && e.Packet[0] == 0xA2)
                return (e.Packet[2], e.Packet[3], e.Packet[4]);
        return null;
    }
}

internal static class ConcurrentQueueExtensions
{
    public static bool TryPeekLast<T>(this ConcurrentQueue<T> q, out T value)
    {
        value = default!;
        bool any = false;
        foreach (var item in q) { value = item; any = true; }
        return any;
    }
}
