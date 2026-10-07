using System;
using System.Collections.Generic;
using System.Linq;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    internal sealed class PhysicalInventoryTransferPlan
    {
        internal string OperationId, DepotId, WorldId, RegistryHash, RegistryVersion;
        internal long RegistryMutationRevision;
        internal object Game;
        internal DepotRegistryModule Registry;
        internal IInventoryGateway Gateway;
        internal InventoryMutationPlan Mutation;
        internal bool Attempted;
        internal PhysicalSuccessWitness Witness;
    }

    // The colony owns operation IDs, replay protection, stock/cargo and prepared
    // ledger bytes. These reference-swap callbacks must be prebuilt and synchronous.
    // The hold includes the exact before/after witness and must survive OnSave.
    internal sealed class PhysicalInventoryCommitBoundary
    {
        internal Func<bool> IsCurrent;
        internal Action CommitDurableHold;
        internal Action CommitSuccess;
        internal Action RestoreBeforeAfterConfirmedRollback;
    }

    internal sealed class PhysicalInventoryTransferResult
    {
        internal string Outcome, Reason;
        internal PhysicalSuccessWitness Witness;
        internal InventoryAppliedRow[] ObservedRows;
    }

    /// <summary>
    /// Common physical-to-ledger or ledger-to-physical boundary. It never invents
    /// colony inventory or pays funds. Callers must prepare their conserved ledger
    /// transition and durable hold before Commit; uncertain effects retain that hold.
    /// </summary>
    internal static class PhysicalInventoryTransfers
    {
        internal static bool TryObserve(string depotId,out InventoryObservation observation,out InventoryCapabilityEvidence capability,out string membershipHash,out string reason)
        {
            observation=null;capability=null;membershipHash="";reason="Selected-save physical registry is unavailable.";
            var registry=DepotRegistryModule.Instance;if(registry==null||!registry.IsReady||registry.IsCorrupt)return false;
            var registrations=registry.Registrations.Where(r=>r.DepotId==depotId).ToArray();if(registrations.Length!=1)return false;
            var registration=registrations[0];var authority=Capture(registry,depotId);if(authority==null)return false;
            var record=registry.CreateSelectedEffectRegistrySnapshot(depotId)?.Depots.SingleOrDefault(r=>r.DepotId==depotId&&r.Active);if(record==null||!Current(authority))return false;
            var members=registration.MemberIds.OrderBy(x=>x).ToArray();var endpoint=new InventoryEndpoint {DepotId=depotId,MembershipRevision=registration.MembershipRevision,AnchorPersistentId=registration.Anchor,MemberPersistentIds=members,MembershipHash=record.MembershipHash,MemberSetHash=OperationIdentity.ComputeMemberSetHash(registration.Anchor,members),Scene=HighLogic.LoadedScene.ToString()};
            IInventoryGateway gateway=new LoadedBrpInventoryGateway();var resolution=gateway.Resolve(endpoint);if(resolution==null){gateway=new RemoteBrpInventoryGateway();resolution=gateway.Resolve(endpoint);}
            capability=gateway.Describe(endpoint);membershipHash=record.MembershipHash;reason=capability.HoldReason;
            observation=gateway.Observe(resolution);if(!Current(authority)||!ProviderCurrent(gateway,resolution)||!ValidObservation(observation,endpoint))
            {observation=null;capability=null;membershipHash="";reason="Selected registry or physical observation changed while reading the endpoint.";return false;}
            reason=capability.CanApply?null:capability.HoldReason;return true;
        }
        internal static bool TryPrepare(string operationId, string depotId, ResourceAmount[] resources, bool debit,
            out PhysicalInventoryTransferPlan prepared, out string reason, uint targetMemberId = 0)
        {
            prepared = null; reason = "Selected physical inventory is unavailable.";
            var registry = DepotRegistryModule.Instance;
            if (String.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || registry == null || !registry.IsReady || registry.IsCorrupt) return false;
            var registrations = registry.Registrations.Where(x => x.DepotId == depotId).ToArray();
            if (registrations.Length != 1) return false;
            var registration = registrations[0]; var authority = Capture(registry,depotId);
            var snapshot = registry.CreateSelectedEffectRegistrySnapshot(depotId);
            if(authority==null||snapshot==null||!Current(authority))return false;
            var record = snapshot.Depots.SingleOrDefault(x => x.DepotId == depotId);
            if (record == null || !record.Active) return false;
            var members = registration.MemberIds.OrderBy(x => x).ToArray();
            if (targetMemberId != 0 && !members.Contains(targetMemberId))
            { reason = "Requested service tank is outside the registered physical endpoint."; return false; }
            var endpoint = new InventoryEndpoint { DepotId = depotId, MembershipRevision = registration.MembershipRevision,
                AnchorPersistentId = registration.Anchor, MemberPersistentIds = members, MembershipHash = record.MembershipHash,
                MemberSetHash = OperationIdentity.ComputeMemberSetHash(registration.Anchor, members), Scene = HighLogic.LoadedScene.ToString() };
            IInventoryGateway gateway = new LoadedBrpInventoryGateway();
            var resolution = gateway.Resolve(endpoint);
            if (resolution == null) { gateway = new RemoteBrpInventoryGateway(); resolution = gateway.Resolve(endpoint); }
            var observed = gateway.Observe(resolution);
            if (!Current(authority)||!ProviderCurrent(gateway,resolution)||!ValidObservation(observed,endpoint)) return false;
            var capability = gateway.Describe(endpoint);
            if (!capability.CanApply) { reason = capability.HoldReason; return false; }
            if (resources == null || resources.Length == 0 || resources.Length > 16 || resources.Any(x => x == null || String.IsNullOrWhiteSpace(x.ResourceName) || x.AmountMicroUnits <= 0) || resources.Select(x => x.ResourceName).Distinct(StringComparer.Ordinal).Count() != resources.Length)
            { reason = "Physical transfer manifest is invalid."; return false; }
            var deltas = new List<InventoryDelta>();
            foreach (var resource in resources.OrderBy(x => x.ResourceName, StringComparer.Ordinal))
            {
                long left = resource.AmountMicroUnits;
                foreach (var row in observed.Rows.Where(x => x.ResourceName == resource.ResourceName && (targetMemberId == 0 || x.MemberPersistentId == targetMemberId)).OrderBy(x => x.MemberPersistentId))
                {
                    if (debit && row.DebitAllowed != true) continue;
                    double value = (debit ? row.Amount : row.Capacity - row.Amount) * 1000000d;
                    if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value >= 9223372036854775808d)
                    { reason = "Physical stock exceeds the supported micro-unit range."; return false; }
                    // Floor free space itself, rather than capacity-stock floors, to
                    // avoid promising a final fractional micro-unit that cannot fit.
                    long amount = Math.Min(left, (long)Math.Floor(value));
                    if (amount > 0) { deltas.Add(new InventoryDelta { MemberPersistentId = row.MemberPersistentId, ResourceName = resource.ResourceName, DeltaMicroUnits = debit ? -amount : amount }); left -= amount; }
                    if (left == 0) break;
                }
                if (left != 0) { reason = debit ? "Selected unlocked source stock is insufficient." : "Selected destination has insufficient free tank capacity."; return false; }
            }
            if(!Current(authority)||!ProviderCurrent(gateway,resolution))return false;
            var preflight = gateway.Preflight(resolution, deltas);
            if (preflight == null || !preflight.Accepted || preflight.Plan == null) { reason = preflight == null ? reason : preflight.Reason; return false; }
            if(!Current(authority)||!ProviderCurrent(gateway,resolution))return false;
            prepared=authority;prepared.OperationId=operationId;prepared.Gateway=gateway;prepared.Mutation=preflight.Plan;
            prepared.Witness = new PhysicalSuccessWitness { ProviderId = observed.ProviderId, ProviderVersion = observed.ProviderVersion,
                    Rows = preflight.Plan.Rows.Select(x => new PhysicalSuccessWitnessRow { MemberPersistentId = x.MemberPersistentId, ResourceName = x.ResourceName,
                        BeforeAmount = x.BeforeAmount, IntendedAfterAmount = x.IntendedAfterAmount, ObservedAfterAmount = x.IntendedAfterAmount }).ToArray() };
            reason = null; return true;
        }

        internal static PhysicalInventoryTransferResult Commit(PhysicalInventoryTransferPlan plan, PhysicalInventoryCommitBoundary boundary)
        {
            if (plan == null || plan.Attempted || boundary == null || boundary.IsCurrent == null || boundary.CommitDurableHold == null || boundary.CommitSuccess == null || boundary.RestoreBeforeAfterConfirmedRollback == null || !Current(plan) || !boundary.IsCurrent() || plan.Mutation == null || !ProviderCurrent(plan.Gateway,plan.Mutation.Resolution))
                return Result("held", "Physical transfer plan or durable ledger boundary is unavailable or stale.", plan, null);
            plan.Attempted = true;
            plan.Mutation.AuthorityIsCurrent=()=>Current(plan)&&boundary.IsCurrent();
            // Any callback-triggered save from the provider sees a terminal hold,
            // never unreserved physical stock alongside a credited colony ledger.
            boundary.CommitDurableHold();
            if (!Current(plan) || !boundary.IsCurrent() || !ProviderCurrent(plan.Gateway,plan.Mutation.Resolution)) return Result("uncertain", "Selected world or native provider changed while reserving the durable physical hold.", plan, null);
            var applied = plan.Gateway.Apply(plan.Mutation);
            if (applied.Succeeded && Current(plan) && boundary.IsCurrent())
            {
                var sync = plan.Gateway.Synchronize(plan.Mutation);
                if (sync.Succeeded && Exact(plan.Mutation) && Current(plan) && boundary.IsCurrent() && ProviderCurrent(plan.Gateway,plan.Mutation.Resolution))
                {
                    boundary.CommitSuccess();
                    plan.Registry.InvalidateEffectInventoryObservations();
                    return Result("accepted", null, plan, applied.Rows);
                }
            }
            if (!Current(plan) || !boundary.IsCurrent()) return Result("uncertain", "Provider context changed; selected-save hold requires reconciliation.", plan, applied.Rows);
            var rollback = plan.Gateway.Rollback(plan.Mutation);
            if (rollback.Confirmed && Current(plan) && boundary.IsCurrent() && ProviderCurrent(plan.Gateway,plan.Mutation.Resolution))
            {
                boundary.RestoreBeforeAfterConfirmedRollback();
                return Result("rejected", applied.Reason ?? "Physical effect failed; exact rollback was confirmed.", plan, rollback.Rows);
            }
            return Result("uncertain", "Physical effect or rollback could not be confirmed; durable hold retained. " + (applied.Reason ?? rollback.Reason), plan, rollback.Rows);
        }
        static bool ProviderCurrent(IInventoryGateway gateway, InventoryResolution resolution) =>
            gateway is LoadedBrpInventoryGateway loaded ? loaded.SelectedContextCurrent(resolution) :
            gateway is RemoteBrpInventoryGateway remote && remote.SelectedContextCurrent(resolution);
        static PhysicalInventoryTransferPlan Capture(DepotRegistryModule registry,string depotId)
        {
            var snapshot=registry.CreateSelectedEffectRegistrySnapshot(depotId);
            if(snapshot==null||snapshot.Depots==null||snapshot.Depots.Length!=1||snapshot.Depots[0].DepotId!=depotId||!snapshot.Depots[0].Active)return null;
            return new PhysicalInventoryTransferPlan {DepotId=depotId,WorldId=registry.WorldId,Registry=registry,RegistryHash=snapshot.RegistryHash,
                RegistryVersion=snapshot.RegistryVersion,RegistryMutationRevision=registry.MutationRevision,Game=HighLogic.CurrentGame};
        }
        static bool Current(PhysicalInventoryTransferPlan plan)
        {
            if(plan==null||plan.Registry==null||!Object.ReferenceEquals(plan.Registry,DepotRegistryModule.Instance)||!Object.ReferenceEquals(plan.Game,HighLogic.CurrentGame)||
                !plan.Registry.IsReady||plan.Registry.IsCorrupt||plan.WorldId!=plan.Registry.WorldId||plan.RegistryMutationRevision!=plan.Registry.MutationRevision)return false;
            var snapshot=plan.Registry.CreateSelectedEffectRegistrySnapshot(plan.DepotId);
            return snapshot!=null&&snapshot.RegistryVersion==plan.RegistryVersion&&snapshot.RegistryHash==plan.RegistryHash;
        }
        static bool ValidObservation(InventoryObservation observed,InventoryEndpoint endpoint)
        {
            return observed!=null&&observed.Rows!=null&&observed.Rows.Length<=512&&observed.MembershipRevision==endpoint.MembershipRevision&&
                observed.Rows.All(r=>r!=null&&endpoint.MemberPersistentIds.Contains(r.MemberPersistentId)&&!String.IsNullOrWhiteSpace(r.ResourceName)&&
                    !Double.IsNaN(r.Amount)&&!Double.IsInfinity(r.Amount)&&!Double.IsNaN(r.Capacity)&&!Double.IsInfinity(r.Capacity)&&r.Amount>=0&&r.Capacity>=r.Amount)&&
                observed.Rows.Select(r=>r.MemberPersistentId+":"+r.ResourceName).Distinct(StringComparer.Ordinal).Count()==observed.Rows.Length;
        }
        static bool Exact(InventoryMutationPlan plan) => plan.Rows.All(x => (x.Resource == null ? x.ProtoResource.amount : x.Resource.amount) == x.IntendedAfterAmount);
        static PhysicalInventoryTransferResult Result(string outcome, string reason, PhysicalInventoryTransferPlan plan, InventoryAppliedRow[] rows) => new PhysicalInventoryTransferResult { Outcome = outcome, Reason = reason, Witness = plan == null ? null : plan.Witness, ObservedRows = rows ?? new InventoryAppliedRow[0] };
    }
}
