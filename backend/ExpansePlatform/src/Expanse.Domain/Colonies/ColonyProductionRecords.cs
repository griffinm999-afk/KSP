using System.Collections.Generic;
namespace Expanse.Domain.Colonies
{
    public sealed partial class ColonyFoundingIntent { public ColonyProductionIntent? Production {get;set;} }
    public sealed class ColonyProductionIntent
    {
        public string Mode {get;set;}="importOnly";
        public string RecipeId {get;set;}="";
        public int PackageCount {get;set;}=1;
        public double ReviewHorizonSeconds {get;set;}=6*ColonyLimits.KerbinDay;
        public ColonyProductionOperationsIntent? Operations {get;set;}
    }
    public sealed class ColonyProductionRate
    {
        public string Resource {get;set;}="";
        public double UnitsPerSecond {get;set;}
    }
    public sealed class ColonyProductionFeed
    {
        public uint CraftPartId {get;set;}
        public string PartName {get;set;}="";
        public string Resource {get;set;}="";
        public int OptionIndex {get;set;}
        public string OptionHash {get;set;}="";
        public int WolfPoints {get;set;}
        public double NominalUnitsPerSecond {get;set;}
    }
    public sealed class ColonyProductionRecipe
    {
        public string Id {get;set;}="";
        public string Name {get;set;}="";
        public string ConfigurationHash {get;set;}="";
        public string TemplateId {get;set;}="";
        public string TemplateHash {get;set;}="";
        public string HopperTemplateId {get;set;}="";
        public string HopperTemplateHash {get;set;}="";
        public uint CraftPartId {get;set;}
        public string PartName {get;set;}="";
        public string ModuleName {get;set;}="USI_Converter";
        public string OptionName {get;set;}="Cultivate(S)";
        public string OptionHash {get;set;}="";
        public int OptionIndex {get;set;}
        public string NativeExperienceEffect {get;set;}="BotanySkill";
        public bool Preconfigured {get;set;}
        public List<ColonyProductionRate> Inputs {get;set;}=new List<ColonyProductionRate>();
        public List<ColonyProductionRate> Outputs {get;set;}=new List<ColonyProductionRate>();
        public List<MaterialRequirement> RequiredInputs {get;set;}=new List<MaterialRequirement>();
        public List<ColonyProductionFeed> Feeds {get;set;}=new List<ColonyProductionFeed>();
        public string EstimateBasis {get;set;}="Installed nominal ratios before actual native efficiency; no measured output guarantee.";
    }
    public sealed class ColonyProductionEnvironment
    {
        public string Reason {get;set;}="Installed production catalog unavailable.";
        public List<ColonyProductionRecipe> Recipes {get;set;}=new List<ColonyProductionRecipe>();
        public List<ColonyProductionObservation> Observations {get;set;}=new List<ColonyProductionObservation>();
        public ColonyProductionRegistryAuthority Registry {get;set;}=new ColonyProductionRegistryAuthority();
    }
    public sealed class ColonyProductionObservation
    {
        public string PlanId {get;set;}="";
        public string InvestmentId {get;set;}="";
        public string ContextKey {get;set;}="";
        public string FacilityId {get;set;}="";
        public uint PartId {get;set;}
        public uint ModuleId {get;set;}
        public bool Active {get;set;}
        public bool Qualified {get;set;}
        public double ObservedUt {get;set;}
        public string RecipeHash {get;set;}="";
        public string Witness {get;set;}="";
        public string Reason {get;set;}="";
        public List<ColonyProductionRate> ObservedRecipeInputs {get;set;}=new List<ColonyProductionRate>();
        public List<ColonyProductionRate> ObservedRecipeOutputs {get;set;}=new List<ColonyProductionRate>();
        public double CurrentNativeEfficiency {get;set;}
        public List<ColonyProductionRate> NativeDeliveredInputs {get;set;}=new List<ColonyProductionRate>();
        public List<ColonyProductionRate> NativeDeliveredOutputs {get;set;}=new List<ColonyProductionRate>();
        public double NativeSampleSeconds {get;set;}
        public string NativeStatus {get;set;}="";
        public string Provider {get;set;}="";
        public List<ColonyProductionRate> PhysicalNetOutputs {get;set;}=new List<ColonyProductionRate>();
        public double PhysicalSampleSeconds {get;set;}
        public string NetAttribution {get;set;}="Measured net output tank change; no gross attribution or stock credit.";
    }
    public sealed class ColonyProductionInvestment
    {
        public string Id {get;set;}="";
        public ColonyProductionRecipe Recipe {get;set;}=new ColonyProductionRecipe();
        public string BuildingId {get;set;}="";
        public string HopperBuildingId {get;set;}="";
        public ColonyWolfQuote Wolf {get;set;}=new ColonyWolfQuote();
        public double ReviewHorizonSeconds {get;set;}
        public double NominalSuppliesPerDay {get;set;}
        public string Downside {get;set;}="Zero physical output: full finite Supplies imports remain funded; inputs, upkeep and paid construction are additional costs.";
        public ColonyProductionInventoryQuote? Inventory {get;set;}
    }
    public sealed class ColonyProductionClaim
    {
        public string Id {get;set;}="";
        public string State {get;set;}="planned";
        public string WolfOrderId {get;set;}="";
        public string Reason {get;set;}="Awaiting reviewed construction and dependency purchase.";
        public string OutputWitness {get;set;}="";
        public double OutputObservedUt {get;set;}
        public double OutputIntervalSeconds {get;set;}
        public double OutputSuppliesUnits {get;set;}
        public string OutputContextKey {get;set;}="";
        public uint OutputPartId {get;set;}
        public uint OutputModuleId {get;set;}
        public List<ColonyProductionStep> Steps {get;set;}=new List<ColonyProductionStep>();
        public ColonyProductionInventoryClaim? Inventory {get;set;}
    }
    public sealed class ColonyProductionStep
    {
        public string Id {get;set;}="";
        public string Kind {get;set;}=""; // hopperConnect / converterStart
        public string FacilityId {get;set;}="";
        public string VesselId {get;set;}="";
        public uint PartId {get;set;}
        public uint ModuleId {get;set;}
        public string OptionHash {get;set;}="";
        public string State {get;set;}="prepared";
        public string HopperId {get;set;}="";
        public string BeforeWitness {get;set;}="";
        public string AfterWitness {get;set;}="";
        public string Reason {get;set;}="";
    }
}

