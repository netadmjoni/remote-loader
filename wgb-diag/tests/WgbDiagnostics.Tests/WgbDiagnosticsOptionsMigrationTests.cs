using WgbDiagnostics.Core.Configuration;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbDiagnosticsOptionsMigrationTests
{
    [Fact]
    public void ObsoleteDevelopmentPingTargetIsCleared()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingTarget = "8.8.8.8";

        var changed = WgbDiagnosticsOptionsMigration.Apply(options);

        Assert.True(changed);
        Assert.Empty(options.PingTarget);
    }

    [Fact]
    public void ConfiguredMachineSidePingTargetIsPreserved()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingTarget = "10.194.240.10";

        var changed = WgbDiagnosticsOptionsMigration.Apply(options);

        Assert.False(changed);
        Assert.Equal("10.194.240.10", options.PingTarget);
    }

    [Fact]
    public void LegacyNormalDiagnosticsDefaultsMigrateOnceToEventFocusedModes()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.IcmpDisplayMode = "AllPings";
        options.WgbDisplayMode = "AllSamples";

        var changed = WgbDiagnosticsOptionsMigration.Apply(options, settingsVersion: 0);

        Assert.True(changed);
        Assert.Equal("EventsOnly", options.IcmpDisplayMode);
        Assert.Equal("ChangesOnly", options.WgbDisplayMode);

        options.IcmpDisplayMode = "AllPings";
        options.WgbDisplayMode = "AllSamples";
        changed = WgbDiagnosticsOptionsMigration.Apply(
            options,
            WgbDiagnosticsOptionsMigration.CurrentSettingsVersion);

        Assert.False(changed);
        Assert.Equal("AllPings", options.IcmpDisplayMode);
        Assert.Equal("AllSamples", options.WgbDisplayMode);
    }

    [Fact]
    public void LegacyDeveloperDisplayPreferencesArePreserved()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.EnableEngineeringDebugViews = true;
        options.IcmpDisplayMode = "LossWindow";
        options.WgbDisplayMode = "RoamsOnly";

        var changed = WgbDiagnosticsOptionsMigration.Apply(options, settingsVersion: 0);

        Assert.False(changed);
        Assert.Equal("LossWindow", options.IcmpDisplayMode);
        Assert.Equal("RoamsOnly", options.WgbDisplayMode);
    }

    [Fact]
    public void PreviousTenMinuteDefaultMigratesOnceToFiveMinutes()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.GraphVisibleMinutes = 10;

        var changed = WgbDiagnosticsOptionsMigration.Apply(options, settingsVersion: 1);

        Assert.True(changed);
        Assert.Equal(5, options.GraphVisibleMinutes);

        options.GraphVisibleMinutes = 10;
        changed = WgbDiagnosticsOptionsMigration.Apply(
            options,
            WgbDiagnosticsOptionsMigration.CurrentSettingsVersion);

        Assert.False(changed);
        Assert.Equal(10, options.GraphVisibleMinutes);
    }
}
