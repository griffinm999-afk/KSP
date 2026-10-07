using Expanse.Clock.Core;

namespace Expanse.Clock.Tests;

public sealed class WolfProtocolTests
{
    private static ClockSample Sample(WolfSnapshot wolf) => new(
        ClockProtocol.Version, "clockSample", 1, Guid.NewGuid(), Guid.NewGuid(),
        @"C:\Kerbal Space Program", "Expanse", "Expanse", 100, true, "FLIGHT", false,
        null, 1, Colony: new ColonySnapshot("observed", null, 100, [], wolf));

    [Fact]
    public void VirtualLedgerKeepsProductionAllocationAndDeficitDistinct()
    {
        var wolf = new WolfSnapshot("observed", null, 100,
            [new WolfDepot("Minmus", "Greater Flats", true,
                [new WolfResource("Power", 35, 35, 0), new WolfResource("Gypsum", 5, 10, -5)])],
            ["Power", "Gypsum", "EngineerCrewPoint"]);
        var decoded = ClockProtocol.DecodeClockSample(
            ClockProtocol.Encode(Sample(wolf)).AsSpan(4).ToArray());
        Assert.Equal(-5, decoded.Colony!.Wolf!.Depots[0].Resources[1].Available);
        Assert.Empty(decoded.Colony.Vessels);
    }

    [Fact]
    public void InvalidLedgerIsIsolatedFromClockAndPhysicalOperations()
    {
        var wolf = new WolfSnapshot("observed", null, 100,
            [new WolfDepot("Minmus", "Greater Flats", true,
                [new WolfResource("Power", 35, 10, 35)])], ["Power"]);
        var decoded = ClockProtocol.DecodeClockSample(
            ClockProtocol.Encode(Sample(wolf)).AsSpan(4).ToArray());
        Assert.Equal("observed", decoded.Colony!.Status);
        Assert.Equal("unavailable", decoded.Colony.Wolf!.Status);
        Assert.Equal(100, decoded.UtSeconds);
    }
}
