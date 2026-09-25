namespace WgbDiagnostics.Core.Configuration;

public static class WgbDiagnosticsOptionsMigration
{
    private const string ObsoleteDevelopmentPingTarget = "8.8.8.8";

    public static bool Apply(WgbDiagnosticsOptions options)
    {
        if (!string.Equals(options.PingTarget, ObsoleteDevelopmentPingTarget, StringComparison.Ordinal))
        {
            return false;
        }

        options.PingTarget = "";
        return true;
    }
}
