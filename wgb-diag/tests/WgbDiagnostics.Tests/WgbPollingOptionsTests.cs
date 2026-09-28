using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class WgbPollingOptionsTests
{
    [Fact]
    public void Iw9167ProfileAlwaysUsesItsInternalPollingCommand()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.ParserProfile = WgbParserProfiles.Iw9167WgbV1;
        options.WgbCommand = "show something unsafe";

        var polling = WgbPollingOptions.FromDiagnosticsOptions(options, "password", "");

        Assert.Equal(WgbParserProfiles.Iw9167WgbV1PollingCommand, polling.Command);
    }

    [Fact]
    public void ExistingCommandRemainsFallbackForOtherProfiles()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.ParserProfile = WgbParserProfiles.GenericKeyValue;
        options.WgbCommand = "show existing custom output";

        var polling = WgbPollingOptions.FromDiagnosticsOptions(options, "password", "");

        Assert.Equal("show existing custom output", polling.Command);
    }
}
