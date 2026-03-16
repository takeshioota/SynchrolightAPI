namespace SynchrolightAPI.Transport;

/// <summary>
/// トランスポートの送信状態
/// </summary>
public record TransportStatus(int QueueLength, int ConnectedPorts, string? LastError);
