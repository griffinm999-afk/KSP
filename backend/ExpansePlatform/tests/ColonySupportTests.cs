using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonySupportTests
{
    static (ColonyState state, ColonyEnvironment env, ColonyRecord colony) Fixture()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 100);
        var colony = new ColonyRecord { Id=Guid.NewGuid().ToString("D"), Name="Test settlement", Site=new(){Body="Minmus"}, FoundedUt=100, SupportAccountedUt=100,
            Charter=new(){PopulationTarget=2,ReserveDays=3}, Stock=new(){new(){Resource="Supplies",Amount=20_000_000,ImportedAmount=20_000_000,Capacity=100_000_000}} };
        var home = new ColonyFacility {Id=Guid.NewGuid().ToString("D"), VesselId=Guid.NewGuid().ToString("D"),Name="Qualified habitat",State="operational",CertifiedHomes=2,Qualification=new(){HousingCertified=true,PowerReliable=true,HeatSafe=true}};
        colony.Facilities.Add(home);state.Colonies.Add(colony);
        var env = new ColonyEnvironment {WorldId=state.WorldId,ContextKey="test",Ut=100,Support=new(){Ready=true,PolicyId="test-support",PolicyHash="fixed-policy",MicroUnitsPerPersonDay=1_000_000},
            People=new(){PresenceComplete=true,PresentByColony=new(){[colony.Id]=new(){"Visitor A","Visitor B","Visitor C"}},Seats=new(){new(){FacilityId=home.Id,VesselId=home.VesselId,PartId=42,Capacity=2,Current=true,HousingCertified=true,UtilitiesQualified=true,ContextKey="test"}}}};
        return (state,env,colony);
    }
    static ColonyCommand Commission(ColonyState state,ColonyEnvironment env,ColonyRecord colony)=>new(){Kind="commissionSupport",OperationId=Guid.NewGuid().ToString("D"),ColonyId=colony.Id,ContextKey="test",ExpectedRevision=state.Revision,
        Fields=new(){["PolicyId"]=env.Support.PolicyId,["PolicyHash"]=env.Support.PolicyHash,["QuotedReserveMicroUnits"]="9000000"}};

    [Fact]
    public void CommissionIncludesVisitorsAndStartsWithoutRetroactiveConsumption()
    {
        var (state,env,colony)=Fixture();env.Ut=100+10*ColonyLimits.KerbinDay;
        var request=Commission(state,env,colony);var result=ColonyEngine.Execute(state,request,env);
        Assert.Equal("accepted",result.Outcome);var accepted=result.State;
        Assert.Equal(20_000_000,accepted.Colonies[0].Stock[0].Amount);Assert.Equal(9_000_000,accepted.Colonies[0].Stock[0].SupportFloor);
        Assert.Equal(3,accepted.Colonies[0].VisitorRosterIds.Count);Assert.Equal("duplicate",ColonyEngine.Execute(accepted,request,env).Outcome);
        env.Ut+=ColonyLimits.KerbinDay;var day=ColonyEngine.Advance(accepted,env);
        Assert.Equal(17_000_000,day.Colonies[0].Stock[0].Amount);Assert.Equal(3_000_000,day.Colonies[0].SupportConsumedMicroUnits);
        Assert.Equal("rejected",ColonyEngine.Execute(day,Commission(day,env,colony),env).Outcome);
    }
    [Theory]
    [InlineData("unpaid")][InlineData("presence")][InlineData("power")][InlineData("homes")][InlineData("external")][InlineData("staleQuote")]
    public void CommissionRejectsUnqualifiedOrUnfundedSupport(string cause)
    {
        var (state,env,colony)=Fixture();var request=Commission(state,env,colony);
        switch(cause){case "unpaid":colony.Stock[0].Amount=0;colony.Stock[0].ImportedAmount=0;break;case "presence":env.People.PresenceComplete=false;break;
            case "power":env.People.Seats[0].UtilitiesQualified=false;break;case "homes":env.People.Seats[0].Capacity=1;break;
            case "external":env.Support.OtherLifeSupportInstalled=true;break;case "staleQuote":env.People.PresentByColony[colony.Id].Add("Visitor D");break;}
        var result=ColonyEngine.Execute(state,request,env);Assert.Equal("rejected",result.Outcome);Assert.Null(result.State.Colonies[0].SupportCommissionedUt);Assert.Empty(result.State.Effects);
    }
    [Fact]
    public void InstallingAnotherSupportOwnerNeverDoubleConsumesOrCreatesLaterDebt()
    {
        var (state,env,colony)=Fixture();state=ColonyEngine.Execute(state,Commission(state,env,colony),env).State;
        env.Support.OtherLifeSupportInstalled=true;env.Ut+=10*ColonyLimits.KerbinDay;state=ColonyEngine.Advance(state,env);
        Assert.Equal(20_000_000,state.Colonies[0].Stock[0].Amount);Assert.Contains("ownership held",state.Colonies[0].SupportStatus);
        env.Support.OtherLifeSupportInstalled=false;env.Ut+=ColonyLimits.KerbinDay;state=ColonyEngine.Advance(state,env);
        Assert.Equal(17_000_000,state.Colonies[0].Stock[0].Amount);Assert.Equal(3_000_000,state.Colonies[0].SupportConsumedMicroUnits);
    }
}
