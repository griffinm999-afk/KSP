using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyWolfTests
{
    static string Id() => Guid.NewGuid().ToString("D");
    static ColonyWolfIngredient Ingredient(string resource, int points) => new() { Resource = resource, Points = points };
    static ColonyWolfRecipe Recipe(string id, long price, ColonyWolfIngredient[] inputs, params ColonyWolfIngredient[] outputs) => new()
    { Id = id, PartName = id, ConfigurationHash = new string('a', 64), Tech = "advScienceTech", Funds = price, Inputs = inputs.ToList(), Outputs = outputs.ToList() };
    static (ColonyState State, ColonyEnvironment Env, ColonyRecord Colony) Fixture(bool established = true, int power = 5, int allocatedPower = 5, int veins = 45)
    {
        var colony = new ColonyRecord { Id = Id(), Name = "Gypsum Settlement", Site = new ColonySite { Body = "Minmus", Biome = "Greater Flats" }, Charter = new ColonyCharter { FoundingBudget = 2_000_000, SpendingLimit = 2_000_000 } };
        var state = ColonyEngine.Create(Id(), 0); state.Colonies.Add(colony);
        var depot = new ColonyWolfDepot { Body = "Minmus", Biome = "Greater Flats", Exists = established, Established = established, Surveyed = established };
        if (established) { depot.Streams.Add(new() { Resource = "Power", Incoming = power, Outgoing = allocatedPower }); if (veins > 0) depot.Streams.Add(new() { Resource = "GypsumVein", Incoming = veins }); }
        var env = new ColonyEnvironment { WorldId = state.WorldId, ContextKey = "selected-a", AvailableFunds = 2_000_000, UnlockedTech = ["advScienceTech"], Wolf = new ColonyWolfEnvironment { Ready = true, Provider = "fixture-only" } };
        env.Wolf.DepotConstruction = Recipe("WOLF_Depot", 56410, []);
        env.Wolf.Recipes.Add(Recipe("WOLF_Harvester_375:Gypsum", 56410, [Ingredient("Power", 5), Ingredient("GypsumVein", 5)], Ingredient("Gypsum", 10)));
        env.Wolf.Recipes.Add(Recipe("WOLF_PowerModule375:low", 55620, [], Ingredient("Power", 5)));
        env.Wolf.Recipes.Add(Recipe("WOLF_PowerModule375:high", 55620, [Ingredient("EngineerCrewPoint", 1), Ingredient("Maintenance", 1)], Ingredient("Power", 50)));
        env.Wolf.Sites.Add(new() { ColonyId = colony.Id, ContextKey = env.ContextKey, Depot = depot,
            SurveyPartId = established ? 0u : 123u, SurveyConfigurationHash = new string('b', 64), SurveyVeins = established ? [] : [Ingredient("GypsumVein", veins)] });
        return (state, env, colony);
    }
    static ColonyCommand Approval(ColonyState state, ColonyEnvironment env, ColonyWolfQuote q) => new()
    { OperationId = Id(), Kind = "approveWolfSupply", ColonyId = q.ColonyId, ContextKey = env.ContextKey, ExpectedRevision = state.Revision, QuoteId = q.Id, Fields = new() { ["Resource"] = q.Resource, ["DesiredAvailable"] = q.DesiredAvailable.ToString() } };
    static ColonyState PaidReady(ColonyState state, ColonyEnvironment env, int desired = 20)
    {
        var q = ColonyEngine.QuoteWolfSupply(state, state.Colonies[0].Id, "Gypsum", desired, env);
        var accepted = ColonyEngine.Execute(state, Approval(state, env, q), env); Assert.Equal("accepted", accepted.Outcome);
        var purchase = accepted.State.Effects.Single(x => x.Kind == "wolfPurchase");
        var applying = ColonyEngine.MarkEffectApplying(accepted.State, purchase.Id, "KSP.Funding", "exact prior funds");
        var paid = ColonyEngine.CompleteFundsEffect(applying, purchase.Id, env.AvailableFunds, env.AvailableFunds + purchase.FundsDelta, 0, "exact verified funds");
        env.Ut = q.LaborSeconds; return ColonyEngine.Advance(paid, env);
    }

    [Fact] public void QuotePurchasesActualPowerDependenciesAndNeverInventsCrewPoints()
    {
        var (s, e, c) = Fixture(); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 20, e);
        Assert.Equal(224060, q.Funds); Assert.Equal(43200, q.LaborSeconds);
        Assert.Equal(2, q.Modules.Single(x => x.Recipe.Id.EndsWith(":low")).Count);
        Assert.DoesNotContain(q.Modules, x => x.Recipe.Id.EndsWith(":high"));
        Assert.Equal(20, ColonyEngine.WolfAvailable(q.After, "Gypsum"));
        Assert.Equal(0, ColonyEngine.WolfAvailable(q.After, "Power"));
        Assert.Equal(10, q.After.Streams.Single(x => x.Resource == "GypsumVein").Outgoing);
        Assert.Empty(c.Stock);
    }
    [Fact] public void MissingSurveyedVeinsCannotBeBoughtAsCapacity()
    {
        var (s, e, c) = Fixture(veins: 0);
        Assert.Contains("GypsumVein", Assert.Throws<InvalidDataException>(() => ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e)).Message);
        Assert.Empty(s.WolfOrders); Assert.Empty(c.Stock);
    }
    [Fact] public void NormalPowerSetupQuotesInstalledLowPowerWithoutCrewOrVeinFabrication()
    {
        var (s, e, c) = Fixture(); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Power", 15, e);
        Assert.Single(q.Modules); Assert.Equal(3, q.Modules.Single().Count); Assert.EndsWith(":low", q.Modules.Single().Recipe.Id);
        Assert.Equal(166860, q.Funds); Assert.Equal(32400, q.LaborSeconds); Assert.Equal(15, ColonyEngine.WolfAvailable(q.After, "Power"));
        Assert.Equal(45, q.After.Streams.Single(x => x.Resource == "GypsumVein").Incoming);
        Assert.DoesNotContain(q.After.Streams, x => x.Resource == "EngineerCrewPoint");
    }
    [Fact] public void NewDepotHasOnceOnlyInstalledStartingPowerAndRequiresPhysicalSurvey()
    {
        var (s, e, c) = Fixture(established: false); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e);
        Assert.True(q.EstablishDepot); Assert.True(q.SurveyDepot); Assert.Equal(112820, q.Funds); Assert.Equal(21600, q.LaborSeconds);
        Assert.Equal(5, q.After.Streams.Single(x => x.Resource == "Power").Incoming); Assert.Single(q.Modules);
        e.Wolf.Sites[0].SurveyPartId = 0;
        Assert.Throws<InvalidDataException>(() => ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e));
    }
    [Fact] public void ExistingDepotNeverReceivesAnotherFreeStartingPower()
    {
        var (s, e, c) = Fixture(power: 4, allocatedPower: 0); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e);
        Assert.False(q.EstablishDepot); Assert.False(q.SurveyDepot); Assert.Equal(112030, q.Funds);
        Assert.Equal(9, q.After.Streams.Single(x => x.Resource == "Power").Incoming);
    }
    [Fact] public void HomeGroundStartingStreamsStayVirtualAndAreVisibleInQuote()
    {
        var (s, e, c) = Fixture(established: false); e.Wolf.Sites[0].HomeGround = true;
        var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e);
        Assert.Equal(10, q.After.Streams.Single(x => x.Resource == "Power").Incoming);
        Assert.Equal(5, q.After.Streams.Single(x => x.Resource == "MaterialKits").Incoming); Assert.Empty(c.Stock);
    }
    [Fact] public void StaleSaveTechAndChangedQuoteAreHeldBeforePurchase()
    {
        var (s, e, c) = Fixture(); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e); var cmd = Approval(s, e, q);
        e.Wolf.Sites[0].Depot.Streams[0].Incoming++;
        Assert.Equal("rejected", ColonyEngine.Execute(s, cmd, e).Outcome);
        e.UnlockedTech.Clear(); Assert.Throws<InvalidDataException>(() => ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e));
        e.UnlockedTech.Add("advScienceTech"); e.Ut = 7; Assert.Throws<InvalidDataException>(() => ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 10, e));
    }
    [Fact] public void CashFloorAndSharedSupplierSlotRemainBinding()
    {
        var (s, e, c) = Fixture(); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 20, e);
        e.AvailableFunds = q.Funds + c.Charter.CashFloor - 1;
        Assert.Equal("rejected", ColonyEngine.Execute(s, Approval(s, e, q), e).Outcome);
        e.AvailableFunds = 2_000_000; var accepted = ColonyEngine.Execute(s, Approval(s, e, q), e);
        Assert.Equal(q.Funds, ColonyEngine.CommittedFunds(accepted.State, c.Id));
        Assert.Throws<InvalidDataException>(() => ColonyEngine.QuoteWolfSupply(accepted.State, c.Id, "Gypsum", 10, e));
    }
    [Fact] public void OutsourcedSequentialLeadTimeCannotBeMultipliedByLocalWorkers()
    {
        var (s, e, c) = Fixture(); var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 20, e);
        var accepted = ColonyEngine.Execute(s, Approval(s, e, q), e).State; var effect = accepted.Effects.Single();
        var applying = ColonyEngine.MarkEffectApplying(accepted, effect.Id, "funds", "before");
        var paid = ColonyEngine.CompleteFundsEffect(applying, effect.Id, e.AvailableFunds, e.AvailableFunds - q.Funds, 100, "after");
        e.BuildersByColony[c.Id] = 512; e.Ut = 1100;
        var advanced = ColonyEngine.Advance(paid, e); Assert.Equal(1000, advanced.WolfOrders.Single().WorkCompleted); Assert.Equal("building", advanced.WolfOrders.Single().State);
        e.Ut = 100 + q.LaborSeconds; advanced = ColonyEngine.Advance(advanced, e); Assert.Equal("ready", advanced.WolfOrders.Single().State);
        Assert.Equal(q.Funds, advanced.Colonies.Single().SpentFunds); Assert.Equal(0, ColonyEngine.CommittedFunds(advanced, c.Id));
    }
    [Fact] public void ExactFullDepotWitnessActivatesWithoutCreatingPhysicalStock()
    {
        var (s, e, c) = Fixture(); var ready = PaidReady(s, e); var order = ready.WolfOrders.Single();
        var applying = ColonyEngine.MarkWolfApplying(ready, order.Id, "installed WOLF", ColonyStateCodec.WolfDepotHash(order.Quote.Before));
        Assert.Throws<InvalidDataException>(() => ColonyEngine.CompleteWolfAllocation(applying, order.Id, new string('0', 64), e.Ut));
        var complete = ColonyEngine.CompleteWolfAllocation(applying, order.Id, ColonyStateCodec.WolfDepotHash(order.Quote.After), e.Ut);
        Assert.Equal("operational", complete.WolfOrders.Single().State); Assert.Empty(complete.Colonies.Single().Stock);
        var restored = ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(complete)); Assert.Equal(order.Quote.Id, restored.WolfOrders.Single().Quote.Id);
        Assert.Throws<InvalidDataException>(() => ColonyEngine.MarkWolfApplying(restored, order.Id, "installed WOLF", order.BeforeWitness));
    }
    [Fact] public void PartialOrLostOutcomePersistsHoldAndCannotReplay()
    {
        var (s, e, c) = Fixture(); var ready = PaidReady(s, e); var order = ready.WolfOrders.Single();
        var applying = ColonyEngine.MarkWolfApplying(ready, order.Id, "installed WOLF", ColonyStateCodec.WolfDepotHash(order.Quote.Before));
        var held = ColonyEngine.HoldWolfAllocation(applying, order.Id, new string('c', 64), "Partial provider throw; outcome unknown, replay disabled.");
        var restored = ColonyStateCodec.Copy(held); Assert.Equal("held", restored.WolfOrders.Single().State);
        Assert.Contains(restored.Effects, x => x.State == "held" && x.Kind == "wolfAllocation");
        Assert.Equal("rejected", ColonyEngine.Execute(restored, new ColonyCommand { OperationId = Id(), ContextKey = e.ContextKey, ExpectedRevision = restored.Revision, Kind = "approveWolfSupply", ColonyId = c.Id }, e).Outcome);
        Assert.Throws<InvalidDataException>(() => ColonyEngine.MarkWolfApplying(restored, order.Id, "installed WOLF", order.BeforeWitness));
    }
    [Fact] public void ExistingExternalCapacityIsAcknowledgedWithZeroNewModules()
    {
        var (s, e, c) = Fixture(); e.Wolf.Sites[0].Depot.Streams.Add(new() { Resource = "Gypsum", Incoming = 30, Outgoing = 5 });
        var q = ColonyEngine.QuoteWolfSupply(s, c.Id, "Gypsum", 20, e); Assert.Empty(q.Modules); Assert.Equal(0, q.Funds); Assert.Equal(0, q.LaborSeconds);
        var accepted = ColonyEngine.Execute(s, Approval(s, e, q), e); Assert.Equal("accepted", accepted.Outcome);
        Assert.Empty(accepted.State.Effects); Assert.Empty(accepted.State.Colonies.Single().Stock); Assert.Equal("ready", accepted.State.WolfOrders.Single().State);
    }
    [Fact] public void ForgedSavedRecipeAfterWitnessIsRejectedEvenIfQuoteHashRecomputed()
    {
        var (s, e, c) = Fixture(); var ready = PaidReady(s, e); var q = ready.WolfOrders.Single().Quote;
        q.After.Streams.Single(x => x.Resource == "Gypsum").Incoming++;
        q.Id = ColonyStateCodec.WolfQuoteHash(q);
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Serialize(ready));
    }
    [Fact] public void PaidUnattemptedPreflightCanReplanSameModulesAgainstFreshLedgerWithoutPurchase()
    {
        var (s, e, c) = Fixture(); var ready = PaidReady(s, e); var order = ready.WolfOrders.Single();
        var held = ColonyEngine.HoldWolfAllocation(ready, order.Id, ColonyStateCodec.WolfDepotHash(order.Quote.Before), "External hopper allocation changed before API attempt.");
        e.Wolf.Sites[0].ObservedUt = e.Ut; e.Wolf.Sites[0].Depot.Streams[0].Incoming += 5;
        var replan = ColonyEngine.QuotePaidWolfReplan(held, order.Id, e);
        var cmd = new ColonyCommand { OperationId = Id(), Kind = "replanPaidWolfSupply", ColonyId = c.Id, TargetId = order.Id, QuoteId = replan.Id, ContextKey = e.ContextKey, ExpectedRevision = held.Revision };
        var accepted = ColonyEngine.Execute(held, cmd, e); Assert.Equal("accepted", accepted.Outcome);
        var own = accepted.State.WolfOrders.Single(); Assert.Equal("ready", own.State); Assert.Equal(order.Quote.Id, own.Quote.Id); Assert.True(own.FundsPaid); Assert.False(own.AllocationAttempted);
        Assert.Equal(order.Quote.Funds, accepted.State.Colonies.Single().SpentFunds); Assert.Single(accepted.State.Effects.Where(x => x.Kind == "wolfPurchase"));
        Assert.Equal(ColonyStateCodec.WolfDepotHash(replan.Before), ColonyStateCodec.WolfDepotHash(ColonyEngine.WolfAllocationQuote(own).Before));
        var applying = ColonyEngine.MarkWolfApplying(accepted.State, own.Id, "installed WOLF", ColonyStateCodec.WolfDepotHash(replan.Before));
        var complete = ColonyEngine.CompleteWolfAllocation(applying, own.Id, ColonyStateCodec.WolfDepotHash(replan.After), e.Ut);
        Assert.Equal("operational", ColonyStateCodec.Copy(complete).WolfOrders.Single().State);
        Assert.Equal(20, ColonyEngine.WolfAvailable(replan.After, "Gypsum")); Assert.Empty(complete.Colonies.Single().Stock);
    }
    [Fact] public void AttemptedOrLostAckAllocationCannotUseReplanOrResendEvenAtOriginalAmounts()
    {
        var (s, e, c) = Fixture(); var ready = PaidReady(s, e); var order = ready.WolfOrders.Single();
        var applying = ColonyEngine.MarkWolfApplying(ready, order.Id, "installed WOLF", ColonyStateCodec.WolfDepotHash(order.Quote.Before));
        var held = ColonyEngine.HoldWolfAllocation(applying, order.Id, ColonyStateCodec.WolfDepotHash(order.Quote.Before), "API threw after unknown partial progress.");
        e.Wolf.Sites[0].ObservedUt = e.Ut;
        Assert.False(ColonyEngine.CanReplanPaidWolfSupply(held, order.Id));
        Assert.Throws<InvalidDataException>(() => ColonyEngine.QuotePaidWolfReplan(held, order.Id, e));
        var cmd = new ColonyCommand { OperationId = Id(), Kind = "replanPaidWolfSupply", ColonyId = c.Id, TargetId = order.Id, ContextKey = e.ContextKey, ExpectedRevision = held.Revision };
        Assert.Equal("rejected", ColonyEngine.Execute(held, cmd, e).Outcome);
    }
    [Fact] public void ReplanKeepsUnusedPurchasedDepotAndDoesNotGrantStartingPowerTwice()
    {
        var (s, e, c) = Fixture(established: false); var ready = PaidReady(s, e, 10); var order = ready.WolfOrders.Single();
        var held = ColonyEngine.HoldWolfAllocation(ready, order.Id, "", "Someone else established and surveyed the biome before API attempt.");
        var site = e.Wolf.Sites[0]; site.ObservedUt = e.Ut; site.Depot = new ColonyWolfDepot { Body = "Minmus", Biome = "Greater Flats", Exists = true, Established = true, Surveyed = true,
            Streams = [new() { Resource = "Power", Incoming = 5 }, new() { Resource = "GypsumVein", Incoming = 45 }] };
        var q = ColonyEngine.QuotePaidWolfReplan(held, order.Id, e);
        Assert.False(q.EstablishDepot); Assert.True(q.UnusedPurchasedDepot); Assert.Equal(5, q.After.Streams.Single(x => x.Resource == "Power").Incoming);
        Assert.Equal(112820, order.Quote.Funds); Assert.Equal(10, ColonyEngine.WolfAvailable(q.After, "Gypsum"));
    }
}
