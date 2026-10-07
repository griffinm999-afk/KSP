using System;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        int workerTransferCursor;
        bool ApplyOneWorkerTransferEffect()
        {
            var queued=state.Effects.Where(e=>e.Kind=="peopleWorkerTransfer" && e.State=="prepared").ToArray();if(queued.Length==0)return false;
            if(workerTransferCursor>=queued.Length)workerTransferCursor=0;var effect=queued[workerTransferCursor++];var op=state.PeopleOperations.Single(p=>p.Id==effect.TargetId);var before=GetEnvironment();
            ColonyState applying;
            try{applying=ColonyEngine.PrepareWorkerTransferEffect(state,effect.Id,before,WorkerTransferWitness(before,op));}
            catch(InvalidDataException ex){string reason=Bound(ex.Message,512);if(op.Reason!=reason){var waiting=GetStateCopy();waiting.PeopleOperations.Single(p=>p.Id==op.Id).Reason=reason;Accept(waiting);}return false;}
            var game=HighLogic.CurrentGame;string epoch=loadEpoch;
            // Resolve every actual object and free seat before the held save state
            // is installed. Loaded packed parts retain native crew membership.
            var source=FlightGlobals.Vessels.Single(v=>v.id.ToString("D")==op.SourceVesselId);
            var target=FlightGlobals.Vessels.Single(v=>v.id.ToString("D")==op.DestinationVesselId);
            var sourcePart=source.parts.Single(p=>p.persistentId==op.SourcePartId);var targetPart=target.parts.Single(p=>p.persistentId==op.WorkPartId);var pcm=game.CrewRoster[op.RosterId];
            int destinationSeat=Enumerable.Range(0,targetPart.CrewCapacity).First(i=>!targetPart.protoModuleCrew.Any(c=>c.seatIdx==i));
            Accept(applying);mutating=true;
            try
            {
                if(pcm==null || sourcePart.protoModuleCrew.Count(c=>ReferenceEquals(c,pcm))!=1 || !sourcePart.crewTransferAvailable || !targetPart.crewTransferAvailable)throw new InvalidOperationException("Actual reviewed cabin membership changed before the callback.");
                sourcePart.RemoveCrewmember(pcm);
                if(!targetPart.AddCrewmemberAt(pcm,destinationSeat))throw new InvalidOperationException("KSP refused the destination seat; partial transfer requires reconciliation.");
                pcm.rosterStatus=ProtoCrewMember.RosterStatus.Assigned;
                GameEvents.onCrewTransferred.Fire(new GameEvents.HostedFromToAction<ProtoCrewMember,Part>(pcm,sourcePart,targetPart));
                Vessel.CrewWasModified(source,target);
                source.CrewListSetDirty();source.RebuildCrewList();if(source!=target){target.CrewListSetDirty();target.RebuildCrewList();}
                if(!source.packed)source.SpawnCrew();if(target!=source && !target.packed)target.SpawnCrew();
                if(!ReferenceEquals(game,HighLogic.CurrentGame) || epoch!=loadEpoch || state!=applying)throw new InvalidOperationException("Selected save changed during the crew transfer.");
                var after=GetEnvironment();Accept(ColonyEngine.CompleteWorkerTransferEffect(applying,effect.Id,after,WorkerTransferWitness(after,op)));
            }
            catch(Exception ex)
            {
                if(ReferenceEquals(game,HighLogic.CurrentGame) && epoch==loadEpoch && state==applying)Accept(ColonyEngine.HoldEffect(state,effect.Id,Bound(ex.Message,512)));
                else HoldReason="Selected save changed during a crew transfer; reconcile actual source and destination before replay.";
                Debug.LogError("[ExpanseColony] Worker transfer held: "+Bound(ex.Message,512));
            }
            finally{mutating=false;}return true;
        }
        static string WorkerTransferWitness(ColonyEnvironment env,ColonyPeopleOperation op)
        {
            var source=env.People.Seats.SingleOrDefault(s=>s.VesselId==op.SourceVesselId && s.PartId==op.SourcePartId);var target=env.People.Seats.SingleOrDefault(s=>s.VesselId==op.DestinationVesselId && s.PartId==op.WorkPartId);
            return Bound(PeopleWitness(env,op.RosterId)+"; source occupants="+string.Join(",",source==null ? new string[0] : source.Occupants.OrderBy(n=>n,StringComparer.Ordinal).ToArray())+"; destination occupants="+string.Join(",",target==null ? new string[0] : target.Occupants.OrderBy(n=>n,StringComparer.Ordinal).ToArray()),4096);
        }
    }
}
