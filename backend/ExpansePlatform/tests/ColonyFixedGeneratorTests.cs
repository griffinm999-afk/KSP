using System;
using Expanse.Domain.Colonies;
using Xunit;
public sealed class FixedGeneratorTests
{
    static bool Bound(double fuel,double horizon,out double output,out double endurance)=>ColonyFixedGeneratorBounds.TryFuelBound(fuel,20,20,1e-6,50,1,1,horizon,out output,out endurance,out _);
    [Fact]public void FullFuelRatingFallsConservativelyOverSixDaysWithoutMakingFuel()
    {double fuel=20;Assert.True(Bound(fuel,6*21600,out double output,out double endurance));Assert.InRange(output,49.67,49.68);Assert.Equal(20000000,endurance);Assert.Equal(20,fuel);}
    [Fact]public void PositiveWornRequiredFuelIsThrottledRatherThanPermanentlyBlocked()
    {Assert.True(Bound(19.9,21600,out double output,out _));Assert.InRange(output,49.69,49.70);}
    [Theory][InlineData(2,2)][InlineData(2,1.99)][InlineData(20,20)][InlineData(20,19.9)]
    public void InitialPaidFuelWitnessPreservesOriginalAndFullFuelPackageAllocations(double reviewed,double actual)
    {Assert.True(ColonyFixedGeneratorBounds.InitialFuelWitness(reviewed,actual,20));Assert.True(Bound(actual,21600,out double output,out _));Assert.True(output>0);Assert.True(output<50);}
    [Theory][InlineData(0,2,20)][InlineData(21,2,20)][InlineData(2,20,20)][InlineData(2,0,20)][InlineData(2,2,21)][InlineData(double.NaN,2,20)]
    public void UnpaidExtraEmptyOrAlteredFuelCannotAuthorizeInitialCommissioning(double reviewed,double actual,double capacity)
    {Assert.False(ColonyFixedGeneratorBounds.InitialFuelWitness(reviewed,actual,capacity));}
    [Fact]public void ActualOriginalPaidPackageBillsRemainUsableWithoutFullTankRetrofit()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);string? templates=null;
        while(directory!=null){string candidate=Path.Combine(directory.FullName,"package/GameData/ExpanseWorldBridge/Templates");if(Directory.Exists(candidate)){templates=candidate;break;}directory=directory.Parent;}
        Assert.NotNull(templates);
        foreach(var package in new[]{("power-duna-v1",2,2d),("agriculture-duna-v1",1,2d),("cultivation-duna-v1",1,20d)})
        {
            using var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(templates!,package.Item1+".manifest.json")));
            var fuels=manifest.RootElement.GetProperty("StartupContents").EnumerateArray().Where(r=>r.GetProperty("ResourceName").GetString()=="Plutonium-238").ToArray();
            Assert.Equal(package.Item2,fuels.Length);
            foreach(var fuel in fuels){double paid=fuel.GetProperty("Amount").GetInt64()/1e6;Assert.Equal(package.Item3,paid);Assert.True(ColonyFixedGeneratorBounds.InitialFuelWitness(paid,paid,20));}
            long embedded=manifest.RootElement.GetProperty("EmbeddedContents").EnumerateArray().Single(r=>r.GetProperty("Resource").GetString()=="Plutonium-238").GetProperty("Amount").GetInt64();
            Assert.Equal(fuels.Sum(f=>f.GetProperty("Amount").GetInt64()),embedded);
        }
    }
    [Fact]public void NearlyEmptyFuelCannotPromiseCurrentOutputForAFullHorizon()
    {Assert.False(Bound(.01,21600,out double output,out double endurance));Assert.Equal(0,output);Assert.Equal(10000,endurance);}
    [Theory][InlineData(0)][InlineData(-1)][InlineData(21)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidPhysicalFuelCannotQualify(double fuel)=>Assert.False(Bound(fuel,21600,out _,out _));
    [Fact]public void EqualityAtFuelExhaustionNeverQualifiesByFloatingRoundoff()
    {Assert.False(Bound(.0216,21600,out _,out _));Assert.False(Bound(20,20000000,out _,out _));}
    [Fact]public void ConservativeSharedBurnAndMultiplierAreBothAppliedOnce()
    {Assert.True(ColonyFixedGeneratorBounds.TryFuelBound(20,20,20,2e-6,50,1,2,21600,out double output,out double endurance,out _));Assert.InRange(output,49.78,49.79);Assert.Equal(5000000,endurance);}
    [Theory][InlineData(true,3,1)][InlineData(false,3,3)][InlineData(false,1,1)]
    public void ObservedFinalRecipeUsesNativePostRecipeMultiplierOnce(bool pre,double multiplier,double expected)=>Assert.Equal(expected,ColonyFixedGeneratorBounds.RecipeMultiplier(pre,multiplier));
}
