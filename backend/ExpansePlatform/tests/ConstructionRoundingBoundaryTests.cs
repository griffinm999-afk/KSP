using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Paid synthetic domain fixtures use the exact archived native failure numbers.
// These prove IEEE scheduling and bounded accounting, not native labor eligibility.
public sealed class ConstructionRoundingBoundaryTests
{
    const double NativeFrom = 2104322.1539013674, NativeTarget = 2104381.52414556;
    static string Id() => Guid.NewGuid().ToString("D");
    static string Sha(char c) => new(c, 64);
    static double Next(double x) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) + 1);
    static (ColonyState State, ColonyEnvironment Env) Fixture(double from, double required, double completed, int workers, double through)
    {
        var orderId = Id(); var plotId = Id(); var colonyId = Id();
        var state = new ColonyState { WorldId = Id(), SimulatedUt = from, TargetUt = from };
        state.Colonies.Add(new ColonyRecord {
            Id=colonyId, Name="Paid rounding fixture", Site=new() { Body="Minmus", RadiusMeters=400 },
            Charter=new() { Purpose="Pure bounded accounting", FoundingBudget=100_000, SpendingLimit=100_000, ReserveDays=6, GrowthPolicy="approval" },
            SpentFunds=1000, Plots=[new() { Id=plotId, SurveyHash=Sha('c'), WidthMeters=10, LengthMeters=10, ReservedBy=orderId }],
            Stock=[new() { Resource="Metal", Amount=3_000_000, Capacity=10_000_000 }, new() { Resource="ElectricCharge", Amount=3_000_000, Capacity=10_000_000 }]
        });
        state.Construction.Add(new ConstructionOrder { Id=orderId, ColonyId=colonyId, PlotId=plotId, TemplateId="paid-fixture", TemplateHash=Sha('a'),
            State="building", Funds=1000, LaborFunds=200, FundsPaid=true, FundsConsumed=1000, MaterialsConsumed=true,
            WorkRequired=required, WorkCompleted=completed, AccountedUt=from,
            Materials=[new() { Resource="Metal", Amount=2_000_000 },new() { Resource="ElectricCharge", Amount=1_000_000 }] });
        var env=new ColonyEnvironment { WorldId=state.WorldId, ContextKey="bounded-domain-context", Ut=through };
        env.ConstructionLaborByOrder.Add(orderId,new ColonyConstructionLaborWitness {
            ProviderId="Explicit finite paid fixture", PoolId="one finite pool", EvidenceHash=Sha('d'), ContextKey=env.ContextKey,
            Workers=workers, PoolCapacity=Math.Max(1,workers), ValidFromUt=from, ValidThroughUt=through, CostIncludedInPaidEscrow=true });
        ColonyStateCodec.Validate(state); return (state,env);
    }
    static void Conserved(ColonyState before,ColonyState after)
    {
        Assert.Equal(before.Colonies.Single().SpentFunds,after.Colonies.Single().SpentFunds);
        Assert.Equal(before.Colonies.Single().Stock.Select(s => (s.Resource,s.Amount,s.Reserved,s.ImportedAmount)),after.Colonies.Single().Stock.Select(s => (s.Resource,s.Amount,s.Reserved,s.ImportedAmount)));
        Assert.Equal(before.Construction.Select(o => (o.Funds,o.LaborFunds,o.FundsPaid,o.FundsConsumed,o.MaterialsConsumed,o.TemplateHash)),after.Construction.Select(o => (o.Funds,o.LaborFunds,o.FundsPaid,o.FundsConsumed,o.MaterialsConsumed,o.TemplateHash)));
        Assert.Equal(before.Effects.Count,after.Effects.Count); ColonyStateCodec.Validate(after);
    }
    [Fact]
    public void ExactNativeNumbersFinishAndChronologyReachesTargetWithoutGrantingAnEpsilon()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);
        Assert.Equal(NativeFrom,NativeFrom+(32994-32993.999999999534)/2);
        var original=ColonyStateCodec.Serialize(state); var next=ColonyEngine.Advance(state,env);
        Assert.Equal(NativeTarget,next.SimulatedUt); Assert.Equal("awaitingPlacement",next.Construction.Single().State);
        Assert.Equal(32994,next.Construction.Single().WorkCompleted);Conserved(state,next);
        Assert.Equal(original,ColonyStateCodec.Serialize(state));
    }
    [Fact]
    public void OneEventBudgetSchedulesOnlyTheNextActualRepresentableInstant()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);
        var next=ColonyEngine.Advance(state,env,1);
        Assert.Equal(Next(NativeFrom),next.SimulatedUt);Assert.Equal(NativeTarget,next.TargetUt);
        Assert.Equal("awaitingPlacement",next.Construction.Single().State);Conserved(state,next);
        var final=ColonyEngine.Advance(next,env);Assert.Equal(NativeTarget,final.SimulatedUt);Conserved(next,final);
    }
    [Theory]
    [InlineData(1000.0,1)] [InlineData(2104322.1539013674,2)] [InlineData(100000000.0,3)] [InlineData(900000000000.0,512)]
    public void PositiveFiniteLargeUtRoundsToARealWitnessedInterval(double from,int workers)
    {
        var completed=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0)-1);
        var (state,env)=Fixture(from,1,completed,workers,from+10);
        Assert.Equal(from,from+(1-completed)/workers);
        var next=ColonyEngine.Advance(state,env,1);Assert.Equal(Next(from),next.SimulatedUt);
        Assert.Equal(1,next.Construction.Single().WorkCompleted);Assert.Equal("awaitingPlacement",next.Construction.Single().State);Conserved(state,next);
        Assert.True(1-completed <= (next.SimulatedUt-from)*workers);
    }
    [Fact]
    public void WitnessEndingBeforeNextInstantGrantsNoResidualWorkAndDoesNotStallOtherChronology()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);
        env.ConstructionLaborByOrder.Values.Single().ValidThroughUt=NativeFrom;
        var next=ColonyEngine.Advance(state,env);
        Assert.Equal(NativeTarget,next.SimulatedUt);Assert.Equal(state.Construction.Single().WorkCompleted,next.Construction.Single().WorkCompleted);
        Assert.Equal("building",next.Construction.Single().State);Assert.Equal(NativeTarget,next.Construction.Single().AccountedUt);Conserved(state,next);
        env.Ut=NativeTarget+10;var stale=ColonyEngine.Advance(next,env);
        Assert.Equal(next.Construction.Single().WorkCompleted,stale.Construction.Single().WorkCompleted);Conserved(next,stale);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ZeroOrMissingWorkerWitnessNeverFinishesTheTinyResidual(bool missing)
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,0,NativeTarget);
        if(missing)env.ConstructionLaborByOrder.Clear();var next=ColonyEngine.Advance(state,env);
        Assert.Equal(NativeTarget,next.SimulatedUt);Assert.Equal(state.Construction.Single().WorkCompleted,next.Construction.Single().WorkCompleted);
        Assert.Equal("building",next.Construction.Single().State);Conserved(state,next);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void UnpaidOrWrongContextWitnessNeverFinishesTheTinyResidual(bool context)
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);var witness=env.ConstructionLaborByOrder.Values.Single();
        if(context)witness.ContextKey="different context";else witness.CostIncludedInPaidEscrow=false;
        var next=ColonyEngine.Advance(state,env);Assert.Equal(NativeTarget,next.SimulatedUt);
        Assert.Equal(state.Construction.Single().WorkCompleted,next.Construction.Single().WorkCompleted);Conserved(state,next);
    }
    [Fact]
    public void TwoFinitePoolOrdersShareTheRealIntervalWithoutOversubscriptionOrDuplicatePayment()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);var first=state.Construction.Single();
        var other=ColonyStateCodec.Copy(state).Construction.Single();other.Id=Id();other.PlotId=Id();
        state.Construction.Add(other);state.Colonies.Single().Plots.Add(new ColonyPlot { Id=other.PlotId,SurveyHash=Sha('f'),WidthMeters=10,LengthMeters=10,ReservedBy=other.Id });
        state.Colonies.Single().SpentFunds=2000;
        var witness=env.ConstructionLaborByOrder.Values.Single();witness.PoolCapacity=4;
        env.ConstructionLaborByOrder.Add(other.Id,new ColonyConstructionLaborWitness { ProviderId=witness.ProviderId,PoolId=witness.PoolId,EvidenceHash=witness.EvidenceHash,ContextKey=env.ContextKey,Workers=2,PoolCapacity=4,ValidFromUt=NativeFrom,ValidThroughUt=NativeTarget,CostIncludedInPaidEscrow=true });
        var next=ColonyEngine.Advance(state,env,1);Assert.Equal(Next(NativeFrom),next.SimulatedUt);
        Assert.All(next.Construction,o => {Assert.Equal("awaitingPlacement",o.State);Assert.Equal(o.WorkRequired,o.WorkCompleted);});Conserved(state,next);
    }
    [Fact]
    public void RoundedFutureWitnessStartDoesNotCreditAnyEarlierTime()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);var start=NativeFrom+1;
        env.ConstructionLaborByOrder.Values.Single().ValidFromUt=start;
        var next=ColonyEngine.Advance(state,env,1);Assert.Equal(Next(start),next.SimulatedUt);Assert.Equal(next.SimulatedUt,next.Construction.Single().AccountedUt);
        Assert.Equal("awaitingPlacement",next.Construction.Single().State);Conserved(state,next);
    }
    [Fact]
    public void InitiallyRepresentableButRoundedDownCompletionStillFinishesByARealLaterInterval()
    {
        var (state,env)=Fixture(NativeFrom,100,99.99999999,3,NativeTarget);
        var next=ColonyEngine.Advance(state,env);Assert.Equal(NativeTarget,next.SimulatedUt);
        Assert.Equal(100,next.Construction.Single().WorkCompleted);Assert.Equal("awaitingPlacement",next.Construction.Single().State);Conserved(state,next);
    }
    [Fact]
    public void ZeroInitialWorkAtFractionalUtConsumesExactlyOnceAndCompletesRoundedDownBoundary()
    {
        var (state,env)=Fixture(NativeFrom,0.005,0,3,NativeTarget);var order=state.Construction.Single();
        order.FundsConsumed=0;order.MaterialsConsumed=false;state.Colonies.Single().SpentFunds=0;
        var metal=state.Colonies.Single().Stock.Single(s => s.Resource=="Metal");metal.Amount=5_000_000;metal.Reserved=2_000_000;
        var charge=state.Colonies.Single().Stock.Single(s => s.Resource=="ElectricCharge");charge.Amount=4_000_000;charge.Reserved=1_000_000;
        var original=ColonyStateCodec.Serialize(state);var first=ColonyEngine.Advance(state,env,1);var firstOrder=first.Construction.Single();
        var realWork=(first.SimulatedUt-NativeFrom)*3;Assert.True(realWork<0.005);
        Assert.Equal(realWork,firstOrder.WorkCompleted);Assert.Equal("building",firstOrder.State);Assert.True(firstOrder.MaterialsConsumed);
        Assert.InRange(firstOrder.FundsConsumed,0,firstOrder.Funds);Assert.Equal(firstOrder.FundsConsumed,first.Colonies.Single().SpentFunds);
        Assert.Equal(3_000_000,first.Colonies.Single().Stock.Single(s => s.Resource=="Metal").Amount);
        Assert.Equal(3_000_000,first.Colonies.Single().Stock.Single(s => s.Resource=="ElectricCharge").Amount);
        var next=ColonyEngine.Advance(first,env);Assert.Equal(NativeTarget,next.SimulatedUt);Assert.Equal("awaitingPlacement",next.Construction.Single().State);
        Assert.Equal(0.005,next.Construction.Single().WorkCompleted);Assert.Equal(1000,next.Construction.Single().FundsConsumed);Assert.Equal(1000,next.Colonies.Single().SpentFunds);
        Assert.Equal(first.Colonies.Single().Stock.Select(s => (s.Amount,s.Reserved)),next.Colonies.Single().Stock.Select(s => (s.Amount,s.Reserved)));
        var again=ColonyEngine.Advance(next,env);Assert.Equal(ColonyStateCodec.Serialize(next),ColonyStateCodec.Serialize(again));
        Assert.Equal(original,ColonyStateCodec.Serialize(state));
    }
    [Fact]
    public void LaterImportArrivalIsNotStarvedByRoundedConstructionCompletion()
    {
        var (state,env)=Fixture(NativeFrom,32994,32993.999999999534,2,NativeTarget);var colony=state.Colonies.Single();
        state.Suppliers.Add(new ColonySupplier { Id="finite-fixture-supplier",Resource="Metal",TravelSeconds=1 });
        state.Shipments.Add(new ColonyShipment { Id=Id(),ColonyId=colony.Id,SupplierId="finite-fixture-supplier",Kind="import",State="inTransit",Resource="Metal",Amount=1_000_000,Funds=1,DepartUt=NativeFrom,ArrivalUt=NativeFrom+1,TravelSeconds=1 });
        colony.Stock.Single(s => s.Resource=="Metal").IncomingReserved=1_000_000;
        var next=ColonyEngine.Advance(state,env);Assert.Equal(NativeTarget,next.SimulatedUt);Assert.Equal("arrived",next.Shipments.Single().State);
        Assert.Equal("awaitingPlacement",next.Construction.Single().State);Assert.Equal(32994,next.Construction.Single().WorkCompleted);
        var metal=next.Colonies.Single().Stock.Single(s => s.Resource=="Metal");Assert.Equal(4_000_000,metal.Amount);Assert.Equal(1_000_000,metal.ImportedAmount);Assert.Equal(0,metal.IncomingReserved);
        Assert.Equal(state.Colonies.Single().SpentFunds,next.Colonies.Single().SpentFunds);Assert.Equal(state.Construction.Single().FundsConsumed,next.Construction.Single().FundsConsumed);
        ColonyStateCodec.Validate(next);
    }
}
