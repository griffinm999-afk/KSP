using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Independent economic tests: explicit detached provider fixtures do not
// certify native placement, modules, terrain, or physical production.
public sealed class ColonyProductionInvariantTests
{
    const long U=ColonyLimits.Units;
    static string Id()=>Guid.NewGuid().ToString("D");
    static string Hash(string text)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(text));
    static ColonyWolfIngredient Point(string resource,int amount)=>new(){Resource=resource,Points=amount};
    static ColonyWolfRecipe WolfRecipe(string resource,long funds)=>new(){Id=resource+" installed fixture",PartName=resource+" fixture",Tech="fixture-tech",
        ConfigurationHash=Hash(resource),Funds=funds,Outputs=[Point(resource,5)],Inputs=resource=="Power"?[]:[Point(resource+"Vein",5),Point("Power",5)]};
    internal static (ColonyState State,ColonyEnvironment Env,string ColonyId,ColonyFoundingIntent Intent) Fixture(int packages=1,bool ownStock=true)
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);var colony=s.Colonies.Single();colony.Site.Biome="Flats";
        if(ownStock){colony.Stock.Single(r=>r.Resource=="MaterialKits").Amount=100*U;colony.Stock.Single(r=>r.Resource=="Supplies").Amount=100*U;}
        foreach(string name in new[]{"production-test","feed-test"})
        {
            bool farm=name=="production-test";
            var template=new ColonyTemplate {Id=name,Name="Reviewed finite fixture "+name,Hash=Hash(name),CraftSha256=Hash("craft:"+name),BuildFunds=farm?200:300,
                LaborSeconds=farm?20:30,WidthMeters=10,LengthMeters=10,MinX=-4,MaxX=4,MinZ=-4,MaxZ=4,ExpectedPartCount=farm?1:2,
                Workers=farm?1:0,WorkerTrait=farm?"Scientist":"",RuntimeCertified=true,CertificationEvidence="Explicit domain economics fixture only",
                Materials=[new(){Resource="MaterialKits",Amount=(farm?2:3)*U}]};e.Templates.Add(template);
            for(int index=0;index<4;index++)colony.Plots.Add(new(){Id=Id(),TemplateId=name,TemplateHash=template.Hash,WidthMeters=10,LengthMeters=10,
                SurveyHash=Hash("plot:"+name+index),EvidenceContext=e.ContextKey,ObservedUt=e.Ut,SurveyProvenance="Explicit domain fixture survey"});
        }
        var source=new ColonyFacility {Id=Id(),VesselId=Id(),Name="Observed ordinary visitor cabin",PartIds=[99]};colony.Facilities.Add(source);
        var seat=new ColonySeatWitness {FacilityId=source.Id,VesselId=source.VesselId,PartId=99,Capacity=4,Body="Minmus",Current=true,ContextKey=e.ContextKey,
            SurfaceTransferSupported=true,CrewMutationSupported=true,Evidence="Explicit domain fixture physical crew provider"};e.People.Seats.Add(seat);
        for(int index=0;index<4;index++)
        {
            string roster="ordinary scientist "+index;colony.VisitorRosterIds.Add(roster);e.People.PresentByColony[id].Add(roster);seat.Occupants.Add(roster);
            e.People.Roster.Add(new(){RosterId=roster,Name=roster,Trait="Scientist",Type="Crew",Status="Assigned",VesselId=source.VesselId,PartId=99,Current=true,ContextKey=e.ContextKey});
        }
        e.UnlockedTech.Add("fixture-tech");e.Wolf.Ready=true;e.Wolf.Provider="Explicit installed WOLF fixture";e.Wolf.DepotConstruction=WolfRecipe("Power",500);
        e.Wolf.Recipes=[WolfRecipe("Substrate",100),WolfRecipe("Water",200),WolfRecipe("Power",50)];
        e.Wolf.Sites.Add(new(){ColonyId=id,ContextKey=e.ContextKey,ObservedUt=e.Ut,Depot=new(){Body="Minmus",Biome="Flats",Exists=true,Established=true,Surveyed=true,
            Streams=[new(){Resource="SubstrateVein",Incoming=100},new(){Resource="WaterVein",Incoming=100},new(){Resource="Power",Incoming=5}]}});
        var farmTemplate=e.Templates.Single(t=>t.Id=="production-test");var feedTemplate=e.Templates.Single(t=>t.Id=="feed-test");
        var recipe=new ColonyProductionRecipe {Id="cultivate-substrate-v1",Name="Reviewed Cultivate(S) finite fixture",TemplateId=farmTemplate.Id,TemplateHash=farmTemplate.Hash,
            HopperTemplateId=feedTemplate.Id,HopperTemplateHash=feedTemplate.Hash,CraftPartId=100,PartName="Duna.Agriculture",OptionIndex=1,OptionHash=Hash("Cultivate(S)"),
            Preconfigured=true,Inputs=[new(){Resource="Substrate",UnitsPerSecond=.1},new(){Resource="Water",UnitsPerSecond=.1},new(){Resource="ElectricCharge",UnitsPerSecond=10}],
            Outputs=[new(){Resource="Supplies",UnitsPerSecond=.05}],Feeds=[
                new(){CraftPartId=115,PartName="HarvestingHopper375",Resource="Substrate",OptionIndex=0,OptionHash=Hash("substrate option"),WolfPoints=5,NominalUnitsPerSecond=.1},
                new(){CraftPartId=120,PartName="HarvestingHopper375",Resource="Water",OptionIndex=1,OptionHash=Hash("water option"),WolfPoints=5,NominalUnitsPerSecond=.1}]};
        recipe.ConfigurationHash=ColonyStateCodec.ProductionRecipeHash(recipe);e.Production.Recipes.Add(recipe);
        return(s,e,id,new(){FillPopulationTarget=false,Production=new(){Mode="localInvestment",RecipeId=recipe.Id,PackageCount=packages}});
    }
    static ColonyCommand Approval(ColonyState state,ColonyEnvironment env,ColonyPlanningQuote quote)=>new(){OperationId=Id(),Kind="approveFoundingPlan",
        ColonyId=quote.ColonyId,ExpectedRevision=state.Revision,ContextKey=env.ContextKey,QuoteId=quote.Id,FoundingIntent=quote.FoundingIntent};
    internal static ColonyState Approve(ColonyState state,ColonyEnvironment env,string id,ColonyFoundingIntent intent)
    {
        var quote=ColonyEngine.QuoteFoundingPlan(state,id,env,intent);Assert.True(quote.CanApprove,string.Join(" ",quote.Blockers));
        var result=ColonyEngine.Execute(state,Approval(state,env,quote),env);Assert.True(result.Outcome=="accepted",result.Reason);return result.State;
    }
    static ColonyCommand Cancel(ColonyState state,ColonyEnvironment env)=>new(){OperationId=Id(),Kind="cancelColonyPlan",ColonyId=state.Plans.Single().ColonyId,
        TargetId=state.Plans.Single().Id,ExpectedRevision=state.Revision,ContextKey=env.ContextKey};
    internal static ColonyState ReserveWolfChild(ColonyState state,ColonyEnvironment env)
    {
        for(int index=0;index<12 && state.WolfOrders.Count==0;index++)state=ColonyEngine.RunPlanning(state,env);
        Assert.Single(state.WolfOrders);return state;
    }
    [Theory] [InlineData(1,350)] [InlineData(2,750)] [InlineData(4,1550)]
    public void MultiPackageBillPaysEachBuildingAndOneCombinedDependencyPackage(int count,long dependencyFunds)
    {
        var(s,e,id,intent)=Fixture(count);var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        Assert.Equal(count,q.ProductionInvestments.Count);Assert.Equal(count*2,q.Buildings.Count(b=>b.Role is "production" or "productionFeed"));
        var wolf=Assert.Single(q.ProductionInvestments.Where(i=>i.Wolf.Id.Length>0)).Wolf;Assert.Equal(dependencyFunds,wolf.Funds);
        Assert.All(wolf.Demands,d=>Assert.Equal(5*count,d.Points));Assert.Equal(count,q.BootstrapWorkers.Count);Assert.Equal(count,q.BootstrapWorkers.Select(w=>w.RosterId).Distinct().Count());
        Assert.Equal(q.Buildings.Sum(b=>b.Funds)+q.Imports.Sum(i=>i.Funds)+wolf.Funds,q.TotalFunds);
        Assert.Equal(q.Buildings.Count,q.Buildings.Select(b=>b.PlotId).Distinct().Count());Assert.Equal((5+5*count)*U,q.Materials.Single().Amount);
        Assert.Empty(s.WolfOrders);Assert.Empty(s.Plans);Assert.Equal(100*U,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
    }
    [Fact] public void NominalOutputDoesNotReduceZeroOutputFallbackOrCreditOwnedStock()
    {
        var(s,e,id,intent)=Fixture(2,false);var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove,string.Join(" ",q.Blockers));
        Assert.Equal(4*U,q.StartupSupportReserve);Assert.Equal(q.StartupSupportReserve,q.Imports.Where(i=>i.Resource=="Supplies").Sum(i=>i.Amount));
        Assert.True(q.ProductionInvestments.Sum(i=>i.NominalSuppliesPerDay)>4);Assert.All(s.Colonies.Single().Stock,r=>Assert.Equal(0,r.Amount));
        Assert.Equal(q.Buildings.Sum(b=>b.Funds)+q.Imports.Sum(i=>i.Funds)+ColonyEngine.ProductionExtraFunds(q),q.TotalFunds);
    }
    [Fact] public void ExactIntentAndInstalledFeedTermsBindApprovalWithoutPartialClaims()
    {
        var(s,e,id,intent)=Fixture();var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove);
        var command=Approval(s,e,q);e.Production.Recipes.Single().Feeds.Single(f=>f.Resource=="Water").WolfPoints=6;
        e.Production.Recipes.Single().ConfigurationHash=ColonyStateCodec.ProductionRecipeHash(e.Production.Recipes.Single());
        var stale=ColonyEngine.Execute(s,command,e);Assert.Equal("rejected",stale.Outcome);Assert.Same(s,stale.State);Assert.Empty(s.Plans);Assert.All(s.Colonies.Single().Plots,p=>Assert.Empty(p.ReservedBy));
    }
    [Fact] public void CombinedInvestmentCannotSpendBelowTheGlobalCashFloor()
    {
        var(s,e,id,intent)=Fixture(2);var q=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.True(q.CanApprove);
        e.AvailableFunds=q.TotalFunds+s.Colonies.Single().Charter.CashFloor-1;
        var rejected=ColonyEngine.Execute(s,Approval(s,e,q),e);Assert.Equal("rejected",rejected.Outcome);Assert.Empty(s.Plans);Assert.Empty(s.WolfOrders);
        Assert.All(s.Colonies.Single().Stock,r=>Assert.Equal(0,r.Reserved));
    }
    [Fact] public void FutureDependencyClaimBecomesOnePaymentWithoutChangingTotalCommittedCash()
    {
        var(s,e,id,intent)=Fixture(2);s=Approve(s,e,id,intent);long maximum=ColonyEngine.PendingCash(s),spent=s.Colonies.Single().SpentFunds;
        var reviewed=s.Plans.Single().Quote;long wolfFunds=ColonyEngine.ProductionExtraFunds(reviewed);
        Assert.Equal(wolfFunds,ColonyEngine.ProductionFutureFunds(s.Plans.Single()));s=ReserveWolfChild(s,e);
        Assert.Equal(maximum,ColonyEngine.PendingCash(s));Assert.Equal(0,ColonyEngine.ProductionFutureFunds(s.Plans.Single()));
        var child=Assert.Single(s.WolfOrders);Assert.Equal(ColonyEngine.PlanningChildId(s.Plans.Single().Id,"production-0:wolf"),child.Id);
        Assert.Equal(-wolfFunds,Assert.Single(s.Effects.Where(effect=>effect.Kind=="wolfPurchase")).FundsDelta);Assert.Equal(spent,s.Colonies.Single().SpentFunds);
        var again=ColonyEngine.RunPlanning(s,e);Assert.Single(again.WolfOrders);Assert.Equal(maximum,ColonyEngine.PendingCash(again));
        Assert.Equal(reviewed.Id,ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(again)).Plans.Single().Quote.Id);
    }
    [Fact] public void SameCostAlternateModuleCannotReplaceTheReviewedSavedDependencyHardware()
    {
        var(s,e,id,intent)=Fixture();s=ReserveWolfChild(Approve(s,e,id,intent),e);var altered=ColonyStateCodec.Copy(s);
        var child=altered.WolfOrders.Single();child.Quote.Modules.First().Recipe.Id+=" alternate";child.Quote.Modules.First().Recipe.PartName+=" alternate";
        child.Quote.Id=ColonyStateCodec.WolfQuoteHash(child.Quote);
        Assert.Contains("paid reservation lineage",Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(altered)).Message);
        Assert.NotEqual(child.Quote.Id,s.WolfOrders.Single().Quote.Id);
    }
    [Fact] public void FutureInvestmentFundsCannotDisappearBeforeAnActualChildReservation()
    {
        var(s,e,id,intent)=Fixture(2);s=Approve(s,e,id,intent);var altered=ColonyStateCodec.Copy(s);
        altered.Plans.Single().RemainingFunds-=ColonyEngine.ProductionExtraFunds(altered.Plans.Single().Quote);
        Assert.Contains("future-funding",Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(altered)).Message);
        Assert.Empty(altered.WolfOrders);Assert.Equal(s.Plans.Single().Quote.TotalFunds,s.Plans.Single().RemainingFunds);
    }
    [Fact] public void FourProductionPackagesRequireFourUnreservedOrdinaryWorkers()
    {
        var(s,e,id,intent)=Fixture(4);e.People.Roster.First().ProtectedMissionCrew=true;
        var quote=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);Assert.False(quote.CanApprove);
        Assert.Equal(3,quote.BootstrapWorkers.Count);Assert.DoesNotContain(quote.BootstrapWorkers,w=>w.RosterId==e.People.Roster.First().RosterId);
        Assert.Contains(quote.Blockers,b=>b.Contains("existing workers are preserved"));Assert.Empty(s.Plans);
    }
    [Fact] public void UnstartedCancellationReleasesOnlyFutureClaimsAndPreservesVisitorsAndStock()
    {
        var(s,e,id,intent)=Fixture(2);s=Approve(s,e,id,intent);long supplies=s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount;
        var cancelled=ColonyEngine.Execute(s,Cancel(s,e),e);Assert.Equal("accepted",cancelled.Outcome);s=cancelled.State;
        Assert.Equal(0,ColonyEngine.PendingCash(s));Assert.Equal(0,s.Plans.Single().RemainingFunds);Assert.All(s.Plans.Single().Production,c=>Assert.Equal("cancelled",c.State));
        Assert.Empty(s.WolfOrders);Assert.All(s.Colonies.Single().Plots,p=>Assert.Empty(p.ReservedBy));Assert.All(s.Colonies.Single().Stock,r=>Assert.Equal(0,r.Reserved));
        Assert.Equal(4,s.Colonies.Single().VisitorRosterIds.Count);Assert.Equal(supplies,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ParentCancellationRetainsDispatchedDependencyTermsAndCannotRefundOrRebuy(bool paid)
    {
        var(s,e,id,intent)=Fixture(2);s=ReserveWolfChild(Approve(s,e,id,intent),e);var child=s.WolfOrders.Single();var quoted=child.Quote.Id;long before=e.AvailableFunds;
        if(paid)
        {
            var effect=s.Effects.Single(effect=>effect.Kind=="wolfPurchase");s=ColonyEngine.MarkEffectApplying(s,effect.Id,"fixture funds","before");
            s=ColonyEngine.CompleteFundsEffect(s,effect.Id,e.AvailableFunds,e.AvailableFunds-child.Quote.Funds,e.Ut,"after");e.AvailableFunds-=child.Quote.Funds;
        }
        long fundsBeforeCancel=e.AvailableFunds,spentBeforeCancel=s.Colonies.Single().SpentFunds;
        var cancelled=ColonyEngine.Execute(s,Cancel(s,e),e);Assert.True(cancelled.Outcome=="accepted",cancelled.Reason);s=cancelled.State;
        Assert.Single(s.WolfOrders);Assert.Equal(quoted,s.WolfOrders.Single().Quote.Id);Assert.Equal(paid,s.WolfOrders.Single().FundsPaid);
        Assert.Equal(fundsBeforeCancel,e.AvailableFunds);Assert.Equal(spentBeforeCancel,s.Colonies.Single().SpentFunds);Assert.Equal(paid?before-child.Quote.Funds:before,e.AvailableFunds);
        Assert.Equal(0,ColonyEngine.PlanningCommittedFunds(s));Assert.Equal(s.Effects.Where(effect=>effect.State=="prepared"&&effect.FundsDelta<0).Sum(effect=>-effect.FundsDelta),ColonyEngine.PendingCash(s));
        var again=ColonyEngine.RunPlanning(s,e);Assert.Single(again.WolfOrders);Assert.Equal(quoted,again.WolfOrders.Single().Quote.Id);Assert.All(again.Plans.Single().Production,c=>Assert.Empty(c.Steps));
    }
}
