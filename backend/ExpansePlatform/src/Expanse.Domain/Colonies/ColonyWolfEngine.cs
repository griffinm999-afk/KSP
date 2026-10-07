using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static ColonyWolfQuote QuoteWolfSupply(ColonyState state, string colonyId, string resource, int desiredAvailable, ColonyEnvironment env)
        {
            var q=CreateWolfSupplyQuote(state,colonyId,resource,desiredAvailable,env);var colony=Colony(state,colonyId);
            var baseline = ExpectedWolfDepot(q);
            int existing = WolfAvailable(baseline, resource), deficit = Math.Max(0, desiredAvailable - existing);
            return CompleteScalarWolfQuote(q,deficit,resource,env,colony,baseline);
        }
        static ColonyWolfQuote CreateWolfSupplyQuote(ColonyState state,string colonyId,string resource,int desiredAvailable,ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(state); ValidateEnvironment(state, env); var colony = Colony(state, colonyId);
            if (!env.Wolf.Ready) throw new InvalidDataException(env.Wolf.Reason);
            if (env.Wolf.Recipes == null || env.Wolf.Recipes.Count > 128 || env.Wolf.Recipes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != env.Wolf.Recipes.Count) throw new InvalidDataException("Installed WOLF recipe catalog is invalid or exceeds its bound.");
            foreach (var recipe in env.Wolf.Recipes) ColonyStateCodec.ValidateWolfRecipe(recipe);
            if (desiredAvailable < 1 || desiredAvailable > 1000) throw new InvalidDataException("Desired unallocated WOLF supply must be 1–1000 points.");
            if (state.WolfOrders.Any(x => x.State != "operational" && x.State != "cancelled")) throw new InvalidDataException("The shared WOLF module supplier already has an unresolved purchase.");
            var site = env.Wolf.Sites.SingleOrDefault(x => x.ColonyId == colonyId) ?? throw new InvalidDataException("No qualified WOLF biome observation.");
            if (site.ContextKey != env.ContextKey || site.ObservedUt > env.Ut || env.Ut - site.ObservedUt > 6 || site.Depot.Body != colony.Site.Body || site.Depot.Biome != colony.Site.Biome)
                throw new InvalidDataException("WOLF site witness is stale or belongs to another selected save/biome.");
            ColonyStateCodec.ValidateWolfDepot(site.Depot);
            var q = new ColonyWolfQuote { ColonyId = colonyId, ContextKey = env.ContextKey, Revision = state.Revision, Resource = resource,
                DesiredAvailable = desiredAvailable, Before = CloneWolfDepot(site.Depot), EstablishDepot = !site.Depot.Established,
                SurveyDepot = !site.Depot.Surveyed, HomeGround = site.HomeGround, DepotConstruction = CloneWolfRecipe(env.Wolf.DepotConstruction) };
            if (q.SurveyDepot)
            {
                if (site.SurveyPartId == 0 || site.SurveyConfigurationHash.Length != 64) throw new InvalidDataException(site.Reason.Length == 0 ? "Survey requires an adopted landed installed WOLF Surface Scanner at this biome." : site.Reason);
                q.SurveyPartId = site.SurveyPartId; q.SurveyConfigurationHash = site.SurveyConfigurationHash; q.SurveyVeins = CloneWolfIngredients(site.SurveyVeins);
            }
            if (q.EstablishDepot) { ColonyStateCodec.ValidateWolfRecipe(q.DepotConstruction, true); RequireWolfTech(q.DepotConstruction, env, colony); }
            return q;
        }
        static ColonyWolfQuote CompleteScalarWolfQuote(ColonyWolfQuote q,int deficit,string resource,ColonyEnvironment env,ColonyRecord colony,ColonyWolfDepot baseline)
        {
            if (deficit > 0 && resource == "Power")
            {
                var power = env.Wolf.Recipes.Where(x => x.Inputs.Count == 0 && x.Outputs.Count == 1 && x.Outputs[0].Resource == "Power")
                    .OrderBy(x => (decimal)x.Funds / x.Outputs[0].Points).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidDataException("Normal Power setup requires an installed input-free Power recipe; virtual crew points are not fabricated.");
                RequireWolfTech(power, env, colony);
                int count = checked((deficit + power.Outputs[0].Points - 1) / power.Outputs[0].Points);
                if (count + (q.EstablishDepot ? 1 : 0) > 64) throw new InvalidDataException("Desired Power exceeds the bounded 64-module supplier setup.");
                q.Modules.Add(new ColonyWolfModule { Recipe = CloneWolfRecipe(power), Count = count }); q.After = ExpectedWolfDepot(q);
            }
            else if (deficit > 0)
            {
                // Bounded extraction only: exact installed raw -> Vein recipe. No
                // generic dependency search may synthesize virtual crew points.
                var harvesters = env.Wolf.Recipes.Where(x => x.Outputs.Count == 1 && x.Outputs[0].Resource == resource &&
                    x.Inputs.Any(i => i.Resource == resource + "Vein") && x.Inputs.All(i => i.Resource == "Power" || i.Resource == resource + "Vein"))
                    .OrderBy(x => (decimal)x.Funds / x.Outputs[0].Points).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
                string lastFailure = "No installed bounded harvester recipe exists for " + resource + ".";
                bool selected = false;
                foreach (var harvester in harvesters)
                {
                    try
                    {
                        ColonyStateCodec.ValidateWolfRecipe(harvester); RequireWolfTech(harvester, env, colony);
                        int count = checked((deficit + harvester.Outputs[0].Points - 1) / harvester.Outputs[0].Points);
                        if (count > 64) throw new InvalidDataException("Desired supply exceeds the bounded 64-module setup.");
                        var modules = new List<ColonyWolfModule> { new ColonyWolfModule { Recipe = CloneWolfRecipe(harvester), Count = count } };
                        int powerNeeded = checked(harvester.Inputs.Where(x => x.Resource == "Power").Sum(x => x.Points) * count);
                        int powerMissing = Math.Max(0, powerNeeded - WolfAvailable(baseline, "Power"));
                        if (powerMissing > 0)
                        {
                            var power = env.Wolf.Recipes.Where(x => x.Inputs.Count == 0 && x.Outputs.Count == 1 && x.Outputs[0].Resource == "Power")
                                .OrderBy(x => (decimal)x.Funds / x.Outputs[0].Points).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault()
                                ?? throw new InvalidDataException("Power dependency is unavailable; no installed input-free Power recipe.");
                            ColonyStateCodec.ValidateWolfRecipe(power); RequireWolfTech(power, env, colony);
                            modules.Insert(0, new ColonyWolfModule { Recipe = CloneWolfRecipe(power), Count = checked((powerMissing + power.Outputs[0].Points - 1) / power.Outputs[0].Points) });
                        }
                        if (modules.Sum(x => x.Count) + (q.EstablishDepot ? 1 : 0) > 64) throw new InvalidDataException("Power dependencies exceed the bounded 64-module setup.");
                        q.Modules = modules; q.After = ExpectedWolfDepot(q); selected = true; break;
                    }
                    catch (InvalidDataException ex) { lastFailure = ex.Message; }
                }
                if (!selected) throw new InvalidDataException(lastFailure);
            }
            else q.After = baseline;
            q.Funds = checked((q.EstablishDepot ? q.DepotConstruction.Funds : 0) + q.Modules.Sum(x => checked(x.Recipe.Funds * x.Count)));
            q.LaborSeconds = (q.EstablishDepot ? q.DepotConstruction.LaborSeconds : 0) + q.Modules.Sum(x => x.Recipe.LaborSeconds * x.Count);
            q.Id = ColonyStateCodec.WolfQuoteHash(q); return q;
        }

        static void RequireWolfTech(ColonyWolfRecipe recipe, ColonyEnvironment env, ColonyRecord colony)
        {
            if (!env.UnlockedTech.Contains(recipe.Tech) && !(env.DevelopmentMode && colony.Charter.Sandbox)) throw new InvalidDataException("Installed WOLF module requires unlocked technology: " + recipe.Tech + ".");
        }
        static string ExecuteWolf(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            if (command.Kind == "replanPaidWolfSupply")
            {
                var replan = QuotePaidWolfReplan(state, command.TargetId, env);
                if (command.QuoteId != replan.Id) throw new InvalidDataException("Current paid-package allocation quote changed. Refresh before replan.");
                var owned = state.WolfOrders.Single(x => x.Id == command.TargetId);
                if (owned.ColonyId != command.ColonyId) throw new InvalidDataException("Paid WOLF package belongs to another colony.");
                owned.Replan = replan; owned.State = "ready"; owned.BeforeWitness = ""; owned.AfterWitness = "";
                owned.Reason = "Same paid modules retained; fresh reviewed allocation preflight ready. No second purchase or manufacturing credit.";
                foreach (var held in state.Effects.Where(x => x.TargetId == owned.Id && x.Kind == "wolfAllocation" && x.State == "held")) { held.State = "cancelled"; held.Reason = "Unattempted preflight superseded by an explicit paid-package replan; stored old witnesses retained."; }
                Log(state, env.Ut, owned.ColonyId, command.OperationId, "wolfPaidReplan", owned.Reason); return owned.Id;
            }
            if (command.Kind == "cancelWolfSupply")
            {
                var order = state.WolfOrders.SingleOrDefault(x => x.Id == command.TargetId && x.ColonyId == command.ColonyId) ?? throw new InvalidDataException("WOLF purchase is unavailable.");
                if (order.State != "reserved" || order.FundsPaid) throw new InvalidDataException("Paid or started WOLF setup cannot be cancelled by granting an unverified refund.");
                order.State = "cancelled"; order.Reason = "Unstarted purchase cancelled; WOLF capacity unchanged.";
                foreach (var effect in state.Effects.Where(x => x.TargetId == order.Id && x.Kind == "wolfPurchase" && x.State == "prepared")) effect.State = "cancelled";
                return order.Id;
            }
            if (command.Kind != "approveWolfSupply" && command.Kind != "approveWolfSupplies") throw new InvalidDataException("Unknown WOLF operation.");
            var q = command.Kind=="approveWolfSupplies" ? QuoteWolfSupplies(state,command.ColonyId,ReadWolfCommandDemands(command),env) :
                QuoteWolfSupply(state, command.ColonyId, Field(command, "Resource"), checked((int)Integer(command, "DesiredAvailable")), env);
            if (command.QuoteId != q.Id) throw new InvalidDataException("WOLF ledger, installed recipe or quoted cost changed. Obtain a new quote.");
            return ReserveWolfQuote(state,command,env,q);
        }
        // Caller has just quoted the intact authoritative state. Production may
        // release exactly this child's parent commitment immediately before this
        // reservation; validate the complete state after both changes together.
        internal static string ReserveWolfQuote(ColonyState state,ColonyCommand command,ColonyEnvironment env,ColonyWolfQuote q)
        {
            if(q==null || q.ColonyId!=command.ColonyId || q.ContextKey!=env.ContextKey || q.Revision!=state.Revision || q.Id!=ColonyStateCodec.WolfQuoteHash(q) || command.QuoteId!=q.Id)
                throw new InvalidDataException("WOLF child reservation lacks the exact fresh reviewed quote.");
            if(state.WolfOrders.Any(o=>o.Id==command.OperationId || o.State!="operational" && o.State!="cancelled"))throw new InvalidDataException("Shared finite WOLF supplier slot is already reserved; no duplicate child purchase.");
            var colony = Colony(state, command.ColonyId); CheckFunds(state, colony, q.Funds, env);
            if (state.WolfOrders.Count >= 128) throw new InvalidDataException("WOLF setup history reached its supported limit.");
            var orderNew = new ColonyWolfOrder { Id = command.OperationId, ColonyId = command.ColonyId, Quote = q, AccountedUt = env.Ut };
            if (q.Funds == 0)
            {
                orderNew.FundsPaid = true; orderNew.State = "ready"; orderNew.Reason = "Existing external WOLF capacity meets desired supply; no stock was created.";
            }
            else state.Effects.Add(FundsEffect(command, orderNew.Id, -q.Funds, "wolfPurchase"));
            state.WolfOrders.Add(orderNew);
            Log(state, env.Ut, colony.Id, command.OperationId, "wolfQuoteApproved", "Installed virtual-module purchase and exact WOLF input allocation approved; physical production remains with installed hoppers.");
            return orderNew.Id;
        }
        static bool CompleteWolfFundsEffect(ColonyState state, ColonyEffect effect, double ut)
        {
            if (effect.Kind != "wolfPurchase") return false;
            var order = state.WolfOrders.Single(x => x.Id == effect.TargetId && x.ColonyId == effect.ColonyId);
            if (order.State != "reserved" || order.FundsPaid || effect.FundsDelta != -order.Quote.Funds) throw new InvalidDataException("WOLF purchase does not match immutable quote.");
            order.FundsPaid = true; order.State = "building"; order.AccountedUt = ut; order.Reason = "Paid outsourced manufacture; sequential supplier lead time under modeled balance.";
            var colony = Colony(state, order.ColonyId); colony.SpentFunds = checked(colony.SpentFunds + order.Quote.Funds); return true;
        }
        static void AdvanceWolf(ColonyState state, ColonyEnvironment env, double ut)
        {
            foreach (var order in state.WolfOrders.Where(x => x.State == "building"))
            {
                double elapsed = Math.Max(0, ut - order.AccountedUt); order.AccountedUt = ut;
                order.WorkCompleted = Math.Min(order.Quote.LaborSeconds, order.WorkCompleted + elapsed);
                order.Reason = "Outsourced virtual WOLF modules in sequential supplier manufacture (modeled lead time).";
                if (order.WorkCompleted == order.Quote.LaborSeconds) { order.State = "ready"; order.Reason = "Awaiting exact current WOLF depot preflight and installed API allocation."; }
            }
        }
        public static ColonyState MarkWolfApplying(ColonyState prior, string orderId, string provider, string beforeWitness)
        {
            var state = ColonyStateCodec.Copy(prior); var order = state.WolfOrders.Single(x => x.Id == orderId);
            if (order.State != "ready" || order.AllocationAttempted || beforeWitness != ColonyStateCodec.WolfDepotHash(WolfAllocationQuote(order).Before)) throw new InvalidDataException("WOLF setup is not ready or depot differs from quoted full witness.");
            if (!CompactTerminalEffects(state, 1)) throw new InvalidDataException("External effect capacity reached; reconcile pending operations.");
            order.State = "applying"; order.AllocationAttempted = true; order.BeforeWitness = beforeWitness; order.Reason = "WOLF mutation outcome is unverified; replay disabled.";
            state.Effects.Add(new ColonyEffect { Id = Guid.NewGuid().ToString("D"), OperationId = order.Id, ColonyId = order.ColonyId, TargetId = order.Id, Kind = "wolfAllocation", State = "applying", Provider = provider, BeforeWitness = beforeWitness, Reason = order.Reason });
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }
        public static ColonyState CompleteWolfAllocation(ColonyState prior, string orderId, string afterWitness, double ut)
        {
            var state = ColonyStateCodec.Copy(prior); var order = state.WolfOrders.Single(x => x.Id == orderId);
            if (order.State != "applying" || afterWitness != ColonyStateCodec.WolfDepotHash(WolfAllocationQuote(order).After)) throw new InvalidDataException("WOLF allocation lacks exact deterministic depot readback.");
            var effect = state.Effects.Single(x => x.TargetId == order.Id && x.Kind == "wolfAllocation" && x.State == "applying");
            order.State = "operational"; order.AfterWitness = afterWitness; order.Reason = "Verified WOLF capacity; physical hopper output is still owned by installed converters/BRP.";
            effect.State = "applied"; effect.AfterWitness = afterWitness; effect.Reason = order.Reason;
            Log(state, ut, order.ColonyId, order.Id, "wolfAllocation", order.Reason); state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }
        public static ColonyState HoldWolfAllocation(ColonyState prior, string orderId, string actualAfterWitness, string reason)
        {
            var state = ColonyStateCodec.Copy(prior); var order = state.WolfOrders.Single(x => x.Id == orderId);
            if (order.State == "operational" || order.State == "cancelled") throw new InvalidDataException("Terminal WOLF setup cannot be held.");
            order.State = "held"; order.AfterWitness = actualAfterWitness; order.Reason = reason;
            foreach (var effect in state.Effects.Where(x => x.TargetId == order.Id && (x.State == "prepared" || x.State == "applying"))) { effect.State = "held"; effect.AfterWitness = actualAfterWitness; effect.Reason = reason; }
            // Preflight holds also fence all mutation paths until explicitly reconciled.
            if (!state.Effects.Any(x => x.TargetId == order.Id && x.State == "held"))
            {
                if (!CompactTerminalEffects(state, 1)) throw new InvalidDataException("External effect capacity reached; reconcile pending operations.");
                state.Effects.Add(new ColonyEffect { Id = Guid.NewGuid().ToString("D"), OperationId = order.Id, ColonyId = order.ColonyId, TargetId = order.Id, Kind = "wolfAllocation", State = "held", Provider = "WOLF", BeforeWitness = ColonyStateCodec.WolfDepotHash(WolfAllocationQuote(order).Before), AfterWitness = actualAfterWitness, Reason = reason });
            }
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }
        public static bool CanReplanPaidWolfSupply(ColonyState state, string orderId)
        {
            var order = state.WolfOrders.SingleOrDefault(x => x.Id == orderId);
            if (order == null || order.State != "held" || !order.FundsPaid || order.AllocationAttempted || order.WorkCompleted != order.Quote.LaborSeconds) return false;
            var unresolved = state.Effects.Where(x => x.State == "held" || x.State == "applying").ToArray();
            return unresolved.Length == 1 && unresolved[0].State == "held" && unresolved[0].Kind == "wolfAllocation" && unresolved[0].TargetId == orderId && unresolved[0].ColonyId == order.ColonyId;
        }
        public static ColonyWolfReplanQuote QuotePaidWolfReplan(ColonyState state, string orderId, ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(state); ValidateEnvironment(state, env);
            if (!CanReplanPaidWolfSupply(state, orderId)) throw new InvalidDataException("Only an unattempted preflight hold with verified paid module ownership can be replanned. Unknown or partial WOLF effects cannot be resent.");
            if (!env.Wolf.Ready) throw new InvalidDataException(env.Wolf.Reason);
            var order = state.WolfOrders.Single(x => x.Id == orderId); var q = order.Quote;
            var site = env.Wolf.Sites.SingleOrDefault(x => x.ColonyId == order.ColonyId) ?? throw new InvalidDataException("Current WOLF biome observation is unavailable.");
            if (site.ContextKey != env.ContextKey || site.ObservedUt > env.Ut || env.Ut - site.ObservedUt > 6 || site.Depot.Body != q.Before.Body || site.Depot.Biome != q.Before.Biome) throw new InvalidDataException("WOLF replan witness belongs to another save/biome or is stale.");
            var r = new ColonyWolfReplanQuote { OrderId = order.Id, PurchaseQuoteId = q.Id, ContextKey = env.ContextKey, Revision = state.Revision,
                Before = CloneWolfDepot(site.Depot), EstablishDepot = !site.Depot.Established, SurveyDepot = !site.Depot.Surveyed, HomeGround = site.HomeGround };
            if (r.EstablishDepot && !q.EstablishDepot) throw new InvalidDataException("This paid package does not own a depot construction module; a missing depot cannot be created at no cost.");
            r.UnusedPurchasedDepot = q.EstablishDepot && !r.EstablishDepot;
            if (r.SurveyDepot)
            {
                if (site.SurveyPartId == 0 || site.SurveyConfigurationHash.Length != 64) throw new InvalidDataException("Paid replan still requires a real current adopted Surface Scanner to survey genuine veins.");
                r.SurveyPartId = site.SurveyPartId; r.SurveyConfigurationHash = site.SurveyConfigurationHash; r.SurveyVeins = CloneWolfIngredients(site.SurveyVeins);
            }
            var planOrder = new ColonyWolfOrder { Quote = q, Replan = r }; r.After = ExpectedWolfDepot(WolfAllocationQuote(planOrder));
            if (!WolfDemandsSatisfied(r.After,q)) throw new InvalidDataException("Same paid modules no longer meet every desired unallocated supply. Additional hardware requires a separate reviewed purchase; this replan cannot grant it.");
            r.Id = ColonyStateCodec.WolfReplanHash(r); return r;
        }
        public static ColonyWolfQuote WolfAllocationQuote(ColonyWolfOrder order)
        {
            if (order.Replan == null) return order.Quote;
            var q = order.Quote; var r = order.Replan;
            return new ColonyWolfQuote { ColonyId = q.ColonyId, Resource = q.Resource, DesiredAvailable = q.DesiredAvailable, Modules = q.Modules,
                Demands=q.Demands,
                Before = r.Before, After = r.After, EstablishDepot = r.EstablishDepot, SurveyDepot = r.SurveyDepot, HomeGround = r.HomeGround,
                SurveyPartId = r.SurveyPartId, SurveyConfigurationHash = r.SurveyConfigurationHash, SurveyVeins = r.SurveyVeins, DepotConstruction = q.DepotConstruction };
        }
        public static ColonyWolfDepot ExpectedWolfDepot(ColonyWolfQuote q)
        {
            var depot = CloneWolfDepot(q.Before); depot.Exists = true; depot.Established = true; depot.Surveyed = true;
            Action<string, int, bool> add = (resource, points, outgoing) => { var row = depot.Streams.SingleOrDefault(x => x.Resource == resource); if (row == null) { row = new ColonyWolfStream { Resource = resource }; depot.Streams.Add(row); } if (outgoing) row.Outgoing = checked(row.Outgoing + points); else row.Incoming = checked(row.Incoming + points); };
            if (q.EstablishDepot)
            {
                if (q.HomeGround) { add("Food", 1, false); add("MaterialKits", 5, false); add("Oxygen", 1, false); add("Power", 10, false); add("Water", 5, false); }
                else add("Power", 5, false);
            }
            if (q.SurveyDepot) foreach (var ingredient in q.SurveyVeins) add(ingredient.Resource, ingredient.Points, false);
            foreach (var module in q.Modules) foreach (var ingredient in module.Recipe.Outputs) add(ingredient.Resource, checked(ingredient.Points * module.Count), false);
            foreach (var module in q.Modules) foreach (var ingredient in module.Recipe.Inputs) add(ingredient.Resource, checked(ingredient.Points * module.Count), true);
            var missing = depot.Streams.Where(x => x.Outgoing > x.Incoming).Select(x => x.Resource + "=" + (x.Outgoing - x.Incoming).ToString(CultureInfo.InvariantCulture)).ToArray();
            if (missing.Length > 0) throw new InvalidDataException("Actual WOLF recipe dependencies unavailable: " + string.Join(", ", missing) + ". Surveyed veins/crew points are not fabricated.");
            ColonyStateCodec.ValidateWolfDepot(depot); return depot;
        }
        public static int WolfAvailable(ColonyWolfDepot depot, string resource) { var row = depot.Streams.SingleOrDefault(x => x.Resource == resource); return row == null ? 0 : row.Incoming - row.Outgoing; }
        public static ColonyWolfDepot CloneWolfDepot(ColonyWolfDepot x) => new ColonyWolfDepot { Body = x.Body, Biome = x.Biome, Exists = x.Exists, Established = x.Established, Surveyed = x.Surveyed, Streams = x.Streams.Select(s => new ColonyWolfStream { Resource = s.Resource, Incoming = s.Incoming, Outgoing = s.Outgoing }).ToList() };
        static List<ColonyWolfIngredient> CloneWolfIngredients(List<ColonyWolfIngredient> rows) => rows.Select(x => new ColonyWolfIngredient { Resource = x.Resource, Points = x.Points }).ToList();
        static ColonyWolfRecipe CloneWolfRecipe(ColonyWolfRecipe x) => new ColonyWolfRecipe { Id = x.Id, PartName = x.PartName, ConfigurationHash = x.ConfigurationHash, Tech = x.Tech, Funds = x.Funds, LaborSeconds = x.LaborSeconds, Inputs = CloneWolfIngredients(x.Inputs), Outputs = CloneWolfIngredients(x.Outputs) };
    }
}
