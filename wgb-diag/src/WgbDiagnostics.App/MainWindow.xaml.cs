using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ScottPlot;
using WgbDiagnostics.App.Configuration;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.App;

public partial class MainWindow : Window
{
    private readonly ISettingsFileStore _settingsFileStore;
    private readonly IConfigurationValidator<WgbDiagnosticsOptions> _validator;
    private readonly IIcmpMonitor _icmpMonitor;
    private readonly IWgbCommandClient _wgbCommandClient;
    private readonly IWgbAssociationParser _wgbAssociationParser;
    private readonly IWgbPollingService _wgbPollingService;
    private readonly IDiagnosticSessionLogger _sessionLogger;
    private readonly DiagnosticsRealtimeModel _realtimeModel = new();
    private readonly DispatcherTimer _graphRefreshTimer;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private CancellationTokenSource? _wgbPollingCancellation;
    private Task? _wgbPollingTask;
    private string? _lastSessionDirectory;
    private volatile bool _graphNeedsRefresh;
    private bool _graphAutoScrollPaused;
    private int _graphTimerTicks;
    private long _totalOk;
    private long _totalLost;

    public MainWindow(
        ISettingsFileStore settingsFileStore,
        IConfigurationValidator<WgbDiagnosticsOptions> validator,
        IIcmpMonitor icmpMonitor,
        IWgbCommandClient wgbCommandClient,
        IWgbAssociationParser wgbAssociationParser,
        IWgbPollingService wgbPollingService,
        IDiagnosticSessionLogger sessionLogger)
    {
        _settingsFileStore = settingsFileStore;
        _validator = validator;
        _icmpMonitor = icmpMonitor;
        _wgbCommandClient = wgbCommandClient;
        _wgbAssociationParser = wgbAssociationParser;
        _wgbPollingService = wgbPollingService;
        _sessionLogger = sessionLogger;

        InitializeComponent();
        InitializeRttPlot();
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

        _totalOk = 0;
        _totalLost = 0;
        TotalOkTextBlock.Text = "0";
        TotalLostTextBlock.Text = "0";
        ConsecutiveLossTextBlock.Text = "0";
        CurrentRttTextBlock.Text = "-";
        LongestOutageTextBlock.Text = "0 ms";
        RuntimeTextBlock.Text = "00:00:00";
        ProbeEventsListBox.Items.Clear();

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
            var rawOutput = await _wgbCommandClient.ExecuteCommandAsync(
                options.ToCommandRequest(),
                CancellationToken.None);
            var parseResult = _wgbAssociationParser.Parse(rawOutput, options.ParserProfile);
            RawWgbOutputTextBox.Text = rawOutput;
            ApplyWgbParseResult(parseResult);
            WgbStatusTextBlock.Text = "Poll succeeded";
        }
        catch (Exception ex)
        {
            WgbStatusTextBlock.Text = "Poll failed";
            RawWgbOutputTextBox.Text = ex.Message;
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
        _graphNeedsRefresh = true;
        RenderRealtimeGraph(force: true, resetZoom: true);
        ShowStatus("Graph cleared.");
    }

    private void PauseGraphButton_Click(object sender, RoutedEventArgs e)
    {
        _graphAutoScrollPaused = !_graphAutoScrollPaused;
        PauseGraphButton.Content = _graphAutoScrollPaused ? "Resume graph" : "Pause graph";
        GraphStatusTextBlock.Text = _graphAutoScrollPaused ? "Paused" : "Autoscroll";
        _graphNeedsRefresh = true;
    }

    private void ResetZoomButton_Click(object sender, RoutedEventArgs e)
    {
        _graphAutoScrollPaused = false;
        PauseGraphButton.Content = "Pause graph";
        GraphStatusTextBlock.Text = "Autoscroll";
        RenderRealtimeGraph(force: true, resetZoom: true);
    }

    private void LoadSettingsFromDisk()
    {
        var result = _settingsFileStore.Load();
        PopulateForm(result.Options);

        var validationErrors = _validator.Validate(result.Options);
        var errors = result.Errors.Concat(validationErrors).ToList();

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

    private void PopulateForm(WgbDiagnosticsOptions options)
    {
        ApplicationNameTextBox.Text = options.ApplicationName;
        WgbAddressTextBox.Text = options.WgbAddress;
        SshPortTextBox.Text = options.SshPort.ToString(CultureInfo.InvariantCulture);
        SshUsernameTextBox.Text = options.SshUsername;
        SshPasswordBox.Password = "";
        WgbPollIntervalSecondsTextBox.Text = options.WgbPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
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
    }

    private WgbDiagnosticsOptions ReadSettingsFromForm(out List<ConfigurationValidationError> errors)
    {
        errors = [];

        return new WgbDiagnosticsOptions
        {
            ApplicationName = ApplicationNameTextBox.Text.Trim(),
            WgbAddress = WgbAddressTextBox.Text.Trim(),
            SshPort = ReadInt(SshPortTextBox, "SSH port", errors),
            SshUsername = SshUsernameTextBox.Text.Trim(),
            EncryptedPasswordPlaceholder = "",
            WgbPollIntervalSeconds = ReadInt(WgbPollIntervalSecondsTextBox, "WGB poll interval", errors),
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
            WgbLogCollectionEnabled = WgbLogCollectionEnabledCheckBox.IsChecked == true,
            TftpTimeoutSeconds = ReadInt(TftpTimeoutSecondsTextBox, "TFTP timeout", errors),
            MaximumReceivedFileSizeBytes = ReadLong(MaximumReceivedFileSizeBytesTextBox, "Maximum received file size", errors)
        };
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
            SshPasswordBox.Password);
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

        ProbeEventsListBox.Items.Insert(0, FormatMonitorEvent(monitorEvent));
        while (ProbeEventsListBox.Items.Count > 500)
        {
            ProbeEventsListBox.Items.RemoveAt(ProbeEventsListBox.Items.Count - 1);
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
        switch (pollEvent.Kind)
        {
            case WgbPollEventKind.Connected:
                WgbStatusTextBlock.Text = "Connected";
                break;
            case WgbPollEventKind.Disconnected:
                WgbStatusTextBlock.Text = "Disconnected";
                break;
            case WgbPollEventKind.PollSucceeded:
                WgbStatusTextBlock.Text = "Poll succeeded";
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
        RssiTextBlock.Text = FormatNullable(association.Rssi);
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
        UnclassifiedLinesTextBox.Text = string.Join(Environment.NewLine, parseResult.UnclassifiedLines);
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
                options.EncryptedPasswordPlaceholder,
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
        }

        _realtimeModel.Configure(RealtimeGraphOptions.FromDiagnosticsOptions(options));
        _graphAutoScrollPaused = false;
        PauseGraphButton.Content = "Pause graph";
        GraphStatusTextBlock.Text = "Autoscroll";
        _graphNeedsRefresh = true;
        RenderRealtimeGraph(force: true, resetZoom: true);
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
        plot.Axes.SetLimitsX(ToPlotX(now.AddMinutes(-60)), ToPlotX(now));
        RttPlot.Refresh();
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
        var snapshot = _realtimeModel.Snapshot(DateTimeOffset.UtcNow);
        ApplyRealtimeSnapshotToStatus(snapshot);

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

        foreach (var marker in snapshot.Markers)
        {
            var line = plot.Add.VerticalLine(
                ToPlotX(marker.Timestamp),
                1.25f,
                GetMarkerColor(marker.Kind),
                LinePattern.Dashed);
            line.Text = FormatMarkerLabel(marker);
            line.LabelRotation = 90;
            line.LabelFontSize = 10;
            line.LabelOppositeAxis = marker.Kind == RealtimeGraphMarkerKind.ParentApChanged;
        }

        plot.Axes.SetLimitsY(0, Math.Max(10, Math.Ceiling(maxRtt * 1.2)));

        if (!_graphAutoScrollPaused || resetZoom)
        {
            var right = DateTimeOffset.UtcNow;
            var left = right - snapshot.Options.VisibleWindow;
            plot.Axes.SetLimitsX(ToPlotX(left), ToPlotX(right));
        }

        GraphMarkerTextBlock.Text = FormatMarkerSummary(snapshot.Markers);
        RttPlot.Refresh();
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
        RssiTextBlock.Text = FormatNullable(snapshot.WgbStatus.Rssi);
        TxRateTextBlock.Text = FormatNullable(snapshot.WgbStatus.TxRate);
        RxRateTextBlock.Text = FormatNullable(snapshot.WgbStatus.RxRate);
        AssociationStatusTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.WgbStatus.AssociationStatus)
            ? "Unknown"
            : snapshot.WgbStatus.AssociationStatus;
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

    private static string FormatMarkerLabel(RealtimeGraphMarker marker)
    {
        if (marker.Kind == RealtimeGraphMarkerKind.ParentApChanged)
        {
            return $"{FormatNullable(marker.OldParentApName)} -> {FormatNullable(marker.NewParentApName)} ch {FormatNullable(marker.OldChannel)} -> {FormatNullable(marker.NewChannel)} {marker.RoamClassification}";
        }

        return marker.Label;
    }

    private static string FormatMarkerSummary(IReadOnlyList<RealtimeGraphMarker> markers)
    {
        if (markers.Count == 0)
        {
            return "Markers: -";
        }

        var recent = markers
            .TakeLast(4)
            .Select(marker => $"{marker.Timestamp.ToLocalTime():HH:mm:ss} {FormatMarkerLabel(marker)}");
        return $"Markers: {string.Join(" | ", recent)}";
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

    private static string FormatMonitorEvent(IcmpMonitorEvent monitorEvent)
    {
        var timestamp = monitorEvent.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var rtt = FormatRoundTripTime(monitorEvent.RoundTripTime);
        var message = string.IsNullOrWhiteSpace(monitorEvent.Message) ? "" : $" {monitorEvent.Message}";

        return $"{timestamp} #{monitorEvent.SequenceNumber} {monitorEvent.Kind} RTT={rtt} Loss={monitorEvent.ConsecutiveLoss} Window={monitorEvent.EstimatedLossWindowMilliseconds} ms{message}";
    }

    private static string FormatNullable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }
}
