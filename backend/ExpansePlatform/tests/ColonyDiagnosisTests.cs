using Expanse.Clock.Core;

namespace Expanse.Clock.Tests;

public sealed class ColonyDiagnosisTests
{
    static readonly ColonyConverter Fertilizer = new("Tundra Agriculture Support", "Fertilizer(G)", true,
        ["Gypsum", "ElectricCharge", "Machinery"], ["Fertilizer"]);
    static ColonyVessel Vessel(params ColonyTank[] tanks) => new("v1", "Fertilizer plant", "Minmus", "Greater Flats",
        3, 2.9, "loaded", 4, tanks, [Fertilizer]);
    static ColonySnapshot Snapshot(ColonyVessel vessel, string status = "observed") => new(status, null, 100, [vessel]);
    static ColonyTank Tank(string resource, bool? warehouse = true, bool? local = true, bool? flow = true,
        double amount = 1, double capacity = 10) => new(resource, amount, capacity, warehouse, local, flow);

    [Fact] public void MissingReceivingBufferIsActionableWithoutMisdiagnosingEcOrMachinery()
    {
        var vessel = Vessel(Tank("Fertilizer"));
        var findings = ColonyDiagnosis.ForConverter(Snapshot(vessel), vessel, Fertilizer);
        Assert.Contains(findings, f => f.Resource == "Gypsum" && f.Code == "missingReceiver");
        Assert.DoesNotContain(findings, f => f.Resource is "ElectricCharge" or "Machinery");
    }

    [Fact] public void SeparatesWarehouseOffFlowOffAndNonwarehouseTank()
    {
        foreach (var (tank, code) in new[]
        {
            (Tank("Gypsum", warehouse:false), "missingWarehouse"),
            (Tank("Gypsum", local:false), "localWarehouseOff"),
            (Tank("Gypsum", flow:false), "flowOff")
        })
        {
            var vessel = Vessel(tank, Tank("Fertilizer"));
            Assert.Contains(ColonyDiagnosis.ForConverter(Snapshot(vessel), vessel, Fertilizer), f => f.Code == code);
        }
    }

    [Fact] public void MixedTankStatesRequireOneEligibleWarehouseBuffer()
    {
        var vessel = Vessel(Tank("Gypsum", local:false), Tank("Gypsum", warehouse:false),
            Tank("Gypsum", amount:0.1), Tank("Fertilizer"));
        Assert.DoesNotContain(ColonyDiagnosis.ForConverter(Snapshot(vessel), vessel, Fertilizer),
            f => f.Resource == "Gypsum");
        // A same-vessel ordinary tank can feed the converter directly, yet it
        // cannot request automatic stock from a neighboring vessel.
        var ordinary = Vessel(Tank("Gypsum", warehouse:false), Tank("Fertilizer"));
        Assert.Equal("missingWarehouse", ColonyDiagnosis.ForConverter(Snapshot(ordinary), ordinary, Fertilizer)
            .Single(f => f.Resource == "Gypsum").Code);
    }

    [Fact] public void UnknownUnloadedAndPartialObservationsDoNotAssertMissingBuffers()
    {
        var unknown = Vessel(Tank("Gypsum", warehouse:true, local:null, flow:null), Tank("Fertilizer"));
        Assert.DoesNotContain(ColonyDiagnosis.ForConverter(Snapshot(unknown), unknown, Fertilizer), f => f.Resource == "Gypsum");
        var unloaded = unknown with { ObservationBasis = "snapshot", Tanks = [] };
        Assert.Empty(ColonyDiagnosis.ForConverter(Snapshot(unloaded), unloaded, Fertilizer));
        var partial = Vessel(Tank("Fertilizer"));
        Assert.Empty(ColonyDiagnosis.ForConverter(Snapshot(partial, "truncated"), partial, Fertilizer));
    }

    [Fact] public void OutputCapacityIsReportedWithoutClaimingProductionRate()
    {
        var vessel = Vessel(Tank("Gypsum"));
        Assert.Contains(ColonyDiagnosis.ForConverter(Snapshot(vessel), vessel, Fertilizer),
            f => f.Code == "missingOutputStorage" && f.Resource == "Fertilizer");
        var full = Vessel(Tank("Gypsum"), Tank("Fertilizer", amount:10));
        Assert.Contains(ColonyDiagnosis.ForConverter(Snapshot(full), full, Fertilizer), f => f.Code == "outputFull");
    }

    [Fact] public void EmptyEligibleInputIsFlaggedButAnotherAvailableTankClearsIt()
    {
        var empty = Vessel(Tank("Gypsum", amount:0), Tank("Fertilizer"));
        Assert.Contains(ColonyDiagnosis.ForConverter(Snapshot(empty), empty, Fertilizer), f => f.Code == "inputEmpty");
        var stocked = Vessel(Tank("Gypsum", amount:0), Tank("Gypsum", warehouse:false, amount:2), Tank("Fertilizer"));
        Assert.DoesNotContain(ColonyDiagnosis.ForConverter(Snapshot(stocked), stocked, Fertilizer), f => f.Code == "inputEmpty");
    }
}
