namespace WgbDiagnostics.Core.Configuration;

public static class WgbDiagnosticsOptionsMigration
{
    public const int CurrentSettingsVersion = 1;

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

        if (settingsVersion < CurrentSettingsVersion && !options.EnableEngineeringDebugViews)
        {
            options.IcmpDisplayMode = "EventsOnly";
            options.WgbDisplayMode = "ChangesOnly";
            changed = true;
        }

        return changed;
    }
}
