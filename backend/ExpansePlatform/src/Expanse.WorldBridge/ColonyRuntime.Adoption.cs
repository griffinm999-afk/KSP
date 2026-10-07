using System.Linq;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private static void AddAdoptionCapabilities(ColonyManagementSnapshot snapshot, ColonyEnvironment environment, ColonyRecord colony)
        {
            foreach (var owned in colony.Facilities.Where(f => ColonyEngine.IsAdoptedHabitatIdentity(f) &&
                (f.Qualification.Provider == ColonyEngine.AdoptedHabitatProvider || environment.AdoptableFacilities.Any(observed =>
                    observed.Id == f.Id && observed.Qualification.Provider == ColonyEngine.AdoptedHabitatProvider))))
            {
                var home = ColonyEngine.QuoteAdoptedHabitat(snapshot.State,colony.Id,owned.Id,environment);
                snapshot.Capabilities.Add(new ColonyManagementCapability { Kind="qualifyAdoptedHabitat", ColonyId=colony.Id, TargetId=owned.Id,
                    Label="Qualify reviewed habitat homes", Available=home.CanApprove, Reason=home.Reason });
            }
            double radius;
            if (!environment.BodyRadiiMeters.TryGetValue(colony.Site.Body, out radius) || !Finite(radius) || radius <= 0) return;
            // Registered sites cannot overlap. Offer each observed candidate only
            // at its physical site, rather than multiplying every candidate by
            // every colony in the bounded management response.
            foreach (var facility in snapshot.AdoptableFacilities.Where(f=>!snapshot.State.Colonies.Any(c=>c.Facilities.Any(owned=>owned.VesselId==f.VesselId))))
            {
                ColonySite site;
                if (!environment.FacilitySites.TryGetValue(facility.Id, out site) || site.Body != colony.Site.Body) continue;
                double distance = ColonyEngine.SurfaceDistance(site, colony.Site, radius);
                if (!Finite(distance) || distance > colony.Site.RadiusMeters) continue;
                var quote = ColonyEngine.QuoteFacilityAdoption(snapshot.State, colony.Id, facility.Id, environment);
                snapshot.Capabilities.Add(new ColonyManagementCapability
                {
                    Kind = "adoptFacility", ColonyId = colony.Id, TargetId = facility.Id,
                    Label = "Adopt reviewed facility", Available = quote.CanApprove, Reason = quote.Reason
                });
            }
        }
    }
}
