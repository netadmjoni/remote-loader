namespace WgbDiagnostics.Core.Monitoring;

public sealed class IcmpMonitorStateMachine
{
    private readonly int _lossThresholdMilliseconds;
    private readonly int _intervalMilliseconds;
    private bool _isInLoss;
    private bool _alertRaised;
    private int _consecutiveLoss;
    private long _completionOrder;
    private long _highestCompletedSequence;
    private long _highestSequenceAppliedToState;
    private long _latestSuccessfulSequence;
    private string _connectionState = "OK";

    public IcmpMonitorStateMachine(int lossThresholdMilliseconds, int intervalMilliseconds = 100)
    {
        _lossThresholdMilliseconds = Math.Max(1, lossThresholdMilliseconds);
        _intervalMilliseconds = Math.Max(1, intervalMilliseconds);
    }

    public IReadOnlyList<IcmpMonitorEvent> Apply(IcmpProbeResult result)
    {
        var completionOrder = ++_completionOrder;
        _highestCompletedSequence = Math.Max(_highestCompletedSequence, result.SequenceNumber);

        if (result.SequenceNumber <= _highestSequenceAppliedToState)
        {
            return [CreateIgnoredEvent(result, completionOrder)];
        }

        return result.Outcome switch
        {
            IcmpProbeOutcome.Success => ApplySuccess(result, completionOrder),
            IcmpProbeOutcome.Loss => ApplyLoss(result, completionOrder, includeErrorEvent: false),
            IcmpProbeOutcome.Error => ApplyLoss(result, completionOrder, includeErrorEvent: true),
            _ => []
        };
    }

    private IReadOnlyList<IcmpMonitorEvent> ApplySuccess(IcmpProbeResult result, long completionOrder)
    {
        _highestSequenceAppliedToState = result.SequenceNumber;
        _latestSuccessfulSequence = Math.Max(_latestSuccessfulSequence, result.SequenceNumber);

        if (!_isInLoss)
        {
            _connectionState = "OK";
            return
            [
                CreateEvent(
                    IcmpMonitorEventKind.PingReply,
                    result,
                    completionOrder,
                    result.RoundTripTime,
                    consecutiveLoss: 0,
                    estimatedLossWindowMilliseconds: 0,
                    result.Message)
            ];
        }

        var recoveredConsecutiveLoss = _consecutiveLoss;
        var recoveredWindow = CalculateLossWindow();
        ResetLossState();
        _connectionState = "OK";

        return
        [
            CreateEvent(
                IcmpMonitorEventKind.PingReply,
                result,
                completionOrder,
                result.RoundTripTime,
                consecutiveLoss: 0,
                estimatedLossWindowMilliseconds: 0,
                result.Message),
            CreateEvent(
                IcmpMonitorEventKind.Recovered,
                result,
                completionOrder,
                result.RoundTripTime,
                recoveredConsecutiveLoss,
                recoveredWindow,
                "ICMP target recovered.")
        ];
    }

    private IReadOnlyList<IcmpMonitorEvent> ApplyLoss(
        IcmpProbeResult result,
        long completionOrder,
        bool includeErrorEvent)
    {
        var events = new List<IcmpMonitorEvent>();
        var isFirstLoss = !_isInLoss;
        _highestSequenceAppliedToState = result.SequenceNumber;

        if (isFirstLoss)
        {
            _isInLoss = true;
            _alertRaised = false;
            _consecutiveLoss = 1;
        }
        else
        {
            _consecutiveLoss++;
        }

        var lossWindow = CalculateLossWindow();
        var shouldRaiseAlert = !_alertRaised && lossWindow >= _lossThresholdMilliseconds;
        if (shouldRaiseAlert)
        {
            _alertRaised = true;
        }

        _connectionState = _alertRaised ? "Alert" : "Loss";

        if (includeErrorEvent)
        {
            events.Add(CreateEvent(
                IcmpMonitorEventKind.Error,
                result,
                completionOrder,
                roundTripTime: null,
                _consecutiveLoss,
                lossWindow,
                result.Message));
        }

        events.Add(CreateEvent(
            isFirstLoss ? IcmpMonitorEventKind.LossStarted : IcmpMonitorEventKind.Loss,
            result,
            completionOrder,
            roundTripTime: null,
            _consecutiveLoss,
            lossWindow,
            result.Message));

        if (shouldRaiseAlert)
        {
            events.Add(CreateEvent(
                IcmpMonitorEventKind.AlertThresholdReached,
                result,
                completionOrder,
                roundTripTime: null,
                _consecutiveLoss,
                lossWindow,
                "ICMP loss threshold reached."));
        }

        return events;
    }

    private int CalculateLossWindow()
    {
        if (!_isInLoss)
        {
            return 0;
        }

        var window = (long)_consecutiveLoss * _intervalMilliseconds;
        return window > int.MaxValue ? int.MaxValue : (int)window;
    }

    private void ResetLossState()
    {
        _isInLoss = false;
        _alertRaised = false;
        _consecutiveLoss = 0;
    }

    private static IcmpMonitorEvent CreateEvent(
        IcmpMonitorEventKind kind,
        IcmpProbeResult result,
        long completionOrder,
        TimeSpan? roundTripTime,
        int consecutiveLoss,
        int estimatedLossWindowMilliseconds,
        string? message,
        bool appliedToState,
        string? ignoredReason,
        long highestSequenceAppliedToState,
        long highestCompletedSequence,
        string connectionState)
    {
        return new IcmpMonitorEvent(
            kind,
            result.Timestamp,
            result.SequenceNumber,
            roundTripTime,
            consecutiveLoss,
            estimatedLossWindowMilliseconds,
            message,
            result.StartedAtMilliseconds,
            result.CompletedAtMilliseconds,
            completionOrder,
            appliedToState,
            ignoredReason,
            highestSequenceAppliedToState,
            highestCompletedSequence,
            connectionState);
    }

    private IcmpMonitorEvent CreateEvent(
        IcmpMonitorEventKind kind,
        IcmpProbeResult result,
        long completionOrder,
        TimeSpan? roundTripTime,
        int consecutiveLoss,
        int estimatedLossWindowMilliseconds,
        string? message)
    {
        return CreateEvent(
            kind,
            result,
            completionOrder,
            roundTripTime,
            consecutiveLoss,
            estimatedLossWindowMilliseconds,
            message,
            appliedToState: true,
            ignoredReason: null,
            highestSequenceAppliedToState: _highestSequenceAppliedToState,
            highestCompletedSequence: _highestCompletedSequence,
            connectionState: _connectionState);
    }

    private IcmpMonitorEvent CreateIgnoredEvent(IcmpProbeResult result, long completionOrder)
    {
        var kind = result.Outcome switch
        {
            IcmpProbeOutcome.Success => IcmpMonitorEventKind.PingReply,
            IcmpProbeOutcome.Error => IcmpMonitorEventKind.Error,
            IcmpProbeOutcome.Loss => IcmpMonitorEventKind.PacketLoss,
            _ => IcmpMonitorEventKind.Loss
        };

        var ignoredReason = result.Outcome switch
        {
            IcmpProbeOutcome.Success => "OutOfOrderSuccess",
            IcmpProbeOutcome.Error => "OutOfOrderError",
            _ when result.SequenceNumber <= _latestSuccessfulSequence => "OutOfOrderTimeoutAfterNewerSuccess",
            _ => "OutOfOrderTimeout"
        };
        var message = kind == IcmpMonitorEventKind.PacketLoss
            ? "late_timeout state_unchanged"
            : result.Message;

        return CreateEvent(
            kind,
            result,
            completionOrder,
            result.RoundTripTime,
            consecutiveLoss: 0,
            estimatedLossWindowMilliseconds: 0,
            message,
            appliedToState: false,
            ignoredReason: ignoredReason,
            highestSequenceAppliedToState: _highestSequenceAppliedToState,
            highestCompletedSequence: _highestCompletedSequence,
            connectionState: _connectionState);
    }
}
