using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyServiceOperation
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public uint PartId { get; set; }
        public string DepotId { get; set; } = "";
        public string SourceResource { get; set; } = "";
        public string DestinationResource { get; set; } = "";
        public long Amount { get; set; }
        public string State { get; set; } = "reserved";
        public bool SourceDebited { get; set; }
        public double CreatedUt { get; set; }
        public double CompletedUt { get; set; }
        public string Provider { get; set; } = "";
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "Real stock reserved; awaiting exact physical tank provider.";
    }
    public sealed class ColonyServicePolicy
    {
        public string ColonyId { get; set; } = "";
        public bool AutomaticEnabled { get; set; }
        public double CadenceSeconds { get; set; } = 21600;
        public double TargetFillFraction { get; set; } = .95;
    }
    public sealed class ColonyServiceTarget
    {
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public uint PartId { get; set; }
        public string PartName { get; set; } = "";
        public string DepotId { get; set; } = "";
        public string SourceResource { get; set; } = "";
        public string DestinationResource { get; set; } = "";
        public long Amount { get; set; }
        public long Capacity { get; set; }
        public string QuoteHash { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public double ObservedUt { get; set; }
        public bool Current { get; set; }
        public bool CanApply { get; set; }
        public bool QualifiedWorker { get; set; }
        public string WorkerWitness { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Reason { get; set; } = "";
    }
    public sealed class ColonyServicesEnvironment
    {
        public List<ColonyServiceTarget> Targets { get; set; } = new List<ColonyServiceTarget>();
        public List<ColonyUtilityReport> Utilities { get; set; } = new List<ColonyUtilityReport>();
    }
    public sealed class ColonyUtilityReport
    {
        public string FacilityId { get; set; } = "";
        public string VesselId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public double ObservedUt { get; set; }
        public string Context { get; set; } = "unknown";
        public double? ElectricCharge { get; set; }
        public double? ConnectedCapacity { get; set; }
        public double? NetStorageEcPerSecond { get; set; }
        public double? WindowSeconds { get; set; }
        public double? NominalGenerationEcPerSecond { get; set; }
        public double? NominalDemandEcPerSecond { get; set; }
        public double? MaximumPartTemperature { get; set; }
        public double? MinimumTemperatureMargin { get; set; }
        public double? MaximumCoreTemperature { get; set; }
        public double? MinimumCoreShutdownMargin { get; set; }
        public double? ContinuousFuelEnduranceSeconds { get; set; }
        public bool ActualConnectedPath { get; set; }
        public bool FullDemandAccounted { get; set; }
        public bool ContinuousSourceQualified { get; set; }
        public bool HeatRejectionQualified { get; set; }
        public bool BackgroundProviderQualified { get; set; }
        public bool InputsAccessible { get; set; }
        public string InputWitness { get; set; } = "";
        public bool DistributionReachQualified { get; set; }
        public string DistributionWitness { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
