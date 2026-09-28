using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using ScottPlot.WPF;
using WgbDiagnostics.App;
using WgbDiagnostics.App.Configuration;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
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
                var dashboardStatus = GetPrivateControl<TextBlock>(window, "DashboardStatusTextBlock");
                Assert.Equal("ICMP NOT CONFIGURED", dashboardStatus.Text);
                var dashboardAdvanced = GetPrivateControl<Expander>(window, "DashboardAdvancedExpander");
                Assert.False(dashboardAdvanced.IsExpanded);
                var selectedRoam = GetPrivateControl<Expander>(window, "SelectedRoamExpander");
                Assert.False(selectedRoam.IsExpanded);
                Assert.True(GetPrivateControl<CheckBox>(window, "FollowLatestRoamCheckBox").IsChecked);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    GetPrivateControl<TabItem>(window, "RawParserTabItem").Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    GetPrivateControl<WpfPlot>(window, "DataRatePlot").Visibility);
            });
    }

    [Fact]
    public void EngineeringAndDataRateViewsLoadOnlyWhenEnabled()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.ShowDataRateGraph = true;
        options.EnableEngineeringDebugViews = true;

        ConstructMainWindowOnSta(
            options,
            assertWindow: window =>
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.Show();
                window.UpdateLayout();

                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    GetPrivateControl<TabItem>(window, "RawParserTabItem").Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    GetPrivateControl<RadioButton>(window, "RawPingViewRadioButton").Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    GetPrivateControl<WpfPlot>(window, "DataRatePlot").Visibility);
                Assert.True(GetPrivateControl<RowDefinition>(window, "DataRateGraphRow").Height.Value > 0);
                Assert.True(GetPrivateControl<WpfPlot>(window, "DataRatePlot").ActualHeight >= 130);
            });
    }

    [Fact]
    public void DashboardShowsSingleOkAndCurrentWgbRate()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingTarget = "10.194.240.10";

        ConstructMainWindowOnSta(
            options,
            assertWindow: window =>
            {
                var timestamp = DateTimeOffset.UtcNow;
                var realtime = GetPrivateControl<DiagnosticsRealtimeModel>(window, "_realtimeModel");
                realtime.Apply(new IcmpMonitorEvent(
                    IcmpMonitorEventKind.PingReply,
                    timestamp,
                    SequenceNumber: 1,
                    RoundTripTime: TimeSpan.FromMilliseconds(17),
                    ConsecutiveLoss: 0,
                    EstimatedLossWindowMilliseconds: 0,
                    Message: null));
                realtime.Apply(new WgbPollEvent(
                    WgbPollEventKind.PollSucceeded,
                    timestamp,
                    new WgbAssociationSnapshot(
                        "AP-221",
                        "aa:bb:cc",
                        "44",
                        "-68",
                        "2",
                        TxRate: "144.4 Mbps",
                        RxRate: "130.0 Mbps",
                        WgbIp: "192.168.1.20",
                        AssociationStatus: "Associated",
                        CandidateApName: null,
                        CandidateBssid: null),
                    ParseResult: null,
                    RawOutput: null,
                    Message: null));

                var render = typeof(MainWindow).GetMethod(
                    "RenderRealtimeGraph",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(render);
                render!.Invoke(window, [true, false]);

                Assert.Equal("OK", GetPrivateControl<TextBlock>(window, "DashboardStatusTextBlock").Text);
                Assert.Equal("", GetPrivateControl<TextBlock>(window, "DashboardStatusSymbolTextBlock").Text);
                Assert.Equal("144.4/130.0 Mbps", GetPrivateControl<TextBlock>(window, "DashboardRateTextBlock").Text);
            });
    }

    [Fact]
    public void FollowLatestRoamSelectsNewRoamsWithoutChangingManualGraphView()
    {
        ConstructMainWindowOnSta(
            WgbDiagnosticsOptions.CreateDefault(),
            assertWindow: window =>
            {
                var realtime = GetPrivateControl<DiagnosticsRealtimeModel>(window, "_realtimeModel");
                var selection = GetPrivateControl<RoamMarkerSelectionModel>(window, "_roamMarkerSelection");
                var viewport = GetPrivateControl<GraphViewportModel>(window, "_graphViewport");
                var follow = GetPrivateControl<CheckBox>(window, "FollowLatestRoamCheckBox");
                var render = typeof(MainWindow).GetMethod(
                    "RenderRealtimeGraph",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(render);

                var manualLimits = new GraphAxisLimits(100, 200);
                viewport.SetManualView(manualLimits);
                var timestamp = DateTimeOffset.UtcNow.AddSeconds(-3);

                realtime.Apply(CreateRoam(timestamp, "AP-1", "AP-2", "1", "2"));
                render!.Invoke(window, [true, false]);
                var firstSelection = selection.SelectedMarkerId;

                realtime.Apply(CreateRoam(timestamp.AddSeconds(1), "AP-2", "AP-3", "2", "3"));
                render.Invoke(window, [true, false]);
                var secondSelection = selection.SelectedMarkerId;

                Assert.True(follow.IsChecked);
                Assert.NotEqual(firstSelection, secondSelection);
                Assert.Equal(GraphViewportState.ManualView, viewport.State);
                Assert.Equal(manualLimits, viewport.LastLimits);

                GetPrivateControl<Button>(window, "PreviousRoamButton").RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));
                Assert.False(follow.IsChecked);
                Assert.Equal(firstSelection, selection.SelectedMarkerId);

                realtime.Apply(CreateRoam(timestamp.AddSeconds(2), "AP-3", "AP-4", "3", "4"));
                render.Invoke(window, [true, false]);

                Assert.Equal(firstSelection, selection.SelectedMarkerId);
                Assert.Equal(GraphViewportState.ManualView, viewport.State);
                Assert.Equal(manualLimits, viewport.LastLimits);
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
    public void DashboardKeepsStatusCompactAndGraphsVisibleAtMinimumWindowSize()
    {
        ConstructMainWindowOnSta(
            WgbDiagnosticsOptions.CreateDefault(),
            assertWindow: window =>
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.Show();
                window.UpdateLayout();

                var status = GetPrivateControl<Border>(window, "DashboardStatusBorder");
                var rttPlot = GetPrivateControl<WpfPlot>(window, "RttPlot");
                var rssiPlot = GetPrivateControl<WpfPlot>(window, "RssiPlot");

                Assert.InRange(status.ActualHeight, 1, 110);
                Assert.True(rttPlot.ActualHeight >= 150);
                Assert.True(rssiPlot.ActualHeight >= 130);
            });
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

    [Fact]
    public void DashboardStartAndStopControlBothMonitoringServices()
    {
        var monitoringServices = new TrackingMonitoringServices();
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.PingTarget = "10.194.240.10";

        ConstructMainWindowOnSta(
            options,
            assertWindow: window =>
            {
                GetPrivateControl<Button>(window, "StartMonitoringButton").RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));

                Assert.True(monitoringServices.IcmpStarted.Wait(TimeSpan.FromSeconds(5)), "ICMP monitoring did not start.");
                Assert.True(monitoringServices.WgbStarted.Wait(TimeSpan.FromSeconds(5)), "WGB polling did not start.");

                GetPrivateControl<Button>(window, "StopMonitoringButton").RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));

                PumpDispatcherUntil(
                    () => monitoringServices.IcmpStopped.IsSet && monitoringServices.WgbStopped.IsSet,
                    TimeSpan.FromSeconds(5));
            },
            icmpMonitor: monitoringServices,
            wgbPollingService: monitoringServices);
    }

    [Fact]
    public void DashboardStartWithoutPingTargetStartsNothingAndShowsError()
    {
        var monitoringServices = new TrackingMonitoringServices();

        ConstructMainWindowOnSta(
            WgbDiagnosticsOptions.CreateDefault(),
            assertWindow: window =>
            {
                GetPrivateControl<Button>(window, "StartMonitoringButton").RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));

                Assert.False(monitoringServices.IcmpStarted.Wait(TimeSpan.FromMilliseconds(250)), "ICMP monitoring started without a target.");
                Assert.False(monitoringServices.WgbStarted.Wait(TimeSpan.FromMilliseconds(250)), "WGB polling started without a ping target.");
                Assert.Equal(
                    "ICMP NOT CONFIGURED",
                    GetPrivateControl<TextBlock>(window, "DashboardStatusTextBlock").Text);
                var errors = GetPrivateControl<ListBox>(window, "ValidationErrorsListBox");
                Assert.Contains(
                    errors.Items.Cast<string>(),
                    error => error.Contains("Ping target not configured", StringComparison.Ordinal));
                Assert.True(GetPrivateControl<Button>(window, "StartMonitoringButton").IsEnabled);
                Assert.False(GetPrivateControl<Button>(window, "StopMonitoringButton").IsEnabled);
            },
            icmpMonitor: monitoringServices,
            wgbPollingService: monitoringServices);
    }

    private static void ConstructMainWindowOnSta(
        WgbDiagnosticsOptions options,
        ISecretProtector? secretProtector = null,
        Action<MainWindow>? assertWindow = null,
        IIcmpMonitor? icmpMonitor = null,
        IWgbPollingService? wgbPollingService = null)
    {
        Exception? exception = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window = new MainWindow(
                    new FakeSettingsFileStore(options),
                    new WgbDiagnosticsOptionsValidator(),
                    icmpMonitor ?? new FakeIcmpMonitor(),
                    new FakeWgbCommandClient(),
                    new WgbAssociationParser(),
                    wgbPollingService ?? new FakeWgbPollingService(),
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

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }

        Assert.True(condition(), "Monitoring services did not stop before the timeout.");
    }

    private static WgbPollEvent CreateRoam(
        DateTimeOffset timestamp,
        string oldAp,
        string newAp,
        string oldRadio,
        string newRadio)
    {
        return new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            timestamp,
            new WgbAssociationSnapshot(
                newAp,
                $"bssid-{newAp}",
                "44",
                "-60",
                newRadio,
                TxRate: "144.4",
                RxRate: "130.0",
                WgbIp: "192.168.1.20",
                AssociationStatus: "Associated",
                CandidateApName: null,
                CandidateBssid: null),
            ParseResult: null,
            RawOutput: null,
            Message: "roam",
            OldParentApName: oldAp,
            NewParentApName: newAp,
            OldParentBssid: $"bssid-{oldAp}",
            NewParentBssid: $"bssid-{newAp}",
            OldChannel: "36",
            NewChannel: "44",
            OldRadioId: oldRadio,
            NewRadioId: newRadio,
            RoamClassification: WgbRoamClassification.DifferentApDifferentChannel);
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

    private sealed class TrackingMonitoringServices : IIcmpMonitor, IWgbPollingService
    {
        public ManualResetEventSlim IcmpStarted { get; } = new();

        public ManualResetEventSlim IcmpStopped { get; } = new();

        public ManualResetEventSlim WgbStarted { get; } = new();

        public ManualResetEventSlim WgbStopped { get; } = new();

        public Task RunAsync(
            IcmpMonitorOptions options,
            Func<IcmpMonitorEvent, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            IcmpStarted.Set();
            return WaitForCancellationAsync(cancellationToken, IcmpStopped);
        }

        public Task RunAsync(
            WgbPollingOptions options,
            Func<WgbPollEvent, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            WgbStarted.Set();
            return WaitForCancellationAsync(cancellationToken, WgbStopped);
        }

        private static async Task WaitForCancellationAsync(
            CancellationToken cancellationToken,
            ManualResetEventSlim stopped)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                stopped.Set();
            }
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
