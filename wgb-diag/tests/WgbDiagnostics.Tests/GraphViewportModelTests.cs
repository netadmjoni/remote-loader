using WgbDiagnostics.Core.Configuration;
using WgbDiagnostics.Core.Realtime;
using Xunit;

namespace WgbDiagnostics.Tests;

public sealed class GraphViewportModelTests
{
    [Fact]
    public void DefaultWindowIsTenMinutes()
    {
        var model = new GraphViewportModel();
        var plan = model.CreateRenderPlan(nowX: 10);

        Assert.Equal(GraphViewportState.Autoscroll, plan.State);
        Assert.Equal(TimeSpan.FromMinutes(10), plan.VisibleWindow);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalDays, plan.XLimits.Width, precision: 10);
    }

    [Fact]
    public void GraphVisibleMinutesFromOptionsCreatesMatchingWindow()
    {
        var options = WgbDiagnosticsOptions.CreateDefault();
        options.GraphVisibleMinutes = 5;

        var graphOptions = RealtimeGraphOptions.FromDiagnosticsOptions(options);

        Assert.Equal(TimeSpan.FromMinutes(5), graphOptions.VisibleWindow);
    }

    [Fact]
    public void PresetWindowCanResetToAutoscrollWhileMonitoring()
    {
        var model = new GraphViewportModel(TimeSpan.FromMinutes(10));
        model.SetManualView(new GraphAxisLimits(1, 2));

        model.ConfigureVisibleWindow(TimeSpan.FromMinutes(30), nowX: 10, resetToAutoscroll: true);
        var plan = model.CreateRenderPlan(nowX: 10);

        Assert.Equal(GraphViewportState.Autoscroll, plan.State);
        Assert.Equal(TimeSpan.FromMinutes(30), plan.VisibleWindow);
        Assert.Equal(TimeSpan.FromMinutes(30).TotalDays, plan.XLimits.Width, precision: 10);
    }

    [Fact]
    public void ManualViewKeepsSynchronizedAxisLimits()
    {
        var model = new GraphViewportModel();
        var limits = new GraphAxisLimits(5, 6);

        model.SetManualView(limits);
        var plan = model.CreateRenderPlan(nowX: 10);

        Assert.Equal(GraphViewportState.ManualView, plan.State);
        Assert.Equal(limits, plan.XLimits);
    }

    [Fact]
    public void PauseAndResumePreservesManualView()
    {
        var model = new GraphViewportModel();
        var limits = new GraphAxisLimits(5, 6);
        model.SetManualView(limits);

        model.Pause();
        var paused = model.CreateRenderPlan(nowX: 10);
        model.Resume();
        var resumed = model.CreateRenderPlan(nowX: 10);

        Assert.Equal(GraphViewportState.Paused, paused.State);
        Assert.False(paused.ShouldRenderPlots);
        Assert.Equal(GraphViewportState.ManualView, resumed.State);
        Assert.True(resumed.ShouldRenderPlots);
        Assert.Equal(limits, resumed.XLimits);
    }

    [Fact]
    public void ResetZoomRestoresAutoscrollWindow()
    {
        var model = new GraphViewportModel(TimeSpan.FromMinutes(10));
        model.SetManualView(new GraphAxisLimits(1, 2));

        model.ResetZoom(nowX: 20);
        var plan = model.CreateRenderPlan(nowX: 20);

        Assert.Equal(GraphViewportState.Autoscroll, plan.State);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalDays, plan.XLimits.Width, precision: 10);
        Assert.Equal(20, plan.XLimits.MaximumX);
    }
}
