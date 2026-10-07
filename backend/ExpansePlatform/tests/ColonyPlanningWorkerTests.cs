using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyPlanningWorkerTests
{
    static (ColonyState state,ColonyEnvironment env,string id) Fixture()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var colony=s.Colonies.Single();var template=e.Templates.Single(t=>t.Id=="workshop");template.Workers=1;template.WorkerTrait="Engineer";
        var source=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),Name="Actual visitor cabin",PartIds=new(){99}};colony.Facilities.Add(source);
        colony.VisitorRosterIds.Add("ordinary engineer");e.People.PresentByColony[id].Add("ordinary engineer");
        e.People.Roster.Add(new(){RosterId="ordinary engineer",Name="Ordinary engineer",Trait="Engineer",Type="Crew",Status="Assigned",VesselId=source.VesselId,PartId=99,Current=true,ContextKey=e.ContextKey});
        e.People.Seats.Add(new(){FacilityId=source.Id,VesselId=source.VesselId,PartId=99,Current=true,ContextKey=e.ContextKey,Capacity=2,Occupants=new(){"ordinary engineer"},Body="Minmus",SurfaceTransferSupported=true,CrewMutationSupported=true,Evidence="Explicit domain-test cabin/provider"});
        return(s,e,id);
    }
    [Fact] public void ReviewedOneCommandPlanReservesConcreteOrdinaryVisitorWithoutCreatingCrew()
    {
        var(s,e,id)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));var worker=Assert.Single(q.BootstrapWorkers);
        Assert.Equal("ordinary engineer",worker.RosterId);Assert.Equal("Engineer",worker.Trait);Assert.Equal(99u,worker.SourcePartId);Assert.Equal(200,worker.MaximumDistanceMeters);
        Assert.Equal("workshop",q.Buildings.Single(b=>b.Id==worker.BuildingId).Role);
        s=ColonyPlanningTests.Approve(s,e,id);Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,worker.RosterId));Assert.Equal("planned",s.Plans.Single().Workers.Single().State);
        Assert.Empty(s.PeopleOperations);Assert.Single(s.Colonies.Single().VisitorRosterIds);Assert.Empty(s.Colonies.Single().Residents);
        var cancel=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="cancelColonyPlan",TargetId=s.Plans.Single().Id};
        var result=ColonyEngine.Execute(s,cancel,e);Assert.Equal("accepted",result.Outcome);Assert.False(ColonyEngine.IsRosterReservedForPlanning(result.State,worker.RosterId));
    }
    [Theory] [InlineData("hero")] [InlineData("existing worker")] [InlineData("not transferable")]
    public void NoImplicitHeroOrExistingWorkerReassignment(string kind)
    {
        var(s,e,id)=Fixture();if(kind=="hero")e.People.Roster.Single().ProtectedMissionCrew=true;
        if(kind=="existing worker")e.People.Seats.Single().WorkSupported=true;
        if(kind=="not transferable")e.People.Seats.Single().SurfaceTransferSupported=false;
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e);Assert.False(q.CanApprove);Assert.Empty(q.BootstrapWorkers);Assert.Contains(q.Blockers,b=>b.Contains("existing workers are preserved"));
    }
    [Fact] public void NamedWorkerChangeRequiresNewReviewAndCannotBeClaimedByAnotherPlan()
    {
        var(s,e,id)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e);var c=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="approveFoundingPlan",QuoteId=q.Id};
        e.People.Roster.Single().PartId=100;Assert.Equal("rejected",ColonyEngine.Execute(s,c,e).Outcome);e.People.Roster.Single().PartId=99;
        s=ColonyPlanningTests.Approve(s,e,id);
        Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,"ordinary engineer",Guid.NewGuid().ToString("D"),s.Colonies.Single().Facilities.Single().Id,99));
        var bad=ColonyStateCodec.Copy(s);bad.Plans.Single().Workers.Single().State="complete";Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(bad));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void BootstrapUsesExactRealConstructionCabinAndNeverNeedsHomesFirst(bool cancelParent)
    {
        var(s,e,id)=Fixture();s=ColonyPlanningTests.Stage(s,e,id);
        s.Colonies.Single().Stock.Single(x=>x.Resource=="MaterialKits").Amount=5*ColonyLimits.Units;
        s.Colonies.Single().Stock.Single(x=>x.Resource=="Supplies").Amount=10*ColonyLimits.Units;
        s=ColonyPlanningTests.Approve(s,e,id);
        for(int i=0;i<3;i++)
        {s=ColonyEngine.RunPlanning(s,e);var order=s.Construction.Single(o=>o.State=="reserved");s=ColonyPlanningTests.Pay(s,e);s=ColonyPlanningTests.CompleteOne(s,e,order.Id);}
        var workshop=s.Colonies.Single().Facilities.Single(f=>f.TemplateId=="workshop");var part=workshop.PartIds.Single();
        e.People.Seats.Add(new(){FacilityId=workshop.Id,VesselId=workshop.VesselId,PartId=part,Capacity=2,Body="Minmus",Current=true,ContextKey=e.ContextKey,WorkSupported=true,SurfaceTransferSupported=true,CrewMutationSupported=true,Evidence="Explicit actual fixture work cabin"});
        Assert.False(s.Colonies.Single().SupportCommissionedUt.HasValue);Assert.DoesNotContain(s.Construction,o=>o.TemplateId=="housing");
        s=ColonyEngine.RunPlanning(s,e);var claim=s.Plans.Single().Workers.Single();var op=Assert.Single(s.PeopleOperations);
        Assert.Equal(ColonyEngine.PlanningChildId(s.Plans.Single().Id,claim.Id),op.Id);Assert.Equal("workerTransfer",op.Kind);Assert.Equal(workshop.Id,op.JobFacilityId);Assert.Equal(part,op.WorkPartId);Assert.Equal(99u,op.SourcePartId);
        Assert.False(ColonyEngine.IsRosterReservedForPlanning(s,op.RosterId,op.Id,workshop.Id,part));
        Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,op.RosterId,op.Id,workshop.Id,part+1));
        Assert.Equal("queued",claim.State);Assert.Single(s.Colonies.Single().VisitorRosterIds);Assert.Empty(s.Colonies.Single().Residents);
        if(cancelParent)
        {
            var cancel=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="cancelColonyPlan",TargetId=s.Plans.Single().Id};
            var result=ColonyEngine.Execute(s,cancel,e);Assert.Equal("accepted",result.Outcome);s=result.State;
            Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,op.RosterId));
            Assert.Equal("queued",s.Plans.Single().Workers.Single().State);
        }
        var effect=s.Effects.Single(x=>x.Kind=="peopleWorkerTransfer");s=ColonyEngine.PrepareWorkerTransferEffect(s,effect.Id,e,"Exact actual before crew fixture");
        var held=ColonyEngine.HoldEffect(s,effect.Id,"Unknown cabin change: exact readback required");Assert.Same(held,ColonyEngine.RunPlanning(held,e));Assert.True(ColonyEngine.IsRosterReservedForPlanning(held,op.RosterId));
        var person=e.People.Roster.Single();e.People.Seats.Single(seat=>seat.PartId==99).Occupants.Clear();e.People.Seats.Single(seat=>seat.PartId==part).Occupants.Add(person.RosterId);person.VesselId=workshop.VesselId;person.PartId=part;
        s=ColonyEngine.CompleteWorkerTransferEffect(s,effect.Id,e,"Exact actual after crew fixture");s=ColonyEngine.RunPlanning(s,e);
        Assert.Equal("complete",s.Plans.Single().Workers.Single().State);Assert.Single(s.PeopleOperations);Assert.Single(s.Colonies.Single().VisitorRosterIds);
        Assert.False(ColonyEngine.IsRosterReservedForPlanning(s,op.RosterId));
        if(cancelParent)Assert.Equal("cancelled",s.Plans.Single().State);
        Assert.DoesNotContain(s.Colonies.Single().Residents,r=>r.RosterId==person.RosterId);
    }
}
