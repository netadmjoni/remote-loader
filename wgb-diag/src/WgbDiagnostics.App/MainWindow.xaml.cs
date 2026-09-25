using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.WPF;
using WgbDiagnostics.App.Configuration;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.App;

public partial class MainWindow : Window
{
    private const int MaxDiagnosticItems = 500;
    private const int MaxRenderedGraphMarkers = 80;
    private const double GraphClickDragTolerancePixels = 4;

    private readonly ISettingsFileStore _settingsFileStore;
    private readonly IConfigurationValidator<WgbDiagnosticsOptions> _validator;
    private readonly IIcmpMonitor _icmpMonitor;
    private readonly IWgbCommandClient _wgbCommandClient;
    private readonly IWgbAssociationParser _wgbAssociationParser;
    private readonly IWgbPollingService _wgbPollingService;
    private readonly IDiagnosticSessionLogger _sessionLogger;
    private readonly ISecretProtector _secretProtector;
    private readonly DiagnosticsRealtimeModel _realtimeModel = new();
    private readonly GraphViewportModel _graphViewport = new(RealtimeGraphOptions.Default.VisibleWindow);
    private readonly PingEventViewModel _pingEventView = new();
    private readonly LiveDiagnosticsPresentationModel _liveDiagnostics = new();
    private readonly RoamMarkerSelectionModel _roamMarkerSelection = new();
    private readonly ApplicationVersionInfo _versionInfo = ApplicationVersionInfo.FromAssembly(typeof(MainWindow).Assembly);
    private readonly ObservableCollection<LiveDiagnosticsRow> _liveIcmpRows = [];
    private readonly ObservableCollection<LiveDiagnosticsRow> _liveWgbRows = [];
    private readonly LiveDiagnosticsPanelViewport _liveIcmpViewport = new();
    private readonly LiveDiagnosticsPanelViewport _liveWgbViewport = new();
    private readonly DispatcherTimer _graphRefreshTimer;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private CancellationTokenSource? _wgbPollingCancellation;
    private Task? _wgbPollingTask;
    private string? _lastSessionDirectory;
    private volatile bool _graphNeedsRefresh;
    private int _graphTimerTicks;
    private long _totalOk;
    private long _totalLost;
    private PingEventViewMode _pingViewMode = PingEventViewMode.Event;
    private LiveDiagnosticsLayout _liveDiagnosticsLayout = LiveDiagnosticsLayout.Auto;
    private double _liveDiagnosticsSplitterPosition = 0.5;
    private bool _suppressLivePreferencePersistence;
    private bool _liveWgbStaleEventActive;
    private WpfPlot? _panningPlot;
    private Point _panStartPoint;
    private GraphAxisLimits? _panStartLimits;
    private bool _graphDragStarted;
    private DiagnosticsRealtimeSnapshot? _latestRealtimeSnapshot;

    public MainWindow(
        ISettingsFileStore settingsFileStore,
        IConfigurationValidator<WgbDiagnosticsOptions> validator,
        IIcmpMonitor icmpMonitor,
        IWgbCommandClient wgbCommandClient,
        IWgbAssociationParser wgbAssociationParser,
        IWgbPollingService wgbPollingService,
        IDiagnosticSessionLogger sessionLogger,
        ISecretProtector secretProtector)
    {
        _settingsFileStore = settingsFileStore;
        _validator = validator;
        _icmpMonitor = icmpMonitor;
        _wgbCommandClient = wgbCommandClient;
        _wgbAssociationParser = wgbAssociationParser;
        _wgbPollingService = wgbPollingService;
        _sessionLogger = sessionLogger;
        _secretProtector = secretProtector;

        InitializeComponent();
        VersionTextBlock.Text = $"{_versionInfo.ProductName} v{_versionInfo.ProductVersion}";
        InitializeLiveDiagnosticsView();
        AttachPostInitializeEventHandlers();
        UpdatePingViewModeFromControls(clearEvents: false);
        InitializeRttPlot();
        InitializeRssiPlot();
        ConfigureRealtimePlotInteractions();
        _graphRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _graphRefreshTimer.Tick += GraphRefreshTimer_Tick;
        _graphRefreshTimer.Start();
        LoadSettingsFromDisk();
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var options = ReadSettingsFromForm(out var formErrors);
        var errors = formErrors.Concat(_validator.Validate(options)).ToList();

        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        try
        {
            _settingsFileStore.Save(options);
            Title = options.ApplicationName;
            ApplyGraphOptionsFromSettings(options, resetToAutoscroll: _graphViewport.State == GraphViewportState.Autoscroll);
            UpdateIcmpTimingText(options);
            ShowStatus($"Settings saved to {_settingsFileStore.SettingsPath}.");
        }
        catch (IOException ex)
        {
            ShowErrors([new ConfigurationValidationError("Settings file", $"Settings could not be saved: {ex.Message}")]);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowErrors([new ConfigurationValidationError("Settings file", $"Settings could not be saved: {ex.Message}")]);
        }
    }

    private async void StartMonitoringButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoringTask is { IsCompleted: false })
        {
            return;
        }

        var diagnosticsOptions = ReadSettingsFromForm(out var formErrors);
        var errors = formErrors.Concat(_validator.Validate(diagnosticsOptions)).ToList();
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        if (!await EnsureDiagnosticSessionAsync(diagnosticsOptions))
        {
            return;
        }

        PrepareRealtimeView(diagnosticsOptions, reset: !IsAnyProducerRunning());
        UpdateIcmpTimingText(diagnosticsOptions);

        _totalOk = 0;
        _totalLost = 0;
        TotalOkTextBlock.Text = "0";
        TotalLostTextBlock.Text = "0";
        ConsecutiveLossTextBlock.Text = "0";
        CurrentRttTextBlock.Text = "-";
        LongestOutageTextBlock.Text = "0 ms";
        RuntimeTextBlock.Text = "00:00:00";
        ProbeEventsListBox.Items.Clear();
        _pingEventView.Reset();

        var monitorOptions = IcmpMonitorOptions.FromDiagnosticsOptions(diagnosticsOptions);
        _monitoringCancellation = new CancellationTokenSource();
        StartMonitoringButton.IsEnabled = false;
        StopMonitoringButton.IsEnabled = true;
        MonitorStatusTextBlock.Text = "Starting";

        _monitoringTask = Task.Run(
            () => _icmpMonitor.RunAsync(
                monitorOptions,
                HandleMonitorEventAsync,
                _monitoringCancellation.Token));

        _ = _monitoringTask.ContinueWith(
            task => Dispatcher.Invoke(() => CompleteMonitoring(task)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async void StopMonitoringButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync();
    }

    private async void TestSshButton_Click(object sender, RoutedEventArgs e)
    {
        var options = ReadWgbPollingOptionsFromForm();
        if (options is null)
        {
            return;
        }

        TestSshButton.IsEnabled = false;
        WgbStatusTextBlock.Text = "Testing SSH";

        try
        {
            var result = _wgbCommandClient is IWgbCommandDiagnosticsClient diagnosticsClient
                ? await diagnosticsClient.ExecuteCommandWithDiagnosticsAsync(options.ToCommandRequest(), CancellationToken.None)
                : new WgbCommandExecutionResult(
                    await _wgbCommandClient.ExecuteCommandAsync(options.ToCommandRequest(), CancellationToken.None),
                    new WgbCommandExecutionDiagnostics(
                        ConnectionSucceeded: true,
                        EnableAttempted: options.UseEnableMode,
                        EnableSucceeded: !options.UseEnableMode,
                        CommandExecuted: true,
                        FinalPromptConfirmed: true,
                        PromptResyncAttempted: false,
                        PromptResyncSucceeded: false,
                        Warning: null,
                        FailureReason: null,
                        Events: []));
            var rawOutput = result.RawOutput;
            var parseResult = _wgbAssociationParser.Parse(rawOutput, options.ParserProfile);
            RawWgbOutputTextBox.Text = FormatSshTestResult(result.Diagnostics, rawOutput);
            ApplyWgbParseResult(parseResult);
            WgbStatusTextBlock.Text = $"Test SSH succeeded: {FormatSshDiagnostics(result.Diagnostics)}";
        }
        catch (Exception ex)
        {
            var diagnostics = ex is WgbCommandException commandException
                ? commandException.Diagnostics
                : null;
            WgbStatusTextBlock.Text = diagnostics is null
                ? "Test SSH failed"
                : $"Test SSH failed: {FormatSshDiagnostics(diagnostics)}";
            RawWgbOutputTextBox.Text = FormatSshTestFailure(diagnostics, ex.Message);
        }
        finally
        {
            TestSshButton.IsEnabled = true;
        }
    }

    private async void StartWgbPollingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wgbPollingTask is { IsCompleted: false })
        {
            return;
        }

        var diagnosticsOptions = ReadSettingsFromForm(out var formErrors);
        var errors = formErrors.Concat(_validator.Validate(diagnosticsOptions)).ToList();
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        if (!await EnsureDiagnosticSessionAsync(diagnosticsOptions))
        {
            return;
        }

        PrepareRealtimeView(diagnosticsOptions, reset: !IsAnyProducerRunning());

        var options = ReadWgbPollingOptionsFromForm();
        if (options is null)
        {
            return;
        }

        _wgbPollingCancellation = new CancellationTokenSource();
        StartWgbPollingButton.IsEnabled = false;
        StopWgbPollingButton.IsEnabled = true;
        TestSshButton.IsEnabled = false;
        WgbStatusTextBlock.Text = "Starting polling";

        _wgbPollingTask = Task.Run(
            () => _wgbPollingService.RunAsync(
                options,
                HandleWgbPollEventAsync,
                _wgbPollingCancellation.Token));

        _ = _wgbPollingTask.ContinueWith(
            task => Dispatcher.Invoke(() => CompleteWgbPolling(task)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async void StopWgbPollingButton_Click(object sender, RoutedEventArgs e)
    {
        await StopWgbPollingAsync();
    }

    private void LoadSampleOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
            Title = "Load WGB sample output"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            RawWgbOutputTextBox.Text = File.ReadAllText(dialog.FileName);
            ParseRawWgbOutputFromTextBox();
        }
        catch (IOException ex)
        {
            WgbStatusTextBlock.Text = $"Sample load failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            WgbStatusTextBlock.Text = $"Sample load failed: {ex.Message}";
        }
    }

    private void ParseSampleOutputButton_Click(object sender, RoutedEventArgs e)
    {
        ParseRawWgbOutputFromTextBox();
    }

    private void ReloadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsFromDisk();
    }

    private void ResetToDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateForm(WgbDiagnosticsOptions.CreateDefault());
        ShowStatus("Default settings loaded into the form.");
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            _versionInfo.FormatAboutText(),
            $"About {_versionInfo.ProductName}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var logDirectory = LogDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            ShowErrors([new ConfigurationValidationError("Log directory", "Log directory is required.")]);
            return;
        }

        try
        {
            var resolvedPath = _settingsFileStore.ResolveLogDirectory(logDirectory);
            Directory.CreateDirectory(resolvedPath);

            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(resolvedPath);

            Process.Start(startInfo);
            ShowStatus($"Opened log folder: {resolvedPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowErrors([new ConfigurationValidationError("Log directory", $"Log folder could not be opened: {ex.Message}")]);
        }
    }

    private void OpenCurrentSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var sessionDirectory = _sessionLogger.CurrentSession?.SessionDirectory ?? _lastSessionDirectory;
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            ShowStatus("No diagnostic session has been created yet.");
            return;
        }

        try
        {
            Directory.CreateDirectory(sessionDirectory);

            var startInfo = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(sessionDirectory);

            Process.Start(startInfo);
            ShowStatus($"Opened current session: {sessionDirectory}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowErrors([new ConfigurationValidationError("Current session", $"Session folder could not be opened: {ex.Message}")]);
        }
    }

    private void ClearGraphButton_Click(object sender, RoutedEventArgs e)
    {
        _realtimeModel.ClearGraph();
        _roamMarkerSelection.Clear();
        _graphNeedsRefresh = true;
        RenderRealtimeGraph(force: true, resetZoom: false);
        ShowStatus("Graph cleared.");
    }

    private void PreviousRoamButton_Click(object sender, RoutedEventArgs e)
    {
        SelectRelativeRoam(previous: true);
    }

    private void NextRoamButton_Click(object sender, RoutedEventArgs e)
    {
        SelectRelativeRoam(previous: false);
    }

    private void ClearSelectedRoamButton_Click(object sender, RoutedEventArgs e)
    {
        _roamMarkerSelection.Clear();
        SelectedRoamExpander.IsExpanded = false;
        RenderRealtimeGraph(force: true, resetZoom: false);
    }

    private void PauseGraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_graphViewport.State == GraphViewportState.Paused)
        {
            _graphViewport.Resume();
            RenderRealtimeGraph(force: true, resetZoom: false);
        }
        else
        {
            _graphViewport.Pause();
            UpdateGraphStatus();
        }

        _graphNeedsRefresh = true;
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        _graphViewport.ResetZoom(ToPlotX(DateTimeOffset.UtcNow));
        RenderRealtimeGraph(force: true, resetZoom: true);
    }

    private void GraphWindowPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.Tag is null
            || !int.TryParse(button.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return;
        }

        GraphVisibleMinutesTextBox.Text = minutes.ToString(CultureInfo.InvariantCulture);
        ApplyGraphOptionsFromVisibleMinutes(minutes, resetToAutoscroll: true);
        RenderRealtimeGraph(force: true, resetZoom: false);
        ShowStatus($"Graph window set to {minutes} minute(s).");
    }

    private void PingViewRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdatePingViewModeFromControls(clearEvents: true);
    }

    private void AttachPostInitializeEventHandlers()
    {
        if (PingEventViewRadioButton is not null)
        {
            PingEventViewRadioButton.Checked += PingViewRadioButton_Checked;
        }

        if (RawPingViewRadioButton is not null)
        {
            RawPingViewRadioButton.Checked += PingViewRadioButton_Checked;
        }
    }

    private void InitializeLiveDiagnosticsView()
    {
        LiveIcmpEventsListBox.ItemsSource = _liveIcmpRows;
        LiveWgbEventsListBox.ItemsSource = _liveWgbRows;
        _suppressLivePreferencePersistence = true;
        try
        {
            SetComboBoxSelectionByTag(LiveDiagnosticsLayoutComboBox, LiveDiagnosticsLayout.Auto.ToString());
            SetComboBoxSelectionByTag(LiveIcmpDisplayModeComboBox, LiveIcmpDisplayMode.AllPings.ToString());
            SetComboBoxSelectionByTag(LiveWgbDisplayModeComboBox, LiveWgbDisplayMode.AllSamples.ToString());
        }
        finally
        {
            _suppressLivePreferencePersistence = false;
        }

        ApplyLiveDiagnosticsLayout();
        UpdateLivePanelIndicators();
    }

    private void LiveDiagnosticsLayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLivePreferencePersistence)
        {
            return;
        }

        _liveDiagnosticsLayout = ParseLiveDiagnosticsLayout(GetComboBoxSelectedTag(LiveDiagnosticsLayoutComboBox));
        ApplyLiveDiagnosticsLayout();
        PersistLiveDiagnosticsPreferences();
    }

    private void LiveIcmpDisplayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLivePreferencePersistence)
        {
            return;
        }

        _liveDiagnostics.SetIcmpDisplayMode(ParseLiveIcmpDisplayMode(GetComboBoxSelectedTag(LiveIcmpDisplayModeComboBox)));
        RefreshLiveIcmpRows(newEventCount: 0, force: true);
        PersistLiveDiagnosticsPreferences();
    }

    private void LiveWgbDisplayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLivePreferencePersistence)
        {
            return;
        }

        _liveDiagnostics.SetWgbDisplayMode(ParseLiveWgbDisplayMode(GetComboBoxSelectedTag(LiveWgbDisplayModeComboBox)));
        RefreshLiveWgbRows(newEventCount: 0, force: true);
        PersistLiveDiagnosticsPreferences();
    }

    private void LiveDiagnosticsRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_liveDiagnosticsLayout == LiveDiagnosticsLayout.Auto)
        {
            ApplyLiveDiagnosticsLayout();
        }
    }

    private void LiveDiagnosticsGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CaptureLiveDiagnosticsSplitterPosition();
        PersistLiveDiagnosticsPreferences();
    }

    private void LiveIcmpPauseButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleLivePanelPause(_liveIcmpViewport, RefreshLiveIcmpRows);
    }

    private void LiveWgbPauseButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleLivePanelPause(_liveWgbViewport, RefreshLiveWgbRows);
    }

    private void LiveIcmpJumpLatestButton_Click(object sender, RoutedEventArgs e)
    {
        _liveIcmpViewport.JumpToLatest();
        RefreshLiveIcmpRows(newEventCount: 0, force: true);
        ScrollLiveListToLatest(LiveIcmpEventsListBox, _liveIcmpRows);
    }

    private void LiveWgbJumpLatestButton_Click(object sender, RoutedEventArgs e)
    {
        _liveWgbViewport.JumpToLatest();
        RefreshLiveWgbRows(newEventCount: 0, force: true);
        ScrollLiveListToLatest(LiveWgbEventsListBox, _liveWgbRows);
    }

    private void LiveIcmpEventsListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HandleLivePanelScroll(e, _liveIcmpViewport);
        UpdateLivePanelIndicators();
    }

    private void LiveWgbEventsListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        HandleLivePanelScroll(e, _liveWgbViewport);
        UpdateLivePanelIndicators();
    }

    private void UpdatePingViewModeFromControls(bool clearEvents)
    {
        _pingViewMode = RawPingViewRadioButton?.IsChecked == true
            ? PingEventViewMode.Raw
            : PingEventViewMode.Event;
        _pingEventView.Reset();
        if (clearEvents && ProbeEventsListBox is not null)
        {
            ProbeEventsListBox.Items.Clear();
        }
    }

    private void ApplyLiveDiagnosticsOptions(WgbDiagnosticsOptions options)
    {
        _suppressLivePreferencePersistence = true;
        try
        {
            _liveDiagnosticsLayout = ParseLiveDiagnosticsLayout(options.LiveDiagnosticsLayout);
            _liveDiagnosticsSplitterPosition = Math.Clamp(options.LiveDiagnosticsSplitterPosition, 0.1, 0.9);
            _liveDiagnostics.ConfigureBufferSize(options.EventDisplayBufferSize);
            _liveDiagnostics.SetIcmpDisplayMode(ParseLiveIcmpDisplayMode(options.IcmpDisplayMode));
            _liveDiagnostics.SetWgbDisplayMode(ParseLiveWgbDisplayMode(options.WgbDisplayMode));
            SetComboBoxSelectionByTag(LiveDiagnosticsLayoutComboBox, _liveDiagnosticsLayout.ToString());
            SetComboBoxSelectionByTag(LiveIcmpDisplayModeComboBox, _liveDiagnostics.IcmpDisplayMode.ToString());
            SetComboBoxSelectionByTag(LiveWgbDisplayModeComboBox, _liveDiagnostics.WgbDisplayMode.ToString());
            LiveDiagnosticsBufferTextBlock.Text = $"Display buffer: {_liveDiagnostics.BufferSize:0} rows";
            ApplyLiveDiagnosticsLayout();
            RefreshLiveIcmpRows(newEventCount: 0, force: true);
            RefreshLiveWgbRows(newEventCount: 0, force: true);
        }
        finally
        {
            _suppressLivePreferencePersistence = false;
        }
    }

    private void ApplyLiveDiagnosticsLayout()
    {
        if (LiveDiagnosticsPanelsGrid is null)
        {
            return;
        }

        var effectiveLayout = GetEffectiveLiveDiagnosticsLayout();
        LiveDiagnosticsPanelsGrid.ColumnDefinitions.Clear();
        LiveDiagnosticsPanelsGrid.RowDefinitions.Clear();

        if (effectiveLayout == LiveDiagnosticsLayout.Vertical)
        {
            LiveDiagnosticsPanelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            LiveDiagnosticsPanelsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_liveDiagnosticsSplitterPosition, GridUnitType.Star) });
            LiveDiagnosticsPanelsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5, GridUnitType.Pixel) });
            LiveDiagnosticsPanelsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - _liveDiagnosticsSplitterPosition, GridUnitType.Star) });
            Grid.SetRow(LiveIcmpPanel, 0);
            Grid.SetColumn(LiveIcmpPanel, 0);
            Grid.SetRow(LiveDiagnosticsGridSplitter, 1);
            Grid.SetColumn(LiveDiagnosticsGridSplitter, 0);
            Grid.SetRow(LiveWgbPanel, 2);
            Grid.SetColumn(LiveWgbPanel, 0);
            LiveDiagnosticsGridSplitter.ResizeDirection = GridResizeDirection.Rows;
            LiveDiagnosticsGridSplitter.Height = 5;
            LiveDiagnosticsGridSplitter.Width = double.NaN;
            LiveDiagnosticsGridSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            LiveDiagnosticsGridSplitter.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            return;
        }

        LiveDiagnosticsPanelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_liveDiagnosticsSplitterPosition, GridUnitType.Star) });
        LiveDiagnosticsPanelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Pixel) });
        LiveDiagnosticsPanelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - _liveDiagnosticsSplitterPosition, GridUnitType.Star) });
        LiveDiagnosticsPanelsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(LiveIcmpPanel, 0);
        Grid.SetColumn(LiveIcmpPanel, 0);
        Grid.SetRow(LiveDiagnosticsGridSplitter, 0);
        Grid.SetColumn(LiveDiagnosticsGridSplitter, 1);
        Grid.SetRow(LiveWgbPanel, 0);
        Grid.SetColumn(LiveWgbPanel, 2);
        LiveDiagnosticsGridSplitter.ResizeDirection = GridResizeDirection.Columns;
        LiveDiagnosticsGridSplitter.Width = 5;
        LiveDiagnosticsGridSplitter.Height = double.NaN;
        LiveDiagnosticsGridSplitter.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        LiveDiagnosticsGridSplitter.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
    }

    private LiveDiagnosticsLayout GetEffectiveLiveDiagnosticsLayout()
    {
        if (_liveDiagnosticsLayout != LiveDiagnosticsLayout.Auto)
        {
            return _liveDiagnosticsLayout;
        }

        return LiveDiagnosticsRoot.ActualWidth > 0 && LiveDiagnosticsRoot.ActualWidth < 900
            ? LiveDiagnosticsLayout.Vertical
            : LiveDiagnosticsLayout.Horizontal;
    }

    private void CaptureLiveDiagnosticsSplitterPosition()
    {
        var effectiveLayout = GetEffectiveLiveDiagnosticsLayout();
        var first = effectiveLayout == LiveDiagnosticsLayout.Vertical
            ? LiveIcmpPanel.ActualHeight
            : LiveIcmpPanel.ActualWidth;
        var second = effectiveLayout == LiveDiagnosticsLayout.Vertical
            ? LiveWgbPanel.ActualHeight
            : LiveWgbPanel.ActualWidth;
        var total = first + second;
        if (total <= 0)
        {
            return;
        }

        _liveDiagnosticsSplitterPosition = Math.Clamp(first / total, 0.1, 0.9);
    }

    private void RefreshLiveIcmpRows(int newEventCount, bool force)
    {
        RefreshLivePanelRows(
            _liveIcmpViewport,
            _liveIcmpRows,
            _liveDiagnostics.Snapshot().IcmpRows,
            LiveIcmpEventsListBox,
            newEventCount,
            force);
    }

    private void RefreshLiveWgbRows(int newEventCount, bool force)
    {
        RefreshLivePanelRows(
            _liveWgbViewport,
            _liveWgbRows,
            _liveDiagnostics.Snapshot().WgbRows,
            LiveWgbEventsListBox,
            newEventCount,
            force);
    }

    private void RefreshLivePanelRows(
        LiveDiagnosticsPanelViewport viewport,
        ObservableCollection<LiveDiagnosticsRow> targetRows,
        IReadOnlyList<LiveDiagnosticsRow> sourceRows,
        ListBox listBox,
        int newEventCount,
        bool force)
    {
        if (viewport.IsPaused && !force)
        {
            viewport.NoteEventsAdded(newEventCount);
            UpdateLivePanelIndicators();
            return;
        }

        var shouldScroll = viewport.IsAutoscrollEnabled;
        viewport.NoteEventsAdded(newEventCount);
        SyncLiveRows(targetRows, sourceRows);

        if (shouldScroll)
        {
            ScrollLiveListToLatest(listBox, targetRows);
        }

        UpdateLivePanelIndicators();
    }

    private static void SyncLiveRows(
        ObservableCollection<LiveDiagnosticsRow> targetRows,
        IReadOnlyList<LiveDiagnosticsRow> sourceRows)
    {
        if (RowsMatch(targetRows, sourceRows))
        {
            return;
        }

        if (sourceRows.Count == targetRows.Count + 1 && RowsMatchPrefix(targetRows, sourceRows, targetRows.Count))
        {
            targetRows.Add(sourceRows[^1]);
            return;
        }

        if (targetRows.Count > 0
            && sourceRows.Count == targetRows.Count
            && RowsMatchTrimmedHead(targetRows, sourceRows))
        {
            targetRows.RemoveAt(0);
            targetRows.Add(sourceRows[^1]);
            return;
        }

        targetRows.Clear();
        foreach (var row in sourceRows)
        {
            targetRows.Add(row);
        }
    }

    private static bool RowsMatch(
        IReadOnlyList<LiveDiagnosticsRow> left,
        IReadOnlyList<LiveDiagnosticsRow> right)
    {
        return left.Count == right.Count && RowsMatchPrefix(left, right, left.Count);
    }

    private static bool RowsMatchPrefix(
        IReadOnlyList<LiveDiagnosticsRow> left,
        IReadOnlyList<LiveDiagnosticsRow> right,
        int count)
    {
        if (right.Count < count)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RowsMatchTrimmedHead(
        IReadOnlyList<LiveDiagnosticsRow> currentRows,
        IReadOnlyList<LiveDiagnosticsRow> nextRows)
    {
        for (var i = 1; i < currentRows.Count; i++)
        {
            if (!Equals(currentRows[i], nextRows[i - 1]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ScrollLiveListToLatest(
        ListBox listBox,
        IReadOnlyList<LiveDiagnosticsRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        listBox.ScrollIntoView(rows[^1]);
    }

    private void ToggleLivePanelPause(
        LiveDiagnosticsPanelViewport viewport,
        Action<int, bool> refreshRows)
    {
        if (viewport.IsPaused)
        {
            viewport.Resume();
            refreshRows(0, true);
            return;
        }

        viewport.Pause();
        UpdateLivePanelIndicators();
    }

    private static void HandleLivePanelScroll(
        ScrollChangedEventArgs e,
        LiveDiagnosticsPanelViewport viewport)
    {
        if (e.OriginalSource is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        if (e.ExtentHeightChange != 0 && viewport.IsAutoscrollEnabled)
        {
            return;
        }

        var atLatest = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 0.5;
        if (atLatest)
        {
            viewport.UserReachedLatest();
        }
        else if (e.VerticalChange < 0 || e.ExtentHeightChange == 0)
        {
            viewport.UserScrolledAwayFromLatest();
        }
    }

    private void UpdateLivePanelIndicators()
    {
        LiveIcmpPanelStatusTextBlock.Text = FormatLivePanelIndicator(_liveIcmpViewport);
        LiveWgbPanelStatusTextBlock.Text = FormatLivePanelIndicator(_liveWgbViewport);
        LiveIcmpPauseButton.Content = _liveIcmpViewport.IsPaused ? "Resume" : "Pause";
        LiveWgbPauseButton.Content = _liveWgbViewport.IsPaused ? "Resume" : "Pause";
    }

    private static string FormatLivePanelIndicator(LiveDiagnosticsPanelViewport viewport)
    {
        if (viewport.IsPaused)
        {
            return viewport.NewEventsWhileViewingOlderData > 0
                ? $"Paused, {viewport.NewEventsWhileViewingOlderData} new events"
                : "Paused";
        }

        if (!viewport.IsAutoscrollEnabled)
        {
            return viewport.NewEventsWhileViewingOlderData > 0
                ? $"Viewing older data, {viewport.NewEventsWhileViewingOlderData} new events"
                : "Viewing older data";
        }

        return "Live";
    }

    private void PersistLiveDiagnosticsPreferences()
    {
        if (_suppressLivePreferencePersistence)
        {
            return;
        }

        try
        {
            CaptureLiveDiagnosticsSplitterPosition();
            var result = _settingsFileStore.Load();
            var options = result.Options;
            options.LiveDiagnosticsLayout = _liveDiagnosticsLayout.ToString();
            options.IcmpDisplayMode = _liveDiagnostics.IcmpDisplayMode.ToString();
            options.WgbDisplayMode = _liveDiagnostics.WgbDisplayMode.ToString();
            options.LiveDiagnosticsSplitterPosition = _liveDiagnosticsSplitterPosition;
            options.EventDisplayBufferSize = _liveDiagnostics.BufferSize;
            _settingsFileStore.Save(options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Live diagnostics preferences could not be saved: {ex.Message}");
        }
    }

    private static void SetComboBoxSelectionByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string GetComboBoxSelectedTag(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? ""
            : "";
    }

    private static LiveDiagnosticsLayout ParseLiveDiagnosticsLayout(string? value)
    {
        return Enum.TryParse<LiveDiagnosticsLayout>(value, ignoreCase: true, out var layout)
            ? layout
            : LiveDiagnosticsLayout.Auto;
    }

    private static LiveIcmpDisplayMode ParseLiveIcmpDisplayMode(string? value)
    {
        return Enum.TryParse<LiveIcmpDisplayMode>(value, ignoreCase: true, out var mode)
            ? mode
            : LiveIcmpDisplayMode.AllPings;
    }

    private static LiveWgbDisplayMode ParseLiveWgbDisplayMode(string? value)
    {
        return Enum.TryParse<LiveWgbDisplayMode>(value, ignoreCase: true, out var mode)
            ? mode
            : LiveWgbDisplayMode.AllSamples;
    }

    private void ForgetSshPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        SshPasswordBox.Password = "";
        SaveSshPasswordCheckBox.IsChecked = false;
        ShowStatus("Saved SSH password cleared from the form. Click Save settings to persist.");
    }

    private void ForgetEnablePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        EnablePasswordBox.Password = "";
        SaveEnablePasswordCheckBox.IsChecked = false;
        ShowStatus("Saved enable password cleared from the form. Click Save settings to persist.");
    }

    private void LoadSettingsFromDisk()
    {
        var result = _settingsFileStore.Load();
        var credentialErrors = PopulateForm(result.Options);

        var validationErrors = _validator.Validate(result.Options);
        var errors = result.Errors.Concat(validationErrors).Concat(credentialErrors).ToList();

        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return;
        }

        ShowStatus($"Settings loaded from {_settingsFileStore.SettingsPath}.");
    }

    protected override async void OnClosed(EventArgs e)
    {
        _graphRefreshTimer.Stop();
        await StopWgbPollingAsync();
        await StopMonitoringAsync();
        base.OnClosed(e);
    }

    private IReadOnlyList<ConfigurationValidationError> PopulateForm(WgbDiagnosticsOptions options)
    {
        var credentialErrors = new List<ConfigurationValidationError>();

        ApplicationNameTextBox.Text = options.ApplicationName;
        WgbAddressTextBox.Text = options.WgbAddress;
        SshPortTextBox.Text = options.SshPort.ToString(CultureInfo.InvariantCulture);
        SshUsernameTextBox.Text = options.SshUsername;
        PopulateProtectedPassword(
            SshPasswordBox,
            SaveSshPasswordCheckBox,
            options.SaveSshPassword,
            options.EncryptedPasswordPlaceholder,
            "SSH password",
            credentialErrors);
        UseEnableModeCheckBox.IsChecked = options.UseEnableMode;
        EnableCommandTextBox.Text = options.EnableCommand;
        PopulateProtectedPassword(
            EnablePasswordBox,
            SaveEnablePasswordCheckBox,
            options.SaveEnablePassword,
            options.EncryptedEnablePasswordPlaceholder,
            "Enable password",
            credentialErrors);
        WgbPollIntervalSecondsTextBox.Text = options.WgbPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WgbReconnectInitialSecondsTextBox.Text = options.WgbReconnectInitialSeconds.ToString(CultureInfo.InvariantCulture);
        WgbReconnectMaximumSecondsTextBox.Text = options.WgbReconnectMaximumSeconds.ToString(CultureInfo.InvariantCulture);
        WgbStaleAfterSecondsTextBox.Text = options.WgbStaleAfterSeconds.ToString(CultureInfo.InvariantCulture);
        WgbCommandTextBox.Text = options.WgbCommand;
        ParserProfileTextBox.Text = options.ParserProfile;
        PingTargetTextBox.Text = options.PingTarget;
        PingIntervalMillisecondsTextBox.Text = options.PingIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
        PingTimeoutMillisecondsTextBox.Text = options.PingTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        LossThresholdMillisecondsTextBox.Text = options.LossThresholdMilliseconds.ToString(CultureInfo.InvariantCulture);
        RawLoggingEnabledCheckBox.IsChecked = options.RawLoggingEnabled;
        LogDirectoryTextBox.Text = options.LogDirectory;
        DailyRotationEnabledCheckBox.IsChecked = options.DailyRotationEnabled;
        RetentionDaysTextBox.Text = options.RetentionDays.ToString(CultureInfo.InvariantCulture);
        GraphVisibleMinutesTextBox.Text = options.GraphVisibleMinutes.ToString(CultureInfo.InvariantCulture);
        WgbLogCollectionEnabledCheckBox.IsChecked = options.WgbLogCollectionEnabled;
        TftpTimeoutSecondsTextBox.Text = options.TftpTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        MaximumReceivedFileSizeBytesTextBox.Text = options.MaximumReceivedFileSizeBytes.ToString(CultureInfo.InvariantCulture);
        Title = options.ApplicationName;
        UpdateIcmpTimingText(options);
        ApplyGraphOptionsFromSettings(options, resetToAutoscroll: _graphViewport.State == GraphViewportState.Autoscroll);
        ApplyLiveDiagnosticsOptions(options);
        return credentialErrors;
    }

    private WgbDiagnosticsOptions ReadSettingsFromForm(out List<ConfigurationValidationError> errors)
    {
        errors = [];
        var saveSshPassword = SaveSshPasswordCheckBox.IsChecked == true;
        var saveEnablePassword = SaveEnablePasswordCheckBox.IsChecked == true;

        CaptureLiveDiagnosticsSplitterPosition();

        return new WgbDiagnosticsOptions
        {
            ApplicationName = ApplicationNameTextBox.Text.Trim(),
            WgbAddress = WgbAddressTextBox.Text.Trim(),
            SshPort = ReadInt(SshPortTextBox, "SSH port", errors),
            SshUsername = SshUsernameTextBox.Text.Trim(),
            EncryptedPasswordPlaceholder = ProtectPasswordForSettings(
                SshPasswordBox.Password,
                saveSshPassword,
                "SSH password",
                errors),
            SaveSshPassword = saveSshPassword,
            UseEnableMode = UseEnableModeCheckBox.IsChecked == true,
            EnableCommand = EnableCommandTextBox.Text.Trim(),
            EncryptedEnablePasswordPlaceholder = ProtectPasswordForSettings(
                EnablePasswordBox.Password,
                saveEnablePassword,
                "Enable password",
                errors),
            SaveEnablePassword = saveEnablePassword,
            WgbPollIntervalSeconds = ReadInt(WgbPollIntervalSecondsTextBox, "WGB poll interval", errors),
            WgbReconnectInitialSeconds = ReadInt(WgbReconnectInitialSecondsTextBox, "WGB reconnect initial", errors),
            WgbReconnectMaximumSeconds = ReadInt(WgbReconnectMaximumSecondsTextBox, "WGB reconnect maximum", errors),
            WgbStaleAfterSeconds = ReadInt(WgbStaleAfterSecondsTextBox, "WGB stale threshold", errors),
            WgbCommand = WgbCommandTextBox.Text.Trim(),
            ParserProfile = ParserProfileTextBox.Text.Trim(),
            PingTarget = PingTargetTextBox.Text.Trim(),
            PingIntervalMilliseconds = ReadInt(PingIntervalMillisecondsTextBox, "Ping interval", errors),
            PingTimeoutMilliseconds = ReadInt(PingTimeoutMillisecondsTextBox, "Ping timeout", errors),
            LossThresholdMilliseconds = ReadInt(LossThresholdMillisecondsTextBox, "Loss threshold", errors),
            RawLoggingEnabled = RawLoggingEnabledCheckBox.IsChecked == true,
            LogDirectory = LogDirectoryTextBox.Text.Trim(),
            DailyRotationEnabled = DailyRotationEnabledCheckBox.IsChecked == true,
            RetentionDays = ReadInt(RetentionDaysTextBox, "Retention days", errors),
            GraphVisibleMinutes = ReadInt(GraphVisibleMinutesTextBox, "Graph visible minutes", errors),
            LiveDiagnosticsLayout = _liveDiagnosticsLayout.ToString(),
            IcmpDisplayMode = _liveDiagnostics.IcmpDisplayMode.ToString(),
            WgbDisplayMode = _liveDiagnostics.WgbDisplayMode.ToString(),
            LiveDiagnosticsSplitterPosition = _liveDiagnosticsSplitterPosition,
            EventDisplayBufferSize = _liveDiagnostics.BufferSize,
            WgbLogCollectionEnabled = WgbLogCollectionEnabledCheckBox.IsChecked == true,
            TftpTimeoutSeconds = ReadInt(TftpTimeoutSecondsTextBox, "TFTP timeout", errors),
            MaximumReceivedFileSizeBytes = ReadLong(MaximumReceivedFileSizeBytesTextBox, "Maximum received file size", errors)
        };
    }

    private void PopulateProtectedPassword(
        PasswordBox passwordBox,
        CheckBox saveCheckBox,
        bool saveEnabled,
        string protectedValue,
        string field,
        ICollection<ConfigurationValidationError> errors)
    {
        passwordBox.Password = "";
        saveCheckBox.IsChecked = false;

        if (!saveEnabled || string.IsNullOrWhiteSpace(protectedValue))
        {
            return;
        }

        var result = _secretProtector.TryUnprotect(protectedValue);
        if (result.Succeeded)
        {
            passwordBox.Password = result.Plaintext;
            saveCheckBox.IsChecked = true;
            return;
        }

        errors.Add(new ConfigurationValidationError(
            field,
            result.ErrorMessage ?? $"{field} could not be decrypted. Enter it again and save settings."));
    }

    private string ProtectPasswordForSettings(
        string plaintext,
        bool saveEnabled,
        string field,
        ICollection<ConfigurationValidationError> errors)
    {
        if (!saveEnabled)
        {
            return "";
        }

        try
        {
            return _secretProtector.Protect(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            errors.Add(new ConfigurationValidationError(field, $"{field} could not be protected with Windows DPAPI: {ex.Message}"));
            return "";
        }
    }

    private void ApplyGraphOptionsFromSettings(WgbDiagnosticsOptions options, bool resetToAutoscroll)
    {
        ApplyGraphOptions(RealtimeGraphOptions.FromDiagnosticsOptions(options), resetToAutoscroll);
    }

    private void ApplyGraphOptionsFromVisibleMinutes(int minutes, bool resetToAutoscroll)
    {
        var defaultOptions = WgbDiagnosticsOptions.CreateDefault();
        defaultOptions.GraphVisibleMinutes = minutes;
        if (int.TryParse(
            WgbStaleAfterSecondsTextBox.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var staleAfterSeconds))
        {
            defaultOptions.WgbStaleAfterSeconds = staleAfterSeconds;
        }

        ApplyGraphOptions(RealtimeGraphOptions.FromDiagnosticsOptions(defaultOptions), resetToAutoscroll);
    }

    private void ApplyGraphOptions(RealtimeGraphOptions options, bool resetToAutoscroll)
    {
        var nowX = ToPlotX(DateTimeOffset.UtcNow);
        _realtimeModel.Configure(options);
        _graphViewport.ConfigureVisibleWindow(options.VisibleWindow, nowX, resetToAutoscroll);
        UpdateGraphStatus();
        _graphNeedsRefresh = true;
    }

    private static int ReadInt(
        TextBox textBox,
        string field,
        ICollection<ConfigurationValidationError> errors)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add(new ConfigurationValidationError(field, $"{field} must be a whole number."));
        return 0;
    }

    private static long ReadLong(
        TextBox textBox,
        string field,
        ICollection<ConfigurationValidationError> errors)
    {
        if (long.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add(new ConfigurationValidationError(field, $"{field} must be a whole number."));
        return 0;
    }

    private void ShowErrors(IReadOnlyList<ConfigurationValidationError> errors)
    {
        ValidationErrorsListBox.ItemsSource = errors.Select(error => $"{error.Field}: {error.Message}");
        ValidationErrorsListBox.Visibility = Visibility.Visible;
        StatusTextBlock.Text = $"{errors.Count} settings issue(s) found.";
        MainTabControl.SelectedIndex = 1;
    }

    private void ShowStatus(string message)
    {
        ValidationErrorsListBox.ItemsSource = null;
        ValidationErrorsListBox.Visibility = Visibility.Collapsed;
        StatusTextBlock.Text = message;
    }

    private WgbPollingOptions? ReadWgbPollingOptionsFromForm()
    {
        var diagnosticsOptions = ReadSettingsFromForm(out var formErrors);
        var errors = formErrors.Concat(_validator.Validate(diagnosticsOptions)).ToList();
        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return null;
        }

        return WgbPollingOptions.FromDiagnosticsOptions(
            diagnosticsOptions,
            SshPasswordBox.Password,
            EnablePasswordBox.Password);
    }

    private ValueTask HandleMonitorEventAsync(IcmpMonitorEvent monitorEvent)
    {
        _realtimeModel.Apply(monitorEvent);
        _graphNeedsRefresh = true;
        _ = _sessionLogger.LogPingEventAsync(monitorEvent);
        var operation = Dispatcher.InvokeAsync(() => ApplyMonitorEvent(monitorEvent));
        return new ValueTask(operation.Task);
    }

    private void ApplyMonitorEvent(IcmpMonitorEvent monitorEvent)
    {
        if (!monitorEvent.AppliedToState
            && monitorEvent.Kind == IcmpMonitorEventKind.PacketLoss)
        {
            _totalLost++;
            TotalLostTextBlock.Text = _totalLost.ToString(CultureInfo.InvariantCulture);
        }

        if (monitorEvent.AppliedToState)
        {
            switch (monitorEvent.Kind)
            {
                case IcmpMonitorEventKind.PingReply:
                    _totalOk++;
                    TotalOkTextBlock.Text = _totalOk.ToString(CultureInfo.InvariantCulture);
                    CurrentRttTextBlock.Text = FormatRoundTripTime(monitorEvent.RoundTripTime);
                    ConsecutiveLossTextBlock.Text = "0";
                    MonitorStatusTextBlock.Text = "OK";
                    break;

                case IcmpMonitorEventKind.LossStarted:
                    _totalLost++;
                    TotalLostTextBlock.Text = _totalLost.ToString(CultureInfo.InvariantCulture);
                    CurrentRttTextBlock.Text = "-";
                    ConsecutiveLossTextBlock.Text = monitorEvent.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture);
                    MonitorStatusTextBlock.Text = "Loss";
                    break;

                case IcmpMonitorEventKind.Loss:
                    _totalLost++;
                    TotalLostTextBlock.Text = _totalLost.ToString(CultureInfo.InvariantCulture);
                    CurrentRttTextBlock.Text = "-";
                    ConsecutiveLossTextBlock.Text = monitorEvent.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture);
                    MonitorStatusTextBlock.Text = "Loss";
                    break;

                case IcmpMonitorEventKind.AlertThresholdReached:
                    ConsecutiveLossTextBlock.Text = monitorEvent.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture);
                    MonitorStatusTextBlock.Text = "Alert";
                    break;

                case IcmpMonitorEventKind.Recovered:
                    ConsecutiveLossTextBlock.Text = "0";
                    CurrentRttTextBlock.Text = FormatRoundTripTime(monitorEvent.RoundTripTime);
                    MonitorStatusTextBlock.Text = "Recovered";
                    break;

                case IcmpMonitorEventKind.Error:
                    ConsecutiveLossTextBlock.Text = monitorEvent.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture);
                    MonitorStatusTextBlock.Text = "Error";
                    break;
            }
        }

        var rows = _pingEventView.Apply(monitorEvent, _pingViewMode);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            ProbeEventsListBox.Items.Insert(0, CreatePingEventListItem(rows[i]));
        }

        while (ProbeEventsListBox.Items.Count > MaxDiagnosticItems)
        {
            ProbeEventsListBox.Items.RemoveAt(ProbeEventsListBox.Items.Count - 1);
        }

        var liveResult = _liveDiagnostics.Apply(monitorEvent);
        if (liveResult.Accepted)
        {
            RefreshLiveIcmpRows(newEventCount: 1, force: false);
        }
    }

    private async Task StopMonitoringAsync()
    {
        var cancellation = _monitoringCancellation;
        var task = _monitoringTask;

        if (cancellation is null || task is null || task.IsCompleted)
        {
            StartMonitoringButton.IsEnabled = true;
            StopMonitoringButton.IsEnabled = false;
            MonitorStatusTextBlock.Text = "Stopped";
            await StopDiagnosticSessionIfIdleAsync();
            return;
        }

        StopMonitoringButton.IsEnabled = false;
        MonitorStatusTextBlock.Text = "Stopping";
        cancellation.Cancel();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_monitoringCancellation, cancellation))
            {
                _monitoringCancellation = null;
                _monitoringTask = null;
            }

            StartMonitoringButton.IsEnabled = true;
            StopMonitoringButton.IsEnabled = false;
            MonitorStatusTextBlock.Text = "Stopped";
            await StopDiagnosticSessionIfIdleAsync();
        }
    }

    private void CompleteMonitoring(Task task)
    {
        if (_monitoringCancellation is not null)
        {
            _monitoringCancellation.Dispose();
            _monitoringCancellation = null;
        }

        _monitoringTask = null;
        StartMonitoringButton.IsEnabled = true;
        StopMonitoringButton.IsEnabled = false;

        if (task.IsFaulted)
        {
            MonitorStatusTextBlock.Text = "Error";
            var message = task.Exception?.GetBaseException().Message ?? "Monitoring stopped unexpectedly.";
            ProbeEventsListBox.Items.Insert(0, $"Monitor error: {message}");
            TrimItems(ProbeEventsListBox, MaxDiagnosticItems);
            _ = StopDiagnosticSessionIfIdleAsync();
            return;
        }

        MonitorStatusTextBlock.Text = "Stopped";
        _ = StopDiagnosticSessionIfIdleAsync();
    }

    private ValueTask HandleWgbPollEventAsync(WgbPollEvent pollEvent)
    {
        _realtimeModel.Apply(pollEvent);
        _graphNeedsRefresh = true;
        _ = _sessionLogger.LogWgbEventAsync(pollEvent);
        var operation = Dispatcher.InvokeAsync(() => ApplyWgbPollEvent(pollEvent));
        return new ValueTask(operation.Task);
    }

    private void ApplyWgbPollEvent(WgbPollEvent pollEvent)
    {
        WgbEventsListBox.Items.Insert(0, FormatWgbPollEvent(pollEvent));
        TrimItems(WgbEventsListBox, MaxDiagnosticItems);
        var liveResult = _liveDiagnostics.Apply(pollEvent);
        if (liveResult.Accepted)
        {
            RefreshLiveWgbRows(newEventCount: 1, force: false);
        }

        switch (pollEvent.Kind)
        {
            case WgbPollEventKind.Connecting:
                WgbStatusTextBlock.Text = "Connecting";
                break;
            case WgbPollEventKind.Connected:
                WgbStatusTextBlock.Text = "Connected";
                break;
            case WgbPollEventKind.ReconnectScheduled:
                WgbStatusTextBlock.Text = $"Reconnecting: {pollEvent.Message}";
                break;
            case WgbPollEventKind.Disconnected:
                WgbStatusTextBlock.Text = "Disconnected";
                break;
            case WgbPollEventKind.PromptDetected:
            case WgbPollEventKind.EnableSucceeded:
            case WgbPollEventKind.CommandStarted:
            case WgbPollEventKind.CommandOutputReceived:
            case WgbPollEventKind.CommandCompleted:
            case WgbPollEventKind.PromptResyncStarted:
            case WgbPollEventKind.PromptResyncSucceeded:
                WgbStatusTextBlock.Text = pollEvent.Message ?? pollEvent.Kind.ToString();
                break;
            case WgbPollEventKind.CommandWarning:
                WgbStatusTextBlock.Text = $"Warning: {pollEvent.Message}";
                break;
            case WgbPollEventKind.PromptResyncFailed:
            case WgbPollEventKind.SessionLost:
                WgbStatusTextBlock.Text = $"{pollEvent.Kind}: {pollEvent.Message}";
                break;
            case WgbPollEventKind.PollSucceeded:
                WgbStatusTextBlock.Text = string.IsNullOrWhiteSpace(pollEvent.Message)
                    ? "Poll succeeded"
                    : $"Poll succeeded: {pollEvent.Message}";
                RawWgbOutputTextBox.Text = pollEvent.RawOutput ?? "";
                if (pollEvent.ParseResult is not null)
                {
                    ApplyParserDiagnostics(pollEvent.ParseResult);
                }

                break;
            case WgbPollEventKind.PollFailed:
                WgbStatusTextBlock.Text = $"Poll failed: {pollEvent.Message}";
                break;
            case WgbPollEventKind.AssociationUpdated:
                if (pollEvent.Association is not null)
                {
                    ApplyWgbAssociation(pollEvent.Association);
                }

                break;
            case WgbPollEventKind.ParentApChanged:
                WgbStatusTextBlock.Text = $"Roam: {pollEvent.RoamClassification} {FormatNullable(pollEvent.OldParentApName)} -> {FormatNullable(pollEvent.NewParentApName)}";
                break;
        }
    }

    private void ParseRawWgbOutputFromTextBox()
    {
        var parseResult = _wgbAssociationParser.Parse(
            RawWgbOutputTextBox.Text,
            ParserProfileTextBox.Text.Trim());
        ApplyWgbParseResult(parseResult);
        WgbStatusTextBlock.Text = $"Sample parsed with {parseResult.ParserProfile}.";
    }

    private void ApplyWgbParseResult(WgbAssociationParseResult parseResult)
    {
        ApplyWgbAssociation(parseResult.Association);
        ApplyParserDiagnostics(parseResult);
    }

    private void ApplyWgbAssociation(WgbAssociationSnapshot association)
    {
        ParentApTextBlock.Text = FormatNullable(association.ParentApName);
        ParentBssidTextBlock.Text = FormatNullable(association.ParentBssid);
        RssiTextBlock.Text = WgbAssociationSample.FormatRssi(association.Rssi);
        ChannelTextBlock.Text = FormatNullable(association.Channel);
        RadioIdTextBlock.Text = FormatNullable(association.RadioId);
        TxRateTextBlock.Text = FormatNullable(association.TxRate);
        RxRateTextBlock.Text = FormatNullable(association.RxRate);
        WgbIpTextBlock.Text = FormatNullable(association.WgbIp);
        CandidateApTextBlock.Text = FormatNullable(association.CandidateApName);
        CandidateBssidTextBlock.Text = FormatNullable(association.CandidateBssid);
        AssociationStatusTextBlock.Text = string.IsNullOrWhiteSpace(association.AssociationStatus)
            ? "Unknown"
            : association.AssociationStatus;
    }

    private void ApplyParserDiagnostics(WgbAssociationParseResult parseResult)
    {
        MatchedFieldsTextBox.Text = string.Join(Environment.NewLine, parseResult.MatchedFields);
        MissingFieldsTextBox.Text = string.Join(Environment.NewLine, parseResult.MissingFields);
        UnclassifiedLinesTextBox.Text = string.Join(
            Environment.NewLine,
            parseResult.Warnings.Select(warning => $"WARNING: {warning}").Concat(parseResult.UnclassifiedLines));
    }

    private async Task StopWgbPollingAsync()
    {
        var cancellation = _wgbPollingCancellation;
        var task = _wgbPollingTask;

        if (cancellation is null || task is null || task.IsCompleted)
        {
            StartWgbPollingButton.IsEnabled = true;
            StopWgbPollingButton.IsEnabled = false;
            TestSshButton.IsEnabled = true;
            await StopDiagnosticSessionIfIdleAsync();
            return;
        }

        StopWgbPollingButton.IsEnabled = false;
        WgbStatusTextBlock.Text = "Stopping polling";
        cancellation.Cancel();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_wgbPollingCancellation, cancellation))
            {
                _wgbPollingCancellation = null;
                _wgbPollingTask = null;
            }

            StartWgbPollingButton.IsEnabled = true;
            StopWgbPollingButton.IsEnabled = false;
            TestSshButton.IsEnabled = true;
            WgbStatusTextBlock.Text = "Polling stopped";
            await StopDiagnosticSessionIfIdleAsync();
        }
    }

    private void CompleteWgbPolling(Task task)
    {
        if (_wgbPollingCancellation is not null)
        {
            _wgbPollingCancellation.Dispose();
            _wgbPollingCancellation = null;
        }

        _wgbPollingTask = null;
        StartWgbPollingButton.IsEnabled = true;
        StopWgbPollingButton.IsEnabled = false;
        TestSshButton.IsEnabled = true;

        if (task.IsFaulted)
        {
            WgbStatusTextBlock.Text = $"Polling error: {task.Exception?.GetBaseException().Message}";
            _ = StopDiagnosticSessionIfIdleAsync();
            return;
        }

        WgbStatusTextBlock.Text = "Polling stopped";
        _ = StopDiagnosticSessionIfIdleAsync();
    }

    private async Task<bool> EnsureDiagnosticSessionAsync(WgbDiagnosticsOptions options)
    {
        try
        {
            var resolvedLogDirectory = _settingsFileStore.ResolveLogDirectory(options.LogDirectory);
            var loggerOptions = new DiagnosticSessionLoggerOptions(
                resolvedLogDirectory,
                GetSessionDeviceOrTarget(options),
                options.RawLoggingEnabled,
                options.DailyRotationEnabled,
                options.RetentionDays,
                GetSensitiveValues(options));
            var session = await _sessionLogger.StartSessionAsync(
                loggerOptions,
                options,
                CancellationToken.None);
            _lastSessionDirectory = session.SessionDirectory;
            CurrentSessionTextBlock.Text = session.SessionDirectory;
            OpenCurrentSessionButton.IsEnabled = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowErrors([new ConfigurationValidationError("Log directory", $"Diagnostic session could not be created: {ex.Message}")]);
            return false;
        }
    }

    private async Task StopDiagnosticSessionIfIdleAsync()
    {
        if (_monitoringTask is { IsCompleted: false } || _wgbPollingTask is { IsCompleted: false })
        {
            return;
        }

        await _sessionLogger.StopSessionAsync(CancellationToken.None);
    }

    private IReadOnlyList<string> GetSensitiveValues(WgbDiagnosticsOptions options)
    {
        return new[]
            {
                SshPasswordBox.Password,
                EnablePasswordBox.Password,
                options.EncryptedPasswordPlaceholder,
                options.EncryptedEnablePasswordPlaceholder,
                options.SshUsername
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetSessionDeviceOrTarget(WgbDiagnosticsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WgbAddress))
        {
            return options.WgbAddress;
        }

        if (!string.IsNullOrWhiteSpace(options.PingTarget))
        {
            return options.PingTarget;
        }

        return "diagnostics";
    }

    private void PrepareRealtimeView(WgbDiagnosticsOptions options, bool reset)
    {
        if (reset)
        {
            _realtimeModel.Reset();
            _liveDiagnostics.Reset();
            _liveIcmpRows.Clear();
            _liveWgbRows.Clear();
            _liveIcmpViewport.JumpToLatest();
            _liveWgbViewport.JumpToLatest();
            _liveWgbStaleEventActive = false;
            UpdateLivePanelIndicators();
        }

        ApplyGraphOptionsFromSettings(options, resetToAutoscroll: true);
        _graphNeedsRefresh = true;
        RenderRealtimeGraph(force: true, resetZoom: false);
    }

    private bool IsAnyProducerRunning()
    {
        return _monitoringTask is { IsCompleted: false }
            || _wgbPollingTask is { IsCompleted: false };
    }

    private void InitializeRttPlot()
    {
        var plot = RttPlot.Plot;
        plot.Clear();
        plot.Title("ICMP RTT");
        plot.XLabel("Local time");
        plot.YLabel("RTT (ms)");
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimitsY(0, 100);
        var now = DateTimeOffset.UtcNow;
        plot.Axes.SetLimitsX(ToPlotX(now - _graphViewport.VisibleWindow), ToPlotX(now));
        RttPlot.Refresh();
    }

    private void InitializeRssiPlot()
    {
        var plot = RssiPlot.Plot;
        plot.Clear();
        plot.Title("Parent RSSI");
        plot.XLabel("Local time");
        plot.YLabel("RSSI (dBm)");
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimitsY(-100, -30);
        var now = DateTimeOffset.UtcNow;
        plot.Axes.SetLimitsX(ToPlotX(now - _graphViewport.VisibleWindow), ToPlotX(now));
        RssiPlot.Refresh();
    }

    private void ConfigureRealtimePlotInteractions()
    {
        ConfigureRealtimePlotInteraction(RttPlot);
        ConfigureRealtimePlotInteraction(RssiPlot);
    }

    private void ConfigureRealtimePlotInteraction(WpfPlot plot)
    {
        plot.UserInputProcessor.Disable();
        UserInputProcessor.ResetState(plot);
        plot.Menu?.Clear();
        plot.ContextMenu = null;
        plot.Focusable = true;
        plot.PreviewMouseWheel += GraphPlot_PreviewMouseWheel;
        plot.MouseLeftButtonDown += GraphPlot_MouseLeftButtonDown;
        plot.MouseMove += GraphPlot_MouseMove;
        plot.MouseLeftButtonUp += GraphPlot_MouseLeftButtonUp;
    }

    private void GraphPlot_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfPlot plot)
        {
            return;
        }

        var current = GetCurrentXLimits(plot);
        var zoomFactor = e.Delta > 0 ? 0.8 : 1.25;
        var center = (current.MinimumX + current.MaximumX) / 2;
        var halfWidth = Math.Clamp(
            current.Width * zoomFactor / 2,
            TimeSpan.FromSeconds(10).TotalDays,
            TimeSpan.FromHours(24).TotalDays);
        var next = new GraphAxisLimits(center - halfWidth, center + halfWidth);
        _graphViewport.SetManualView(next);
        ApplySynchronizedManualXLimits(next);
        UpdateGraphStatus();
        e.Handled = true;
    }

    private void GraphPlot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfPlot plot)
        {
            return;
        }

        _panningPlot = plot;
        _panStartPoint = e.GetPosition(plot);
        _panStartLimits = GetCurrentXLimits(plot);
        _graphDragStarted = false;
        plot.Focus();
        plot.CaptureMouse();
        e.Handled = true;
    }

    private void GraphPlot_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panningPlot is null || _panStartLimits is null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (sender is WpfPlot plot && ReferenceEquals(plot, RssiPlot))
            {
                UpdateRssiPlotHoverTooltip(e);
            }

            return;
        }

        var position = e.GetPosition(_panningPlot);
        var dragDistance = Math.Sqrt(
            Math.Pow(position.X - _panStartPoint.X, 2)
            + Math.Pow(position.Y - _panStartPoint.Y, 2));
        if (!_graphDragStarted && dragDistance < GraphClickDragTolerancePixels)
        {
            if (ReferenceEquals(_panningPlot, RssiPlot))
            {
                UpdateRssiPlotHoverTooltip(e);
            }

            return;
        }

        _graphDragStarted = true;
        var plotWidth = Math.Max(1, _panningPlot.ActualWidth);
        var offsetDays = (position.X - _panStartPoint.X) / plotWidth * _panStartLimits.Width;
        var next = new GraphAxisLimits(
            _panStartLimits.MinimumX - offsetDays,
            _panStartLimits.MaximumX - offsetDays);
        _graphViewport.SetManualView(next);
        ApplySynchronizedManualXLimits(next);
        UpdateGraphStatus();
        e.Handled = true;
    }

    private void GraphPlot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var plot = sender as WpfPlot;
        var wasClick = !_graphDragStarted;
        if (_panningPlot is not null)
        {
            _panningPlot.ReleaseMouseCapture();
        }

        _panningPlot = null;
        _panStartLimits = null;
        _graphDragStarted = false;

        if (wasClick && ReferenceEquals(plot, RssiPlot))
        {
            SelectRoamMarkerFromRssiClick(e);
        }

        e.Handled = true;
    }

    private void SelectRelativeRoam(bool previous)
    {
        var snapshot = _latestRealtimeSnapshot ?? _realtimeModel.Snapshot(DateTimeOffset.UtcNow);
        if (previous)
        {
            _roamMarkerSelection.SelectPrevious(snapshot.RoamEvents);
        }
        else
        {
            _roamMarkerSelection.SelectNext(snapshot.RoamEvents);
        }

        SelectedRoamExpander.IsExpanded = _roamMarkerSelection.HasSelection;
        RenderRealtimeGraph(force: true, resetZoom: false);
    }

    private void SelectRoamMarkerFromRssiClick(MouseButtonEventArgs e)
    {
        var hitTargets = CreateRssiRoamHitTargets();
        _roamMarkerSelection.SelectNearest(hitTargets, GetRssiPlotDataPixelX(e));
        SelectedRoamExpander.IsExpanded = _roamMarkerSelection.HasSelection;
        RenderRealtimeGraph(force: true, resetZoom: false);
    }

    private void UpdateRssiPlotHoverTooltip(MouseEventArgs e)
    {
        var selected = FindNearestRssiRoamTarget(GetRssiPlotDataPixelX(e));
        if (selected is null)
        {
            RssiPlot.ToolTip = null;
            return;
        }

        RssiPlot.ToolTip = SelectedRoamDetailsFormatter.Format(selected).Tooltip;
    }

    private RealtimeRoamEvent? FindNearestRssiRoamTarget(double clickPixelX)
    {
        var snapshot = _latestRealtimeSnapshot;
        if (snapshot is null)
        {
            return null;
        }

        var nearest = CreateRssiRoamHitTargets()
            .Select(target => new
            {
                Target = target,
                Distance = Math.Abs(target.PixelX - clickPixelX)
            })
            .Where(candidate => candidate.Distance <= RoamMarkerSelectionModel.DefaultHitTolerancePixels)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Target.Timestamp)
            .FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }

        return snapshot.RoamEvents.FirstOrDefault(roamEvent =>
            string.Equals(
                RoamMarkerSelectionModel.GetStableMarkerId(roamEvent),
                nearest.Target.MarkerId,
                StringComparison.Ordinal));
    }

    private IReadOnlyList<RoamMarkerHitTarget> CreateRssiRoamHitTargets()
    {
        var snapshot = _latestRealtimeSnapshot;
        if (snapshot is null)
        {
            return [];
        }

        return RoamMarkerSelectionModel.CreateHitTargets(
            snapshot.RoamEvents,
            GetCurrentXLimits(RssiPlot),
            GetRssiPlotDataAreaWidth(),
            ToPlotX);
    }

    private double GetRssiPlotDataPixelX(MouseEventArgs e)
    {
        var mousePixel = RssiPlot.GetPlotPixelPosition(e);
        var dataRect = RssiPlot.Plot.LastRender.DataRect;
        return mousePixel.X - dataRect.Left;
    }

    private double GetRssiPlotDataAreaWidth()
    {
        var width = RssiPlot.Plot.LastRender.DataRect.Width;
        return width > 0
            ? width
            : Math.Max(1, RssiPlot.ActualWidth);
    }

    private void GraphRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _graphTimerTicks++;
        if (_graphNeedsRefresh || IsAnyProducerRunning() || _graphTimerTicks >= 7)
        {
            _graphTimerTicks = 0;
            RenderRealtimeGraph(force: true, resetZoom: false);
        }
    }

    private void RenderRealtimeGraph(bool force, bool resetZoom)
    {
        if (!force && !_graphNeedsRefresh)
        {
            return;
        }

        _graphNeedsRefresh = false;
        var now = DateTimeOffset.UtcNow;
        var nowX = ToPlotX(now);
        if (resetZoom)
        {
            _graphViewport.ResetZoom(nowX);
        }

        var snapshot = _realtimeModel.Snapshot(now);
        _latestRealtimeSnapshot = snapshot;
        EnsureSelectedRoamStillExists(snapshot);
        ApplyRealtimeSnapshotToStatus(snapshot);
        ApplyLiveWgbStaleState(snapshot, now);
        var renderPlan = _graphViewport.CreateRenderPlan(nowX);
        UpdateGraphStatus(renderPlan);
        UpdateSelectedRoamPanel(snapshot);

        if (renderPlan.ShouldRenderPlots)
        {
            RenderRttPlot(snapshot, renderPlan.XLimits);
            RenderRssiPlot(snapshot, renderPlan.XLimits);
        }

        var markerSummary = FormatMarkerSummary(snapshot.RoamEvents, _roamMarkerSelection.SelectedMarkerId);
        GraphMarkerTextBlock.Text = markerSummary;
        DashboardGraphStatusTextBlock.Text = markerSummary;
        RoamTimelineListBox.ItemsSource = snapshot.RoamEvents
            .Select(FormatRoamTimelineEvent)
            .ToArray();
    }

    private void EnsureSelectedRoamStillExists(DiagnosticsRealtimeSnapshot snapshot)
    {
        if (_roamMarkerSelection.HasSelection
            && _roamMarkerSelection.GetSelectedRoam(snapshot.RoamEvents) is null)
        {
            _roamMarkerSelection.Clear();
        }
    }

    private void UpdateSelectedRoamPanel(DiagnosticsRealtimeSnapshot snapshot)
    {
        PreviousRoamButton.IsEnabled = snapshot.RoamEvents.Count > 0;
        NextRoamButton.IsEnabled = snapshot.RoamEvents.Count > 0;

        var roamEvent = _roamMarkerSelection.GetSelectedRoam(snapshot.RoamEvents);
        ClearSelectedRoamButton.IsEnabled = roamEvent is not null;
        if (roamEvent is null)
        {
            SetSelectedRoamPanelDetails(
                observed: "-",
                ap: "-",
                bssid: "-",
                channel: "-",
                radio: "-",
                rssi: "-",
                rate: "-",
                classification: "-");
            return;
        }

        var details = SelectedRoamDetailsFormatter.Format(roamEvent);
        SetSelectedRoamPanelDetails(
            details.Observed,
            details.Ap,
            details.Bssid,
            details.Channel,
            details.Radio,
            details.Rssi,
            details.Rate,
            details.Classification);
    }

    private void SetSelectedRoamPanelDetails(
        string observed,
        string ap,
        string bssid,
        string channel,
        string radio,
        string rssi,
        string rate,
        string classification)
    {
        SelectedRoamObservedTextBlock.Text = observed;
        SelectedRoamApTextBlock.Text = ap;
        SelectedRoamBssidTextBlock.Text = bssid;
        SelectedRoamChannelTextBlock.Text = channel;
        SelectedRoamRadioTextBlock.Text = radio;
        SelectedRoamRssiTextBlock.Text = rssi;
        SelectedRoamRateTextBlock.Text = rate;
        SelectedRoamClassTextBlock.Text = classification;
    }

    private void ApplyLiveWgbStaleState(DiagnosticsRealtimeSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.WgbStatus.IsStale)
        {
            if (_liveWgbStaleEventActive)
            {
                var recoveredResult = _liveDiagnostics.ApplyWgbRecovered(now, snapshot.WgbStatus);
                if (recoveredResult.Accepted)
                {
                    RefreshLiveWgbRows(newEventCount: 1, force: false);
                }
            }

            _liveWgbStaleEventActive = false;
            return;
        }

        if (_liveWgbStaleEventActive)
        {
            return;
        }

        var staleResult = _liveDiagnostics.ApplyWgbStale(now, snapshot.WgbStatus);
        _liveWgbStaleEventActive = true;
        if (staleResult.Accepted)
        {
            RefreshLiveWgbRows(newEventCount: 1, force: false);
        }
    }

    private void RenderRttPlot(
        DiagnosticsRealtimeSnapshot snapshot,
        GraphAxisLimits xLimits)
    {
        var plot = RttPlot.Plot;
        plot.Clear();
        plot.Title("ICMP RTT");
        plot.XLabel("Local time");
        plot.YLabel("RTT (ms)");
        plot.Axes.DateTimeTicksBottom();

        var pointCount = 0;
        var maxRtt = 10d;
        foreach (var segment in snapshot.RttSegments)
        {
            if (segment.Points.Count == 0)
            {
                continue;
            }

            var xs = segment.Points.Select(point => ToPlotX(point.Timestamp)).ToArray();
            var ys = segment.Points.Select(point => point.RoundTripTimeMilliseconds).ToArray();
            var scatter = plot.Add.Scatter(xs, ys, Colors.DodgerBlue);
            scatter.LegendText = pointCount == 0 ? "RTT" : "";
            scatter.LineWidth = 1.5f;
            scatter.MarkerSize = 3;
            scatter.MarkerShape = MarkerShape.FilledCircle;
            pointCount += segment.Points.Count;
            maxRtt = Math.Max(maxRtt, ys.Max());
        }

        foreach (var marker in SelectRenderedMarkers(snapshot.Markers))
        {
            AddGraphMarkerLine(plot, marker);
        }

        plot.Axes.SetLimitsY(0, Math.Max(10, Math.Ceiling(maxRtt * 1.2)));
        ApplyGraphXLimits(plot, xLimits);
        RttPlot.Refresh();
    }

    private void RenderRssiPlot(
        DiagnosticsRealtimeSnapshot snapshot,
        GraphAxisLimits xLimits)
    {
        var plot = RssiPlot.Plot;
        plot.Clear();
        plot.Title("Parent RSSI");
        plot.XLabel("Local time");
        plot.YLabel("RSSI (dBm)");
        plot.Axes.DateTimeTicksBottom();

        if (snapshot.RssiPoints.Count > 0)
        {
            var xs = snapshot.RssiPoints.Select(point => ToPlotX(point.Timestamp)).ToArray();
            var ys = snapshot.RssiPoints.Select(point => point.Rssi).ToArray();
            var scatter = plot.Add.Scatter(xs, ys, Colors.SeaGreen);
            scatter.LegendText = "RSSI";
            scatter.LineWidth = 1.5f;
            scatter.MarkerSize = 3;
            scatter.MarkerShape = MarkerShape.FilledCircle;

            var minimumRssi = Math.Floor(ys.Min() - 5);
            var maximumRssi = Math.Ceiling(ys.Max() + 5);
            if (maximumRssi - minimumRssi < 10)
            {
                var middle = (maximumRssi + minimumRssi) / 2;
                minimumRssi = middle - 5;
                maximumRssi = middle + 5;
            }

            plot.Axes.SetLimitsY(minimumRssi, maximumRssi);
        }
        else
        {
            plot.Axes.SetLimitsY(-100, -30);
        }

        foreach (var marker in SelectRenderedMarkers(snapshot.Markers)
                     .Where(marker => marker.Kind == RealtimeGraphMarkerKind.ParentApChanged))
        {
            AddGraphMarkerLine(plot, marker);
        }

        ApplyGraphXLimits(plot, xLimits);
        RssiPlot.Refresh();
    }

    private static void ApplyGraphXLimits(Plot plot, GraphAxisLimits limits)
    {
        plot.Axes.SetLimitsX(limits.MinimumX, limits.MaximumX);
    }

    private GraphAxisLimits GetCurrentXLimits(WpfPlot plot)
    {
        var limits = plot.Plot.Axes.GetLimits();
        return new GraphAxisLimits(limits.Left, limits.Right);
    }

    private void ApplySynchronizedManualXLimits(GraphAxisLimits limits)
    {
        ApplyGraphXLimits(RttPlot.Plot, limits);
        ApplyGraphXLimits(RssiPlot.Plot, limits);
        RttPlot.Refresh();
        RssiPlot.Refresh();
    }

    private void UpdateGraphStatus(GraphRenderPlan? plan = null)
    {
        var state = plan?.State ?? _graphViewport.State;
        GraphStatusTextBlock.Text = state switch
        {
            GraphViewportState.Autoscroll => "Autoscroll",
            GraphViewportState.ManualView => "Manual view",
            GraphViewportState.Paused => "Paused",
            _ => state.ToString()
        };
        GraphWindowTextBlock.Text = FormatGraphVisibleWindow(plan?.VisibleWindow ?? _graphViewport.VisibleWindow);
        PauseGraphButton.Content = state == GraphViewportState.Paused ? "Resume graph" : "Pause graph";
    }

    private static IReadOnlyList<RealtimeGraphMarker> SelectRenderedMarkers(
        IReadOnlyList<RealtimeGraphMarker> markers)
    {
        return markers.Count <= MaxRenderedGraphMarkers
            ? markers
            : markers.TakeLast(MaxRenderedGraphMarkers).ToArray();
    }

    private void ApplyRealtimeSnapshotToStatus(DiagnosticsRealtimeSnapshot snapshot)
    {
        CurrentRttTextBlock.Text = FormatRoundTripTime(snapshot.PingStatus.CurrentRoundTripTime);
        TotalOkTextBlock.Text = snapshot.PingStatus.TotalOk.ToString(CultureInfo.InvariantCulture);
        TotalLostTextBlock.Text = snapshot.PingStatus.TotalLost.ToString(CultureInfo.InvariantCulture);
        ConsecutiveLossTextBlock.Text = snapshot.PingStatus.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture);
        LongestOutageTextBlock.Text = FormatDuration(snapshot.PingStatus.LongestOutage);
        RuntimeTextBlock.Text = FormatRuntime(snapshot.PingStatus.Runtime);

        ParentApTextBlock.Text = FormatNullable(snapshot.WgbStatus.ParentApName);
        ParentBssidTextBlock.Text = FormatNullable(snapshot.WgbStatus.ParentBssid);
        ChannelTextBlock.Text = FormatNullable(snapshot.WgbStatus.Channel);
        RadioIdTextBlock.Text = FormatNullable(snapshot.WgbStatus.RadioId);
        RssiTextBlock.Text = WgbAssociationSample.FormatRssi(snapshot.WgbStatus.Rssi);
        TxRateTextBlock.Text = FormatNullable(snapshot.WgbStatus.TxRate);
        RxRateTextBlock.Text = FormatNullable(snapshot.WgbStatus.RxRate);
        GraphParentApTextBlock.Text = FormatNullable(snapshot.WgbStatus.ParentApName);
        GraphRssiTextBlock.Text = WgbAssociationSample.FormatRssi(snapshot.WgbStatus.Rssi);
        GraphChannelTextBlock.Text = FormatNullable(snapshot.WgbStatus.Channel);
        GraphRadioIdTextBlock.Text = FormatNullable(snapshot.WgbStatus.RadioId);
        DashboardWgbStatusTextBlock.Text = FormatDashboardWgbStatus(snapshot.WgbStatus);
        LastWgbPollTextBlock.Text = snapshot.WgbStatus.LastSuccessfulPollTimestamp is null
            ? "-"
            : snapshot.WgbStatus.LastSuccessfulPollTimestamp.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        WgbDataAgeTextBlock.Text = snapshot.WgbStatus.LastSuccessfulPollTimestamp is null
            ? "-"
            : $"{FormatDuration(snapshot.WgbStatus.DataAge)}{(snapshot.WgbStatus.IsStale ? " stale" : "")}";
        AssociationStatusTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.WgbStatus.AssociationStatus)
            ? "Unknown"
            : snapshot.WgbStatus.AssociationStatus;
        var compact = WgbCompactStatusModel.FromSnapshot(snapshot);
        CompactWgbStatusTextBlock.Text = compact.SessionStatus;
        CompactWgbLastPollTextBlock.Text = compact.LastSuccessfulPoll;
        CompactWgbDataAgeTextBlock.Text = compact.DataAge;
        CompactWgbLastRoamTextBlock.Text = compact.LastRoam;
        CompactWgbParentTextBlock.Text = compact.ParentApName;
        CompactWgbBssidTextBlock.Text = compact.ParentBssid;
        CompactWgbRadioTextBlock.Text = $"RSSI {compact.Rssi}, ch {compact.Channel}, radio {compact.RadioId}";
        CompactWgbRatesTextBlock.Text = $"Tx {compact.TxRate}, Rx {compact.RxRate}, {compact.AssociationStatus}";

        var latestRoam = snapshot.RoamEvents.LastOrDefault();
        LiveIcmpStateTextBlock.Text = snapshot.PingStatus.Status;
        LiveWgbStateTextBlock.Text = FormatDashboardWgbStatus(snapshot.WgbStatus);
        LiveLatestRoamTextBlock.Text = latestRoam is null
            ? "-"
            : $"{latestRoam.Timestamp.ToLocalTime():HH:mm:ss} {latestRoam.RoamClassification}";
        LiveLatestOutageTextBlock.Text = snapshot.PingStatus.CurrentLossWindow > TimeSpan.Zero
            ? FormatDuration(snapshot.PingStatus.CurrentLossWindow)
            : FormatDuration(snapshot.PingStatus.LongestOutage);
        LiveCurrentRttTextBlock.Text = FormatRoundTripTime(snapshot.PingStatus.CurrentRoundTripTime);
        LiveCurrentApTextBlock.Text = FormatNullable(snapshot.WgbStatus.ParentApName);
        LiveCurrentRssiTextBlock.Text = WgbAssociationSample.FormatRssi(snapshot.WgbStatus.Rssi);
        LiveWgbDataAgeTextBlock.Text = snapshot.WgbStatus.LastSuccessfulPollTimestamp is null
            ? "-"
            : FormatDuration(snapshot.WgbStatus.DataAge);
        LiveWgbStaleTextBlock.Text = snapshot.WgbStatus.IsStale ? "Yes" : "No";

        ApplyDashboardSummary(snapshot);
    }

    private void ApplyDashboardSummary(DiagnosticsRealtimeSnapshot snapshot)
    {
        var lossThresholdMilliseconds = GetDashboardLossThresholdMilliseconds();
        var pingStatus = snapshot.PingStatus;
        var dashboardState = DetermineDashboardStatus(pingStatus, lossThresholdMilliseconds);
        DashboardStatusTextBlock.Text = dashboardState switch
        {
            "DEGRADED" => "WARNING",
            "OUTAGE" => "LOSS",
            _ => dashboardState
        };
        DashboardStatusSymbolTextBlock.Text = dashboardState switch
        {
            "OK" => "OK",
            "DEGRADED" => "!",
            "OUTAGE" => "X",
            _ => "-"
        };

        var statusForeground = dashboardState switch
        {
            "OK" => System.Windows.Media.Brushes.DarkGreen,
            "DEGRADED" => System.Windows.Media.Brushes.DarkOrange,
            "OUTAGE" => System.Windows.Media.Brushes.DarkRed,
            _ => System.Windows.Media.Brushes.DimGray
        };
        DashboardStatusBorder.Background = dashboardState switch
        {
            "OK" => System.Windows.Media.Brushes.Honeydew,
            "DEGRADED" => System.Windows.Media.Brushes.LemonChiffon,
            "OUTAGE" => System.Windows.Media.Brushes.MistyRose,
            _ => System.Windows.Media.Brushes.WhiteSmoke
        };
        DashboardStatusBorder.BorderBrush = dashboardState switch
        {
            "OK" => System.Windows.Media.Brushes.SeaGreen,
            "DEGRADED" => System.Windows.Media.Brushes.DarkOrange,
            "OUTAGE" => System.Windows.Media.Brushes.Crimson,
            _ => System.Windows.Media.Brushes.DarkGray
        };
        DashboardStatusTextBlock.Foreground = statusForeground;
        DashboardStatusSymbolTextBlock.Foreground = statusForeground;
        DashboardInterruptionTextBlock.Foreground = statusForeground;

        DashboardInterruptionTextBlock.Text = dashboardState == "STOPPED"
            ? "Monitoring is not running"
            : pingStatus.ConsecutiveLoss > 0
                ? $"NETWORK INTERRUPTION - {FormatDuration(pingStatus.CurrentLossWindow)}"
                : "No active interruption";

        var latestMarker = snapshot.Markers.LastOrDefault();
        DashboardLatestEventTextBlock.Text = latestMarker is null
            ? "-"
            : $"{latestMarker.Timestamp.ToLocalTime():HH:mm:ss} {latestMarker.Kind switch
            {
                RealtimeGraphMarkerKind.LossStarted => "Packet loss started",
                RealtimeGraphMarkerKind.Recovered => "Connection restored",
                RealtimeGraphMarkerKind.ParentApChanged => "Roam",
                _ => latestMarker.Label
            }}";

        var latestRoam = snapshot.RoamEvents.LastOrDefault();
        if (latestRoam is null)
        {
            DashboardRoamCorrelationTextBlock.Text = "Latest roam: -";
            DashboardPreRoamRssiTextBlock.Text = "RSSI roam: -";
            return;
        }

        var roamText = $"{latestRoam.Timestamp.ToLocalTime():HH:mm:ss} "
            + $"{FormatNullable(latestRoam.OldParentApName)} -> {FormatNullable(latestRoam.NewParentApName)} "
            + $"ch {FormatNullable(latestRoam.OldChannel)} -> {FormatNullable(latestRoam.NewChannel)} "
            + $"radio {FormatNullable(latestRoam.OldRadioId)} -> {FormatNullable(latestRoam.NewRadioId)} "
            + $"RSSI {WgbAssociationSample.FormatRssi(latestRoam.OldRssi)} -> {WgbAssociationSample.FormatRssi(latestRoam.NewRssi)} "
            + latestRoam.RoamClassification;
        DashboardRoamCorrelationTextBlock.Text = $"Latest observed roam: {roamText}";

        DashboardPreRoamRssiTextBlock.Text = $"RSSI roam: {WgbAssociationSample.FormatRssi(latestRoam.OldRssi)} -> {WgbAssociationSample.FormatRssi(latestRoam.NewRssi)}";
    }

    private string DetermineDashboardStatus(
        PingRealtimeStatus pingStatus,
        int lossThresholdMilliseconds)
    {
        if (pingStatus.Status.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
            && pingStatus.TotalOk == 0
            && pingStatus.TotalLost == 0)
        {
            return "STOPPED";
        }

        if (pingStatus.ConsecutiveLoss > 0
            && pingStatus.CurrentLossWindow.TotalMilliseconds >= lossThresholdMilliseconds)
        {
            return "OUTAGE";
        }

        if (pingStatus.Status.Equals("Alert", StringComparison.OrdinalIgnoreCase))
        {
            return "OUTAGE";
        }

        if (pingStatus.ConsecutiveLoss > 0)
        {
            return "DEGRADED";
        }

        if (pingStatus.CurrentRoundTripTime is not null
            && pingStatus.CurrentRoundTripTime.Value.TotalMilliseconds >= lossThresholdMilliseconds)
        {
            return "DEGRADED";
        }

        if (pingStatus.Status.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            return "DEGRADED";
        }

        return "OK";
    }

    private int GetDashboardLossThresholdMilliseconds()
    {
        return int.TryParse(
            LossThresholdMillisecondsTextBox.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var threshold)
            ? Math.Max(1, threshold)
            : WgbDiagnosticsOptions.CreateDefault().LossThresholdMilliseconds;
    }

    private static double ToPlotX(DateTimeOffset timestamp)
    {
        return NumericConversion.ToNumber(timestamp.LocalDateTime);
    }

    private static Color GetMarkerColor(RealtimeGraphMarkerKind kind)
    {
        return kind switch
        {
            RealtimeGraphMarkerKind.LossStarted => Colors.Crimson,
            RealtimeGraphMarkerKind.Recovered => Colors.SeaGreen,
            RealtimeGraphMarkerKind.ParentApChanged => Colors.Orange,
            _ => Colors.Gray
        };
    }

    private void AddGraphMarkerLine(Plot plot, RealtimeGraphMarker marker)
    {
        var isSelected = marker.Kind == RealtimeGraphMarkerKind.ParentApChanged
            && _roamMarkerSelection.IsSelected(marker.MarkerId);
        var line = plot.Add.VerticalLine(
            ToPlotX(marker.Timestamp),
            isSelected ? 3.0f : 1.0f,
            GetMarkerColor(marker.Kind),
            isSelected ? LinePattern.Solid : LinePattern.Dashed);

        if (isSelected)
        {
            line.Text = "Selected roam";
            line.LabelFontSize = 11;
            line.LabelFontColor = Colors.Black;
            line.LabelBackgroundColor = Colors.White;
        }
    }

    private static string FormatMarkerLabel(RealtimeGraphMarker marker)
    {
        if (marker.Kind == RealtimeGraphMarkerKind.ParentApChanged)
        {
            return $"{FormatNullable(marker.OldParentApName)} -> {FormatNullable(marker.NewParentApName)} ch {FormatNullable(marker.OldChannel)} -> {FormatNullable(marker.NewChannel)} radio {FormatNullable(marker.OldRadioId)} -> {FormatNullable(marker.NewRadioId)} {marker.RoamClassification}";
        }

        return marker.Label;
    }

    private static string FormatMarkerSummary(
        IReadOnlyList<RealtimeRoamEvent> roamEvents,
        string? selectedMarkerId)
    {
        return RoamMarkerStatusFormatter.Format(
            roamEvents.Count,
            !string.IsNullOrWhiteSpace(selectedMarkerId));
    }

    private static string FormatRoamTimelineEvent(RealtimeRoamEvent roamEvent)
    {
        var timestamp = roamEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return $"{timestamp} {FormatNullable(roamEvent.OldParentApName)} -> {FormatNullable(roamEvent.NewParentApName)} "
            + $"BSSID {FormatNullable(roamEvent.OldParentBssid)} -> {FormatNullable(roamEvent.NewParentBssid)} "
            + $"ch {FormatNullable(roamEvent.OldChannel)} -> {FormatNullable(roamEvent.NewChannel)} "
            + $"radio {FormatNullable(roamEvent.OldRadioId)} -> {FormatNullable(roamEvent.NewRadioId)} "
            + $"RSSI {WgbAssociationSample.FormatRssi(roamEvent.OldRssi)} -> {WgbAssociationSample.FormatRssi(roamEvent.NewRssi)} "
            + $"{roamEvent.RoamClassification}";
    }

    private static string FormatWgbPollEvent(WgbPollEvent pollEvent)
    {
        var timestamp = pollEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var association = pollEvent.Association is null
            ? ""
            : $" AP={FormatNullable(pollEvent.Association.ParentApName)} RSSI={WgbAssociationSample.FormatRssi(pollEvent.Association.Rssi)} ch={FormatNullable(pollEvent.Association.Channel)} radio={FormatNullable(pollEvent.Association.RadioId)}";
        var roam = pollEvent.Kind == WgbPollEventKind.ParentApChanged
            ? $" {FormatNullable(pollEvent.OldParentApName)} -> {FormatNullable(pollEvent.NewParentApName)} {pollEvent.RoamClassification}"
            : "";
        var message = string.IsNullOrWhiteSpace(pollEvent.Message)
            ? ""
            : $" {pollEvent.Message}";

        return $"{timestamp} {pollEvent.Kind}{association}{roam}{message}";
    }

    private static string FormatSshTestResult(
        WgbCommandExecutionDiagnostics diagnostics,
        string rawOutput)
    {
        return $"{FormatSshDiagnostics(diagnostics)}{Environment.NewLine}{Environment.NewLine}Raw output:{Environment.NewLine}{rawOutput}";
    }

    private static string FormatSshTestFailure(
        WgbCommandExecutionDiagnostics? diagnostics,
        string message)
    {
        if (diagnostics is null)
        {
            return $"Error: {message}";
        }

        return $"{FormatSshDiagnostics(diagnostics)}{Environment.NewLine}Error: {message}";
    }

    private static string FormatSshDiagnostics(WgbCommandExecutionDiagnostics diagnostics)
    {
        var parts = new List<string>
        {
            $"Connection: {FormatStep(diagnostics.ConnectionSucceeded)}",
            diagnostics.EnableAttempted
                ? $"Enable: {FormatStep(diagnostics.EnableSucceeded)}"
                : "Enable: not used",
            $"Command: {FormatStep(diagnostics.CommandExecuted)}",
            $"Prompt: {(diagnostics.FinalPromptConfirmed ? "confirmed" : "not confirmed")}"
        };

        if (diagnostics.PromptResyncAttempted)
        {
            parts.Add($"Resync: {FormatStep(diagnostics.PromptResyncSucceeded)}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.Warning))
        {
            parts.Add($"Warning: {diagnostics.Warning}");
        }

        return string.Join(" | ", parts);
    }

    private static string FormatStep(bool succeeded)
    {
        return succeeded ? "succeeded" : "not completed";
    }

    private static string FormatDashboardWgbStatus(WgbRealtimeStatus status)
    {
        if (status.IsStale)
        {
            return "Stale";
        }

        if (status.Status.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase))
        {
            return "Reconnecting";
        }

        if (status.Status.Contains("Disconnected", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("error", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("lost", StringComparison.OrdinalIgnoreCase))
        {
            return "Disconnected";
        }

        if (status.Status.Contains("Connecting", StringComparison.OrdinalIgnoreCase))
        {
            return "Connecting";
        }

        if (status.Status.Contains("Connected", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("updated", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("Roam", StringComparison.OrdinalIgnoreCase)
            || status.Status.Contains("Warning", StringComparison.OrdinalIgnoreCase))
        {
            return "Connected";
        }

        return status.Status;
    }

    private static string FormatRoundTripTime(TimeSpan? roundTripTime)
    {
        return roundTripTime is null
            ? "-"
            : $"{roundTripTime.Value.TotalMilliseconds:0} ms";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
        {
            return $"{duration.TotalMilliseconds:0} ms";
        }

        return duration.TotalMinutes < 1
            ? $"{duration.TotalSeconds:0.0} s"
            : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatRuntime(TimeSpan runtime)
    {
        return runtime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatGraphVisibleWindow(TimeSpan visibleWindow)
    {
        return visibleWindow.TotalMinutes >= 1
            ? $"{visibleWindow.TotalMinutes:0} min"
            : $"{visibleWindow.TotalSeconds:0} s";
    }

    private void UpdateIcmpTimingText(WgbDiagnosticsOptions options)
    {
        DashboardIcmpTimingTextBlock.Text =
            $"ICMP interval: {options.PingIntervalMilliseconds} ms / timeout: {options.PingTimeoutMilliseconds} ms / threshold: {options.LossThresholdMilliseconds} ms";
        LiveIcmpTimingTextBlock.Text =
            $"{options.PingIntervalMilliseconds} ms interval / {options.PingTimeoutMilliseconds} ms timeout / {options.LossThresholdMilliseconds} ms threshold";
    }

    private static string FormatMonitorEvent(IcmpMonitorEvent monitorEvent)
    {
        var timestamp = monitorEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var rtt = FormatRoundTripTime(monitorEvent.RoundTripTime);
        var message = string.IsNullOrWhiteSpace(monitorEvent.Message) ? "" : $" {monitorEvent.Message}";

        return $"{timestamp} #{monitorEvent.SequenceNumber} {monitorEvent.Kind} RTT={rtt} Loss={monitorEvent.ConsecutiveLoss} Window={monitorEvent.EstimatedLossWindowMilliseconds} ms{message}";
    }

    private static ListBoxItem CreatePingEventListItem(PingEventRow row)
    {
        return new ListBoxItem
        {
            Content = row.Text,
            Foreground = row.Severity switch
            {
                PingEventSeverity.Success => System.Windows.Media.Brushes.DarkGreen,
                PingEventSeverity.Warning => System.Windows.Media.Brushes.DarkOrange,
                PingEventSeverity.Critical => System.Windows.Media.Brushes.DarkRed,
                _ => System.Windows.Media.Brushes.DimGray
            },
            FontWeight = row.Severity == PingEventSeverity.Neutral
                ? FontWeights.Normal
                : FontWeights.SemiBold
        };
    }

    private static void TrimItems(ItemsControl itemsControl, int maxItems)
    {
        while (itemsControl.Items.Count > maxItems)
        {
            itemsControl.Items.RemoveAt(itemsControl.Items.Count - 1);
        }
    }

    private static string FormatNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
