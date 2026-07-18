namespace WgbDiagnostics.Core.Monitoring;

public sealed class IcmpMonitorStateMachine
{
    private readonly int _lossThresholdMilliseconds;
    private bool _isInLoss;
    private bool _alertRaised;
    private int _consecutiveLoss;
    private long _lossStartedAtMilliseconds;

    public IcmpMonitorStateMachine(int lossThresholdMilliseconds)
    {
        _lossThresholdMilliseconds = Math.Max(1, lossThresholdMilliseconds);
    }

    public IReadOnlyList<IcmpMonitorEvent> Apply(IcmpProbeResult result)
    {
        return result.Outcome switch
        {
            IcmpProbeOutcome.Success => ApplySuccess(result),
            IcmpProbeOutcome.Loss => ApplyLoss(result, includeErrorEvent: false),
            IcmpProbeOutcome.Error => ApplyLoss(result, includeErrorEvent: true),
            _ => []
        };
    }

    private IReadOnlyList<IcmpMonitorEvent> ApplySuccess(IcmpProbeResult result)
    {
        var events = new List<IcmpMonitorEvent>
        {
            CreateEvent(
                IcmpMonitorEventKind.PingReply,
                result,
                result.RoundTripTime,
                consecutiveLoss: 0,
                estimatedLossWindowMilliseconds: 0,
                result.Message)
        };

        if (!_isInLoss)
        {
            return events;
        }

        var recoveredConsecutiveLoss = _consecutiveLoss;
        var recoveredWindow = CalculateLossWindow(result.CompletedAtMilliseconds);

        events.Add(CreateEvent(
            IcmpMonitorEventKind.Recovered,
            result,
            result.RoundTripTime,
            recoveredConsecutiveLoss,
            recoveredWindow,
            "ICMP target recovered."));

        ResetLossState();
        return events;
    }

    private IReadOnlyList<IcmpMonitorEvent> ApplyLoss(
        IcmpProbeResult result,
        bool includeErrorEvent)
    {
        var events = new List<IcmpMonitorEvent>();
        var isFirstLoss = !_isInLoss;

        if (isFirstLoss)
        {
            _isInLoss = true;
            _alertRaised = false;
            _consecutiveLoss = 1;
            _lossStartedAtMilliseconds = result.StartedAtMilliseconds;
        }
        else
        {
            _consecutiveLoss++;
        }

        var lossWindow = CalculateLossWindow(result.CompletedAtMilliseconds);

        if (includeErrorEvent)
        {
            events.Add(CreateEvent(
                IcmpMonitorEventKind.Error,
                result,
                roundTripTime: null,
                _consecutiveLoss,
                lossWindow,
                result.Message));
        }

        events.Add(CreateEvent(
            isFirstLoss ? IcmpMonitorEventKind.LossStarted : IcmpMonitorEventKind.Loss,
            result,
            roundTripTime: null,
            _consecutiveLoss,
            lossWindow,
            result.Message));

        if (!_alertRaised && lossWindow >= _lossThresholdMilliseconds)
        {
            _alertRaised = true;
            events.Add(CreateEvent(
                IcmpMonitorEventKind.AlertThresholdReached,
                result,
                roundTripTime: null,
                _consecutiveLoss,
                lossWindow,
                "ICMP loss threshold reached."));
        }

        return events;
    }

    private int CalculateLossWindow(long completedAtMilliseconds)
    {
        if (!_isInLoss)
        {
            return 0;
        }

        var window = Math.Max(0, completedAtMilliseconds - _lossStartedAtMilliseconds);
        return window > int.MaxValue ? int.MaxValue : (int)window;
    }

    private void ResetLossState()
    {
        _isInLoss = false;
        _alertRaised = false;
        _consecutiveLoss = 0;
        _lossStartedAtMilliseconds = 0;
    }

    private static IcmpMonitorEvent CreateEvent(
        IcmpMonitorEventKind kind,
        IcmpProbeResult result,
        TimeSpan? roundTripTime,
        int consecutiveLoss,
        int estimatedLossWindowMilliseconds,
        string? message)
    {
        return new IcmpMonitorEvent(
            kind,
            result.Timestamp,
            result.SequenceNumber,
            roundTripTime,
            consecutiveLoss,
            estimatedLossWindowMilliseconds,
            message);
    }
}
