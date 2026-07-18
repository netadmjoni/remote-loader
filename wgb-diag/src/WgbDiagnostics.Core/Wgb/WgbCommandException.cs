namespace WgbDiagnostics.Core.Wgb;

public sealed class WgbCommandException : Exception
{
    public WgbCommandException(string message)
        : base(message)
    {
    }

    public WgbCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
