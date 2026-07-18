namespace WgbDiagnostics.Core.Logging;

public sealed record DiagnosticSessionInfo(
    string SessionDirectory,
    DateTimeOffset StartedAt);
