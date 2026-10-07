using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static string AdoptionWitnessHash(ColonyFacility facility, ColonySite site)
        {
            if (facility == null) throw new InvalidDataException("Adoption candidate is unavailable.");
            ColonyStateCodec.Id(facility.Id); ColonyStateCodec.Id(facility.VesselId); ColonyStateCodec.Site(site);
            ColonyStateCodec.Text(facility.Name, 160);
            if (facility.PartIds == null || facility.PartIds.Count == 0 || facility.PartIds.Count > 512 ||
                facility.PartIds.Any(p => p == 0) || facility.PartIds.Distinct().Count() != facility.PartIds.Count)
                throw new InvalidDataException("Adoption requires a complete unique actual part identity set.");
            var terms = new StringBuilder(); AdoptionTerm(terms, "facility-adoption-v2");
            AdoptionTerm(terms, facility.Id); AdoptionTerm(terms, facility.VesselId); AdoptionTerm(terms, facility.Name);
            AdoptionSiteTerms(terms, site);
            foreach (uint member in facility.PartIds.OrderBy(p => p)) AdoptionTerm(terms, member.ToString(CultureInfo.InvariantCulture));
            return ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms.ToString()));
        }

        public static ColonyAdoptionQuote QuoteFacilityAdoption(ColonyState state, string colonyId, string facilityId, ColonyEnvironment env)
        {
            var quote = new ColonyAdoptionQuote { ColonyId = colonyId, FacilityId = facilityId };
            try
            {
                ColonyStateCodec.Validate(state); ValidateEnvironment(state, env);
                ColonyStateCodec.Id(colonyId); ColonyStateCodec.Id(facilityId);
                var colony = Colony(state, colonyId);
                if (state.Effects.Any(e => e.State == "applying" || e.State == "held")) throw new InvalidDataException("An external effect needs reconciliation before adoption.");
                if (env.Ut < state.SimulatedUt) throw new InvalidDataException("Selected-save time moved backward; reload before adoption.");
                if (env.AdoptableFacilities == null || env.AdoptableFacilities.Count > ColonyLimits.Facilities || env.FacilitySites == null)
                    throw new InvalidDataException("Current bounded adoption observations are unavailable.");
                var matches = env.AdoptableFacilities.Where(f => f != null && f.Id == facilityId).Take(2).ToArray();
                if (matches.Length != 1 || !env.FacilitySites.TryGetValue(facilityId, out var site)) throw new InvalidDataException("Selected facility has no unique fresh adoption observation.");
                var candidate = matches[0];
                quote.AdoptionWitnessHash = AdoptionWitnessHash(candidate, site);
                quote.Name = candidate.Name; quote.VesselId = candidate.VesselId; quote.PartIds = candidate.PartIds.OrderBy(p => p).ToList();
                quote.Site = new ColonySite { Body = site.Body, Biome = site.Biome, Latitude = site.Latitude, Longitude = site.Longitude, RadiusMeters = site.RadiusMeters };
                var observation = candidate.Qualification;
                if (observation != null) ColonyStateCodec.Time(observation.ObservedUt);
                if (observation == null || observation.Context != "loaded-packed" && observation.Context != "loaded-unpacked" && observation.Context != "unloaded" ||
                    observation.ObservedUt > env.Ut || env.Ut - observation.ObservedUt > 10)
                    throw new InvalidDataException("Selected facility observation is stale or lacks its actual loaded/proto context.");
                if (site.Body != colony.Site.Body || !env.BodyRadiiMeters.TryGetValue(site.Body, out var radius) || !FiniteAdoption(radius) || radius <= 0 ||
                    SurfaceDistance(site, colony.Site, radius) > colony.Site.RadiusMeters)
                    throw new InvalidDataException("Selected facility lies outside this colony's body or boundary.");
                if (state.Colonies.Sum(c => c.Facilities.Count) >= ColonyLimits.Facilities) throw new InvalidDataException("Registered facility capacity is reached.");
                if (state.Colonies.Any(c => c.Facilities.Any(f => f.Id == candidate.Id || f.VesselId == candidate.VesselId || f.PartIds.Any(candidate.PartIds.Contains))))
                    throw new InvalidDataException("Selected facility, vessel or actual part already belongs to a colony.");
                if (env.People == null || !env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id, out var present) || present == null ||
                    present.Count > ColonyLimits.Residents || present.Distinct(StringComparer.Ordinal).Count() != present.Count)
                    throw new InvalidDataException("Adoption requires complete current physical site population.");
                if (env.People.Roster == null || env.People.Roster.Count > 2048 || env.People.Seats == null || env.People.Seats.Count > 16384)
                    throw new InvalidDataException("Current bounded crew witnesses are unavailable.");
                foreach (string id in present)
                {
                    ColonyStateCodec.Text(id, 160, true);
                    var people = env.People.Roster.Where(p => p != null && p.RosterId == id).Take(2).ToArray();
                    if (people.Length != 1 || !people[0].Current || people[0].ContextKey != env.ContextKey || people[0].Status != "Assigned" ||
                        people[0].Type != "Crew" && people[0].Type != "Tourist" || people[0].PartId == 0 ||
                        env.People.Seats.Count(s => s != null && s.Current && s.ContextKey == env.ContextKey && s.VesselId == people[0].VesselId && s.PartId == people[0].PartId && s.Occupants != null && s.Occupants.Count(n => n == id) == 1) != 1)
                        throw new InvalidDataException("Current site crew lack exact roster and physical cabin membership.");
                    if (state.Colonies.Any(c => c.Id != colony.Id && (c.Residents.Any(r => r.RosterId == id) || c.VisitorRosterIds.Contains(id))) ||
                        state.PeopleOperations.Any(p => p.ColonyId != colony.Id && p.RosterId == id && p.State != "complete" && p.State != "cancelled") ||
                        state.Plans.Any(p => p.ColonyId != colony.Id && PlanningActive(p) && (p.Quote.BootstrapWorkers.Any(w => w.RosterId == id) || p.Quote.Residents.Any(r => r.RosterId == id))) ||
                        env.People.PresentByColony.Any(pair => pair.Key != colony.Id && pair.Value != null && pair.Value.Contains(id)))
                        throw new InvalidDataException("Current crew are owned or physically assigned to another colony; adoption cannot steal people.");
                }
                var candidateCrew = env.People.Roster.Where(p => p != null && p.VesselId == candidate.VesselId && p.Status == "Assigned").ToArray();
                if (candidateCrew.Any(p => !present.Contains(p.RosterId) || !candidate.PartIds.Contains(p.PartId)))
                    throw new InvalidDataException("Selected vessel crew do not agree with its complete part and site membership.");
                var residentIds = colony.Residents.Select(r => r.RosterId).ToArray();
                quote.PresentPeople = present.OrderBy(id => id, StringComparer.Ordinal).ToList();
                quote.VisitorCount = present.Where(id => !residentIds.Contains(id)).Union(colony.VisitorRosterIds, StringComparer.Ordinal).Count();
                quote.NewVisitorCount = present.Count(id => !residentIds.Contains(id) && !colony.VisitorRosterIds.Contains(id));
                if (quote.VisitorCount > colony.Charter.VisitorLimit || colony.Residents.Count > colony.Charter.ResidentLimit)
                    throw new InvalidDataException("Current people exceed the existing charter limits; review the charter first.");
                // Presence includes unadopted visiting vessels already. Wait for
                // normal chronology instead of back-charging new people, skipping
                // support time or consuming stock during this registration.
                var savedPresent = colony.Residents.Where(r => r.Status != "arriving" && r.Status != "missing").Select(r => r.RosterId).Concat(colony.VisitorRosterIds);
                if (colony.SupportCommissionedUt.HasValue && !new System.Collections.Generic.HashSet<string>(savedPresent, StringComparer.Ordinal).SetEquals(present))
                    throw new InvalidDataException("Current presence awaits normal support reconciliation; wait for the colony tick before adoption.");
                if (state.Colonies.Sum(c => c.Residents.Count + c.VisitorRosterIds.Count) + quote.NewVisitorCount > ColonyLimits.Residents)
                    throw new InvalidDataException("Supported people capacity is reached.");
                var terms = new StringBuilder(); AdoptionTerm(terms, "reviewed-facility-adoption-v2");
                AdoptionTerm(terms, state.WorldId); AdoptionTerm(terms, env.ContextKey); AdoptionTerm(terms, state.Revision.ToString(CultureInfo.InvariantCulture));
                AdoptionTerm(terms, colony.Id); AdoptionTerm(terms, quote.AdoptionWitnessHash);
                AdoptionSiteTerms(terms, colony.Site); AdoptionCharterTerms(terms, colony.Charter);
                foreach (string id in quote.PresentPeople) AdoptionTerm(terms, id);
                quote.Id = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms.ToString()));
                quote.CanApprove = true;
                quote.Reason = "Register this exact existing physical facility. Stock, funds, crew, residency and support chronology remain unchanged; work cabins are not homes.";
            }
            catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is FormatException || e is OverflowException)
            { quote.Reason = e.Message; }
            return quote;
        }

        static string AdoptFacility(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            if (command.Fields == null || command.Fields.Count != 1 || !command.Fields.ContainsKey("AdoptionWitnessHash") || command.FoundingIntent != null)
                throw new InvalidDataException("Adoption requires only the exact reviewed facility witness; no resource, crew or certification fields are accepted.");
            var quote = QuoteFacilityAdoption(state, command.ColonyId, command.TargetId, env);
            if (!quote.CanApprove) throw new InvalidDataException(quote.Reason);
            if (command.QuoteId != quote.Id || command.Fields["AdoptionWitnessHash"] != quote.AdoptionWitnessHash)
                throw new InvalidDataException("Reviewed facility identity, members, site or adoption context changed. Review the current target again.");
            // Only physical identities are adopted. No candidate capability,
            // observed tank contents, work seat or nominal home capacity becomes
            // paid stock, a production owner or a housing certificate.
            Colony(state, command.ColonyId).Facilities.Add(new ColonyFacility { Id = quote.FacilityId, VesselId = quote.VesselId,
                Name = quote.Name, PartIds = quote.PartIds.ToList(), Qualification = new ColonyQualification {
                    Context = env.AdoptableFacilities.Single(f => f.Id == quote.FacilityId).Qualification.Context, ObservedUt = env.Ut },
                LastReason = "Existing physical facility explicitly adopted; functional roles and housing require separate actual qualification." });
            Log(state, env.Ut, command.ColonyId, command.OperationId, "facilityAdopted", "Exact physical vessel and members registered; existing crew, inventory and support chronology preserved.");
            return quote.FacilityId;
        }

        // Transient reviews cross modern CLR and KSP Mono/Framework. Their "R"
        // double text can differ for identical bits; bind exact values instead.
        // Signed zero denotes the same coordinate/reserve on both runtimes.
        static string AdoptionDoubleTerm(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0d : value).ToString(CultureInfo.InvariantCulture);
        static void AdoptionSiteTerms(StringBuilder terms, ColonySite site)
        {
            AdoptionTerm(terms, site.Body); AdoptionTerm(terms, site.Biome);
            AdoptionTerm(terms, AdoptionDoubleTerm(site.Latitude)); AdoptionTerm(terms, AdoptionDoubleTerm(site.Longitude));
            AdoptionTerm(terms, AdoptionDoubleTerm(site.RadiusMeters));
        }
        static void AdoptionCharterTerms(StringBuilder terms, ColonyCharter charter)
        {
            AdoptionTerm(terms, charter.Purpose); AdoptionTerm(terms, charter.PopulationTarget.ToString(CultureInfo.InvariantCulture));
            AdoptionTerm(terms, charter.ResidentLimit.ToString(CultureInfo.InvariantCulture)); AdoptionTerm(terms, charter.VisitorLimit.ToString(CultureInfo.InvariantCulture));
            AdoptionTerm(terms, charter.FoundingBudget.ToString(CultureInfo.InvariantCulture)); AdoptionTerm(terms, charter.CashFloor.ToString(CultureInfo.InvariantCulture));
            AdoptionTerm(terms, charter.SpendingLimit.ToString(CultureInfo.InvariantCulture)); AdoptionTerm(terms, AdoptionDoubleTerm(charter.ReserveDays));
            AdoptionTerm(terms, charter.GrowthPolicy); AdoptionTerm(terms, charter.Sandbox ? "true" : "false");
        }
        static void AdoptionTerm(StringBuilder terms, string value) { terms.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';'); }
        static bool FiniteAdoption(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
