using Expanse.WorldBridge;

namespace Expanse.Tests;

public class ColonyPerformanceTests
{
    [Fact]
    public void OutliersAreNotLostWhenWindowWraps()
    {
        var samples = new ColonyPerformanceSamples();
        samples.Record(10000);
        for (int i = 1; i <= ColonyPerformanceSamples.Capacity; i++) samples.Record(i);
        var report = samples.Summarize();
        Assert.Equal(4097, report.Observed);
        Assert.Equal(4096, report.Retained);
        Assert.Equal(1, report.Overwritten);
        Assert.Equal(3892, report.P95Milliseconds);
        Assert.Equal(4056, report.P99Milliseconds);
        Assert.Equal(4096, report.WindowMaxMilliseconds);
        Assert.Equal(10000, report.LifetimeMaxMilliseconds);
    }

    [Fact]
    public void ResetSeparatesMatchedQualificationRuns()
    {
        var samples = new ColonyPerformanceSamples();
        samples.Record(10000); samples.Reset(); samples.Record(.4);
        var report = samples.Summarize();
        Assert.Equal(1, report.Observed); Assert.Equal(0, report.Overwritten);
        Assert.Equal(.4, report.P99Milliseconds); Assert.Equal(.4, report.LifetimeMaxMilliseconds);
    }

    [Fact]
    public void InvalidDurationsCannotContaminateQualification()
    {
        var samples = new ColonyPerformanceSamples();
        foreach (double value in new[] {double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d}) samples.Record(value);
        samples.Record(0); samples.Record(2);
        var report = samples.Summarize();
        Assert.Equal(2, report.Observed); Assert.Equal(1, report.MeanMilliseconds); Assert.Equal(2, report.P99Milliseconds);
    }

    [Fact]
    public void DisabledProfilingRecordsNoMeasurements()
    {
        var profiler = new ColonyRuntimeProfiler(false);
        Assert.Equal(0, profiler.Start());
        profiler.End(ColonyPerformanceChannel.ActiveTick, 0); profiler.RecordFrameInterval(.1);
        Assert.Equal(0, profiler.Summary(ColonyPerformanceChannel.ActiveTick).Observed);
        Assert.Equal(0, profiler.Summary(ColonyPerformanceChannel.FrameInterval).Observed);
    }

    [Fact]
    public void IdleFrameSamplesDoNotDiluteActiveTickPercentiles()
    {
        var profiler = new ColonyRuntimeProfiler(true);
        for (int i = 0; i < 5000; i++) profiler.RecordFrameInterval(.016);
        Assert.Equal(5000, profiler.Summary(ColonyPerformanceChannel.FrameInterval).Observed);
        Assert.Equal(0, profiler.Summary(ColonyPerformanceChannel.ActiveTick).Observed);
    }
}
