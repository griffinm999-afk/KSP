using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyConstructionLaborWitness
    {
        public string ProviderId { get; set; } = "";
        public string PoolId { get; set; } = "";
        public string EvidenceHash { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public int Workers { get; set; }
        public int PoolCapacity { get; set; }
        public double ValidFromUt { get; set; }
        public double ValidThroughUt { get; set; }
        public bool CostIncludedInPaidEscrow { get; set; }
    }

    public sealed class ColonyConstructionPlacementIntent
    {
        public string OperationId { get; set; } = "";
        public string EffectId { get; set; } = "";
        public string Phase { get; set; } = "none";
        public string ContextKey { get; set; } = "";
        public string RequestPayload { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public string RequestFingerprint { get; set; } = "";
        public string EscrowWitness { get; set; } = "";
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool AssemblyAttempted { get; set; }
    }

    // Only the main-thread physical adapter creates this readback. The domain
    // validates identities and lineage; it never grants a physical certificate
    // from a client command or an economy-only completion.
    public sealed class ColonyConstructionPlacementObservation
    {
        public string WorldId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string RequestFingerprint { get; set; } = "";
        public string Phase { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool AssemblyAttempted { get; set; }
        public double ObservedUt { get; set; }
        public string VesselId { get; set; } = "";
        public string FoundationId { get; set; } = "";
        public bool Anchored { get; set; }
        public bool DevelopmentOnly { get; set; }
        public Dictionary<uint, uint> CraftToPersistentIds { get; set; } = new Dictionary<uint, uint>();
        public List<uint> QualifiedHomePartIds { get; set; } = new List<uint>();
    }

    public static partial class ColonyEngine
    {
        // One bounded round-robin observation per pump. A package waiting for
        // staffing does not monopolize placement of independent paid buildings.
        public static string NextConstructionPumpOrderId(ColonyState state, string lastOrderId)
        {
            var candidates = state.Construction.Where(o => o.State == "awaitingPlacement" || o.State == "placing" || o.State == "commissioning" || o.State == "held" && o.Placement.OperationId.Length > 0)
                .OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id).ToArray();
            if (candidates.Length == 0) return "";
            int last = Array.IndexOf(candidates, lastOrderId);
            return candidates[(last + 1) % candidates.Length];
        }

        public static ColonyState AwaitConstructionEvidence(ColonyState prior, string orderId, string reason)
        {
            var current = prior.Construction.Single(o => o.Id == orderId);
            if (current.State == "held" || current.State == "operational" || current.State == "cancelled") return prior;
            ColonyStateCodec.Text(reason, 512, true);
            if (current.Reason == reason) return prior;
            var next = ColonyStateCodec.Copy(prior); var order = next.Construction.Single(o => o.Id == orderId);
            order.Reason = reason;
            if (order.State == "commissioning" && order.FacilityId.Length > 0) Colony(next, order.ColonyId).Facilities.Single(f => f.Id == order.FacilityId).LastReason = reason;
            next.Revision++; ColonyStateCodec.Serialize(next); return next;
        }

        // Only the trusted loaded-terrain adapter calls this transition. Preserve
        // the paid coordinates and package; refresh evidence rather than asking
        // a client to supply a hash for an already reserved plot after reload.
        public static ColonyState RefreshConstructionSurvey(ColonyState prior, string orderId, ColonyEnvironment env, ColonyPlot observed)
        {
            ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, env);
            var current = prior.Construction.Single(o => o.Id == orderId);
            if (current.State != "awaitingPlacement" || current.Placement.OperationId.Length > 0 || !current.FundsPaid || !current.MaterialsConsumed || current.WorkCompleted != current.WorkRequired || current.FundsConsumed != current.Funds)
                throw new InvalidDataException("Survey refresh requires completed paid work before the immutable placement intent.");
            var template = env.Templates.Single(t => t.Id == current.TemplateId && t.Hash == current.TemplateHash);
            var plot = Colony(prior, current.ColonyId).Plots.Single(p => p.Id == current.PlotId);
            if (observed == null || observed.Id != plot.Id || plot.ReservedBy != current.Id || plot.OccupiedBy.Length > 0 || observed.ReservedBy.Length > 0 || observed.OccupiedBy.Length > 0 ||
                observed.Latitude != plot.Latitude || observed.Longitude != plot.Longitude || observed.Heading != plot.Heading || observed.TemplateId != template.Id || observed.TemplateHash != template.Hash || observed.EvidenceContext != env.ContextKey ||
                observed.WidthMeters != 2 * (Math.Max(Math.Abs(template.MinX), Math.Abs(template.MaxX)) + template.ClearanceMetres) || observed.LengthMeters != 2 * (Math.Max(Math.Abs(template.MinZ), Math.Abs(template.MaxZ)) + template.ClearanceMetres) ||
                observed.ObservedUt > env.Ut + 1 || observed.ObservedUt < env.Ut - 1 || observed.SurveyProvenance.Length == 0 || observed.SurveyHash.Length != 64 || !observed.SurveyHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Fresh loaded survey does not bind the exact paid plot/package/current context.");
            var next = ColonyStateCodec.Copy(prior); var refreshed = Colony(next, current.ColonyId).Plots.Single(p => p.Id == current.PlotId);
            refreshed.WidthMeters = observed.WidthMeters; refreshed.LengthMeters = observed.LengthMeters;
            refreshed.TemplateId = observed.TemplateId; refreshed.TemplateHash = observed.TemplateHash; refreshed.SurveyHash = observed.SurveyHash;
            refreshed.EvidenceContext = observed.EvidenceContext; refreshed.ObservedUt = observed.ObservedUt; refreshed.SurveyProvenance = observed.SurveyProvenance;
            next.Revision++; ColonyStateCodec.Serialize(next); return next;
        }

        public static string ConstructionEscrowWitness(ColonyState state, string orderId)
        {
            var order = state.Construction.Single(x => x.Id == orderId);
            string terms = string.Join("|", state.WorldId, order.Id, order.ColonyId, order.PlotId, order.TemplateId, order.TemplateHash,
                order.Funds.ToString(CultureInfo.InvariantCulture), order.LaborFunds.ToString(CultureInfo.InvariantCulture), order.FundsPaid.ToString(),
                order.FundsConsumed.ToString(CultureInfo.InvariantCulture), order.MaterialsConsumed.ToString(), order.WorkRequired.ToString("R", CultureInfo.InvariantCulture),
                order.WorkCompleted.ToString("R", CultureInfo.InvariantCulture), string.Join(";", order.Materials.OrderBy(m => m.Resource, StringComparer.Ordinal).Select(m => m.Resource + "=" + m.Amount.ToString(CultureInfo.InvariantCulture))));
            return ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms));
        }

        public static ColonyState PrepareConstructionPlacement(ColonyState prior, string orderId, ColonyEnvironment env, ColonyConstructionPlacementIntent intent)
        {
            ColonyStateCodec.Validate(prior); ValidateEnvironment(prior, env); ColonyStateCodec.ValidateConstruction(prior);
            var current = prior.Construction.Single(x => x.Id == orderId);
            if (current.Placement.OperationId.Length > 0)
            {
                if (current.Placement.OperationId != intent.OperationId || current.Placement.PayloadHash != intent.PayloadHash || current.Placement.RequestFingerprint != intent.RequestFingerprint)
                    throw new InvalidDataException("Construction placement terms changed under an existing durable operation.");
                return prior;
            }
            if (prior.Effects.Any(e => e.State == "applying" || e.State == "held")) throw new InvalidDataException("Reconcile the existing external effect before placement.");
            var template = env.Templates.SingleOrDefault(t => t.Id == current.TemplateId && t.Hash == current.TemplateHash) ?? throw new InvalidDataException("Paid construction package changed or is unavailable.");
            var owner = Colony(prior, current.ColonyId);
            if (!template.RuntimeCertified && !(env.DevelopmentMode && owner.Charter.Sandbox)) throw new InvalidDataException("Candidate placement is authorized only for isolated development sandbox testing.");
            if (current.State != "awaitingPlacement" || !current.FundsPaid || current.FundsConsumed != current.Funds || !current.MaterialsConsumed || current.WorkCompleted != current.WorkRequired)
                throw new InvalidDataException("Placement requires completed paid work and consumed construction materials/startup contents.");
            if (current.Funds != template.BuildFunds || current.LaborFunds != template.LaborFunds || current.LaborFunds <= 0)
                throw new InvalidDataException("Paid construction labor is not bound to the reviewed package quote.");
            var required = template.Materials.Concat(template.EmbeddedContents).GroupBy(m => m.Resource, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(m => m.Amount), StringComparer.Ordinal);
            if (required.Count != current.Materials.Count || current.Materials.Any(m => !required.ContainsKey(m.Resource) || required[m.Resource] != m.Amount)) throw new InvalidDataException("Consumed construction charge differs from the exact package BOM/startup allocation.");
            var plot = owner.Plots.Single(x => x.Id == current.PlotId);
            if (plot.ReservedBy != current.Id || plot.OccupiedBy.Length != 0 || plot.TemplateId != template.Id || plot.TemplateHash != template.Hash || plot.SurveyHash.Length == 0 || plot.EvidenceContext != env.ContextKey)
                throw new InvalidDataException("Current surveyed plot is not bound to this paid package and context.");
            ColonyStateCodec.ValidatePlacementIntent(intent, true);
            if (intent.ContextKey != env.ContextKey || intent.EscrowWitness != ConstructionEscrowWitness(prior, orderId) || intent.Phase != "intent") throw new InvalidDataException("Placement intent does not match the current paid escrow/context.");
            if (prior.Construction.Any(o => o.Id != orderId && o.Placement.OperationId == intent.OperationId) || prior.Effects.Any(e => e.Id == intent.EffectId)) throw new InvalidDataException("Placement operation/effect identity is already in use.");
            var next = ColonyStateCodec.Copy(prior); var order = next.Construction.Single(o => o.Id == orderId);
            if (!CompactTerminalEffects(next, 1)) throw new InvalidDataException("External effect capacity reached; reconcile pending operations.");
            order.Placement = intent; order.State = "placing"; order.Reason = "Paid placement intent persisted; awaiting exact product queue receipt.";
            next.Effects.Add(new ColonyEffect { Id = intent.EffectId, OperationId = intent.OperationId, ColonyId = order.ColonyId, TargetId = order.Id,
                Kind = "constructionPlacement", State = "applying", Provider = "KSP.ColonyPlacement.v1", BeforeWitness = intent.BeforeWitness, Reason = order.Reason });
            next.Revision++; Log(next, env.Ut, order.ColonyId, intent.OperationId, "constructionPlacementIntent", order.Reason, 0);
            ColonyStateCodec.ValidateConstruction(next); ColonyStateCodec.Serialize(next); return ColonyStateCodec.Copy(next);
        }

        public static ColonyState ObserveConstructionPlacement(ColonyState prior, string orderId, ColonyTemplate template, ColonyConstructionPlacementObservation observation)
        {
            ColonyStateCodec.Validate(prior); ColonyStateCodec.ValidateConstruction(prior);
            var current = prior.Construction.Single(x => x.Id == orderId); var intent = current.Placement;
            if (intent.OperationId.Length == 0 || observation.WorldId != prior.WorldId || observation.OperationId != intent.OperationId || observation.RequestFingerprint != intent.RequestFingerprint || template.Id != current.TemplateId || template.Hash != current.TemplateHash)
                throw new InvalidDataException("Physical placement receipt does not match the durable paid order.");
            ColonyStateCodec.Time(observation.ObservedUt); ColonyStateCodec.Text(observation.AfterWitness, 4096, true); ColonyStateCodec.Text(observation.Reason, 512);
            string[] phases = { "AwaitingPlacement", "Prepared", "Created", "Settling", "Anchored", "RecoveryHold", "Cancelled" };
            if (!phases.Contains(observation.Phase)) throw new InvalidDataException("Unknown product placement phase.");
            if (intent.AssemblyAttempted && (observation.Phase == "AwaitingPlacement" || observation.Phase == "Prepared" && intent.Phase != "Prepared") ||
                intent.Phase == "Anchored" && observation.Phase != "Anchored" && observation.Phase != "RecoveryHold")
                throw new InvalidDataException("Physical receipt regressed behind an already witnessed assembly/anchor.");
            var next = ColonyStateCodec.Copy(prior); var order = next.Construction.Single(x => x.Id == orderId);
            var effect = next.Effects.Single(x => x.Id == order.Placement.EffectId && x.Kind == "constructionPlacement");
            order.Placement.Phase = observation.Phase; order.Placement.AfterWitness = observation.AfterWitness;
            order.Placement.AssemblyAttempted |= observation.AssemblyAttempted; order.Placement.Reason = observation.Reason; order.Reason = observation.Reason;
            if (observation.Phase == "RecoveryHold" || observation.Phase == "Cancelled")
            {
                order.State = "held"; effect.State = "held"; effect.Reason = observation.Reason;
            }
            else
            {
                // Applied means queue acceptance, never operational certification.
                effect.State = "applied"; effect.AfterWitness = observation.AfterWitness; effect.Reason = "Exact immutable placement queue receipt read back.";
                if (order.FacilityId.Length == 0) order.State = "placing";
            }
            if (observation.Phase == "Anchored")
            {
                if (!observation.Anchored || observation.FoundationId.Length == 0) throw new InvalidDataException("Anchored receipt lacks actual Foundation hold readback.");
                ColonyStateCodec.Id(observation.VesselId); ColonyStateCodec.Text(observation.FoundationId, 128, true);
                var mapping = observation.CraftToPersistentIds;
                if (mapping == null || mapping.Count != template.ExpectedPartCount || mapping.Any(p => p.Key == 0 || p.Value == 0) || mapping.Values.Distinct().Count() != mapping.Count)
                    throw new InvalidDataException("Anchored vessel lacks unique complete actual part identities.");
                var homes = observation.QualifiedHomePartIds;
                if (homes == null || homes.Distinct().Count() != homes.Count || homes.Any(id => !template.HomeCraftPartIds.Any(craft => mapping.ContainsKey(craft) && mapping[craft] == id))) throw new InvalidDataException("Housing identities lack explicit qualified manifest home mapping.");
                var owner = Colony(next, order.ColonyId); var facility = owner.Facilities.SingleOrDefault(f => f.ConstructionOrderId == order.Id);
                if (facility == null)
                {
                    if (next.Colonies.SelectMany(c => c.Facilities).Any(f => f.VesselId == observation.VesselId)) throw new InvalidDataException("Anchored vessel already belongs to a different facility.");
                    facility = new ColonyFacility { Id = observation.VesselId, VesselId = observation.VesselId, ConstructionOrderId = order.Id,
                        Name = template.Name, TemplateId = template.Id, TemplateHash = template.Hash, CraftSha256 = template.CraftSha256, CertificationId = template.CertificationId,
                        PlacementOperationId = intent.OperationId, PlacementRequestFingerprint = intent.RequestFingerprint, PlotId = order.PlotId,
                        PartIds = mapping.Values.OrderBy(id => id).ToList(), RequiredWorkers = template.Workers, RequiredTrait = template.WorkerTrait,
                        State = "commissioning", ProductionOwner = "physical", FoundationId = observation.FoundationId, DevelopmentOnly = observation.DevelopmentOnly };
                    owner.Facilities.Add(facility);
                }
                if (facility.VesselId != observation.VesselId || !facility.PartIds.OrderBy(id => id).SequenceEqual(mapping.Values.OrderBy(id => id))) throw new InvalidDataException("Previously registered building membership changed; replacement requires explicit migration.");
                facility.HomePartPersistentIds = homes.OrderBy(id => id).ToList();
                facility.HomePartCertificationHash = homes.Count == 0 ? "" : HomeMappingHash(facility);
                facility.PlacementWitnessHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(observation.AfterWitness));
                facility.LastReason = "Actual anchored package read back; utilities, staffing and support commissioning still required.";
                order.FacilityId = facility.Id; if (order.State != "operational") order.State = "commissioning";
                var plot = owner.Plots.Single(p => p.Id == order.PlotId);
                if (plot.OccupiedBy.Length > 0 && plot.OccupiedBy != facility.Id) throw new InvalidDataException("Registered plot was occupied by another building.");
                plot.ReservedBy = ""; plot.OccupiedBy = facility.Id;
            }
            next.Revision++; ColonyStateCodec.ValidateConstruction(next); ColonyStateCodec.Serialize(next); return ColonyStateCodec.Copy(next);
        }

        public static ColonyState CommissionConstruction(ColonyState prior, string orderId, ColonyTemplate template, ColonyQualification qualification, int actualQualifiedHomeCapacity)
        {
            ColonyStateCodec.Validate(prior); ColonyStateCodec.ValidateConstruction(prior);
            var current = prior.Construction.Single(x => x.Id == orderId);
            if (current.FacilityId.Length == 0 || current.Placement.Phase != "Anchored" || current.State == "held") throw new InvalidDataException("Physical placement is not ready for commissioning.");
            var next = ColonyStateCodec.Copy(prior); var order = next.Construction.Single(x => x.Id == orderId); var colony = Colony(next, order.ColonyId);
            var facility = colony.Facilities.Single(x => x.Id == order.FacilityId);
            if (template == null || template.Id != order.TemplateId || template.Hash != order.TemplateHash) throw new InvalidDataException("Commissioning package terms changed.");
            if (qualification == null || qualification.EvidenceHash.Length == 0 || qualification.Provider.Length == 0) throw new InvalidDataException("Commissioning provider evidence is unavailable.");
            if (actualQualifiedHomeCapacity < 0 || actualQualifiedHomeCapacity > ColonyLimits.Residents || actualQualifiedHomeCapacity > 0 && (facility.HomePartPersistentIds.Count == 0 || facility.HomePartCertificationHash != HomeMappingHash(facility))) throw new InvalidDataException("Actual home capacity lacks a sealed explicit part mapping.");
            facility.Qualification = qualification;
            bool operational = qualification.PlacementStable && qualification.PowerReliable && qualification.HeatSafe && qualification.InputsAccessible && qualification.StaffingQualified && qualification.BackgroundSupported;
            if (template.Homes > 0) operational &= qualification.HousingCertified && actualQualifiedHomeCapacity >= template.Homes && facility.HomePartPersistentIds.Count == template.HomeCraftPartIds.Count;
            // Homes are never counted while qualification is incomplete. Do not
            // revoke occupied housing here: resident-safe evacuation is separate.
            if (operational)
            {
                facility.CertifiedHomes = Math.Min(template.Homes, actualQualifiedHomeCapacity); facility.State = "operational"; order.State = "operational";
                facility.LastReason = "Actual anchored package commissioned with measured utility and explicit housing evidence.";
                order.Reason = facility.LastReason;
                Log(next, qualification.ObservedUt, colony.Id, order.Id, "constructionCommissioned", facility.LastReason, 0);
            }
            else
            {
                facility.LastReason = "Commissioning pending: placement=" + qualification.PlacementStable + "; power=" + qualification.PowerReliable + "; heat=" + qualification.HeatSafe + "; inputs=" + qualification.InputsAccessible + "; staff=" + qualification.StaffingQualified + "; background=" + qualification.BackgroundSupported + "; homes=" + qualification.HousingCertified;
                order.Reason = facility.LastReason;
            }
            next.Revision++; ColonyStateCodec.ValidateConstruction(next); ColonyStateCodec.Serialize(next); return ColonyStateCodec.Copy(next);
        }

        public static ColonyState HoldConstructionPlacement(ColonyState prior, string orderId, string reason)
        {
            var next = ColonyStateCodec.Copy(prior); var order = next.Construction.Single(x => x.Id == orderId);
            order.State = "held"; order.Reason = reason; order.Placement.Reason = reason;
            if (order.Placement.EffectId.Length > 0) { var effect = next.Effects.Single(x => x.Id == order.Placement.EffectId); effect.State = "held"; effect.Reason = reason; }
            next.Revision++; ColonyStateCodec.ValidateConstruction(next); ColonyStateCodec.Serialize(next); return next;
        }

        public static string HomeMappingHash(ColonyFacility facility) => ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(string.Join("|", facility.Id, facility.VesselId, facility.TemplateHash, facility.CraftSha256, facility.PlacementOperationId, facility.PlacementRequestFingerprint, string.Join(",", facility.HomePartPersistentIds.OrderBy(id => id)))));

        static ColonyConstructionLaborWitness? ConstructionLabor(ColonyState state, ColonyEnvironment env, ConstructionOrder order)
        {
            if (!order.FundsPaid || order.LaborFunds <= 0 || order.Dependencies.Any(id => state.Construction.Single(o => o.Id == id).State != "operational")) return null;
            ColonyConstructionLaborWitness? witness;
            if (!env.ConstructionLaborByOrder.TryGetValue(order.Id, out witness) || witness == null) return null;
            ColonyStateCodec.ValidateLaborWitness(witness);
            if (witness.ContextKey != env.ContextKey || !witness.CostIncludedInPaidEscrow || witness.ValidThroughUt < order.AccountedUt || witness.ValidFromUt > env.Ut) return null;
            var samePool = env.ConstructionLaborByOrder.Values.Where(w => w != null && w.PoolId == witness.PoolId).ToArray();
            foreach (var allocation in samePool) ColonyStateCodec.ValidateLaborWitness(allocation);
            if (samePool.Any(w => w.PoolCapacity != witness.PoolCapacity || w.ContextKey != witness.ContextKey) || samePool.Sum(w => w.Workers) > witness.PoolCapacity) throw new InvalidDataException("Construction labor pool was oversubscribed across paid orders.");
            return witness;
        }

        static double ConstructionCompletionBoundary(ColonyState state, ColonyEnvironment env, ConstructionOrder order, double next)
        {
            var witness = ConstructionLabor(state, env, order); if (witness == null || witness.Workers == 0) return next;
            double from = Math.Max(Math.Max(state.SimulatedUt, order.AccountedUt), witness.ValidFromUt);
            double completion = from + (order.WorkRequired - order.WorkCompleted) / witness.Workers;
            // A positive residual can be below half an UT ulp. Schedule a real
            // interval; AdvanceWitnessedConstructionWork still caps it to the witness.
            if (completion <= from) completion = NextInstant(from);
            return completion <= witness.ValidThroughUt ? Math.Min(next, Math.Max(state.SimulatedUt, completion)) : next;
        }

        static void AdvanceWitnessedConstructionWork(ColonyState state, ConstructionOrder order, ColonyEnvironment env, double ut)
        {
            var witness = ConstructionLabor(state, env, order);
            double from = Math.Max(order.AccountedUt, witness == null ? ut : witness.ValidFromUt);
            double through = Math.Min(ut, witness == null ? ut : witness.ValidThroughUt);
            order.AccountedUt = Math.Max(order.AccountedUt, ut);
            if (witness == null || witness.Workers == 0) { order.Reason = "No witnessed paid construction labor or dependency not commissioned."; return; }
            if (through <= from) return;
            var colony = Colony(state, order.ColonyId);
            if (!order.MaterialsConsumed)
            {
                foreach (var material in order.Materials)
                {
                    var stock = Stock(colony, material.Resource); stock.Reserved -= material.Amount; stock.Amount -= material.Amount;
                    stock.ImportedAmount = Math.Max(0, stock.ImportedAmount - material.Amount);
                    Log(state, through, colony.Id, order.Id, "constructionMaterial", "Paid construction consumed exact hardware BOM and startup allocation.", 0, material.Resource, -material.Amount);
                }
                order.MaterialsConsumed = true;
            }
            order.WorkCompleted = Math.Min(order.WorkRequired, order.WorkCompleted + (through - from) * witness.Workers);
            long spent = checked((long)Math.Ceiling(order.Funds * (order.WorkCompleted / order.WorkRequired)));
            colony.SpentFunds = checked(colony.SpentFunds + spent - order.FundsConsumed); order.FundsConsumed = spent;
            order.LaborProviderId = witness.ProviderId; order.LaborEvidenceHash = witness.EvidenceHash; order.LaborWorkers = witness.Workers;
            order.Reason = "Paid witnessed construction work in progress.";
            if (order.WorkCompleted >= order.WorkRequired) { order.State = "awaitingPlacement"; order.Reason = "Paid work complete; awaiting actual surveyed placement and commissioning."; }
        }
    }

    public static partial class ColonyStateCodec
    {
        public static void ValidateLaborWitness(ColonyConstructionLaborWitness witness)
        {
            Text(witness.ProviderId, 128, true); Text(witness.PoolId, 128, true); Text(witness.ContextKey, 256, true); Text(witness.EvidenceHash, 64, true);
            if (!ConstructionSha(witness.EvidenceHash)) throw new InvalidDataException("Construction labor evidence SHA missing.");
            Range(witness.Workers, 0, 512); Range(witness.PoolCapacity, 1, 512); Time(witness.ValidFromUt); Time(witness.ValidThroughUt);
            if (witness.Workers > witness.PoolCapacity || witness.ValidThroughUt < witness.ValidFromUt) throw new InvalidDataException("Invalid bounded construction labor interval.");
        }

        public static void ValidatePlacementIntent(ColonyConstructionPlacementIntent intent, bool required)
        {
            if (intent == null) throw new InvalidDataException("Missing construction placement record.");
            if (intent.OperationId.Length == 0)
            {
                if (required || intent.Phase != "none" || intent.EffectId.Length > 0 || intent.RequestPayload.Length > 0 || intent.AssemblyAttempted) throw new InvalidDataException("Incomplete construction placement identity.");
                return;
            }
            Id(intent.OperationId); Id(intent.EffectId); Text(intent.ContextKey, 256, true); Text(intent.RequestPayload, 65536, true);
            Text(intent.BeforeWitness, 4096, true); Text(intent.AfterWitness, 4096); Text(intent.Reason, 512);
            if (!ConstructionSha(intent.PayloadHash) || !ConstructionSha(intent.RequestFingerprint) || !ConstructionSha(intent.EscrowWitness) || Hash(Encoding.UTF8.GetBytes(intent.RequestPayload)) != intent.PayloadHash)
                throw new InvalidDataException("Construction placement immutable payload/escrow checksum failed.");
            if (!(new[] { "intent", "AwaitingPlacement", "Prepared", "Created", "Settling", "Anchored", "RecoveryHold", "Cancelled" }).Contains(intent.Phase)) throw new InvalidDataException("Unknown persisted placement phase.");
        }

        public static void ValidateConstruction(ColonyState state)
        {
            foreach (var order in state.Construction)
            {
                Funds(order.LaborFunds); Text(order.LaborProviderId, 128); Text(order.LaborEvidenceHash, 64); Range(order.LaborWorkers, 0, 512);
                if (order.LaborFunds > order.Funds || order.LaborEvidenceHash.Length > 0 && !ConstructionSha(order.LaborEvidenceHash)) throw new InvalidDataException("Invalid saved labor price/evidence.");
                ValidatePlacementIntent(order.Placement, false);
                if (order.Placement.OperationId.Length > 0)
                {
                    if (!order.FundsPaid || !order.MaterialsConsumed || order.FundsConsumed != order.Funds || order.WorkCompleted != order.WorkRequired || order.State == "cancelled") throw new InvalidDataException("Physical placement lacks completed consumed paid construction.");
                    if (!state.Effects.Any(e => e.Id == order.Placement.EffectId && e.Kind == "constructionPlacement" && e.TargetId == order.Id && e.ColonyId == order.ColonyId && e.OperationId == order.Placement.OperationId && e.FundsDelta == 0)) throw new InvalidDataException("Placement intent lacks its matching saved external effect.");
                    if (order.Placement.EscrowWitness != ColonyEngine.ConstructionEscrowWitness(state, order.Id)) throw new InvalidDataException("Saved placement escrow terms changed.");
                }
            }
            if (state.Construction.Where(o => o.Placement.OperationId.Length > 0).GroupBy(o => o.Placement.OperationId).Any(g => g.Count() > 1)) throw new InvalidDataException("Placement operation assigned to multiple construction orders.");
            foreach (var facility in state.Colonies.SelectMany(c => c.Facilities))
            {
                Text(facility.ConstructionOrderId, 64); Text(facility.PlacementOperationId, 64); Text(facility.PlacementRequestFingerprint, 64); Text(facility.CraftSha256, 64); Text(facility.CertificationId, 128); Text(facility.FoundationId, 128); Text(facility.PlacementWitnessHash, 64); Text(facility.HomePartCertificationHash, 64);
                if (facility.HomePartPersistentIds == null || facility.HomePartPersistentIds.Count > 256 || facility.HomePartPersistentIds.Any(id => id == 0 || !facility.PartIds.Contains(id)) || facility.HomePartPersistentIds.Distinct().Count() != facility.HomePartPersistentIds.Count) throw new InvalidDataException("Invalid explicit housing part identities.");
                if (facility.HomePartPersistentIds.Count > 0 && facility.HomePartCertificationHash != ColonyEngine.HomeMappingHash(facility)) throw new InvalidDataException("Housing identity mapping seal changed.");
                if (facility.ConstructionOrderId.Length > 0)
                {
                    var order = state.Construction.SingleOrDefault(o => o.Id == facility.ConstructionOrderId);
                    if (order == null || order.FacilityId != facility.Id || facility.PlacementOperationId != order.Placement.OperationId || facility.PlacementRequestFingerprint != order.Placement.RequestFingerprint || facility.TemplateHash != order.TemplateHash || !ConstructionSha(facility.CraftSha256) || !ConstructionSha(facility.PlacementWitnessHash)) throw new InvalidDataException("Constructed facility lacks durable paid placement lineage.");
                    if (facility.CertifiedHomes > 0 && facility.HomePartPersistentIds.Count == 0) throw new InvalidDataException("Constructed housing has no explicit certified part mapping.");
                }
            }
        }
        static bool ConstructionSha(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    }
}
