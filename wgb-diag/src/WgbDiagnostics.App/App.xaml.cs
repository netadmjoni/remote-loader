using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WgbDiagnostics.App.Configuration;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddJsonFile(AppDataPaths.SettingsPath, optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<WgbDiagnosticsOptions>(
                    context.Configuration.GetSection(WgbDiagnosticsOptions.SectionName));
                services.AddSingleton<IConfigurationValidator<WgbDiagnosticsOptions>, WgbDiagnosticsOptionsValidator>();
                services.AddSingleton<ISettingsFileStore, JsonSettingsFileStore>();
                services.AddSingleton<IDiagnosticClock, SystemDiagnosticClock>();
                services.AddSingleton<IDiagnosticSessionLogger, DiagnosticSessionLogger>();
                services.AddSingleton<IIcmpProbe, DotNetPingIcmpProbe>();
                services.AddSingleton<IIcmpMonitor, IcmpMonitor>();
                services.AddSingleton<IWgbCommandClient, SshNetWgbCommandClient>();
                services.AddSingleton<IWgbAssociationParser, WgbAssociationParser>();
                services.AddSingleton<IWgbPollingService, WgbPollingService>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
