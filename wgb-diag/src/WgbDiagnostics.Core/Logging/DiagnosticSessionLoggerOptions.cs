namespace WgbDiagnostics.Core.Logging;

public sealed record DiagnosticSessionLoggerOptions(
    string LogDirectory,
    string DeviceOrTarget,
    bool RawLoggingEnabled,
    bool DailyRotationEnabled,
    int RetentionDays,
    IReadOnlyList<string> SensitiveValues);
