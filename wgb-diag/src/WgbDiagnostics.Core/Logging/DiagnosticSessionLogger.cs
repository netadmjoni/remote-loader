using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;

namespace WgbDiagnostics.Core.Logging;

public sealed class DiagnosticSessionLogger : IDiagnosticSessionLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IDiagnosticClock _clock;
    private readonly object _sync = new();
    private SessionState? _state;

    public DiagnosticSessionLogger(IDiagnosticClock clock)
    {
        _clock = clock;
    }

    public DiagnosticSessionInfo? CurrentSession
    {
        get
        {
            lock (_sync)
            {
                return _state?.Info;
            }
        }
    }

    public Task<DiagnosticSessionInfo> StartSessionAsync(
        DiagnosticSessionLoggerOptions options,
        WgbDiagnosticsOptions configSnapshot,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_state is not null)
            {
                return Task.FromResult(_state.Info);
            }

            var startedAt = _clock.UtcNow;
            var root = string.IsNullOrWhiteSpace(options.LogDirectory)
                ? "Logs"
                : options.LogDirectory;
            Directory.CreateDirectory(root);
            ApplyRetention(root, options.RetentionDays, startedAt);

            var sessionDirectory = Path.Combine(
                root,
                $"{SanitizePathSegment(options.DeviceOrTarget)}_{startedAt.UtcDateTime:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(sessionDirectory);

            var state = new SessionState(
                new DiagnosticSessionInfo(sessionDirectory, startedAt),
                options,
                _clock);
            state.WriteConfigSnapshot(configSnapshot);
            state.Start();
            _state = state;

            return Task.FromResult(state.Info);
        }
    }

    public ValueTask LogPingEventAsync(IcmpMonitorEvent monitorEvent)
    {
        SessionState? state;
        lock (_sync)
        {
            state = _state;
        }

        state?.Enqueue(new PingLogWorkItem(monitorEvent));
        return ValueTask.CompletedTask;
    }

    public ValueTask LogWgbEventAsync(WgbPollEvent pollEvent)
    {
        SessionState? state;
        lock (_sync)
        {
            state = _state;
        }

        state?.Enqueue(new WgbLogWorkItem(pollEvent));
        return ValueTask.CompletedTask;
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        SessionState? state;
        lock (_sync)
        {
            state = _state;
            _state = null;
        }

        if (state is null)
        {
            return;
        }

        await state.StopAsync(cancellationToken);
    }

    private static void ApplyRetention(
        string root,
        int retentionDays,
        DateTimeOffset now)
    {
        if (retentionDays < 1 || !Directory.Exists(root))
        {
            return;
        }

        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        foreach (var directory in Directory.GetDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            if (info.CreationTimeUtc >= cutoff && info.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                info.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "diagnostics" : sanitized;
    }

    private interface ILogWorkItem
    {
        void Write(SessionState state);
    }

    private sealed record PingLogWorkItem(IcmpMonitorEvent MonitorEvent) : ILogWorkItem
    {
        public void Write(SessionState state)
        {
            state.WritePingEvent(MonitorEvent);
        }
    }

    private sealed record WgbLogWorkItem(WgbPollEvent PollEvent) : ILogWorkItem
    {
        public void Write(SessionState state)
        {
            state.WriteWgbEvent(PollEvent);
        }
    }

    private sealed class SessionState
    {
        private readonly DiagnosticSessionLoggerOptions _options;
        private readonly IDiagnosticClock _clock;
        private readonly Channel<ILogWorkItem> _channel = Channel.CreateUnbounded<ILogWorkItem>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        private readonly Dictionary<string, RotatingLogWriter> _writers = [];
        private Task? _writerTask;
        private IcmpMonitorEvent? _lastSuccessfulPing;
        private long _lastLoggedSuccessfulPingSequence;
        private long _pingEvents;
        private long _wgbEvents;
        private long _roamEvents;
        private long _errors;

        public SessionState(
            DiagnosticSessionInfo info,
            DiagnosticSessionLoggerOptions options,
            IDiagnosticClock clock)
        {
            Info = info;
            _options = options;
            _clock = clock;
        }

        public DiagnosticSessionInfo Info { get; }

        public void Start()
        {
            _writerTask = Task.Run(ProcessQueueAsync);
        }

        public void Enqueue(ILogWorkItem workItem)
        {
            _channel.Writer.TryWrite(workItem);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.TryComplete();
            if (_writerTask is not null)
            {
                await _writerTask.WaitAsync(cancellationToken);
            }

            WriteSummary();
            foreach (var writer in _writers.Values)
            {
                writer.Dispose();
            }
        }

        public void WriteConfigSnapshot(WgbDiagnosticsOptions options)
        {
            var sanitized = new WgbDiagnosticsOptions
            {
                ApplicationName = options.ApplicationName,
                WgbAddress = options.WgbAddress,
                SshPort = options.SshPort,
                SshUsername = "",
                EncryptedPasswordPlaceholder = "",
                UseEnableMode = options.UseEnableMode,
                EnableCommand = options.EnableCommand,
                EncryptedEnablePasswordPlaceholder = "",
                WgbPollIntervalSeconds = options.WgbPollIntervalSeconds,
                WgbCommand = options.WgbCommand,
                ParserProfile = options.ParserProfile,
                PingTarget = options.PingTarget,
                PingIntervalMilliseconds = options.PingIntervalMilliseconds,
                PingTimeoutMilliseconds = options.PingTimeoutMilliseconds,
                LossThresholdMilliseconds = options.LossThresholdMilliseconds,
                RawLoggingEnabled = options.RawLoggingEnabled,
                LogDirectory = options.LogDirectory,
                DailyRotationEnabled = options.DailyRotationEnabled,
                RetentionDays = options.RetentionDays,
                GraphVisibleMinutes = options.GraphVisibleMinutes,
                WgbLogCollectionEnabled = options.WgbLogCollectionEnabled,
                TftpTimeoutSeconds = options.TftpTimeoutSeconds,
                MaximumReceivedFileSizeBytes = options.MaximumReceivedFileSizeBytes
            };

            var json = JsonSerializer.Serialize(sanitized, JsonOptions);
            File.WriteAllText(Path.Combine(Info.SessionDirectory, "config-snapshot.json"), json);
        }

        public void WritePingEvent(IcmpMonitorEvent monitorEvent)
        {
            var eventName = MapPingEventName(monitorEvent.Kind);
            if (eventName is not null)
            {
                if (monitorEvent.Kind == IcmpMonitorEventKind.Error)
                {
                    _errors++;
                }

                if (monitorEvent.Kind == IcmpMonitorEventKind.LossStarted)
                {
                    WriteLastOkBeforeLoss();
                }

                WritePingEventLine(eventName, monitorEvent);
            }

            if (monitorEvent.Kind == IcmpMonitorEventKind.PingReply)
            {
                _lastSuccessfulPing = monitorEvent;
            }

            if (_options.RawLoggingEnabled)
            {
                WriteRaw("raw-ping", $"{monitorEvent.Timestamp:O} #{monitorEvent.SequenceNumber} {monitorEvent.Kind} rtt={monitorEvent.RoundTripTime?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "-"} loss={monitorEvent.ConsecutiveLoss} window={monitorEvent.EstimatedLossWindowMilliseconds} {Scrub(monitorEvent.Message)}", monitorEvent.Timestamp);
            }
        }

        public void WriteWgbEvent(WgbPollEvent pollEvent)
        {
            if (pollEvent.Kind is WgbPollEventKind.AssociationUpdated or WgbPollEventKind.ParentApChanged or WgbPollEventKind.PollFailed)
            {
                _wgbEvents++;
                if (pollEvent.Kind == WgbPollEventKind.PollFailed)
                {
                    _errors++;
                }

                WriteLine("events", "timestamp,source,event,message", CsvRow(
                    pollEvent.Timestamp,
                    "wgb",
                    pollEvent.Kind.ToString(),
                    Scrub(pollEvent.Message)),
                    pollEvent.Timestamp);
                WriteLine("wgb-events", "timestamp,event,parent_ap,parent_bssid,channel,rssi,radio_id,tx_rate,rx_rate,wgb_ip,association_status,message", CsvRow(
                    pollEvent.Timestamp,
                    pollEvent.Kind.ToString(),
                    pollEvent.Association?.ParentApName,
                    pollEvent.Association?.ParentBssid,
                    pollEvent.Association?.Channel,
                    pollEvent.Association?.Rssi,
                    pollEvent.Association?.RadioId,
                    pollEvent.Association?.TxRate,
                    pollEvent.Association?.RxRate,
                    pollEvent.Association?.WgbIp,
                    pollEvent.Association?.AssociationStatus,
                    Scrub(pollEvent.Message)),
                    pollEvent.Timestamp);
            }

            if (pollEvent.Kind == WgbPollEventKind.ParentApChanged)
            {
                _roamEvents++;
                WriteLine("roam-events", "timestamp,old_parent_ap,new_parent_ap,old_bssid,new_bssid,old_channel,new_channel,old_radio_id,new_radio_id,classification,potential_bug_match_id", CsvRow(
                    pollEvent.Timestamp,
                    pollEvent.OldParentApName,
                    pollEvent.NewParentApName,
                    pollEvent.OldParentBssid,
                    pollEvent.NewParentBssid,
                    pollEvent.OldChannel,
                    pollEvent.NewChannel,
                    pollEvent.OldRadioId,
                    pollEvent.NewRadioId,
                    pollEvent.RoamClassification.ToString(),
                    pollEvent.PotentialBugMatchId),
                    pollEvent.Timestamp);
            }

            if (_options.RawLoggingEnabled
                && pollEvent.Kind == WgbPollEventKind.PollSucceeded
                && !string.IsNullOrWhiteSpace(pollEvent.RawOutput))
            {
                WriteRaw("raw-wgb", $"{pollEvent.Timestamp:O} {pollEvent.Kind}{Environment.NewLine}{Scrub(pollEvent.RawOutput)}", pollEvent.Timestamp);
            }
        }

        private void WriteLastOkBeforeLoss()
        {
            if (_lastSuccessfulPing is null
                || _lastSuccessfulPing.SequenceNumber == _lastLoggedSuccessfulPingSequence)
            {
                return;
            }

            _lastLoggedSuccessfulPingSequence = _lastSuccessfulPing.SequenceNumber;
            WritePingEventLine("LAST_OK", _lastSuccessfulPing);
        }

        private void WritePingEventLine(string eventName, IcmpMonitorEvent monitorEvent)
        {
            _pingEvents++;
            WriteLine("events", "timestamp,source,event,message", CsvRow(
                monitorEvent.Timestamp,
                "ping",
                eventName,
                Scrub(monitorEvent.Message)),
                monitorEvent.Timestamp);
            WriteLine("ping-events", "timestamp,event,sequence,rtt_ms,consecutive_loss,loss_window_ms,message", CsvRow(
                monitorEvent.Timestamp,
                eventName,
                monitorEvent.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                monitorEvent.RoundTripTime?.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) ?? "",
                monitorEvent.ConsecutiveLoss.ToString(CultureInfo.InvariantCulture),
                monitorEvent.EstimatedLossWindowMilliseconds.ToString(CultureInfo.InvariantCulture),
                Scrub(monitorEvent.Message)),
                monitorEvent.Timestamp);
        }

        private async Task ProcessQueueAsync()
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                item.Write(this);
            }
        }

        private void WriteSummary()
        {
            var summary = new
            {
                startedAt = Info.StartedAt,
                stoppedAt = _clock.UtcNow,
                sessionDirectory = Info.SessionDirectory,
                pingEvents = _pingEvents,
                wgbEvents = _wgbEvents,
                roamEvents = _roamEvents,
                errors = _errors
            };
            var json = JsonSerializer.Serialize(summary, JsonOptions);
            File.WriteAllText(Path.Combine(Info.SessionDirectory, "session-summary.json"), json);
        }

        private void WriteLine(string name, string header, string line, DateTimeOffset timestamp)
        {
            GetWriter(name, "csv", header).WriteLine(line, timestamp);
        }

        private void WriteRaw(string name, string line, DateTimeOffset timestamp)
        {
            GetWriter(name, "log", header: null).WriteLine(line, timestamp);
        }

        private RotatingLogWriter GetWriter(string name, string extension, string? header)
        {
            var key = $"{name}.{extension}";
            if (_writers.TryGetValue(key, out var writer))
            {
                return writer;
            }

            writer = new RotatingLogWriter(
                Info.SessionDirectory,
                name,
                extension,
                header,
                Info.StartedAt.UtcDateTime.Date,
                _options.DailyRotationEnabled);
            _writers[key] = writer;
            return writer;
        }

        private string Scrub(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var scrubbed = value;
            foreach (var sensitive in _options.SensitiveValues.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                scrubbed = scrubbed.Replace(sensitive, "[redacted]", StringComparison.Ordinal);
            }

            return scrubbed;
        }
    }

    private sealed class RotatingLogWriter : IDisposable
    {
        private readonly string _directory;
        private readonly string _name;
        private readonly string _extension;
        private readonly string? _header;
        private readonly DateTime _initialDate;
        private readonly bool _dailyRotationEnabled;
        private DateTime _currentDate;
        private StreamWriter? _writer;

        public RotatingLogWriter(
            string directory,
            string name,
            string extension,
            string? header,
            DateTime initialDate,
            bool dailyRotationEnabled)
        {
            _directory = directory;
            _name = name;
            _extension = extension;
            _header = header;
            _initialDate = initialDate;
            _dailyRotationEnabled = dailyRotationEnabled;
        }

        public void WriteLine(string line, DateTimeOffset timestamp)
        {
            EnsureWriter(timestamp);
            _writer!.WriteLine(line);
            _writer.Flush();
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }

        private void EnsureWriter(DateTimeOffset timestamp)
        {
            var date = _dailyRotationEnabled ? timestamp.UtcDateTime.Date : _initialDate;
            if (_writer is not null && date == _currentDate)
            {
                return;
            }

            _writer?.Dispose();
            _currentDate = date;
            var path = Path.Combine(_directory, CreateFileName(date));
            var exists = File.Exists(path);
            _writer = new StreamWriter(path, append: true);

            if (!exists && !string.IsNullOrWhiteSpace(_header))
            {
                _writer.WriteLine(_header);
            }
        }

        private string CreateFileName(DateTime date)
        {
            if (!_dailyRotationEnabled || date == _initialDate)
            {
                return $"{_name}.{_extension}";
            }

            return $"{_name}_{date:yyyyMMdd}.{_extension}";
        }
    }

    private static string? MapPingEventName(IcmpMonitorEventKind kind)
    {
        return kind switch
        {
            IcmpMonitorEventKind.LossStarted => "LOSS_START",
            IcmpMonitorEventKind.AlertThresholdReached => "ALERT",
            IcmpMonitorEventKind.Recovered => "RECOVER",
            IcmpMonitorEventKind.Error => "ERROR",
            _ => null
        };
    }

    private static string CsvRow(params object?[] values)
    {
        return string.Join(",", values.Select(EscapeCsv));
    }

    private static string EscapeCsv(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };

        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
        {
            return text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
