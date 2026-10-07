using System.Text.Json.Serialization;

namespace Expanse.Clock.Core;

// Read-only observations from KSP. Null flags mean the bridge could not inspect
// the corresponding part setting (usually because the vessel was unloaded).
public sealed record ColonyTank(string Resource, double Amount, double Capacity,
    bool? WarehousePresent, bool? LocalWarehouseOn, bool? FlowEnabled,
    string Role = "storage");

public sealed record ColonyConverter(string PartName, string Recipe, bool? Running,
    string[] Inputs, string[] Outputs);
public sealed record ColonyCrewMember(string Name, string Profession);
public sealed record ColonyVesselCensus(string Status, string[] VesselIds, long? ObservationSequence, string? Reason = null);

public sealed record ColonyVessel(string VesselId, string Name, string Body, string Biome,
    double Latitude, double Longitude, string ObservationBasis, int Crew,
    ColonyTank[] Tanks, ColonyConverter[] Converters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonyPowerRate? Power = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonyPowerEstimate? PowerEstimate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonyProductionTelemetry? Production = null,
    ColonyCrewMember[]? CrewRoster = null,
    bool CrewRosterComplete = false,
    int? PhysicalCrewCapacity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonyLifeSupportTelemetry? LifeSupport = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PowerAverageTelemetry? PowerAverage = null);

// Fulfilled ElectricCharge requests observed on a loaded vessel. KSP modules
// may also change PartResource.amount directly, so these are tracked flows,
// not an assertion that all vessel generation or demand was captured.
public sealed record ColonyPowerRate(string Status, string? Reason, double? SampleUt,
    double? WindowSeconds, double? GenerationEcPerSecond,
    double? ConsumptionEcPerSecond, double? NetEcPerSecond);

// Configured ratios of active converters inspected while a vessel is packed.
// This remains separate from measured fulfilled ElectricCharge requests.
public sealed record ColonyPowerEstimate(string Status, string? Reason,
    double GenerationEcPerSecond, double ConsumptionEcPerSecond, int ModuleCount);

public sealed record WolfResource(string Name, int Incoming, int Outgoing, int Available);
public sealed record WolfDepot(string Body, string Biome, bool Established, WolfResource[] Resources);
public sealed record WolfSnapshot(string Status, string? Reason, double? ObservedUt,
    WolfDepot[] Depots, string[] AllowedResources);

public sealed record ColonySnapshot(string Status, string? Reason, double? ObservedUt,
    ColonyVessel[] Vessels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WolfSnapshot? Wolf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonyVesselCensus? VesselCensus = null);
