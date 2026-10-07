using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyWorkerTransferTests
{
    [Fact]
    public void OrdinaryVisitorCanStaffCommissioningWithoutHomeOrSupportOrFare()
    {
        var (state,env,request)=Fixture();var result=ColonyEngine.Execute(state,request,env);
        Assert.Equal("accepted",result.Outcome);Assert.Empty(result.State.Colonies[0].Residents);Assert.Equal(new[]{"Ada Kerman"},result.State.Colonies[0].VisitorRosterIds);
        Assert.Equal(0,ColonyEngine.CommittedFunds(result.State,request.ColonyId));Assert.Equal("commissioning",result.State.Colonies[0].Facilities[0].State);Assert.Equal(0,result.State.Colonies[0].Facilities[0].CertifiedHomes);
        Assert.Equal("duplicate",ColonyEngine.Execute(result.State,request,env).Outcome);Assert.Single(result.State.PeopleOperations);
        var restored=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(result.State));Assert.Equal(result.State.PeopleOperations[0].WorkerQuoteId,restored.PeopleOperations[0].WorkerQuoteId);
    }
    [Fact]
    public void BothCabinsMustProveExactMinusOnePlusOneAndUniqueCurrentMembership()
    {
        var (state,env,request)=Fixture();var accepted=ColonyEngine.Execute(state,request,env).State;var effect=accepted.Effects.Single();
        var applying=ColonyEngine.PrepareWorkerTransferEffect(accepted,effect.Id,env,"exact source/destination before");
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompleteWorkerTransferEffect(applying,effect.Id,env,"unchanged"));
        var person=env.People.Roster.Single();env.People.Seats[1].Occupants.Add(person.RosterId);person.VesselId=env.People.Seats[1].VesselId;person.PartId=43;
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompleteWorkerTransferEffect(applying,effect.Id,env,"duplicate mapping"));
        env.People.Seats[0].Occupants.Clear();var complete=ColonyEngine.CompleteWorkerTransferEffect(applying,effect.Id,env,"source empty; destination Ada; membership one");
        Assert.Equal("complete",complete.PeopleOperations.Single().State);Assert.Empty(complete.Colonies[0].Residents);Assert.Equal(new[]{"Ada Kerman"},complete.Colonies[0].VisitorRosterIds);
        Assert.Same(complete,ColonyEngine.CompleteWorkerTransferEffect(complete,effect.Id,env,"retry"));
    }
    [Fact]
    public void ChangedOccupantsBlockedAndProtectedFullDistantOrUnqualifiedCabinsNeverMove()
    {
        var (state,env,request)=Fixture();env.People.Seats[1].Occupants.Add("Other Kerman");
        Assert.Equal("rejected",ColonyEngine.Execute(state,request,env).Outcome);
        env.People.Seats[1].Occupants.Clear();env.People.Roster[0].ProtectedMissionCrew=true;
        Assert.Contains("consent",ColonyEngine.Execute(state,request,env).Reason);
        env.People.Roster[0].ProtectedMissionCrew=false;env.People.Seats[1].Longitude=1;
        Assert.Contains("200",ColonyEngine.Execute(state,request,env).Reason);
        env.People.Seats[1].Longitude=.01;env.People.Seats[1].WorkSupported=false;
        Assert.Contains("workplace",ColonyEngine.Execute(state,request,env).Reason);
        Assert.Empty(state.PeopleOperations);Assert.Single(env.People.Seats[0].Occupants);
    }
    [Fact]
    public void UnstartedTransferCanCancelButUnknownPartialTransferPersistsItsHold()
    {
        var (state,env,request)=Fixture();var queued=ColonyEngine.Execute(state,request,env).State;
        var cancellation=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=queued.Revision,ColonyId=request.ColonyId,TargetId=queued.PeopleOperations[0].Id,Kind="cancelWorkerTransfer"};
        var cancelled=ColonyEngine.Execute(queued,cancellation,env);Assert.Equal("accepted",cancelled.Outcome);Assert.Equal("cancelled",cancelled.State.PeopleOperations[0].State);Assert.Single(env.People.Seats[0].Occupants);
        var applying=ColonyEngine.PrepareWorkerTransferEffect(queued,queued.Effects[0].Id,env,"before both cabins");var held=ColonyEngine.HoldEffect(applying,applying.Effects[0].Id,"destination callback failed");
        cancellation.ExpectedRevision=held.Revision;Assert.Equal("rejected",ColonyEngine.Execute(held,cancellation,env).Outcome);
        var restored=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(held));Assert.Equal("held",restored.Effects[0].State);Assert.Equal("before both cabins",restored.PeopleOperations[0].BeforeWitness);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareWorkerTransferEffect(restored,restored.Effects[0].Id,env,"attempt replay"));
    }
    [Fact]
    public void QueuedSourceOccupancyChangesRequireCancellationAndFreshReview()
    {
        var (state,env,request)=Fixture();var queued=ColonyEngine.Execute(state,request,env).State;
        env.People.Seats[0].Occupants.Add("New visitor");
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.PrepareWorkerTransferEffect(queued,queued.Effects[0].Id,env,"different cabin"));
        Assert.Equal("reserved",queued.PeopleOperations[0].State);Assert.Equal("prepared",queued.Effects[0].State);
    }
    static (ColonyState,ColonyEnvironment,ColonyCommand) Fixture()
    {
        var state=ColonyEngine.Create(Guid.NewGuid().ToString("D"),0);var workshop=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),Name="Commissioning workshop",State="commissioning",RequiredWorkers=1,RequiredTrait="Engineer",PartIds=new(){43}};
        var colony=new ColonyRecord {Id=Guid.NewGuid().ToString("D"),Name="Worker invariant fixture",Site=new(){Body="Minmus"},Facilities=new(){workshop},VisitorRosterIds=new(){"Ada Kerman"}};state.Colonies.Add(colony);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey=state.WorldId+"/worker-fixture",BodyRadiiMeters=new(){{"Minmus",60000}}};string source=Guid.NewGuid().ToString("D");
        env.People.Roster.Add(new(){RosterId="Ada Kerman",Name="Ada Kerman",Trait="Engineer",Type="Crew",Status="Assigned",Current=true,ContextKey=env.ContextKey,VesselId=source,PartId=42});
        env.People.Seats.Add(new(){VesselId=source,PartId=42,Capacity=2,Occupants=new(){"Ada Kerman"},Current=true,ContextKey=env.ContextKey,CrewMutationSupported=true,SurfaceTransferSupported=true,Body="Minmus"});
        env.People.Seats.Add(new(){FacilityId=workshop.Id,VesselId=workshop.VesselId,PartId=43,Capacity=2,Current=true,ContextKey=env.ContextKey,CrewMutationSupported=true,SurfaceTransferSupported=true,WorkSupported=true,Body="Minmus",Longitude=.01});
        env.People.PresenceComplete=true;env.People.PresentByColony[colony.Id]=new(){"Ada Kerman"};
        var quote=ColonyEngine.QuoteWorkerTransfer(state,colony.Id,"Ada Kerman",workshop.Id,43,env);Assert.True(quote.CanApprove,quote.Reason);
        var request=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=state.Revision,ColonyId=colony.Id,Kind="transferColonyWorker",TargetId="Ada Kerman",QuoteId=quote.Id,Fields=new(){{"JobFacilityId",workshop.Id},{"WorkPartId","43"}}};return(state,env,request);
    }
}
