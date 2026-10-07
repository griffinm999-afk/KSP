using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    // Fresh provider readback only; command fields cannot manufacture this witness.
    public sealed class ColonyConstructionRecoveryWitness
    {
        public string WorldId { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string RequestFingerprint { get; set; } = "";
        public string PayloadHash { get; set; } = "";
        public string EscrowWitness { get; set; } = "";
        public string Phase { get; set; } = "";
        public string RetryOperationId { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "";
        public double ObservedUt { get; set; }
        public bool Current { get; set; }
        public bool AssemblyAttempted { get; set; }
        public int MarkedVessels { get; set; }
    }

    public static partial class ColonyEngine
    {
        public const string ConstructionRetryKind = "constructionPlacementRetry";
        public const string ConstructionRetryProvider = "KSP.ColonyPlacement.RetryUnattempted.v1";

        public static string ConstructionRetryBinding(ColonyState state, ConstructionOrder order)
        {
            return ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(string.Join("|", state.WorldId, order.Id, order.ColonyId,
                order.TemplateId, order.TemplateHash, order.Placement.OperationId, order.Placement.RequestFingerprint,
                order.Placement.PayloadHash, order.Placement.EscrowWitness)));
        }

        public static bool CanRetryConstructionPlacement(ColonyState state, string orderId, ColonyEnvironment env, out string reason)
        {
            try { RetryOrder(state, orderId, env); reason = "Retry original placement terms; paid materials, work and funds remain consumed."; return true; }
            catch (Exception ex) { reason = ex.Message; return false; }
        }

        static ConstructionOrder RetryOrder(ColonyState state, string orderId, ColonyEnvironment env)
        {
            var order = state.Construction.SingleOrDefault(o => o.Id == orderId) ?? throw new InvalidDataException("Paid construction order is unavailable.");
            var p = order.Placement;
            if (order.State != "held" || p.Phase != "RecoveryHold" || p.OperationId.Length == 0 || p.AssemblyAttempted || order.FacilityId.Length > 0 ||
                !order.FundsPaid || !order.MaterialsConsumed || order.WorkCompleted != order.WorkRequired || order.FundsConsumed != order.Funds ||
                p.EscrowWitness != ConstructionEscrowWitness(state, orderId))
                throw new InvalidDataException("Retry requires one completed paid order held before any assembly, with its original escrow intact.");
            if (!env.Templates.Any(t => t.Id == order.TemplateId && t.Hash == order.TemplateHash)) throw new InvalidDataException("The paid package changed; same-terms retry cannot amend its footprint or quote.");
            var placement = state.Effects.SingleOrDefault(e => e.Id == p.EffectId && e.TargetId == order.Id && e.ColonyId == order.ColonyId && e.Kind == "constructionPlacement" && e.OperationId == p.OperationId);
            if (placement == null || placement.State != "held" || state.Effects.Any(e => (e.State == "held" || e.State == "applying") && e.Id != placement.Id) ||
                state.Effects.Any(e => e.Kind == ConstructionRetryKind && e.TargetId == order.Id && e.State != "applied" && e.State != "cancelled"))
                throw new InvalidDataException("Only this order's own placement hold may be reconciled; another unresolved effect or retry must finish first.");
            if (!env.ConstructionRecovery.TryGetValue(order.Id, out var witness)) throw new InvalidDataException("Current placement provider is unavailable; the paid order remains held.");
            ValidateRetryWitness(state, order, env.ContextKey, env.Ut, witness);
            if (witness.Phase != "RecoveryHold" || witness.AssemblyAttempted || witness.MarkedVessels != 0)
                throw new InvalidDataException("Provider must confirm RecoveryHold with no assembly attempt and no marked vessel.");
            return order;
        }

        public static void ValidateRetryWitness(ColonyState state, ConstructionOrder order, string context, double ut, ColonyConstructionRecoveryWitness witness)
        {
            if (witness == null || !witness.Current || witness.WorldId != state.WorldId || witness.ContextKey != context || witness.OrderId != order.Id ||
                witness.OperationId != order.Placement.OperationId || witness.RequestFingerprint != order.Placement.RequestFingerprint ||
                witness.PayloadHash != order.Placement.PayloadHash || witness.EscrowWitness != order.Placement.EscrowWitness ||
                double.IsNaN(witness.ObservedUt) || double.IsInfinity(witness.ObservedUt) || Math.Abs(witness.ObservedUt - ut) > 1 || witness.MarkedVessels < 0)
                throw new InvalidDataException("Fresh provider identity does not match the original paid placement in this world/context.");
        }

        static string AuthorizeConstructionRetry(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var order = RetryOrder(state, command.TargetId, env);
            if (order.ColonyId != command.ColonyId || Field(command, "PlacementOperationId") != order.Placement.OperationId ||
                Field(command, "RequestFingerprint") != order.Placement.RequestFingerprint || Field(command, "PayloadHash") != order.Placement.PayloadHash)
                throw new InvalidDataException("Retry command does not identify the exact original order/request.");
            if (state.Effects.Any(e => e.Id == command.OperationId)) throw new InvalidDataException("Retry identity is already in use.");
            var p = order.Placement;
            string before = "binding=" + ConstructionRetryBinding(state, order) + ";context=" + env.ContextKey +
                ";holdWitnessSha=" + ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(p.AfterWitness)) + ";holdReason=" + p.Reason;
            state.Effects.Add(new ColonyEffect { Id = command.OperationId, OperationId = command.OperationId, ColonyId = order.ColonyId, TargetId = order.Id,
                Kind = ConstructionRetryKind, Provider = ConstructionRetryProvider, BeforeWitness = before, Reason = "Same-terms retry authorized; provider has not yet retried." });
            Log(state, env.Ut, order.ColonyId, command.OperationId, "constructionRetryOriginalHold", p.Reason);
            Log(state, env.Ut, order.ColonyId, command.OperationId, "constructionRetryAuthorization", "placement=" + p.OperationId + ";request=" + p.RequestFingerprint + ";binding=" + ConstructionRetryBinding(state, order));
            return order.Id;
        }

        public static ColonyState MarkConstructionRetryApplying(ColonyState prior, string effectId)
        {
            ColonyStateCodec.Validate(prior);
            var saved = prior.Effects.Single(e => e.Id == effectId && e.Kind == ConstructionRetryKind);
            if (saved.State == "applied") return prior;
            var next = ColonyStateCodec.Copy(prior); var effect = next.Effects.Single(e => e.Id == effectId);
            effect.State = "applying"; effect.Reason = "Serialized same-terms authorization retained before placement-provider retry.";
            next.Revision++; ColonyStateCodec.Serialize(next); return next;
        }

        public static ColonyState CompleteConstructionRetry(ColonyState prior, string effectId, string context, double ut, ColonyConstructionRecoveryWitness witness)
        {
            ColonyStateCodec.Validate(prior);
            var saved = prior.Effects.Single(e => e.Id == effectId && e.Kind == ConstructionRetryKind);
            if (saved.State == "applied") return prior;
            var order = prior.Construction.Single(o => o.Id == saved.TargetId);
            ValidateRetryWitness(prior, order, context, ut, witness);
            if (witness.RetryOperationId != saved.Id) throw new InvalidDataException("Placement provider has not acknowledged this retry operation; no success is inferred.");
            var next = ColonyStateCodec.Copy(prior); var effect = next.Effects.Single(e => e.Id == effectId);
            effect.State = "applied"; effect.AfterWitness = "retry=" + witness.RetryOperationId + ";phase=" + witness.Phase + ";witness=" + witness.AfterWitness;
            effect.Reason = "Provider acknowledged the original request retry; placement/commissioning remain independently observed.";
            next.Revision++; Log(next, ut, order.ColonyId, effectId, "constructionRetryReadback", "placement=" + witness.OperationId + ";phase=" + witness.Phase + ";assemblyAttempted=" + witness.AssemblyAttempted);
            ColonyStateCodec.Serialize(next); return next;
        }
    }

    public static partial class ColonyStateCodec
    {
        static void ValidateConstructionRetries(ColonyState state)
        {
            foreach (var effect in state.Effects.Where(e => e.Kind == ColonyEngine.ConstructionRetryKind))
            {
                var order = state.Construction.SingleOrDefault(o => o.Id == effect.TargetId);
                if (order == null || !order.FundsPaid || !order.MaterialsConsumed || order.WorkCompleted != order.WorkRequired || order.FundsConsumed != order.Funds ||
                    order.Placement.EscrowWitness != ColonyEngine.ConstructionEscrowWitness(state, order.Id) ||
                    effect.Id != effect.OperationId || effect.ColonyId != order.ColonyId || effect.FundsDelta != 0 ||
                    effect.Provider != ColonyEngine.ConstructionRetryProvider || !effect.BeforeWitness.StartsWith("binding=" + ColonyEngine.ConstructionRetryBinding(state, order) + ";context=", StringComparison.Ordinal))
                    throw new InvalidDataException("Saved retry is not bound to the original paid placement.");
            }
        }
    }
}
