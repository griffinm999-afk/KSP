using System;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private ColonyConstructionRecoveryWitness ConstructionRecoveryWitness(ConstructionOrder order)
        {
            var witness = new ColonyConstructionRecoveryWitness { WorldId = state.WorldId, ContextKey = ContextKey, OrderId = order.Id,
                ObservedUt = Planetarium.GetUniversalTime(), Reason = "Current placement provider is unavailable; original paid order remains held." };
            var scenario = ColonyPlacementScenario.Instance;
            if (!Ready || Current != this || !ReferenceEquals(selectedGame, HighLogic.CurrentGame) || FlightGlobals.Vessels == null || scenario == null || !scenario.Ready ||
                scenario.WorldId != state.WorldId || HighLogic.CurrentGame.scenarios == null ||
                !HighLogic.CurrentGame.scenarios.Any(s => ReferenceEquals(s.moduleRef, scenario) && ReferenceEquals(s, scenario.snapshot))) return witness;
            ColonyPlacementRecord record;
            if (!scenario.Records.TryGetValue(order.Placement.OperationId, out record)) return witness;
            var status = scenario.GetStatus(order.Placement.OperationId);
            if (status == null || record.Request.WorldId != state.WorldId || record.Request.ColonyId != order.ColonyId || record.Request.PlotId != order.PlotId ||
                record.Request.OperationId != order.Placement.OperationId || record.Request.Fingerprint() != order.Placement.RequestFingerprint ||
                status.RequestFingerprint != order.Placement.RequestFingerprint || record.Request.EscrowWitness != order.Placement.EscrowWitness)
            { witness.Reason = "Provider request identity differs from the original paid placement; retry is held."; return witness; }
            witness.OperationId = record.Request.OperationId; witness.RequestFingerprint = status.RequestFingerprint;
            witness.PayloadHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(ColonyPlacementCodec.WriteRequest(record.Request).ToString()));
            witness.EscrowWitness = record.Request.EscrowWitness; witness.Phase = status.Stage.ToString(); witness.RetryOperationId = status.RetryOperationId;
            witness.AssemblyAttempted = status.AssemblyAttempted; witness.MarkedVessels = ColonyPlacementRuntime.MarkedVessels(record.Request.OperationId).Length;
            witness.AfterWitness = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(ColonyPlacementCodec.WriteStatus(status).ToString()));
            witness.Reason = Bound(status.Reason ?? "", 512); witness.Current = witness.PayloadHash == order.Placement.PayloadHash;
            if (!witness.Current) witness.Reason = "Provider payload differs from the original paid placement; retry is held.";
            return witness;
        }

        private void PopulateConstructionRecovery(ColonyEnvironment env)
        {
            env.ConstructionRecovery.Clear();
            if (state == null) return;
            foreach (var order in state.Construction.Where(o => o.Placement.OperationId.Length > 0 && o.State == "held"))
                env.ConstructionRecovery[order.Id] = ConstructionRecoveryWitness(order);
        }

        private bool PumpConstructionRetry(ColonyPlacementScenario scenario)
        {
            var pending = state.Effects.FirstOrDefault(e => e.Kind == ColonyEngine.ConstructionRetryKind && e.State != "applied" && e.State != "cancelled");
            if (pending == null) return false;
            var order = state.Construction.Single(o => o.Id == pending.TargetId);
            var game = HighLogic.CurrentGame; string epoch = loadEpoch;
            mutating = true;
            try
            {
                var witness = ConstructionRecoveryWitness(order);
                ColonyEngine.ValidateRetryWitness(state, order, ContextKey, Planetarium.GetUniversalTime(), witness);
                // After a load/crash, the retained provider retry ID is the acknowledgement.
                // Never re-arm a later RecoveryHold or an actual assembly for the same retry.
                if (witness.RetryOperationId != pending.Id)
                {
                    if (witness.Phase != "RecoveryHold" || witness.AssemblyAttempted || witness.MarkedVessels != 0 ||
                        order.Placement.AssemblyAttempted || order.FacilityId.Length > 0 ||
                        state.Effects.Any(e => (e.State == "held" || e.State == "applying") && e.Id != pending.Id && e.Id != order.Placement.EffectId))
                        throw new InvalidOperationException("Original unattempted hold no longer qualifies; provider retry was not repeated.");
                    if (!GetConstructionEnvironment().Templates.Any(t => t.Id == order.TemplateId && t.Hash == order.TemplateHash))
                        throw new InvalidOperationException("Original paid package changed; retry cannot amend deployment terms.");
                    Accept(ColonyEngine.MarkConstructionRetryApplying(state, pending.Id)); // Serialize before the native queue transition.
                    if (!ReferenceEquals(game, HighLogic.CurrentGame) || epoch != loadEpoch || scenario != ColonyPlacementScenario.Instance)
                        throw new InvalidOperationException("Selected world changed before placement retry.");
                    string reason;
                    if (!scenario.RetryUnattempted(order.Placement.OperationId, pending.Id, out reason)) throw new InvalidOperationException(reason);
                    witness = ConstructionRecoveryWitness(order);
                }
                if (!ReferenceEquals(game, HighLogic.CurrentGame) || epoch != loadEpoch) throw new InvalidOperationException("Selected world changed across placement retry readback.");
                Accept(ColonyEngine.CompleteConstructionRetry(state, pending.Id, ContextKey, Planetarium.GetUniversalTime(), witness));
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(game, HighLogic.CurrentGame) || !ReferenceEquals(selectedGame, game) || epoch != loadEpoch || Current != this) return true;
                string reason = Bound("Original paid placement retry held: " + ex.Message, 512);
                var saved = state.Effects.Single(e => e.Id == pending.Id);
                if (saved.State != "held" || saved.Reason != reason) Accept(ColonyEngine.HoldEffect(state, pending.Id, reason));
            }
            finally { mutating = false; }
            return true;
        }
    }
}
