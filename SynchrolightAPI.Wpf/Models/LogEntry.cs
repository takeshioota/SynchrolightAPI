namespace SynchrolightAPI.Wpf.Models;

public record LogEntry(
    DateTime Timestamp,
    string Direction,
    string Hex,
    string? OperationId = null,
    string? TargetPort = null,
    double? LatencyMs = null)
{
    public string Display
    {
        get
        {
            var prefix = $"{Timestamp:HH:mm:ss} [{Direction}]";
            var opPart = OperationId != null ? $" [{OperationId}]" : "";
            var portPart = TargetPort != null ? $" →{TargetPort}" : "";
            return $"{prefix}{opPart}{portPart} {Hex}";
        }
    }
}
