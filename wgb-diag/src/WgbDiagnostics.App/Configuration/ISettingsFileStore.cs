using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.App.Configuration;

public interface ISettingsFileStore
{
    string SettingsPath { get; }

    SettingsLoadResult Load();

    void Save(WgbDiagnosticsOptions options);

    string ResolveLogDirectory(string logDirectory);
}
