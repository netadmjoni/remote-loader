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
}
