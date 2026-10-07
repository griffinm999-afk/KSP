using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyEconomyTests
{
    static (ColonyState state, ColonyEnvironment env, ColonyRecord colony) Fixture()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 100);
        var colony = new ColonyRecord { Id = Guid.NewGuid().ToString("D"), Name = "Greater Flats", Site = new() { Body = "Minmus" }, FoundedUt = 100, SupportAccountedUt = 100 };
        state.Colonies.Add(colony);
        var policy = new ColonyEconomyPolicy { Id = "supply", Body = "Minmus", Provider = "Paid model supplier", SetupFunds = 100, SetupTravelSeconds = 10, ContractorWorkers = 2,
            Stores = new() { new() { Resource = "Supplies", Capacity = 100_000_000, UnitMassMicroTonnes = 1000, UnitVolumeMilliLiters = 5000 }, new() { Resource = "MaterialKits", Capacity = 100_000_000, UnitMassMicroTonnes = 1000, UnitVolumeMilliLiters = 5000 } },
            Suppliers = new() { new() { Id = "food", Resource = "Supplies", FreightPoolId = "kerbin", DestinationBody = "Minmus", Available = 100_000_000, FundsPerUnit = 2, FreightFunds = 5, TravelSeconds = 20, ConcurrentCapacity = 1, MassCapacityMicroTonnes = 1_000_000, VolumeCapacityMilliLiters = 1_000_000 },
                new() { Id = "kits", Resource = "MaterialKits", FreightPoolId = "kerbin", DestinationBody = "Minmus", Available = 100_000_000, FundsPerUnit = 2, FreightFunds = 5, TravelSeconds = 20, ConcurrentCapacity = 1, MassCapacityMicroTonnes = 1_000_000, VolumeCapacityMilliLiters = 1_000_000 } } };
        policy.Hash = ColonyEngine.EconomyPolicyHash(policy);
        return (state, new ColonyEnvironment { WorldId = state.WorldId, ContextKey = "test", Ut = 100, AvailableFunds = 1_000_000, EconomyPolicies = new() { policy }, BodyRadiiMeters = new() { ["Minmus"] = 60000 } }, colony);
    }
    static ColonyCommand Command(ColonyState state, ColonyRecord colony, string kind, Dictionary<string,string> fields) => new() { Kind = kind, OperationId = Guid.NewGuid().ToString("D"), ColonyId = colony.Id, ContextKey = "test", ExpectedRevision = state.Revision, Fields = fields };
    static ColonyState Activate(ColonyState state, ColonyEnvironment env, ColonyRecord colony)
    {
        var policy = env.EconomyPolicies.Single();
        var result = ColonyEngine.Execute(state, Command(state, colony, "activateLogistics", new() { ["PolicyId"] = policy.Id, ["PolicyHash"] = policy.Hash, ["QuotedFunds"] = "100" }), env);
        Assert.Equal("accepted", result.Outcome); return result.State;
    }
    static ColonyState Pay(ColonyState state, ColonyEnvironment env)
    {
        var effect = state.Effects.Single(x => x.State == "prepared");
        state = ColonyEngine.MarkEffectApplying(state, effect.Id, "testFunding", "exact before");
        return ColonyEngine.CompleteFundsEffect(state, effect.Id, env.AvailableFunds, env.AvailableFunds + effect.FundsDelta, env.Ut, "exact after");
    }
    [Fact]
    public void StagingCapacityRequiresPaymentAndArrivalAndNeverCreatesStock()
    {
        var (state, env, colony) = Fixture(); state = Activate(state, env, colony);
        Assert.Empty(state.Colonies.Single().Stock); Assert.Equal(100, ColonyEngine.CommittedFunds(state, colony.Id));
        env.Ut = 200; state = ColonyEngine.Advance(state, env); Assert.Empty(state.Colonies.Single().Stock);
        state = Pay(state, env); env.Ut = 209; state = ColonyEngine.Advance(state, env); Assert.Empty(state.Colonies.Single().Stock);
        env.Ut = 210; state = ColonyEngine.Advance(state, env);
        Assert.Equal("operational", state.Colonies.Single().Logistics.State);
        Assert.All(state.Colonies.Single().Stock, row => Assert.Equal(0, row.Amount));
        var count = state.Journal.Count(x => x.Kind == "stagingArrival");
        env.Ut = 300; state = ColonyEngine.Advance(ColonyStateCodec.Copy(state), env);
        Assert.Equal(count, state.Journal.Count(x => x.Kind == "stagingArrival"));
    }
    [Fact]
    public void FreightPoolIsSharedAcrossResourcesAndFundsAndCargoAreConserved()
    {
        var (state, env, colony) = Fixture(); state = Pay(Activate(state, env, colony), env); env.Ut = 110; state = ColonyEngine.Advance(state, env);
        ColonyCommand Import(string supplier) => Command(state, colony, "approveTrade", new() { ["SupplierId"] = supplier, ["AmountMicroUnits"] = "1000000", ["QuotedFunds"] = "7" });
        var first = ColonyEngine.Execute(state, Import("food"), env); Assert.Equal("accepted", first.Outcome); state = first.State;
        var blocked = ColonyEngine.Execute(state, Import("kits"), env); Assert.Equal("rejected", blocked.Outcome); Assert.Contains("freight", blocked.Reason);
        state = Pay(state, env); Assert.Equal(99_000_000, state.Suppliers.Single(x => x.Id == "food").Available);
        Assert.Equal(0, state.Colonies.Single().Stock.Single(x => x.Resource == "Supplies").Amount);
        env.Ut = 130; state = ColonyEngine.Advance(state, env);
        Assert.Equal(1_000_000, state.Colonies.Single().Stock.Single(x => x.Resource == "Supplies").Amount);
        Assert.Equal(107, state.Colonies.Single().SpentFunds);
        Assert.Equal("accepted", ColonyEngine.Execute(state, Import("kits"), env).Outcome);
    }
    [Fact]
    public void SecondColonyCannotReplenishGlobalSupplierStock()
    {
        var (state, env, colony) = Fixture(); state = Pay(Activate(state, env, colony), env); env.Ut = 110; state = ColonyEngine.Advance(state, env);
        var first = ColonyEngine.Execute(state, Command(state, colony, "approveTrade", new() { ["SupplierId"] = "food", ["AmountMicroUnits"] = "1000000", ["QuotedFunds"] = "7" }), env);
        state = Pay(first.State, env);
        var second = new ColonyRecord { Id = Guid.NewGuid().ToString("D"), Name = "Second site", Site = new() { Body = "Minmus", Latitude = 20 }, FoundedUt = 110, SupportAccountedUt = 110 };
        state.Colonies.Add(second); state = Activate(state, env, second);
        Assert.Equal(2, state.Suppliers.Count); Assert.Equal(99_000_000, state.Suppliers.Single(x => x.Id == "food").Available);
    }
    [Fact]
    public void AnotherRouteCannotEnlargeTheSameFreightFleet()
    {
        var (state, env, colony) = Fixture(); state = Pay(Activate(state, env, colony), env);
        var second = new ColonyRecord { Id = Guid.NewGuid().ToString("D"), Name = "Duna site", Site = new() { Body = "Duna" }, FoundedUt = 100, SupportAccountedUt = 100 };
        state.Colonies.Add(second);
        var policy = env.EconomyPolicies.Single(); policy.Id = "duna-supply"; policy.Body = "Duna";
        foreach (var supplier in policy.Suppliers) { supplier.Id += "-duna"; supplier.DestinationBody = "Duna"; supplier.ConcurrentCapacity = 8; }
        policy.Hash = ColonyEngine.EconomyPolicyHash(policy);
        var rejected = ColonyEngine.Execute(state, Command(state, second, "activateLogistics", new() { ["PolicyId"] = policy.Id, ["PolicyHash"] = policy.Hash, ["QuotedFunds"] = "100" }), env);
        Assert.Equal("rejected", rejected.Outcome); Assert.Contains("freight pool", rejected.Reason);
        Assert.Equal(2, rejected.State.Suppliers.Count); Assert.Equal("none", rejected.State.Colonies.Last().Logistics.State);
        Assert.Single(rejected.State.Effects);
    }
    [Fact]
    public void ReloadRejectsInconsistentSavedFreightFleet()
    {
        var (state, env, colony) = Fixture(); state = Activate(state, env, colony);
        state.Suppliers[1].ConcurrentCapacity = 8;
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Serialize(state));
    }
    [Fact]
    public void ForgedSurveyHashAndAlteredPolicyCannotCommit()
    {
        var (state, env, colony) = Fixture();
        var request = Command(state, colony, "surveyPlot", new() { ["SurveyHash"] = "client-generated", ["TemplateId"] = "housing" }); request.TargetId = Guid.NewGuid().ToString("D");
        var rejected = ColonyEngine.Execute(state, request, env); Assert.Equal("rejected", rejected.Outcome); Assert.Empty(rejected.State.Colonies.Single().Plots);
        env.EconomyPolicies[0].SetupFunds = 1;
        var policy = env.EconomyPolicies[0];
        rejected = ColonyEngine.Execute(state, Command(state, colony, "activateLogistics", new() { ["PolicyId"] = policy.Id, ["PolicyHash"] = policy.Hash, ["QuotedFunds"] = "1" }), env);
        Assert.Equal("rejected", rejected.Outcome); Assert.Empty(rejected.State.Effects);
    }
}
