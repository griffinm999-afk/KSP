using System;
using System.Linq;
using System.Threading.Tasks;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    // One owner-thread operation, with Advance then planning worker passes.
    // Task delegates capture only owned bytes/scalars, never these authority
    // tokens, the runtime, Game, Unity objects or a caller's mutable graph.
    internal sealed class ColonyPureTickWork
    {
        internal sealed class Result
        {
            internal ColonyState State;
            internal byte[] Bytes;
            internal ColonyEnvironment Environment;
            internal string NextCursor, Error;
            internal bool AdvanceOnly;
        }

        object priorIdentity, gameIdentity;
        string world, context, epoch;
        Task<Result> task;
        bool invalid, taken;
        internal bool IsCompleted { get { return task.IsCompleted; } }

        internal ColonyPureTickWork(object prior, object game, string worldId, string contextKey, string loadEpoch,
            byte[] acceptedBytes, ColonyEnvironment environment)
        {
            Start(prior, game, worldId, contextKey, loadEpoch, acceptedBytes, environment, true, "");
        }

        internal void Invalidate() { invalid = true; }

        internal void StartPlanning(object prior, object game, string worldId, string contextKey, string loadEpoch,
            byte[] acceptedBytes, ColonyEnvironment environment, string cursor)
        {
            if (!task.IsCompleted || !taken || invalid) throw new InvalidOperationException("Previous pure pass is still pending or invalid.");
            Start(prior, game, worldId, contextKey, loadEpoch, acceptedBytes, environment, false, cursor);
        }

        void Start(object prior, object game, string worldId, string contextKey, string loadEpoch,
            byte[] acceptedBytes, ColonyEnvironment environment, bool advanceOnly, string cursor)
        {
            // These tokens are inspected only on the owner thread. Bytes are
            // copied/snapshotted before scheduling, so later loads/commands and
            // template/observation refreshes cannot touch the worker's input.
            priorIdentity = prior; gameIdentity = game; world = worldId; context = contextKey; epoch = loadEpoch;
            var stateBytes = (byte[])acceptedBytes.Clone();
            var environmentBytes = ColonyStateCodec.SerializeTickEnvironment(environment);
            taken = false;
            task = Task.Run(() => Run(stateBytes, environmentBytes, advanceOnly, cursor));
        }

        internal bool TryTake(object prior, object game, string worldId, string contextKey, string loadEpoch,
            out Result result, out string error)
        {
            result = null; error = "";
            if (!task.IsCompleted || taken) return false;
            taken = true;
            // Reading Result is nonblocking after this completed/fault fence.
            if (task.IsFaulted) { error = task.Exception.GetBaseException().Message; return false; }
            if (task.IsCanceled) return false;
            var completed = task.Result;
            if (invalid || !ReferenceEquals(prior, priorIdentity) || !ReferenceEquals(game, gameIdentity) ||
                worldId != world || contextKey != context || loadEpoch != epoch) return false;
            if (!string.IsNullOrEmpty(completed.Error)) { error = completed.Error; return false; }
            if (completed.State.WorldId != world || completed.Environment.WorldId != world || completed.Environment.ContextKey != context) return false;
            result = completed; return true;
        }

        static Result Run(byte[] stateBytes, byte[] environmentBytes, bool advanceOnly, string cursor)
        {
            try
            {
                var state = ColonyStateCodec.Deserialize(stateBytes);
                var environment = ColonyStateCodec.DeserializeTickEnvironment(environmentBytes);
                byte[] bytes;
                string nextCursor = "";
                if (advanceOnly)
                {
                    state = ColonyEngine.Advance(state, environment, out bytes);
                    if (bytes == null) bytes = ColonyStateCodec.Serialize(state);
                }
                else
                {
                    var active = state.Plans.Where(p => p.State != "cancelled" && p.State != "complete")
                        .OrderBy(p => p.CreatedUt).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
                    int index = active.FindIndex(p => p.Id == cursor);
                    nextCursor = active.Count == 0 ? "" : active[(index + 1) % active.Count].Id;
                    var planned = ColonyEngine.RunPlanningAndProcurement(state, environment, cursor);
                    bytes = ReferenceEquals(planned, state) ? stateBytes : ColonyStateCodec.Serialize(planned);
                    state = planned;
                }
                return new Result { State = state, Bytes = bytes, Environment = environment, AdvanceOnly = advanceOnly, NextCursor = nextCursor };
            }
            catch (Exception ex) { return new Result { Error = ex.Message }; }
        }
    }
}
