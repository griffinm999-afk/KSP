using System;
using System.Collections.Generic;

namespace Expanse.WorldBridge
{
    // Game-thread-only boundary between selected depot membership and a physical provider.
    // Capability fields are evidence; an endpoint is writable only when every required
    // capability is true. Implementations must not infer support from proto amount access.
    internal interface IInventoryGateway
    {
        InventoryCapabilityEvidence Describe(InventoryEndpoint endpoint);
        InventoryResolution Resolve(InventoryEndpoint endpoint);
        InventoryObservation Observe(InventoryResolution resolution);
        InventoryPreflightResult Preflight(InventoryResolution resolution, IList<InventoryDelta> deltas);
        InventoryApplyResult Apply(InventoryMutationPlan plan);
        InventoryRollbackResult Rollback(InventoryMutationPlan plan);
        InventorySyncResult Synchronize(InventoryMutationPlan plan);
    }

    internal sealed class InventoryEndpoint
    {
        public string DepotId;
        public int MembershipRevision;
        public uint[] MemberPersistentIds;
        public uint AnchorPersistentId;
        public string MembershipHash;
        public string MemberSetHash;
        public string Scene;
    }

    internal sealed class InventoryCapabilityEvidence
    {
        public string ProviderId;
        public string ProviderVersion;
        public string Scene;
        public bool ObservationAvailable;
        public bool ReadSupported;
        public bool WriteSupported;
        public bool SynchronousRollbackSupported;
        public bool PersistenceSyncSupported;
        public string HoldReason;

        public bool CanApply
        {
            get
            {
                return ObservationAvailable && ReadSupported && WriteSupported &&
                    SynchronousRollbackSupported && PersistenceSyncSupported;
            }
        }
    }

    internal sealed class InventoryResolution
    {
        internal InventoryResolution(InventoryEndpoint endpoint, object providerContext, string contextToken)
        { Endpoint = endpoint; ProviderContext = providerContext; ContextToken = contextToken; }
        public readonly InventoryEndpoint Endpoint;
        internal readonly object ProviderContext;
        public readonly string ContextToken;
    }

    internal sealed class InventoryObservation
    {
        public string ContextToken;
        public string ObservationVersion;
        public string ProviderId;
        public string ProviderVersion;
        public int MembershipRevision;
        public InventoryStockRow[] Rows;
    }

    internal sealed class InventoryStockRow
    {
        public uint MemberPersistentId;
        public string ResourceName;
        public double Amount;
        public double Capacity;
        public bool? DebitAllowed;
    }

    internal sealed class InventoryDelta
    {
        public InventoryDelta() { MemberPersistentId = 0; ResourceName = String.Empty; DeltaMicroUnits = 0; }
        public uint MemberPersistentId;
        public string ResourceName;
        // Signed exact intended change in millionths of a KSP resource unit.
        public long DeltaMicroUnits;
    }

    internal sealed class InventoryMutationPlan
    {
        internal InventoryMutationPlan(InventoryResolution resolution, string contextToken, List<InventoryMutationRow> rows)
        { Resolution = resolution; ContextToken = contextToken; Rows = rows.AsReadOnly(); }
        public readonly InventoryResolution Resolution;
        public readonly string ContextToken;
        public readonly IList<InventoryMutationRow> Rows;
        internal int AttemptedCount;
        // Optional selected-save authority. Null retains legacy behavior.
        internal Func<bool> AuthorityIsCurrent;
        internal bool AuthorityCurrent()
        {try{return AuthorityIsCurrent==null||AuthorityIsCurrent();}catch{return false;}}
    }

    internal sealed class InventoryMutationRow
    {
        internal PartResource Resource;
        internal ProtoPartResourceSnapshot ProtoResource;
        internal object ProviderInventory;
        internal object ProviderSnapshot;
        internal System.Reflection.FieldInfo ProviderAmountField;
        internal System.Reflection.FieldInfo ProviderOriginalAmountField;
        internal System.Reflection.FieldInfo SnapshotAmountField;
        public uint MemberPersistentId;
        public string ResourceName;
        public double BeforeAmount;
        public double BeforeProviderAmount;
        public double BeforeOriginalAmount;
        public double BeforeSnapshotAmount;
        public bool HasProviderSnapshot;
        public double IntendedAfterAmount;
        public double Capacity;
        public long DeltaMicroUnits;
        internal string LineageKey;
        internal string BeforeConfigAmount;
        internal string BeforeConfigCapacity;
        internal bool BeforeFlowState;
    }

    internal sealed class InventoryPreflightResult
    {
        public bool Accepted;
        public string Reason;
        public InventoryObservation Observation;
        public InventoryMutationPlan Plan;
    }

    internal sealed class InventoryApplyResult
    {
        public bool Succeeded;
        public string Reason;
        public InventoryMutationPlan Plan;
        public InventoryAppliedRow[] Rows;
    }

    internal sealed class InventoryAppliedRow
    {
        public uint MemberPersistentId;
        public string ResourceName;
        public double BeforeAmount;
        public double IntendedAfterAmount;
        public double ObservedAfterAmount;
        public double ActualDelta;
    }

    internal sealed class InventoryRollbackResult
    {
        public bool Confirmed;
        public string Reason;
        public InventoryAppliedRow[] Rows;
    }

    internal sealed class InventorySyncResult
    {
        public bool Succeeded;
        public string Reason;
    }
}
