using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        // Additive schema-one migration: old saves have no passenger authority and
        // no assigned physical home part. Preserve every old person and home.
        static void MigratePeople(ColonyState state)
        {
            if (state == null) return;
            if (state.PeopleOperations == null) state.PeopleOperations = new List<ColonyPeopleOperation>();
            if (state.Colonies != null) foreach (var colony in state.Colonies.Where(x => x != null))
                if (colony.Residents != null) foreach (var resident in colony.Residents.Where(x => x != null))
                { if (resident.ArrivalOperationId == null) resident.ArrivalOperationId = ""; if (resident.LastReason == null) resident.LastReason = ""; }
        }

        static void ValidatePeople(ColonyState state)
        {
            Rows(state.PeopleOperations, 256); Unique(state.PeopleOperations.Select(x => x.Id));
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in state.PeopleOperations)
            {
                if(op.Kind=="workerTransfer") {ValidateWorkerTransfer(state,op,active);continue;}
                Id(op.Id); Id(op.ColonyId); Text(op.RosterId, 160, true); Text(op.HomeFacilityId, 64, true); Text(op.RouteId, 128, true); Text(op.RouteHash, 64, true);
                Choice(op.Kind, "arrival", "departure"); Choice(op.State, "reserved", "inTransit", "awaitingArrival", "applying", "complete", "held", "cancelled");
                Funds(op.Funds); Time(op.DepartUt); Time(op.ArrivalUt); Time(op.TravelSeconds);
                if (op.HomePartId == 0 || op.TravelSeconds <= 0 || op.ArrivalUt < op.DepartUt || op.State == "reserved" && op.FundsPaid || op.State != "reserved" && op.State != "cancelled" && !op.FundsPaid) Fail("Invalid passenger chronology or payment provenance.");
                Choice(op.ExpectedRosterType, "Crew", "Applicant"); Text(op.Provider, 128); Text(op.BeforeWitness, 4096); Text(op.AfterWitness, 4096); Text(op.Reason, 512);
                var colony = state.Colonies.SingleOrDefault(x => x.Id == op.ColonyId); if (colony == null) Fail("Passenger operation lacks a colony.");
                bool pending = op.State != "complete" && op.State != "cancelled";
                if (pending && !active.Add(op.RosterId)) Fail("Roster identity is reserved by two passenger operations.");
                var resident = colony!.Residents.SingleOrDefault(x => x.RosterId == op.RosterId);
                if (pending && (resident == null || resident.HomeFacilityId != op.HomeFacilityId || resident.HomePartId != op.HomePartId)) Fail("Passenger identity or home reservation lacks its resident owner.");
                if (op.State == "reserved" && state.Effects.Count(x => x.TargetId == op.Id && x.Kind == "passengerFare" && x.FundsDelta == -op.Funds && (x.State == "prepared" || x.State == "applying" || x.State == "held")) != 1) Fail("Passenger fare lacks exactly one unapplied payment effect.");
                if (op.State == "applying" && (!state.Effects.Any(x => x.TargetId == op.Id && (x.Kind == "peopleArrival" || x.Kind == "peopleDeparture") && (x.State == "applying" || x.State == "held")) || op.BeforeWitness.Length == 0)) Fail("Applying passenger transition lacks durable before-witness authority.");
                if (op.State == "complete" && (op.AfterWitness.Length == 0 || op.BeforeWitness.Length == 0)) Fail("Completed passenger transition lacks actual readback evidence.");
            }
            foreach (var colony in state.Colonies) foreach (var resident in colony.Residents)
            {
                Text(resident.ArrivalOperationId, 64); Text(resident.LastReason, 512);
                if (resident.ArrivalOperationId.Length != 0) Id(resident.ArrivalOperationId);
                if (resident.HomePartId != 0 && !colony.Facilities.Any(x => x.Id == resident.HomeFacilityId && x.PartIds.Contains(resident.HomePartId))) Fail("Resident home part is not a registered physical facility part.");
                if (resident.PhysicallyAtWork && (resident.WorkPartId == 0 || !colony.Facilities.Any(x => x.Id == resident.JobFacilityId && x.PartIds.Contains(resident.WorkPartId)))) Fail("Physical work claim lacks a registered job part.");
            }
        }
        static void ValidateWorkerTransfer(ColonyState state,ColonyPeopleOperation op,HashSet<string> active)
        {
            Id(op.Id);Id(op.ColonyId);Text(op.RosterId,160,true);Id(op.SourceVesselId);Id(op.JobFacilityId);Id(op.DestinationVesselId);Text(op.WorkerQuoteId,64,true);if(op.WorkerQuoteId.Length!=64 || op.WorkerQuoteId.Any(c=>!Uri.IsHexDigit(c)))Fail("Invalid worker transfer quote seal.");
            Choice(op.State,"reserved","applying","complete","held","cancelled");Text(op.Provider,128);Text(op.BeforeWitness,4096);Text(op.AfterWitness,4096);Text(op.Reason,512);
            Rows(op.SourceOccupantsBefore,512);Rows(op.DestinationOccupantsBefore,512);Unique(op.SourceOccupantsBefore);Unique(op.DestinationOccupantsBefore);foreach(var name in op.SourceOccupantsBefore.Concat(op.DestinationOccupantsBefore))Text(name,160,true);
            if(op.Funds!=0 || op.FundsPaid || op.TravelSeconds!=0 || op.RouteId.Length!=0 || op.RouteHash.Length!=0 || op.SourcePartId==0 || op.WorkPartId==0 || op.SourceVesselId==op.DestinationVesselId && op.SourcePartId==op.WorkPartId || op.SourceOccupantsBefore.Count(n=>n==op.RosterId)!=1 || op.DestinationOccupantsBefore.Contains(op.RosterId))Fail("Invalid worker-transfer physical or financial provenance.");
            var colony=state.Colonies.SingleOrDefault(c=>c.Id==op.ColonyId);if(colony==null || !colony.Facilities.Any(f=>f.Id==op.JobFacilityId && f.VesselId==op.DestinationVesselId && f.PartIds.Contains(op.WorkPartId)))Fail("Worker destination is not a registered physical part.");
            if(op.State!="complete" && op.State!="cancelled" && !active.Add(op.RosterId))Fail("Roster identity has competing physical operations.");
            if(op.State=="reserved" && state.Effects.Count(e=>e.TargetId==op.Id && e.Kind=="peopleWorkerTransfer" && e.State=="prepared")!=1)Fail("Worker transfer lacks one queued physical effect.");
            if((op.State=="applying" || op.State=="held") && (op.BeforeWitness.Length==0 || !state.Effects.Any(e=>e.TargetId==op.Id && e.Kind=="peopleWorkerTransfer" && (e.State=="applying" || e.State=="held"))))Fail("Worker transfer lacks durable partial-effect authority.");
            if(op.State=="complete" && (op.BeforeWitness.Length==0 || op.AfterWitness.Length==0))Fail("Worker transfer lacks exact physical readback.");
        }
    }
}
