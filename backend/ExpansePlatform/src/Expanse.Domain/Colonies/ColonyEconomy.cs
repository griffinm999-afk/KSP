using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyLogisticsContract
    {
        public string OperationId { get; set; } = "";
        public string State { get; set; } = "none";
        public string PolicyHash { get; set; } = "";
        public string Provider { get; set; } = "";
        public long Funds { get; set; }
        public bool FundsPaid { get; set; }
        public double ActivationUt { get; set; }
        public double TravelSeconds { get; set; }
        public int ContractorWorkers { get; set; }
        public string Reason { get; set; } = "No staging contract approved.";
        public List<ColonyStock> Stores { get; set; } = new List<ColonyStock>();
    }

    public sealed class ColonyEconomyPolicy
    {
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Body { get; set; } = "";
        public string Provider { get; set; } = "";
        public long SetupFunds { get; set; }
        public double SetupTravelSeconds { get; set; }
        public int ContractorWorkers { get; set; }
        public List<ColonyStock> Stores { get; set; } = new List<ColonyStock>();
        public List<ColonySupplier> Suppliers { get; set; } = new List<ColonySupplier>();
        public string Reason { get; set; } = "";
    }

    public static partial class ColonyEngine
    {
        // The contract buys finite empty staging capacity and a bounded contractor
        // service. It creates no physical tank contents, WOLF supply or local crew.
        static string ActivateLogistics(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId);
            var policy = env.EconomyPolicies.SingleOrDefault(x => x.Body == colony.Site.Body && x.Id == Field(command, "PolicyId"));
            if (policy == null) throw new InvalidDataException("No configured logistics supplier serves this body.");
            ColonyStateCodec.ValidateEconomyPolicy(policy);
            if (Field(command, "PolicyHash") != policy.Hash || Integer(command, "QuotedFunds") != policy.SetupFunds) throw new InvalidDataException("Logistics terms changed. Review a fresh quote.");
            if (colony.Logistics.State != "none") throw new InvalidDataException("This colony already has a staging contract.");
            if (colony.Stock.Count != 0) throw new InvalidDataException("Existing inventory needs a separate capacity migration; it cannot be replaced.");
            // Fleet capacity is global to the saved pool, not a fresh allowance
            // supplied by each new body route or updated configuration.
            foreach (var supplier in policy.Suppliers)
            {
                if (state.Suppliers.Any(x => x.FreightPoolId == supplier.FreightPoolId && x.ConcurrentCapacity != supplier.ConcurrentCapacity))
                    throw new InvalidDataException("Shared freight pool capacity differs from the saved contract; fleet expansion requires a separate purchase.");
                var existing = state.Suppliers.SingleOrDefault(x => x.Id == supplier.Id);
                if (existing != null && (existing.Resource != supplier.Resource || existing.DestinationBody != supplier.DestinationBody || existing.FreightPoolId != supplier.FreightPoolId))
                    throw new InvalidDataException("Supplier identity conflicts with its saved resource, destination or freight pool.");
            }
            CheckFunds(state, colony, policy.SetupFunds, env);
            colony.Logistics = new ColonyLogisticsContract { OperationId = command.OperationId, State = "reserved", PolicyHash = policy.Hash, Provider = policy.Provider,
                Funds = policy.SetupFunds, TravelSeconds = policy.SetupTravelSeconds, ContractorWorkers = policy.ContractorWorkers, Stores = policy.Stores,
                Reason = "Awaiting verified startup payment; empty staging capacity is not available yet." };
            foreach (var supplier in policy.Suppliers)
            {
                // Global supplier stock belongs to the selected save and is never
                // replenished by approving a second colony or reloading the catalog.
                if (!state.Suppliers.Any(x => x.Id == supplier.Id)) state.Suppliers.Add(supplier);
            }
            state.Effects.Add(FundsEffect(command, command.OperationId, -policy.SetupFunds, "logisticsSetup"));
            return command.OperationId;
        }

        static bool CompleteEconomyFundsEffect(ColonyState state, ColonyEffect effect, double ut)
        {
            if (effect.Kind != "logisticsSetup") return false;
            var colony = Colony(state, effect.ColonyId); var contract = colony.Logistics;
            if (contract.OperationId != effect.TargetId || contract.State != "reserved" || effect.FundsDelta != -contract.Funds) throw new InvalidDataException("Staging payment does not match its reservation.");
            contract.FundsPaid = true; contract.State = "delivering"; contract.ActivationUt = ut + contract.TravelSeconds;
            contract.Reason = "Paid staging equipment and contracted support are in transit.";
            colony.SpentFunds = checked(colony.SpentFunds + contract.Funds);
            return true;
        }

        static double NextEconomyEvent(ColonyState state, double next)
        {
            foreach (var colony in state.Colonies.Where(x => x.Logistics.State == "delivering"))
                next = Math.Min(next, Math.Max(state.SimulatedUt, colony.Logistics.ActivationUt));
            return next;
        }

        static void AdvanceEconomy(ColonyState state, ColonyEnvironment env, double ut)
        {
            foreach (var colony in state.Colonies.Where(x => x.Logistics.State == "delivering" && x.Logistics.ActivationUt <= ut))
            {
                if (!colony.Logistics.FundsPaid || colony.Stock.Count != 0) throw new InvalidDataException("Staging arrival lacks a paid empty destination.");
                colony.Stock = colony.Logistics.Stores;
                colony.Logistics.Stores = new List<ColonyStock>();
                colony.Logistics.State = "operational";
                colony.Logistics.Reason = "Colony-owned staging stores. Physical tanks and WOLF capacity retain their own authority.";
                Log(state, ut, colony.Id, colony.Logistics.OperationId, "stagingArrival", colony.Logistics.Reason);
            }
        }

        static string RegisterSurveyedPlot(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId);
            var plot = env.SurveyedPlot ?? throw new InvalidDataException(env.SurveyFailure.Length == 0 ? "Current loaded-terrain survey is unavailable." : env.SurveyFailure);
            if (plot.Id != command.TargetId || plot.EvidenceContext != env.ContextKey || plot.ObservedUt > env.Ut || env.Ut - plot.ObservedUt > 10 || plot.SurveyHash.Length == 0)
                throw new InvalidDataException("Plot evidence does not match this current operation.");
            if (plot.TemplateId != Field(command, "TemplateId") || plot.TemplateHash != Field(command, "TemplateHash") ||
                plot.Latitude != Number(command, "Latitude") || plot.Longitude != Number(command, "Longitude") || plot.Heading != Number(command, "Heading"))
                throw new InvalidDataException("Plot evidence differs from the requested package or coordinates.");
            if (!env.BodyRadiiMeters.TryGetValue(colony.Site.Body, out var radius) || SurfaceDistance(colony.Site, new ColonySite { Latitude = plot.Latitude, Longitude = plot.Longitude }, radius) > colony.Site.RadiusMeters)
                throw new InvalidDataException("Plot lies outside the registered colony boundary.");
            var existing = colony.Plots.SingleOrDefault(x => x.Id == plot.Id);
            if (existing != null)
            {
                if (existing.ReservedBy.Length != 0 || existing.OccupiedBy.Length != 0) throw new InvalidDataException("Committed plots cannot be moved or reassigned.");
                colony.Plots.Remove(existing);
            }
            if (plot.ReservedBy.Length != 0 || plot.OccupiedBy.Length != 0 || state.Colonies.SelectMany(x => x.Plots).Any(x => x.Id == plot.Id)) throw new InvalidDataException("Plot identity is already owned.");
            colony.Plots.Add(plot);
            Log(state, env.Ut, colony.Id, command.OperationId, "plotSurvey", "Loaded terrain preview recorded; physical placement rechecks final clearance.");
            return plot.Id;
        }

        public static string EconomyPolicyHash(ColonyEconomyPolicy policy)
        {
            // Hash the complete immutable quote; the hash field is excluded.
            string prior = policy.Hash; policy.Hash = "";
            try { return ColonyStateCodec.Hash(ColonyJson.Serialize(policy, 512 * 1024)); }
            finally { policy.Hash = prior; }
        }
    }

    public static partial class ColonyStateCodec
    {
        static void MigrateEconomy(ColonyState state)
        {
            if (state.Suppliers != null) foreach (var supplier in state.Suppliers)
            { supplier.FreightPoolId = supplier.FreightPoolId ?? ""; supplier.DestinationBody = supplier.DestinationBody ?? ""; }
            if (state.Colonies != null) foreach (var colony in state.Colonies)
            {
                if (colony.Logistics == null) colony.Logistics = new ColonyLogisticsContract();
                if (colony.Plots != null) foreach (var plot in colony.Plots)
                {
                    plot.TemplateId = plot.TemplateId ?? ""; plot.TemplateHash = plot.TemplateHash ?? "";
                    plot.EvidenceContext = plot.EvidenceContext ?? ""; plot.SurveyProvenance = plot.SurveyProvenance ?? "";
                }
            }
        }

        public static void ValidateEconomyPolicy(ColonyEconomyPolicy policy)
        {
            Text(policy.Id, 128, true); Text(policy.Hash, 128, true); Text(policy.Body, 128, true); Text(policy.Provider, 128, true);
            Funds(policy.SetupFunds); Range(policy.SetupTravelSeconds, 1, 1e9); Count(policy.ContractorWorkers, 32); Text(policy.Reason, 512);
            ValidateEmptyStores(policy.Stores);
            Rows(policy.Suppliers, ColonyLimits.Suppliers); Unique(policy.Suppliers.Select(x => x.Id));
            foreach (var supplier in policy.Suppliers)
            {
                Text(supplier.Id, 128, true); Text(supplier.Resource, 128, true); Quantity(supplier.Available); Quantity(supplier.Reserved);
                Text(supplier.FreightPoolId, 128, true); Text(supplier.DestinationBody, 128, true);
                Funds(supplier.FundsPerUnit); Funds(supplier.FreightFunds); Quantity(supplier.MassCapacityMicroTonnes); Quantity(supplier.VolumeCapacityMilliLiters);
                Range(supplier.TravelSeconds, 1, 1e9); Count(supplier.ConcurrentCapacity, 32);
                if (supplier.Reserved != 0 || supplier.Available == 0 || supplier.ConcurrentCapacity == 0 || !policy.Stores.Any(x => x.Resource == supplier.Resource)) Fail("Invalid finite supplier terms.");
            }
            foreach (var pool in policy.Suppliers.GroupBy(x => x.FreightPoolId))
                if (pool.Select(x => x.ConcurrentCapacity).Distinct().Count() != 1) Fail("Shared freight pool concurrency differs across resources.");
            if (ColonyEngine.EconomyPolicyHash(policy) != policy.Hash) Fail("Economy policy hash differs from its complete terms.");
        }

        static void ValidateEmptyStores(List<ColonyStock> stores)
        {
            Rows(stores, ColonyLimits.Resources); Unique(stores.Select(x => x.Resource));
            foreach (var store in stores)
            {
                Text(store.Resource, 128, true); Quantity(store.Capacity); Quantity(store.UnitMassMicroTonnes); Quantity(store.UnitVolumeMilliLiters);
                if (store.Capacity == 0 || store.Amount != 0 || store.Reserved != 0 || store.IncomingReserved != 0 || store.ImportedAmount != 0 || store.SupportFloor != 0) Fail("Staging contract may create empty capacity only.");
            }
        }

        static void ValidateEconomy(ColonyState state)
        {
            foreach (var pool in state.Suppliers.Where(x => x.FreightPoolId.Length != 0).GroupBy(x => x.FreightPoolId))
                if (pool.Select(x => x.ConcurrentCapacity).Distinct().Count() != 1) Fail("Saved shared freight pool has conflicting fleet capacity.");
            foreach (var colony in state.Colonies)
            {
                var contract = colony.Logistics ?? throw new InvalidDataException("Missing logistics contract state.");
                Choice(contract.State, "none", "reserved", "delivering", "operational", "held"); Text(contract.OperationId, 64); Text(contract.PolicyHash, 128); Text(contract.Provider, 128); Text(contract.Reason, 512);
                Funds(contract.Funds); Time(contract.ActivationUt); Time(contract.TravelSeconds); Count(contract.ContractorWorkers, 32); ValidateEmptyStores(contract.Stores);
                if (contract.State == "none") { if (contract.FundsPaid || contract.OperationId.Length != 0 || contract.Stores.Count != 0) Fail("Inactive staging contract has side effects."); }
                else
                {
                    Id(contract.OperationId); Text(contract.PolicyHash, 128, true); Text(contract.Provider, 128, true);
                    if (contract.TravelSeconds <= 0 || (contract.State == "delivering" || contract.State == "operational") && !contract.FundsPaid) Fail("Staging capacity lacks paid commissioning.");
                    if (contract.State == "operational" && contract.Stores.Count != 0) Fail("Staging capacity was granted twice.");
                }
            }
        }
    }
}
