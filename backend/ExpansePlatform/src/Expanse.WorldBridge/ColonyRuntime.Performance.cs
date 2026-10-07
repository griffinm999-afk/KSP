using System;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        readonly ColonyRuntimeProfiler performance = new ColonyRuntimeProfiler(
            Environment.GetCommandLineArgs().Any(a => a == "-expanseColonyProfile"));
        float performanceStartRealtime;

        // Read-only diagnostics invoked on Unity's thread by the isolated harness.
        // These never serialize into, advance or mutate the economic save ledger.
        public void ResetPerformanceSamples()
        { performance.Reset(); performanceStartRealtime = Time.realtimeSinceStartup; }

        public ConfigNode CapturePerformanceSamples()
        {
            var result = new ConfigNode("COLONY_PERFORMANCE");
            result.AddValue("enabled", performance.Enabled);
            result.AddValue("context", ContextKey);
            result.AddValue("scene", HighLogic.LoadedScene);
            result.AddValue("paused", FlightDriver.Pause || Time.timeScale <= 0);
            result.AddValue("warpRate", TimeWarp.CurrentRate.ToString("R", CultureInfo.InvariantCulture));
            result.AddValue("ut", Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            result.AddValue("elapsedRealtimeSeconds", (Time.realtimeSinceStartup - performanceStartRealtime).ToString("R", CultureInfo.InvariantCulture));
            result.AddValue("colonyCount", state == null ? 0 : state.Colonies.Count);
            result.AddValue("facilityCount", state == null ? 0 : state.Colonies.Sum(c => c.Facilities.Count));
            result.AddValue("residentCount", state == null ? 0 : state.Colonies.Sum(c => c.Residents.Count));
            result.AddValue("loadedVesselCount", FlightGlobals.Vessels == null ? 0 : FlightGlobals.Vessels.Count(v => v != null && v.loaded));
            result.AddValue("sampleWindowCapacity", ColonyPerformanceSamples.Capacity);
            result.AddValue("interpretation", "Inclusive elapsed time; nested channels overlap. ActiveTick excludes idle and paused callbacks. Channels keep independent latest windows; compare observed/retained counts. FrameInterval is the whole game, not attributable colony cost. No threshold implies qualification without matched baseline and scale evidence.");
            for (int i = 0; i < (int)ColonyPerformanceChannel.Count; i++)
            {
                var channel = (ColonyPerformanceChannel)i;
                var s = performance.Summary(channel);
                var row = result.AddNode("METRIC");
                row.AddValue("name", channel.ToString());
                row.AddValue("observed", s.Observed); row.AddValue("retained", s.Retained); row.AddValue("overwritten", s.Overwritten);
                AddMilliseconds(row, "meanMs", s.MeanMilliseconds);
                AddMilliseconds(row, "p50Ms", s.P50Milliseconds);
                AddMilliseconds(row, "p95Ms", s.P95Milliseconds);
                AddMilliseconds(row, "p99Ms", s.P99Milliseconds);
                AddMilliseconds(row, "windowMaxMs", s.WindowMaxMilliseconds);
                AddMilliseconds(row, "lifetimeMaxMs", s.LifetimeMaxMilliseconds);
            }
            return result;
        }
        static void AddMilliseconds(ConfigNode row, string name, double value)
        { row.AddValue(name, value.ToString("R", CultureInfo.InvariantCulture)); }
    }
}
