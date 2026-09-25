using System.IO;
using System.Xml.Linq;
using WgbDiagnostics.App;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class ApplicationVersionInfoTests
{
    [Fact]
    public void GuiVersionComesFromAssemblyMetadataAndIsNotEmpty()
    {
        var version = ApplicationVersionInfo.FromAssembly(typeof(MainWindow).Assembly);

        Assert.NotEmpty(version.ProductName);
        Assert.NotEmpty(version.ProductVersion);
        Assert.NotEmpty(version.InformationalVersion);
        Assert.NotEmpty(version.Runtime);
        Assert.NotEmpty(version.Architecture);
    }

    [Fact]
    public void GuiVersionMatchesSharedReleaseVersion()
    {
        var root = FindRepositoryRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var expectedVersion = props.Root?
            .Elements("PropertyGroup")
            .Elements("VersionPrefix")
            .FirstOrDefault()
            ?.Value;

        var version = ApplicationVersionInfo.FromAssembly(typeof(MainWindow).Assembly);
        var wixProject = File.ReadAllText(Path.Combine(root, "installer", "WgbDiagnostics.Installer", "WgbDiagnostics.Installer.wixproj"));
        var buildScript = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));

        Assert.False(string.IsNullOrWhiteSpace(expectedVersion));
        Assert.Equal(expectedVersion, version.ProductVersion);
        Assert.Contains("<ProductVersion Condition=\"'$(ProductVersion)' == ''\">$(VersionPrefix)</ProductVersion>", wixProject);
        Assert.Contains("<SetProperty Id=\"REINSTALLMODE\" Value=\"amus\" Before=\"CostFinalize\" Sequence=\"execute\" />", File.ReadAllText(Path.Combine(root, "installer", "WgbDiagnostics.Installer", "Product.wxs")));
        Assert.Contains("VersionPrefix", buildScript);
    }

    [Fact]
    public void AboutTextContainsRequiredVersionMetadata()
    {
        var version = ApplicationVersionInfo.FromAssembly(typeof(MainWindow).Assembly);
        var aboutText = version.FormatAboutText();

        Assert.Contains(version.ProductName, aboutText);
        Assert.Contains("Version:", aboutText);
        Assert.Contains("Informational version:", aboutText);
        Assert.Contains("Build:", aboutText);
        Assert.Contains("Runtime:", aboutText);
        Assert.Contains("Architecture:", aboutText);
        Assert.Contains("Path:", aboutText);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "WgbDiagnostics.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate wgb-diag repository root.");
    }
}
