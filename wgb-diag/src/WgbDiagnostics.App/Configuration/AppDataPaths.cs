using System.IO;

namespace WgbDiagnostics.App.Configuration;

public static class AppDataPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WgbDiagnostics");

    public static string SettingsPath { get; } = Path.Combine(RootDirectory, "appsettings.json");

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "Logs");

    public static void EnsureRootDirectory()
    {
        Directory.CreateDirectory(RootDirectory);
    }
}
