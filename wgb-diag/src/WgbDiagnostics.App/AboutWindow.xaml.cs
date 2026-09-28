using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace WgbDiagnostics.App;

public partial class AboutWindow : Window
{
    public const string ProjectUrl = "https://github.com/netadmjoni/remote-loader/tree/main/wgb-diag";

    public AboutWindow(ApplicationVersionInfo versionInfo)
    {
        InitializeComponent();
        ProductNameTextBlock.Text = versionInfo.ProductName;
        ProductVersionTextBlock.Text = $"Version {versionInfo.ProductVersion}";
    }

    private void ProjectHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
