using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Pure domain fixtures exercise paid lineage and fail-closed transitions.
// These are not KSP placement, terrain or commissioning certificates.
public sealed partial class ColonyConstructionInvariantTests
{
    const string Context = "explicit-domain-test-context";
    static string Id() => Guid.NewGuid().ToString("D");
    static string Sha(char c) => new(c, 64);
    static string Hash(string value) => ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(value));

    static (ColonyState State, ColonyEnvironment Env, ColonyTemplate Template) Paid()
    {
        var template = new ColonyTemplate
        {
            Id = "test-home", Name = "Unit test home", Hash = Sha('a'), CraftSha256 = Sha('b'),
            RuntimeCertified = true, CertificationEvidence = "Explicit unit test provider; no native certificate",
            BuildFunds = 1000, LaborFunds = 200, LaborSeconds = 10, Homes = 2, ExpectedPartCount = 3,
            WidthMeters = 10, LengthMeters = 10, MinX = -4, MaxX = 4, MinZ = -4, MaxZ = 4, ClearanceMetres = 1,
            HomeCraftPartIds = [101, 103], Materials = [new() { Resource = "Metal", Amount = 2_000_000 }],
            EmbeddedContents = [new() { Resource = "ElectricCharge", Amount = 1_000_000 }]
        };
        var plot = new ColonyPlot
        {
            Id = Id(), WidthMeters = 10, LengthMeters = 10, SurveyHash = Sha('c'),
            TemplateId = template.Id, TemplateHash = template.Hash, EvidenceContext = Context,
            SurveyProvenance = "Explicit unit test survey provider"
        };
        var colony = new ColonyRecord
        {
            Id = Id(), Name = "Unit test colony", Site = new() { Body = "Minmus", RadiusMeters = 400 },
            Charter = new() { Purpose = "Unit test", CashFloor = 0, FoundingBudget = 100_000, SpendingLimit = 100_000, ReserveDays = 6, GrowthPolicy = "approval" },
            Plots = [plot], Stock = [new() { Resource = "Metal", Amount = 5_000_000, Capacity = 10_000_000 }, new() { Resource = "ElectricCharge", Amount = 4_000_000, Capacity = 10_000_000 }]
        };
        var state = new ColonyState { WorldId = Id(), Colonies = [colony] };
        var env = new ColonyEnvironment { WorldId = state.WorldId, ContextKey = Context, AvailableFunds = 20_000, Templates = [template] };
        var command = new ColonyCommand { OperationId = Id(), ColonyId = colony.Id, ContextKey = Context, Kind = "approveConstruction" };
        command.Fields.Add("TemplateId", template.Id); command.Fields.Add("TemplateHash", template.Hash); command.Fields.Add("PlotId", plot.Id);
        var accepted = ColonyEngine.Execute(state, command, env);
        Assert.True(accepted.Outcome == "accepted", accepted.Reason);
        var debit = accepted.State.Effects.Single();
        var applying = ColonyEngine.MarkEffectApplying(accepted.State, debit.Id, "Explicit test funding adapter", "funds=20000");
        state = ColonyEngine.CompleteFundsEffect(applying, debit.Id, 20_000, 19_000, 0, "funds=19000");
        return (state, env, template);
    }

    static ColonyConstructionLaborWitness Labor(ColonyEnvironment env, double from, double through, int workers = 1) => new()
    {
        ProviderId = "Explicit paid domain test provider", PoolId = "finite-domain-test-pool", ContextKey = env.ContextKey,
        EvidenceHash = Sha('d'), Workers = workers, PoolCapacity = workers,
        ValidFromUt = from, ValidThroughUt = through, CostIncludedInPaidEscrow = true
    };

    static ColonyState Finished(ColonyState state, ColonyEnvironment env)
    {
        env.Ut = 10; env.ConstructionLaborByOrder[state.Construction.Single().Id] = Labor(env, 0, 10);
        var done = ColonyEngine.Advance(state, env);
        Assert.Equal("awaitingPlacement", done.Construction.Single().State); return done;
    }

    static ColonyConstructionPlacementIntent Intent(ColonyState state, ColonyEnvironment env) => new()
    {
        OperationId = Id(), EffectId = Id(), Phase = "intent", ContextKey = env.ContextKey,
        RequestPayload = "Explicit domain test payload; no native craft assembly", PayloadHash = Hash("Explicit domain test payload; no native craft assembly"),
        RequestFingerprint = Sha('e'), EscrowWitness = ColonyEngine.ConstructionEscrowWitness(state, state.Construction.Single().Id),
        BeforeWitness = "Explicit domain test world witness"
    };

    static ColonyConstructionPlacementObservation Anchored(ColonyState state) => new()
    {
        WorldId = state.WorldId, OperationId = state.Construction.Single().Placement.OperationId,
        RequestFingerprint = state.Construction.Single().Placement.RequestFingerprint,
        Phase = "Anchored", AssemblyAttempted = true, ObservedUt = 10, AfterWitness = "Explicit domain test anchored readback",
        VesselId = Id(), FoundationId = Id(), Anchored = true,
        CraftToPersistentIds = new() { [100] = 500, [101] = 501, [103] = 503 }, QualifiedHomePartIds = [501, 503]
    };

    static ColonyManagementSnapshot ProjectedCapabilities(ColonyState state, ColonyCommand command)
    {
        var snapshot = new ColonyManagementSnapshot { State = state };
        snapshot.Capabilities.Add(new ColonyManagementCapability
        {
            Kind = "retryConstructionPlacement", ColonyId = command.ColonyId, TargetId = command.TargetId,
            Available = true, Reason = "Initial projected retry capability."
        });
        snapshot.Capabilities.Add(new ColonyManagementCapability
        {
            Kind = "approveTrade", ColonyId = command.ColonyId, Available = true,
            Reason = "Ordinary action must stay held."
        });
        snapshot.Capabilities.Add(new ColonyManagementCapability
        {
            Kind = "replanPaidWolfSupply", ColonyId = command.ColonyId, Available = true,
            Reason = "Existing unresolved-effect exception."
        });
        return snapshot;
    }

    [Fact]
    public void ManagementHoldProjectionPreservesOnlyCurrentMatchingHeldRetry()
    {
        var (state, env, _, command) = HeldRetry();
        var snapshot = ProjectedCapabilities(state, command);
        ColonyManagementCapabilityHolds.Apply(snapshot, env);

        var retry = Assert.Single(snapshot.Capabilities.Where(c => c.Kind == "retryConstructionPlacement"));
        Assert.True(retry.Available, retry.Reason);
        Assert.Equal(command.ColonyId, retry.ColonyId); Assert.Equal(command.TargetId, retry.TargetId);
        Assert.False(snapshot.Capabilities.Single(c => c.Kind == "approveTrade").Available);
        Assert.True(snapshot.Capabilities.Single(c => c.Kind == "replanPaidWolfSupply").Available);
    }

    [Fact]
    public void ManagementHoldProjectionRefusesUnavailableStaleAndMismatchedCapabilities()
    {
        var (staleState, staleEnv, _, staleCommand) = HeldRetry();
        staleEnv.ConstructionRecovery.Remove(staleCommand.TargetId);
        var unavailableSnapshot = ProjectedCapabilities(staleState, staleCommand);
        ColonyManagementCapabilityHolds.Apply(unavailableSnapshot, staleEnv);
        Assert.False(unavailableSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);

        var (staleState2, staleEnv2, _, staleCommand2) = HeldRetry();
        staleEnv2.ConstructionRecovery[staleCommand2.TargetId].ObservedUt = staleEnv2.Ut - 2;
        var staleSnapshot = ProjectedCapabilities(staleState2, staleCommand2);
        ColonyManagementCapabilityHolds.Apply(staleSnapshot, staleEnv2);
        Assert.False(staleSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);

        var (wrongTargetState, wrongTargetEnv, _, wrongTargetCommand) = HeldRetry();
        var wrongTargetSnapshot = ProjectedCapabilities(wrongTargetState, wrongTargetCommand);
        wrongTargetSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").TargetId = Id();
        ColonyManagementCapabilityHolds.Apply(wrongTargetSnapshot, wrongTargetEnv);
        Assert.False(wrongTargetSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);

        var (wrongColonyState, wrongColonyEnv, _, wrongColonyCommand) = HeldRetry();
        var wrongColonySnapshot = ProjectedCapabilities(wrongColonyState, wrongColonyCommand);
        wrongColonySnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").ColonyId = Id();
        ColonyManagementCapabilityHolds.Apply(wrongColonySnapshot, wrongColonyEnv);
        Assert.False(wrongColonySnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);
    }

    [Fact]
    public void ManagementHoldProjectionRefusesCompetingEffectAndAssemblyAttempt()
    {
        var (competingState, competingEnv, _, competingCommand) = HeldRetry();
        competingState.Effects.Add(new ColonyEffect
        {
            Id = Id(), OperationId = Id(), Kind = "otherExternalEffect", State = "applying",
            Reason = "Unrelated unresolved effect."
        });
        var competingSnapshot = ProjectedCapabilities(competingState, competingCommand);
        ColonyManagementCapabilityHolds.Apply(competingSnapshot, competingEnv);
        Assert.False(competingSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);

        var (attemptedState, attemptedEnv, _, attemptedCommand) = HeldRetry();
        attemptedState.Construction.Single().Placement.AssemblyAttempted = true;
        var attemptedSnapshot = ProjectedCapabilities(attemptedState, attemptedCommand);
        ColonyManagementCapabilityHolds.Apply(attemptedSnapshot, attemptedEnv);
        Assert.False(attemptedSnapshot.Capabilities.Single(c => c.Kind == "retryConstructionPlacement").Available);
    }

    [Fact]
    public void GenericBuilderCountNeverGrantsFreeWork()
    {
        var (state, env, _) = Paid(); env.Ut = 100; env.BuildersByColony[state.Colonies.Single().Id] = 512;
        var next = ColonyEngine.Advance(state, env); var order = next.Construction.Single();
        Assert.Equal(0, order.WorkCompleted); Assert.Equal(100, order.AccountedUt); Assert.False(order.MaterialsConsumed);
        Assert.Equal(0, order.FundsConsumed); Assert.All(next.Colonies.Single().Stock, s => Assert.True(s.Reserved > 0));
    }

    [Fact]
    public void LaborOnlyAccountsItsBoundedIntervalAndNeverBackfillsMissingTime()
    {
        var (state, env, _) = Paid(); env.Ut = 10; var id = state.Construction.Single().Id;
        env.ConstructionLaborByOrder[id] = Labor(env, 4, 6);
        var first = ColonyEngine.Advance(state, env);
        Assert.Equal(2, first.Construction.Single().WorkCompleted); Assert.Equal(10, first.Construction.Single().AccountedUt);
        Assert.Equal(200, first.Construction.Single().FundsConsumed); Assert.Equal(3_000_000, first.Colonies.Single().Stock.Single(s => s.Resource == "Metal").Amount);
        env.Ut = 11; env.ConstructionLaborByOrder[id] = Labor(env, 0, 11);
        var second = ColonyEngine.Advance(first, env);
        Assert.Equal(3, second.Construction.Single().WorkCompleted); Assert.Equal(300, second.Construction.Single().FundsConsumed);
        Assert.Equal(3_000_000, second.Colonies.Single().Stock.Single(s => s.Resource == "Metal").Amount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnpaidOrOtherContextLaborCannotConsumeStock(bool otherContext)
    {
        var (state, env, _) = Paid(); env.Ut = 10;
        var witness = Labor(env, 0, 10);
        if (otherContext) witness.ContextKey = "different-save-load"; else witness.CostIncludedInPaidEscrow = false;
        env.ConstructionLaborByOrder[state.Construction.Single().Id] = witness;
        var next = ColonyEngine.Advance(state, env);
        Assert.Equal(0, next.Construction.Single().WorkCompleted); Assert.False(next.Construction.Single().MaterialsConsumed);
    }

    [Fact]
    public void FiniteLaborPoolCannotBeCountedForMultipleOrders()
    {
        var (state, env, _) = Paid(); env.Ut = 10;
        env.ConstructionLaborByOrder[state.Construction.Single().Id] = Labor(env, 0, 10);
        env.ConstructionLaborByOrder[Id()] = Labor(env, 0, 10);
        Assert.Throws<InvalidDataException>(() => ColonyEngine.Advance(state, env));
        Assert.Equal(0, state.Construction.Single().WorkCompleted);
    }

    [Fact]
    public void PlacementPersistsOneImmutablePaidIntentAndRejectsChangedRetry()
    {
        var (state, env, _) = Paid(); var id = state.Construction.Single().Id;
        Assert.Throws<InvalidDataException>(() => ColonyEngine.PrepareConstructionPlacement(state, id, env, Intent(state, env)));
        state = Finished(state, env); var intent = Intent(state, env);
        var placing = ColonyEngine.PrepareConstructionPlacement(state, id, env, intent);
        Assert.Equal("placing", placing.Construction.Single().State);
        Assert.Equal("applying", placing.Effects.Single(e => e.Kind == "constructionPlacement").State);
        Assert.Same(placing, ColonyEngine.PrepareConstructionPlacement(placing, id, env, intent));
        intent.PayloadHash = Sha('f');
        Assert.Throws<InvalidDataException>(() => ColonyEngine.PrepareConstructionPlacement(placing, id, env, intent));
        Assert.Equal(1, placing.Effects.Count(e => e.Kind == "constructionPlacement"));
        Assert.Equal("awaitingPlacement", state.Construction.Single().State);
    }

    [Fact]
    public void ReloadSurveyRefreshPreservesPaidCoordinatesClaimsAndRequiresTrustedNewEvidence()
    {
        var (state, env, template) = Paid(); state = Finished(state, env); var order = state.Construction.Single();
        env.ContextKey = "explicit-reloaded-test-context";
        Assert.Throws<InvalidDataException>(() => ColonyEngine.PrepareConstructionPlacement(state, order.Id, env, Intent(state, env)));
        var current = state.Colonies.Single().Plots.Single();
        var fresh = new ColonyPlot
        {
            Id = current.Id, Latitude = current.Latitude, Longitude = current.Longitude, Heading = current.Heading,
            WidthMeters = 10, LengthMeters = 10, TemplateId = template.Id, TemplateHash = template.Hash,
            EvidenceContext = env.ContextKey, ObservedUt = env.Ut, SurveyHash = Sha('f'), SurveyProvenance = "Explicit refreshed test evidence"
        };
        fresh.Longitude = 1;
        Assert.Throws<InvalidDataException>(() => ColonyEngine.RefreshConstructionSurvey(state, order.Id, env, fresh));
        fresh.Longitude = current.Longitude;
        var refreshed = ColonyEngine.RefreshConstructionSurvey(state, order.Id, env, fresh);
        Assert.Equal(order.Id, refreshed.Colonies.Single().Plots.Single().ReservedBy);
        Assert.Equal(env.ContextKey, refreshed.Colonies.Single().Plots.Single().EvidenceContext);
        Assert.Equal(order.FundsConsumed, refreshed.Construction.Single().FundsConsumed);
        Assert.Equal("placing", ColonyEngine.PrepareConstructionPlacement(refreshed, order.Id, env, Intent(refreshed, env)).Construction.Single().State);
    }

    [Fact]
    public void MissingSurveyWindowStaysAwaitingAndRoundRobinVisitsOtherReadyWork()
    {
        var (state, env, _) = Paid(); state = Finished(state, env); var first = state.Construction.Single();
        var waiting = ColonyEngine.AwaitConstructionEvidence(state, first.Id, "Awaiting loaded terrain at 1x");
        Assert.Equal("awaitingPlacement", waiting.Construction.Single().State); Assert.DoesNotContain(waiting.Effects, e => e.State == "held");
        var other = new ConstructionOrder { Id = Id(), State = "commissioning", Placement = new() { OperationId = Id() } };
        // Scheduling-only graph; no economy/placement certificate is inferred.
        waiting.Construction.Add(other);
        Assert.Equal(other.Id, ColonyEngine.NextConstructionPumpOrderId(waiting, first.Id));
        Assert.Equal(first.Id, ColonyEngine.NextConstructionPumpOrderId(waiting, other.Id));
    }

    [Fact]
    public void AnchoredReadbackMapsOnlyExplicitHomesAndDoesNotGrantOperationalCapacity()
    {
        var (state, env, template) = Paid(); state = Finished(state, env); var id = state.Construction.Single().Id;
        state = ColonyEngine.PrepareConstructionPlacement(state, id, env, Intent(state, env));
        var observed = Anchored(state); observed.QualifiedHomePartIds.Add(500);
        Assert.Throws<InvalidDataException>(() => ColonyEngine.ObserveConstructionPlacement(state, id, template, observed));
        observed.QualifiedHomePartIds.Remove(500);
        var next = ColonyEngine.ObserveConstructionPlacement(state, id, template, observed);
        var facility = next.Colonies.Single().Facilities.Single();
        Assert.Equal(new uint[] { 501, 503 }, facility.HomePartPersistentIds); Assert.Equal(0, facility.CertifiedHomes);
        Assert.Equal(ColonyEngine.HomeMappingHash(facility), facility.HomePartCertificationHash);
        Assert.Equal("commissioning", next.Construction.Single().State); Assert.Equal("physical", facility.ProductionOwner);
        Assert.Equal(facility.Id, next.Colonies.Single().Plots.Single().OccupiedBy); Assert.Empty(next.Colonies.Single().Plots.Single().ReservedBy);
        observed.Phase = "Prepared";
        Assert.Throws<InvalidDataException>(() => ColonyEngine.ObserveConstructionPlacement(next, id, template, observed));
    }

    [Fact]
    public void CommissioningRequiresUtilitiesAndActualHomeEvidenceWithoutCertifyingTemplate()
    {
        var (state, env, template) = Paid(); state = Finished(state, env); var id = state.Construction.Single().Id;
        template.RuntimeCertified = false; state.Colonies.Single().Charter.Sandbox = true; env.DevelopmentMode = true;
        state = ColonyEngine.PrepareConstructionPlacement(state, id, env, Intent(state, env));
        state = ColonyEngine.ObserveConstructionPlacement(state, id, template, Anchored(state));
        var q = new ColonyQualification { Provider = "Explicit test qualifier", Context = "unit-test", EvidenceHash = Sha('a'), ObservedUt = 10, PlacementStable = true, HousingCertified = true };
        var pending = ColonyEngine.CommissionConstruction(state, id, template, q, 2);
        Assert.Equal(0, pending.Colonies.Single().Facilities.Single().CertifiedHomes); Assert.Equal("commissioning", pending.Construction.Single().State);
        q.PowerReliable = q.HeatSafe = q.InputsAccessible = q.StaffingQualified = q.BackgroundSupported = true;
        var done = ColonyEngine.CommissionConstruction(pending, id, template, q, 2);
        Assert.Equal(2, done.Colonies.Single().Facilities.Single().CertifiedHomes); Assert.Equal("operational", done.Construction.Single().State);
        Assert.False(template.RuntimeCertified); Assert.Equal(1000, done.Construction.Single().FundsConsumed);
        Assert.Single(done.Colonies.Single().Facilities); Assert.Single(done.Effects.Where(e => e.Kind == "constructionPlacement"));
    }

    [Fact]
    public void AmbiguousPhysicalHoldCannotRefundOrLoseItsSavedIntent()
    {
        var (state, env, template) = Paid(); state = Finished(state, env); var id = state.Construction.Single().Id;
        state = ColonyEngine.PrepareConstructionPlacement(state, id, env, Intent(state, env));
        var hold = Anchored(state); hold.Phase = "RecoveryHold"; hold.Anchored = false; hold.Reason = "Uncertain external assembly readback";
        state = ColonyEngine.ObserveConstructionPlacement(state, id, template, hold);
        var cancel = new ColonyCommand { OperationId = Id(), ContextKey = env.ContextKey, ExpectedRevision = state.Revision, ColonyId = state.Colonies.Single().Id, TargetId = id, Kind = "cancelConstruction" };
        var rejected = ColonyEngine.Execute(state, cancel, env);
        Assert.Equal("rejected", rejected.Outcome); Assert.Equal("held", state.Construction.Single().State);
        Assert.DoesNotContain(state.Effects, e => e.Kind == "constructionRefund");
        var tampered = ColonyStateCodec.Copy(state); tampered.Construction.Single().Placement.RequestPayload += "changed";
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.ValidateConstruction(tampered));
    }
}
