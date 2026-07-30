namespace IpLeash.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>A single timestamped line in the activity log.</summary>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Level">Severity, used by the view to colour the row.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
