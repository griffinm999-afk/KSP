using System;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    /// <summary>Applies the selected-save unresolved-effect hold to projected management actions.</summary>
    public static class ColonyManagementCapabilityHolds
    {
        public static void Apply(ColonyManagementSnapshot snapshot, ColonyEnvironment environment)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            var state = snapshot.State;
            if (state == null) return;

            var unresolved = state.Effects.Where(effect => effect.State == "held" || effect.State == "applying").ToArray();
            if (unresolved.Length == 0) return;

            var hold = unresolved[0];
            foreach (var capability in snapshot.Capabilities.Where(item => item.Kind != "replanPaidWolfSupply"))
            {
                capability.Available = false;
                capability.Reason = "Reconcile external effect " + hold.OperationId + ": " + hold.Reason;
            }

            // A retry is the only exception: it must be this exact placement hold, with
            // no competing unresolved effect, and the engine must still validate its
            // paid terms and current provider witness against the projected state.
            if (unresolved.Length != 1 || hold.State != "held" || hold.Kind != "constructionPlacement") return;

            var order = state.Construction.SingleOrDefault(item => item.State == "held" &&
                item.Placement.EffectId == hold.Id && item.Placement.OperationId == hold.OperationId);
            if (order == null) return;

            var matching = snapshot.Capabilities.Where(item => item.Kind == "retryConstructionPlacement" &&
                item.ColonyId == order.ColonyId && item.TargetId == order.Id).ToArray();
            if (matching.Length != 1) return;

            string reason;
            bool available = ColonyEngine.CanRetryConstructionPlacement(state, order.Id, environment, out reason);
            matching[0].Available = available;
            matching[0].Reason = Bound(reason, 512);
        }

        static string Bound(string value, int limit)
        {
            value = value ?? "";
            return value.Length <= limit ? value : value.Substring(0, limit);
        }
    }
}
