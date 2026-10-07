using Expanse.Domain.Colonies;
using Xunit;

namespace Expanse.Clock.Tests;

public sealed class ColonyUtilityCurveTests
{
    [Fact]
    public void InteriorHermitePeakIsIncludedRatherThanOnlySampledKeys()
    {
        var points=new[]{new ColonyUtilityCurvePoint{Time=0,Value=0,OutTangent=4},new ColonyUtilityCurvePoint{Time=1,Value=0,InTangent=-4}};
        Assert.True(ColonyUtilityCurveBounds.TryMaximum(points,out var maximum));Assert.Equal(1,maximum,10);
    }
    [Fact]
    public void FlatLinearAndMonotoneReactorCurvesHaveExactBounds()
    {
        Assert.True(ColonyUtilityCurveBounds.TryMaximum(new[]{new ColonyUtilityCurvePoint{Time=0,Value=.5}},out var flat));Assert.Equal(.5,flat);
        Assert.True(ColonyUtilityCurveBounds.TryMaximum(new[]{new ColonyUtilityCurvePoint{Time=0,OutTangent=80},new ColonyUtilityCurvePoint{Time=100,Value=8000,InTangent=80}},out var linear));Assert.Equal(8000,linear);
        Assert.True(ColonyUtilityCurveBounds.TryMaximum(new[]{new ColonyUtilityCurvePoint{Time=0},new ColonyUtilityCurvePoint{Time=100,Value=15000}},out var smooth));Assert.Equal(15000,smooth);
    }
    [Fact]
    public void NonFiniteDuplicateAndOversizedCurvesReject()
    {
        Assert.False(ColonyUtilityCurveBounds.TryMaximum(new[]{new ColonyUtilityCurvePoint{Time=0,Value=double.NaN}},out _));
        Assert.False(ColonyUtilityCurveBounds.TryMaximum(new[]{new ColonyUtilityCurvePoint{Time=1},new ColonyUtilityCurvePoint{Time=1}},out _));
        Assert.False(ColonyUtilityCurveBounds.TryMaximum(new ColonyUtilityCurvePoint[65],out _));
    }
}
