using Expanse.Colony.Acceptance;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyAcceptanceChecksTests
{
    const long U=ColonyLimits.Units;
    static (ColonyManagementSnapshot before,ColonyManagementSnapshot after,string colony) SupportFixture()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();var colony=s.Colonies.Single();
        colony.Stock=e.EconomyPolicies.Single().Stores.Select(st=>new ColonyStock {Resource=st.Resource,Capacity=st.Capacity,Amount=st.Resource=="Supplies"?100*U:0}).ToList();
        colony.SupportCommissionedUt=100;colony.SupportAccountedUt=100;colony.SupportPolicyHash=e.Support.PolicyHash;colony.SupportMicroUnitsPerPersonDay=U;colony.SupportStatus="Supported";
        colony.VisitorRosterIds=new(){"actual visitor1","actual visitor2"};
        var before=new ColonyManagementSnapshot {State=s,ContextKey=e.ContextKey,ObservedUt=100};
        var after=new ColonyManagementSnapshot {State=ColonyStateCodec.Copy(s),ContextKey=e.ContextKey,ObservedUt=100+3*ColonyLimits.KerbinDay};
        var changed=after.State.Colonies.Single();changed.SupportAccountedUt=after.ObservedUt;changed.SupportConsumedMicroUnits=6*U;changed.Stock.Single(st=>st.Resource=="Supplies").Amount-=6*U;
        return(before,after,id);
    }
    [Fact] public void ActualThreeDayIntegralAcceptsPaidArrivalButRejectsDoubleConsumption()
    {
        var(a,b,id)=SupportFixture();b.State!.Shipments.Add(new(){Id=Guid.NewGuid().ToString("D"),ColonyId=id,Resource="Supplies",Amount=10*U,Kind="import",State="arrived"});b.State.Colonies.Single().Stock.Single(st=>st.Resource=="Supplies").Amount+=10*U;
        AcceptanceChecks.SupportDays(a,b,id);
        b.State.Colonies.Single().SupportConsumedMicroUnits=12*U;
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.SupportDays(a,b,id));
    }
    [Fact] public void StockFabricationCannotPassSupportIntegralAlone()
    {
        var(a,b,id)=SupportFixture();b.State!.Colonies.Single().Stock.Single(st=>st.Resource=="Supplies").Amount++;
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.SupportDays(a,b,id));
    }
    [Fact] public void ThreeDaysRequiresActualChronologyAndStablePopulation()
    {
        var(a,b,id)=SupportFixture();b.State!.Colonies.Single().SupportAccountedUt--;
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.SupportDays(a,b,id));
        (a,b,id)=SupportFixture();b.State!.Colonies.Single().VisitorRosterIds.Add("extra fabricated person");
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.SupportDays(a,b,id));
    }
    [Fact] public void PreservingCountsDoesNotHideReplacedPhysicalMemberOrLostNamedCrew()
    {
        var(a,b,id)=SupportFixture();var facility=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),PartIds=new(){11,12}};
        a.State!.Colonies.Single().Facilities.Add(facility);b.State!.Colonies.Single().Facilities.Add(new(){Id=facility.Id,VesselId=facility.VesselId,PartIds=new(){11,99}});
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.Preserved(a,b,id));
        b.State.Colonies.Single().Facilities.Single().PartIds=new(){11,12};b.State.Colonies.Single().VisitorRosterIds[0]="replacement person";
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.Preserved(a,b,id));
    }
    [Fact] public void UnknownExternalEffectCannotBeCountedAsNativeAcceptance()
    {
        var(a,b,id)=SupportFixture();b.State!.Effects.Add(new(){Id=Guid.NewGuid().ToString("D"),Kind="physicalTransfer",State="held"});
        Assert.Throws<InvalidDataException>(()=>AcceptanceChecks.SupportDays(a,b,id));
    }
}
