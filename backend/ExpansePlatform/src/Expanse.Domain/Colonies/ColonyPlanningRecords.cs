using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    // Roles are explicit configured package identities. An observed producer is
    // never credited as owned stock or assumed to satisfy an unrelated role.
    public sealed class ColonyPlanningEnvironment
    {
        public List<ColonyPlanningRole> Roles { get; set; } = new List<ColonyPlanningRole>();
        public List<ColonyPlanningAsset> Assets { get; set; } = new List<ColonyPlanningAsset>();
        public List<ColonyPlot> SurveyedPlots { get; set; } = new List<ColonyPlot>();
        public List<ColonyLocalStock> LocalStocks { get; set; } = new List<ColonyLocalStock>();
        public double CadenceSeconds { get; set; } = 21600;
        public double ImportDelayFactor { get; set; } = 2;
        public string SurveyFailure { get; set; } = "";
    }
    public sealed class ColonyPlanningRole
    {
        public string Role { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public int MinimumCount { get; set; } = 1;
    }
    public sealed class ColonyPlanningAsset
    {
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public string Role { get; set; } = "";
        public string Name { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public string Witness { get; set; } = "";
        // Stable registered physical membership/provider identity, distinct from
        // continuously refreshed utility/temperature/timestamp evidence.
        public string IdentityHash { get; set; } = "";
        public int Homes { get; set; }
        public bool Qualified { get; set; }
    }
    public sealed class ColonyPlanningQuote
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "founding";
        public string ColonyId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public long Revision { get; set; }
        public string AuthorityHash { get; set; } = "";
        public string SupportPolicyHash { get; set; } = "";
        public bool CanApprove { get; set; }
        public List<string> Blockers { get; set; } = new List<string>();
        public List<string> ExistingAssets { get; set; } = new List<string>();
        public List<ColonyPlanningBuilding> Buildings { get; set; } = new List<ColonyPlanningBuilding>();
        public List<ColonyPlanningWorker> BootstrapWorkers { get; set; } = new List<ColonyPlanningWorker>();
        public ColonyFoundingIntent? FoundingIntent { get; set; }
        public List<ColonyPlanningResident> Residents { get; set; } = new List<ColonyPlanningResident>();
        public ColonyStartupPolicies? StartupPolicies { get; set; }
        public List<ColonyProductionInvestment> ProductionInvestments { get; set; } = new List<ColonyProductionInvestment>();
        public List<ColonyMakeImportComparison> MakeImportComparisons { get; set; } = new List<ColonyMakeImportComparison>();
        public List<ColonyPlanningImport> Imports { get; set; } = new List<ColonyPlanningImport>();
        public List<MaterialRequirement> Materials { get; set; } = new List<MaterialRequirement>();
        public List<ColonyPlanningIncomingClaim> ExistingIncoming { get; set; } = new List<ColonyPlanningIncomingClaim>();
        public string StagingPolicyId { get; set; } = "";
        public string StagingPolicyHash { get; set; } = "";
        public long StagingFunds { get; set; }
        public long StartupSupportReserve { get; set; }
        public int TargetPopulation { get; set; }
        public long TotalFunds { get; set; }
        public double LaborSeconds { get; set; }
        public string Rationale { get; set; } = "";
        public string MakeOrImport { get; set; } = "Owned stock first; finite quoted imports supply the remaining shortage. Native MKS/WOLF output is excluded until a witnessed stock transfer occurs.";
        public ColonyForecast Forecast { get; set; } = new ColonyForecast();
    }
    public sealed class ColonyPlanningBuilding
    {
        public string Id { get; set; } = ""; // Immutable line identifier within quote.
        public string Role { get; set; } = "";
        public string TemplateId { get; set; } = "";
        public string TemplateHash { get; set; } = "";
        public string Name { get; set; } = "";
        public string PlotId { get; set; } = "";
        public int Homes { get; set; }
        public long Funds { get; set; }
        public double LaborSeconds { get; set; }
        public List<MaterialRequirement> Materials { get; set; } = new List<MaterialRequirement>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public string OrderId { get; set; } = "";
    }
    public sealed class ColonyPlanningImport
    {
        public string Id { get; set; } = "";
        public string SupplierId { get; set; } = "";
        public string SupplierTermsHash { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
        public long FundsPerUnit { get; set; }
        public long FreightFunds { get; set; }
        public long Funds { get; set; }
        public double TravelSeconds { get; set; }
        public string ShipmentId { get; set; } = "";
        // Progress only: the immutable reviewed load is a maximum purchase.
        public long SubstitutedLocallyAmount { get; set; }
    }
    public sealed class ColonyPlanningIncomingClaim
    {
        public string ShipmentId { get; set; } = "";
        public string Resource { get; set; } = "";
        public long Amount { get; set; }
        public long MaterialAmount { get; set; }
        public bool Credited { get; set; }
    }
    public sealed class ColonyPlanningMaterialClaim
    {
        public string Resource { get; set; } = "";
        public long Remaining { get; set; }
        public long Reserved { get; set; }
    }
    public sealed class ColonyPlan
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string State { get; set; } = "approved";
        public string Reason { get; set; } = "Reviewed commitments reserved; awaiting bounded procurement.";
        public double CreatedUt { get; set; }
        public long RemainingFunds { get; set; }
        public bool StagingFundsTransferred { get; set; }
        public long PreviousSupportFloor { get; set; }
        public ColonyPlanningQuote Quote { get; set; } = new ColonyPlanningQuote();
        public List<ColonyPlanningBuilding> Buildings { get; set; } = new List<ColonyPlanningBuilding>();
        public List<ColonyPlanningImport> Imports { get; set; } = new List<ColonyPlanningImport>();
        public List<ColonyPlanningMaterialClaim> Claims { get; set; } = new List<ColonyPlanningMaterialClaim>();
        public List<ColonyPlanningIncomingClaim> Incoming { get; set; } = new List<ColonyPlanningIncomingClaim>();
        public List<ColonyPlanningWorkerClaim> Workers { get; set; } = new List<ColonyPlanningWorkerClaim>();
        public List<ColonyPlanningResidentClaim> Residents { get; set; } = new List<ColonyPlanningResidentClaim>();
        public bool StartupReservesReleased { get; set; }
        public bool StartupPoliciesApplied { get; set; }
        public List<ColonyProductionClaim> Production { get; set; } = new List<ColonyProductionClaim>();
    }
    public sealed class ColonyReorderPolicy
    {
        public string ColonyId { get; set; } = "";
        public string Resource { get; set; } = "";
        public bool Enabled { get; set; }
        public long ReorderPoint { get; set; }
        public long TargetAmount { get; set; }
        public double CadenceSeconds { get; set; } = 21600;
        public double NextReviewUt { get; set; }
        public string Reason { get; set; } = "Configured; awaiting review cadence.";
    }
    public sealed class ColonyForecast
    {
        public long Cash { get; set; }
        public long CommittedCash { get; set; }
        public long AvailableCash { get; set; }
        public long Receivables { get; set; }
        public long CashFloor { get; set; }
        public long DownsideReplacementCost { get; set; }
        public long DownsideCash { get; set; }
        public int SupportedPeople { get; set; }
        public int QualifiedHomes { get; set; }
        public int OpenQualifiedJobs { get; set; }
        public double HorizonSeconds { get; set; }
        public double CurrentSupplyDays { get; set; }
        public double DownsideSupplyDays { get; set; }
        public bool Sustainable { get; set; }
        public List<string> Shortages { get; set; } = new List<string>();
        public string Assumptions { get; set; } = "Zero export receipts; imports delayed by the configured factor; no new MKS/WOLF outputs; no invented residents or currency.";
    }
}
