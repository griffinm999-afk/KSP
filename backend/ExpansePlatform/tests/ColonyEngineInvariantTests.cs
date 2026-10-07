using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

/// <summary>
/// Domain-model invariants only. These fixtures are constructed save states, not KSP runtime evidence.
/// </summary>
public sealed class ColonyEngineInvariantTests
{
    const string World = "11f85341-03db-48f1-9e9b-c9e51e994150";
    const string Context = "save-branch-a";

    static string NewId() => Guid.NewGuid().ToString("D");

    static ColonyRecord NewColony(string? id = null, long cashFloor = 100_000, long spendingLimit = 1_000_000) => new()
    {
        Id = id ?? NewId(),
        Name = "Test Settlement",
        Site = new ColonySite { Body = "Minmus", Biome = "Greater Flats", Latitude = 0, Longitude = 0, RadiusMeters = 400 },
        Charter = new ColonyCharter
        {
            Purpose = "Settlement", PopulationTarget = 12, ResidentLimit = 24, VisitorLimit = 24,
            FoundingBudget = 2_000_000, CashFloor = cashFloor, SpendingLimit = spendingLimit,
            ReserveDays = 6, GrowthPolicy = "approval"
        }
    };

    static ColonyState NewState(params ColonyRecord[] colonies) => new()
    {
        WorldId = World,
        Colonies = colonies.ToList()
    };

    static ColonyEnvironment Env(ColonyState state, double ut = 0, long funds = 500_000) => new()
    {
        ContextKey = Context,
        WorldId = state.WorldId,
        Ut = ut,
        AvailableFunds = funds
    };

    static ColonyCommand Command(string kind, string colonyId, long revision, string? operationId = null,
        string? targetId = null, params (string Key, string Value)[] fields)
    {
        var command = new ColonyCommand
        {
            OperationId = operationId ?? NewId(), ContextKey = Context, ExpectedRevision = revision,
            Kind = kind, ColonyId = colonyId, TargetId = targetId ?? ""
        };
        foreach (var (key, value) in fields) command.Fields.Add(key, value);
        return command;
    }

    static ColonyPlot AddPlot(ColonyRecord colony)
    {
        var plot = new ColonyPlot
        {
            Id = NewId(), Latitude = 0, Longitude = 0, Heading = 0,
            WidthMeters = 20, LengthMeters = 20, SurveyHash = "survey-v1"
        };
        colony.Plots.Add(plot);
        return plot;
    }

    static ColonyTemplate Template(string plotId, long funds = 200_000) => new()
    {
        Id = "certified-habitat", Name = "Certified habitat", Version = 1, Hash = "sha256-template",
        CraftRelativePath = "Colony/CertifiedHabitat.craft", BuildFunds = funds, LaborFunds = 10_000, LaborSeconds = 10,
        WidthMeters = 10, LengthMeters = 10, Homes = 4, RuntimeCertified = true,
        CertificationEvidence = "fixture-only certification witness",
        Materials = [new MaterialRequirement { Resource = "Metal", Amount = 2_000_000 }],
        EmbeddedContents = [new MaterialRequirement { Resource = "Supplies", Amount = 1_000_000 }]
    };

    static (ColonyState State, ColonyRecord Colony, ColonyPlot Plot, ColonyEnvironment Environment, ColonyCommand Command) ConstructionPlan()
    {
        var colony = NewColony();
        var plot = AddPlot(colony);
        colony.Stock.Add(new ColonyStock { Resource = "Metal", Amount = 5_000_000, Capacity = 10_000_000, SupportFloor = 1_000_000 });
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 4_000_000, Capacity = 10_000_000, SupportFloor = 1_000_000 });
        var state = NewState(colony);
        var env = Env(state);
        env.Templates.Add(Template(plot.Id));
        var cmd = Command("approveConstruction", colony.Id, state.Revision, null, null,
            ("TemplateId", "certified-habitat"), ("TemplateHash", "sha256-template"), ("PlotId", plot.Id));
        return (state, colony, plot, env, cmd);
    }

    static (ColonyRecord Colony, ColonyState State, ColonyEnvironment Environment) ImportFixture(
        long cashFloor = 100_000, long stockAmount = 0, long stockCapacity = 20_000_000,
        long freightMassCapacity = 20_000_000, int concurrentCapacity = 4)
    {
        var colony = NewColony(cashFloor: cashFloor);
        colony.Stock.Add(new ColonyStock
        {
            Resource = "Ore", Amount = stockAmount, Capacity = stockCapacity,
            UnitMassMicroTonnes = 1_000_000, UnitVolumeMilliLiters = 1_000_000
        });
        var state = NewState(colony);
        state.Suppliers.Add(new ColonySupplier
        {
            Id = "ore-supplier", Resource = "Ore", Available = 20_000_000,
            FundsPerUnit = 10_000, FreightFunds = 5_000,
            MassCapacityMicroTonnes = freightMassCapacity, VolumeCapacityMilliLiters = 20_000_000,
            ConcurrentCapacity = concurrentCapacity, TravelSeconds = 10
        });
        return (colony, state, Env(state, funds: 500_000));
    }

    static ColonyCommand ImportCommand(ColonyRecord colony, long revision, string? op = null,
        long amount = 5_000_000, long quote = 55_000) => Command("approveTrade", colony.Id, revision, op,
            null, ("SupplierId", "ore-supplier"), ("AmountMicroUnits", amount.ToString()), ("QuotedFunds", quote.ToString()));

    static ColonyShipment Shipment(ColonyRecord colony, string supplierId, string resource, long amount, double arrival) => new()
    {
        Id = NewId(), ColonyId = colony.Id, SupplierId = supplierId, Kind = "import", State = "inTransit",
        Resource = resource, Amount = amount, Funds = 1, DepartUt = 0, ArrivalUt = arrival,
        TravelSeconds = arrival, MassMicroTonnes = amount, VolumeMilliLiters = amount
    };

    [Fact]
    public void ConstructionApprovalReservesMaterialsAndQueuesOneEscrowDebit()
    {
        var (prior, colony, plot, env, command) = ConstructionPlan();

        var accepted = ColonyEngine.Execute(prior, command, env);

        Assert.Equal("accepted", accepted.Outcome);
        Assert.Empty(prior.Construction);
        Assert.Empty(prior.Effects);
        Assert.Equal(2_000_000, accepted.State.Colonies[0].Stock.Single(x => x.Resource == "Metal").Reserved);
        Assert.Equal(1_000_000, accepted.State.Colonies[0].Stock.Single(x => x.Resource == "Supplies").Reserved);
        Assert.Equal(accepted.ResultId, accepted.State.Construction.Single().Id);
        Assert.Equal(accepted.ResultId, accepted.State.Colonies[0].Plots.Single().ReservedBy);
        var debit = Assert.Single(accepted.State.Effects);
        Assert.Equal("constructionEscrow", debit.Kind);
        Assert.Equal(-200_000, debit.FundsDelta);
        Assert.Equal("prepared", debit.State);

        var applying = ColonyEngine.MarkEffectApplying(accepted.State, debit.Id, "funds-adapter", "funds-before:500000");
        var paid = ColonyEngine.CompleteFundsEffect(applying, debit.Id, 500_000, 300_000, 1, "funds-after:300000");
        Assert.True(paid.Construction.Single().FundsPaid);
        Assert.Equal("building", paid.Construction.Single().State);
        Assert.Equal("Metal", paid.Construction.Single().Materials[0].Resource);
        Assert.Equal(plot.Id, paid.Construction.Single().PlotId);
    }

    [Fact]
    public void SameOperationIdDedupesButSamePayloadWithNewIdIsAnIndependentIntent()
    {
        var (colony, prior, env) = ImportFixture(concurrentCapacity: 4);
        var firstCommand = ImportCommand(colony, prior.Revision);

        var first = ColonyEngine.Execute(prior, firstCommand, env);
        Assert.Equal("accepted", first.Outcome);
        Assert.Equal("duplicate", ColonyEngine.Execute(first.State, firstCommand, env).Outcome);

        var changedPayload = ImportCommand(colony, first.State.Revision, firstCommand.OperationId,
            amount: 4_000_000, quote: 45_000);
        Assert.Equal("rejected", ColonyEngine.Execute(first.State, changedPayload, env).Outcome);

        var secondCommand = ImportCommand(colony, first.State.Revision);
        var second = ColonyEngine.Execute(first.State, secondCommand, env);
        Assert.Equal("accepted", second.Outcome);
        Assert.Equal(2, second.State.Shipments.Count);
        Assert.Equal(10_000_000, second.State.Colonies.Single().Stock.Single().IncomingReserved);
        Assert.Equal(10_000_000, second.State.Suppliers.Single().Reserved);
        Assert.Equal(2, second.State.Effects.Count(x => x.Kind == "importPurchase"));
    }

    [Fact]
    public void TwoColonyPoliciesPreserveTheHighestSharedCashFloor()
    {
        var firstColony = NewColony(cashFloor: 2_000);
        var secondColony = NewColony(cashFloor: 4_000);
        foreach (var colony in new[] { firstColony, secondColony })
            colony.Stock.Add(new ColonyStock { Resource = "Ore", Capacity = 20_000_000, UnitMassMicroTonnes = 1_000_000, UnitVolumeMilliLiters = 1_000_000 });
        var prior = NewState(firstColony, secondColony);
        prior.Suppliers.Add(new ColonySupplier
        {
            Id = "ore-supplier", Resource = "Ore", Available = 20_000_000, FundsPerUnit = 1_000,
            FreightFunds = 1_000, MassCapacityMicroTonnes = 20_000_000, VolumeCapacityMilliLiters = 20_000_000,
            ConcurrentCapacity = 4, TravelSeconds = 10
        });
        var env = Env(prior, funds: 10_000);
        var command = ImportCommand(firstColony, prior.Revision, amount: 5_000_000, quote: 6_000);

        var accepted = ColonyEngine.Execute(prior, command, env);

        Assert.Equal("accepted", accepted.Outcome);
        var effect = Assert.Single(accepted.State.Effects);
        var applying = ColonyEngine.MarkEffectApplying(accepted.State, effect.Id, "funds-adapter", "10000");
        var settled = ColonyEngine.CompleteFundsEffect(applying, effect.Id, 10_000, 4_000, 1, "4000");
        Assert.Equal("inTransit", settled.Shipments.Single().State);
        Assert.Equal(6_000, settled.Colonies.Single(x => x.Id == firstColony.Id).SpentFunds);
    }

    [Fact]
    public void CancellingConstructionBeforePaymentReleasesReservationsAndCancelsEscrow()
    {
        var (prior, _, _, env, approve) = ConstructionPlan();
        var accepted = ColonyEngine.Execute(prior, approve, env);
        var orderId = accepted.ResultId;
        var cancel = Command("cancelConstruction", accepted.State.Colonies.Single().Id, accepted.State.Revision,
            targetId: orderId);

        var result = ColonyEngine.Execute(accepted.State, cancel, env);

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal("cancelled", result.State.Construction.Single().State);
        Assert.All(result.State.Colonies.Single().Stock, x => Assert.Equal(0, x.Reserved));
        Assert.Equal("cancelled", Assert.Single(result.State.Effects).State);
        Assert.Equal("", result.State.Colonies.Single().Plots.Single().ReservedBy);
        Assert.Empty(result.State.Effects.Where(x => x.Kind == "constructionRefund"));
    }

    [Fact]
    public void CancellingFundedConstructionRefundsOnlyUnconsumedEscrow()
    {
        var (prior, _, _, env, approve) = ConstructionPlan();
        var accepted = ColonyEngine.Execute(prior, approve, env);
        var debit = Assert.Single(accepted.State.Effects);
        var applying = ColonyEngine.MarkEffectApplying(accepted.State, debit.Id, "funds-adapter", "500000");
        var paid = ColonyEngine.CompleteFundsEffect(applying, debit.Id, 500_000, 300_000, 0, "300000");
        env.Ut = 2;
        env.ConstructionLaborByOrder[paid.Construction.Single().Id] = new ColonyConstructionLaborWitness
        {
            ProviderId = "Explicit test construction provider", PoolId = "test-construction-contract", ContextKey = env.ContextKey,
            EvidenceHash = new string('a', 64), Workers = 1, PoolCapacity = 1,
            ValidFromUt = 0, ValidThroughUt = 2, CostIncludedInPaidEscrow = true
        };
        var worked = ColonyEngine.Advance(paid, env);
        var consumed = worked.Construction.Single().FundsConsumed;
        Assert.True(worked.Construction.Single().MaterialsConsumed);
        Assert.Equal(40_000, consumed);

        var cancel = Command("cancelConstruction", worked.Construction.Single().ColonyId,
            worked.Revision, targetId: worked.Construction.Single().Id);
        var result = ColonyEngine.Execute(worked, cancel, env);

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal("cancelled", result.State.Construction.Single().State);
        var refund = Assert.Single(result.State.Effects.Where(x => x.Kind == "constructionRefund"));
        Assert.Equal(200_000 - consumed, refund.FundsDelta);
        Assert.True(refund.FundsDelta > 0);
        Assert.Equal(0, result.State.Colonies.Single().Stock.Single(x => x.Resource == "Metal").Reserved);
    }

    [Fact]
    public void ImportReservesSupplierInventoryReceivingSpaceAndFreightCapacity()
    {
        var (colony, prior, env) = ImportFixture(concurrentCapacity: 1);
        var first = ColonyEngine.Execute(prior, ImportCommand(colony, prior.Revision), env);

        Assert.Equal("accepted", first.Outcome);
        Assert.Equal(5_000_000, first.State.Suppliers.Single().Reserved);
        Assert.Equal(5_000_000, first.State.Colonies.Single().Stock.Single().IncomingReserved);
        Assert.Equal("reserved", first.State.Shipments.Single().State);
        var second = ColonyEngine.Execute(first.State,
            ImportCommand(colony, first.State.Revision, amount: 1_000_000, quote: 15_000), env);
        Assert.Equal("rejected", second.Outcome);
        Assert.Equal(first.State, second.State);
        Assert.Equal(5_000_000, second.State.Suppliers.Single().Reserved);
    }

    [Fact]
    public void ImportRejectsInsufficientDestinationSpaceOrFreightMass()
    {
        var (fullColony, fullState, fullEnv) = ImportFixture(stockAmount: 2_000_000, stockCapacity: 6_000_000);
        Assert.Equal("rejected", ColonyEngine.Execute(fullState, ImportCommand(fullColony, 0), fullEnv).Outcome);

        var (colony, state, env) = ImportFixture(freightMassCapacity: 4_000_000);
        Assert.Equal("rejected", ColonyEngine.Execute(state, ImportCommand(colony, 0), env).Outcome);
    }

    [Fact]
    public void LargeCatchUpMatchesChronologicalSmallStepsAcrossSupportAndArrival()
    {
        static ColonyState WithEvents()
        {
            var colony = NewColony();
            colony.Residents.Add(new ColonyResident { Id = NewId(), RosterId = "resident-1", Name = "Rin", Status = "resident" });
            colony.SupportCommissionedUt = 0;
            colony.SupportAccountedUt = 0;
            colony.SupportMicroUnitsPerPersonDay = 1_000_000;
            colony.Stock.Add(new ColonyStock
            {
                Resource = "Supplies", Amount = 3_000_000, ImportedAmount = 3_000_000,
                IncomingReserved = 4_000_000, Capacity = 20_000_000
            });
            var state = NewState(colony);
            state.Suppliers.Add(new ColonySupplier { Id = "supplier", Resource = "Supplies", TravelSeconds = 10_000 });
            state.Shipments.Add(Shipment(colony, "supplier", "Supplies", 4_000_000, 10_000));
            return state;
        }

        var onePass = WithEvents();
        var longResult = ColonyEngine.Advance(onePass, Env(onePass, 43_200));

        var stepped = WithEvents();
        foreach (var ut in new[] { 5_000d, 10_000d, 16_000d, 30_000d, 43_200d })
            stepped = ColonyEngine.Advance(stepped, Env(stepped, ut));

        Assert.Equal(43_200, longResult.SimulatedUt);
        Assert.Equal(longResult.SimulatedUt, stepped.SimulatedUt);
        Assert.Equal(5_000_000, longResult.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(longResult.Colonies.Single().Stock.Single().Amount, stepped.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(longResult.Colonies.Single().Stock.Single().ImportedAmount, stepped.Colonies.Single().Stock.Single().ImportedAmount);
        Assert.Equal("arrived", longResult.Shipments.Single().State);
        Assert.Equal("arrived", stepped.Shipments.Single().State);
        Assert.Equal(longResult.Colonies.Single().SupportStatus, stepped.Colonies.Single().SupportStatus);
        Assert.Equal(longResult.Colonies.Single().SupportRemainder, stepped.Colonies.Single().SupportRemainder);
    }

    [Fact]
    public void ExhaustedSupportNeverMakesStockNegativeOrChargesShortageAsDebt()
    {
        var colony = NewColony();
        colony.Residents.Add(new ColonyResident { Id = NewId(), RosterId = "resident-1", Name = "Rin", Status = "resident" });
        colony.SupportCommissionedUt = 0;
        colony.SupportAccountedUt = 0;
        colony.SupportMicroUnitsPerPersonDay = 1_000_000;
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 1_000_000, ImportedAmount = 1_000_000, Capacity = 10_000_000 });
        var state = NewState(colony);

        var exhausted = ColonyEngine.Advance(state, Env(state, 43_200));

        Assert.Equal(0, exhausted.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(0, exhausted.Colonies.Single().Stock.Single().ImportedAmount);
        Assert.Equal(0, exhausted.Colonies.Single().SupportRemainder);
        Assert.Contains("Shortage", exhausted.Colonies.Single().SupportStatus);
        Assert.DoesNotContain(exhausted.Journal, x => x.ResourceDelta > 0);
    }

    [Fact]
    public void SupportCommissioningDoesNotChargeEarlierUncommissionedTime()
    {
        var colony = NewColony();
        colony.Residents.Add(new ColonyResident { Id = NewId(), RosterId = "resident-1", Name = "Rin", Status = "resident" });
        colony.SupportCommissionedUt = 15_000;
        colony.SupportAccountedUt = 10_000;
        colony.SupportMicroUnitsPerPersonDay = 1_000_000;
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 2_000_000, ImportedAmount = 2_000_000, Capacity = 10_000_000 });
        var state = NewState(colony);
        state.SimulatedUt = 10_000;
        state.TargetUt = 10_000;

        var advanced = ColonyEngine.Advance(state, Env(state, 25_000));

        Assert.Equal(1_537_038, advanced.Colonies.Single().Stock.Single().Amount);
        Assert.Equal(1_537_038, advanced.Colonies.Single().Stock.Single().ImportedAmount);
        Assert.Equal(25_000, advanced.Colonies.Single().SupportAccountedUt);
    }

    [Fact]
    public void SupportExhaustionBelowDoublePrecisionStepStillAdvancesTime()
    {
        var colony = NewColony();
        colony.Residents.Add(new ColonyResident { Id = NewId(), RosterId = "resident-1", Name = "Rin", Status = "resident" });
        colony.SupportCommissionedUt = 999_999_999_999.9;
        colony.SupportAccountedUt = 999_999_999_999.9;
        colony.SupportMicroUnitsPerPersonDay = 1_000_000_000_000;
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 1, ImportedAmount = 1, Capacity = 10 });
        var state = NewState(colony);
        state.SimulatedUt = 999_999_999_999.9;
        state.TargetUt = state.SimulatedUt;
        const double target = 1_000_000_000_000;

        var advanced = ColonyEngine.Advance(state, Env(state, target));

        Assert.Equal(target, advanced.SimulatedUt);
        Assert.Equal(0, advanced.Colonies.Single().Stock.Single().Amount);
        Assert.Contains("Shortage", advanced.Colonies.Single().SupportStatus);
    }

    [Fact]
    public void FrameworkCodecRoundTripsStateAndRejectsPayloadBeyondLimit()
    {
        var state = ColonyEngine.Create(World, 12_345);
        var bytes = ColonyStateCodec.Serialize(state);
        var restored = ColonyStateCodec.Deserialize(bytes);
        Assert.Equal(bytes, ColonyStateCodec.Serialize(restored));
        Assert.Equal(World, restored.WorldId);

        var tooLarge = new byte[ColonyLimits.MaxBytes + 1];
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Deserialize(tooLarge));
    }

    [Fact]
    public void SerializerRefusesValidBoundedRecordsWhosePayloadExceedsFourMiB()
    {
        var colony = NewColony();
        var state = NewState(colony);
        var witness = new string('w', 4_096);
        for (var i = 0; i < ColonyLimits.Effects; i++)
            state.Effects.Add(new ColonyEffect
            {
                Id = NewId(), OperationId = NewId(), ColonyId = colony.Id, TargetId = NewId(),
                Kind = "bounded", State = "prepared", BeforeWitness = witness, AfterWitness = witness
            });

        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Serialize(state));
    }

    [Fact]
    public void CodecRejectsConstructionFundsConsumedOutsideEscrowAndWorkWithoutProvenance()
    {
        foreach (var invalidConsumed in new[] { -1L, 101L })
        {
            var colony = NewColony();
            var state = NewState(colony);
            state.Construction.Add(new ConstructionOrder
            {
                Id = NewId(), ColonyId = colony.Id, PlotId = NewId(), TemplateId = "template", TemplateHash = "hash",
                State = "building", Funds = 100, FundsConsumed = invalidConsumed, FundsPaid = true,
                MaterialsConsumed = true, WorkRequired = 10, WorkCompleted = 1
            });
            Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Validate(state));
        }

        var unpaidColony = NewColony();
        var unpaidState = NewState(unpaidColony);
        unpaidState.Construction.Add(new ConstructionOrder
        {
            Id = NewId(), ColonyId = unpaidColony.Id, PlotId = NewId(), TemplateId = "template", TemplateHash = "hash",
            State = "building", Funds = 100, FundsPaid = false, MaterialsConsumed = false,
            WorkRequired = 10, WorkCompleted = 1
        });
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Validate(unpaidState));
    }

    [Fact]
    public void CodecRejectsCyclicConstructionDependencies()
    {
        var colony = NewColony();
        var firstId = NewId();
        var secondId = NewId();
        var state = NewState(colony);
        state.Construction.Add(new ConstructionOrder
        {
            Id = firstId, ColonyId = colony.Id, PlotId = NewId(), TemplateId = "a", TemplateHash = "a",
            State = "building", FundsPaid = true, MaterialsConsumed = true, Funds = 1,
            FundsConsumed = 1, WorkRequired = 10, WorkCompleted = 1, Dependencies = [secondId]
        });
        state.Construction.Add(new ConstructionOrder
        {
            Id = secondId, ColonyId = colony.Id, PlotId = NewId(), TemplateId = "b", TemplateHash = "b",
            State = "building", FundsPaid = true, MaterialsConsumed = true, Funds = 1,
            FundsConsumed = 1, WorkRequired = 10, WorkCompleted = 1, Dependencies = [firstId]
        });

        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Validate(state));
    }

    [Fact]
    public void BlockedConstructionCannotConsumeEverySchedulerEventBeforeShipmentArrival()
    {
        var colony = NewColony();
        var blockedPlot = AddPlot(colony);
        var dependencyPlot = AddPlot(colony);
        colony.Stock.Add(new ColonyStock { Resource = "Ore", Capacity = 2_000_000, IncomingReserved = 1_000_000 });
        var state = NewState(colony);
        state.Suppliers.Add(new ColonySupplier { Id = "supplier", Resource = "Ore", TravelSeconds = 1 });
        var blockedId = NewId();
        state.Construction.Add(new ConstructionOrder
        {
            Id = blockedId, ColonyId = colony.Id, PlotId = blockedPlot.Id, TemplateId = "blocked", TemplateHash = "blocked",
            State = "building", Funds = 0, FundsPaid = true, MaterialsConsumed = true,
            WorkRequired = 0.0001, WorkCompleted = 0, AccountedUt = 0, Dependencies = [NewId()]
        });
        // Supply the pending dependency as a valid, non-operational order.
        var dependencyId = state.Construction.Single().Dependencies.Single();
        state.Construction.Add(new ConstructionOrder
        {
            Id = dependencyId, ColonyId = colony.Id, PlotId = dependencyPlot.Id, TemplateId = "waiting", TemplateHash = "waiting",
            State = "reserved", Funds = 0, FundsPaid = false, MaterialsConsumed = false,
            WorkRequired = 1, WorkCompleted = 0, AccountedUt = 0
        });
        state.Shipments.Add(Shipment(colony, "supplier", "Ore", 1_000_000, 1));

        var advanced = ColonyEngine.Advance(state, Env(state, 1));

        Assert.Equal(1, advanced.SimulatedUt);
        Assert.Equal("arrived", advanced.Shipments.Single().State);
        Assert.Equal(1_000_000, advanced.Colonies.Single().Stock.Single().Amount);
    }

    [Fact]
    public void FoundingRejectsAdoptionOutsideFreshSiteSurveyRadius()
    {
        var state = ColonyEngine.Create(World, 0);
        var env = Env(state);
        env.BodyRadiiMeters.Add("Minmus", 60_000);
        var facility = new ColonyFacility
        {
            Id = NewId(), VesselId = NewId(), Name = "Distant industry", State = "operational",
            ProductionOwner = "physical", Qualification = new ColonyQualification()
        };
        env.AdoptableFacilities.Add(facility);
        env.FacilitySites.Add(facility.Id, new ColonySite
        {
            Body = "Minmus", Biome = "Greater Flats", Latitude = 25, Longitude = 0, RadiusMeters = 400
        });
        var selectedSite = new ColonySite { Body = "Minmus", Biome = "Greater Flats", Latitude = 0, Longitude = 0, RadiusMeters = 400 };
        var command = Command("foundColony", "", state.Revision, fields:
            [("Name", "New Settlement"), ("Body", selectedSite.Body), ("Biome", selectedSite.Biome),
             ("Latitude", "0"), ("Longitude", "0"), ("RadiusMeters", "400"),
             ("AdoptFacilityIds", facility.Id), ("SurveyId", ColonyEngine.SiteKey(selectedSite))]);

        var result = ColonyEngine.Execute(state, command, env);

        Assert.Equal("rejected", result.Outcome);
        Assert.Empty(result.State.Colonies);
    }
}
