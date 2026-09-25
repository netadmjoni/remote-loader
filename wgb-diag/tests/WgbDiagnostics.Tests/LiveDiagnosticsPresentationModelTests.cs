using WgbDiagnostics.Core.Monitoring;
using WgbDiagnostics.Core.Realtime;
using WgbDiagnostics.Core.Wgb;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class LiveDiagnosticsPresentationModelTests
{
    private static readonly DateTimeOffset BaseTimestamp = new(2026, 7, 23, 21, 4, 30, TimeSpan.Zero);

    [Fact]
    public void IcmpEventsRouteToLeftPanelAndWgbEventsRouteToRightPanel()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 10));

        var snapshot = model.Snapshot();

        Assert.Single(snapshot.IcmpRows);
        Assert.Equal(LiveDiagnosticsPanel.Icmp, snapshot.IcmpRows[0].Panel);
        Assert.Single(snapshot.WgbRows);
        Assert.Equal(LiveDiagnosticsPanel.Wgb, snapshot.WgbRows[0].Panel);
    }

    [Fact]
    public void EventsAreShownChronologicallyWhenLateEventsArrive()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 3, milliseconds: 300, rttMilliseconds: 21));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 100, rttMilliseconds: 18));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 2, milliseconds: 200, rttMilliseconds: 19));

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(row => ExtractSequence(row.Text)).ToArray());
    }

    [Fact]
    public void DuplicateEventsAreStoredOnce()
    {
        var model = new LiveDiagnosticsPresentationModel();
        var ping = Ping(IcmpMonitorEventKind.LossStarted, sequence: 7, milliseconds: 700, consecutiveLoss: 1, lossWindow: 100);
        var wgb = Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 800);

        Assert.True(model.Apply(ping).Accepted);
        Assert.False(model.Apply(ping).Accepted);
        Assert.True(model.Apply(wgb).Accepted);
        Assert.False(model.Apply(wgb).Accepted);

        var snapshot = model.Snapshot();
        Assert.Single(snapshot.IcmpRows);
        Assert.Single(snapshot.WgbRows);
    }

    [Fact]
    public void AllPingsShowsEveryPingResult()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.AllPings);

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, milliseconds: 100, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.Loss, sequence: 3, milliseconds: 200, consecutiveLoss: 2, lossWindow: 200));
        model.Apply(Ping(IcmpMonitorEventKind.Recovered, sequence: 4, milliseconds: 300, rttMilliseconds: 24, lossWindow: 200));

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(new[] { "OK", "LOSS_START", "TIMEOUT", "RECOVER" }, rows.Select(row => row.EventName).ToArray());
        Assert.All(rows, row => Assert.Contains("seq=", row.Text));
    }

    [Fact]
    public void AllPingsHandlesSeveralMinutesAtScriptCadenceWithinBuffer()
    {
        var model = new LiveDiagnosticsPresentationModel(bufferSize: 5000);
        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.AllPings);

        for (var sequence = 1; sequence <= 1800; sequence++)
        {
            model.Apply(Ping(
                IcmpMonitorEventKind.PingReply,
                sequence,
                milliseconds: sequence * 100,
                rttMilliseconds: 15 + sequence % 10));
        }

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(1800, rows.Count);
        Assert.Equal(1, ExtractSequence(rows[0].Text));
        Assert.Equal(1800, ExtractSequence(rows[^1].Text));
    }

    [Fact]
    public void EventsOnlyFiltersOrdinaryPingReplies()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.EventsOnly);

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 2, milliseconds: 100, rttMilliseconds: 19));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 3, milliseconds: 200, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.AlertThresholdReached, sequence: 4, milliseconds: 300, consecutiveLoss: 2, lossWindow: 650));
        model.Apply(Ping(IcmpMonitorEventKind.Recovered, sequence: 5, milliseconds: 400, rttMilliseconds: 24, lossWindow: 650));

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(new[] { "LAST_OK", "LOSS_START", "ALERT", "RECOVER" }, rows.Select(row => row.EventName).ToArray());
        Assert.DoesNotContain(rows, row => row.EventName == "OK");
    }

    [Fact]
    public void LateTimeoutIsVisibleAsPacketLossInAllPingsAndEventsOnly()
    {
        var model = new LiveDiagnosticsPresentationModel();
        var lateTimeout = Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 52,
            milliseconds: 6_054,
            message: "late_timeout state_unchanged",
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess");

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 60, milliseconds: 6_029, rttMilliseconds: 7));
        model.Apply(lateTimeout);
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 61, milliseconds: 6_121, rttMilliseconds: 6));

        var allPingsRows = model.Snapshot().IcmpRows;

        Assert.Equal(new[] { "OK", "PACKET_LOSS", "OK" }, allPingsRows.Select(row => row.EventName).ToArray());
        Assert.Contains("seq=52", allPingsRows[1].Text);
        Assert.Contains("late_timeout", allPingsRows[1].Text);
        Assert.Contains("state_unchanged", allPingsRows[1].Text);
        Assert.DoesNotContain("ignored_for_state", allPingsRows[1].Text);

        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.EventsOnly);

        var packetLoss = Assert.Single(model.Snapshot().IcmpRows);
        Assert.Equal("PACKET_LOSS", packetLoss.EventName);
        Assert.Contains("state_unchanged", packetLoss.Text);
    }

    [Fact]
    public void TenSuccessesOneLateTimeoutAndMoreSuccessesShowsOneEventOnlyPacketLoss()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.AllPings);

        for (var sequence = 1; sequence <= 10; sequence++)
        {
            model.Apply(Ping(
                IcmpMonitorEventKind.PingReply,
                sequence,
                milliseconds: sequence * 100,
                rttMilliseconds: 5));
        }

        model.Apply(Ping(
            IcmpMonitorEventKind.PacketLoss,
            sequence: 7,
            milliseconds: 1_050,
            message: "late_timeout state_unchanged",
            appliedToState: false,
            ignoredReason: "OutOfOrderTimeoutAfterNewerSuccess"));

        for (var sequence = 11; sequence <= 15; sequence++)
        {
            model.Apply(Ping(
                IcmpMonitorEventKind.PingReply,
                sequence,
                milliseconds: sequence * 100,
                rttMilliseconds: 5));
        }

        var allPingsRows = model.Snapshot().IcmpRows;
        Assert.Equal(16, allPingsRows.Count);
        Assert.Single(allPingsRows.Where(row => row.EventName == "PACKET_LOSS"));

        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.EventsOnly);

        var eventOnlyRow = Assert.Single(model.Snapshot().IcmpRows);
        Assert.Equal("PACKET_LOSS", eventOnlyRow.EventName);
        Assert.Contains("seq=7", eventOnlyRow.Text);
    }

    [Fact]
    public void LossWindowShowsEveryPingDuringOutageWithNormalSummaries()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.LossWindow);

        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 2, milliseconds: 100, rttMilliseconds: 19));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 3, milliseconds: 200, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Ping(IcmpMonitorEventKind.Loss, sequence: 4, milliseconds: 300, consecutiveLoss: 2, lossWindow: 200));
        model.Apply(Ping(IcmpMonitorEventKind.AlertThresholdReached, sequence: 5, milliseconds: 400, consecutiveLoss: 3, lossWindow: 650));
        model.Apply(Ping(IcmpMonitorEventKind.Recovered, sequence: 6, milliseconds: 500, rttMilliseconds: 24, lossWindow: 650));
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 7, milliseconds: 600, rttMilliseconds: 20));

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(
            new[] { "OK_PERIOD", "LOSS_START", "TIMEOUT", "ALERT", "RECOVER", "OK_PERIOD" },
            rows.Select(row => row.EventName).ToArray());
    }

    [Fact]
    public void ChangingIcmpDisplayModeDoesNotRemoveBufferedEvents()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 2, milliseconds: 100, consecutiveLoss: 1, lossWindow: 100));

        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.EventsOnly);
        Assert.Equal(2, model.Snapshot().IcmpSourceEventCount);

        model.SetIcmpDisplayMode(LiveIcmpDisplayMode.AllPings);
        Assert.Equal(new[] { "OK", "LOSS_START" }, model.Snapshot().IcmpRows.Select(row => row.EventName).ToArray());
    }

    [Fact]
    public void ValidWgbPollCreatesExactlyOneOperatorSampleRow()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, rssi: "-56"));

        var row = Assert.Single(model.Snapshot().WgbRows);
        Assert.Equal("SAMPLE", row.EventName);
        Assert.Contains("AP=AP-221", row.Text);
        Assert.Contains("RSSI=-56 dBm", row.Text);
        Assert.Contains("CH=44", row.Text);
        Assert.Contains("RATE=\"173/173 Mbps\"", row.Text);
        Assert.Contains("R=1", row.Text);
        Assert.DoesNotContain("POLL SUCCESS", row.Text);
        Assert.Contains("2026-07-23 23:04:30.000", row.Text);
    }

    [Fact]
    public void TenValidWgbPollsCreateTenRowsInAllSamples()
    {
        var model = new LiveDiagnosticsPresentationModel();

        for (var index = 0; index < 10; index++)
        {
            model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: index * 1000));
        }

        var rows = model.Snapshot().WgbRows;

        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal("SAMPLE", row.EventName));
    }

    [Fact]
    public void ChangesOnlySuppressesRepeatedIdenticalSamplesAfterInitial()
    {
        var model = new LiveDiagnosticsPresentationModel();
        model.SetWgbDisplayMode(LiveWgbDisplayMode.ChangesOnly);

        for (var index = 0; index < 10; index++)
        {
            model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: index * 1000));
        }

        var row = Assert.Single(model.Snapshot().WgbRows);
        Assert.Equal("SAMPLE", row.EventName);
    }

    [Fact]
    public void ApChangeCreatesSampleRowAndRoamMarkerInAllSamples()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, parentAp: "AP-221", bssid: "aa:bb:cc", channel: "44", radioId: "1", rssi: "-64"));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 1000, parentAp: "AP-222", bssid: "dd:ee:ff", channel: "48", radioId: "2", rssi: "-53"));

        var rows = model.Snapshot().WgbRows;

        Assert.Equal(new[] { "SAMPLE", "ROAM", "SAMPLE" }, rows.Select(row => row.EventName).ToArray());
        Assert.Contains("AP=AP-221->AP-222", rows[1].Text);
        Assert.Contains("CH=44->48", rows[1].Text);
        Assert.Contains("R=1->2", rows[1].Text);
        Assert.Contains("RSSI=-64->-53 dBm", rows[1].Text);
        Assert.Contains("AP=AP-222", rows[2].Text);
        Assert.Contains("RSSI=-53 dBm", rows[2].Text);
    }

    [Fact]
    public void ChannelChangeIsDetectedFromAdjacentSamples()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, channel: "44"));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 1000, channel: "48"));

        var roam = Assert.Single(model.Snapshot().WgbRows.Where(row => row.EventName == "ROAM"));

        Assert.Contains("CH=44->48", roam.Text);
    }

    [Fact]
    public void RadioChangeIsDetectedFromAdjacentSamples()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, radioId: "1"));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 1000, radioId: "2"));

        var roam = Assert.Single(model.Snapshot().WgbRows.Where(row => row.EventName == "ROAM"));

        Assert.Contains("R=1->2", roam.Text);
    }

    [Fact]
    public void TxRxRateIsShownInScriptOrder()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, txRate: "144 Mbps", rxRate: "173 Mbps"));

        var row = Assert.Single(model.Snapshot().WgbRows);

        Assert.Contains("RATE=\"144/173 Mbps\"", row.Text);
    }

    [Fact]
    public void MissingOptionalWgbFieldsAreShownAsDashes()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(new WgbPollEvent(
            WgbPollEventKind.PollSucceeded,
            BaseTimestamp,
            new WgbAssociationSnapshot(
                "AP-221",
                ParentBssid: null,
                Channel: "44",
                Rssi: "-56",
                RadioId: null,
                TxRate: null,
                RxRate: null,
                WgbIp: null,
                AssociationStatus: "Associated",
                CandidateApName: null,
                CandidateBssid: null),
            ParseResult: null,
            RawOutput: null,
            Message: null));

        var row = Assert.Single(model.Snapshot().WgbRows);

        Assert.Contains("RATE=\"-/-\"", row.Text);
        Assert.Contains("R=-", row.Text);
    }

    [Fact]
    public void CommandLifecycleEventsAreHiddenFromWgbOperatorView()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.CommandStarted, milliseconds: 0, message: "running command"));
        model.Apply(Wgb(WgbPollEventKind.CommandOutputReceived, milliseconds: 10, message: "raw output received"));
        model.Apply(Wgb(WgbPollEventKind.CommandCompleted, milliseconds: 20, message: "command complete"));
        model.Apply(Wgb(WgbPollEventKind.AssociationUpdated, milliseconds: 30, message: "association updated"));

        Assert.Empty(model.Snapshot().WgbRows);
    }

    [Fact]
    public void PollFailureIsShownAsCompactOperatorRow()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(WgbFailure(WgbPollEventKind.PollFailed, milliseconds: 0, "SSH timeout"));

        var row = Assert.Single(model.Snapshot().WgbRows);

        Assert.Equal("POLL_FAILED", row.EventName);
        Assert.Contains("POLL_FAILED reason=\"SSH timeout\"", row.Text);
    }

    [Fact]
    public void DisconnectReconnectAreShownAsCompactOperatorRows()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(WgbFailure(WgbPollEventKind.SessionLost, milliseconds: 0, "socket closed"));
        model.Apply(WgbFailure(WgbPollEventKind.Disconnected, milliseconds: 1, "socket closed"));
        model.Apply(WgbFailure(WgbPollEventKind.ReconnectScheduled, milliseconds: 2, "Reconnect scheduled in 2 seconds."));
        model.Apply(WgbFailure(WgbPollEventKind.Connected, milliseconds: 2000, null));

        var rows = model.Snapshot().WgbRows;

        Assert.Equal(new[] { "DISCONNECTED", "RECONNECTING", "RECONNECTED" }, rows.Select(row => row.EventName).ToArray());
    }

    [Fact]
    public void IdenticalParserFailuresAreDeduplicatedAndThrottled()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(WgbFailure(WgbPollEventKind.PollFailed, milliseconds: 0, "parser failed: missing parent AP"));
        model.Apply(WgbFailure(WgbPollEventKind.PollFailed, milliseconds: 10_000, "parser failed: missing parent AP"));
        model.Apply(WgbFailure(WgbPollEventKind.PollFailed, milliseconds: 70_000, "parser failed: missing parent AP"));

        var rows = model.Snapshot().WgbRows;

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("PARSER_FAILURE", row.EventName));
        Assert.Contains("parser warning repeated 1 times", rows[1].Text);
    }

    [Fact]
    public void FiveMinutesOfWgbPollingDoesNotShowInternalRows()
    {
        var model = new LiveDiagnosticsPresentationModel(bufferSize: 2000);

        for (var second = 0; second < 300; second++)
        {
            model.Apply(Wgb(WgbPollEventKind.CommandStarted, milliseconds: second * 1000, message: "running"));
            model.Apply(Wgb(WgbPollEventKind.CommandOutputReceived, milliseconds: second * 1000 + 10, message: "raw"));
            model.Apply(Wgb(WgbPollEventKind.CommandCompleted, milliseconds: second * 1000 + 20, message: "done"));
            model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: second * 1000 + 30, rssi: (-56 - second % 3).ToString()));
            model.Apply(Wgb(WgbPollEventKind.AssociationUpdated, milliseconds: second * 1000 + 40, message: "updated"));
        }

        var rows = model.Snapshot().WgbRows;

        Assert.Equal(300, rows.Count);
        Assert.All(rows, row => Assert.Equal("SAMPLE", row.EventName));
    }

    [Fact]
    public void PausingViewportDoesNotBlockDataCollection()
    {
        var model = new LiveDiagnosticsPresentationModel();
        var viewport = new LiveDiagnosticsPanelViewport();

        viewport.Pause();
        model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence: 1, milliseconds: 0, rttMilliseconds: 18));
        viewport.NoteEventsAdded(1);

        Assert.True(viewport.IsPaused);
        Assert.Equal(1, viewport.NewEventsWhileViewingOlderData);
        Assert.Single(model.Snapshot().IcmpRows);
    }

    [Fact]
    public void UserScrollingUpDisablesAutoscrollAndCountsNewEvents()
    {
        var viewport = new LiveDiagnosticsPanelViewport();

        viewport.UserScrolledAwayFromLatest();
        viewport.NoteEventsAdded(12);

        Assert.False(viewport.IsAutoscrollEnabled);
        Assert.True(viewport.IsViewingOlderData);
        Assert.Equal(12, viewport.NewEventsWhileViewingOlderData);
    }

    [Fact]
    public void JumpToLatestReenablesAutoscroll()
    {
        var viewport = new LiveDiagnosticsPanelViewport();

        viewport.UserScrolledAwayFromLatest();
        viewport.NoteEventsAdded(3);
        viewport.JumpToLatest();

        Assert.False(viewport.IsPaused);
        Assert.True(viewport.IsAutoscrollEnabled);
        Assert.Equal(0, viewport.NewEventsWhileViewingOlderData);
    }

    [Fact]
    public void BoundedBufferTrimsOldestDisplayEvents()
    {
        var model = new LiveDiagnosticsPresentationModel(bufferSize: 3);

        for (var sequence = 1; sequence <= 5; sequence++)
        {
            model.Apply(Ping(IcmpMonitorEventKind.PingReply, sequence, milliseconds: sequence * 100, rttMilliseconds: 10 + sequence));
        }

        var rows = model.Snapshot().IcmpRows;

        Assert.Equal(3, rows.Count);
        Assert.Equal(new long[] { 3, 4, 5 }, rows.Select(row => ExtractSequence(row.Text)).ToArray());
    }

    [Fact]
    public void LayoutStateCanSwitchBetweenSavedModes()
    {
        Assert.True(Enum.TryParse<LiveDiagnosticsLayout>("Horizontal", ignoreCase: true, out var horizontal));
        Assert.True(Enum.TryParse<LiveDiagnosticsLayout>("Vertical", ignoreCase: true, out var vertical));
        Assert.Equal(LiveDiagnosticsLayout.Horizontal, horizontal);
        Assert.Equal(LiveDiagnosticsLayout.Vertical, vertical);
    }

    [Fact]
    public void RoamRowsDoNotIncludeIcmpCorrelationOrInferredDuration()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Ping(IcmpMonitorEventKind.LossStarted, sequence: 10, milliseconds: 1_000, consecutiveLoss: 1, lossWindow: 100));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 1_000, parentAp: "AP-221", bssid: "aa:bb:cc", channel: "44", radioId: "1", rssi: "-62"));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 1_120, parentAp: "AP-223", bssid: "dd:ee:ff", channel: "48", radioId: "2", rssi: "-55"));
        model.Apply(Ping(IcmpMonitorEventKind.Recovered, sequence: 11, milliseconds: 1_970, rttMilliseconds: 24, lossWindow: 970));

        var roam = Assert.Single(model.Snapshot().WgbRows.Where(row => row.EventName == "ROAM"));

        Assert.DoesNotContain("ICMP", roam.Text);
        Assert.DoesNotContain("loss started", roam.Text);
        Assert.DoesNotContain("recovered", roam.Text);
        Assert.DoesNotContain("duration", roam.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("impact", roam.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoamRowsCarryApBssidChannelRadioRssiAndClassification()
    {
        var model = new LiveDiagnosticsPresentationModel();

        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 0, parentAp: "AP-221", bssid: "aa:bb:cc", channel: "44", radioId: "1", rssi: "-62"));
        model.Apply(Wgb(WgbPollEventKind.PollSucceeded, milliseconds: 100, parentAp: "AP-223", bssid: "dd:ee:ff", channel: "48", radioId: "2", rssi: "-55"));

        var roam = Assert.Single(model.Snapshot().WgbRows.Where(row => row.EventName == "ROAM"));

        Assert.Contains("AP=AP-221->AP-223", roam.Text);
        Assert.Contains("BSSID=aa:bb:cc->dd:ee:ff", roam.Text);
        Assert.Contains("CH=44->48", roam.Text);
        Assert.Contains("R=1->2", roam.Text);
        Assert.Equal(BaseTimestamp.AddMilliseconds(100), roam.Timestamp);
        Assert.Contains("RSSI=-62->-55 dBm", roam.Text);
        Assert.Contains("RATE=\"173/173->173/173 Mbps\"", roam.Text);
        Assert.Contains("DifferentApDifferentChannel", roam.Text);
        Assert.DoesNotContain("ICMP", roam.Text);
        Assert.DoesNotContain("impact", roam.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static long ExtractSequence(string text)
    {
        var marker = "seq=";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"No sequence found in '{text}'.");
        index += marker.Length;
        var end = index;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        return long.Parse(text[index..end]);
    }

    private static IcmpMonitorEvent Ping(
        IcmpMonitorEventKind kind,
        long sequence,
        int milliseconds,
        int rttMilliseconds = 0,
        int consecutiveLoss = 0,
        int lossWindow = 0,
        string? message = null,
        bool appliedToState = true,
        string? ignoredReason = null)
    {
        return new IcmpMonitorEvent(
            kind,
            BaseTimestamp.AddMilliseconds(milliseconds),
            sequence,
            rttMilliseconds > 0 ? TimeSpan.FromMilliseconds(rttMilliseconds) : null,
            consecutiveLoss,
            lossWindow,
            message,
            AppliedToState: appliedToState,
            IgnoredReason: ignoredReason);
    }

    private static WgbPollEvent Wgb(
        WgbPollEventKind kind,
        int milliseconds,
        string parentAp = "AP-221",
        string bssid = "aa:bb:cc",
        string channel = "44",
        string radioId = "1",
        string rssi = "-62",
        string txRate = "173 Mbps",
        string rxRate = "173 Mbps",
        string associationStatus = "Associated",
        string? message = null)
    {
        return new WgbPollEvent(
            kind,
            BaseTimestamp.AddMilliseconds(milliseconds),
            Association(parentAp, bssid, channel, radioId, rssi, txRate, rxRate, associationStatus),
            ParseResult: null,
            RawOutput: null,
            Message: message);
    }

    private static WgbPollEvent WgbFailure(WgbPollEventKind kind, int milliseconds, string? message)
    {
        return new WgbPollEvent(
            kind,
            BaseTimestamp.AddMilliseconds(milliseconds),
            Association("AP-221", "aa:bb:cc", "44", "1", "-62"),
            ParseResult: null,
            RawOutput: null,
            Message: message);
    }

    private static WgbPollEvent ParentChanged(int milliseconds)
    {
        return new WgbPollEvent(
            WgbPollEventKind.ParentApChanged,
            BaseTimestamp.AddMilliseconds(milliseconds),
            Association("AP-223", "dd:ee:ff", "48", "2", "-55"),
            ParseResult: null,
            RawOutput: null,
            Message: "Parent AP transition observed.",
            OldParentApName: "AP-221",
            NewParentApName: "AP-223",
            OldParentBssid: "aa:bb:cc",
            NewParentBssid: "dd:ee:ff",
            OldChannel: "44",
            NewChannel: "48",
            OldRadioId: "1",
            NewRadioId: "2",
            RoamClassification: WgbRoamClassification.DifferentApDifferentChannel,
            OldRssi: "-62",
            NewRssi: "-55");
    }

    private static WgbAssociationSnapshot Association(
        string parentAp,
        string bssid,
        string channel,
        string radioId,
        string rssi,
        string txRate = "173 Mbps",
        string rxRate = "173 Mbps",
        string associationStatus = "Associated")
    {
        return new WgbAssociationSnapshot(
            parentAp,
            bssid,
            channel,
            rssi,
            radioId,
            TxRate: txRate,
            RxRate: rxRate,
            WgbIp: "192.168.1.20",
            AssociationStatus: associationStatus,
            CandidateApName: null,
            CandidateBssid: null);
    }
}
