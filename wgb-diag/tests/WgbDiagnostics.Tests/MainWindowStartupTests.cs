using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using WgbDiagnostics.App;
using WgbDiagnostics.App.Configuration;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class MainWindowStartupTests
{
    [Fact]
    public void MainWindowConstructsWithCleanConfigAndDefaultCheckedPingEventView()
    {
        ConstructMainWindowOnSta(
            WgbDiagnosticsOptions.CreateDefault(),
            assertWindow: window =>
            {
                var diagnosticsTabControl = GetPrivateControl<TabControl>(window, "DiagnosticsTabControl");
                Assert.Equal(0, diagnosticsTabControl.SelectedIndex);
                var liveTab = Assert.IsType<TabItem>(diagnosticsTabControl.Items[0]);
                Assert.Equal("Live diagnostics", liveTab.Header);
                var version = GetPrivateControl<TextBlock>(window, "VersionTextBlock");
                Assert.Contains("v", version.Text);
            });
    }

    [Fact]
    public void MainWindowConstructsWithExisting014StyleConfig()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.UseEnableMode = true;
        options.EnableCommand = "enable";
        options.GraphVisibleMinutes = 10;
        options.SaveSshPassword = false;
        options.EncryptedPasswordPlaceholder = "";
        options.SaveEnablePassword = false;
        options.EncryptedEnablePasswordPlaceholder = "";

        ConstructMainWindowOnSta(options);
    }

    [Fact]
    public void MainWindowConstructsWithExisting015StyleSavedSecretConfig()
    {
        var protector = new FakeSecretProtector();
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.SaveSshPassword = true;
        options.EncryptedPasswordPlaceholder = protector.Protect("ssh-secret");
        options.SaveEnablePassword = true;
        options.EncryptedEnablePasswordPlaceholder = protector.Protect("enable-secret");
        options.GraphVisibleMinutes = 10;

        ConstructMainWindowOnSta(options, protector);
    }

    [Fact]
    public void MainWindowLoadsSavedLiveDiagnosticsPreferences()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.LiveDiagnosticsLayout = "Vertical";
        options.IcmpDisplayMode = "LossWindow";
        options.WgbDisplayMode = "RoamsOnly";
        options.LiveDiagnosticsSplitterPosition = 0.35;
        options.EventDisplayBufferSize = 5000;

        ConstructMainWindowOnSta(
            options,
            assertWindow: window =>
            {
                var layout = GetPrivateControl<ComboBox>(window, "LiveDiagnosticsLayoutComboBox");
                var mode = GetPrivateControl<ComboBox>(window, "LiveIcmpDisplayModeComboBox");
                var wgbMode = GetPrivateControl<ComboBox>(window, "LiveWgbDisplayModeComboBox");

                Assert.Equal("Vertical", ((ComboBoxItem)layout.SelectedItem).Tag?.ToString());
                Assert.Equal("LossWindow", ((ComboBoxItem)mode.SelectedItem).Tag?.ToString());
                Assert.Equal("RoamsOnly", ((ComboBoxItem)wgbMode.SelectedItem).Tag?.ToString());
            });
    }

    private static void ConstructMainWindowOnSta(
        WgbDiagnosticsOptions options,
        ISecretProtector? secretProtector = null,
        Action<MainWindow>? assertWindow = null)
    {
        Exception? exception = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(
                    new FakeSettingsFileStore(options),
                    new WgbDiagnosticsOptionsValidator(),
                    new FakeIcmpMonitor(),
                    new FakeWgbCommandClient(),
                    new WgbAssociationParser(),
                    new FakeWgbPollingService(),
                    new FakeDiagnosticSessionLogger(),
                    secretProtector ?? new FakeSecretProtector());

                Assert.True(window.IsInitialized);
                assertWindow?.Invoke(window);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(20)), "MainWindow construction did not finish.");
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static T GetPrivateControl<T>(MainWindow window, string name)
        where T : class
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(window));
    }

    private sealed class FakeSettingsFileStore : ISettingsFileStore
    {
        private readonly WgbDiagnosticsOptions _options;

        public FakeSettingsFileStore(WgbDiagnosticsOptions options)
        {
            _options = options;
        }

        public string SettingsPath => @"C:\Users\test\AppData\Local\WgbDiagnostics\appsettings.json";

        public SettingsLoadResult Load()
        {
            return new SettingsLoadResult(_options, []);
        }

        public void Save(WgbDiagnosticsOptions options)
        {
        }

        public string ResolveLogDirectory(string logDirectory)
        {
            return logDirectory;
        }
    }

    private sealed class FakeIcmpMonitor : IIcmpMonitor
    {
        public Task RunAsync(
            IcmpMonitorOptions options,
            Func<IcmpMonitorEvent, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWgbCommandClient : IWgbCommandClient
    {
        public Task<string> ExecuteCommandAsync(
            WgbCommandRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult("");
        }
    }

    private sealed class FakeWgbPollingService : IWgbPollingService
    {
        public Task RunAsync(
            WgbPollingOptions options,
            Func<WgbPollEvent, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDiagnosticSessionLogger : IDiagnosticSessionLogger
    {
        public DiagnosticSessionInfo? CurrentSession => null;

        public Task<DiagnosticSessionInfo> StartSessionAsync(
            DiagnosticSessionLoggerOptions options,
            WgbDiagnosticsOptions configSnapshot,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DiagnosticSessionInfo(options.LogDirectory, DateTimeOffset.UtcNow));
        }

        public ValueTask LogPingEventAsync(IcmpMonitorEvent monitorEvent)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask LogWgbEventAsync(WgbPollEvent pollEvent)
        {
            return ValueTask.CompletedTask;
        }

        public Task StopSessionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext)
        {
            return string.IsNullOrEmpty(plaintext) ? "" : $"protected:{plaintext}";
        }

        public SecretUnprotectResult TryUnprotect(string protectedText)
        {
            return protectedText.StartsWith("protected:", StringComparison.Ordinal)
                ? SecretUnprotectResult.Success(protectedText["protected:".Length..])
                : SecretUnprotectResult.Success("");
        }
    }
}
