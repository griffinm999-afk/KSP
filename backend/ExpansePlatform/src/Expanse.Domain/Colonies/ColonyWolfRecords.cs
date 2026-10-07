using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    // WOLF points remain WOLF-owned capacity, never colony stock or physical EC.
    public sealed class ColonyWolfStream
    {
        public string Resource { get; set; } = "";
        public int Incoming { get; set; }
        public int Outgoing { get; set; }
    }
    public sealed class ColonyWolfDepot
    {
        public string Body { get; set; } = "";
        public string Biome { get; set; } = "";
        public bool Exists { get; set; }
        public bool Established { get; set; }
        public bool Surveyed { get; set; }
        public List<ColonyWolfStream> Streams { get; set; } = new List<ColonyWolfStream>();
    }
    public sealed class ColonyWolfIngredient
    {
        public string Resource { get; set; } = "";
        public int Points { get; set; }
    }
    public sealed class ColonyWolfRecipe
    {
        public string Id { get; set; } = "";
        public string PartName { get; set; } = "";
        public string ConfigurationHash { get; set; } = "";
        public string Tech { get; set; } = "";
        public long Funds { get; set; }
        public double LaborSeconds { get; set; } = 10800;
        public List<ColonyWolfIngredient> Inputs { get; set; } = new List<ColonyWolfIngredient>();
        public List<ColonyWolfIngredient> Outputs { get; set; } = new List<ColonyWolfIngredient>();
    }
    public sealed class ColonyWolfSiteObservation
    {
        public string ColonyId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public double ObservedUt { get; set; }
        public ColonyWolfDepot Depot { get; set; } = new ColonyWolfDepot();
        public bool HomeGround { get; set; }
        public uint SurveyPartId { get; set; }
        public string SurveyConfigurationHash { get; set; } = "";
        public List<ColonyWolfIngredient> SurveyVeins { get; set; } = new List<ColonyWolfIngredient>();
        public string Reason { get; set; } = "";
    }
    public sealed class ColonyWolfEnvironment
    {
        public bool Ready { get; set; }
        public string Provider { get; set; } = "";
        public string Reason { get; set; } = "WOLF provider unavailable";
        public ColonyWolfRecipe DepotConstruction { get; set; } = new ColonyWolfRecipe();
        public List<ColonyWolfRecipe> Recipes { get; set; } = new List<ColonyWolfRecipe>();
        public List<ColonyWolfSiteObservation> Sites { get; set; } = new List<ColonyWolfSiteObservation>();
    }
    public sealed class ColonyWolfModule
    {
        public ColonyWolfRecipe Recipe { get; set; } = new ColonyWolfRecipe();
        public int Count { get; set; }
    }
    public sealed class ColonyWolfQuote
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public long Revision { get; set; }
        public string Resource { get; set; } = "";
        public int DesiredAvailable { get; set; }
        public List<ColonyWolfIngredient> Demands { get; set; } = new List<ColonyWolfIngredient>();
        public long Funds { get; set; }
        public double LaborSeconds { get; set; }
        public bool EstablishDepot { get; set; }
        public bool SurveyDepot { get; set; }
        public bool HomeGround { get; set; }
        public uint SurveyPartId { get; set; }
        public string SurveyConfigurationHash { get; set; } = "";
        public ColonyWolfRecipe DepotConstruction { get; set; } = new ColonyWolfRecipe();
        public ColonyWolfDepot Before { get; set; } = new ColonyWolfDepot();
        public ColonyWolfDepot After { get; set; } = new ColonyWolfDepot();
        public List<ColonyWolfIngredient> SurveyVeins { get; set; } = new List<ColonyWolfIngredient>();
        public List<ColonyWolfModule> Modules { get; set; } = new List<ColonyWolfModule>();
        public string BalancePolicy { get; set; } = "Outsourced virtual WOLF module purchase at installed part cost; modeled sequential supplier lead time of 10,800 UT seconds per module; one shared supplier slot. Physical contents are not credited.";
    }
    public sealed class ColonyWolfOrder
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public ColonyWolfQuote Quote { get; set; } = new ColonyWolfQuote();
        public string State { get; set; } = "reserved";
        public string Reason { get; set; } = "Awaiting verified module purchase";
        public bool FundsPaid { get; set; }
        public double WorkCompleted { get; set; }
        public double AccountedUt { get; set; }
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public bool AllocationAttempted { get; set; }
        public ColonyWolfReplanQuote? Replan { get; set; }
    }
    public sealed class ColonyWolfReplanQuote
    {
        public string Id { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string PurchaseQuoteId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public long Revision { get; set; }
        public bool EstablishDepot { get; set; }
        public bool SurveyDepot { get; set; }
        public bool HomeGround { get; set; }
        public bool UnusedPurchasedDepot { get; set; }
        public uint SurveyPartId { get; set; }
        public string SurveyConfigurationHash { get; set; } = "";
        public List<ColonyWolfIngredient> SurveyVeins { get; set; } = new List<ColonyWolfIngredient>();
        public ColonyWolfDepot Before { get; set; } = new ColonyWolfDepot();
        public ColonyWolfDepot After { get; set; } = new ColonyWolfDepot();
        public string Reason { get; set; } = "Same paid module package; fresh current allocation only. No additional funds or manufacturing are granted.";
    }
}
