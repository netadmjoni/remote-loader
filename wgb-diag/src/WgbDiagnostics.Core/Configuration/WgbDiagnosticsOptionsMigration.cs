namespace WgbDiagnostics.Core.Configuration;

public static class WgbDiagnosticsOptionsMigration
{
    public const int CurrentSettingsVersion = 2;

    private const int LiveDiagnosticsDefaultsVersion = 1;
    private const int GraphWindowDefaultsVersion = 2;

    private const string ObsoleteDevelopmentPingTarget = "8.8.8.8";

    public static bool Apply(
        WgbDiagnosticsOptions options,
        int settingsVersion = CurrentSettingsVersion)
    {
        var changed = false;
        if (string.Equals(options.PingTarget, ObsoleteDevelopmentPingTarget, StringComparison.Ordinal))
        {
            options.PingTarget = "";
            changed = true;
        }

        if (settingsVersion < LiveDiagnosticsDefaultsVersion && !options.EnableEngineeringDebugViews)
        {
            options.IcmpDisplayMode = "EventsOnly";
            options.WgbDisplayMode = "ChangesOnly";
            changed = true;
        }

        if (settingsVersion < GraphWindowDefaultsVersion && options.GraphVisibleMinutes == 10)
        {
            options.GraphVisibleMinutes = 5;
            changed = true;
        }

        return changed;
    }
}
