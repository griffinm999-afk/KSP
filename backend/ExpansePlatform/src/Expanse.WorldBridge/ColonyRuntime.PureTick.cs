using System;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        ColonyPureTickWork pendingPureTick;

        void InvalidatePureTick()
        {
            // Retain an invalid running operation until it finishes. This keeps
            // at most one pure task per runtime; no cancellation wait or queue.
            if (pendingPureTick != null) pendingPureTick.Invalidate();
        }

        void CompletePureTick()
        {
            var operation = pendingPureTick;
            if (operation == null || !operation.IsCompleted) return;
            pendingPureTick = null;
            ColonyPureTickWork.Result result; string error;
            if (!operation.TryTake(state, HighLogic.CurrentGame, state == null ? "" : state.WorldId, ContextKey, loadEpoch, out result, out error))
            {
                if (error.Length > 0) Debug.LogWarning("[ExpanseColony] Pure tick discarded: " + Bound(error, 360));
                return;
            }
            if (Current != this || !Ready || mutating || !ReferenceEquals(selectedGame, HighLogic.CurrentGame) ||
                FlightDriver.Pause || Time.timeScale <= 0 || Planetarium.GetUniversalTime() < result.State.SimulatedUt) return;
            if (!result.AdvanceOnly && (WorldBridgeAddon.Current == null || WorldBridgeAddon.Current.LegacyAuthorityWriteHeld)) return;
            long started = performance.Start();
            try
            {
                Accept(result.State, result.Bytes);
                if (WorldBridgeAddon.Current == null || WorldBridgeAddon.Current.LegacyAuthorityWriteHeld) return;
                if (result.AdvanceOnly && result.Environment.Ut == state.SimulatedUt)
                {
                    // Advance is already accepted for saves. Survey remains on
                    // the main thread before a detached post-survey pure pass.
                    try { QueuePlanningPolicy(result.Environment, operation); }
                    catch (Exception ex) { Debug.LogWarning("[ExpanseColony] Pure planning snapshot deferred: " + Bound(ex.Message, 360)); }
                    return;
                }
                if (!result.AdvanceOnly) planningCursor = result.NextCursor;
                RunAcceptedTickEffects(result.Environment);
            }
            catch (Exception ex) { HoldReason = "Colony processing held: " + Bound(ex.Message, 360); Debug.LogError("[ExpanseColony] " + HoldReason); }
            finally { performance.End(ColonyPerformanceChannel.ActiveTick, started); }
        }

        void RunAcceptedTickEffects(ColonyEnvironment observed)
        {
            // No native effect runs in the worker. Every positive effect keeps
            // its existing fresh main-thread preflight, durable hold/readback.
            // The older worker observation can only skip a current negative;
            // stale observations follow the fresh physical environment path.
            ApplyOneFundsEffect();
            ApplyOneWolfEffect();
            ApplyOnePeopleEffect();
            ApplyOneServiceEffect();
            ApplyOnePhysicalProcurementEffect(observed);
            PumpConstruction();
            RunProductionEffects();
        }
    }
}
