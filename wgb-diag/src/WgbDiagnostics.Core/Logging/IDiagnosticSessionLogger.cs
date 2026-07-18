using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Logging;

public interface IDiagnosticSessionLogger
{
    DiagnosticSessionInfo? CurrentSession { get; }

    Task<DiagnosticSessionInfo> StartSessionAsync(
        DiagnosticSessionLoggerOptions options,
        WgbDiagnosticsOptions configSnapshot,
        CancellationToken cancellationToken);

    ValueTask LogPingEventAsync(IcmpMonitorEvent monitorEvent);

    ValueTask LogWgbEventAsync(WgbPollEvent pollEvent);

    Task StopSessionAsync(CancellationToken cancellationToken);
}
