namespace WgbDiagnostics.Core.Logging;

public sealed class SystemDiagnosticClock : IDiagnosticClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
