using SynchrolightAPI.Protocol;
using SynchrolightAPI.Transport;
using SynchrolightAPI.Wpf.ViewModels;

namespace SynchrolightAPI.Wpf.Services;

public class LoggingTransportDecorator : ITransport
{
    private readonly ITransport _inner;
    private readonly SendLogViewModel _logVm;

    public LoggingTransportDecorator(ITransport inner, SendLogViewModel logVm)
    {
        _inner = inner;
        _logVm = logVm;
    }

    public IReadOnlyList<PortInfo> ListPorts() => _inner.ListPorts();

    public Task ConnectAsync(IEnumerable<string> portNames, CancellationToken ct = default)
        => _inner.ConnectAsync(portNames, ct);

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public async Task EnqueueAsync(byte[] packet, CancellationToken ct = default)
    {
        _logVm.AddEntry("TX", LightProtocol.ToHex(packet));
        await _inner.EnqueueAsync(packet, ct);
    }

    public async Task EnqueueAsync(Packet32 packet, CancellationToken ct = default)
    {
        _logVm.AddEntry("TX", packet.ToHex());
        await _inner.EnqueueAsync(packet, ct);
    }

    public async Task EnqueueAsync(byte[] packet, SendOptions options, CancellationToken ct = default)
    {
        var zonePart = options.TargetZoneId != null ? $" zone={options.TargetZoneId}" : "";
        var priPart = options.HighPriority ? " [HIGH]" : "";
        _logVm.AddEntry("TX", $"{priPart}{zonePart} {LightProtocol.ToHex(packet)}");
        await _inner.EnqueueAsync(packet, options, ct);
    }

    public async Task EnqueueAsync(Packet32 packet, SendOptions options, CancellationToken ct = default)
    {
        var zonePart = options.TargetZoneId != null ? $" zone={options.TargetZoneId}" : "";
        var priPart = options.HighPriority ? " [HIGH]" : "";
        _logVm.AddEntry("TX", $"{priPart}{zonePart} {packet.ToHex()}");
        await _inner.EnqueueAsync(packet, options, ct);
    }

    public TransportStatus GetStatus() => _inner.GetStatus();
}
