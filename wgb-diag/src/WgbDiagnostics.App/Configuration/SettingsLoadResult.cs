using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.App.Configuration;

public sealed record SettingsLoadResult(
    WgbDiagnosticsOptions Options,
    IReadOnlyList<ConfigurationValidationError> Errors);
