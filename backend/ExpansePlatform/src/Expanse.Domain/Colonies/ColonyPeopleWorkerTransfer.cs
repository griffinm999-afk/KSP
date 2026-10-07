using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyWorkerTransferQuote
    {
        public string Id { get; set; } = "";
        public bool CanApprove { get; set; }
        public string Reason { get; set; } = "";
        public string RosterId { get; set; } = "";
        public string SourceVesselId { get; set; } = "";
        public uint SourcePartId { get; set; }
        public string DestinationVesselId { get; set; } = "";
        public uint WorkPartId { get; set; }
        public string JobFacilityId { get; set; } = "";
        public double DistanceMeters { get; set; }
        public List<string> SourceOccupants { get; set; } = new List<string>();
        public List<string> DestinationOccupants { get; set; } = new List<string>();
    }
    public static partial class ColonyEngine
    {
        public static ColonyWorkerTransferQuote QuoteWorkerTransfer(ColonyState state,string colonyId,string rosterId,string facilityId,uint partId,ColonyEnvironment env,bool explicitMissionCrewAssignment=false,string planningChildOperationId="")
        {
            var q=new ColonyWorkerTransferQuote {RosterId=rosterId,JobFacilityId=facilityId,WorkPartId=partId};
            try
            {
                ValidateEnvironment(state,env);var colony=Colony(state,colonyId);var person=Person(env,rosterId);
                if(IsRosterReservedForPlanning(state,rosterId,planningChildOperationId,facilityId,partId))throw new InvalidDataException("This Kerbal is reserved for an approved colony startup worker shift.");
                if(person.ProtectedMissionCrew && !explicitMissionCrewAssignment)throw new InvalidDataException("Protected mission crew need explicit consent; ordinary colony crew are selected by default.");
                if(person.Type!="Crew" || person.Status!="Assigned" || !env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id,out var present) || !present.Contains(person.RosterId))throw new InvalidDataException("Worker transfer requires one current ordinary crew member already physically present at this colony.");
                if(state.PeopleOperations.Any(p=>p.RosterId==rosterId && p.State!="complete" && p.State!="cancelled"))throw new InvalidDataException("This Kerbal already has a pending physical transport or crew transfer.");
                var facility=colony.Facilities.SingleOrDefault(f=>f.Id==facilityId);
                if(facility==null || facility.State!="operational" && facility.State!="commissioning" || !facility.PartIds.Contains(partId))throw new InvalidDataException("Choose an actual registered commissioning or operational workplace part.");
                if(facility.RequiredTrait.Length>0 && facility.RequiredTrait!=person.Trait)throw new InvalidDataException("The actual Kerbal trait does not meet this workplace requirement.");
                var source=env.People.Seats.SingleOrDefault(s=>s.VesselId==person.VesselId && s.PartId==person.PartId);
                var target=env.People.Seats.SingleOrDefault(s=>s.FacilityId==facilityId && s.VesselId==facility.VesselId && s.PartId==partId);
                if(source==null || target==null || !source.Current || !target.Current || source.ContextKey!=env.ContextKey || target.ContextKey!=env.ContextKey || !source.SurfaceTransferSupported || !target.SurfaceTransferSupported || !source.CrewMutationSupported || !target.CrewMutationSupported || !target.WorkSupported)throw new InvalidDataException("Both actual loaded landed cabins must allow crew transfer, and the destination must be a qualified workplace.");
                if(source.VesselId==target.VesselId && source.PartId==target.PartId)throw new InvalidDataException("The Kerbal already occupies this workplace.");
                if(source.Occupants.Count(n=>n==rosterId)!=1 || target.Occupants.Contains(rosterId) || target.Occupants.Count>=target.Capacity || env.People.Seats.Sum(s=>s.Occupants.Count(n=>n==rosterId))!=1)throw new InvalidDataException("Source membership must be unique and the destination must have a current vacant physical seat.");
                if(source.Body!=colony.Site.Body || target.Body!=colony.Site.Body || !env.BodyRadiiMeters.TryGetValue(colony.Site.Body,out var radius))throw new InvalidDataException("Current same-body surface geometry is unavailable.");
                ColonyStateCodec.Range(radius,1,1e12);ColonyStateCodec.Range(source.Latitude,-90,90);ColonyStateCodec.Range(target.Latitude,-90,90);ColonyStateCodec.Range(source.Longitude,-180,180);ColonyStateCodec.Range(target.Longitude,-180,180);
                q.DistanceMeters=SurfaceDistance(new ColonySite {Body=source.Body,Latitude=source.Latitude,Longitude=source.Longitude},new ColonySite {Body=target.Body,Latitude=target.Latitude,Longitude=target.Longitude},radius);
                if(q.DistanceMeters>200 || SurfaceDistance(colony.Site,new ColonySite {Body=source.Body,Latitude=source.Latitude,Longitude=source.Longitude},radius)>colony.Site.RadiusMeters || SurfaceDistance(colony.Site,new ColonySite {Body=target.Body,Latitude=target.Latitude,Longitude=target.Longitude},radius)>colony.Site.RadiusMeters)throw new InvalidDataException("The bounded colony surface shift requires both cabins within the colony and at most 200 metres apart.");
                q.SourceVesselId=source.VesselId;q.SourcePartId=source.PartId;q.DestinationVesselId=target.VesselId;q.SourceOccupants=source.Occupants.OrderBy(n=>n,StringComparer.Ordinal).ToList();q.DestinationOccupants=target.Occupants.OrderBy(n=>n,StringComparer.Ordinal).ToList();
                // Length-prefixed names prevent delimiter collisions in roster IDs.
                using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8))
                {
                    writer.Write(state.WorldId);writer.Write(env.ContextKey);writer.Write(colonyId);writer.Write(rosterId);writer.Write(facilityId);writer.Write(q.SourceVesselId);writer.Write(q.SourcePartId);writer.Write(q.DestinationVesselId);writer.Write(partId);writer.Write(explicitMissionCrewAssignment);
                    foreach(var names in new[]{q.SourceOccupants,q.DestinationOccupants}){writer.Write(names.Count);foreach(var name in names)writer.Write(name);}
                    writer.Flush();q.Id=ColonyStateCodec.Hash(stream.ToArray());
                }
                q.CanApprove=true;q.Reason="Actual nearby surface crew transfer; roster, visitor/resident status and certified home are preserved. No housing or job bonus is created by a label.";
            }
            catch(InvalidDataException ex){q.Reason=ex.Message;}
            return q;
        }
        static string TransferColonyWorker(ColonyState state,ColonyCommand command,ColonyEnvironment env)
        {
            uint part=checked((uint)Integer(command,"WorkPartId"));bool consent=Field(command,"ExplicitMissionCrewAssignment","false")=="true";
            var q=QuoteWorkerTransfer(state,command.ColonyId,command.TargetId,Field(command,"JobFacilityId"),part,env,consent,command.OperationId);
            if(!q.CanApprove)throw new InvalidDataException(q.Reason);if(q.Id!=command.QuoteId)throw new InvalidDataException("Actual crew or cabin occupancy changed; review the transfer again.");
            var op=new ColonyPeopleOperation {Id=command.OperationId,ColonyId=command.ColonyId,RosterId=q.RosterId,Kind="workerTransfer",SourceVesselId=q.SourceVesselId,SourcePartId=q.SourcePartId,JobFacilityId=q.JobFacilityId,DestinationVesselId=q.DestinationVesselId,WorkPartId=q.WorkPartId,WorkerQuoteId=q.Id,ExplicitMissionCrewAssignment=consent,SourceOccupantsBefore=q.SourceOccupants,DestinationOccupantsBefore=q.DestinationOccupants,Reason="Reviewed nearby colony worker shift queued; actual crew retained until physical readback."};
            AddPeopleOperation(state,op);state.Effects.Add(new ColonyEffect {Id=Guid.NewGuid().ToString("D"),OperationId=op.Id,ColonyId=op.ColonyId,TargetId=op.Id,Kind="peopleWorkerTransfer"});return op.Id;
        }
        public static ColonyState PrepareWorkerTransferEffect(ColonyState prior,string effectId,ColonyEnvironment env,string beforeWitness)
        {
            var copy=ColonyStateCodec.Copy(prior);var effect=copy.Effects.Single(e=>e.Id==effectId);var op=copy.PeopleOperations.Single(p=>p.Id==effect.TargetId);
            if(effect.Kind!="peopleWorkerTransfer" || effect.State!="prepared" || op.Kind!="workerTransfer" || op.State!="reserved" || string.IsNullOrWhiteSpace(beforeWitness))throw new InvalidDataException("Worker transfer is not ready for a durable before witness.");
            // Exclude only this exact reservation while revalidating all others.
            copy.PeopleOperations.Remove(op);var quote=QuoteWorkerTransfer(copy,op.ColonyId,op.RosterId,op.JobFacilityId,op.WorkPartId,env,op.ExplicitMissionCrewAssignment,op.Id);copy.PeopleOperations.Add(op);
            if(!quote.CanApprove || quote.Id!=op.WorkerQuoteId)throw new InvalidDataException(quote.CanApprove ? "Reviewed cabin or roster witness changed; transfer remains queued." : quote.Reason);
            effect.State="applying";effect.Provider="KSP.LoadedSurfaceCrewTransfer.v1";effect.BeforeWitness=beforeWitness;op.State="applying";op.Provider=effect.Provider;op.BeforeWitness=beforeWitness;copy.Revision++;ColonyStateCodec.Serialize(copy);return copy;
        }
        static string CancelWorkerTransfer(ColonyState state,ColonyCommand command)
        {
            var op=state.PeopleOperations.SingleOrDefault(p=>p.Id==command.TargetId && p.ColonyId==command.ColonyId && p.Kind=="workerTransfer");
            if(op==null || op.State!="reserved" || state.Effects.Any(e=>e.TargetId==op.Id && e.State!="prepared" && e.State!="cancelled"))throw new InvalidDataException("Only an unstarted worker transfer can be cancelled; partial effects retain their actual crew witnesses.");
            op.State="cancelled";op.Reason="Unstarted worker shift cancelled; actual crew and saved residency remain unchanged.";foreach(var effect in state.Effects.Where(e=>e.TargetId==op.Id && e.State=="prepared"))effect.State="cancelled";return op.Id;
        }
        public static ColonyState CompleteWorkerTransferEffect(ColonyState prior,string effectId,ColonyEnvironment after,string witness)
        {
            ValidateEnvironment(prior,after);var copy=ColonyStateCodec.Copy(prior);var effect=copy.Effects.Single(e=>e.Id==effectId);if(effect.State=="applied")return prior;
            var op=copy.PeopleOperations.Single(p=>p.Id==effect.TargetId);var person=Person(after,op.RosterId);
            var source=after.People.Seats.SingleOrDefault(s=>s.VesselId==op.SourceVesselId && s.PartId==op.SourcePartId && s.Current && s.ContextKey==after.ContextKey);
            var target=after.People.Seats.SingleOrDefault(s=>s.VesselId==op.DestinationVesselId && s.PartId==op.WorkPartId && s.Current && s.ContextKey==after.ContextKey);
            if(effect.Kind!="peopleWorkerTransfer" || effect.State!="applying" || op.State!="applying" || string.IsNullOrWhiteSpace(witness) || person.Type!="Crew" || person.Status!="Assigned" || person.VesselId!=op.DestinationVesselId || person.PartId!=op.WorkPartId || !after.People.PresenceComplete || source==null || target==null || !target.WorkSupported ||
                !source.Occupants.OrderBy(n=>n,StringComparer.Ordinal).SequenceEqual(op.SourceOccupantsBefore.Where(n=>n!=op.RosterId)) || !target.Occupants.OrderBy(n=>n,StringComparer.Ordinal).SequenceEqual(op.DestinationOccupantsBefore.Concat(new[]{op.RosterId}).OrderBy(n=>n,StringComparer.Ordinal)) || after.People.Seats.Sum(s=>s.Occupants.Count(n=>n==op.RosterId))!=1)throw new InvalidDataException("Worker transfer readback does not prove exact source removal, destination addition and unique roster membership.");
            op.State="complete";op.AfterWitness=witness;op.Reason="Actual crew cabin transfer verified; visitor/resident/home records preserved.";effect.State="applied";effect.AfterWitness=witness;effect.Reason=op.Reason;
            Log(copy,after.Ut,op.ColonyId,op.Id,"workerTransferred",op.Reason);copy.Revision++;ColonyStateCodec.Serialize(copy);return copy;
        }
    }
}
