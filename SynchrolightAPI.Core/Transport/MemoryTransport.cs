using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SynchrolightAPI.Protocol;

namespace SynchrolightAPI.Transport;

/// <summary>
/// 実機不要のメモリトランスポート。
/// SerialPort の代わりにログ出力で送信を模擬する。
/// MultiPortTransport と同じ 2チャネル優先度キュー構造を持つ。
/// </summary>
public class MemoryTransport : ITransport, IDisposable
{
    private readonly ILogger<MemoryTransport> _logger;
    private readonly Channel<SendEnvelope> _highPriorityChannel;
    private readonly Channel<SendEnvelope> _normalChannel;
    private readonly List<VirtualPort> _ports = [];
    private readonly HashSet<string> _expectedPortNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ZoneRouter? _zoneRouter;
    private readonly object _lock = new();
    private string? _lastError;
    private bool _disposed;
    private int _totalSent;

    /// <summary>高優先キューのReader</summary>
    internal ChannelReader<SendEnvelope> HighPriorityReader => _highPriorityChannel.Reader;

    /// <summary>通常キューのReader</summary>
    internal ChannelReader<SendEnvelope> NormalReader => _normalChannel.Reader;

    /// <summary>仮想ポート一覧（TxWorkerService互換用）</summary>
    internal IReadOnlyList<VirtualPort> ConnectedVirtualPorts
    {
        get
        {
            lock (_lock) { return _ports.Where(p => p.IsOpen).ToList().AsReadOnly(); }
        }
    }

    /// <summary>接続を期待するポート名一覧</summary>
    internal IReadOnlySet<string> ExpectedPortNames
    {
        get
        {
            lock (_lock) { return _expectedPortNames.ToHashSet(StringComparer.OrdinalIgnoreCase); }
        }
    }

    /// <summary>総送信パケット数</summary>
    public int TotalSent => _totalSent;

    /// <summary>最終エラーを記録</summary>
    internal void SetLastError(string error) => _lastError = error;

    public MemoryTransport(
        ILogger<MemoryTransport> logger,
        int queueCapacity = 256,
        ZoneRouter? zoneRouter = null)
    {
        _logger = logger;
        _zoneRouter = zoneRouter;

        _highPriorityChannel = Channel.CreateBounded<SendEnvelope>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _normalChannel = Channel.CreateBounded<SendEnvelope>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public IReadOnlyList<PortInfo> ListPorts()
    {
        lock (_lock)
        {
            return _ports.Select(p => new PortInfo(p.Name, p.IsOpen)).ToList().AsReadOnly();
        }
    }

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
    {
        lock (_lock)
        {
            foreach (var name in portNames)
            {
                ct.ThrowIfCancellationRequested();
                _expectedPortNames.Add(name);
                _ports.Add(new VirtualPort(name));
                _logger.LogInformation("[MOCK] 仮想ポート {PortName} を開きました", name);
            }
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        lock (_lock)
        {
            foreach (var p in _ports)
            {
                p.Close();
                _logger.LogInformation("[MOCK] 仮想ポート {PortName} を閉じました", p.Name);
            }
            _ports.Clear();
            _expectedPortNames.Clear();
        }
        return Task.CompletedTask;
    }

    // --- EnqueueAsync: 後方互換 ---

    public Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
        => EnqueueAsync(packet, SendOptions.Default, ct);

    public Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
        => EnqueueAsync(packet.Data, SendOptions.Default, ct);

    // --- EnqueueAsync: SendOptions付き ---

    public async Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default)
    {
        var targetPorts = _zoneRouter?.ResolveTargetPorts(packet, options);

        var envelope = new SendEnvelope(
            Packet: packet,
            Options: options,
            TargetPortNames: targetPorts,
            EnqueuedAtTicks: Stopwatch.GetTimestamp()
        );

        var channel = options.HighPriority ? _highPriorityChannel : _normalChannel;
        await channel.Writer.WriteAsync(envelope, ct);
    }

    public Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default)
        => EnqueueAsync(packet.Data, options, ct);

    /// <summary>OperationId付きエンキュー</summary>
    internal async Task EnqueueWithContextAsync(
        byte[] packet, SendOptions options, string? operationId, CancellationToken ct = default)
    {
        var targetPorts = _zoneRouter?.ResolveTargetPorts(packet, options);

        var envelope = new SendEnvelope(
            Packet: packet,
            Options: options,
            TargetPortNames: targetPorts,
            EnqueuedAtTicks: Stopwatch.GetTimestamp(),
            OperationId: operationId
        );

        var channel = options.HighPriority ? _highPriorityChannel : _normalChannel;
        await channel.Writer.WriteAsync(envelope, ct);

        _logger.LogDebug("[MOCK] Enqueue [{OpId}] priority={Priority} queue={Queue}",
            operationId ?? "-", options.HighPriority ? "HIGH" : "NORMAL",
            channel.Reader.Count);
    }

    /// <summary>仮想ポートへの送信をシミュレート（MemoryTxWorkerが呼び出す）</summary>
    internal void SimulateSend(SendEnvelope envelope, VirtualPort port)
    {
        Interlocked.Increment(ref _totalSent);
        var latencyMs = envelope.GetElapsedMs();
        var hex = LightProtocol.ToHex(envelope.Packet);
        var priority = envelope.Options.HighPriority ? "[HIGH] " : "";

        _logger.LogInformation("[MOCK TX] {Priority}{Port} [{OpId}] ({Latency:F1}ms) {Hex}",
            priority, port.Name, envelope.OperationId ?? "-", latencyMs, hex);
    }

    public TransportStatus GetStatus()
    {
        lock (_lock)
        {
            return new TransportStatus(
                QueueLength: _normalChannel.Reader.Count,
                HighPriorityQueueLength: _highPriorityChannel.Reader.Count,
                ConnectedPorts: _ports.Count(p => p.IsOpen),
                DisconnectedPorts: Math.Max(0, _expectedPortNames.Count - _ports.Count(p => p.IsOpen)),
                LastError: _lastError
            );
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _highPriorityChannel.Writer.TryComplete();
        _normalChannel.Writer.TryComplete();
        DisconnectAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

/// <summary>仮想COMポート</summary>
public class VirtualPort
{
    public string Name { get; }
    public bool IsOpen { get; private set; } = true;

    public VirtualPort(string name) => Name = name;
    public void Close() => IsOpen = false;
}
