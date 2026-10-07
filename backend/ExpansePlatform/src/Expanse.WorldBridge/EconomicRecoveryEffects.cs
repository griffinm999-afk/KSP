using System;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private bool economicRecoveryApplying;

        // Read only from the Unity game thread. Worker capability JSON uses the
        // captured EffectContext instead of touching Funding or HighLogic.
        private static bool EconomicRecoveryAvailable
        {
            get
            {
                return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER &&
                    Funding.Instance != null && SafeFinite(Funding.Instance.Funds) && Funding.Instance.Funds >= 0;
            }
        }

        private EffectReceipt ApplyEconomicRecoveryProposal(EffectProposal proposal, AcceptedState prior,
            RecoveryCapsuleModule recovery, double appliedUt)
        {
            DeliveryEffectPayload delivery = proposal.Delivery;
            EconomicRecoveryIntent intent = delivery == null ? null : delivery.RecoveryIntent;
            if (economicRecoveryApplying || !EconomicRecoveryAvailable || recovery == null ||
                delivery == null || delivery.Kind != "recoverySale" || intent == null ||
                delivery.PhysicalEffect != null || delivery.ScheduleRuleUpdate != null)
                return BuildRejectedReceipt(proposal, "Kerbin recovery requires an available Career funds account and a typed recovery intent");

            Funding funding = Funding.Instance;
            Game game = HighLogic.CurrentGame;
            string startEpoch = loadEpoch, startRun = runId;
            double before = funding.Funds;
            double after = before + intent.FundsDelta;
            if (!SafeFinite(after) || after <= before || after - before != intent.FundsDelta)
                return BuildRejectedReceipt(proposal, "Funds account cannot represent the exact recovery payment");

            FundsSuccessWitness witness = new FundsSuccessWitness
            {
                BeforeFunds = before, IntendedDeltaFunds = intent.FundsDelta,
                IntendedAfterFunds = after, ObservedAfterFunds = after
            };
            StateTransitionResult accepted = AcceptedStateCodec.SettleRecovery(prior, proposal.OperationId,
                proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision,
                proposal.ExpectedStateHash, intent, appliedUt, witness);
            if (accepted.Outcome != "accepted")
                return BuildReceipt(proposal, accepted.Outcome, appliedUt, 0, prior, accepted.Reason);

            string prepareReason;
            RecoveryCapsuleModule.PreparedAcceptedState successPrepared = recovery.PrepareAcceptedStateReplacement(
                prior.WorldId, AcceptedStateCodec.Serialize(accepted.State), out prepareReason);
            if (successPrepared == null) return BuildRejectedReceipt(proposal, prepareReason ?? "Recovery payment witness could not be prepared");

            EconomicEffectResult uncertain = new EconomicEffectResult
            {
                Status = "uncertain", BeforeFunds = before, IntendedDeltaFunds = intent.FundsDelta,
                IntendedAfterFunds = after, ObservedAfterKnown = false, ObservedAfterFunds = 0,
                Reason = "Recovery payment started; exact funds outcome has not been verified"
            };
            StateTransitionResult fault = AcceptedStateCodec.FaultRecovery(prior, proposal.OperationId,
                proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision,
                proposal.ExpectedStateHash, intent, uncertain, appliedUt);
            RecoveryCapsuleModule.PreparedAcceptedState faultPrepared = fault.Outcome == "faulted"
                ? recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(fault.State), out prepareReason)
                : null;
            if (faultPrepared == null) return BuildRejectedReceipt(proposal, fault.Reason ?? prepareReason ?? "Conservative funds fault could not be reserved");

            economicRecoveryApplying = true;
            try
            {
                // Funding.SetFunds invokes mod callbacks. Precommit a terminal uncertain
                // receipt so a callback-triggered save cannot leave a payable shipment
                // beside credited funds. The verified success swap occurs afterwards.
                recovery.CommitPreparedState(faultPrepared);
                string mutationError = null;
                try { funding.SetFunds(after, TransactionReasons.VesselRecovery); }
                catch (Exception ex) { mutationError = BoundEffect(ex.Message, 192); }

                bool sameWorld = object.ReferenceEquals(HighLogic.CurrentGame, game) &&
                    object.ReferenceEquals(Funding.Instance, funding) &&
                    object.ReferenceEquals(RecoveryCapsuleModule.Instance, recovery) &&
                    startEpoch == loadEpoch && startRun == runId &&
                    recovery.WorldId == prior.WorldId && recovery.StateHash == faultPrepared.Hash;
                if (!sameWorld)
                    return BuildRejectedReceipt(proposal, "World or funds context changed inside a recovery callback; payment must be reconciled from the selected save");

                double observed = funding.Funds;
                if (SafeFinite(observed) && observed == after)
                {
                    recovery.CommitPreparedState(successPrepared);
                    EconomicEffectResult result = new EconomicEffectResult
                    {
                        Status = "applied", BeforeFunds = before, IntendedDeltaFunds = intent.FundsDelta,
                        IntendedAfterFunds = after, ObservedAfterKnown = true, ObservedAfterFunds = observed,
                        Reason = "Simulated Kerbin landing and recovery completed"
                    };
                    return BuildReceipt(proposal, "accepted", appliedUt, 0, accepted.State, null,
                        witness: successPrepared, economicResult: result);
                }

                uncertain.ObservedAfterKnown = SafeFinite(observed) && observed >= 0;
                uncertain.ObservedAfterFunds = uncertain.ObservedAfterKnown ? observed : 0;
                uncertain.Reason = mutationError ?? "Funds readback differs from the exact recovery payment; automatic settlement is blocked";
                StateTransitionResult exactFault = AcceptedStateCodec.FaultRecovery(prior, proposal.OperationId,
                    proposal.ClientRequestId, proposal.CommandSequence, proposal.PayloadHash, proposal.ExpectedRevision,
                    proposal.ExpectedStateHash, intent, uncertain, appliedUt);
                RecoveryCapsuleModule.PreparedAcceptedState exactPrepared = exactFault.Outcome == "faulted"
                    ? recovery.PrepareAcceptedStateReplacement(prior.WorldId, AcceptedStateCodec.Serialize(exactFault.State), out prepareReason)
                    : null;
                if (exactPrepared != null) { recovery.CommitPreparedState(exactPrepared); fault = exactFault; faultPrepared = exactPrepared; }
                else uncertain = Array.Find(fault.State.EconomicFaults, x => x.OperationId == proposal.OperationId).Result;
                return BuildReceipt(proposal, "faulted", appliedUt, 0, fault.State, uncertain.Reason,
                    witness: faultPrepared, economicResult: uncertain);
            }
            finally { economicRecoveryApplying = false; }
        }

        private static string EconomicResultJson(EconomicEffectResult result)
        {
            if (result == null) return "null";
            return "{\"status\":" + Q(result.Status) + ",\"beforeFunds\":" + F(result.BeforeFunds) +
                ",\"intendedDeltaFunds\":" + I(result.IntendedDeltaFunds) + ",\"intendedAfterFunds\":" + F(result.IntendedAfterFunds) +
                ",\"observedAfterKnown\":" + (result.ObservedAfterKnown ? "true" : "false") +
                ",\"observedAfterFunds\":" + F(result.ObservedAfterFunds) + ",\"reason\":" + Q(result.Reason) + "}";
        }
    }
}
