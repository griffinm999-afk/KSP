using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyPlanningWorker
    {
        public string Id { get; set; } = "";
        public string BuildingId { get; set; } = "";
        public string RosterId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Trait { get; set; } = "";
        public string SourceFacilityId { get; set; } = "";
        public string SourceCabinName { get; set; } = "";
        public string SourceVesselId { get; set; } = "";
        public uint SourcePartId { get; set; }
        public double MaximumDistanceMeters { get; set; } = 200;
        public string DestinationSeatRole { get; set; } = "Actual qualified work cabin in the reviewed building";
    }
    public sealed class ColonyPlanningWorkerClaim
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "planned";
        public string OperationId { get; set; } = "";
        public uint WorkPartId { get; set; }
    }
    public static partial class ColonyEngine
    {
        public static bool IsRosterReservedForPlanning(ColonyState state,string rosterId) => IsRosterReservedForPlanning(state,rosterId,"","",0);
        internal static bool PlanningWorkerClaimPending(ColonyState state,ColonyPlan plan,ColonyPlanningWorkerClaim claim) =>
            claim.State=="planned"&&PlanningActive(plan)||claim.State=="queued"&&state.PeopleOperations.Any(o=>o.Id==claim.OperationId&&o.State!="complete"&&o.State!="cancelled");
        public static bool IsRosterReservedForPlanning(ColonyState state,string rosterId,string planningChildOperationId,string facilityId,uint workPartId)
        {
            if(IsRosterReservedForPlanningResident(state,rosterId))return true;
            foreach(var plan in state.Plans) foreach(var claim in plan.Workers.Where(w=>PlanningWorkerClaimPending(state,plan,w)||w.State=="queued"&&w.OperationId==planningChildOperationId))
            {
                var worker=plan.Quote.BootstrapWorkers.Single(w=>w.Id==claim.Id);if(worker.RosterId!=rosterId)continue;
                var line=plan.Buildings.Single(b=>b.Id==worker.BuildingId);
                var order=state.Construction.SingleOrDefault(o=>o.Id==line.OrderId);
                // Own bypass is scoped to the exact saved approved child and
                // actual registered destination. A matching roster ID alone
                // never excuses another command or another colony's claim.
                bool own=claim.State=="queued"&&claim.OperationId==planningChildOperationId&&claim.OperationId==PlanningChildId(plan.Id,worker.Id)&&
                    workPartId!=0&&claim.WorkPartId==workPartId&&order!=null&&order.FacilityId==facilityId&&
                    state.Colonies.Single(c=>c.Id==plan.ColonyId).Facilities.Any(f=>f.Id==facilityId&&f.ConstructionOrderId==order.Id&&f.PartIds.Contains(workPartId));
                if(!own)return true;
            }
            return false;
        }
        static void QuotePlanningWorkers(ColonyPlanningQuote q,ColonyRecord colony,ColonyState state,ColonyEnvironment env)
        {
            var selected=new HashSet<string>(StringComparer.Ordinal);
            foreach(var building in q.Buildings)
            {
                var template=env.Templates.Single(t=>t.Id==building.TemplateId&&t.Hash==building.TemplateHash);
                for(int i=0;i<template.Workers;i++)
                {
                    if(q.BootstrapWorkers.Count>=64)throw new InvalidDataException("Founding worker shifts exceed the bounded 64-person plan.");
                    var person=env.People.Roster.Where(p=>p.Current&&p.ContextKey==env.ContextKey&&p.Type=="Crew"&&p.Status=="Assigned"&&!p.ProtectedMissionCrew&&
                        (template.WorkerTrait.Length==0||p.Trait==template.WorkerTrait)&&colony.VisitorRosterIds.Contains(p.RosterId)&&!selected.Contains(p.RosterId)&&
                        !state.Colonies.Any(c=>c.Residents.Any(r=>r.RosterId==p.RosterId))&&!IsRosterReservedForPlanning(state,p.RosterId)&&
                        !state.PeopleOperations.Any(o=>o.RosterId==p.RosterId&&o.State!="complete"&&o.State!="cancelled"))
                        .OrderBy(p=>p.RosterId,StringComparer.Ordinal).FirstOrDefault(p=>PlanningWorkerSourceEligible(p,colony,building,env));
                    if(person==null){q.Blockers.Add("No unreserved ordinary visiting "+template.WorkerTrait+" in a transferable non-work cabin can staff "+building.Name+" within 200 metres; existing workers are preserved.");continue;}
                    selected.Add(person.RosterId);var source=colony.Facilities.Single(f=>f.VesselId==person.VesselId&&f.PartIds.Contains(person.PartId));
                    q.BootstrapWorkers.Add(new ColonyPlanningWorker {Id="worker-"+q.BootstrapWorkers.Count,BuildingId=building.Id,RosterId=person.RosterId,Name=person.Name,Trait=person.Trait,
                        SourceFacilityId=source.Id,SourceCabinName=source.Name,SourceVesselId=person.VesselId,SourcePartId=person.PartId});
                }
            }
        }
        static bool PlanningWorkerSourceEligible(ColonyRosterWitness p,ColonyRecord colony,ColonyPlanningBuilding building,ColonyEnvironment env)
        {
            if(!env.People.PresenceComplete||!env.People.PresentByColony.TryGetValue(colony.Id,out var present)||!present.Contains(p.RosterId))return false;
            if(!colony.Facilities.Any(f=>f.VesselId==p.VesselId&&f.PartIds.Contains(p.PartId)&&f.State!="retired"))return false;
            var source=env.People.Seats.SingleOrDefault(s=>s.VesselId==p.VesselId&&s.PartId==p.PartId);
            if(source==null||!source.Current||source.ContextKey!=env.ContextKey||!source.SurfaceTransferSupported||!source.CrewMutationSupported||source.WorkSupported||source.Occupants.Count(n=>n==p.RosterId)!=1)return false;
            var plot=colony.Plots.SingleOrDefault(s=>s.Id==building.PlotId);
            return plot==null||source.Body==colony.Site.Body&&env.BodyRadiiMeters.TryGetValue(colony.Site.Body,out var radius)&&
                SurfaceDistance(new ColonySite {Body=source.Body,Latitude=source.Latitude,Longitude=source.Longitude},new ColonySite {Body=source.Body,Latitude=plot.Latitude,Longitude=plot.Longitude},radius)<=200;
        }
        static bool RunPlanningWorker(ColonyState state,ColonyPlan plan,ColonyEnvironment env)
        {
            foreach(var claim in plan.Workers)
            {
                if(claim.State=="complete")continue;
                if(claim.State=="cancelled")throw new InvalidDataException("The approved named bootstrap worker shift was cancelled; review/cancel the founding plan, no alternate crew are chosen.");
                var worker=plan.Quote.BootstrapWorkers.Single(w=>w.Id==claim.Id);
                if(claim.State=="queued")
                {
                    var op=state.PeopleOperations.Single(o=>o.Id==claim.OperationId);
                    if(op.State=="complete"){claim.State="complete";return true;}
                    if(op.State=="cancelled"){claim.State="cancelled";plan.Reason="Approved bootstrap shift cancelled; no substitute Kerbal is chosen.";return true;}
                    continue;
                }
                var line=plan.Buildings.Single(b=>b.Id==worker.BuildingId);var order=state.Construction.SingleOrDefault(o=>o.Id==line.OrderId);
                if(order==null||order.FacilityId.Length==0)continue;
                var person=env.People.Roster.SingleOrDefault(p=>p.RosterId==worker.RosterId);
                if(person==null||person.VesselId!=worker.SourceVesselId||person.PartId!=worker.SourcePartId||person.Trait!=worker.Trait||person.ProtectedMissionCrew)
                    throw new InvalidDataException("Approved bootstrap Kerbal/source cabin changed; no implicit alternate worker or source is used.");
                if(!PlanningWorkerSourceEligible(person,state.Colonies.Single(c=>c.Id==plan.ColonyId),line,env))throw new InvalidDataException("Approved bootstrap source is unavailable or now a work cabin; existing workers remain in place.");
                var facility=state.Colonies.Single(c=>c.Id==plan.ColonyId).Facilities.Single(f=>f.Id==order.FacilityId&&f.ConstructionOrderId==order.Id);
                var seat=env.People.Seats.Where(s=>s.FacilityId==facility.Id&&s.VesselId==facility.VesselId&&facility.PartIds.Contains(s.PartId)&&s.Current&&s.ContextKey==env.ContextKey&&s.WorkSupported&&s.SurfaceTransferSupported&&s.CrewMutationSupported&&s.Occupants.Count<s.Capacity).OrderBy(s=>s.PartId).FirstOrDefault();
                if(seat==null)throw new InvalidDataException("Load the real reviewed workplace: its actual qualified work cabin must allow the approved surface crew shift.");
                claim.State="queued";claim.OperationId=PlanningChildId(plan.Id,worker.Id);claim.WorkPartId=seat.PartId;
                var quote=QuoteWorkerTransfer(state,plan.ColonyId,worker.RosterId,facility.Id,seat.PartId,env,false,claim.OperationId);
                if(!quote.CanApprove)throw new InvalidDataException(quote.Reason);
                var command=PlanningCommand(state,env,plan,worker.Id,"transferColonyWorker",new Dictionary<string,string> {["JobFacilityId"]=facility.Id,["WorkPartId"]=seat.PartId.ToString(System.Globalization.CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"});
                command.TargetId=worker.RosterId;command.QuoteId=quote.Id;TransferColonyWorker(state,command,env);
                plan.Reason="Approved named ordinary visitor queued for actual nearby workshop transfer; exact physical crew readback precedes dependency commissioning.";return true;
            }
            return false;
        }
        static ColonyState? ReconcileTerminalPlanningWorkers(ColonyState prior)
        {
            var terminal=prior.Plans.SelectMany(p=>p.Workers.Where(w=>w.State=="queued").Select(w=>new {PlanId=p.Id,Claim=w,
                Operation=prior.PeopleOperations.Single(o=>o.Id==w.OperationId)})).FirstOrDefault(x=>x.Operation.State=="complete"||x.Operation.State=="cancelled");
            if(terminal==null)return null;
            var state=ColonyStateCodec.Copy(prior);state.Plans.Single(p=>p.Id==terminal.PlanId).Workers.Single(w=>w.Id==terminal.Claim.Id).State=terminal.Operation.State;
            return FinishPlanningTransition(state);
        }
    }
    public static partial class ColonyStateCodec
    {
        static void ValidatePlanningWorkers(ColonyState state,ColonyPlan plan,ColonyRecord colony)
        {
            Rows(plan.Quote.BootstrapWorkers,64);Rows(plan.Workers,64);
            Unique(plan.Quote.BootstrapWorkers.Select(w=>w.Id));Unique(plan.Quote.BootstrapWorkers.Select(w=>w.RosterId));Unique(plan.Workers.Select(w=>w.Id));
            if(plan.Quote.BootstrapWorkers.Count!=plan.Workers.Count)Fail("Approved worker claims differ from the reviewed manifest.");
            if(plan.State=="complete"&&plan.Workers.Any(w=>w.State!="complete"))Fail("Complete founding plan has unfinished approved worker shifts.");
            foreach(var worker in plan.Quote.BootstrapWorkers)
            {
                Text(worker.Id,128,true);Text(worker.BuildingId,128,true);Text(worker.RosterId,160,true);Text(worker.Name,160,true);Text(worker.Trait,64,true);
                Id(worker.SourceFacilityId);Id(worker.SourceVesselId);Text(worker.SourceCabinName,160,true);Text(worker.DestinationSeatRole,160,true);Range(worker.MaximumDistanceMeters,200,200);
                if(worker.SourcePartId==0||!plan.Quote.Buildings.Any(b=>b.Id==worker.BuildingId)||!colony.Facilities.Any(f=>f.Id==worker.SourceFacilityId&&f.VesselId==worker.SourceVesselId&&f.PartIds.Contains(worker.SourcePartId)))Fail("Worker quote lacks the reviewed building or registered actual source cabin.");
                var claim=plan.Workers.SingleOrDefault(w=>w.Id==worker.Id);if(claim==null)Fail("Reviewed worker reservation is missing.");
                Choice(claim!.State,"planned","queued","complete","cancelled");
                if(claim.State=="planned"&&claim.OperationId.Length>0||claim.OperationId.Length==0&&claim.WorkPartId!=0)Fail("Unstarted worker claim contains a fabricated destination.");
                if(claim.OperationId.Length==0)
                {if(claim.State=="queued"||claim.State=="complete")Fail("Started worker shift lacks an exact child identity.");continue;}
                Id(claim.OperationId);if(claim.OperationId!=ColonyEngine.PlanningChildId(plan.Id,worker.Id)||claim.WorkPartId==0)Fail("Worker shift lost deterministic reviewed-child lineage.");
                var line=plan.Buildings.Single(b=>b.Id==worker.BuildingId);var order=state.Construction.SingleOrDefault(o=>o.Id==line.OrderId);
                var op=state.PeopleOperations.SingleOrDefault(o=>o.Id==claim.OperationId);
                if(order==null||op==null||op.Kind!="workerTransfer"||op.ColonyId!=plan.ColonyId||op.RosterId!=worker.RosterId||op.SourceVesselId!=worker.SourceVesselId||op.SourcePartId!=worker.SourcePartId||op.JobFacilityId!=order.FacilityId||op.WorkPartId!=claim.WorkPartId)Fail("Worker shift no longer matches its reviewed source and real construction destination.");
                if(claim.State=="complete"&&op!.State!="complete"||claim.State=="cancelled"&&op!.State!="cancelled")Fail("Worker progress claims an unproved terminal physical result.");
                if(claim.State=="complete"&&op!.AfterWitness.Length==0)Fail("Completed approved worker shift lacks actual terminal cabin witness.");
            }
        }
        static void ValidatePlanningWorkerReservations(ColonyState state)
        {
            var owners=state.Plans.SelectMany(p=>p.Workers.Where(w=>ColonyEngine.PlanningWorkerClaimPending(state,p,w))
                .Select(w=>new {Plan=p,Claim=w,Worker=p.Quote.BootstrapWorkers.Single(q=>q.Id==w.Id)})).ToArray();
            Unique(owners.Select(o=>o.Worker.RosterId));
            foreach(var owner in owners)
                if(state.PeopleOperations.Any(p=>p.RosterId==owner.Worker.RosterId&&p.State!="complete"&&p.State!="cancelled"&&p.Id!=owner.Claim.OperationId))Fail("Another transport holds the approved founding worker roster identity.");
        }
    }
}
