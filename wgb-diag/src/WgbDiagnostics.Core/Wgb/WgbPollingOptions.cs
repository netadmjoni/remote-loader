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
