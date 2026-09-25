namespace WgbDiagnostics.Core.Realtime;

public enum GraphViewportState
{
    Autoscroll,
    ManualView,
    Paused
}

public sealed record GraphAxisLimits(double MinimumX, double MaximumX)
{
    public double Width => MaximumX - MinimumX;
}

public sealed record GraphRenderPlan(
    GraphViewportState State,
    GraphAxisLimits XLimits,
    bool ShouldRenderPlots,
    TimeSpan VisibleWindow);

public sealed class GraphViewportModel
{
    private GraphViewportState _resumeState = GraphViewportState.Autoscroll;
    private GraphAxisLimits? _manualLimits;
    private GraphAxisLimits? _lastLimits;

    public GraphViewportModel(TimeSpan? visibleWindow = null)
    {
        VisibleWindow = visibleWindow ?? TimeSpan.FromMinutes(10);
    }

    public GraphViewportState State { get; private set; } = GraphViewportState.Autoscroll;

    public TimeSpan VisibleWindow { get; private set; }

    public GraphAxisLimits? LastLimits => _lastLimits;

    public void ConfigureVisibleWindow(
        TimeSpan visibleWindow,
        double nowX,
        bool resetToAutoscroll)
    {
        VisibleWindow = visibleWindow <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(1)
            : visibleWindow;

        if (resetToAutoscroll || State == GraphViewportState.Autoscroll)
        {
            ResetZoom(nowX);
        }
    }

    public void SetManualView(GraphAxisLimits limits)
    {
        if (limits.MaximumX <= limits.MinimumX)
        {
            return;
        }

        _manualLimits = limits;
        _lastLimits = limits;
        State = GraphViewportState.ManualView;
        _resumeState = GraphViewportState.ManualView;
    }

    public void Pause()
    {
        if (State == GraphViewportState.Paused)
        {
            return;
        }

        _resumeState = State;
        State = GraphViewportState.Paused;
    }

    public void Resume()
    {
        if (State != GraphViewportState.Paused)
        {
            return;
        }

        State = _resumeState;
    }

    public void ResetZoom(double nowX)
    {
        var limits = CreateAutoscrollLimits(nowX);
        _manualLimits = null;
        _lastLimits = limits;
        State = GraphViewportState.Autoscroll;
        _resumeState = GraphViewportState.Autoscroll;
    }

    public GraphRenderPlan CreateRenderPlan(double nowX)
    {
        if (State == GraphViewportState.Paused)
        {
            var pausedLimits = _lastLimits ?? CreateAutoscrollLimits(nowX);
            return new GraphRenderPlan(State, pausedLimits, ShouldRenderPlots: false, VisibleWindow);
        }

        var limits = State == GraphViewportState.ManualView && _manualLimits is not null
            ? _manualLimits
            : CreateAutoscrollLimits(nowX);
        _lastLimits = limits;
        return new GraphRenderPlan(State, limits, ShouldRenderPlots: true, VisibleWindow);
    }

    private GraphAxisLimits CreateAutoscrollLimits(double nowX)
    {
        return new GraphAxisLimits(nowX - VisibleWindow.TotalDays, nowX);
    }
}
