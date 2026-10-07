using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        static void MigrateWolf(ColonyState state) { if (state.WolfOrders == null) state.WolfOrders = new List<ColonyWolfOrder>(); }
        public static string WolfDepotHash(ColonyWolfDepot depot)
        {
            ValidateWolfDepot(depot);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                WriteWolfDepot(writer, depot); writer.Flush(); return Hash(stream.ToArray());
            }
        }
        public static string WolfQuoteHash(ColonyWolfQuote quote)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(quote.ColonyId); writer.Write(quote.ContextKey); writer.Write(quote.Revision);
                writer.Write(quote.Resource); writer.Write(quote.DesiredAvailable); writer.Write(quote.Funds); writer.Write(quote.LaborSeconds);
                writer.Write(quote.EstablishDepot); writer.Write(quote.SurveyDepot); writer.Write(quote.HomeGround); writer.Write(quote.SurveyPartId); writer.Write(quote.SurveyConfigurationHash);
                WriteWolfRecipe(writer, quote.DepotConstruction); WriteWolfDepot(writer, quote.Before); WriteWolfDepot(writer, quote.After);
                WriteWolfIngredients(writer, quote.SurveyVeins); writer.Write(quote.Modules.Count);
                foreach (var module in quote.Modules) { writer.Write(module.Count); WriteWolfRecipe(writer, module.Recipe); }
                writer.Write(quote.BalancePolicy);
                // Empty additive demands retain every preexisting scalar quote's
                // exact binary hash. Nonempty vector terms bind all requirements.
                if(quote.Demands!=null && quote.Demands.Count>0){writer.Write("compound-demands/v1");WriteWolfIngredients(writer,quote.Demands);}
                writer.Flush(); return Hash(stream.ToArray());
            }
        }
        public static string WolfReplanHash(ColonyWolfReplanQuote quote)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(quote.OrderId); writer.Write(quote.PurchaseQuoteId); writer.Write(quote.ContextKey); writer.Write(quote.Revision);
                writer.Write(quote.EstablishDepot); writer.Write(quote.SurveyDepot); writer.Write(quote.HomeGround); writer.Write(quote.UnusedPurchasedDepot);
                writer.Write(quote.SurveyPartId); writer.Write(quote.SurveyConfigurationHash); WriteWolfIngredients(writer, quote.SurveyVeins);
                WriteWolfDepot(writer, quote.Before); WriteWolfDepot(writer, quote.After); writer.Write(quote.Reason);
                writer.Flush(); return Hash(stream.ToArray());
            }
        }
        static void WriteWolfDepot(BinaryWriter writer, ColonyWolfDepot depot)
        {
            writer.Write(depot.Body); writer.Write(depot.Biome); writer.Write(depot.Exists); writer.Write(depot.Established); writer.Write(depot.Surveyed);
            writer.Write(depot.Streams.Count);
            foreach (var row in depot.Streams.OrderBy(x => x.Resource, StringComparer.Ordinal)) { writer.Write(row.Resource); writer.Write(row.Incoming); writer.Write(row.Outgoing); }
        }
        static void WriteWolfIngredients(BinaryWriter writer, List<ColonyWolfIngredient> rows)
        {
            writer.Write(rows.Count); foreach (var row in rows.OrderBy(x => x.Resource, StringComparer.Ordinal)) { writer.Write(row.Resource); writer.Write(row.Points); }
        }
        static void WriteWolfRecipe(BinaryWriter writer, ColonyWolfRecipe recipe)
        {
            writer.Write(recipe.Id); writer.Write(recipe.PartName); writer.Write(recipe.ConfigurationHash); writer.Write(recipe.Tech);
            writer.Write(recipe.Funds); writer.Write(recipe.LaborSeconds); WriteWolfIngredients(writer, recipe.Inputs); WriteWolfIngredients(writer, recipe.Outputs);
        }
        public static void ValidateWolfDepot(ColonyWolfDepot depot)
        {
            if (depot == null) Fail("Missing WOLF depot witness.");
            Text(depot!.Body, 128, true); Text(depot.Biome, 128, true); Rows(depot.Streams, 128); Unique(depot.Streams.Select(x => x.Resource));
            if (!depot.Exists && (depot.Established || depot.Surveyed || depot.Streams.Count > 0)) Fail("Absent WOLF depot contains capacity.");
            foreach (var row in depot.Streams) { Text(row.Resource, 128, true); Count(row.Incoming, 1_000_000); Count(row.Outgoing, 1_000_000); if (row.Outgoing > row.Incoming) Fail("WOLF allocation exceeds capacity."); }
        }
        public static void ValidateWolfRecipe(ColonyWolfRecipe recipe, bool depot = false)
        {
            if (recipe == null) Fail("Missing installed WOLF recipe.");
            Text(recipe!.Id, 160, true); Text(recipe.PartName, 128, true); Text(recipe.Tech, 128, true);
            if (recipe.ConfigurationHash.Length != 64 || !recipe.ConfigurationHash.All(Uri.IsHexDigit)) Fail("WOLF recipe lacks an installed configuration hash.");
            Funds(recipe.Funds); if (recipe.Funds == 0) Fail("Normal WOLF module purchase has no installed cost.");
            Range(recipe.LaborSeconds, 1, 365 * ColonyLimits.KerbinDay); WolfIngredients(recipe.Inputs); WolfIngredients(recipe.Outputs);
            if (!depot && recipe.Outputs.Count == 0) Fail("WOLF module has no installed outputs.");
        }
        public static void WolfIngredients(List<ColonyWolfIngredient> rows)
        {
            Rows(rows, 16); Unique(rows.Select(x => x.Resource));
            foreach (var row in rows) { Text(row.Resource, 128, true); Count(row.Points, 1000); if (row.Points == 0) Fail("Empty WOLF ingredient."); }
        }
        static void ValidateWolf(ColonyState state)
        {
            Rows(state.WolfOrders, 128); Unique(state.WolfOrders.Select(x => x.Id));
            if (state.WolfOrders.Count(x => x.State != "operational" && x.State != "cancelled") > 1) Fail("Shared WOLF supplier slot was allocated more than once.");
            foreach (var order in state.WolfOrders)
            {
                Id(order.Id); Id(order.ColonyId); var colony = state.Colonies.SingleOrDefault(x => x.Id == order.ColonyId);
                if (colony == null) Fail("WOLF contract has no owning colony.");
                Choice(order.State, "reserved", "building", "ready", "applying", "operational", "held", "cancelled"); Text(order.Reason, 512);
                Text(order.BeforeWitness, 128); Text(order.AfterWitness, 128); Time(order.AccountedUt); Time(order.WorkCompleted);
                var q = order.Quote ?? throw new InvalidDataException("Missing WOLF contract quote.");
                Id(q.ColonyId); Text(q.ContextKey, 256, true); Text(q.Resource, 128, true); Count(q.DesiredAvailable, 1000);
                if (q.DesiredAvailable == 0 || q.ColonyId != order.ColonyId || q.Revision < 0) Fail("Invalid desired WOLF supply.");
                WolfIngredients(q.Demands);
                if(q.Demands.Count>0 && (q.Demands.Count<2 || !q.Demands.Select(d=>d.Resource).SequenceEqual(q.Demands.Select(d=>d.Resource).OrderBy(r=>r,StringComparer.Ordinal)) ||
                    q.Resource!=q.Demands[0].Resource || q.DesiredAvailable!=q.Demands[0].Points))Fail("Combined WOLF quote has noncanonical or incomplete exact demand terms.");
                Text(q.BalancePolicy, 512, true); Funds(q.Funds); Time(q.LaborSeconds); ValidateWolfDepot(q.Before); ValidateWolfDepot(q.After);
                if (q.Before.Body != colony!.Site.Body || q.Before.Biome != colony.Site.Biome || q.After.Body != q.Before.Body || q.After.Biome != q.Before.Biome) Fail("WOLF contract moved to a different colony biome.");
                if (q.EstablishDepot != !q.Before.Established || q.SurveyDepot != !q.Before.Surveyed) Fail("WOLF contract establishment/survey terms changed.");
                if (q.EstablishDepot) ValidateWolfRecipe(q.DepotConstruction, true);
                WolfIngredients(q.SurveyVeins); if (q.SurveyVeins.Any(x => !x.Resource.EndsWith("Vein", StringComparison.Ordinal))) Fail("Survey may provide only installed geological veins.");
                if (q.SurveyDepot && (q.SurveyPartId == 0 || q.SurveyConfigurationHash.Length != 64)) Fail("WOLF survey lacks physical scanner evidence.");
                if (!q.SurveyDepot && (q.SurveyPartId != 0 || q.SurveyVeins.Count != 0)) Fail("Already surveyed depot cannot receive veins again.");
                Rows(q.Modules, 32); if (q.Modules.Sum(x => x.Count) > 64) Fail("WOLF contract exceeds bounded module count.");
                long funds = q.EstablishDepot ? q.DepotConstruction.Funds : 0; double labor = q.EstablishDepot ? q.DepotConstruction.LaborSeconds : 0;
                foreach (var module in q.Modules) { ValidateWolfRecipe(module.Recipe); Count(module.Count, 64); if (module.Count == 0) Fail("Empty WOLF module order."); funds = checked(funds + checked(module.Recipe.Funds * module.Count)); labor += module.Recipe.LaborSeconds * module.Count; }
                if (q.Funds != funds || q.LaborSeconds != labor || order.WorkCompleted > labor || order.WorkCompleted > 0 && !order.FundsPaid) Fail("WOLF purchase/labor terms lack conservation.");
                if (q.Id != WolfQuoteHash(q)) Fail("WOLF quote hash failed.");
                if (WolfDepotHash(q.After) != WolfDepotHash(ColonyEngine.ExpectedWolfDepot(q)) || !ColonyEngine.WolfDemandsSatisfied(q.After,q)) Fail("WOLF quote violates installed recipe or desired supply conservation.");
                if (order.State == "reserved" && order.FundsPaid || new[] { "building", "ready", "applying", "operational" }.Contains(order.State) && !order.FundsPaid) Fail("WOLF phase lacks a paid purchase.");
                if (new[] { "ready", "applying", "operational" }.Contains(order.State) && order.WorkCompleted != q.LaborSeconds) Fail("WOLF setup lacks completed labor.");
                var active = ColonyEngine.WolfAllocationQuote(order);
                if (order.Replan != null)
                {
                    var r = order.Replan; Id(r.OrderId); Text(r.PurchaseQuoteId, 64, true); Text(r.ContextKey, 256, true); Text(r.Reason, 512, true);
                    if (!order.FundsPaid || r.Revision < 0 || r.OrderId != order.Id || r.PurchaseQuoteId != q.Id || r.Id != WolfReplanHash(r)) Fail("WOLF replan lacks immutable paid module ownership.");
                    ValidateWolfDepot(r.Before); ValidateWolfDepot(r.After); WolfIngredients(r.SurveyVeins);
                    if (r.Before.Body != q.Before.Body || r.Before.Biome != q.Before.Biome || r.EstablishDepot != !r.Before.Established || r.SurveyDepot != !r.Before.Surveyed || r.EstablishDepot && !q.EstablishDepot) Fail("WOLF replan changed biome or creates an unpurchased depot.");
                    if (r.UnusedPurchasedDepot != (q.EstablishDepot && !r.EstablishDepot)) Fail("Unused paid depot ownership was lost.");
                    if (r.SurveyDepot && (r.SurveyPartId == 0 || r.SurveyConfigurationHash.Length != 64) || !r.SurveyDepot && (r.SurveyPartId != 0 || r.SurveyVeins.Count != 0) || r.SurveyVeins.Any(x => !x.Resource.EndsWith("Vein", StringComparison.Ordinal))) Fail("WOLF replan survey lacks physical geological evidence.");
                    if (WolfDepotHash(r.After) != WolfDepotHash(ColonyEngine.ExpectedWolfDepot(active)) || !ColonyEngine.WolfDemandsSatisfied(r.After,q)) Fail("WOLF replan changes owned modules or fails desired allocation conservation.");
                }
                if ((order.State == "applying" || order.State == "operational") && !order.AllocationAttempted) Fail("WOLF external phase lacks a recorded attempt boundary.");
                if (order.State == "operational" && (order.BeforeWitness != WolfDepotHash(active.Before) || order.AfterWitness != WolfDepotHash(active.After))) Fail("WOLF activation lacks deterministic full-depot witnesses.");
            }
        }
    }
}
