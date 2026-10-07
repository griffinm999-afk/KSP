using System;
using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    // Save-owned records. Observations, physical resources and WOLF capacity are never
    // inserted in Stock: a provider must witness a debit before virtual stock is credited.
    public sealed class ColonyState
    {
        public int SchemaVersion { get; set; } = 1;
        public string WorldId { get; set; } = "";
        public long Revision { get; set; }
        public long NextSequence { get; set; } = 1;
        public long CompactedJournalCount { get; set; }
        public string CompactedJournalHash { get; set; } = "";
        public double SimulatedUt { get; set; }
        public double TargetUt { get; set; }
        public List<ColonyRecord> Colonies { get; set; } = new List<ColonyRecord>();
        public List<ConstructionOrder> Construction { get; set; } = new List<ConstructionOrder>();
        public List<ColonyShipment> Shipments { get; set; } = new List<ColonyShipment>();
        public List<ColonyEffect> Effects { get; set; } = new List<ColonyEffect>();
        public List<ColonyReceipt> Receipts { get; set; } = new List<ColonyReceipt>();
        public List<ColonyJournalEntry> Journal { get; set; } = new List<ColonyJournalEntry>();
        public List<ColonySupplier> Suppliers { get; set; } = new List<ColonySupplier>();
        public List<ColonyPeopleOperation> PeopleOperations { get; set; } = new List<ColonyPeopleOperation>();
        public List<ColonyWolfOrder> WolfOrders { get; set; } = new List<ColonyWolfOrder>();
        public List<ColonyServiceOperation> ServiceOperations { get; set; } = new List<ColonyServiceOperation>();
        public List<ColonyServicePolicy> ServicePolicies { get; set; } = new List<ColonyServicePolicy>();
        public List<ColonyPlan> Plans { get; set; } = new List<ColonyPlan>();
        public List<ColonyReorderPolicy> ReorderPolicies { get; set; } = new List<ColonyReorderPolicy>();
        public List<ColonyPhysicalTransfer> PhysicalTransfers { get; set; } = new List<ColonyPhysicalTransfer>();
        public List<ColonyPhysicalLot> PhysicalLots { get; set; } = new List<ColonyPhysicalLot>();
        public List<ColonyPhysicalPolicy> PhysicalPolicies { get; set; } = new List<ColonyPhysicalPolicy>();
    }

    public sealed class ColonyRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public ColonySite Site { get; set; } = new ColonySite();
        public ColonyCharter Charter { get; set; } = new ColonyCharter();
        public string Status { get; set; } = "founding";
        public double FoundedUt { get; set; }
        public double? SupportCommissionedUt { get; set; }
        public double SupportAccountedUt { get; set; }
        public string SupportPolicy { get; set; } = "Expanse resident support";
        public string SupportPolicyHash { get; set; } = "";
        public string SupportStatus { get; set; } = "Not commissioned";
        public long SpentFunds { get; set; }
        public long SupportMicroUnitsPerPersonDay { get; set; } = 1_000_000;
        public decimal SupportRemainder { get; set; }
        public long SupportConsumedMicroUnits { get; set; }
        public long SupportPendingJournalMicroUnits { get; set; }
        public List<ColonyFacility> Facilities { get; set; } = new List<ColonyFacility>();
        public List<ColonyPlot> Plots { get; set; } = new List<ColonyPlot>();
        public List<ColonyStock> Stock { get; set; } = new List<ColonyStock>();
        public List<ColonyResident> Residents { get; set; } = new List<ColonyResident>();
        public List<string> VisitorRosterIds { get; set; } = new List<string>();
        public List<ColonyProposal> Proposals { get; set; } = new List<ColonyProposal>();
        public ColonyLogisticsContract Logistics { get; set; } = new ColonyLogisticsContract();
    }

    public sealed class ColonySite
    {
        public string Body { get; set; } = "";
        public string Biome { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double RadiusMeters { get; set; } = 400;
    }

    public sealed class ColonyCharter
    {
        public string Purpose { get; set; } = "Settlement";
        public int PopulationTarget { get; set; } = 12;
        public int ResidentLimit { get; set; } = 24;
        public int VisitorLimit { get; set; } = 24;
        public long FoundingBudget { get; set; } = 1_000_000;
        public long CashFloor { get; set; } = 100_000;
        public long SpendingLimit { get; set; } = 1_000_000;
        public double ReserveDays { get; set; } = 6;
        public string GrowthPolicy { get; set; } = "approval";
        public bool Sandbox { get; set; }
    }

    public sealed class ColonyFacility
    {
        public string Id { get; set; } = "";
        public string VesselId { get; set; } = "";
        public string Name { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string TemplateHash { get; set; } = "";
        public string PlotId { get; set; } = "";
        public string State { get; set; } = "adopted";
        public string ProductionOwner { get; set; } = "physical";
        public int CertifiedHomes { get; set; }
        public int RequiredWorkers { get; set; }
        public string RequiredTrait { get; set; } = "";
        public string LastReason { get; set; } = "Awaiting qualification";
        public List<uint> PartIds { get; set; } = new List<uint>();
        public string ConstructionOrderId { get; set; } = "";
        public string PlacementOperationId { get; set; } = "";
        public string PlacementRequestFingerprint { get; set; } = "";
        public string CraftSha256 { get; set; } = "";
        public string CertificationId { get; set; } = "";
        public string FoundationId { get; set; } = "";
        public string PlacementWitnessHash { get; set; } = "";
        public bool DevelopmentOnly { get; set; }
        public List<uint> HomePartPersistentIds { get; set; } = new List<uint>();
        public string HomePartCertificationHash { get; set; } = "";
        public ColonyQualification Qualification { get; set; } = new ColonyQualification();
    }

    public sealed class ColonyQualification
    {
        public string Provider { get; set; } = "";
        public string Context { get; set; } = "unknown";
        public double ObservedUt { get; set; }
        public string EvidenceHash { get; set; } = "";
        public bool PlacementStable { get; set; }
        public bool PowerReliable { get; set; }
        public bool HeatSafe { get; set; }
        public bool InputsAccessible { get; set; }
        public bool StaffingQualified { get; set; }
        public bool BackgroundSupported { get; set; }
        public bool HousingCertified { get; set; }
        public ColonyReactorContinuationProof? ReactorContinuation { get; set; }
    }

    public sealed class ColonyPlot
    {
        public string Id { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Heading { get; set; }
        public double WidthMeters { get; set; }
        public double LengthMeters { get; set; }
        public string SurveyHash { get; set; } = "";
        public string ReservedBy { get; set; } = "";
        public string OccupiedBy { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string TemplateHash { get; set; } = "";
        public string EvidenceContext { get; set; } = "";
        public double ObservedUt { get; set; }
        public string SurveyProvenance { get; set; } = "";
    }

    public sealed class ColonyStock
    {
        public string Resource { get; set; } = "";
        public long Amount { get; set; } // millionths of one resource unit
        public long Capacity { get; set; }
        public long Reserved { get; set; }
        public long IncomingReserved { get; set; }
        public long SupportFloor { get; set; }
        public long ImportedAmount { get; set; }
        public long UnitMassMicroTonnes { get; set; }
        public long UnitVolumeMilliLiters { get; set; }
    }

    public sealed class ColonyResident
    {
        public string Id { get; set; } = "";
        public string RosterId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Trait { get; set; } = "";
        public string HomeFacilityId { get; set; } = "";
        public string JobFacilityId { get; set; } = "";
        public uint WorkPartId { get; set; }
        public string Status { get; set; } = "resident";
        public bool PhysicallyAtWork { get; set; }
        public uint HomePartId { get; set; }
        public string ArrivalOperationId { get; set; } = "";
        public string LastReason { get; set; } = "";
    }

    public sealed class MaterialRequirement
    {
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
    }

    public sealed class ColonyTemplate
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Version { get; set; } = 1;
        public string Hash { get; set; } = "";
        public string CraftRelativePath { get; set; } = "";
        public long BuildFunds { get; set; }
        public long LaborFunds { get; set; }
        public double LaborSeconds { get; set; }
        public double WidthMeters { get; set; }
        public double LengthMeters { get; set; }
        public int Homes { get; set; }
        public int Workers { get; set; }
        public string WorkerTrait { get; set; } = "";
        public List<MaterialRequirement> Materials { get; set; } = new List<MaterialRequirement>();
        public List<MaterialRequirement> EmbeddedContents { get; set; } = new List<MaterialRequirement>();
        public List<string> RequiredTech { get; set; } = new List<string>();
        public List<string> RequiredPartNames { get; set; } = new List<string>();
        public bool RuntimeCertified { get; set; }
        public string CertificationEvidence { get; set; } = "";
        // Hash binds the reviewed manifest/quote; CraftSha256 binds installed bytes.
        public string CraftSha256 { get; set; } = "";
        public string PartConfigurationHash { get; set; } = "";
        public string CertificationId { get; set; } = "";
        public int ExpectedPartCount { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
        public double MaximumHeight { get; set; }
        public double TemplateRotationX { get; set; }
        public double TemplateRotationY { get; set; }
        public double TemplateRotationZ { get; set; }
        public double TemplateRotationW { get; set; } = 1;
        public double MaximumSlopeDegrees { get; set; } = 3;
        public double MaximumSupportGapMetres { get; set; } = 0.25;
        public double ClearanceMetres { get; set; } = 1;
        public List<ColonyStartupContent> StartupContents { get; set; } = new List<ColonyStartupContent>();
        public List<uint> HomeCraftPartIds { get; set; } = new List<uint>();
    }

    public sealed class ColonyStartupContent
    {
        public uint CraftPartId { get; set; }
        public string ResourceName { get; set; } = "";
        public long Amount { get; set; } // millionths; adapter receives Amount / 1,000,000d
    }

    public sealed class ConstructionOrder
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string PlotId { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string TemplateHash { get; set; } = "";
        public string State { get; set; } = "reserved";
        public string Reason { get; set; } = "Awaiting funds debit";
        public long Funds { get; set; }
        public bool FundsPaid { get; set; }
        public long FundsConsumed { get; set; }
        public bool MaterialsConsumed { get; set; }
        public double WorkRequired { get; set; }
        public double WorkCompleted { get; set; }
        public double AccountedUt { get; set; }
        public string FacilityId { get; set; } = "";
        public List<MaterialRequirement> Materials { get; set; } = new List<MaterialRequirement>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public long LaborFunds { get; set; }
        public string LaborProviderId { get; set; } = "";
        public string LaborEvidenceHash { get; set; } = "";
        public int LaborWorkers { get; set; }
        public ColonyConstructionPlacementIntent Placement { get; set; } = new ColonyConstructionPlacementIntent();
    }

    public sealed class ColonyShipment
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string SupplierId { get; set; } = "";
        public string Kind { get; set; } = "import";
        public string State { get; set; } = "reserved";
        public string Reason { get; set; } = "Awaiting funds debit";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
        public long Funds { get; set; }
        public double DepartUt { get; set; }
        public double ArrivalUt { get; set; }
        public double TravelSeconds { get; set; }
        public long MassMicroTonnes { get; set; }
        public long VolumeMilliLiters { get; set; }
    }

    public sealed class ColonySupplier
    {
        public string Id { get; set; } = "";
        public string FreightPoolId { get; set; } = "";
        public string DestinationBody { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Available { get; set; }
        public long Reserved { get; set; }
        public long FundsPerUnit { get; set; }
        public long FreightFunds { get; set; }
        public long MassCapacityMicroTonnes { get; set; }
        public long VolumeCapacityMilliLiters { get; set; }
        public int ConcurrentCapacity { get; set; }
        public double TravelSeconds { get; set; }
    }

    public sealed class ColonyEffect
    {
        public string Id { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string State { get; set; } = "prepared";
        public long FundsDelta { get; set; }
        public string Provider { get; set; } = "";
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class ColonyReceipt
    {
        public string OperationId { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public long Sequence { get; set; }
        public long Revision { get; set; }
        public string ResultId { get; set; } = "";
        public string Outcome { get; set; } = "accepted";
    }

    public sealed class ColonyJournalEntry
    {
        public long Sequence { get; set; }
        public double Ut { get; set; }
        public string ColonyId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Detail { get; set; } = "";
        public long FundsDelta { get; set; }
        public string Resource { get; set; } = "";
        public long ResourceDelta { get; set; }
    }

    public sealed class ColonyProposal
    {
        public string Id { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string PlotId { get; set; } = "";
        public string State { get; set; } = "proposed";
        public string Reason { get; set; } = "";
        public double CreatedUt { get; set; }
        public double DownsideCashDays { get; set; }
        public string DecisionReason { get; set; } = "";
        public double DeferredUntilUt { get; set; }
        public string PlanId { get; set; } = "";
    }

    public sealed class ColonyCommand
    {
        public string OperationId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public long ExpectedRevision { get; set; }
        public string Kind { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string QuoteId { get; set; } = "";
        public ColonyFoundingIntent? FoundingIntent { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }
}
