using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;

namespace SynchrolightAPI.Api.Services;

/// <summary>
/// API側の ITransport デコレータ。
/// 全 EnqueueAsync 呼び出しを SendLogStore に記録してからインナーへ委譲する。
/// </summary>
public class ApiLoggingTransportDecorator : ITransport
{
    private readonly ITransport _inner;
    private readonly SendLogStore _logStore;

    public ApiLoggingTransportDecorator(ITransport inner, SendLogStore logStore)
    {
        _inner = inner;
        _logStore = logStore;
    }

    /// <summary>デコレータ内部の実トランスポートを取得する（DI用）</summary>
    public ITransport Inner => _inner;

    public IReadOnlyList<PortInfo> ListPorts() => _inner.ListPorts();

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
        => _inner.ConnectAsync(portNames, ct);

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public async Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
    {
        _logStore.Add("TX", LightProtocol.ToHex(packet));
        await _inner.EnqueueAsync(packet, ct);
    }

    public async Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
    {
        _logStore.Add("TX", packet.ToHex());
        await _inner.EnqueueAsync(packet, ct);
    }

    public async Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default)
    {
        var zonePart = options.TargetZoneId != null ? $" zone={options.TargetZoneId}" : "";
        var priPart = options.HighPriority ? " [HIGH]" : "";
        _logStore.Add("TX", $"{priPart}{zonePart} {LightProtocol.ToHex(packet)}");
        await _inner.EnqueueAsync(packet, options, ct);
    }

    public async Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default)
    {
        var zonePart = options.TargetZoneId != null ? $" zone={options.TargetZoneId}" : "";
        var priPart = options.HighPriority ? " [HIGH]" : "";
        _logStore.Add("TX", $"{priPart}{zonePart} {packet.ToHex()}");
        await _inner.EnqueueAsync(packet, options, ct);
    }

    public void FlushQueue() => _inner.FlushQueue();

    public TransportStatus GetStatus() => _inner.GetStatus();
}
