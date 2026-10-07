using System.IO;
using System.Reflection;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyPureTick53Tests
{
    static ColonyState Sequential(ColonyState s, ColonyEnvironment e, string? cursor = null)
    {
        s=ColonyEngine.RunPlanning(s,e,cursor);
        s=ColonyEngine.RunPlanningPolicies(s,e);
        s=ColonyEngine.RunPhysicalProcurementPolicies(s,e);
        return ColonyEngine.RunProductionOperatingTransfers(s,e);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("automatic")]
    [InlineData("disabled")]
    public void GrowthTransitionsMatchSequentialCallsAndDetachInput(string policy)
    {
        var(s,e,_)=ColonyPlanningTests.GrowthFixture();s.Colonies[0].Charter.GrowthPolicy=policy;
        var before=ColonyStateCodec.Serialize(s);
        var expected=Sequential(s,e);var actual=ColonyEngine.RunPlanningAndProcurement(s,e);
        Assert.Equal(ColonyStateCodec.Serialize(expected),ColonyStateCodec.Serialize(actual));
        Assert.Equal(before,ColonyStateCodec.Serialize(s));
        if(!ReferenceEquals(s,actual))
        {
            actual.Colonies[0].Name="Caller mutation";
            Assert.Equal(before,ColonyStateCodec.Serialize(s));
            Assert.NotEqual(actual.Colonies[0].Name,expected.Colonies[0].Name);
        }
    }

    [Fact]
    public void CursorOrderAndPaidChildClaimsMatchSequentialCalls()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Approve(s,e,id);
        var second=ColonyStateCodec.Copy(s).Colonies.Single();second.Id=Guid.NewGuid().ToString("D");second.Site.Latitude=20;
        foreach(var p in second.Plots){p.Id=Guid.NewGuid().ToString("D");p.ReservedBy="";p.Latitude=20;}
        s.Colonies.Add(second);e.People.PresentByColony[second.Id]=new();s=ColonyPlanningTests.Approve(s,e,second.Id);
        string cursor=s.Plans.Single(p=>p.ColonyId==id).Id;
        var before=ColonyStateCodec.Serialize(s);var expected=Sequential(s,e,cursor);var actual=ColonyEngine.RunPlanningAndProcurement(s,e,cursor);
        // Funds-effect IDs are independently minted by each pure replay. Match
        // the exact deterministic owner/target, then compare every saved field.
        var normalized=ColonyStateCodec.Copy(actual);
        Assert.Equal(expected.Effects.Count,actual.Effects.Count);
        foreach(var effect in normalized.Effects)
            effect.Id=expected.Effects.Single(x=>x.OperationId==effect.OperationId&&x.TargetId==effect.TargetId&&x.Kind==effect.Kind).Id;
        Assert.Equal(ColonyStateCodec.Serialize(expected),ColonyStateCodec.Serialize(normalized));
        Assert.Equal(before,ColonyStateCodec.Serialize(s));
        Assert.Equal("reserved",actual.Colonies.Single(c=>c.Id==second.Id).Logistics.State);
        Assert.Equal("none",actual.Colonies.Single(c=>c.Id==id).Logistics.State);
        Assert.Equal(ColonyEngine.PendingCash(expected),ColonyEngine.PendingCash(actual));
    }

    [Fact]
    public void EveryPublicBoundaryRejectsMalformedCallerMutation()
    {
        var(s,e,_)=ColonyPlanningTests.GrowthFixture();
        var same=ColonyEngine.RunPlanningAndProcurement(s,e);
        same.Colonies[0].Stock[0].Amount=-1;
        foreach(var call in new Func<ColonyState,ColonyEnvironment,ColonyState>[] {
            (p,v)=>ColonyEngine.RunPlanning(p,v),ColonyEngine.RunPlanningPolicies,
            ColonyEngine.RunPhysicalProcurementPolicies,ColonyEngine.RunProductionOperatingTransfers,
            (p,v)=>ColonyEngine.RunPlanningAndProcurement(p,v)})
            Assert.Throws<InvalidDataException>(()=>call(same,e));
    }

    [Theory]
    [InlineData("world")]
    [InlineData("context")]
    [InlineData("funds")]
    [InlineData("local")]
    public void CombinedBoundaryKeepsEnvironmentAndLocalStockValidation(string malformed)
    {
        var(s,e,_)=ColonyPlanningTests.GrowthFixture();var before=ColonyStateCodec.Serialize(s);
        if(malformed=="world")e.WorldId=Guid.NewGuid().ToString("D");
        if(malformed=="context")e.ContextKey="";
        if(malformed=="funds")e.AvailableFunds=-1;
        if(malformed=="local")e.Planning.LocalStocks.Add(new(){Amount=-1});
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.RunPlanningAndProcurement(s,e));
        Assert.Equal(before,ColonyStateCodec.Serialize(s));
    }

    [Fact]
    public void HeldEffectsAndUnadvancedTimeRetainExistingPhaseBehavior()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Approve(s,e,id);s=ColonyEngine.RunPlanning(s,e);
        var held=ColonyEngine.HoldEffect(s,s.Effects[0].Id,"Unknown native outcome");
        Assert.Same(held,ColonyEngine.RunPlanningAndProcurement(held,e));
        e.Ut++;Assert.Same(s,ColonyEngine.RunPlanningAndProcurement(s,e));
    }

    [Fact]
    public void UnvalidatedCoresArePrivate()
    {
        foreach(string name in new[]{"RunPlanningValidated","RunPlanningPoliciesValidated","RunPhysicalProcurementPoliciesValidated","RunProductionOperatingTransfersValidated"})
        {
            Assert.Null(typeof(ColonyEngine).GetMethod(name,BindingFlags.Public|BindingFlags.Static));
            Assert.True(typeof(ColonyEngine).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static)!.IsPrivate);
        }
    }
}
