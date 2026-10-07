using System;
using System.Diagnostics;

namespace Expanse.WorldBridge
{
    // Fixed storage, no allocation on the measured path. Report generation is
    // deliberately outside that path. These are inclusive wall-clock samples;
    // nested channels must never be added together as separate CPU costs.
    internal sealed class ColonyPerformanceSamples
    {
        internal const int Capacity = 4096;
        readonly double[] values = new double[Capacity];
        int cursor, count;
        long observed;
        double lifetimeMax;

        internal void Record(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            values[cursor] = milliseconds;
            cursor = (cursor + 1) % Capacity;
            if (count < Capacity) count++;
            observed++;
            if (milliseconds > lifetimeMax) lifetimeMax = milliseconds;
        }

        internal void Reset() { cursor = count = 0; observed = 0; lifetimeMax = 0; }

        internal ColonyPerformanceSummary Summarize()
        {
            var sorted = new double[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);
            double total = 0;
            for (int i = 0; i < count; i++) total += sorted[i];
            return new ColonyPerformanceSummary
            {
                Observed = observed, Retained = count, Overwritten = observed - count,
                MeanMilliseconds = count == 0 ? 0 : total / count,
                P50Milliseconds = Percentile(sorted, .50),
                P95Milliseconds = Percentile(sorted, .95),
                P99Milliseconds = Percentile(sorted, .99),
                WindowMaxMilliseconds = count == 0 ? 0 : sorted[count - 1],
                LifetimeMaxMilliseconds = lifetimeMax
            };
        }

        static double Percentile(double[] sorted, double fraction)
        { return sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)]; }
    }

    internal sealed class ColonyPerformanceSummary
    {
        public long Observed, Overwritten;
        public int Retained;
        public double MeanMilliseconds, P50Milliseconds, P95Milliseconds, P99Milliseconds,
            WindowMaxMilliseconds, LifetimeMaxMilliseconds;
    }

    internal enum ColonyPerformanceChannel
    {
        FrameCallback, ActiveTick, ManagementEndpoint, Discovery, Environment,
        SimulationAndAcceptance, AcceptanceSerialization, FrameInterval,
        EnvironmentPlanning, EnvironmentWolf, EnvironmentUtilities, EnvironmentPeople,
        EnvironmentSupport, EnvironmentServices, EnvironmentConstruction,
        EnvironmentPlanningPolicy, EnvironmentPhysicalProcurement,
        UtilitySources, UtilityModules, UtilityBackground, UtilityInputs, EnvironmentProduction,
        UtilityCatchUp, UtilityBackgroundAdapters, UtilityBackgroundRecipes, UtilityConfigurationQuery, Count
    }

    internal sealed class ColonyRuntimeProfiler
    {
        readonly ColonyPerformanceSamples[] channels;
        internal readonly bool Enabled;
        internal ColonyRuntimeProfiler(bool enabled)
        {
            Enabled = enabled;
            if (!enabled) { channels = Array.Empty<ColonyPerformanceSamples>(); return; }
            channels = new ColonyPerformanceSamples[(int)ColonyPerformanceChannel.Count];
            for (int i = 0; i < channels.Length; i++) channels[i] = new ColonyPerformanceSamples();
        }
        internal long Start() { return Enabled ? Stopwatch.GetTimestamp() : 0; }
        internal void End(ColonyPerformanceChannel channel, long start)
        {
            if (Enabled) channels[(int)channel].Record((Stopwatch.GetTimestamp() - start) * (1000.0 / Stopwatch.Frequency));
        }
        internal void RecordFrameInterval(double seconds)
        { if (Enabled) channels[(int)ColonyPerformanceChannel.FrameInterval].Record(seconds * 1000); }
        internal ColonyPerformanceSummary Summary(ColonyPerformanceChannel channel)
        { return Enabled ? channels[(int)channel].Summarize() : new ColonyPerformanceSummary(); }
        internal void Reset()
        { if (Enabled) foreach (var channel in channels) channel.Reset(); }
    }
}
