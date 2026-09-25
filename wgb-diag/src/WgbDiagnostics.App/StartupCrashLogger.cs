using System.IO;
using System.Windows.Threading;
using WgbDiagnostics.App.Configuration;

namespace WgbDiagnostics.App;

public static class StartupCrashLogger
{
    public static string LogPath => Path.Combine(AppDataPaths.LogsDirectory, "startup-crash.log");

    public static void Log(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LogsDirectory);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O} [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Startup crash logging must never become the reason startup fails.
        }
    }

    public static void LogDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        Log("DispatcherUnhandledException", e.Exception);
    }

    public static void LogAppDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log("AppDomain.UnhandledException", exception);
        }
        else
        {
            Log("AppDomain.UnhandledException", new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception."));
        }
    }
}
