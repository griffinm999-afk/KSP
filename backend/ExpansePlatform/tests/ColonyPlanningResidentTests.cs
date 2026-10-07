using System.Text;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;

// Independent domain contract fixtures; no fabricated runtime certification.
public sealed class ColonyPlanningResidentTests
{
    const long U=ColonyLimits.Units;
    [Fact] public void DefaultFillUsesOrdinaryVisitorsAndExplicitArrivalsHaveNamedFareAndSupportUnion()
    {
        var(s,e,id,home)=Fixture();
        var defaultQuote=ColonyEngine.QuoteFoundingPlan(s,id,e,new());
        Assert.True(defaultQuote.CanApprove,string.Join(";",defaultQuote.Blockers));Assert.Equal(new[]{"Ada Kerman","Bob Kerman"},defaultQuote.Residents.Select(r=>r.RosterId));
        var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});
        Assert.True(q.CanApprove,string.Join(";",q.Blockers));Assert.Equal(new[]{"Ada Kerman","Grace Kerman"},q.Residents.Select(r=>r.RosterId));
        Assert.DoesNotContain(q.Residents,r=>r.RosterId=="Jebediah Kerman");Assert.Equal(3000,q.Residents.Single(r=>r.Kind=="recruit").Fare);
        Assert.Equal(4*U,q.StartupSupportReserve);Assert.Equal(3000,q.TotalFunds);Assert.All(q.Residents,r=>Assert.Equal(home.Id,r.HomeFacilityId));
        Assert.False(q.FoundingIntent!.FillPopulationTarget);Assert.Equal(new[]{"Grace Kerman"},q.FoundingIntent.RecruitRosterIds);
        Assert.Equal(q.Id,ColonyEngine.QuoteFoundingPlan(s,id,e,q.FoundingIntent).Id);
    }
    [Fact] public void ChangedNamesRoutesAndMissingSeatsRequireFreshReviewAndDoNotReserveAnyEffect()
    {
        var(s,e,id,_)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});var cmd=Approval(s,e,id,q);
        e.People.Roster.Single(p=>p.RosterId=="Grace Kerman").Status="Assigned";
        Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);Assert.Empty(s.Plans);
        e.People.Roster.Single(p=>p.RosterId=="Grace Kerman").Status="Available";e.People.Routes.Single().Fare++;
        Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);e.People.Routes.Single().Fare--;
        e.People.Seats.Single(p=>p.PartId==42).Occupants.AddRange(new[]{"Outside One","Outside Two"});
        Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);Assert.Empty(s.PeopleOperations);
    }
    [Fact] public void OneApprovalDesignatesThenQueuesPaidRecruitmentAndCompletesOnlyAfterActualArrival()
    {
        var(s,e,id,home)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});s=Approved(s,e,id,q);
        Assert.Equal(3000,ColonyEngine.PendingCash(s));Assert.Empty(s.Colonies[0].Residents);
        for(int i=0;i<8&&!s.PeopleOperations.Any();i++)s=ColonyEngine.RunPlanning(s,e);
        Assert.Equal("resident",s.Colonies[0].Residents.Single(r=>r.RosterId=="Ada Kerman").Status);
        var ada=e.People.Roster.Single(p=>p.RosterId=="Ada Kerman");Assert.Equal(50u,ada.PartId);Assert.Contains("Ada Kerman",e.People.Seats.Single(p=>p.PartId==50).Occupants);
        Assert.Equal("reserved",s.PeopleOperations.Single().State);Assert.Equal(0,s.Plans[0].RemainingFunds);Assert.Equal(3000,ColonyEngine.PendingCash(s));Assert.NotEqual("complete",s.Plans[0].State);
        var receipt=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));Assert.Equal(q.Id,receipt.Plans[0].Quote.Id);
        s=ColonyPlanningTests.Pay(s,e);e.Ut+=100;s=ColonyEngine.Advance(s,e);
        var effect=s.Effects.Single(x=>x.Kind=="peopleArrival");s=ColonyEngine.PreparePeopleEffect(s,effect.Id,e,"actual Applicant Available; membership zero");
        var recruit=e.People.Roster.Single(p=>p.RosterId=="Grace Kerman");recruit.Type="Crew";recruit.Status="Assigned";recruit.VesselId=home.VesselId;recruit.PartId=42;
        e.People.Seats.Single(p=>p.PartId==42).Occupants.Add(recruit.RosterId);e.People.PresentByColony[id].Add(recruit.RosterId);
        s=ColonyEngine.CompletePeopleEffect(s,effect.Id,e,"actual Crew Assigned at exact reviewed home; one membership");
        for(int i=0;i<5&&s.Plans[0].State!="complete";i++)s=ColonyEngine.RunPlanning(s,e);
        Assert.Equal("complete",s.Plans[0].State);Assert.Equal(2,s.Colonies[0].Residents.Count);Assert.All(s.Plans[0].Residents,c=>Assert.Equal("complete",c.State));
        Assert.Equal(3000,s.Colonies[0].SpentFunds-100); // fixture staging cost was paid before quote
        s=ColonyEngine.RunPlanning(s,e);Assert.Single(s.PeopleOperations);Assert.Equal(2,s.Colonies[0].Residents.Count);Assert.Equal(3000,s.Colonies[0].SpentFunds-100);Assert.Equal("complete",ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s)).Plans[0].State);
    }
    [Fact] public void ApprovedPeopleProtectRosterPopulationAndHomesFromUnrelatedCommands()
    {
        var(s,e,id,home)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});s=Approved(s,e,id,q);
        var other=Assign(s,e,id,home,"Bob Kerman");Assert.Equal("rejected",ColonyEngine.Execute(s,other,e).Outcome);
        Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,"Ada Kerman"));Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,"Grace Kerman"));
        other.Fields["RosterId"]="Ada Kerman";Assert.Equal("rejected",ColonyEngine.Execute(s,other,e).Outcome);Assert.Empty(s.Colonies[0].Residents);
    }
    [Fact] public void CancelledParentReleasesUnstartedChoicesAndReconcilesKnownUnpaidPassengerChild()
    {
        var(s,e,id,_)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});s=Approved(s,e,id,q);
        for(int i=0;i<8&&!s.PeopleOperations.Any();i++)s=ColonyEngine.RunPlanning(s,e);
        var cancel=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="cancelColonyPlan",TargetId=s.Plans[0].Id};s=Accept(s,e,cancel);
        Assert.True(ColonyEngine.IsRosterReservedForPlanning(s,"Grace Kerman"));Assert.Equal(0,s.Plans[0].RemainingFunds);
        cancel.OperationId=Guid.NewGuid().ToString("D");cancel.ExpectedRevision=s.Revision;cancel.Kind="cancelPassenger";cancel.TargetId=s.PeopleOperations[0].Id;s=Accept(s,e,cancel);
        s=ColonyEngine.RunPlanning(s,e);Assert.False(ColonyEngine.IsRosterReservedForPlanning(s,"Grace Kerman"));Assert.Equal("cancelled",s.Plans[0].Residents.Single(c=>c.Id==q.Residents.Single(r=>r.Kind=="recruit").Id).State);
        Assert.Equal("Applicant",e.People.Roster.Single(p=>p.RosterId=="Grace Kerman").Type);Assert.Equal("Available",e.People.Roster.Single(p=>p.RosterId=="Grace Kerman").Status);ColonyStateCodec.Validate(s);
    }
    [Fact] public void NewDesignationRequiresCommissionedSupportAndTargetButReassignmentCountsNoAdmission()
    {
        var(s,e,id,home)=Fixture();var cmd=Assign(s,e,id,home,"Ada Kerman");Assert.Contains("commissioned",ColonyEngine.Execute(s,cmd,e).Reason);
        var colony=s.Colonies[0];colony.SupportCommissionedUt=e.Ut;colony.SupportPolicyHash=e.Support.PolicyHash;colony.Status="operational";
        colony.Stock.Single(x=>x.Resource=="Supplies").Amount=3*U;var accepted=ColonyEngine.Execute(s,cmd,e);Assert.Equal("accepted",accepted.Outcome);s=accepted.State;
        Assert.Equal(3*U,s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies").Amount);Assert.DoesNotContain("Ada Kerman",s.Colonies[0].VisitorRosterIds);
        s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies").Amount=0;cmd.OperationId=Guid.NewGuid().ToString("D");cmd.ExpectedRevision=s.Revision;
        Assert.Equal("accepted",ColonyEngine.Execute(s,cmd,e).Outcome); // same resident, no new admission
        cmd.Fields["RosterId"]="Bob Kerman";cmd.OperationId=Guid.NewGuid().ToString("D");Assert.Contains("reserves",ColonyEngine.Execute(s,cmd,e).Reason);
        s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies").Amount=100*U;s.Colonies[0].Charter.PopulationTarget=1;
        Assert.Contains("target",ColonyEngine.Execute(s,cmd,e).Reason);
    }
    [Fact] public void WireIntentBoundsAndCommandReplayHashAreIndependentOfNullLegacyExtension()
    {
        var(s,e,id,_)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,new(){NewArrivalCount=1});var cmd=Approval(s,e,id,q);
        var wire=ColonyManagementWire.Encode(new ColonyManagementWireRequest{RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=cmd});
        var decoded=ColonyManagementWire.DecodeRequest(wire).Command!;Assert.Equal(ColonyStateCodec.CommandHash(cmd),ColonyStateCodec.CommandHash(decoded));
        cmd.FoundingIntent!.NewArrivalCount=65;Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(new ColonyManagementWireRequest{RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=cmd}));
        cmd.FoundingIntent=null;string old=ColonyStateCodec.CommandHash(cmd);cmd.FoundingIntent=new();Assert.NotEqual(old,ColonyStateCodec.CommandHash(cmd));cmd.FoundingIntent=null;Assert.Equal(old,ColonyStateCodec.CommandHash(cmd));
        cmd.FoundingIntent=new(){ExistingResidentRosterIds=new(){"Ada Kerman","Ada Kerman"}};Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.CommandHash(cmd));
    }
    static ColonyCommand Assign(ColonyState s,ColonyEnvironment e,string id,ColonyFacility home,string roster)=>new(){OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="assignResident",Fields=new(){["RosterId"]=roster,["HomeFacilityId"]=home.Id,["HomePartId"]="42"}};
    static ColonyCommand Approval(ColonyState s,ColonyEnvironment e,string id,ColonyPlanningQuote q)=>new(){OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,Kind="approveFoundingPlan",QuoteId=q.Id,FoundingIntent=q.FoundingIntent};
    static ColonyState Approved(ColonyState s,ColonyEnvironment e,string id,ColonyPlanningQuote q)=>Accept(s,e,Approval(s,e,id,q));
    static ColonyState Accept(ColonyState s,ColonyEnvironment e,ColonyCommand c){var r=ColonyEngine.Execute(s,c,e);Assert.True(r.Outcome=="accepted",r.Reason);return r.State;}
    static (ColonyState,ColonyEnvironment,string,ColonyFacility) Fixture()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);var colony=s.Colonies[0];colony.Stock.Single(x=>x.Resource=="Supplies").Amount=100*U;
        var home=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),Name="Explicit domain provider certified home",State="operational",CertifiedHomes=2,PartIds=new(){42},HomePartPersistentIds=new(){42},Qualification=new(){HousingCertified=true}};
        home.HomePartCertificationHash=ColonyEngine.HomeMappingHash(home);colony.Facilities.Add(home);
        foreach(string role in new[]{"storage","power","workshop","lamp"})e.Planning.Assets.Add(new(){ColonyId=id,FacilityId=home.Id,Name=home.Name,Role=role,ContextKey=e.ContextKey,Qualified=true,Witness=new string('a',64)});
        e.People.Seats.Add(new(){FacilityId=home.Id,VesselId=home.VesselId,PartId=42,Capacity=2,Current=true,HousingCertified=true,UtilitiesQualified=true,CrewMutationSupported=true,ContextKey=e.ContextKey});
        string source=Guid.NewGuid().ToString("D");
        foreach(string name in new[]{"Ada Kerman","Bob Kerman","Jebediah Kerman"})
        {colony.VisitorRosterIds.Add(name);e.People.PresentByColony[id].Add(name);e.People.Roster.Add(new(){RosterId=name,Name=name,Trait="Engineer",Type="Crew",Status="Assigned",Current=true,ContextKey=e.ContextKey,VesselId=source,PartId=50,ProtectedMissionCrew=name.StartsWith("Jeb")});}
        e.People.Seats.Add(new(){VesselId=source,PartId=50,Capacity=3,Current=true,ContextKey=e.ContextKey,Occupants=new(){"Ada Kerman","Bob Kerman","Jebediah Kerman"}});
        e.People.Roster.Add(new(){RosterId="Grace Kerman",Name="Grace Kerman",Trait="Scientist",Type="Applicant",Status="Available",Current=true,ContextKey=e.ContextKey});
        e.People.Routes.Add(new(){Id="paid",Name="Explicit modeled domain transport",Body="Minmus",Hash=new string('b',64),Fare=1000,RecruitmentFee=2000,TravelSeconds=100,ConcurrentSeats=1,Qualified=true,Evidence="Domain test only"});
        return(s,e,id,home);
    }
}
