using System;
using System.Collections.Generic;
using System.Linq;
using Expanse.TrackingStation.Core;

namespace Expanse.TrackingStation.Adapter
{
    /// <summary>
    /// A main-thread, read-only snapshot of the stock tracking lists.  It deliberately
    /// contains no Vessel or CelestialBody references so a refresh cannot make an old
    /// row authoritative after KSP rebuilds FlightGlobals.
    /// </summary>
    public sealed class KspTrackingSnapshot
    {
        public readonly BodySummary[] Bodies;
        public readonly VesselSummary[] Vessels;
        public readonly DateTime CapturedUtc;
        public readonly bool IsAvailable;
        public readonly string Status;

        public KspTrackingSnapshot(IEnumerable<BodySummary> bodies, IEnumerable<VesselSummary> vessels, bool isAvailable = true, string status = null)
        {
            Bodies = (bodies ?? Enumerable.Empty<BodySummary>()).Where(x => x != null).ToArray();
            Vessels = (vessels ?? Enumerable.Empty<VesselSummary>()).Where(x => x != null).ToArray();
            CapturedUtc = DateTime.UtcNow;
            IsAvailable = isAvailable;
            Status = status ?? string.Empty;
        }
    }

    /// <summary>Converts KSP objects to pure tracking summaries. Call from the main thread.</summary>
    public static class KspSnapshotAdapter
    {
        public static KspTrackingSnapshot Capture()
        {
            var available = true;
            var availabilityStatus = string.Empty;
            var bodies = new List<CelestialBody>();
            try
            {
                if (FlightGlobals.Bodies != null) bodies.AddRange(FlightGlobals.Bodies.Where(x => x != null));
                else { available = false; availabilityStatus = "Body data is still loading."; }
            }
            catch (Exception ex) { return new KspTrackingSnapshot(new BodySummary[0], new VesselSummary[0], false, "Tracking data unavailable: " + ex.Message); }

            var keys = BuildBodyKeys(bodies);
            var summaries = new List<BodySummary>(bodies.Count);
            foreach (var body in bodies)
            {
                var key = keys[body];
                var parent = string.Empty;
                try
                {
                    var reference = body.referenceBody;
                    if (reference != null && !ReferenceEquals(reference, body) && keys.ContainsKey(reference))
                        parent = keys[reference];
                }
                catch { }
                summaries.Add(new BodySummary(key, BodyLabel(body), parent, SafeIsStar(body)));
            }

            var vessels = new List<VesselSummary>();
            try
            {
                if (FlightGlobals.Vessels != null)
                {
                    foreach (var vessel in FlightGlobals.Vessels)
                    {
                        if (vessel == null) continue;
                        string id = string.Empty;
                        try { id = vessel.id.ToString(); } catch { }
                        if (string.IsNullOrEmpty(id)) continue;
                        string bodyKey = string.Empty;
                        try { if (vessel.mainBody != null && keys.ContainsKey(vessel.mainBody)) bodyKey = keys[vessel.mainBody]; } catch { }
                        string type = SafeEnum(vessel.vesselType);
                        string situation = SafeEnum(vessel.situation);
                        bool crewKnown;
                        int crew = SafeCrew(vessel, out crewKnown);
                        string name = string.Empty;
                        try { name = vessel.vesselName ?? string.Empty; } catch { }
                        vessels.Add(new VesselSummary(id, name, bodyKey, type, situation, crew, crewKnown));
                    }
                }
                else { available = false; availabilityStatus = "Vessel data is still loading."; }
            }
            catch (Exception ex) { available = false; availabilityStatus = "Tracking data unavailable: " + ex.Message; }
            return new KspTrackingSnapshot(summaries, vessels, available, availabilityStatus);
        }

        private static Dictionary<CelestialBody, string> BuildBodyKeys(IList<CelestialBody> bodies)
        {
            var result = new Dictionary<CelestialBody, string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var body in bodies)
            {
                string canonical = string.Empty;
                try { canonical = body.bodyName; } catch { }
                if (string.IsNullOrEmpty(canonical)) canonical = "body";
                var key = canonical;
                // Keep room for the explicit hierarchy's synthetic unknown branch.
                if (string.Equals(key, BodyHierarchy.UnknownBodyKey, StringComparison.OrdinalIgnoreCase)) key = canonical + "#body";
                if (!used.Add(key))
                {
                    int index = 0;
                    try { index = body.flightGlobalsIndex; } catch { }
                    key = canonical + "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var suffix = 2;
                    while (!used.Add(key)) key = canonical + "#" + (index + suffix++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                result[body] = key;
            }
            return result;
        }

        private static string BodyLabel(CelestialBody body)
        {
            try
            {
                var label = body.GetDisplayName();
                if (!string.IsNullOrEmpty(label)) return label.LocalizeRemoveGender();
            }
            catch { }
            try { return (body.displayName ?? body.bodyName ?? "Unknown body").LocalizeRemoveGender(); } catch { return "Unknown body"; }
        }

        private static bool SafeIsStar(CelestialBody body) { try { return body.isStar; } catch { return false; } }

        private static string SafeEnum<T>(T value)
        {
            try { return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"; }
            catch { return "Unknown"; }
        }

        private static int SafeCrew(Vessel vessel, out bool known)
        {
            try { known = true; return vessel.GetCrewCount(); }
            catch { known = false; return 0; }
        }

        public static Vessel ResolveVessel(string vesselId)
        {
            if (string.IsNullOrEmpty(vesselId)) return null;
            try
            {
                return (FlightGlobals.Vessels ?? new List<Vessel>()).FirstOrDefault(v => v != null && v.id.ToString() == vesselId);
            }
            catch { return null; }
        }

        public static CelestialBody ResolveBody(string bodyKey)
        {
            try
            {
                var bodies=(FlightGlobals.Bodies??new List<CelestialBody>()).Where(b=>b!=null).ToList();
                return BuildBodyKeys(bodies).FirstOrDefault(pair=>string.Equals(pair.Value,bodyKey,StringComparison.OrdinalIgnoreCase)).Key;
            }
            catch { return null; }
        }
    }
}
