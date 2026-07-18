using System.IO;
using System.Text.Json;
using WgbDiagnostics.Core.Configuration;

namespace WgbDiagnostics.App.Configuration;

public sealed class JsonSettingsFileStore : ISettingsFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string SettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public SettingsLoadResult Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsLoadResult(
                WgbDiagnosticsOptions.CreateDefault(),
                [new ConfigurationValidationError("Settings file", $"Settings file was not found at {SettingsPath}. Defaults are loaded.")]);
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var document = JsonSerializer.Deserialize<SettingsDocument>(json, SerializerOptions);
            return new SettingsLoadResult(
                document?.WgbDiagnostics ?? WgbDiagnosticsOptions.CreateDefault(),
                []);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(
                WgbDiagnosticsOptions.CreateDefault(),
                [new ConfigurationValidationError("Settings file", $"Settings file is not valid JSON: {ex.Message}")]);
        }
    }

    public void Save(WgbDiagnosticsOptions options)
    {
        var document = new SettingsDocument { WgbDiagnostics = options };
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        File.WriteAllText(SettingsPath, json);
    }

    public string ResolveLogDirectory(string logDirectory)
    {
        return Path.IsPathFullyQualified(logDirectory)
            ? logDirectory
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, logDirectory));
    }

    private sealed class SettingsDocument
    {
        public WgbDiagnosticsOptions? WgbDiagnostics { get; init; }
    }
}
