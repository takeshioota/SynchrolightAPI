namespace SynchrolightAPI.Wpf.Models;

public record LogEntry(DateTime Timestamp, string Direction, string Hex)
{
    public string Display => $"{Timestamp:HH:mm:ss} [{Direction}] {Hex}";
}
