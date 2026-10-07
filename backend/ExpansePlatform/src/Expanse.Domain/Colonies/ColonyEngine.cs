using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyEnvironment
    {
        public string ContextKey { get; set; } = "";
        public string WorldId { get; set; } = "";
        public double Ut { get; set; }
        public long AvailableFunds { get; set; }
        public List<ColonyTemplate> Templates { get; set; } = new List<ColonyTemplate>();
        public List<string> UnlockedTech { get; set; } = new List<string>();
        public Dictionary<string, int> BuildersByColony { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, ColonyConstructionLaborWitness> ConstructionLaborByOrder { get; set; } = new Dictionary<string, ColonyConstructionLaborWitness>();
        public Dictionary<string, ColonyConstructionRecoveryWitness> ConstructionRecovery { get; set; } = new Dictionary<string, ColonyConstructionRecoveryWitness>();
        // These are fresh validated observations from the adapter, not client-supplied fields.
        public List<ColonyFacility> AdoptableFacilities { get; set; } = new List<ColonyFacility>();
        public Dictionary<string, ColonySite> FacilitySites { get; set; } = new Dictionary<string, ColonySite>();
        public Dictionary<string, double> BodyRadiiMeters { get; set; } = new Dictionary<string, double>();
        public bool DevelopmentMode { get; set; }
        public ColonyPeopleEnvironment People { get; set; } = new ColonyPeopleEnvironment();
        public ColonyWolfEnvironment Wolf { get; set; } = new ColonyWolfEnvironment();
        public ColonyServicesEnvironment Services { get; set; } = new ColonyServicesEnvironment();
        public List<ColonyEconomyPolicy> EconomyPolicies { get; set; } = new List<ColonyEconomyPolicy>();
        public ColonyPlot? SurveyedPlot { get; set; }
        public string SurveyFailure { get; set; } = "";
        public ColonySupportEnvironment Support { get; set; } = new ColonySupportEnvironment();
        public ColonyPlanningEnvironment Planning { get; set; } = new ColonyPlanningEnvironment();
        public ColonyProductionEnvironment Production { get; set; } = new ColonyProductionEnvironment();
    }

    public sealed class ColonyResult
    {
        public ColonyState State { get; set; } = new ColonyState();
        public string Outcome { get; set; } = "rejected";
        public string Reason { get; set; } = "";
        public string ResultId { get; set; } = "";
    }

    public static partial class ColonyEngine
    {
        public static ColonyState Create(string worldId, double ut)
        {
            var state = new ColonyState { WorldId = worldId, SimulatedUt = ut, TargetUt = ut };
            ColonyStateCodec.Validate(state); return state;
        }

        // Each command transitions a copy. Exceptions cannot leave partial reservations
        // in the authoritative state. External effects are queued, never performed here.
        public static ColonyResult Execute(ColonyState prior, ColonyCommand command, ColonyEnvironment environment)
        {
            try
            {
                ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, environment);
                ColonyStateCodec.Id(command.OperationId);
                if (command.ContextKey != environment.ContextKey) throw new InvalidDataException("Save/load context changed. Refresh before submitting.");
                string hash = ColonyStateCodec.CommandHash(command);
                var existing = prior.Receipts.Find(x => x.OperationId == command.OperationId);
                if (existing != null)
                {
                    if (existing.PayloadHash != hash) throw new InvalidDataException("Operation ID was reused with a different request.");
                    return new ColonyResult { State = prior, Outcome = "duplicate", ResultId = existing.ResultId, Reason = "Previously accepted operation; effect was not repeated." };
                }
                if (command.ExpectedRevision != prior.Revision) throw new InvalidDataException("Colony state changed. Recheck the plan before committing.");
                string retryReason;
                bool retryOwnPlacement = command.Kind == "retryConstructionPlacement" && CanRetryConstructionPlacement(prior, command.TargetId, environment, out retryReason);
                if (prior.Effects.Any(x => x.State == "applying" || x.State == "held") && !retryOwnPlacement && !(command.Kind == "replanPaidWolfSupply" && CanReplanPaidWolfSupply(prior, command.TargetId))) throw new InvalidDataException("An external effect needs reconciliation before further colony mutations.");
                var state = ColonyStateCodec.Copy(prior);
                string result;
                switch (command.Kind)
                {
                    case "foundColony": result = Found(state, command, environment); break;
                    case "adoptFacility": result = AdoptFacility(state, command, environment); break;
                    case "qualifyAdoptedHabitat": result = QualifyAdoptedHabitat(state, command, environment); break;
                    case "updateCharter": result = UpdateCharter(state, command, environment); break;
                    case "activateLogistics": result = ActivateLogistics(state, command, environment); break;
                    case "commissionSupport": result = CommissionSupport(state, command, environment); break;
                    case "surveyPlot": result = RegisterSurveyedPlot(state, command, environment); break;
                    case "approveConstruction": result = ReserveConstruction(state, command, environment); break;
                    case "cancelConstruction": result = CancelConstruction(state, command); break;
                    case "retryConstructionPlacement": result = AuthorizeConstructionRetry(state, command, environment); break;
                    case "approveTrade": result = ReserveImport(state, command, environment); break;
                    case "cancelTrade": result = CancelTrade(state, command); break;
                    case "approveWolfSupply":
                    case "approveWolfSupplies":
                    case "replanPaidWolfSupply":
                    case "cancelWolfSupply": result = ExecuteWolf(state, command, environment); break;
                    case "approveFoundingPlan":
                    case "approveGrowthPlan":
                    case "approveGrowthProposal":
                    case "deferGrowthProposal":
                    case "rejectGrowthProposal":
                    case "reconsiderGrowthProposal":
                    case "cancelColonyPlan":
                    case "configureReorderPolicy":
                    case "surveyFoundingPlan":
                    case "surveyGrowthPlan": result = ExecutePlanning(state, command, environment); break;
                    case "transferColonyStock":
                    case "configurePhysicalProcurement":
                    case "cancelPhysicalTransfer": result = ExecutePhysicalProcurement(state, command, environment); break;
                    default: result = ExecutePeople(state, command, environment); break;
                }
                state.Revision = checked(state.Revision + 1);
                state.Receipts.Add(new ColonyReceipt { OperationId = command.OperationId, PayloadHash = hash,
                    Sequence = state.NextSequence++, Revision = state.Revision, ResultId = result });
                // An evicted request still carries its original ExpectedRevision. It
                // cannot be replayed after compaction; reload also changes ContextKey.
                while (state.Receipts.Count > ColonyLimits.Receipts) state.Receipts.RemoveAt(0);
                CompactTerminalEffects(state);
                // Detach adoption/template observations: later observer refreshes must
                // not mutate accepted state without a serialized state transition.
                state = ColonyStateCodec.Copy(state);
                return new ColonyResult { State = state, Outcome = "accepted", ResultId = result };
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is OverflowException || ex is FormatException)
            { return new ColonyResult { State = prior, Reason = ex.Message }; }
        }

        static string Found(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = new ColonyRecord { Id = command.OperationId, Name = Field(command, "Name"), FoundedUt = env.Ut, SupportAccountedUt = env.Ut,
                Site = new ColonySite { Body = Field(command, "Body"), Biome = Field(command, "Biome", ""), Latitude = Number(command, "Latitude"), Longitude = Number(command, "Longitude"), RadiusMeters = Number(command, "RadiusMeters", 400) },
                Charter = ReadCharter(command) };
            ColonyStateCodec.Site(colony.Site); ColonyStateCodec.Charter(colony.Charter);
            if (colony.Charter.Sandbox && !env.DevelopmentMode) throw new InvalidDataException("The explicit package-certification override requires an authorized isolated development context.");
            if (!env.BodyRadiiMeters.TryGetValue(colony.Site.Body, out var radius) || radius <= 0) throw new InvalidDataException("The selected celestial body is unavailable.");
            if (state.Colonies.Any(x => x.Site.Body == colony.Site.Body && SurfaceDistance(x.Site, colony.Site, radius) < x.Site.RadiusMeters + colony.Site.RadiusMeters))
                throw new InvalidDataException("This site overlaps another registered colony. Expand that colony or choose a separate site.");
            // Coordinates alone cannot grant adopted assets or certify work seats as homes.
            string ids = Field(command, "AdoptFacilityIds", "");
            foreach (string id in ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Distinct(StringComparer.Ordinal))
            {
                var facility = env.AdoptableFacilities.SingleOrDefault(x => x.Id == id);
                if (facility == null || !env.FacilitySites.TryGetValue(id, out var site) || site.Body != colony.Site.Body) throw new InvalidDataException("Selected asset is no longer available for adoption.");
                if (state.Colonies.Any(x => x.Facilities.Any(f => f.Id == id || f.VesselId == facility.VesselId))) throw new InvalidDataException("Selected asset already belongs to a colony.");
                if (SurfaceDistance(site, colony.Site, radius) > colony.Site.RadiusMeters) throw new InvalidDataException("Selected asset lies outside the colony boundary; use an explicit inter-site delivery.");
                colony.Facilities.Add(facility);
            }
            if (state.Colonies.Count >= ColonyLimits.Colonies) throw new InvalidDataException("Colony limit reached.");
            state.Colonies.Add(colony);
            Log(state, env.Ut, colony.Id, command.OperationId, "founded", "Charter registered; support begins only after commissioning.");
            return colony.Id;
        }

        static string UpdateCharter(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId); var charter = ReadCharter(command);
            if (charter.Sandbox && !env.DevelopmentMode) throw new InvalidDataException("The explicit package-certification override requires an authorized isolated development context.");
            ColonyStateCodec.Charter(charter);
            if (colony.Residents.Count > charter.ResidentLimit || colony.VisitorRosterIds.Count > charter.VisitorLimit) throw new InvalidDataException("Charter limits cannot discard existing people.");
            long commitments = CommittedFunds(state, colony.Id);
            if (charter.SpendingLimit < colony.SpentFunds + commitments) throw new InvalidDataException("Spending limit is below existing obligations.");
            colony.Charter = charter; return colony.Id;
        }

        static string ReserveConstruction(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId);
            var template = env.Templates.SingleOrDefault(x => x.Id == Field(command, "TemplateId")) ?? throw new InvalidDataException("Building package is unavailable.");
            ColonyStateCodec.Funds(template.LaborFunds);
            if (template.LaborFunds <= 0 || template.LaborFunds > template.BuildFunds) throw new InvalidDataException("Building quote requires explicit construction labor funds included in its escrow.");
            if ((!template.RuntimeCertified || string.IsNullOrWhiteSpace(template.CertificationEvidence)) && !(env.DevelopmentMode && colony.Charter.Sandbox)) throw new InvalidDataException("Building package has not passed runtime certification.");
            if (Field(command, "TemplateHash") != template.Hash) throw new InvalidDataException("Building package changed. Obtain a new quote.");
            if (!colony.Charter.Sandbox && template.RequiredTech.Any(x => !env.UnlockedTech.Contains(x))) throw new InvalidDataException("Building requires technology that is not unlocked.");
            var plot = colony.Plots.SingleOrDefault(x => x.Id == Field(command, "PlotId")) ?? throw new InvalidDataException("Surveyed plot is unavailable.");
            if (plot.SurveyHash.Length == 0 || plot.ReservedBy.Length > 0 || plot.OccupiedBy.Length > 0 || plot.WidthMeters < template.WidthMeters || plot.LengthMeters < template.LengthMeters) throw new InvalidDataException("Plot is occupied, reserved, unsurveyed or too small for the deployment envelope.");
            CheckFunds(state, colony, template.BuildFunds, env);
            ColonyStateCodec.Materials(template.Materials); ColonyStateCodec.Materials(template.EmbeddedContents);
            var requirements = template.Materials.Concat(template.EmbeddedContents).GroupBy(x => x.Resource, StringComparer.Ordinal)
                .Select(x => new MaterialRequirement { Resource = x.Key, Amount = x.Sum(y => y.Amount) }).ToList();
            foreach (var material in requirements)
            {
                var stock = Stock(colony, material.Resource);
                if (stock.Amount - stock.Reserved - stock.SupportFloor < material.Amount) throw new InvalidDataException("Insufficient unreserved " + material.Resource + "; support reserves are protected.");
                stock.Reserved = checked(stock.Reserved + material.Amount);
            }
            var order = new ConstructionOrder { Id = command.OperationId, ColonyId = colony.Id, PlotId = plot.Id, TemplateId = template.Id, TemplateHash = template.Hash,
                Funds = template.BuildFunds, LaborFunds = template.LaborFunds, WorkRequired = template.LaborSeconds, Materials = requirements, AccountedUt = env.Ut };
            state.Construction.Add(order); plot.ReservedBy = order.Id;
            state.Effects.Add(FundsEffect(command, order.Id, -order.Funds, "constructionEscrow"));
            return order.Id;
        }

        static string CancelConstruction(ColonyState state, ColonyCommand command)
        {
            var order = state.Construction.SingleOrDefault(x => x.Id == command.TargetId && x.ColonyId == command.ColonyId) ?? throw new InvalidDataException("Construction order not found.");
            if (order.State == "cancelled") return order.Id;
            if (order.State == "placing" || order.State == "commissioning" || order.State == "operational" || order.FacilityId.Length > 0 || order.Placement.OperationId.Length > 0 || order.Placement.AssemblyAttempted) throw new InvalidDataException("A persisted physical placement requires provider reconciliation and a separate occupant-safe removal plan.");
            var colony = Colony(state, order.ColonyId);
            foreach (var effect in state.Effects.Where(x => x.TargetId == order.Id && x.State == "prepared")) effect.State = "cancelled";
            if (!order.MaterialsConsumed) foreach (var material in order.Materials) Stock(colony, material.Resource).Reserved -= material.Amount;
            order.State = "cancelled"; order.Reason = "Unconsumed materials released; only unspent escrow is refundable.";
            colony.Plots.Single(x => x.Id == order.PlotId).ReservedBy = "";
            long refund = order.FundsPaid ? order.Funds - order.FundsConsumed : 0;
            if (refund > 0) state.Effects.Add(FundsEffect(command, order.Id, refund, "constructionRefund"));
            return order.Id;
        }

        static string ReserveImport(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId); var supplier = state.Suppliers.SingleOrDefault(x => x.Id == Field(command, "SupplierId")) ?? throw new InvalidDataException("Supplier is unavailable.");
            if (supplier.DestinationBody.Length > 0 && supplier.DestinationBody != colony.Site.Body) throw new InvalidDataException("This supplier route does not serve the colony body.");
            long amount = Integer(command, "AmountMicroUnits"); ColonyStateCodec.Quantity(amount);
            if (amount <= 0) throw new InvalidDataException("Shipment quantity must be positive.");
            var stock = Stock(colony, supplier.Resource);
            if (amount > supplier.Available - supplier.Reserved - PlanningSupplierReserved(state, supplier.Id)) throw new InvalidDataException("Supplier stock is insufficient or committed to reviewed colony plans.");
            if (amount > stock.Capacity - stock.Amount - stock.IncomingReserved) throw new InvalidDataException("Receiving storage is full or reserved for another shipment.");
            var pool = state.Suppliers.Where(x => x.Id == supplier.Id || supplier.FreightPoolId.Length > 0 && x.FreightPoolId == supplier.FreightPoolId).Select(x => x.Id).ToArray();
            int active = state.Shipments.Count(x => pool.Contains(x.SupplierId) && (x.State == "reserved" || x.State == "inTransit" || x.State == "held"));
            if (active >= supplier.ConcurrentCapacity) throw new InvalidDataException("Supplier freight capacity is fully committed.");
            long mass = ScaledProduct(amount, stock.UnitMassMicroTonnes), volume = ScaledProduct(amount, stock.UnitVolumeMilliLiters);
            if (mass > supplier.MassCapacityMicroTonnes || volume > supplier.VolumeCapacityMilliLiters) throw new InvalidDataException("Shipment exceeds freight mass or volume capacity.");
            long cost = checked(ScaledProduct(amount, supplier.FundsPerUnit) + supplier.FreightFunds);
            if (Integer(command, "QuotedFunds") != cost) throw new InvalidDataException("Import quote changed. Review the current price.");
            CheckFunds(state, colony, cost, env);
            var shipment = new ColonyShipment { Id = command.OperationId, ColonyId = colony.Id, SupplierId = supplier.Id, Resource = supplier.Resource,
                Amount = amount, Funds = cost, TravelSeconds = supplier.TravelSeconds, MassMicroTonnes = mass, VolumeMilliLiters = volume };
            state.Shipments.Add(shipment); stock.IncomingReserved += amount; supplier.Reserved += amount;
            state.Effects.Add(FundsEffect(command, shipment.Id, -cost, "importPurchase"));
            return shipment.Id;
        }

        static string CancelTrade(ColonyState state, ColonyCommand command)
        {
            var shipment = state.Shipments.SingleOrDefault(x => x.Id == command.TargetId && x.ColonyId == command.ColonyId) ?? throw new InvalidDataException("Shipment not found.");
            if (shipment.State == "cancelled") return shipment.Id;
            if (PlanningIncomingReserved(state, shipment.Id) > 0) throw new InvalidDataException("Shipment is assigned to an approved plan. Cancel that plan's future claims first.");
            if (shipment.State != "reserved") throw new InvalidDataException("Departed cargo cannot be cancelled as unspent stock.");
            Stock(Colony(state, shipment.ColonyId), shipment.Resource).IncomingReserved -= shipment.Amount;
            state.Suppliers.Single(x => x.Id == shipment.SupplierId).Reserved -= shipment.Amount;
            foreach (var effect in state.Effects.Where(x => x.TargetId == shipment.Id && x.State == "prepared")) effect.State = "cancelled";
            shipment.State = "cancelled"; shipment.Reason = "Reservation released before payment and dispatch.";
            return shipment.Id;
        }

        public static ColonyState MarkEffectApplying(ColonyState prior, string id, string provider, string beforeWitness)
        {
            var state = ColonyStateCodec.Copy(prior); var effect = state.Effects.Single(x => x.Id == id);
            if (effect.State != "prepared") throw new InvalidDataException("Effect is not prepared; reconcile before retrying.");
            if (beforeWitness.Length == 0 || provider.Length == 0) throw new InvalidDataException("External effect requires a provider and pre-mutation witness.");
            effect.State = "applying"; effect.Provider = provider; effect.BeforeWitness = beforeWitness;
            effect.Reason = "Mutation outcome has not been verified. A save in this state must reconcile it.";
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }

        public static ColonyState HoldEffect(ColonyState prior, string id, string reason)
        {
            var state = ColonyStateCodec.Copy(prior); var effect = state.Effects.Single(x => x.Id == id);
            if (effect.State == "applied" || effect.State == "cancelled") throw new InvalidDataException("Terminal effect cannot be held.");
            effect.State = "held"; effect.Reason = reason;
            HoldProductionEffect(state, effect, reason);
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }

        // Adapter supplies an exact readback. This method does not infer an outcome
        // merely because funds now happen to equal an intended total after a restart.
        public static ColonyState CompleteFundsEffect(ColonyState prior, string id, long before, long after, double ut, string witness)
        {
            var state = ColonyStateCodec.Copy(prior); var effect = state.Effects.Single(x => x.Id == id);
            if (effect.State == "applied") return prior;
            if (effect.State != "applying" || witness.Length == 0 || checked(after - before) != effect.FundsDelta) throw new InvalidDataException("Funds effect lacks an exact same-context readback.");
            ColonyStateCodec.Funds(before); ColonyStateCodec.Funds(after); ColonyStateCodec.Time(ut);
            var colony = Colony(state, effect.ColonyId);
            switch (effect.Kind)
            {
                case "constructionEscrow":
                    var order = state.Construction.Single(x => x.Id == effect.TargetId);
                    order.FundsPaid = true; order.State = "building"; order.AccountedUt = ut; order.Reason = "Awaiting qualified construction labor.";
                    break;
                case "constructionRefund": break;
                case "importPurchase":
                    var shipment = state.Shipments.Single(x => x.Id == effect.TargetId);
                    var supplier = state.Suppliers.Single(x => x.Id == shipment.SupplierId);
                    supplier.Available -= shipment.Amount; supplier.Reserved -= shipment.Amount;
                    shipment.State = "inTransit"; shipment.DepartUt = ut; shipment.ArrivalUt = ut + shipment.TravelSeconds; shipment.Reason = "";
                    colony.SpentFunds = checked(colony.SpentFunds + shipment.Funds);
                    break;
                default: if (!CompletePeopleFundsEffect(state, effect, ut) && !CompleteEconomyFundsEffect(state, effect, ut) && !CompleteWolfFundsEffect(state, effect, ut)) throw new InvalidDataException("Unrecognized funds effect."); break;
            }
            effect.State = "applied"; effect.AfterWitness = witness; effect.Reason = "Verified exact funds readback.";
            Log(state, ut, colony.Id, effect.OperationId, effect.Kind, effect.Reason, effect.FundsDelta);
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }

        public static ColonyState Advance(ColonyState prior, ColonyEnvironment env, int eventBudget = ColonyLimits.EventsPerTick)
            => Advance(prior, env, out _, eventBudget);

        // The immediate adapter commit may reuse these exact validated bytes.
        // Early held/applying returns retain the normal serialization fallback.
        public static ColonyState Advance(ColonyState prior, ColonyEnvironment env, out byte[]? preparedBytes, int eventBudget = ColonyLimits.EventsPerTick)
        {
            preparedBytes = null;
            ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, env);
            if (env.Ut < prior.SimulatedUt) throw new InvalidDataException("Game time went backward; reload the selected save state before simulation.");
            if (eventBudget < 1 || eventBudget > ColonyLimits.EventsPerTick) throw new ArgumentOutOfRangeException(nameof(eventBudget));
            var state = ColonyStateCodec.CopyAfterValidation(prior); state.TargetUt = env.Ut;
            if (state.Effects.Any(x => x.State == "applying" || x.State == "held")) return state;
            int events = 0;
            while (state.SimulatedUt < state.TargetUt && events++ < eventBudget)
            {
                double next = NextPeopleEvent(state, state.TargetUt);
                next = NextEconomyEvent(state, next);
                foreach (var shipment in state.Shipments.Where(x => x.State == "inTransit")) next = Math.Min(next, Math.Max(state.SimulatedUt, shipment.ArrivalUt));
                foreach (var order in state.Construction.Where(x => x.State == "building"))
                {
                    next = ConstructionCompletionBoundary(state, env, order, next);
                }
                foreach (var colony in state.Colonies.Where(x => x.SupportCommissionedUt.HasValue && !SupportOwnershipHeld(x, env)))
                {
                    long people = colony.Residents.Count(x => x.Status != "arriving" && x.Status != "missing") + colony.VisitorRosterIds.Count;
                    var supplies = colony.Stock.Find(x => x.Resource == "Supplies");
                    if (people > 0 && supplies != null && supplies.Amount > supplies.Reserved)
                    {
                        decimal rate = (decimal)people * colony.SupportMicroUnitsPerPersonDay / (decimal)ColonyLimits.KerbinDay;
                        if (rate > 0)
                        {
                            double boundary = Math.Max(state.SimulatedUt, colony.SupportAccountedUt) + (double)((supplies.Amount - supplies.Reserved - colony.SupportRemainder) / rate);
                            // At large UT the next micro-unit boundary can round back
                            // to the same double. Advance one representable instant.
                            if (boundary <= state.SimulatedUt) boundary = NextInstant(state.SimulatedUt);
                            next = Math.Min(next, boundary);
                        }
                    }
                }
                // Exhaustion and arrivals share the same boundary: consume the old
                // interval first, then replenish. No consumption occurs before adoption.
                foreach (var colony in state.Colonies) ConsumeSupport(state, colony, env, next);
                foreach (var order in state.Construction.Where(x => x.State == "building")) AdvanceWork(state, order, env, next);
                state.SimulatedUt = next;
                AdvanceEconomy(state, env, next);
                AdvancePeople(state, env, next);
                MaintainSupportReserves(state);
                AdvanceServices(state, env, next);
                AdvanceWolf(state, env, next);
                foreach (var shipment in state.Shipments.Where(x => x.State == "inTransit" && x.ArrivalUt <= next))
                {
                    var stock = Stock(Colony(state, shipment.ColonyId), shipment.Resource);
                    if (stock.Capacity - stock.Amount < shipment.Amount) { shipment.State = "held"; shipment.Reason = "Reserved destination capacity is no longer available."; continue; }
                    stock.IncomingReserved -= shipment.Amount; stock.Amount += shipment.Amount; stock.ImportedAmount += shipment.Amount;
                    shipment.State = "arrived"; shipment.Reason = "Cargo credited to colony-owned inventory.";
                    ClaimPlanningArrival(state, shipment);
                    Log(state, next, shipment.ColonyId, shipment.Id, "importArrival", shipment.Reason, 0, shipment.Resource, shipment.Amount);
                }
            }
            // Simulation time changes do not revoke a reviewed command. Every
            // command revalidates present stock, funds, plot and phase. Revision
            // fences command/effect acceptance and receipt compaction instead.
            CompactTerminalEffects(state);
            preparedBytes = ColonyStateCodec.Serialize(state); return state;
        }

        static void ConsumeSupport(ColonyState state, ColonyRecord colony, ColonyEnvironment env, double ut)
        {
            if (!colony.SupportCommissionedUt.HasValue) return;
            if (SupportOwnershipHeld(colony, env))
            {
                colony.SupportAccountedUt = ut; colony.SupportRemainder = 0;
                colony.SupportStatus = "Support ownership held: installed life-support provider or policy changed. Expanse consumption and immigration paused.";
                return;
            }
            double start = Math.Max(colony.SupportAccountedUt, colony.SupportCommissionedUt.Value);
            if (ut <= start) return;
            long people = colony.Residents.Count(x => x.Status != "arriving" && x.Status != "missing") + colony.VisitorRosterIds.Count;
            decimal exact = (decimal)(ut - start) * people * colony.SupportMicroUnitsPerPersonDay / (decimal)ColonyLimits.KerbinDay + colony.SupportRemainder;
            long needed = checked((long)decimal.Floor(exact));
            var stock = colony.Stock.Find(x => x.Resource == "Supplies");
            long consumed = stock == null ? 0 : Math.Min(needed, stock.Amount - stock.Reserved);
            if (stock != null) { stock.Amount -= consumed; stock.ImportedAmount = Math.Max(0, stock.ImportedAmount - consumed); }
            bool shortage = needed > consumed || people > 0 && (stock == null || stock.Amount <= stock.Reserved);
            string previousStatus = colony.SupportStatus;
            colony.SupportStatus = shortage ? "Shortage: immigration and growth paused; residents remain alive." : "Supported";
            // Unmet support is a historical shortage, never a debt charged against a
            // later delivery. Retain only the fractional unit while actually supplied.
            colony.SupportRemainder = shortage ? 0 : exact - needed;
            colony.SupportAccountedUt = ut;
            colony.SupportConsumedMicroUnits = checked(colony.SupportConsumedMicroUnits + consumed);
            colony.SupportPendingJournalMicroUnits = checked(colony.SupportPendingJournalMicroUnits + consumed);
            // Aggregate ordinary subsecond consumption; retain exact cumulative
            // counters in every save and flush daily or at a support transition.
            if (Math.Floor(start / ColonyLimits.KerbinDay) != Math.Floor(ut / ColonyLimits.KerbinDay) || previousStatus != colony.SupportStatus)
            {
                Log(state, ut, colony.Id, "", "residentSupport", colony.SupportStatus + " Includes residents and visitors.", 0, "Supplies", -colony.SupportPendingJournalMicroUnits);
                colony.SupportPendingJournalMicroUnits = 0;
            }
        }

        static void AdvanceWork(ColonyState state, ConstructionOrder order, ColonyEnvironment env, double ut)
        {
            AdvanceWitnessedConstructionWork(state, order, env, ut);
        }

        static void ValidateEnvironment(ColonyState state, ColonyEnvironment env)
        {
            if (env == null || state.WorldId != env.WorldId || string.IsNullOrWhiteSpace(env.ContextKey)) throw new InvalidDataException("No matching loaded colony world.");
            ColonyStateCodec.Time(env.Ut); ColonyStateCodec.Funds(env.AvailableFunds);
        }
        static ColonyEffect FundsEffect(ColonyCommand command, string target, long amount, string kind) => new ColonyEffect { Id = Guid.NewGuid().ToString("D"), OperationId = command.OperationId, ColonyId = command.ColonyId, TargetId = target, Kind = kind, FundsDelta = amount };
        static void CheckFunds(ColonyState state, ColonyRecord colony, long amount, ColonyEnvironment env)
        {
            ColonyStateCodec.Funds(amount);
            long pending = PendingCash(state);
            long floor = state.Colonies.Count == 0 ? 0 : state.Colonies.Max(x => x.Charter.CashFloor);
            if (amount > env.AvailableFunds - pending - floor) throw new InvalidDataException("Shared funds, pending commitments and the colony cash floor leave insufficient cash.");
            if (amount > colony.Charter.SpendingLimit - colony.SpentFunds - CommittedFunds(state, colony.Id)) throw new InvalidDataException("Colony spending limit would be exceeded.");
            if (colony.Status == "founding" && amount > colony.Charter.FoundingBudget - colony.SpentFunds - CommittedFunds(state, colony.Id)) throw new InvalidDataException("Founding budget would be exceeded.");
        }
        public static long CommittedFunds(ColonyState state, string colonyId) => checked(
            state.Construction.Where(x => x.ColonyId == colonyId && x.State != "cancelled").Sum(x => x.Funds - x.FundsConsumed) +
            state.Shipments.Where(x => x.ColonyId == colonyId && x.State == "reserved").Sum(x => x.Funds) +
            state.PeopleOperations.Where(x => x.ColonyId == colonyId && x.State == "reserved").Sum(x => x.Funds) +
            state.Colonies.Where(x => x.Id == colonyId && x.Logistics.State == "reserved").Sum(x => x.Logistics.Funds) +
            state.WolfOrders.Where(x => x.ColonyId == colonyId && !x.FundsPaid && x.State != "cancelled").Sum(x => x.Quote.Funds) + PlanningCommittedFunds(state, colonyId));
        public static long ScaledProduct(long amount, long perUnit) => checked((long)decimal.Ceiling((decimal)amount * perUnit / ColonyLimits.Units));
        public static string SiteKey(ColonySite site) => site.Body + ":" + site.Latitude.ToString("R", CultureInfo.InvariantCulture) + ":" + site.Longitude.ToString("R", CultureInfo.InvariantCulture) + ":" + site.RadiusMeters.ToString("R", CultureInfo.InvariantCulture);
        public static double SurfaceDistance(ColonySite a, ColonySite b, double radius)
        {
            double lat1 = a.Latitude * Math.PI / 180, lat2 = b.Latitude * Math.PI / 180, dlat = lat2 - lat1, dlon = (b.Longitude - a.Longitude) * Math.PI / 180;
            double h = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dlon / 2), 2);
            return 2 * radius * Math.Asin(Math.Sqrt(Math.Min(1, Math.Max(0, h))));
        }
        static double NextInstant(double value) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + 1);
        static ColonyRecord Colony(ColonyState state, string id) => state.Colonies.SingleOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Colony not found.");
        static ColonyStock Stock(ColonyRecord colony, string resource) => colony.Stock.SingleOrDefault(x => x.Resource == resource) ?? throw new InvalidDataException("No colony-owned receiving storage for " + resource + ".");
        static string Field(ColonyCommand command, string key, string? fallback = null) => command.Fields.TryGetValue(key, out var value) ? value : fallback ?? throw new InvalidDataException("Missing field: " + key);
        static double Number(ColonyCommand command, string key, double fallback = double.NaN) => double.Parse(Field(command, key, double.IsNaN(fallback) ? null : fallback.ToString("R", CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        static long Integer(ColonyCommand command, string key, long? fallback = null) => long.Parse(Field(command, key, fallback?.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        static ColonyCharter ReadCharter(ColonyCommand command) => new ColonyCharter { Purpose = Field(command, "Purpose", "Settlement"), PopulationTarget = checked((int)Integer(command, "Population", 12)),
            FoundingBudget = Integer(command, "Budget", 1_000_000), CashFloor = Integer(command, "CashFloor", 100_000), SpendingLimit = Integer(command, "SpendingLimit", 1_000_000),
            ResidentLimit = checked((int)Integer(command, "ResidentLimit", 24)), VisitorLimit = checked((int)Integer(command, "VisitorLimit", 24)), ReserveDays = Number(command, "ReserveDays", 6), GrowthPolicy = Field(command, "GrowthPolicy", "approval"), Sandbox = bool.Parse(Field(command, "Sandbox", "false")) };
        static void Log(ColonyState state, double ut, string colony, string operation, string kind, string detail, long funds = 0, string resource = "", long delta = 0)
        {
            // Journal text is a rolling view, not the replay authority. Fold removed
            // entries into a chained checkpoint; receipts/revisions and target phase
            // records retain the evidence required to prevent duplicate effects.
            while (state.Journal.Count >= ColonyLimits.Journal)
            {
                var old = state.Journal[0];
                using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(state.CompactedJournalHash); writer.Write(old.Sequence); writer.Write(old.Ut); writer.Write(old.ColonyId); writer.Write(old.OperationId);
                    writer.Write(old.Kind); writer.Write(old.Detail); writer.Write(old.FundsDelta); writer.Write(old.Resource); writer.Write(old.ResourceDelta); writer.Flush();
                    state.CompactedJournalHash = ColonyStateCodec.Hash(stream.ToArray());
                }
                state.CompactedJournalCount = checked(state.CompactedJournalCount + 1); state.Journal.RemoveAt(0);
            }
            state.Journal.Add(new ColonyJournalEntry { Sequence = state.NextSequence++, Ut = ut, ColonyId = colony, OperationId = operation, Kind = kind, Detail = detail, FundsDelta = funds, Resource = resource, ResourceDelta = delta });
        }
        static bool CompactTerminalEffects(ColonyState state, int requiredSlots = 0)
        {
            // Uncertain/prepared effects are never evicted. Completed targets retain
            // their own phases; old client requests are fenced by accepted revision.
            while (state.Effects.Count > ColonyLimits.Effects - requiredSlots)
            {
                int index = state.Effects.FindIndex(x => (x.State == "applied" || x.State == "cancelled") &&
                    !(x.Kind == "constructionPlacement" && state.Construction.Any(o => o.Placement != null && o.Placement.EffectId == x.Id)) &&
                    !(x.Kind == ConstructionActivationKind && state.Construction.Any(o => o.Id == x.TargetId)) &&
                    !(x.Kind == "productionNative" && state.Plans.Any(p => p.Production.Any(c => c.Steps.Any(s => s.Id == x.Id)))) &&
                    !(x.Kind == "productionRegistration" && state.Plans.Any(p => p.Production.Any(c => c.Inventory != null && c.Inventory.Endpoints.Any(e => e.EffectId == x.Id)))));
                if (index < 0)
                {
                    if (requiredSlots > 0) return false;
                    throw new InvalidDataException("External effect capacity reached; reconcile pending operations.");
                }
                state.Effects.RemoveAt(index);
            }
            return true;
        }
    }
}
