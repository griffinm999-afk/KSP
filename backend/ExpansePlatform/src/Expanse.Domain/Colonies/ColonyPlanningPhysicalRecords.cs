using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyLocalStock
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public string VesselId { get; set; } = "";
        public uint PartId { get; set; }
        public string PartName { get; set; } = "";
        public string DepotId { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
        public long Capacity { get; set; }
        public long PhysicalReserve { get; set; }
        public bool Current { get; set; }
        public bool CanApply { get; set; }
        public bool WarehouseEnabled { get; set; }
        public bool FlowAllowed { get; set; }
        public bool WithinRange { get; set; }
        public bool NativeInput { get; set; }
        public string StockKind { get; set; } = "warehouse";
        public bool QualifiedWorker { get; set; }
        public string WorkerWitness { get; set; } = "";
        public string Provider { get; set; } = "";
        public string ProviderVersion { get; set; } = "";
        public string MembershipHash { get; set; } = "";
        public string AccessWitness { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public double ObservedUt { get; set; }
        public string Reason { get; set; } = "";
    }
    public sealed class ColonyPhysicalTransferQuote
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string LocalStockId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public long Revision { get; set; }
        public string Direction { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
        public long PhysicalBefore { get; set; }
        public long PhysicalAfter { get; set; }
        public long OwnedBefore { get; set; }
        public long OwnedAfter { get; set; }
        public bool CanApprove { get; set; }
        public string Reason { get; set; } = "";
        public string Provider { get; set; } = "";
        public string AccessWitness { get; set; } = "";
        public string Provenance { get; set; } = "";
    }
    public sealed class ColonyPhysicalTransfer
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public string LocalStockId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string DepotId { get; set; } = "";
        public uint PartId { get; set; }
        public string Resource { get; set; } = "";
        public string Direction { get; set; } = "toColony";
        public long Amount { get; set; }
        public long ImportedAttribution { get; set; }
        public long PlannedMaterialAmount { get; set; }
        public bool ProvenanceUncertain { get; set; }
        public string Provenance { get; set; } = "";
        public string State { get; set; } = "reserved";
        public bool OwnedSourceDebited { get; set; }
        public double CreatedUt { get; set; }
        public double CompletedUt { get; set; }
        public double PhysicalBefore { get; set; }
        public double PhysicalAfter { get; set; }
        public string Provider { get; set; } = "";
        public string MembershipHash { get; set; } = "";
        public string AccessWitness { get; set; } = "";
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "Owned capacity/stock reserved; exact physical provider commit required.";
    }
    public sealed class ColonyPhysicalLot
    {
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public uint PartId { get; set; }
        public string Resource { get; set; } = "";
        public double LastPhysicalAmount { get; set; }
        public long ImportedAttribution { get; set; }
        public bool ProvenanceUncertain { get; set; }
        public bool HadImportedStock { get; set; }
        public string Witness { get; set; } = "";
    }
    public sealed class ColonyPhysicalPolicy
    {
        public string ColonyId { get; set; } = "";
        public bool Enabled { get; set; }
        public double NextReviewUt { get; set; }
        public double CadenceSeconds { get; set; } = 5;
        public long MaximumTransfer { get; set; } = 1000*ColonyLimits.Units;
        public double NativeInputTargetFraction { get; set; } = .5;
        public string Reason { get; set; } = "Local accessible warehouse procurement and native input buffer review.";
    }
}
