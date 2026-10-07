using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.WorldBridge
{
    internal enum ColonyAdoptionKind { Unknown, Hardware, NaturalLode }
    internal sealed class ColonyAdoptionObservation
    {
        internal ColonyAdoptionKind Kind;
        internal List<uint> PartIds = new List<uint>();
    }

    // Native surveyors can create proto parts with no saved MODULE nodes. Their
    // installed prefab still identifies the deposit. A resource name, vessel
    // name or part-name prefix alone is never a natural-deposit witness.
    internal static class ColonyAdoptionEligibility
    {
        internal static ColonyAdoptionObservation Observe(Vessel vessel)
        {
            var result = new ColonyAdoptionObservation();
            if (vessel == null) return result;
            var kinds = new List<ColonyAdoptionKind>();
            if (vessel.loaded)
            {
                if (vessel.parts == null) return result;
                foreach (var part in vessel.parts.Take(513))
                {
                    if (part == null || part.Modules == null || part.Resources == null) return result;
                    result.PartIds.Add(part.persistentId);
                    kinds.Add(PartKind(part.partInfo, part.Modules.Cast<PartModule>().Select(m => m == null ? null : m.moduleName),
                        part.Resources.Cast<PartResource>().Select(r => r == null ? null : r.resourceName)));
                }
            }
            else
            {
                if (vessel.protoVessel == null || vessel.protoVessel.protoPartSnapshots == null) return result;
                foreach (var part in vessel.protoVessel.protoPartSnapshots.Take(513))
                {
                    if (part == null || part.modules == null || part.resources == null) return result;
                    result.PartIds.Add(part.persistentId);
                    kinds.Add(PartKind(part.partInfo, part.modules.Select(m => m == null ? null : m.moduleName),
                        part.resources.Select(r => r == null ? null : r.resourceName)));
                }
            }
            if (result.PartIds.Count == 0 || result.PartIds.Count > 512 || result.PartIds.Any(id => id == 0) ||
                result.PartIds.Distinct().Count() != result.PartIds.Count || kinds.Any(k => k == ColonyAdoptionKind.Unknown)) return result;
            // A real vessel containing a harvester or other hardware is not a
            // natural deposit just because it also carries ResourceLode.
            result.Kind = kinds.All(k => k == ColonyAdoptionKind.NaturalLode) ? ColonyAdoptionKind.NaturalLode : ColonyAdoptionKind.Hardware;
            return result;
        }

        private static ColonyAdoptionKind PartKind(AvailablePart info, IEnumerable<string> currentModules, IEnumerable<string> currentResources)
        {
            if (info == null || info.partPrefab == null || info.partPrefab.Modules == null || info.partConfig == null) return ColonyAdoptionKind.Unknown;
            var native = info.partPrefab.Modules.Cast<PartModule>().Take(513).ToArray();
            var modules = currentModules.Take(513).ToArray();
            var resources = currentResources.Take(513).ToArray();
            if (native.Length > 512 || modules.Length > 512 || resources.Length > 512 || native.Any(m => m == null) ||
                modules.Any(string.IsNullOrEmpty) || resources.Any(string.IsNullOrEmpty)) return ColonyAdoptionKind.Unknown;
            bool controller = native.Any(m => m.GetType().FullName == "KolonyTools.ModuleResourceLode" && m.GetType().Assembly.GetName().Name == "KolonyTools");
            bool namedController = native.Any(m => m.moduleName == "ModuleResourceLode") || modules.Any(m => m == "ModuleResourceLode") ||
                info.partConfig.GetNodes("MODULE").Any(n => n.GetValue("name") == "ModuleResourceLode");
            if (controller)
            {
                // The native controller takes its first physical resource. Bind
                // its configured/current ResourceLode tank, not converter inputs.
                bool configured = info.partConfig.GetNodes("RESOURCE").Any(n => n.GetValue("name") == "ResourceLode");
                return configured && resources.Contains("ResourceLode") ? ColonyAdoptionKind.NaturalLode : ColonyAdoptionKind.Unknown;
            }
            return namedController ? ColonyAdoptionKind.Unknown : ColonyAdoptionKind.Hardware;
        }
    }
}
