using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // The reviewed adapter is a dedicated KPBS cabin, not arbitrary crew capacity.
        static bool DeployedAdoptedHome(Part part)
        {
            if (part == null || part.partInfo == null || part.partInfo.name != "KKAOSS.Habitat.MK2.g" || part.CrewCapacity != 4 || part.protoModuleCrew.Count > 4) return false;
            var modules = part.Modules.Cast<PartModule>().Where(m => m.moduleName == "PlanetaryModule").ToArray();
            if (modules.Length != 1 || modules[0].GetType().FullName != "PlanetarySurfaceStructures.PlanetaryModule") return false;
            var deployment = modules[0];
            return Convert.ToString(UtilityRead(deployment, "moduleStatus"), CultureInfo.InvariantCulture) == "Deployed" &&
                UtilityRead(deployment, "hasBeenInitialized") is bool && (bool)UtilityRead(deployment, "hasBeenInitialized") &&
                Convert.ToInt32(UtilityRead(deployment, "crewCapacityDeployed"), CultureInfo.InvariantCulture) == 4;
        }

        static bool ObserveAdoptedHomes(ColonyFacility facility, Vessel vessel, out List<uint> homes)
        {
            homes = new List<uint>();
            if (!ColonyEngine.IsAdoptedHabitatIdentity(facility) || vessel == null || !vessel.Landed || vessel.Splashed || vessel.isEVA || vessel.mainBody == null || vessel.id.ToString("D") != facility.VesselId) return false;
            uint[] members;
            if (vessel.loaded && vessel.parts != null)
            {
                var parts = vessel.parts.Where(p => p != null).ToArray();
                if (parts.Length != vessel.parts.Count || parts.Any(p => p.vessel != vessel)) return false;
                members = parts.Select(p => p.persistentId).ToArray();
                homes = parts.Where(DeployedAdoptedHome).Select(p => p.persistentId).OrderBy(id => id).ToList();
            }
            else if (vessel.protoVessel != null)
            {
                var parts = vessel.protoVessel.protoPartSnapshots;
                if (parts.Any(p => p == null || p.partInfo == null || p.partInfo.partPrefab == null)) return false;
                members = parts.Select(p => p.persistentId).ToArray();
                foreach (var p in parts.Where(p => p.partInfo.name == "KKAOSS.Habitat.MK2.g"))
                {
                    var modules = p.modules.Where(m => m.moduleName == "PlanetaryModule").ToArray();
                    double animation; bool initialized;
                    if (modules.Length != 1 || !double.TryParse(modules[0].moduleValues.GetValue("animationTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out animation) ||
                        !Finite(animation) || animation < .999 || animation > 1 || !bool.TryParse(modules[0].moduleValues.GetValue("hasBeenInitialized"), out initialized) || !initialized) continue;
                    // Same native four-seat mapping used by the existing saved KPBS observer.
                    var native = p.partInfo.partPrefab.Modules.Cast<PartModule>().Where(m => m.moduleName == "PlanetaryModule").ToArray();
                    if (native.Length != 1 || native[0].GetType().FullName != "PlanetarySurfaceStructures.PlanetaryModule" || Convert.ToInt32(UtilityRead(native[0], "crewCapacityDeployed"), CultureInfo.InvariantCulture) != 4 || p.protoModuleCrew.Count > 4) continue;
                    homes.Add(p.persistentId);
                }
                homes.Sort();
            }
            else return false;
            return members.Length > 0 && members.Length <= 512 && members.All(id => id != 0) && members.Distinct().Count() == members.Length &&
                facility.PartIds.OrderBy(id => id).SequenceEqual(members.OrderBy(id => id)) && homes.Count > 0 && homes.Count <= Math.Min(256, ColonyLimits.Residents / 4);
        }

        private void PopulateAdoptedHabitatWitnesses(ColonyEnvironment env)
        {
            foreach (var facility in state.Colonies.SelectMany(c => c.Facilities).Where(ColonyEngine.IsAdoptedHabitatIdentity))
            {
                var candidate = env.AdoptableFacilities.SingleOrDefault(f => f.Id == facility.Id && f.VesselId == facility.VesselId);
                var vessel = FlightGlobals.Vessels.SingleOrDefault(v => v != null && v.id.ToString("D") == facility.VesselId);
                List<uint> homes;
                if (candidate == null || vessel == null || !vessel.loaded || !ObserveAdoptedHomes(facility, vessel, out homes)) continue;
                var report = env.Services.Utilities.SingleOrDefault(r => r.FacilityId == facility.Id && r.VesselId == facility.VesselId && r.ContextKey == env.ContextKey && r.ObservedUt <= env.Ut && env.Ut - r.ObservedUt <= 10);
                // Detach discovery rows; refreshing display evidence must not mutate a saved facility.
                var observed = new ColonyFacility { Id = candidate.Id, VesselId = candidate.VesselId, Name = candidate.Name, PartIds = candidate.PartIds.ToList(),
                    CertifiedHomes = checked(homes.Count * 4), HomePartPersistentIds = homes,
                    Qualification = new ColonyQualification { Provider = ColonyEngine.AdoptedHabitatProvider, Context = vessel.packed ? "loaded-packed" : "loaded-unpacked", ObservedUt = env.Ut,
                        HousingCertified = true, PlacementStable = true, PowerReliable = ColonyUtilityQualification.PowerSupported(report), HeatSafe = ColonyUtilityQualification.HeatSupported(report),
                        InputsAccessible = report != null && report.InputsAccessible, BackgroundSupported = report != null && report.BackgroundProviderQualified,
                        EvidenceHash = "" } };
                observed.HomePartCertificationHash = ColonyEngine.HomeMappingHash(observed);
                // Rates/temperatures are rechecked live, not frozen into a quote
                // that would expire merely because safe native simulation advances.
                observed.Qualification.EvidenceHash = report == null ? "" : observed.HomePartCertificationHash;
                env.AdoptableFacilities[env.AdoptableFacilities.IndexOf(candidate)] = observed;
            }
        }

        private bool HasAdoptedHousingPart(ColonyFacility facility, Vessel vessel, uint id, ColonyEnvironment env)
        {
            if (facility.Qualification.Provider != ColonyEngine.AdoptedHabitatProvider || !ColonyEngine.IsAdoptedHabitatIdentity(facility)) return false;
            var colony = state.Colonies.SingleOrDefault(c => c.Facilities.Any(f => f.Id == facility.Id));
            List<uint> homes;
            if (colony == null || vessel.mainBody == null || colony.Site.Body != vessel.mainBody.bodyName || !ObserveAdoptedHomes(facility, vessel, out homes) ||
                !homes.SequenceEqual(facility.HomePartPersistentIds.OrderBy(p => p)) || facility.CertifiedHomes != checked(homes.Count * 4) || !homes.Contains(id)) return false;
            var site = new ColonySite { Body = vessel.mainBody.bodyName, Latitude = vessel.latitude, Longitude = vessel.longitude };
            return Finite(site.Latitude) && Finite(site.Longitude) && ColonyEngine.SurfaceDistance(site, colony.Site, vessel.mainBody.Radius) <= colony.Site.RadiusMeters;
        }
    }
}
