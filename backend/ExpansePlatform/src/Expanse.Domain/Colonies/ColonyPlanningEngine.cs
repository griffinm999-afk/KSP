using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static ColonyPlanningQuote QuoteFoundingPlan(ColonyState state, string colonyId, ColonyEnvironment env) => BuildPlanningQuote(state, colonyId, env, "founding");
        public static ColonyPlanningQuote QuoteFoundingPlan(ColonyState state, string colonyId, ColonyEnvironment env, ColonyFoundingIntent? intent) => BuildPlanningQuote(state, colonyId, env, "founding", intent);

        static ColonyPlanningQuote BuildPlanningQuote(ColonyState state, string colonyId, ColonyEnvironment env, string kind, ColonyFoundingIntent? intent = null)
        {
            var q = new ColonyPlanningQuote { ColonyId = colonyId, ContextKey = env.ContextKey, Revision = state.Revision, Kind = kind };
            try
            {
                ValidateEnvironment(state, env);
                var colony = Colony(state, colonyId);
                q.AuthorityHash = PlanningAuthorityHash(state, colony, env);
                q.SupportPolicyHash = env.Support.PolicyHash;
                q.Forecast = Forecast(state, colonyId, env);
                if (state.Plans.Any(p => p.ColonyId == colonyId && PlanningActive(p))) q.Blockers.Add("An approved plan already owns this colony's procurement and plots.");
                if (kind == "founding" && colony.SupportCommissionedUt.HasValue) q.Blockers.Add("This colony is commissioned; use a growth plan.");
                var qualified = env.Planning.Assets.Where(a => a.ColonyId == colony.Id && a.ContextKey == env.ContextKey && a.Qualified && a.Witness.Length > 0 &&
                    colony.Facilities.Any(f => f.Id == a.FacilityId && f.State != "retired")).ToArray();
                q.ExistingAssets = colony.Facilities.OrderBy(f => f.Id, StringComparer.Ordinal).Select(f => f.Name + " — preserved " + f.State).ToList();
                int homes = QualifiedPlanningHomes(colony, env);
                var usedPlots = new HashSet<string>();
                if (kind == "founding")
                {
                    foreach (var role in new[] { "storage", "power", "workshop", "lamp" })
                    {
                        var configured = env.Planning.Roles.SingleOrDefault(r => r.Role == role);
                        if (configured == null) { q.Blockers.Add("No installed package configured for " + role + "."); continue; }
                        int existing = qualified.Where(a => a.Role == role).Select(a => a.FacilityId).Distinct(StringComparer.Ordinal).Count();
                        for (int i = existing; i < configured.MinimumCount; i++) AddPlanningBuilding(q, role, ChoosePlanningTemplate(colony, env, role), colony, usedPlots);
                    }
                }
                int demand = kind == "founding" ? colony.Charter.PopulationTarget : Math.Min(colony.Charter.ResidentLimit,
                    Math.Max(colony.Charter.PopulationTarget, colony.Residents.Count(r => r.Status != "missing") + q.Forecast.OpenQualifiedJobs));
                var habitat = ChoosePlanningTemplate(colony, env, "housing");
                q.TargetPopulation = colony.Charter.PopulationTarget;
                if (kind == "growth" || demand > homes)
                {
                    if (habitat == null || habitat.Homes <= 0) throw new InvalidDataException("No affordable, unlocked, certified habitat package is configured.");
                    int count = kind == "growth" ? 1 : Math.Max(0, (int)Math.Ceiling((demand - homes) / (double)habitat.Homes));
                    if (count > 64) throw new InvalidDataException("Founding target exceeds the bounded 64-building plan.");
                    for (int i = 0; i < count; i++) AddPlanningBuilding(q, "housing", habitat, colony, usedPlots);
                    if (kind == "growth") q.TargetPopulation = Math.Min(colony.Charter.ResidentLimit, Math.Min(demand, homes + habitat.Homes));
                }
                QuotePlanningProduction(q,state,colony,env,intent,usedPlots);
                if (q.Buildings.Count > 64) throw new InvalidDataException("Founding service, housing and production requirements exceed 64 buildings.");
                // Prerequisites are actual building lines, not fictitious free facilities.
                foreach (var building in q.Buildings)
                {
                    string[] roles = building.Role == "workshop" ? new[] { "storage", "power" } : building.Role == "housing" || building.Role == "lamp" || building.Role=="production" || building.Role=="productionFeed" ? new[] { "storage", "power", "workshop" } : new string[0];
                    building.Dependencies = q.Buildings.Where(b => roles.Contains(b.Role)).Select(b => b.Id).ToList();
                }
                QuotePlanningWorkers(q,colony,state,env);
                QuotePlanningResidents(q,colony,state,env,intent);
                q.Materials = q.Buildings.SelectMany(b => b.Materials).GroupBy(m => m.Resource, StringComparer.Ordinal)
                    .Select(g => new MaterialRequirement { Resource = g.Key, Amount = checked(g.Sum(m => m.Amount)) }).OrderBy(m => m.Resource, StringComparer.Ordinal).ToList();
                q.TotalFunds = checked(q.Buildings.Sum(b => b.Funds) + q.Residents.Sum(r=>r.Fare) + ProductionExtraFunds(q)); q.LaborSeconds = q.Buildings.Sum(b => b.LaborSeconds);
                q.StartupSupportReserve = SupportReserveQuote(colony, env);
                q.StartupSupportReserve = Math.Max(q.StartupSupportReserve, checked((long)decimal.Ceiling((decimal)q.TargetPopulation * env.Support.MicroUnitsPerPersonDay * (decimal)colony.Charter.ReserveDays)));
                q.StartupSupportReserve = Math.Max(q.StartupSupportReserve, PlanningResidentSupportReserve(q,colony,env));
                q.Forecast=Forecast(state,colonyId,env,q.Residents.Where(r=>r.Kind=="recruit").Select(r=>r.RosterId));
                QuoteStartupPolicies(q,state,colony,env);
                var receiving = colony.Stock;
                ColonyEconomyPolicy? policy = null;
                if (colony.Logistics.State == "none")
                {
                    policy = env.EconomyPolicies.SingleOrDefault(p => p.Body == colony.Site.Body);
                    if (policy == null) throw new InvalidDataException("No finite supplier/staging contract serves this colony body.");
                    ColonyStateCodec.ValidateEconomyPolicy(policy);
                    q.StagingPolicyId = policy.Id; q.StagingPolicyHash = policy.Hash; q.StagingFunds = policy.SetupFunds;
                    q.TotalFunds = checked(q.TotalFunds + q.StagingFunds); receiving = policy.Stores;
                }
                else if (colony.Logistics.State == "reserved" || colony.Logistics.State == "delivering") receiving = colony.Logistics.Stores;
                else if (colony.Logistics.State != "operational") q.Blockers.Add("The staging contract needs reconciliation.");
                ValidateProductionPolicyCompatibility(q,state,colony,env,policy);
                if(q.StartupPolicies!=null)
                    foreach(var reorder in q.StartupPolicies.Reorders.Where(r=>r.Enabled))
                        if(!state.Suppliers.Concat(policy?.Suppliers??new List<ColonySupplier>()).Any(s=>s.Resource==reorder.Resource&&(s.DestinationBody.Length==0||s.DestinationBody==colony.Site.Body)))
                            q.Blockers.Add("No finite supplier contract supports reviewed recurring "+reorder.Resource+" procurement.");
                var materialRequirements = PlanningMaterialRequirements(q);
                var requirements = materialRequirements.Select(m => new MaterialRequirement { Resource = m.Resource, Amount = m.Amount }).ToList();
                var support = requirements.SingleOrDefault(m => m.Resource == "Supplies");
                if (support == null && q.StartupSupportReserve > 0) requirements.Add(new MaterialRequirement { Resource = "Supplies", Amount = q.StartupSupportReserve });
                else if (support != null) support.Amount = checked(support.Amount + q.StartupSupportReserve);
                foreach (var requirement in requirements.OrderBy(m => m.Resource, StringComparer.Ordinal))
                {
                    var store = receiving.SingleOrDefault(s => s.Resource == requirement.Resource);
                    if (store == null) { q.Blockers.Add("No owned receiving capacity for " + requirement.Resource + "."); continue; }
                    long floor = requirement.Resource == "Supplies" ? Math.Max(store.SupportFloor, q.StartupSupportReserve) : store.SupportFloor;
                    long material = materialRequirements.Where(m => m.Resource == requirement.Resource).Sum(m => m.Amount);
                    long shortage = checked(material + floor - Math.Max(0, store.Amount - store.Reserved));
                    shortage = Math.Max(0, shortage);
                    long materialIncoming = Math.Max(0, material - Math.Max(0, store.Amount - store.Reserved - floor));
                    foreach (var shipment in state.Shipments.Where(s => s.ColonyId == colonyId && s.Resource == requirement.Resource && s.Kind == "import" &&
                        (s.State == "reserved" || s.State == "inTransit")).OrderBy(s => s.ArrivalUt).ThenBy(s => s.Id, StringComparer.Ordinal))
                    {
                        long free = shipment.Amount - PlanningIncomingReserved(state, shipment.Id), take = Math.Min(shortage, free);
                        if (take <= 0) continue;
                        long claimMaterial = Math.Min(materialIncoming, take);
                        q.ExistingIncoming.Add(new ColonyPlanningIncomingClaim { ShipmentId = shipment.Id, Resource = shipment.Resource, Amount = take, MaterialAmount = claimMaterial });
                        shortage -= take; materialIncoming -= claimMaterial;
                    }
                    if (material + floor > store.Capacity || shortage > store.Capacity - store.Amount - store.IncomingReserved)
                    { q.Blockers.Add("Receiving capacity cannot hold promised materials and support reserve: " + requirement.Resource + "."); continue; }
                    var candidates = state.Suppliers.Where(s => s.Resource == requirement.Resource && (s.DestinationBody.Length == 0 || s.DestinationBody == colony.Site.Body))
                        .Concat(policy == null ? Enumerable.Empty<ColonySupplier>() : policy.Suppliers.Where(s => s.Resource == requirement.Resource && !state.Suppliers.Any(t => t.Id == s.Id)))
                        .OrderBy(s => s.FundsPerUnit).ThenBy(s => s.FreightFunds).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
                    foreach (var supplier in candidates)
                    {
                        long available = Math.Max(0, supplier.Available - supplier.Reserved - PlanningSupplierReserved(state, supplier.Id));
                        long batch = PlanningMaximumBatch(supplier, store);
                        while (shortage > 0 && available > 0 && batch > 0)
                        {
                            if (q.Imports.Count >= 64) throw new InvalidDataException("Procurement exceeds the bounded 64-load quote; increase finite freight capacity or reduce target.");
                            long take = Math.Min(shortage, Math.Min(available, batch));
                            long funds = checked(ScaledProduct(take, supplier.FundsPerUnit) + supplier.FreightFunds);
                            q.Imports.Add(new ColonyPlanningImport { Id = "cargo-" + q.Imports.Count.ToString(CultureInfo.InvariantCulture), SupplierId = supplier.Id,
                                SupplierTermsHash = PlanningSupplierTermsHash(supplier), Resource = requirement.Resource, Amount = take,
                                FundsPerUnit = supplier.FundsPerUnit, FreightFunds = supplier.FreightFunds, Funds = funds, TravelSeconds = supplier.TravelSeconds });
                            q.TotalFunds = checked(q.TotalFunds + funds); shortage -= take; available -= take;
                        }
                        if (shortage == 0) break;
                    }
                    if (shortage > 0) q.Blockers.Add("Finite supplier stock or freight mass/volume cannot cover " + requirement.Resource + ".");
                }
                if (q.Buildings.Any(b => b.PlotId.Length == 0)) q.Blockers.Add("Missing clear surveyed plots. Load the site and survey the proposed street before approval.");
                CheckFunds(state, colony, q.TotalFunds, env);
                QuoteMakeImportComparisons(q,state,colony,env);
                q.Rationale = kind == "founding" ? "Preserve adopted hardware; establish storage, reliable utilities, workshop and street lamps, then real homes for the charter target. Native commissioning remains required."
                    : "Housing pressure and actual open jobs justify one affordable habitat; the zero-export, delayed-import forecast must remain supported.";
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException || ex is ArgumentException)
            { q.Blockers.Add(ex.Message); }
            q.CanApprove = q.Blockers.Count == 0;
            q.Id = ColonyStateCodec.PlanningQuoteHash(q);
            return q;
        }

        static ColonyTemplate? ChoosePlanningTemplate(ColonyRecord colony, ColonyEnvironment env, string role)
        {
            var ids = env.Planning.Roles.Where(r => r.Role == role).Select(r => r.TemplateId).ToArray();
            return env.Templates.Where(t => ids.Contains(t.Id) && t.LaborFunds > 0 && t.LaborFunds <= t.BuildFunds && t.LaborSeconds > 0 &&
                (t.RuntimeCertified && t.CertificationEvidence.Length > 0 || env.DevelopmentMode && colony.Charter.Sandbox) &&
                (colony.Charter.Sandbox || t.RequiredTech.All(env.UnlockedTech.Contains)))
                .OrderBy(t => role == "housing" ? (decimal)t.BuildFunds / Math.Max(1, t.Homes) : t.BuildFunds).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        static void AddPlanningBuilding(ColonyPlanningQuote q, string role, ColonyTemplate? template, ColonyRecord colony, HashSet<string> used)
        {
            if (template == null) { q.Blockers.Add("No certified and unlocked package for " + role + "."); return; }
            var plot = colony.Plots.OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(p => p.ReservedBy.Length == 0 && p.OccupiedBy.Length == 0 &&
                p.SurveyHash.Length > 0 && p.TemplateId == template.Id && p.TemplateHash == template.Hash && p.WidthMeters >= template.WidthMeters &&
                p.LengthMeters >= template.LengthMeters && !used.Contains(p.Id));
            if (plot != null) used.Add(plot.Id);
            q.Buildings.Add(new ColonyPlanningBuilding { Id = "building-" + q.Buildings.Count.ToString(CultureInfo.InvariantCulture), Role = role, TemplateId = template.Id,
                TemplateHash = template.Hash, Name = template.Name, PlotId = plot == null ? "" : plot.Id, Homes = template.Homes, Funds = template.BuildFunds,
                LaborSeconds = template.LaborSeconds, Materials = template.Materials.Concat(template.EmbeddedContents).GroupBy(m => m.Resource, StringComparer.Ordinal)
                    .Select(g => new MaterialRequirement { Resource = g.Key, Amount = checked(g.Sum(m => m.Amount)) }).OrderBy(m => m.Resource, StringComparer.Ordinal).ToList() });
        }

        static string ExecutePlanning(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            switch (command.Kind)
            {
                case "approveFoundingPlan": return ApprovePlanning(state, command, env, QuoteFoundingPlan(state, command.ColonyId, env,command.FoundingIntent));
                case "approveGrowthPlan": return ApprovePlanning(state, command, env, QuoteGrowthPlan(state, command.ColonyId, env));
                case "approveGrowthProposal":
                case "deferGrowthProposal":
                case "rejectGrowthProposal":
                case "reconsiderGrowthProposal": return ExecuteGrowthProposal(state,command,env);
                case "cancelColonyPlan": return CancelPlanning(state, command);
                case "configureReorderPolicy": return ConfigureReorder(state, command, env);
                case "surveyFoundingPlan":
                case "surveyGrowthPlan": return RegisterPlanningSurveys(state, command, env);
                default: throw new InvalidDataException("Unknown planning command.");
            }
        }

        static string ApprovePlanning(ColonyState state, ColonyCommand command, ColonyEnvironment env, ColonyPlanningQuote quote)
        {
            if(quote.Kind=="growth")GuardDirectGrowthApproval(state,command,env);
            if (!quote.CanApprove) throw new InvalidDataException(string.Join(" ", quote.Blockers));
            if (command.QuoteId != quote.Id) throw new InvalidDataException("Founding/growth quote changed. Review the exact current itemized plan.");
            if (state.Plans.Count >= 64) throw new InvalidDataException("Saved plan capacity reached.");
            var colony = Colony(state, command.ColonyId);
            var progress = ColonyJson.Deserialize<ColonyPlanningQuote>(ColonyJson.Serialize(quote,ColonyLimits.MaxBytes),ColonyLimits.MaxBytes);
            var plan = new ColonyPlan { Id = command.OperationId, ColonyId = colony.Id, CreatedUt = env.Ut, Quote = quote, RemainingFunds = quote.TotalFunds,
                Buildings = progress.Buildings, Imports = progress.Imports, Incoming = progress.ExistingIncoming,
                Workers = quote.BootstrapWorkers.Select(w=>new ColonyPlanningWorkerClaim {Id=w.Id}).ToList(),
                Residents = quote.Residents.Select(r=>new ColonyPlanningResidentClaim {Id=r.Id}).ToList(), Production=CreatePlanningProductionClaims(quote) };
            foreach (var material in PlanningMaterialRequirements(quote))
            {
                var stock = colony.Stock.SingleOrDefault(s => s.Resource == material.Resource);
                long floor = material.Resource == "Supplies" ? Math.Max(quote.StartupSupportReserve, stock == null ? 0 : stock.SupportFloor) : stock == null ? 0 : stock.SupportFloor;
                long reserved = stock == null ? 0 : Math.Min(material.Amount, Math.Max(0, stock.Amount - stock.Reserved - floor));
                if (stock != null) stock.Reserved += reserved;
                plan.Claims.Add(new ColonyPlanningMaterialClaim { Resource = material.Resource, Remaining = material.Amount, Reserved = reserved });
            }
            var supplies = colony.Stock.SingleOrDefault(s => s.Resource == "Supplies");
            plan.PreviousSupportFloor = supplies == null ? 0 : supplies.SupportFloor;
            if (supplies != null) supplies.SupportFloor = Math.Max(supplies.SupportFloor, quote.StartupSupportReserve);
            foreach (var building in plan.Buildings) colony.Plots.Single(p => p.Id == building.PlotId).ReservedBy = plan.Id;
            state.Plans.Add(plan);
            Log(state, env.Ut, colony.Id, plan.Id, "planApproved", "Exact quote reserved future funding, finite supplier inventory, owned material and surveyed plots.");
            return plan.Id;
        }

        static string CancelPlanning(ColonyState state, ColonyCommand command)
        {
            var plan = state.Plans.SingleOrDefault(p => p.Id == command.TargetId && p.ColonyId == command.ColonyId) ?? throw new InvalidDataException("Plan not found.");
            if (!PlanningActive(plan)) return plan.Id;
            var colony = Colony(state, plan.ColonyId);
            foreach (var claim in plan.Claims) { if (claim.Reserved > 0) Stock(colony, claim.Resource).Reserved -= claim.Reserved; claim.Reserved = 0; claim.Remaining = 0; }
            foreach (var building in plan.Buildings.Where(b => b.OrderId.Length == 0)) colony.Plots.Single(p => p.Id == building.PlotId).ReservedBy = "";
            plan.RemainingFunds = 0; plan.State = "cancelled"; plan.Reason = "Future claims released. Paid/dispatch/construction child operations retain their original terms and continue independently.";
            foreach(var worker in plan.Workers.Where(w=>w.State=="planned"))worker.State="cancelled";
            CancelPlanningResidents(plan);
            CancelPlanningProduction(plan);
            var supplies = colony.Stock.SingleOrDefault(s=>s.Resource=="Supplies");
            if(supplies!=null) supplies.SupportFloor=Math.Max(plan.PreviousSupportFloor,colony.SupportCommissionedUt.HasValue?checked((long)decimal.Ceiling((decimal)Math.Max(colony.Charter.PopulationTarget,colony.Residents.Count(r=>r.Status!="missing")+colony.VisitorRosterIds.Count)*colony.SupportMicroUnitsPerPersonDay*(decimal)colony.Charter.ReserveDays)):0);
            Log(state, state.SimulatedUt, colony.Id, command.OperationId, "planCancelled", plan.Reason);
            return plan.Id;
        }

        // Exactly one conversion per call. Every conversion occurs on a detached
        // state; a failed legacy reserve operation cannot leak released claims.
        public static ColonyState RunPlanning(ColonyState prior, ColonyEnvironment env, string? afterPlanId = null)
        {
            ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, env);
            return RunPlanningValidated(prior, env, afterPlanId);
        }

        // One synchronous domain-owned phase shares its input validation. Every
        // changed transition still validates its detached result before return;
        // no caller callback or native side effect runs between these stages.
        public static ColonyState RunPlanningAndProcurement(ColonyState prior, ColonyEnvironment env, string? afterPlanId = null)
        {
            ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, env);
            var next = RunPlanningValidated(prior, env, afterPlanId);
            next = RunPlanningPoliciesValidated(next, env);
            next = RunPhysicalProcurementPoliciesValidated(next, env);
            return RunProductionOperatingTransfersValidated(next, env);
        }

        static ColonyState RunPlanningValidated(ColonyState prior, ColonyEnvironment env, string? afterPlanId)
        {
            ValidateEnvironment(prior, env);
            if (prior.Effects.Any(e => e.State == "applying" || e.State == "held") || prior.SimulatedUt < env.Ut || env.Ut < prior.SimulatedUt) return prior;
            var workers = ReconcileTerminalPlanningWorkers(prior);if(workers!=null)return workers;
            var residents = ReconcileTerminalPlanningResidents(prior);if(residents!=null)return residents;
            var candidates = prior.Plans.Where(PlanningActive).OrderBy(p => p.CreatedUt).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            int cursor = candidates.FindIndex(p => p.Id == afterPlanId); if (cursor >= 0) candidates = candidates.Skip(cursor + 1).Concat(candidates.Take(cursor + 1)).ToList();
            foreach (var saved in candidates)
            {
                var state = ColonyStateCodec.Copy(prior); var plan = state.Plans.Single(p => p.Id == saved.Id); var colony = Colony(state, plan.ColonyId);
                try
                {
                    var supplies = colony.Stock.SingleOrDefault(s => s.Resource == "Supplies");
                    if (supplies != null) supplies.SupportFloor = Math.Max(supplies.SupportFloor, plan.Quote.StartupSupportReserve);
                    if (plan.Quote.StagingPolicyId.Length > 0 && colony.Logistics.State == "none")
                    {
                        plan.RemainingFunds -= plan.Quote.StagingFunds; plan.StagingFundsTransferred = true;
                        ActivateLogistics(state, PlanningCommand(state, env, plan, "stage", "activateLogistics", new Dictionary<string,string> {
                            ["PolicyId"] = plan.Quote.StagingPolicyId, ["PolicyHash"] = plan.Quote.StagingPolicyHash,
                            ["QuotedFunds"] = plan.Quote.StagingFunds.ToString(CultureInfo.InvariantCulture) }), env);
                        plan.State = "procuring"; plan.Reason = "Paid empty staging contract reserved; actual payment and delivery are required.";
                        return FinishPlanningTransition(state);
                    }
                    if (plan.Quote.StagingPolicyId.Length > 0 && !plan.StagingFundsTransferred && colony.Logistics.State != "none")
                    {
                        if (colony.Logistics.PolicyHash != plan.Quote.StagingPolicyHash) throw new InvalidDataException("The separately activated staging contract differs from the reviewed founding plan.");
                        plan.RemainingFunds -= plan.Quote.StagingFunds; plan.StagingFundsTransferred = true;
                        return FinishPlanningTransition(state);
                    }
                    if (colony.Logistics.State != "operational") { SetPlanningReason(plan, "Waiting for actual paid staging delivery."); return FinishPlanningIfChanged(prior, state); }
                    if (RunPlanningWorker(state,plan,env))return FinishPlanningTransition(state);
                    foreach (var incoming in plan.Incoming.Where(i => !i.Credited))
                    {
                        var shipment = state.Shipments.Single(s => s.Id == incoming.ShipmentId);
                        if (shipment.State == "cancelled" || shipment.State == "held") throw new InvalidDataException("An assigned shipment was cancelled or held; review remaining procurement.");
                    }
                    var load = plan.Imports.FirstOrDefault(i => i.ShipmentId.Length == 0 && PlanningImportAmount(i)>0);
                    if (load != null)
                    {
                        if(TryReserveLocalShortage(state,colony,load.Resource,PlanningImportAmount(load),env,plan.Id))
                        {plan.State="procuring";plan.Reason="Verified accessible physical warehouse stock reserved before paid imports; exact provider receipt required.";return FinishPlanningIfChanged(prior,state);}
                        var supplier = state.Suppliers.SingleOrDefault(s => s.Id == load.SupplierId) ?? throw new InvalidDataException("Quoted supplier is unavailable.");
                        if (PlanningSupplierTermsHash(supplier) != load.SupplierTermsHash) throw new InvalidDataException("Quoted supplier terms changed; no substitute price or timing was accepted.");
                        // Remove the future claim before replacing it with a real
                        // shipment; all other plans' claims remain protected.
                        long purchaseAmount=PlanningImportAmount(load),purchaseFunds=PlanningImportFunds(load);
                        load.ShipmentId = PlanningChildId(plan.Id, load.Id); plan.RemainingFunds -= purchaseFunds;
                        ReserveImport(state, PlanningCommand(state, env, plan, load.Id, "approveTrade", new Dictionary<string,string> {
                            ["SupplierId"] = load.SupplierId, ["AmountMicroUnits"] = purchaseAmount.ToString(CultureInfo.InvariantCulture), ["QuotedFunds"] = purchaseFunds.ToString(CultureInfo.InvariantCulture) }), env);
                        var claim = plan.Claims.SingleOrDefault(c => c.Resource == load.Resource);
                        long promised = plan.Incoming.Where(i => !i.Credited && i.Resource == load.Resource).Sum(i => i.MaterialAmount)+PhysicalMaterialPromised(state,plan.Id,load.Resource);
                        long material = claim == null ? 0 : Math.Min(purchaseAmount, Math.Max(0, claim.Remaining - claim.Reserved - promised));
                        plan.Incoming.Add(new ColonyPlanningIncomingClaim { ShipmentId = load.ShipmentId, Resource = load.Resource, Amount = purchaseAmount, MaterialAmount = material });
                        plan.State = "procuring"; plan.Reason = "One quoted freight load reserved; shared fleet and actual funds readback govern dispatch.";
                        return FinishPlanningTransition(state);
                    }
                    foreach (var building in plan.Buildings.Where(b => b.OrderId.Length == 0))
                    {
                        if (building.Dependencies.Any(id => { var b = plan.Buildings.Single(x => x.Id == id); return b.OrderId.Length == 0 || state.Construction.Single(o => o.Id == b.OrderId).State != "operational"; })) continue;
                        if (building.Materials.Any(m => plan.Claims.Single(c => c.Resource == m.Resource).Reserved < m.Amount)) continue;
                        foreach (var material in building.Materials)
                        { var claim = plan.Claims.Single(c => c.Resource == material.Resource); claim.Reserved -= material.Amount; claim.Remaining -= material.Amount; Stock(colony, material.Resource).Reserved -= material.Amount; }
                        var plot = colony.Plots.Single(p => p.Id == building.PlotId); if (plot.ReservedBy != plan.Id || plot.OccupiedBy.Length > 0) throw new InvalidDataException("Plan's surveyed plot ownership changed.");
                        plot.ReservedBy = ""; plan.RemainingFunds -= building.Funds;
                        building.OrderId = ReserveConstruction(state, PlanningCommand(state, env, plan, building.Id, "approveConstruction", new Dictionary<string,string> {
                            ["TemplateId"] = building.TemplateId, ["TemplateHash"] = building.TemplateHash, ["PlotId"] = building.PlotId }), env);
                        var order = state.Construction.Single(o => o.Id == building.OrderId);
                        order.Dependencies = building.Dependencies.Select(id => plan.Buildings.Single(b => b.Id == id).OrderId).ToList();
                        plan.State = "constructing"; plan.Reason = "Real owned materials and escrow transferred to dependency construction; physical placement and commissioning remain required.";
                        return FinishPlanningTransition(state);
                    }
                    if (plan.Buildings.Any(b => b.OrderId.Length > 0 && state.Construction.Single(o => o.Id == b.OrderId).State == "cancelled")) throw new InvalidDataException("A planned construction child was cancelled; cancel/review the parent plan before replacement.");
                    if(RunPlanningProduction(state,plan,env))return FinishPlanningTransition(state);
                    bool operational = plan.Workers.All(w=>w.State=="complete") && plan.Buildings.All(b => b.OrderId.Length > 0 && state.Construction.Single(o => o.Id == b.OrderId).State == "operational");
                    if(operational && ReleasePlanningStartupReserves(state,plan))return FinishPlanningTransition(state);
                    if (operational && plan.Quote.Kind == "founding" && !colony.SupportCommissionedUt.HasValue)
                    {
                        var command = PlanningCommand(state, env, plan, "support", "commissionSupport", new Dictionary<string,string> {
                            ["PolicyId"] = env.Support.PolicyId, ["PolicyHash"] = env.Support.PolicyHash, ["QuotedReserveMicroUnits"] = SupportReserveQuote(colony, env).ToString(CultureInfo.InvariantCulture) });
                        if (env.Support.PolicyHash != plan.Quote.SupportPolicyHash) throw new InvalidDataException("Reviewed support policy changed; no commissioning under substituted terms.");
                        CommissionSupport(state, command, env);
                        Stock(colony,"Supplies").SupportFloor=Math.Max(Stock(colony,"Supplies").SupportFloor,plan.Quote.StartupSupportReserve);
                        if(plan.Quote.FoundingIntent!=null)return FinishPlanningTransition(state);
                    }
                    if (operational)
                    {
                        if(plan.Quote.StartupPolicies!=null&&!plan.StartupPoliciesApplied)
                        {ApplyPlanningStartupPolicies(state,plan,env);return FinishPlanningTransition(state);}
                        if(RunPlanningResident(state,plan,env))return FinishPlanningTransition(state);
                        if(plan.Residents.Any(r=>r.State!="complete"))
                        {plan.Reason="Waiting for the exact reviewed named passenger arrivals and home receipts.";return FinishPlanningIfChanged(prior,state);}
                        if(plan.Production.Any(p=>p.State!="operational"))
                        {plan.Reason="Waiting for reviewed native production activation and actual productive input/power/worker/background evidence; full import downside remains funded.";return FinishPlanningIfChanged(prior,state);}
                        if (plan.Quote.Kind == "growth")
                        {
                            if (QualifiedPlanningHomes(colony,env) < plan.Quote.TargetPopulation || plan.Quote.TargetPopulation > colony.Charter.ResidentLimit ||
                                Stock(colony,"Supplies").Amount-Stock(colony,"Supplies").Reserved < plan.Quote.StartupSupportReserve)
                                throw new InvalidDataException("Growth needs actual qualified homes and owned support reserves before increasing admission target.");
                            colony.Charter.PopulationTarget=Math.Max(colony.Charter.PopulationTarget,plan.Quote.TargetPopulation);
                        }
                        plan.State = "complete"; plan.Reason = "All real buildings operational; founding support commissioned where required."; return FinishPlanningTransition(state);
                    }
                    SetPlanningReason(plan, plan.Buildings.Any(b => b.OrderId.Length > 0 && state.Construction.Single(o => o.Id == b.OrderId).State == "awaitingPlacement") ?
                        "Ready for real placement: load the site at 1× for assembly, anchor and native commissioning." : "Waiting for actual cargo arrivals, operational dependencies or qualified construction labor.");
                    return FinishPlanningIfChanged(prior, state);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException || ex is ArgumentException)
                {
                    // Discard attempted conversions before persisting a blocker.
                    state = ColonyStateCodec.Copy(prior); plan = state.Plans.Single(p => p.Id == saved.Id);
                    SetPlanningReason(plan, ex.Message.Length > 512 ? ex.Message.Substring(0,512) : ex.Message);
                    return FinishPlanningIfChanged(prior, state);
                }
            }
            return RunReordersAndGrowth(prior, env);
        }

        static ColonyState FinishPlanningTransition(ColonyState state) { state.Revision++; CompactTerminalEffects(state); ColonyStateCodec.Serialize(state); return state; }
        static ColonyState FinishPlanningIfChanged(ColonyState prior, ColonyState next) => ColonyStateCodec.Hash(ColonyStateCodec.Serialize(prior)) == ColonyStateCodec.Hash(ColonyStateCodec.Serialize(next)) ? prior : FinishPlanningTransition(next);
        static void SetPlanningReason(ColonyPlan plan, string reason) { plan.Reason = reason; }
        static bool PlanningActive(ColonyPlan p) => p.State != "cancelled" && p.State != "complete";
        public static long PlanningCommittedFunds(ColonyState state, string? colonyId = null) => state.Plans.Where(p => PlanningActive(p) && (colonyId == null || p.ColonyId == colonyId)).Sum(p => p.RemainingFunds);
        public static long PlanningSupplierReserved(ColonyState state, string supplierId) => state.Plans.Where(PlanningActive).SelectMany(p => p.Imports).Where(i => i.SupplierId == supplierId && i.ShipmentId.Length == 0).Sum(PlanningImportAmount);
        public static long PlanningMaterialReserved(ColonyState state, string colonyId, string resource) => state.Plans.Where(p => p.ColonyId == colonyId && PlanningActive(p)).SelectMany(p => p.Claims).Where(c => c.Resource == resource).Sum(c => c.Reserved);
        public static long PlanningSupportFloor(ColonyState state, string colonyId) => state.Plans.Where(p=>p.ColonyId==colonyId&&PlanningActive(p)).Select(p=>p.Quote.StartupSupportReserve).DefaultIfEmpty(0).Max();
        public static long PlanningIncomingReserved(ColonyState state, string shipmentId) => state.Plans.Where(PlanningActive).SelectMany(p => p.Incoming).Where(c => c.ShipmentId == shipmentId && !c.Credited).Sum(c => c.Amount);
        public static long PendingCash(ColonyState state) => checked(state.Effects.Where(e => e.FundsDelta < 0 && (e.State == "prepared" || e.State == "applying" || e.State == "held")).Sum(e => -e.FundsDelta) + PlanningCommittedFunds(state));
        public static long PlanningMaximumBatch(ColonySupplier supplier, ColonyStock stock)
        {
            if (supplier.ConcurrentCapacity <= 0 || supplier.TravelSeconds <= 0) return 0;
            long mass = stock.UnitMassMicroTonnes == 0 ? ColonyLimits.MaxQuantity : checked((long)Math.Min(ColonyLimits.MaxQuantity, decimal.Floor((decimal)supplier.MassCapacityMicroTonnes * ColonyLimits.Units / stock.UnitMassMicroTonnes)));
            long volume = stock.UnitVolumeMilliLiters == 0 ? ColonyLimits.MaxQuantity : checked((long)Math.Min(ColonyLimits.MaxQuantity, decimal.Floor((decimal)supplier.VolumeCapacityMilliLiters * ColonyLimits.Units / stock.UnitVolumeMilliLiters)));
            return Math.Min(mass, volume);
        }
        public static string PlanningSupplierTermsHash(ColonySupplier s) => ColonyStateCodec.Hash(ColonyJson.Serialize(new ColonySupplier { Id = s.Id, Resource = s.Resource,
            DestinationBody = s.DestinationBody, FreightPoolId = s.FreightPoolId, FundsPerUnit = s.FundsPerUnit, FreightFunds = s.FreightFunds,
            TravelSeconds = s.TravelSeconds, ConcurrentCapacity = s.ConcurrentCapacity, MassCapacityMicroTonnes = s.MassCapacityMicroTonnes, VolumeCapacityMilliLiters = s.VolumeCapacityMilliLiters }, 65536));
        static string PlanningAuthorityHash(ColonyState state, ColonyRecord colony, ColonyEnvironment env)
        {
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(state.WorldId); w.Write(SiteKey(colony.Site)); w.Write(ColonyStateCodec.Hash(ColonyJson.Serialize(colony.Charter, 65536)));
                // A human review may outlive a support tick. Inventory, funding
                // and supplier availability are still read anew when rebuilding
                // the entire bill at approval; any changed import/material/plot
                // allocation changes that bill. Do not expire an unchanged bill
                // solely because surplus supplies or a temperature changed.
                foreach (var s in colony.Stock.OrderBy(s => s.Resource, StringComparer.Ordinal)) { w.Write(s.Resource); w.Write(s.Capacity); w.Write(s.SupportFloor); }
                foreach (var f in colony.Facilities.OrderBy(f => f.Id, StringComparer.Ordinal))
                { w.Write(f.Id); w.Write(f.VesselId); w.Write(f.TemplateId); w.Write(f.TemplateHash); w.Write(f.State == "retired"); w.Write(f.PartIds.Count); foreach (uint id in f.PartIds.OrderBy(id => id)) w.Write(id); }
                foreach (var a in env.Planning.Assets.Where(a => a.ColonyId == colony.Id).OrderBy(a => a.FacilityId, StringComparer.Ordinal).ThenBy(a => a.Role, StringComparer.Ordinal))
                { w.Write(a.FacilityId); w.Write(a.Role); w.Write(a.IdentityHash); w.Write(a.Qualified && a.ContextKey == env.ContextKey && a.Witness.Length > 0); w.Write(a.Homes); }
                w.Flush(); return ColonyStateCodec.Hash(stream.ToArray());
            }
        }
        public static string PlanningChildId(string planId, string line)
        { var bytes = Encoding.UTF8.GetBytes(planId + "\n" + line); var hash = ColonyStateCodec.Hash(bytes); var guid = new byte[16]; for(int i=0;i<16;i++) guid[i]=byte.Parse(hash.Substring(i*2,2),NumberStyles.HexNumber,CultureInfo.InvariantCulture); return new Guid(guid).ToString("D"); }
        static ColonyCommand PlanningCommand(ColonyState state, ColonyEnvironment env, ColonyPlan plan, string line, string kind, Dictionary<string,string> fields) =>
            new ColonyCommand { OperationId = PlanningChildId(plan.Id, line), ColonyId = plan.ColonyId, Kind = kind, ContextKey = env.ContextKey, ExpectedRevision = state.Revision, Fields = fields };
        static void ClaimPlanningArrival(ColonyState state, ColonyShipment shipment)
        {
            foreach (var plan in state.Plans.Where(p => p.ColonyId == shipment.ColonyId && PlanningActive(p))) foreach (var incoming in plan.Incoming.Where(i => i.ShipmentId == shipment.Id && !i.Credited))
            {
                incoming.Credited = true;
                if (incoming.MaterialAmount == 0) continue;
                var claim = plan.Claims.Single(c => c.Resource == incoming.Resource);
                if (incoming.MaterialAmount > claim.Remaining - claim.Reserved) throw new InvalidDataException("Incoming plan material claim exceeds its immutable requirement.");
                claim.Reserved += incoming.MaterialAmount; Stock(Colony(state, plan.ColonyId), incoming.Resource).Reserved += incoming.MaterialAmount;
            }
        }
    }
}
