using Expanse.Clock.Core;

namespace Expanse.Clock.Tests;

public sealed class ColonyPowerProtocolTests
{
    private static ColonyVessel Vessel(ColonyPowerRate? power = null) => new(
        "e79c13cb-6a7f-4053-925c-afd903382380", "Minmus PDU", "Minmus", "Greater Flats",
        1, 2, "loaded", 2, [], [], power);

    private static ClockSample Sample(ColonyVessel vessel) => new(
        ClockProtocol.Version, "clockSample", 1, Guid.NewGuid(), Guid.NewGuid(),
        @"C:\Kerbal Space Program", "Expanse", "Expanse", 100, true, "FLIGHT", false,
        null, 1, Colony: new ColonySnapshot("observed", null, 100, [vessel]));

    [Fact]
    public void LegacyColonyCaptureWithoutPowerStillDecodes()
    {
        var sample = Sample(Vessel());
        var json = ClockProtocol.Encode(sample).AsSpan(4).ToArray();
        var decoded = ClockProtocol.DecodeClockSample(json);
        Assert.Null(decoded.Colony!.Vessels[0].Power);
    }

    [Fact]
    public void ValidPartialAndUnavailableRatesPreserveColony()
    {
        var partial = new ColonyPowerRate("partial", "Fulfilled EC requests only.", 100, 3,
            25, 21, 4);
        var decoded = ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel(partial))).AsSpan(4).ToArray());
        Assert.Equal(25, decoded.Colony!.Vessels[0].Power!.GenerationEcPerSecond);

        var unavailable = new ColonyPowerRate("unavailable", "Facility is unloaded.", 100,
            null, null, null, null);
        ClockProtocol.Validate(Sample(Vessel(unavailable)));
    }

    [Fact]
    public void InventedOrInconsistentRatesAreIsolatedFromClock()
    {
        var invented = new ColonyPowerRate("partial", null, 100, 3, 25, 21, 25);
        var decoded = ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel(invented))).AsSpan(4).ToArray());
        Assert.Equal("unavailable", decoded.Colony!.Status);
        Assert.Equal(100, decoded.UtSeconds);
        Assert.True(decoded.ActiveWorld);
    }

    [Fact]
    public void NominalEstimateRoundTripsWithoutCreatingMeasuredRate()
    {
        var estimate = new ColonyPowerEstimate("nominal", "Active converter recipes only.", 1200, 2, 2);
        var vessel = Vessel() with { PowerEstimate = estimate };
        var decoded = ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(vessel)).AsSpan(4).ToArray());
        Assert.Null(decoded.Colony!.Vessels[0].Power);
        Assert.Equal(estimate, decoded.Colony.Vessels[0].PowerEstimate);
    }

    [Fact]
    public void UnloadedOrInvalidEstimateIsRejectedWithoutAffectingClock()
    {
        var estimate = new ColonyPowerEstimate("nominal", "Recipe ratios.", 1200, 0, 1);
        var unloaded = Vessel() with { ObservationBasis = "snapshot", PowerEstimate = estimate };
        var decoded = ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(unloaded)).AsSpan(4).ToArray());
        Assert.Equal("unavailable", decoded.Colony!.Status);

        var invalid = Vessel() with { PowerEstimate = estimate with { GenerationEcPerSecond = double.NaN } };
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(Sample(invalid)));
    }
}
