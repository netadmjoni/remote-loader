using Xunit;
using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.Tests;

public sealed class WgbDiagnosticsOptionsValidatorTests
{
    private readonly WgbDiagnosticsOptionsValidator _validator = new();

    [Fact]
    public void DefaultOptionsAreValid()
    {
        var errors = _validator.Validate(WgbDiagnosticsOptions.CreateDefault());

        Assert.Empty(errors);
    }

    [Fact]
    public void DefaultOptionsUseRequestedTimingAndCommandValues()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();

        Assert.Equal(1, options.WgbPollIntervalSeconds);
        Assert.Equal("show wgb dot11 associations", options.WgbCommand);
        Assert.Equal(100, options.PingIntervalMilliseconds);
        Assert.Equal(1000, options.PingTimeoutMilliseconds);
        Assert.Equal(600, options.LossThresholdMilliseconds);
    }

    [Fact]
    public void MissingRequiredTextValuesReturnErrors()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.ApplicationName = "";
        options.WgbAddress = " ";
        options.SshUsername = "";
        options.WgbCommand = "";
        options.ParserProfile = "";
        options.PingTarget = "";
        options.LogDirectory = "";

        var fields = _validator.Validate(options).Select(error => error.Field).ToArray();

        Assert.Contains("Application name", fields);
        Assert.Contains("WGB address", fields);
        Assert.Contains("SSH username", fields);
        Assert.Contains("WGB command", fields);
        Assert.Contains("Parser profile", fields);
        Assert.Contains("Ping target", fields);
        Assert.Contains("Log directory", fields);
    }

    [Fact]
    public void InvalidNumericValuesReturnRangeErrors()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.SshPort = 70000;
        options.WgbPollIntervalSeconds = 0;
        options.PingIntervalMilliseconds = 9;
        options.PingTimeoutMilliseconds = 0;
        options.LossThresholdMilliseconds = 8;
        options.RetentionDays = 0;
        options.GraphVisibleMinutes = 0;
        options.TftpTimeoutSeconds = 0;
        options.MaximumReceivedFileSizeBytes = 0;

        var fields = _validator.Validate(options).Select(error => error.Field).ToArray();

        Assert.Contains("SSH port", fields);
        Assert.Contains("WGB poll interval", fields);
        Assert.Contains("Ping interval", fields);
        Assert.Contains("Ping timeout", fields);
        Assert.Contains("Loss threshold", fields);
        Assert.Contains("Retention days", fields);
        Assert.Contains("Graph visible minutes", fields);
        Assert.Contains("TFTP timeout", fields);
        Assert.Contains("Maximum received file size", fields);
    }

    [Fact]
    public void PingIntervalMustNotExceedSixtySeconds()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingIntervalMilliseconds = 60001;
        options.LossThresholdMilliseconds = 60001;

        var error = Assert.Single(_validator.Validate(options));
        Assert.Equal("Ping interval", error.Field);
    }

    [Fact]
    public void LossThresholdMustBeAtLeastPingInterval()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingIntervalMilliseconds = 500;
        options.LossThresholdMilliseconds = 499;

        var error = Assert.Single(_validator.Validate(options));
        Assert.Equal("Loss threshold", error.Field);
    }

    [Fact]
    public void NullOptionsReturnConfigurationError()
    {
        var errors = _validator.Validate(null);

        var error = Assert.Single(errors);
        Assert.Equal("Configuration", error.Field);
    }
}
