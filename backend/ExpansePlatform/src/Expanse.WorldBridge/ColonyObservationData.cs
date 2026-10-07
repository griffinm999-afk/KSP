using System;
using System.Collections.Generic;

namespace Expanse.WorldBridge
{
    internal sealed class ColonySnapshot
    {
        public string Status;
        public string Reason;
        public double? ObservedUt;
        public readonly List<ColonyVessel> Vessels = new List<ColonyVessel>();
        public WolfSnapshot Wolf;
        public ColonyVesselCensus VesselCensus;
    }
    internal sealed class ColonyVessel
    {
        public string VesselId, Name, Body, Biome, ObservationBasis;
        public double Latitude, Longitude;
        public int Crew;
        public readonly List<ColonyCrewMember> CrewRoster = new List<ColonyCrewMember>();
        public bool CrewRosterComplete;
        public int? PhysicalCrewCapacity;
        public readonly List<ColonyTank> Tanks = new List<ColonyTank>();
        public readonly List<ColonyConverter> Converters = new List<ColonyConverter>();
        public ColonyPowerRate Power;
        public ColonyPowerEstimate PowerEstimate;
        public ColonyProductionTelemetry Production;
        public ColonyLifeSupportTelemetry LifeSupport;
        public PowerAverageTelemetry PowerAverage;
        public CapturedPowerEstimate CapturedPower;
        public CapturedPowerFlow CapturedFlow;
    }
    internal sealed class ColonyCrewMember
    {
        public string Name, Profession;
    }
    internal sealed class ColonyTank
    {
        public string Resource, Role;
        public double Amount, Capacity;
        public bool? WarehousePresent, LocalWarehouseOn, FlowEnabled;
    }
    internal sealed class ColonyConverter
    {
        public string PartName, Recipe;
        public bool? Running;
        public string[] Inputs = new string[0], Outputs = new string[0];
    }

    internal sealed class ColonyPowerRate
    {
        public string Status, Reason;
        public double? SampleUt, WindowSeconds, GenerationEcPerSecond, ConsumptionEcPerSecond, NetEcPerSecond;
    }
    internal sealed class ColonyPowerEstimate
    {
        public string Status, Reason;
        public double GenerationEcPerSecond, ConsumptionEcPerSecond;
        public int ModuleCount;
    }

    internal sealed class ColonyProductionRateTelemetry { public string Resource,FlowMode;public double UnitsPerSecond;public bool DumpExcess; }
    internal sealed class ColonyProductionRequirementTelemetry { public string Resource;public double Amount; }
    internal sealed class ColonyProductionVector
    {
        public ColonyProductionRateTelemetry[] Inputs=new ColonyProductionRateTelemetry[0],Outputs=new ColonyProductionRateTelemetry[0];
        public ColonyProductionRequirementTelemetry[] Requirements=new ColonyProductionRequirementTelemetry[0];
    }
    internal sealed class ColonyProductionPotential { public double SampleUt,EfficiencyMultiplier,RequirementMultiplier;public ColonyProductionVector Rates; }
    internal sealed class ColonyProductionAchieved { public double SampleUt,IntervalGameSeconds,TimeFactor;public long CaptureSequence;public ColonyProductionRateTelemetry[] Inputs,Outputs; }
    internal sealed class ColonyProductionBackgroundRate { public double SampleUt;public string ConstraintState;public ColonyProductionRateTelemetry[] Inputs,Outputs; }
    internal sealed class ColonyProductionWolfPoints {public string Resource;public int Points;}
    internal sealed class ColonyProductionHopper {public string HopperId,Body,Biome;public bool? Connected;public ColonyProductionWolfPoints[] AllocationPoints=new ColonyProductionWolfPoints[0];}
    internal sealed class ColonyProductionHarvester {public string Resource;public double Efficiency,HarvestThreshold;public int HarvesterType;}
    internal sealed class ColonyProductionModuleTelemetry
    {
        public uint PartId;public uint? ModuleId;public int ModuleIndex;public string ModuleType,PartName,Recipe,RecipeHash,Basis,NativeStatus,Reason;
        public int? BayIndex,SelectedLoadout;public bool? Enabled,Activated;public ColonyProductionVector Configured;
        public ColonyProductionPotential Prepared;public ColonyProductionAchieved Achieved;public ColonyProductionBackgroundRate Background;public ColonyProductionHopper Hopper;
        public ColonyProductionHarvester Harvester;
    }
    internal sealed class ColonyProductionTelemetry
    {
        public int BudgetOmittedModuleCount;
        public long? BudgetSelectionSequence;
        public string InventoryStatus="partial";
        public string Status="partial",Reason="Broker observations cover supported native converters; other resource owners and direct writes are outside coverage.";
        public double ObservedUt;public readonly List<ColonyProductionModuleTelemetry> Modules=new List<ColonyProductionModuleTelemetry>();
    }
    internal sealed class WolfSnapshot
    {
        public string Status, Reason;
        public double? ObservedUt;
        public readonly List<WolfDepotRow> Depots = new List<WolfDepotRow>();
        public string[] AllowedResources = new string[0];
    }
    internal sealed class WolfDepotRow
    {
        public string Body, Biome;
        public bool Established;
        public readonly List<WolfResourceRow> Resources = new List<WolfResourceRow>();
    }
    internal sealed class WolfResourceRow
    {
        public string Name;
        public int Incoming, Outgoing, Available;
    }

}
