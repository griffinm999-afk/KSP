namespace Expanse.Clock.Core;

public sealed record ColonyFinding(string Resource, string Code, string Severity, string Message);

public static class ColonyDiagnosis
{
    // This checks configuration and observed storage. KSP's activation switch is
    // not proof of positive conversion, and no production rate is inferred.
    public static ColonyFinding[] ForConverter(ColonySnapshot? snapshot, ColonyVessel vessel, ColonyConverter converter)
    {
        if (snapshot?.Status != "observed" || vessel.ObservationBasis != "loaded")
            return Array.Empty<ColonyFinding>();
        var findings = new List<ColonyFinding>();
        foreach (var input in converter.Inputs.Distinct(StringComparer.Ordinal))
        {
            if (input is "ElectricCharge" or "Machinery" or "EnrichedUranium" or "DepletedFuel") continue;
            var tanks = vessel.Tanks.Where(t => t.Resource == input && t.Capacity > 0).ToArray();
            if (tanks.Length == 0)
            {
                findings.Add(new(input, "missingReceiver", "action",
                    $"No {input} tank on this vessel. Add a warehouse-capable receiving buffer for automatic local supply."));
                continue;
            }
            if (tanks.Any(EligibleWarehouse))
            {
                if (tanks.Where(t => t.FlowEnabled == true).Sum(t => t.Amount) <= 0)
                    findings.Add(new(input, "inputEmpty", "check",
                        $"{input} receiving storage is empty. Check source stock, the hopper, and local transfer before relying on this recipe."));
                continue;
            }
            if (tanks.All(t => t.WarehousePresent == false))
                findings.Add(new(input, "missingWarehouse", "action",
                    $"{input} is stored here, but this vessel has no {input} warehouse buffer for automatic replenishment."));
            else if (tanks.Any(t => t.WarehousePresent is null || t.WarehousePresent == true &&
                (t.LocalWarehouseOn is null || t.FlowEnabled is null))) continue;
            else if (tanks.Where(t => t.WarehousePresent == true).All(t => t.LocalWarehouseOn == false))
                findings.Add(new(input, "localWarehouseOff", "action",
                    $"Turn on Local Warehouse for a {input} receiving tank on this vessel."));
            else if (tanks.Where(t => t.WarehousePresent == true && t.LocalWarehouseOn == true).All(t => t.FlowEnabled == false))
                findings.Add(new(input, "flowOff", "action",
                    $"Enable resource flow on a {input} receiving warehouse tank."));
        }
        foreach (var output in converter.Outputs.Distinct(StringComparer.Ordinal))
        {
            if (output is "ElectricCharge" or "Recyclables" or "DepletedFuel") continue;
            var tanks = vessel.Tanks.Where(t => t.Resource == output && t.Capacity > 0).ToArray();
            if (tanks.Length == 0)
                findings.Add(new(output, "missingOutputStorage", "check",
                    $"No {output} output storage was observed on this vessel. Check this recipe's output behavior before enabling it."));
            else if (tanks.All(t => t.Amount >= t.Capacity))
                findings.Add(new(output, "outputFull", "check",
                    $"{output} storage on this vessel is full. Production may pause unless the recipe can discard excess."));
        }
        return findings.ToArray();
    }

    public static bool EligibleWarehouse(ColonyTank tank) => tank.Capacity > 0 &&
        tank.WarehousePresent == true && tank.LocalWarehouseOn == true && tank.FlowEnabled == true;
}
