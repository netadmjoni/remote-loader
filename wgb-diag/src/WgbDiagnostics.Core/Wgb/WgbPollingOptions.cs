using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbPollingOptions(
    string Address,
    int Port,
    string Username,
    string Password,
    string Command,
    string ParserProfile,
    int PollIntervalSeconds,
    int CommandTimeoutMilliseconds,
    int ReconnectInitialSeconds = 2,
    int ReconnectMaximumSeconds = 60,
    int StaleAfterSeconds = 5,
    int FailureEventRateLimitSeconds = 30,
    bool UseEnableMode = false,
    string EnableCommand = "enable",
    string EnablePassword = "")
{
    public static WgbPollingOptions FromDiagnosticsOptions(
        WgbDiagnosticsOptions options,
        string password,
        string enablePassword,
        int? commandTimeoutMilliseconds = null)
    {
        return new WgbPollingOptions(
            options.WgbAddress,
            options.SshPort,
            options.SshUsername,
            password,
            options.WgbCommand,
            options.ParserProfile,
            options.WgbPollIntervalSeconds,
            commandTimeoutMilliseconds ?? Math.Max(5000, options.WgbPollIntervalSeconds * 1000),
            options.WgbReconnectInitialSeconds,
            options.WgbReconnectMaximumSeconds,
            options.WgbStaleAfterSeconds,
            FailureEventRateLimitSeconds: 30,
            options.UseEnableMode,
            options.EnableCommand,
            enablePassword);
    }

    public WgbCommandRequest ToCommandRequest()
    {
        return new WgbCommandRequest(
            Address,
            Port,
            Username,
            Password,
            Command,
            CommandTimeoutMilliseconds,
            UseEnableMode,
            EnableCommand,
            EnablePassword);
    }
}
