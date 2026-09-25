using System.Reflection;
using System.Runtime.InteropServices;

namespace WgbDiagnostics.App;

public sealed record ApplicationVersionInfo(
    string ProductName,
    string ProductVersion,
    string InformationalVersion,
    string BuildIdentifier,
    string Runtime,
    string Architecture,
    string ExecutablePath)
{
    public static ApplicationVersionInfo FromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(ApplicationVersionInfo).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "";
        var productVersion = ExtractProductVersion(informationalVersion, assembly);
        var productName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? assembly.GetName().Name
            ?? "WGB Diagnostics";
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = assembly.Location;
        }

        return new ApplicationVersionInfo(
            productName,
            productVersion,
            informationalVersion,
            ExtractBuildIdentifier(informationalVersion),
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            executablePath ?? "");
    }

    public string FormatAboutText()
    {
        return string.Join(
            Environment.NewLine,
            ProductName,
            $"Version: {ProductVersion}",
            $"Informational version: {InformationalVersion}",
            $"Build: {BuildIdentifier}",
            $"Runtime: {Runtime}",
            $"Architecture: {Architecture}",
            $"Path: {ExecutablePath}");
    }

    private static string ExtractProductVersion(string informationalVersion, Assembly assembly)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex > 0
                ? informationalVersion[..plusIndex]
                : informationalVersion;
        }

        return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString()
            ?? "";
    }

    private static string ExtractBuildIdentifier(string informationalVersion)
    {
        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 && plusIndex < informationalVersion.Length - 1
            ? informationalVersion[(plusIndex + 1)..]
            : "-";
    }
}
