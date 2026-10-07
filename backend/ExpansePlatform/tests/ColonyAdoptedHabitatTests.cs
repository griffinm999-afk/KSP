using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Trusted native observations are constructed here. Actual KSP qualification
// remains a separate native check; no game, resources or crew are fabricated.
public sealed class ColonyAdoptedHabitatTests
{
    static string Id()=>Guid.NewGuid().ToString("D");
    public static (ColonyState State,ColonyRecord Colony,ColonyFacility Observed,ColonyEnvironment Env) Fixture()
    {
        var state=ColonyEngine.Create(Id(),100);
        var colony=new ColonyRecord {Id=Id(),Name="Existing Minmus colony",FoundedUt=50,SupportAccountedUt=100,
            Site=new(){Body="Minmus",Latitude=0,Longitude=0,RadiusMeters=400},Charter=new(){PopulationTarget=4,ResidentLimit=12}};
        colony.Stock.Add(new(){Resource="Supplies",Amount=100_000_000,ImportedAmount=100_000_000,Capacity=200_000_000});
        var saved=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Actual dedicated habitat",PartIds=[11,22,33]};
        colony.Facilities.Add(saved);state.Colonies.Add(colony);
        var observed=new ColonyFacility {Id=saved.Id,VesselId=saved.VesselId,Name=saved.Name,PartIds=[11,22,33],HomePartPersistentIds=[11,22],CertifiedHomes=8,
            Qualification=new(){Provider=ColonyEngine.AdoptedHabitatProvider,Context="loaded-packed",ObservedUt=100,HousingCertified=true,PlacementStable=true,
                PowerReliable=true,HeatSafe=true,InputsAccessible=true,BackgroundSupported=true,EvidenceHash=new string('a',64)}};
        observed.HomePartCertificationHash=ColonyEngine.HomeMappingHash(observed);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey="selected-save/epoch",Ut=100,AvailableFunds=2_000_000,
            Support=new(){Ready=true,PolicyId="installed-support",PolicyHash=new string('b',64),MicroUnitsPerPersonDay=1_000_000}};
        env.AdoptableFacilities.Add(observed);env.FacilitySites[observed.Id]=new(){Body="Minmus",Longitude=.01};env.BodyRadiiMeters["Minmus"]=60000;
        env.People.PresenceComplete=true;env.People.PresentByColony[colony.Id]=[];
        foreach(var id in observed.HomePartPersistentIds)env.People.Seats.Add(new(){FacilityId=saved.Id,VesselId=saved.VesselId,PartId=id,Capacity=4,
            Current=true,CrewMutationSupported=true,ContextKey=env.ContextKey,Occupants=[]});
        return(state,colony,observed,env);
    }
    static ColonyCommand Command(ColonyState state,ColonyRecord colony,ColonyFacility observed,ColonyEnvironment env)
    {
        var quote=ColonyEngine.QuoteAdoptedHabitat(state,colony.Id,observed.Id,env);
        return new(){Kind="qualifyAdoptedHabitat",OperationId=Id(),ColonyId=colony.Id,TargetId=observed.Id,ContextKey=env.ContextKey,
            ExpectedRevision=state.Revision,QuoteId=quote.Id,Fields=new(){["HabitatWitnessHash"]=quote.AdoptionWitnessHash}};
    }
    [Fact]
    public void FirstHomesQualifyWithoutCommissionedSupportOrAnyFinancialCrewStockMutation()
    {
        var(s,c,o,e)=Fixture();var before=ColonyStateCodec.Serialize(s);var command=Command(s,c,o,e);
        var result=ColonyEngine.Execute(s,command,e);Assert.Equal("accepted",result.Outcome);Assert.Equal(before,ColonyStateCodec.Serialize(s));
        var home=Assert.Single(result.State.Colonies.Single().Facilities);Assert.Equal(8,home.CertifiedHomes);Assert.Equal(new uint[]{11,22},home.HomePartPersistentIds);
        Assert.Equal(ColonyEngine.HomeMappingHash(home),home.HomePartCertificationHash);Assert.True(home.Qualification.HousingCertified);Assert.Equal("operational",home.State);
        Assert.Null(result.State.Colonies.Single().SupportCommissionedUt);Assert.Equal(c.Stock.Single().Amount,result.State.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(c.SpentFunds,result.State.Colonies.Single().SpentFunds);Assert.Equal(2_000_000,e.AvailableFunds);
        Assert.Empty(result.State.Effects);Assert.Empty(result.State.PeopleOperations);Assert.Empty(result.State.Colonies.Single().Residents);Assert.Empty(e.People.Roster);
        Assert.Empty(home.TemplateId);Assert.Empty(home.PlacementOperationId);Assert.Empty(home.ConstructionOrderId);Assert.Equal("physical",home.ProductionOwner);
        var replay=ColonyEngine.Execute(result.State,command,e);Assert.Equal("duplicate",replay.Outcome);Assert.Equal(ColonyStateCodec.Serialize(result.State),ColonyStateCodec.Serialize(replay.State));
        o.HomePartPersistentIds[0]=99;Assert.Equal(11u,home.HomePartPersistentIds[0]);
    }
    [Theory]
    [InlineData("members")][InlineData("home")][InlineData("capacity")][InlineData("vessel")][InlineData("site")][InlineData("support")][InlineData("world")][InlineData("context")][InlineData("revision")]
    public void ReviewedChangesCannotApplyOldQualification(string change)
    {
        var(s,c,o,e)=Fixture();var command=Command(s,c,o,e);
        switch(change){case "members":o.PartIds.Add(44);break;case "home":o.HomePartPersistentIds=[11,33];o.HomePartCertificationHash=ColonyEngine.HomeMappingHash(o);break;
            case "capacity":o.CertifiedHomes=9;break;case "vessel":o.VesselId=Id();break;case "site":e.FacilitySites[o.Id].Latitude+=.0001;break;
            case "support":e.Support.PolicyHash=new string('c',64);break;case "world":e.WorldId=Id();break;case "context":e.ContextKey+="new";break;case "revision":s.Revision++;break;}
        Assert.Equal("rejected",ColonyEngine.Execute(s,command,e).Outcome);Assert.Equal(0,c.Facilities.Single().CertifiedHomes);
    }
    [Theory]
    [InlineData("workshop")][InlineData("stale")][InlineData("unloaded")][InlineData("power")][InlineData("heat")][InlineData("inputs")][InlineData("support")][InlineData("otherSupport")]
    [InlineData("mapping")][InlineData("crewProvider")][InlineData("outside")][InlineData("otherBody")][InlineData("wrongOwner")][InlineData("duplicateSeat")][InlineData("duplicateHome")][InlineData("incomplete")]
    public void UnqualifiedDedicatedCabinsAndProvidersRefuse(string failure)
    {
        var(s,c,o,e)=Fixture();
        switch(failure){case "workshop":o.Qualification.HousingCertified=false;break;case "stale":o.Qualification.ObservedUt=80;break;case "unloaded":o.Qualification.Context="unloaded";break;
            case "power":o.Qualification.PowerReliable=false;break;case "heat":o.Qualification.HeatSafe=false;break;case "inputs":o.Qualification.InputsAccessible=false;break;
            case "support":e.Support.Ready=false;break;case "otherSupport":e.Support.OtherLifeSupportInstalled=true;break;case "mapping":e.People.Seats[0].Capacity=2;break;
            case "crewProvider":e.People.Seats[0].CrewMutationSupported=false;break;case "outside":e.FacilitySites[o.Id].Longitude=20;break;case "otherBody":e.FacilitySites[o.Id].Body="Mun";break;
            case "wrongOwner":s.Colonies.Add(new(){Id=Id(),Name="Other",Facilities=[new(){Id=Id(),VesselId=Id(),PartIds=[11]}]});break;
            case "duplicateSeat":e.People.Seats.Add(e.People.Seats[0]);break;case "duplicateHome":o.HomePartPersistentIds=[11,11];break;case "incomplete":e.People.PresenceComplete=false;break;}
        Assert.False(ColonyEngine.QuoteAdoptedHabitat(s,c.Id,o.Id,e).CanApprove);Assert.Equal(0,c.Facilities.Single().CertifiedHomes);
    }
    [Fact]
    public void FreshStillQualifiedObservationDoesNotInvalidateReviewJustBecauseTimeAdvances()
    {
        var(s,c,o,e)=Fixture();var first=ColonyEngine.QuoteAdoptedHabitat(s,c.Id,o.Id,e);
        e.Ut++;o.Qualification.ObservedUt++;e.AvailableFunds++;
        var next=ColonyEngine.QuoteAdoptedHabitat(s,c.Id,o.Id,e);
        Assert.True(next.CanApprove);Assert.Equal(first.Id,next.Id);Assert.Equal(first.AdoptionWitnessHash,next.AdoptionWitnessHash);
    }
    [Fact]
    public void RequalificationCannotChangePreviouslyCertifiedCabinsOrResidentCapacity()
    {
        var(s,c,o,e)=Fixture();var accepted=ColonyEngine.Execute(s,Command(s,c,o,e),e);
        Assert.Equal("accepted",accepted.Outcome);
        o.HomePartPersistentIds=[11];o.CertifiedHomes=4;o.HomePartCertificationHash=ColonyEngine.HomeMappingHash(o);
        Assert.False(ColonyEngine.QuoteAdoptedHabitat(accepted.State,c.Id,o.Id,e).CanApprove);
        Assert.Equal(8,accepted.State.Colonies.Single().Facilities.Single().CertifiedHomes);
    }
    [Fact]
    public void PaidTemplateAuthorityCannotBeReplacedAndExtraClientFieldsCannotGrantHomes()
    {
        var(s,c,o,e)=Fixture();c.Facilities.Single().TemplateId="paid-habitat";
        Assert.False(ColonyEngine.QuoteAdoptedHabitat(s,c.Id,o.Id,e).CanApprove);
        c.Facilities.Single().TemplateId="";var cmd=Command(s,c,o,e);cmd.Fields["CertifiedHomes"]="99";
        Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);
    }
    [Fact]
    public void RecordedCoordinateBitsQualifyAndPreserveTheExistingHomeMappingSeal()
    {
        var(s,c,o,e)=Fixture();
        c.Site.Latitude=e.FacilitySites[o.Id].Latitude=BitConverter.Int64BitsToDouble(4607167183711359775);
        c.Site.Longitude=e.FacilitySites[o.Id].Longitude=BitConverter.Int64BitsToDouble(4607161497937331200);
        var seal=o.HomePartCertificationHash;var before=ColonyStateCodec.Serialize(s);
        var result=ColonyEngine.Execute(s,Command(s,c,o,e),e);
        Assert.Equal("accepted",result.Outcome);Assert.Equal(seal,result.State.Colonies.Single().Facilities.Single().HomePartCertificationHash);
        Assert.Equal(before,ColonyStateCodec.Serialize(s));Assert.Empty(result.State.Effects);Assert.Equal(0,result.State.Colonies.Single().SpentFunds);
    }
    [Theory]
    [InlineData("latitude")][InlineData("longitude")][InlineData("radius")]
    public void ExactColonySiteBitChangesCannotReuseAnOldHabitatReview(string term)
    {
        var(s,c,o,e)=Fixture();
        c.Site.Latitude=e.FacilitySites[o.Id].Latitude=BitConverter.Int64BitsToDouble(4607167183711359775);
        c.Site.Longitude=e.FacilitySites[o.Id].Longitude=BitConverter.Int64BitsToDouble(4607161497937331200);
        var command=Command(s,c,o,e);
        switch(term){case "latitude":c.Site.Latitude=Math.BitIncrement(c.Site.Latitude);break;case "longitude":c.Site.Longitude=Math.BitIncrement(c.Site.Longitude);break;case "radius":c.Site.RadiusMeters=Math.BitIncrement(c.Site.RadiusMeters);break;}
        Assert.Equal("rejected",ColonyEngine.Execute(s,command,e).Outcome);Assert.Equal(0,c.Facilities.Single().CertifiedHomes);
    }
}
