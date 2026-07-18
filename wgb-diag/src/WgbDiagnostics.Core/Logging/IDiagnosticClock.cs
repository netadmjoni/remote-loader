namespace WgbDiagnostics.Core.Logging;

public interface IDiagnosticClock
{
    DateTimeOffset UtcNow { get; }
}
