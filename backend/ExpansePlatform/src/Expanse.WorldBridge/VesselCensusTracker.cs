using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.WorldBridge
{
    internal sealed class ColonyVesselCensus
    {
        public string Status = "unavailable";
        public string Reason = "";
        public string[] VesselIds = new string[0];
        public long ObservationSequence;
    }

    // A complete census is a statement about the entire loaded save, not about
    // the bounded settlement/resource scan. Require both KSP identity lists to
    // agree twice in one scene before publishing it. The attempt sequence spans
    // scene changes within a world/session/load epoch so revisiting a scene
    // cannot reuse a lower sequence number.
    internal sealed class VesselCensusTracker
    {
        private const int MaxVessels = 512;
        private string currentEpochContext = "";
        private string previousScene = "";
        private string[] previousIds = new string[0];
        private bool previousReliable;
        private long observationSequence;

        internal void Invalidate()
        {
            previousReliable = false;
            previousIds = new string[0];
        }

        internal ColonyVesselCensus Observe(string epochContext, string scene, ICollection<Guid> worldIds,
            ICollection<Guid> saveIds, IEnumerable<string> observedColonyIds, long? attemptSequence = null)
        {
            if (!String.Equals(currentEpochContext, epochContext, StringComparison.Ordinal))
            {
                currentEpochContext = epochContext;
                previousIds = new string[0];
                previousReliable = false;
                observationSequence = 0;
            }
            if (!String.Equals(previousScene, scene, StringComparison.Ordinal))
            {
                previousScene = scene;
                previousIds = new string[0];
                previousReliable = false;
            }
            if (attemptSequence.HasValue && attemptSequence.Value != observationSequence + 1)
            { previousReliable = false; previousIds = new string[0]; }
            observationSequence = attemptSequence ?? observationSequence + 1;
            ColonyVesselCensus result = new ColonyVesselCensus { ObservationSequence = observationSequence };
            if (String.IsNullOrWhiteSpace(epochContext) || String.IsNullOrWhiteSpace(scene) ||
                worldIds == null || saveIds == null || observedColonyIds == null)
                return Fail(result, "World identity or a KSP vessel list is unavailable.");
            if (worldIds.Count > MaxVessels || saveIds.Count > MaxVessels)
            {
                result.Status = "truncated";
                return Fail(result, "The complete vessel census exceeds 512 identities.");
            }
            if (worldIds.Any(id => id == Guid.Empty) || saveIds.Any(id => id == Guid.Empty))
                return Fail(result, "A KSP vessel has no usable identity.");
            string[] world = worldIds.Select(id => id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            string[] saved = saveIds.Select(id => id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (world.Distinct(StringComparer.Ordinal).Count() != world.Length ||
                saved.Distinct(StringComparer.Ordinal).Count() != saved.Length ||
                !world.SequenceEqual(saved, StringComparer.Ordinal))
                return Fail(result, "KSP world and save vessel identities disagree.");
            HashSet<string> members = new HashSet<string>(world, StringComparer.Ordinal);
            if (observedColonyIds.Any(id => !members.Contains(id)))
                return Fail(result, "A settlement observation is outside the complete vessel census.");
            bool stable = previousReliable && previousIds.SequenceEqual(world, StringComparer.Ordinal);
            previousReliable = true;
            previousIds = world;
            if (!stable)
            {
                result.Reason = "Awaiting a second matching world census.";
                return result;
            }
            result.Status = "complete";
            result.VesselIds = world;
            return result;
        }

        private ColonyVesselCensus Fail(ColonyVesselCensus result, string reason)
        {
            previousReliable = false;
            previousIds = new string[0];
            result.Reason = reason;
            return result;
        }
    }
}
