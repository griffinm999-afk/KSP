using System;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonySupportEnvironment
    {
        public bool Ready { get; set; }
        public string PolicyId { get; set; } = "";
        public string PolicyHash { get; set; } = "";
        public long MicroUnitsPerPersonDay { get; set; }
        public bool OtherLifeSupportInstalled { get; set; }
        public string Reason { get; set; } = "Resident support policy has not been observed.";
    }

    public static partial class ColonyEngine
    {
        public static long SupportReserveQuote(ColonyRecord colony, ColonyEnvironment env)
        {
            RequireSupportOwner(colony, env, false);
            if (!env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id, out var present))
                throw new InvalidDataException("A complete current site population observation is required before commissioning support.");
            // Include all actual visitors, regardless of adoption; also protect the
            // residents already reserved on a paid passenger route.
            int people = present.Concat(colony.Residents.Where(x => x.Status != "missing").Select(x => x.RosterId)).Distinct(StringComparer.Ordinal).Count();
            people = Math.Max(people, colony.Charter.PopulationTarget);
            return checked((long)decimal.Ceiling((decimal)people * env.Support.MicroUnitsPerPersonDay * (decimal)colony.Charter.ReserveDays));
        }

        static void RequireSupportOwner(ColonyRecord colony, ColonyEnvironment env, bool admission)
        {
            if (env.Support.OtherLifeSupportInstalled)
                throw new InvalidDataException("Another life-support mod is installed. Expanse consumption and immigration are held until that support owner is qualified; no supplies will be consumed twice.");
            if (!env.Support.Ready || env.Support.MicroUnitsPerPersonDay <= 0)
                throw new InvalidDataException("Resident-support policy is unavailable. Refresh the installed policy before proceeding.");
            if (admission && colony.SupportPolicyHash.Length > 0 && (colony.SupportPolicyHash != env.Support.PolicyHash || colony.SupportMicroUnitsPerPersonDay != env.Support.MicroUnitsPerPersonDay))
                throw new InvalidDataException("Installed support terms differ from the commissioned policy; review a policy migration before immigration.");
        }

        static string CommissionSupport(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId);
            if (colony.SupportCommissionedUt.HasValue) throw new InvalidDataException("Support is already commissioned; replay cannot reset consumption or the startup boundary.");
            long reserve = SupportReserveQuote(colony, env);
            ColonyStateCodec.Text(env.Support.PolicyId, 128, true); ColonyStateCodec.Text(env.Support.PolicyHash, 128, true);
            ColonyStateCodec.Quantity(env.Support.MicroUnitsPerPersonDay);
            if (Field(command, "PolicyId") != env.Support.PolicyId || Field(command, "PolicyHash") != env.Support.PolicyHash || Integer(command, "QuotedReserveMicroUnits") != reserve)
                throw new InvalidDataException("Support policy or population changed; review the current reserve requirement.");
            int homes = colony.Facilities.Where(f => f.State == "operational" && f.Qualification.HousingCertified).Sum(f =>
                Math.Min(f.CertifiedHomes, env.People.Seats.Where(s => s.Current && s.HousingCertified && s.UtilitiesQualified && s.ContextKey == env.ContextKey &&
                    s.FacilityId == f.Id && s.VesselId == f.VesselId).Sum(s => s.Capacity)));
            if (homes < colony.Charter.PopulationTarget) throw new InvalidDataException("Actual qualified homes and continuous utilities do not cover the founding population.");
            var supplies = Stock(colony, "Supplies");
            if (supplies.Amount - supplies.Reserved < reserve) throw new InvalidDataException("Colony-owned supplies do not yet cover residents and visitors for the charter reserve days. Purchase or transfer the shortage first.");
            colony.SupportPolicy = "Expanse resident support"; colony.SupportPolicyHash = env.Support.PolicyHash;
            colony.SupportMicroUnitsPerPersonDay = env.Support.MicroUnitsPerPersonDay;
            colony.SupportCommissionedUt = env.Ut; colony.SupportAccountedUt = env.Ut; colony.SupportRemainder = 0;
            colony.VisitorRosterIds = env.People.PresentByColony[colony.Id].Distinct(StringComparer.Ordinal)
                .Where(id => !state.Colonies.Any(c => c.Residents.Any(r => r.RosterId == id))).ToList();
            supplies.SupportFloor = reserve; colony.SupportStatus = "Supported"; colony.Status = "operational";
            Log(state, env.Ut, colony.Id, command.OperationId, "supportCommissioned", "Funded owned supplies, actual homes and current site population verified. Consumption starts now; earlier time creates no debt.");
            return colony.Id;
        }

        static bool SupportOwnershipHeld(ColonyRecord colony, ColonyEnvironment env) => env.Support.OtherLifeSupportInstalled ||
            colony.SupportPolicyHash.Length > 0 && (!env.Support.Ready || colony.SupportPolicyHash != env.Support.PolicyHash);

        static void MaintainSupportReserves(ColonyState state)
        {
            foreach (var colony in state.Colonies.Where(c => c.SupportCommissionedUt.HasValue))
            {
                var supplies = colony.Stock.SingleOrDefault(s => s.Resource == "Supplies");
                if (supplies == null) continue;
                long people = colony.Residents.Count(r => r.Status != "missing") + colony.VisitorRosterIds.Count;
                supplies.SupportFloor = Math.Max(PlanningSupportFloor(state,colony.Id),checked((long)decimal.Ceiling((decimal)Math.Max(people, colony.Charter.PopulationTarget) * colony.SupportMicroUnitsPerPersonDay * (decimal)colony.Charter.ReserveDays)));
            }
        }
    }
}
