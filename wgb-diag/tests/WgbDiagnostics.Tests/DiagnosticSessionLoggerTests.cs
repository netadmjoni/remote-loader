using System.IO;
using System.Text.Json;
using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Logging;
using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class DiagnosticSessionLoggerTests
{
    [Fact]
    public async Task StartSessionCreatesSessionFolderAndConfigSnapshot()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var clock = new FakeDiagnosticClock(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero));
        var logger = new DiagnosticSessionLogger(clock);

        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, deviceOrTarget: "wgb/one"),
            CreateConfigSnapshot(),
            CancellationToken.None);
        await logger.StopSessionAsync(CancellationToken.None);

        Assert.True(Directory.Exists(session.SessionDirectory));
        Assert.Contains($"wgb_one_{clock.UtcNow.ToLocalTime():yyyyMMdd_HHmmss}", session.SessionDirectory);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "config-snapshot.json")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "session-summary.json")));
    }

    [Fact]
    public async Task PingCsvContainsHeader()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2));
        await logger.StopSessionAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"));
        Assert.Equal("timestamp,event,sequence,rtt_ms,consecutive_loss,loss_window_ms,applied_to_state,ignored_reason,connection_state,message", lines[0]);
    }

    [Fact]
    public async Task PingLoggingIsEventOnlyWhenRawLoggingIsDisabled()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: false),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 8));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, consecutiveLoss: 1, lossWindow: 100));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Loss, sequence: 3, consecutiveLoss: 2, lossWindow: 200));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.AlertThresholdReached, sequence: 3, consecutiveLoss: 2, lossWindow: 600));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Recovered, sequence: 4, rttMilliseconds: 11));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Error, sequence: 5, consecutiveLoss: 1, lossWindow: 10, message: "probe failed"));
        await logger.StopSessionAsync(CancellationToken.None);

        var rows = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"))
            .Skip(1)
            .Select(line => line.Split(',')[1])
            .ToArray();

        Assert.Equal(new[] { "LAST_OK", "LOSS_START", "ALERT", "RECOVER", "ERROR" }, rows);
        Assert.False(File.Exists(Path.Combine(session.SessionDirectory, "raw-ping.log")));
    }

    [Fact]
    public async Task RawLoggingWritesEveryPingProbeEventWhenEnabled()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: true),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, rttMilliseconds: 4));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2));
        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Loss, sequence: 3));
        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 1,
            message: "late_timeout state_unchanged",
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess",
            highestSequenceAppliedToState: 3,
            completionOrder: 4));
        await logger.StopSessionAsync(CancellationToken.None);

        var rawLog = File.ReadAllText(Path.Combine(session.SessionDirectory, "raw-ping.log"));
        Assert.Contains("PingReply", rawLog);
        Assert.Contains("LossStarted", rawLog);
        Assert.Contains("Loss", rawLog);
        Assert.Contains("LATE_TIMEOUT", rawLog);
        Assert.Contains("completion_order=4", rawLog);
        Assert.Contains("appliedToState=false", rawLog);
        Assert.Contains("ignoredReason=OutOfOrderTimeoutAfterNewerSuccess", rawLog);
        Assert.Contains("highestSequenceAppliedToState=3", rawLog);
    }

    [Fact]
    public async Task PacketLossIsWrittenToEventOnlyPingCsvWithoutChangingState()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: false),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 52,
            message: "late_timeout state_unchanged",
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess",
            highestSequenceAppliedToState: 60,
            connectionState: "OK"));
        await logger.StopSessionAsync(CancellationToken.None);

        var rows = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"));
        Assert.Equal(2, rows.Length);
        Assert.Contains(",PACKET_LOSS,52,", rows[1]);
        Assert.Contains(",false,OutOfOrderTimeoutAfterNewerSuccess,OK,", rows[1]);
        Assert.EndsWith(",late_timeout state_unchanged", rows[1]);
    }

    [Fact]
    public async Task PingLossCsvRecordsEveryFailedProbeOnceWithStateSemantics()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);
        var detectedAt = new DateTimeOffset(2026, 7, 18, 10, 0, 2, TimeSpan.Zero);

        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.LossStarted,
            sequence: 10,
            consecutiveLoss: 1,
            lossWindow: 100,
            timestamp: detectedAt,
            startedAtMilliseconds: 1000,
            completedAtMilliseconds: 2000,
            completionOrder: 12,
            connectionState: "Loss"));
        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.AlertThresholdReached,
            sequence: 10,
            consecutiveLoss: 1,
            lossWindow: 600,
            timestamp: detectedAt,
            startedAtMilliseconds: 1000,
            completedAtMilliseconds: 2000,
            completionOrder: 12,
            connectionState: "Alert"));
        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 11,
            timestamp: detectedAt.AddMilliseconds(100),
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess",
            startedAtMilliseconds: 1100,
            completedAtMilliseconds: 2100,
            completionOrder: 13,
            connectionState: "OK"));
        await logger.StopSessionAsync(CancellationToken.None);

        var rows = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-losses.csv"));

        Assert.Equal(
            "probe_started_at,detected_at,sequence,outcome,elapsed_ms,completion_order,applied_to_state,state_consecutive_loss,state_loss_window_ms,ignored_reason,connection_state,message",
            rows[0]);
        Assert.Equal(3, rows.Length);
        Assert.Contains($"{detectedAt.AddSeconds(-1).ToLocalTime():O},{detectedAt.ToLocalTime():O},10,LOSS,1000,12,true,1,100,,Loss,", rows[1]);
        Assert.Contains(",11,LOSS,1000,13,false,0,0,OutOfOrderTimeoutAfterNewerSuccess,OK,", rows[2]);
    }

    [Fact]
    public async Task CsvRawLogsAndSummaryUseLocalIsoTimestampsWithOffset()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var timestamp = new DateTimeOffset(2026, 7, 18, 10, 0, 0, 194, TimeSpan.Zero);
        var clock = new FakeDiagnosticClock(timestamp);
        var logger = new DiagnosticSessionLogger(clock);
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, rawLoggingEnabled: true),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.LossStarted,
            sequence: 1,
            timestamp: timestamp,
            startedAtMilliseconds: 0,
            completedAtMilliseconds: 1000));
        await logger.LogWgbEventAsync(WgbPoll(
            milliseconds: 194,
            txRate: "173 Mbps",
            rxRate: "144 Mbps",
            rawOutput: "sample output"));
        await logger.StopSessionAsync(CancellationToken.None);

        var expectedPingTimestamp = timestamp.ToLocalTime().ToString("O");
        var pingRow = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"))[1];
        var rawPing = File.ReadAllText(Path.Combine(session.SessionDirectory, "raw-ping.log"));
        var wgbTimestamp = new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(194)
            .ToLocalTime()
            .ToString("O");
        var wgbRow = File.ReadAllLines(Path.Combine(session.SessionDirectory, "wgb-samples.csv"))[1];
        var rawWgb = File.ReadAllText(Path.Combine(session.SessionDirectory, "raw-wgb.log"));

        Assert.StartsWith($"{expectedPingTimestamp},", pingRow);
        Assert.Contains(expectedPingTimestamp, rawPing);
        Assert.StartsWith($"{wgbTimestamp},", wgbRow);
        Assert.Contains(wgbTimestamp, rawWgb);

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.SessionDirectory, "session-summary.json")));
        var startedAt = summary.RootElement.GetProperty("startedAt").GetDateTimeOffset();
        Assert.Equal(timestamp.ToLocalTime(), startedAt);
        Assert.Equal(timestamp.ToLocalTime().Offset, startedAt.Offset);
    }

    [Fact]
    public async Task WgbSampleCsvContainsEveryValidPoll()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogWgbEventAsync(WgbPoll(milliseconds: 0, txRate: "173 Mbps", rxRate: "144 Mbps"));
        await logger.LogWgbEventAsync(WgbPoll(milliseconds: 1000, txRate: "144 Mbps", rxRate: "173 Mbps"));
        await logger.StopSessionAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(session.SessionDirectory, "wgb-samples.csv"));

        Assert.Equal("timestamp,parent_ap,parent_bssid,candidate_ap,candidate_bssid,rssi_dbm,channel,tx_rate_mbps,rx_rate_mbps,radio_id,association_state,poll_status,error_reason", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("AP-221", lines[1]);
        Assert.Contains(",-56,", lines[1]);
        Assert.Contains(",173,144,", lines[1]);
        Assert.Contains(",144,173,", lines[2]);
    }

    [Fact]
    public async Task DailyRotationCreatesDatedFilesAfterDateChanges()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var firstLocal = CreateLocalTimestamp(2026, 7, 18, 23, 59, 0);
        var secondLocal = CreateLocalTimestamp(2026, 7, 19, 0, 0, 1);
        var clock = new FakeDiagnosticClock(firstLocal.ToUniversalTime());
        var logger = new DiagnosticSessionLogger(clock);
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path, dailyRotationEnabled: true),
            CreateConfigSnapshot(),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.LossStarted,
            sequence: 1,
            timestamp: firstLocal));
        clock.UtcNow = secondLocal.ToUniversalTime();
        await logger.LogPingEventAsync(Ping(
            IcmpMonitorEventKind.AlertThresholdReached,
            sequence: 2,
            timestamp: secondLocal));
        await logger.StopSessionAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "ping-events.csv")));
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "ping-events_20260719.csv")));
    }

    [Fact]
    public async Task StopSessionFlushesQueuedEventsAndSummary()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(testDirectory.Path),
            CreateConfigSnapshot(),
            CancellationToken.None);

        for (var sequence = 1; sequence <= 100; sequence++)
        {
            await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.LossStarted, sequence));
        }

        await logger.StopSessionAsync(CancellationToken.None);

        var lines = File.ReadAllLines(Path.Combine(session.SessionDirectory, "ping-events.csv"));
        Assert.Equal(101, lines.Length);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, "session-summary.json")));
    }

    [Fact]
    public async Task CredentialsAreNotWrittenToSnapshotOrLogs()
    {
        await using var testDirectory = TempDiagnosticDirectory.Create();
        const string password = "super-secret-password";
        const string enablePassword = "enable-secret-password";
        const string username = "admin-user";
        var logger = new DiagnosticSessionLogger(new FakeDiagnosticClock());
        var session = await logger.StartSessionAsync(
            CreateLoggerOptions(
                testDirectory.Path,
                rawLoggingEnabled: true,
                sensitiveValues: [password, enablePassword, username]),
            CreateConfigSnapshot(username, password, enablePassword),
            CancellationToken.None);

        await logger.LogPingEventAsync(Ping(IcmpMonitorEventKind.Error, sequence: 1, message: $"failure {password} {enablePassword}"));
        await logger.LogWgbEventAsync(new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            DateTimeOffset.UtcNow,
            WgbAssociationSnapshot.Unknown,
            ParseResult: null,
            RawOutput: $"raw output {username} {password} {enablePassword}",
            Message: $"ok {username}"));
        await logger.StopSessionAsync(CancellationToken.None);

        var allText = string.Join(
            Environment.NewLine,
            Directory.GetFiles(session.SessionDirectory)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(password, allText);
        Assert.DoesNotContain(enablePassword, allText);
        Assert.DoesNotContain(username, allText);
        Assert.Contains("[redacted]", allText);
    }

    private static DiagnosticSessionLoggerOptions CreateLoggerOptions(
        string logDirectory,
        string deviceOrTarget = "wgb-1",
        bool rawLoggingEnabled = false,
        bool dailyRotationEnabled = true,
        IReadOnlyList<string>? sensitiveValues = null)
    {
        return new DiagnosticSessionLoggerOptions(
            logDirectory,
            deviceOrTarget,
            rawLoggingEnabled,
            dailyRotationEnabled,
            RetentionDays: 14,
            sensitiveValues ?? []);
    }

    private static WgbDiagnosticsOptions CreateConfigSnapshot(
        string username = "wgb-admin",
        string passwordPlaceholder = "",
        string enablePasswordPlaceholder = "")
    {
        return new WgbDiagnosticsOptions
        {
            SshUsername = username,
            EncryptedPasswordPlaceholder = passwordPlaceholder,
            EncryptedEnablePasswordPlaceholder = enablePasswordPlaceholder
        };
    }

    private static IcmpMonitorEvent Ping(
        IcmpMonitorEventKind kind,
        long sequence,
        int rttMilliseconds = 0,
        int consecutiveLoss = 0,
        int lossWindow = 0,
        string? message = null,
        DateTimeOffset? timestamp = null,
        bool appliedToState = true,
        string? ignoredReason = null,
        long highestSequenceAppliedToState = 0,
        long completionOrder = 0,
        long startedAtMilliseconds = 0,
        long completedAtMilliseconds = 0,
        string connectionState = "Unknown")
    {
        return new IcmpMonitorEvent(
            kind,
            timestamp ?? new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence),
            sequence,
            rttMilliseconds > 0 ? TimeSpan.FromMilliseconds(rttMilliseconds) : null,
            consecutiveLoss,
            lossWindow,
            message,
            StartedAtMilliseconds: startedAtMilliseconds,
            CompletedAtMilliseconds: completedAtMilliseconds,
            CompletionOrder: completionOrder,
            AppliedToState: appliedToState,
            IgnoredReason: ignoredReason,
            HighestSequenceAppliedToState: highestSequenceAppliedToState,
            ConnectionState: connectionState);
    }

    private static WgbPollEvent WgbPoll(
        int milliseconds,
        string txRate,
        string rxRate,
        string? rawOutput = null)
    {
        return new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds),
            new WgbAssociationSnapshot(
                "AP-221",
                "0011.2233.4455",
                "44",
                "-56",
                "1",
                txRate,
                rxRate,
                WgbIp: "192.0.2.10",
                AssociationStatus: "Associated",
                CandidateApName: null,
                CandidateBssid: null),
            ParseResult: null,
            RawOutput: rawOutput,
            Message: null);
    }

    private static DateTimeOffset CreateLocalTimestamp(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }

    private sealed class FakeDiagnosticClock : IDiagnosticClock
    {
        public FakeDiagnosticClock()
            : this(new DateTimeOffset(2026, 7, 18, 10, 0, 0, TimeSpan.Zero))
        {
        }

        public FakeDiagnosticClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class TempDiagnosticDirectory : IAsyncDisposable
    {
        private TempDiagnosticDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDiagnosticDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wgb-diagnostics-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDiagnosticDirectory(path);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
