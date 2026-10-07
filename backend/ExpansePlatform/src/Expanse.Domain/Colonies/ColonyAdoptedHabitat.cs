using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public const string AdoptedHabitatProvider = "KSP.AdoptedHabitat.v1";

        // This is a native observation of an already adopted dedicated habitat,
        // never a client-provided capacity or a substitute for paid placement.
        public static ColonyAdoptionQuote QuoteAdoptedHabitat(ColonyState state, string colonyId, string facilityId, ColonyEnvironment env)
        {
            var quote = new ColonyAdoptionQuote { ColonyId = colonyId, FacilityId = facilityId };
            try
            {
                ColonyStateCodec.Validate(state); ValidateEnvironment(state, env);
                var colony = Colony(state, colonyId);
                if (colony.Site.Body != "Minmus") throw new InvalidDataException("Ordinary adopted-home qualification currently supports Minmus only.");
                if (env.Ut < state.SimulatedUt) throw new InvalidDataException("Selected-save time moved backward; reload before qualifying homes.");
                var saved = colony.Facilities.SingleOrDefault(f => f.Id == facilityId);
                if (saved == null || !IsAdoptedHabitatIdentity(saved)) throw new InvalidDataException("Choose an adopted existing habitat; paid buildings keep their original qualification path.");
                if (state.Effects.Any(e => e.State == "applying" || e.State == "held")) throw new InvalidDataException("Resolve the current colony hold before qualifying homes.");
                RequireSupportOwner(colony, env, false);
                ColonyStateCodec.Text(env.Support.PolicyId, 128, true); ColonyStateCodec.Text(env.Support.PolicyHash, 128, true);
                if (env.AdoptableFacilities == null || env.AdoptableFacilities.Count > ColonyLimits.Facilities || env.FacilitySites == null)
                    throw new InvalidDataException("Current habitat observations are unavailable; refresh the selected save.");
                var matches = env.AdoptableFacilities.Where(f => f != null && f.Id == facilityId).Take(2).ToArray();
                if (matches.Length != 1 || !env.FacilitySites.TryGetValue(facilityId, out var site)) throw new InvalidDataException("Load the adopted habitat and refresh its current observation.");
                var candidate = matches[0]; var q = candidate.Qualification;
                if (candidate.PartIds == null || candidate.VesselId != saved.VesselId || !IsAdoptedHabitatIdentity(candidate) ||
                    !saved.PartIds.OrderBy(id => id).SequenceEqual(candidate.PartIds.OrderBy(id => id)) ||
                    state.Colonies.SelectMany(c => c.Facilities).Any(f => f.Id != saved.Id && (f.VesselId == saved.VesselId || f.PartIds.Any(saved.PartIds.Contains))))
                    throw new InvalidDataException("The adopted vessel or complete member parts changed; homes were not qualified.");
                AdoptionWitnessHash(candidate, site); // validates complete unique members and actual coordinates
                if (site.Body != colony.Site.Body || !env.BodyRadiiMeters.TryGetValue(site.Body, out var radius) || !FiniteAdoption(radius) || radius <= 0 || SurfaceDistance(site, colony.Site, radius) > colony.Site.RadiusMeters)
                    throw new InvalidDataException("The habitat is outside this colony's body or boundary.");
                if (q == null || q.Provider != AdoptedHabitatProvider || !q.HousingCertified || !q.PlacementStable ||
                    q.Context != "loaded-packed" && q.Context != "loaded-unpacked" || !FiniteAdoption(q.ObservedUt) || q.ObservedUt > env.Ut || env.Ut - q.ObservedUt > 10)
                    throw new InvalidDataException("Load a deployed KPBS Habitat MK2 with its actual four-seat cabin; work and command seats are not homes.");
                if (!q.PowerReliable || !q.HeatSafe || !q.InputsAccessible || string.IsNullOrWhiteSpace(q.EvidenceHash))
                    throw new InvalidDataException("The habitat needs current qualified power, heat and native inputs before its homes can be used.");
                var homes = candidate.HomePartPersistentIds;
                if (homes == null || homes.Count == 0 || homes.Count > 256 || homes.Any(id => id == 0 || !candidate.PartIds.Contains(id)) || homes.Distinct().Count() != homes.Count ||
                    candidate.CertifiedHomes != checked(homes.Count * 4) || candidate.CertifiedHomes > ColonyLimits.Residents || candidate.HomePartCertificationHash != HomeMappingHash(candidate))
                    throw new InvalidDataException("Dedicated home part identities or actual cabin capacity are incomplete.");
                if (env.People == null || !env.People.PresenceComplete || env.People.Seats == null || env.People.Seats.Count > 16384) throw new InvalidDataException("Current crew and cabin membership must be complete before homes are qualified.");
                foreach (var id in homes)
                {
                    var seats = env.People.Seats.Where(s => s != null && s.FacilityId == saved.Id && s.VesselId == saved.VesselId && s.PartId == id).Take(2).ToArray();
                    if (seats.Length != 1 || !seats[0].Current || seats[0].ContextKey != env.ContextKey || !seats[0].CrewMutationSupported || seats[0].Capacity != 4 ||
                        seats[0].Occupants == null || seats[0].Occupants.Count > 4 || seats[0].Occupants.Distinct(StringComparer.Ordinal).Count() != seats[0].Occupants.Count)
                        throw new InvalidDataException("The habitat's actual four-seat crew mapping is unavailable or changed.");
                }
                if (saved.CertifiedHomes > 0 && (saved.CertifiedHomes != candidate.CertifiedHomes || !saved.HomePartPersistentIds.OrderBy(id => id).SequenceEqual(homes.OrderBy(id => id))))
                    throw new InvalidDataException("Previously qualified home parts or capacity changed; existing resident bindings are preserved.");
                var terms = new StringBuilder(); AdoptionTerm(terms, "adopted-habitat-v2");
                AdoptionTerm(terms, AdoptionWitnessHash(candidate, site)); AdoptionTerm(terms, HomeMappingHash(candidate));
                AdoptionTerm(terms, candidate.CertifiedHomes.ToString(CultureInfo.InvariantCulture)); AdoptionTerm(terms, q.EvidenceHash);
                AdoptionTerm(terms, env.Support.PolicyHash); AdoptionTerm(terms, env.Support.MicroUnitsPerPersonDay.ToString(CultureInfo.InvariantCulture));
                quote.AdoptionWitnessHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms.ToString()));
                AdoptionTerm(terms, state.WorldId); AdoptionTerm(terms, env.ContextKey); AdoptionTerm(terms, state.Revision.ToString(CultureInfo.InvariantCulture)); AdoptionTerm(terms, colony.Id);
                AdoptionSiteTerms(terms, colony.Site);
                quote.Id = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms.ToString()));
                quote.Name = saved.Name; quote.VesselId = saved.VesselId; quote.PartIds = homes.OrderBy(id => id).ToList();
                quote.Site = new ColonySite { Body = site.Body, Biome = site.Biome, Latitude = site.Latitude, Longitude = site.Longitude, RadiusMeters = site.RadiusMeters };
                quote.CanApprove = true; quote.Reason = "Qualify " + candidate.CertifiedHomes + " existing habitat seats as homes. Pause KSP and refresh before reviewing and applying. No crew, supplies or funds are created. Purchase owned Supplies and commission resident support separately before immigration.";
            }
            catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is FormatException || e is OverflowException)
            { quote.Reason = e.Message; }
            return quote;
        }

        public static bool IsAdoptedHabitatIdentity(ColonyFacility f) => f != null && f.TemplateId == "" && f.TemplateHash == "" && f.CraftSha256 == "" &&
            f.ConstructionOrderId == "" && f.PlacementOperationId == "" && f.PlacementRequestFingerprint == "" && f.PlotId == "" && f.ProductionOwner == "physical";

        static string QualifyAdoptedHabitat(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            if (command.Fields == null || command.Fields.Count != 1 || !command.Fields.ContainsKey("HabitatWitnessHash") || command.FoundingIntent != null)
                throw new InvalidDataException("Habitat qualification accepts only its reviewed native witness; capacity, crew and resource fields are not accepted.");
            var quote = QuoteAdoptedHabitat(state, command.ColonyId, command.TargetId, env);
            if (!quote.CanApprove) throw new InvalidDataException(quote.Reason);
            if (command.QuoteId != quote.Id || command.Fields["HabitatWitnessHash"] != quote.AdoptionWitnessHash) throw new InvalidDataException("The habitat, capacity or current qualification changed. Review the habitat again.");
            var observed = env.AdoptableFacilities.Single(f => f.Id == command.TargetId);
            var saved = Colony(state, command.ColonyId).Facilities.Single(f => f.Id == command.TargetId);
            saved.HomePartPersistentIds = observed.HomePartPersistentIds.ToList(); saved.CertifiedHomes = observed.CertifiedHomes;
            saved.HomePartCertificationHash = HomeMappingHash(saved); saved.State = "operational";
            var q = observed.Qualification;
            saved.Qualification = new ColonyQualification { Provider = AdoptedHabitatProvider, Context = q.Context, ObservedUt = q.ObservedUt,
                EvidenceHash = q.EvidenceHash, PlacementStable = q.PlacementStable, PowerReliable = q.PowerReliable, HeatSafe = q.HeatSafe,
                InputsAccessible = q.InputsAccessible, BackgroundSupported = q.BackgroundSupported, HousingCertified = true };
            saved.LastReason = quote.Reason;
            Log(state, env.Ut, command.ColonyId, command.OperationId, "adoptedHabitatQualified", quote.Reason);
            return saved.Id;
        }
    }
}
