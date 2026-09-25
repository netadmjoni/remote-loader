namespace WgbDiagnostics.Core.Wgb;

public sealed class WgbCommandException : Exception
{
    public WgbCommandException(string message)
        : base(message)
    {
    }

    public WgbCommandException(
        string message,
        WgbCommandExecutionDiagnostics diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    public WgbCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WgbCommandException(
        string message,
        WgbCommandExecutionDiagnostics diagnostics,
        Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = diagnostics;
    }

    public WgbCommandExecutionDiagnostics? Diagnostics { get; }
}
