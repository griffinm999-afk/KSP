using System.Globalization;
using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyWolfVectorTests
{
    static string Id()=>Guid.NewGuid().ToString("D");
    static ColonyWolfIngredient D(string resource,int points)=>new(){Resource=resource,Points=points};
    static ColonyWolfRecipe Recipe(string id,long cost,int power,string resource,int output=5)=>new()
    {
        Id=id,PartName=id,ConfigurationHash=new('a',64),Tech="science",Funds=cost,
        Inputs=resource=="Power" ? [] : (power==0 ? new[]{D(resource+"Vein",output)} : new[]{D("Power",power),D(resource+"Vein",output)}).ToList(),Outputs=[D(resource,output)]
    };
    static (ColonyState State,ColonyEnvironment Env,ColonyRecord Colony) Fixture(int availablePower=5)
    {
        var colony=new ColonyRecord {Id=Id(),Name="Explicit vector fixture",Site=new(){Body="Minmus",Biome="Flats"},Charter=new(){CashFloor=100,FoundingBudget=1000000,SpendingLimit=1000000}};
        var state=ColonyEngine.Create(Id(),0);state.Colonies.Add(colony);
        var env=new ColonyEnvironment {WorldId=state.WorldId,ContextKey="selected-vector-fixture",AvailableFunds=1000000,UnlockedTech=["science"],Wolf=new(){Ready=true,Provider="Explicit pure fixture"}};
        env.Wolf.DepotConstruction=Recipe("Depot",10000,0,"Power");
        env.Wolf.Recipes=[Recipe("SubstrateRaw",1000,5,"Substrate"),Recipe("WaterRaw",1000,5,"Water"),Recipe("Power",500,0,"Power")];
        env.Wolf.Sites=[new(){ColonyId=colony.Id,ContextKey=env.ContextKey,Depot=new(){Body="Minmus",Biome="Flats",Exists=true,Established=true,Surveyed=true,
            Streams=[new(){Resource="Power",Incoming=availablePower},new(){Resource="SubstrateVein",Incoming=1000},new(){Resource="WaterVein",Incoming=1000}]}}];
        return(state,env,colony);
    }
    static ColonyCommand Approve(ColonyState state,ColonyEnvironment env,ColonyWolfQuote q)=>new()
    {
        OperationId=Id(),ColonyId=q.ColonyId,ContextKey=env.ContextKey,ExpectedRevision=state.Revision,Kind="approveWolfSupplies",QuoteId=q.Id,
        Fields=q.Demands.ToDictionary(d=>"Demand:"+d.Resource,d=>d.Points.ToString(CultureInfo.InvariantCulture))
    };
    static ColonyState PayAndReady(ColonyState state,ColonyEnvironment env,ColonyWolfQuote quote)
    {
        var result=ColonyEngine.Execute(state,Approve(state,env,quote),env);Assert.True(result.Outcome=="accepted",result.Reason);
        var effect=result.State.Effects.Single();
        var paid=ColonyEngine.CompleteFundsEffect(ColonyEngine.MarkEffectApplying(result.State,effect.Id,"fixture funds","before"),effect.Id,
            env.AvailableFunds,env.AvailableFunds-quote.Funds,env.Ut,"after");
        env.Ut+=quote.LaborSeconds;return ColonyEngine.Advance(paid,env);
    }
    [Fact] public void SharedPowerIsPurchasedOnceAgainstOneActualBeforeDepot()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",5),D("Substrate",5)],e);
        Assert.Equal(2500,q.Funds);Assert.Equal(32400,q.LaborSeconds);Assert.Equal(1,q.Modules.Single(m=>m.Recipe.PartName=="Power").Count);
        Assert.Equal(10,q.After.Streams.Single(r=>r.Resource=="Power").Incoming);Assert.Equal(10,q.After.Streams.Single(r=>r.Resource=="Power").Outgoing);
        Assert.Equal(5,ColonyEngine.WolfAvailable(q.After,"Substrate"));Assert.Equal(5,ColonyEngine.WolfAvailable(q.After,"Water"));
        Assert.Equal(ColonyStateCodec.WolfDepotHash(e.Wolf.Sites.Single().Depot),ColonyStateCodec.WolfDepotHash(q.Before));Assert.Empty(c.Stock);Assert.Empty(s.WolfOrders);
    }
    [Fact] public void ExplicitUnallocatedPowerRequirementIsAdditionalToRawDependencies()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Power",2),D("Substrate",5),D("Water",5)],e);
        Assert.Equal(2,q.Modules.Single(m=>m.Recipe.PartName=="Power").Count);Assert.True(ColonyEngine.WolfAvailable(q.After,"Power")>=2);
        Assert.Equal(3000,q.Funds);
    }
    [Fact] public void FullInvestmentCostSelectsCostlyRawRecipeWhenItAvoidsExpensiveSharedPower()
    {
        var (s,e,c)=Fixture(0);
        e.Wolf.Recipes=[Recipe("SubCheap",1,4,"Substrate",1),Recipe("SubLowPower",5,1,"Substrate",1),Recipe("WaterCheap",1,4,"Water",1),Recipe("WaterLowPower",5,1,"Water",1),Recipe("Power",10,0,"Power",1)];
        var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",1),D("Substrate",1)],e);
        Assert.Equal(30,q.Funds);Assert.Contains(q.Modules,m=>m.Recipe.Id=="SubLowPower");Assert.Contains(q.Modules,m=>m.Recipe.Id=="WaterLowPower");
        Assert.DoesNotContain(q.Modules,m=>m.Recipe.Id.EndsWith("Cheap"));
    }
    [Fact] public void PowerSelectionIncludesActualModuleRoundingAndUnlockedTechnology()
    {
        var (s,e,c)=Fixture(0);e.Wolf.Recipes.RemoveAll(r=>r.Outputs[0].Resource=="Power");
        e.Wolf.Recipes.Add(Recipe("SmallPower",5,0,"Power",5));e.Wolf.Recipes.Add(Recipe("BigPower",20,0,"Power",50));
        var locked=Recipe("LockedCheap",1,0,"Power",100);locked.Tech="locked";e.Wolf.Recipes.Add(locked);
        e.Wolf.Recipes.Single(r=>r.Id=="WaterRaw").Inputs.Single(i=>i.Resource=="Power").Points=1;
        var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Power",5),D("Water",5)],e);
        Assert.Equal(1010,q.Funds);Assert.Equal(2,q.Modules.Single(m=>m.Recipe.Id=="SmallPower").Count);Assert.DoesNotContain(q.Modules,m=>m.Recipe.Id=="LockedCheap" || m.Recipe.Id=="BigPower");
    }
    [Fact] public void ExactVeinsAndFiniteSixtyFourModuleBoundaryRemainBinding()
    {
        var (s,e,c)=Fixture(0);e.Wolf.Recipes=[Recipe("SubOne",1,0,"Substrate",1),Recipe("WaterOne",1,0,"Water",1)];
        var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",32),D("Water",32)],e);Assert.Equal(64,q.Modules.Sum(m=>m.Count));
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",33),D("Water",32)],e));
        e.Wolf.Sites.Single().Depot.Streams.Single(r=>r.Resource=="WaterVein").Outgoing=999;
        Assert.Contains("WaterVein",Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",1),D("Water",2)],e)).Message);
    }
    [Fact] public void ExplicitExistingVeinReserveIsPreservedWhileSelectingTheRawRecipe()
    {
        var (s,e,c)=Fixture();e.Wolf.Sites.Single().Depot.Streams.Single(r=>r.Resource=="WaterVein").Incoming=10;
        var cheap=Recipe("CheapHungry",1,0,"Water");cheap.Inputs.Single().Points=10;
        var efficient=Recipe("PreservesVein",2,0,"Water");e.Wolf.Recipes=[cheap,efficient];
        var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",5),D("WaterVein",5)],e);
        Assert.Equal(2,q.Funds);Assert.Equal("PreservesVein",Assert.Single(q.Modules).Recipe.Id);
        Assert.Equal(5,ColonyEngine.WolfAvailable(q.After,"WaterVein"));
    }
    [Fact] public void VectorAndRecipeOrderAreCanonicalButChangingAnyDemandChangesQuote()
    {
        var (s,e,c)=Fixture();var a=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        e.Wolf.Recipes.Reverse();var b=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",5),D("Substrate",5)],e);Assert.Equal(a.Id,b.Id);
        var changed=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",4),D("Substrate",5)],e);Assert.NotEqual(a.Id,changed.Id);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Water",5),D("Water",4)],e));
    }
    [Fact] public void OnePaymentOneManufactureOneWholeDepotAllocationAndReplayCreatesNoPhysicalStock()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);var command=Approve(s,e,q);
        var accepted=ColonyEngine.Execute(s,command,e);Assert.True(accepted.Outcome=="accepted",accepted.Reason);Assert.Single(accepted.State.WolfOrders);var payment=accepted.State.Effects.Single();
        Assert.Equal(-q.Funds,payment.FundsDelta);var applying=ColonyEngine.MarkEffectApplying(accepted.State,payment.Id,"fixture-funds","before");
        var paid=ColonyEngine.CompleteFundsEffect(applying,payment.Id,e.AvailableFunds,e.AvailableFunds-q.Funds,0,"after");
        e.Ut=q.LaborSeconds;var ready=ColonyEngine.Advance(paid,e);var order=ready.WolfOrders.Single();Assert.Equal("ready",order.State);
        var allocation=ColonyEngine.MarkWolfApplying(ready,order.Id,"fixture-WOLF",ColonyStateCodec.WolfDepotHash(q.Before));
        var complete=ColonyEngine.CompleteWolfAllocation(allocation,order.Id,ColonyStateCodec.WolfDepotHash(q.After),e.Ut);
        Assert.Single(complete.Effects.Where(x=>x.Kind=="wolfAllocation"));Assert.Empty(complete.Colonies.Single().Stock);Assert.Equal(q.Funds,complete.Colonies.Single().SpentFunds);
        var replay=ColonyEngine.Execute(complete,command,e);Assert.Equal("duplicate",replay.Outcome);Assert.Single(replay.State.WolfOrders);
        Assert.Equal(q.Id,ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(complete)).WolfOrders.Single().Quote.Id);
    }
    [Fact] public void AllSavedDemandsMustRemainSatisfiedBySamePaidModuleReplan()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        e.Wolf.Sites.Single().Depot.Streams.Add(new(){Resource="Water",Incoming=5});q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        Assert.DoesNotContain(q.Modules,m=>m.Recipe.Id=="WaterRaw");var accepted=ColonyEngine.Execute(s,Approve(s,e,q),e).State;var effect=accepted.Effects.Single();
        var paid=ColonyEngine.CompleteFundsEffect(ColonyEngine.MarkEffectApplying(accepted,effect.Id,"funds","before"),effect.Id,e.AvailableFunds,e.AvailableFunds-q.Funds,0,"after");
        e.Ut=q.LaborSeconds;var ready=ColonyEngine.Advance(paid,e);var held=ColonyEngine.HoldWolfAllocation(ready,ready.WolfOrders.Single().Id,"","Unattempted native preflight hold");
        e.Wolf.Sites.Single().ObservedUt=e.Ut;e.Wolf.Sites.Single().Depot.Streams.Single(r=>r.Resource=="Water").Outgoing=5;
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuotePaidWolfReplan(held,held.WolfOrders.Single().Id,e));
        Assert.True(held.WolfOrders.Single().FundsPaid);Assert.Equal(q.Funds,held.Colonies.Single().SpentFunds);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AttemptedPartialOrLostAcknowledgmentCannotReplanOrRepeatTheNativeAllocation(bool exactAfterButLostAck)
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        var ready=PayAndReady(s,e,q);var orderId=ready.WolfOrders.Single().Id;
        var applying=ColonyEngine.MarkWolfApplying(ready,orderId,"fixture WOLF",ColonyStateCodec.WolfDepotHash(q.Before));
        var actualAfter=exactAfterButLostAck?ColonyStateCodec.WolfDepotHash(q.After):new string('c',64);
        var held=ColonyEngine.HoldWolfAllocation(applying,orderId,actualAfter,"Native return lost or partial mutation; the adapter cannot prove ownership of the outcome.");
        var loaded=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(held));
        Assert.True(loaded.WolfOrders.Single().AllocationAttempted);Assert.False(ColonyEngine.CanReplanPaidWolfSupply(loaded,orderId));
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuotePaidWolfReplan(loaded,orderId,e));
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.MarkWolfApplying(loaded,orderId,"fixture WOLF",ColonyStateCodec.WolfDepotHash(q.Before)));
        var newRequest=Approve(loaded,e,q);var rejected=ColonyEngine.Execute(loaded,newRequest,e);Assert.Equal("rejected",rejected.Outcome);
        Assert.Same(loaded,rejected.State);Assert.Equal(q.Funds,loaded.Colonies.Single().SpentFunds);Assert.Empty(loaded.Colonies.Single().Stock);
        Assert.Single(loaded.Effects.Where(effect=>effect.Kind=="wolfAllocation"&&effect.State=="held"));
    }
    [Fact] public void CombinedPurchaseRespectsSharedCashAndSupplierOwnershipAcrossColonies()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        e.AvailableFunds=q.Funds+c.Charter.CashFloor-1;
        var shortCash=ColonyEngine.Execute(s,Approve(s,e,q),e);Assert.Equal("rejected",shortCash.Outcome);Assert.Same(s,shortCash.State);Assert.Empty(s.WolfOrders);
        e.AvailableFunds=1000000;var accepted=ColonyEngine.Execute(s,Approve(s,e,q),e);Assert.Equal("accepted",accepted.Outcome);
        var other=new ColonyRecord {Id=Id(),Name="Other actual colony",Site=new(){Body="Duna",Biome="Midlands"}};
        accepted.State.Colonies.Add(other);
        e.Wolf.Sites.Add(new(){ColonyId=other.Id,ContextKey=e.ContextKey,Depot=new(){Body="Duna",Biome="Midlands",Exists=true,Established=true,Surveyed=true}});
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.QuoteWolfSupplies(accepted.State,other.Id,[D("Substrate",5),D("Water",5)],e));
        Assert.Single(accepted.State.WolfOrders);Assert.Single(accepted.State.Effects.Where(effect=>effect.Kind=="wolfPurchase"));
    }
    [Fact] public void DepotChangeAfterReviewRejectsEvenAnUnchangedPrice()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);
        e.Wolf.Sites.Single().Depot.Streams.Single(row=>row.Resource=="Power").Incoming=6;
        var fresh=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5),D("Water",5)],e);Assert.Equal(q.Funds,fresh.Funds);Assert.NotEqual(q.Id,fresh.Id);
        var stale=ColonyEngine.Execute(s,Approve(s,e,q),e);Assert.Equal("rejected",stale.Outcome);Assert.Empty(s.WolfOrders);Assert.Empty(s.Effects);
    }
    [Fact] public void LegacyScalarQuoteBinaryHashIsUnchangedByEmptyAdditiveDemands()
    {
        var (s,e,c)=Fixture();var q=ColonyEngine.QuoteWolfSupply(s,c.Id,"Substrate",5,e);
        var wrapped=ColonyEngine.QuoteWolfSupplies(s,c.Id,[D("Substrate",5)],e);Assert.Empty(wrapped.Demands);Assert.Equal(q.Id,wrapped.Id);
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        void Ingredients(List<ColonyWolfIngredient> rows){writer.Write(rows.Count);foreach(var row in rows.OrderBy(r=>r.Resource,StringComparer.Ordinal)){writer.Write(row.Resource);writer.Write(row.Points);}}
        void RecipeBytes(ColonyWolfRecipe r){writer.Write(r.Id);writer.Write(r.PartName);writer.Write(r.ConfigurationHash);writer.Write(r.Tech);writer.Write(r.Funds);writer.Write(r.LaborSeconds);Ingredients(r.Inputs);Ingredients(r.Outputs);}
        void Depot(ColonyWolfDepot d){writer.Write(d.Body);writer.Write(d.Biome);writer.Write(d.Exists);writer.Write(d.Established);writer.Write(d.Surveyed);writer.Write(d.Streams.Count);foreach(var r in d.Streams.OrderBy(x=>x.Resource,StringComparer.Ordinal)){writer.Write(r.Resource);writer.Write(r.Incoming);writer.Write(r.Outgoing);}}
        writer.Write(q.ColonyId);writer.Write(q.ContextKey);writer.Write(q.Revision);writer.Write(q.Resource);writer.Write(q.DesiredAvailable);writer.Write(q.Funds);writer.Write(q.LaborSeconds);
        writer.Write(q.EstablishDepot);writer.Write(q.SurveyDepot);writer.Write(q.HomeGround);writer.Write(q.SurveyPartId);writer.Write(q.SurveyConfigurationHash);
        RecipeBytes(q.DepotConstruction);Depot(q.Before);Depot(q.After);Ingredients(q.SurveyVeins);writer.Write(q.Modules.Count);foreach(var m in q.Modules){writer.Write(m.Count);RecipeBytes(m.Recipe);}writer.Write(q.BalancePolicy);writer.Flush();
        Assert.Equal(q.Id,ColonyStateCodec.Hash(stream.ToArray()));
    }
}
