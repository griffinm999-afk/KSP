using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static ColonyForecast Forecast(ColonyState state, string colonyId, ColonyEnvironment env, IEnumerable<string>? expectedImmigrants=null)
        {
            var colony = Colony(state, colonyId);
            var f = new ColonyForecast { Cash = env.AvailableFunds,
                CommittedCash = PendingCash(state), CashFloor = state.Colonies.Max(c => c.Charter.CashFloor),
                Receivables = state.Shipments.Where(s => s.Kind == "export" && s.State == "inTransit").Sum(s => s.Funds) };
            f.AvailableCash = Math.Max(0, f.Cash - f.CommittedCash - f.CashFloor);
            f.SupportedPeople = Math.Max(colony.Charter.PopulationTarget, colony.Residents.Where(r => r.Status != "missing").Select(r => r.RosterId)
                .Concat(colony.VisitorRosterIds).Concat(env.People.PresentByColony.TryGetValue(colony.Id, out var present) ? present : new List<string>())
                .Concat(state.Plans.Where(p=>p.ColonyId==colonyId).SelectMany(p=>p.Residents.Where(r=>PlanningResidentClaimPending(p,r)).Select(r=>p.Quote.Residents.Single(q=>q.Id==r.Id))).Where(r=>r.Kind=="recruit").Select(r=>r.RosterId))
                .Concat(expectedImmigrants??Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).Count());
            f.QualifiedHomes = QualifiedPlanningHomes(colony, env);
            f.OpenQualifiedJobs = colony.Facilities.Where(p => p.State == "operational" && p.RequiredWorkers > 0 && p.Qualification.PowerReliable &&
                p.Qualification.InputsAccessible && p.Qualification.HeatSafe && p.Qualification.BackgroundSupported &&
                env.Planning.Assets.Any(a => a.ColonyId == colony.Id && a.FacilityId == p.Id && a.ContextKey == env.ContextKey && a.Qualified))
                .Sum(p => ActualOpenWorkPlaces(state, colony, p, env));
            double late = Math.Max(1, Math.Min(10, env.Planning.ImportDelayFactor));
            var supplies = colony.Stock.SingleOrDefault(s => s.Resource == "Supplies");
            var suppliers = state.Suppliers.Where(s => s.Resource == "Supplies" && (s.DestinationBody.Length == 0 || s.DestinationBody == colony.Site.Body)).ToArray();
            double lead = suppliers.Length == 0 ? ColonyLimits.KerbinDay : suppliers.Max(s => s.TravelSeconds);
            f.HorizonSeconds = Math.Max(colony.Charter.ReserveDays * ColonyLimits.KerbinDay, lead * late);
            long rate = colony.SupportCommissionedUt.HasValue ? colony.SupportMicroUnitsPerPersonDay : env.Support.MicroUnitsPerPersonDay;
            if (!env.Support.Ready || env.Support.OtherLifeSupportInstalled || rate <= 0 || !env.People.PresenceComplete)
                f.Shortages.Add("Current support ownership and complete site population must be verified.");
            decimal daily = (decimal)f.SupportedPeople * Math.Max(0, rate);
            long owned = supplies == null ? 0 : Math.Max(0, supplies.Amount - supplies.Reserved);
            f.CurrentSupplyDays = daily == 0 ? 0 : (double)(owned / daily);
            long demand = checked((long)decimal.Ceiling(daily * (decimal)(f.HorizonSeconds / ColonyLimits.KerbinDay)));
            // Only cargo whose delayed arrival precedes the current stock's
            // exhaustion can avert a shortage. A late shipment is not retroactive food.
            double exhaustion = env.Ut + f.CurrentSupplyDays * ColonyLimits.KerbinDay;
            long usableIncoming = state.Shipments.Where(s => s.ColonyId == colony.Id && s.Kind == "import" && s.Resource == "Supplies" && s.State == "inTransit" &&
                s.DepartUt + s.TravelSeconds * late <= Math.Min(env.Ut + f.HorizonSeconds, exhaustion)).Sum(s => s.Amount);
            f.DownsideSupplyDays = daily == 0 ? 0 : (double)((owned + usableIncoming) / daily);
            long shortage = Math.Max(0, demand - owned - usableIncoming);
            if (shortage > 0)
            {
                f.Shortages.Add("Owned supplies do not cover the downside horizon before delayed arrivals.");
                long remaining = shortage;
                foreach (var supplier in suppliers.OrderBy(s => s.FundsPerUnit).ThenBy(s => s.Id, StringComparer.Ordinal))
                {
                    long quantity = Math.Min(remaining, Math.Max(0, supplier.Available - supplier.Reserved - PlanningSupplierReserved(state, supplier.Id)));
                    if (quantity == 0 || supplies == null) continue;
                    long batch = PlanningMaximumBatch(supplier, supplies);
                    if (batch <= 0) continue;
                    long count = checked((long)decimal.Ceiling((decimal)quantity / batch));
                    // Ceiling once per load can cost more than aggregate pricing.
                    f.DownsideReplacementCost = checked(f.DownsideReplacementCost + ScaledProduct(quantity, supplier.FundsPerUnit) + count * (supplier.FreightFunds + 1));
                    remaining -= quantity;
                    if (remaining == 0) break;
                }
                if (remaining > 0) f.Shortages.Add("Finite supplier inventory cannot cover the downside supply shortage.");
            }
            f.DownsideCash = f.AvailableCash - f.DownsideReplacementCost;
            if (f.DownsideCash < 0) f.Shortages.Add("Zero-export cash cannot fund replacement supplies while preserving commitments and cash floor.");
            f.Sustainable = f.Shortages.Count == 0;
            return f;
        }

        static int ActualOpenWorkPlaces(ColonyState state, ColonyRecord colony, ColonyFacility facility, ColonyEnvironment env)
        {
            if (!env.People.PresenceComplete) return 0;
            var seats = env.People.Seats.Where(s => s.FacilityId == facility.Id && s.VesselId == facility.VesselId &&
                facility.PartIds.Contains(s.PartId) && s.Current && s.ContextKey == env.ContextKey &&
                s.UtilitiesQualified && s.WorkSupported && s.Capacity > 0).ToArray();
            if (seats.Length == 0 || seats.Select(s => s.PartId).Distinct().Count() != seats.Length) return 0;
            int workers = 0, vacancies = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seat in seats)
            {
                if (seat.Occupants.Count > seat.Capacity) return 0;
                vacancies += seat.Capacity - seat.Occupants.Count;
                foreach (string name in seat.Occupants)
                {
                    var roster = env.People.Roster.Where(r => r.RosterId == name).ToArray();
                    // An unknown occupant is not an empty job. A resident's job
                    // label is likewise not proof that they occupy this module.
                    if (!seen.Add(name) || roster.Length != 1) return 0;
                    var person = roster[0];
                    if (!person.Current || person.ContextKey != env.ContextKey || person.Status != "Assigned" ||
                        person.VesselId != seat.VesselId || person.PartId != seat.PartId ||
                        double.IsNaN(person.ObservedUt) || double.IsInfinity(person.ObservedUt) || person.ObservedUt > env.Ut || env.Ut - person.ObservedUt > 10) return 0;
                    if (person.Type == "Crew" && (facility.RequiredTrait.Length == 0 || person.Trait == facility.RequiredTrait)) workers++;
                }
            }
            // Pending reviewed shifts already claim these workplaces. They do
            // not justify recruiting another person while the transfer settles.
            int incoming = state.PeopleOperations.Count(p => p.ColonyId == colony.Id && p.Kind == "workerTransfer" &&
                p.JobFacilityId == facility.Id && p.State != "complete" && p.State != "cancelled" && !seen.Contains(p.RosterId));
            return Math.Max(0, Math.Min(facility.RequiredWorkers - workers - incoming, vacancies - incoming));
        }

        public static int QualifiedPlanningHomes(ColonyRecord colony, ColonyEnvironment env) => colony.Facilities.Where(p =>
            (p.State == "operational" || p.State == "adopted") && p.Qualification.HousingCertified).Sum(p =>
                Math.Min(p.CertifiedHomes, env.People.Seats.Where(s => s.FacilityId == p.Id && s.VesselId == p.VesselId && s.Current &&
                    s.ContextKey == env.ContextKey && s.HousingCertified && s.UtilitiesQualified).Sum(s => s.Capacity)));
    }
}
