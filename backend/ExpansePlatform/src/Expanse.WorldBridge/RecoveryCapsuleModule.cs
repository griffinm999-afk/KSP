using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Expanse.Domain;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // Owns the save-facing recovery capsule boundary. Typed accepted-state validation is
    // delegated to the shared Domain assembly; this class only bounds and preserves bytes.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public sealed class RecoveryCapsuleModule : ScenarioModule
    {
        private const int Schema = 1;
        private const int MaxStateBytes = 36 * 1024;
        private const int MaxEncodedCapsuleBytes = 48 * 1024;
        private static RecoveryCapsuleModule instance;

        private byte[] acceptedStateBytes;
        private AcceptedState acceptedState;
        private RecoveryCapsule acceptedCapsule;
        private ConfigNode acceptedCapsuleNode;
        private string acceptedStateHash;
        private string worldId;
        private readonly List<ConfigNode> preservedRawNodes = new List<ConfigNode>();
        private bool loaded;
        private bool corrupt;
        private string holdReason;
        private long observerFailureCount;
        private string observerFailureReason;
        private double observerFailureUt;
        internal long ObserverFailureCount { get { return observerFailureCount; } }
        internal string ObserverFailureReason { get { return observerFailureReason; } }
        internal void RecordSettlementObserverLoss(string reason, double ut)
        {
            try { if(observerFailureCount<long.MaxValue)observerFailureCount++;observerFailureReason=Bound(reason ?? "Observer coverage lost",512);observerFailureUt=ut; } catch { }
        }

        // Fully validated save witness prepared before any physical provider field is
        // changed. CommitPreparedState is deliberately a small main-thread reference swap.
        internal sealed class PreparedAcceptedState
        {
            internal byte[] Bytes;
            internal AcceptedState State;
            internal RecoveryCapsule Capsule;
            internal ConfigNode Node;
            internal string Hash;
            internal string World;
            internal ColonyRuntime.PreparedSettlement Settlement;
        }

        public static RecoveryCapsuleModule Instance { get { return instance; } }
        public string WorldId { get { return worldId; } }
        public bool IsLoaded { get { return loaded; } }
        public bool IsCorrupt { get { return corrupt; } }
        public bool HasAcceptedState { get { return acceptedStateBytes != null; } }
        public bool AuthorityWriteHeld { get { return !loaded || corrupt || acceptedState == null || acceptedState.WritesBlocked; } }
        public string StateHash { get { return acceptedStateHash; } }
        public string CheckpointId { get { return acceptedState == null ? null : acceptedState.CheckpointId; } }
        public long Revision { get { return acceptedState == null ? 0 : acceptedState.Revision; } }
        public long AcceptedSequence { get { return acceptedState == null ? 0 : acceptedState.AcceptedSequence; } }
        public long CompactionWatermark { get { return acceptedState == null ? 0 : acceptedState.CompactionWatermark; } }
        public RecoveryCapsule GetCapsuleCopy()
        {
            if (acceptedCapsule == null) return null;
            return new RecoveryCapsule { SchemaVersion = acceptedCapsule.SchemaVersion, WorldId = acceptedCapsule.WorldId, StateBytesBase64 = acceptedCapsule.StateBytesBase64, StateSha256 = acceptedCapsule.StateSha256 };
        }
        public string HoldReason { get { return holdReason; } }
        public byte[] GetAcceptedStateBytes()
        {
            return acceptedStateBytes == null ? null : (byte[])acceptedStateBytes.Clone();
        }

        public override void OnAwake()
        {
            base.OnAwake();
            instance = this;
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            observerFailureCount=0;observerFailureReason=null;observerFailureUt=0;
            var losses=node.GetNodes("SETTLEMENT_OBSERVER_LOSS");
            if(losses.Length>0)
            {
                long count;double ut;
                if(losses.Length==1 && long.TryParse(losses[0].GetValue("count"),out count) && count>0 && double.TryParse(losses[0].GetValue("ut"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out ut) && ut>=0 && !double.IsNaN(ut) && !double.IsInfinity(ut))
                {observerFailureCount=count;observerFailureUt=ut;observerFailureReason=Bound(losses[0].GetValue("reason") ?? "Persisted observer coverage loss",512);}
                else RecordSettlementObserverLoss("Malformed observer loss marker; coverage cannot be certified.",0);
            }
            // Replace all prior state even when the capsule is absent or invalid.
            acceptedStateBytes = null;
            acceptedState = null;
            acceptedCapsule = null;
            acceptedCapsuleNode = null;
            acceptedStateHash = null;
            worldId = null;
            preservedRawNodes.Clear();
            corrupt = false;
            holdReason = null;
            loaded = true;

            ConfigNode[] candidates = node.GetNodes("EXPANSE_RECOVERY");
            if (WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled)
                Debug.Log("[ExpanseRemoteBrp] recovery OnLoad capsuleNodes=" + candidates.Length +
                    " savedWorld=" + (candidates.Length == 1 ? candidates[0].GetValue("worldId") : "unavailable") +
                    " savedHash=" + (candidates.Length == 1 ? candidates[0].GetValue("stateSha256") : "unavailable") +
                    " scene=" + HighLogic.LoadedScene + " saveFolder=" + HighLogic.SaveFolder);
            if (candidates.Length == 0) return;
            if (candidates.Length != 1)
            {
                foreach (ConfigNode candidate in candidates) preservedRawNodes.Add(candidate.CreateCopy());
                corrupt = true;
                holdReason = "Multiple recovery capsule nodes are present";
                return;
            }

            ConfigNode saved = candidates[0];
            try
            {
                int schema = int.Parse(saved.GetValue("schemaVersion"), System.Globalization.CultureInfo.InvariantCulture);
                if (schema != Schema) throw new FormatException("Unsupported recovery schema " + schema);
                Guid parsedWorld = Guid.Parse(saved.GetValue("worldId"));
                if (parsedWorld == Guid.Empty) throw new FormatException("Empty world identifier");
                RecoveryCapsule capsule = new RecoveryCapsule
                {
                    SchemaVersion = schema,
                    WorldId = saved.GetValue("worldId"),
                    StateBytesBase64 = saved.GetValue("stateBytesBase64"),
                    StateSha256 = saved.GetValue("stateSha256")
                };
                if (string.IsNullOrEmpty(capsule.StateBytesBase64) || string.IsNullOrEmpty(capsule.StateSha256)) throw new FormatException("Recovery capsule is incomplete");
                // Check encoded size before decoding to keep corrupt saves bounded.
                if (Encoding.UTF8.GetByteCount(saved.ToString()) > MaxEncodedCapsuleBytes) throw new FormatException("Recovery capsule exceeds encoded size bound");
                AcceptedState accepted = AcceptedStateCodec.ReadCapsule(capsule);
                byte[] decoded = AcceptedStateCodec.DecodeCapsuleBytes(capsule.StateBytesBase64);

                if (!string.Equals(accepted.WorldId, parsedWorld.ToString("D"), StringComparison.OrdinalIgnoreCase)) throw new FormatException("Capsule state world does not match outer world identity");
                worldId = accepted.WorldId;
                acceptedStateBytes = decoded;
                acceptedState = accepted;
                // Re-encode legacy standard Base64 before a later save so a
                // decoded state can never acquire ConfigNode's // comment token.
                acceptedCapsule = AcceptedStateCodec.CreateCapsule(accepted);
                acceptedCapsuleNode = CreateNode(acceptedCapsule);
                acceptedStateHash = AcceptedStateCodec.ComputeHash(accepted);
                if (WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled)
                    Debug.Log("[ExpanseRemoteBrp] recovery OnLoad accepted world=" + worldId +
                        " sequence=" + accepted.AcceptedSequence + " hash=" + acceptedStateHash);
            }
            catch (Exception ex)
            {
                PreserveInvalid(node, "Recovery capsule is corrupt or unsupported: " + Bound(ex.Message, 192));
                if (WorldBridgeAddon.RemotePhysicalDiagnosticsEnabled)
                    Debug.Log("[ExpanseRemoteBrp] recovery OnLoad rejected reason=" + holdReason);
            }
        }

        // Called on the game thread only after the shared Domain validates a complete
        // accepted projection and confirms it belongs to DepotRegistryModule.WorldId.
        internal bool TryReplaceAcceptedState(string expectedWorldId, byte[] stateBytes, out string reason)
        {
            PreparedAcceptedState prepared = PrepareAcceptedStateReplacement(expectedWorldId, stateBytes, out reason);
            if (prepared == null) return false;
            CommitPreparedState(prepared);
            return true;
        }

        internal PreparedAcceptedState PrepareAcceptedStateReplacement(string expectedWorldId, byte[] stateBytes, out string reason)
        {
            reason = null;
            if (!loaded) { reason = "Recovery capsule has not loaded"; return null; }
            if (corrupt) { reason = holdReason ?? "Recovery capsule is corrupt"; return null; }
            Guid parsedWorld;
            if (!Guid.TryParse(expectedWorldId, out parsedWorld) || parsedWorld == Guid.Empty || stateBytes == null || stateBytes.Length == 0 || stateBytes.Length > MaxStateBytes)
            { reason = "Recovery state identity or size is invalid"; return null; }
            byte[] copy = (byte[])stateBytes.Clone();
            AcceptedState accepted;
            RecoveryCapsule capsule;
            ConfigNode capsuleNode;
            try
            {
                accepted = AcceptedStateCodec.Deserialize(copy);
                if (!string.Equals(accepted.WorldId, parsedWorld.ToString("D"), StringComparison.OrdinalIgnoreCase)) { reason = "Accepted state belongs to a different world"; return null; }
                capsule = AcceptedStateCodec.CreateCapsule(accepted);
                capsuleNode = CreateNode(capsule);
                if (Encoding.UTF8.GetByteCount(capsuleNode.ToString()) > MaxEncodedCapsuleBytes) { reason = "Encoded recovery capsule exceeds its size bound"; return null; }
            }
            catch (Exception ex) { reason = "Accepted state is invalid: " + Bound(ex.Message, 192); return null; }
            var settlement = acceptedState == null ? null : ColonyRuntime.PrepareRecoveryObservation(acceptedState, accepted);
            return new PreparedAcceptedState { Bytes = copy, State = accepted, Capsule = capsule, Node = capsuleNode, Hash = AcceptedStateCodec.ComputeHash(accepted), World = accepted.WorldId, Settlement = settlement };
        }

        internal void CommitPreparedState(PreparedAcceptedState prepared)
        {
            // The caller must complete all context checks before it starts a synchronous
            // provider commit. This method intentionally does no validation or allocation.
            worldId = prepared.World;
            acceptedStateBytes = prepared.Bytes;
            acceptedState = prepared.State;
            acceptedCapsule = prepared.Capsule;
            acceptedCapsuleNode = prepared.Node;
            acceptedStateHash = prepared.Hash;
            ColonyRuntime.CommitSettlement(prepared.Settlement);
            preservedRawNodes.Clear();
        }

        internal bool TryInitializeNewWorld(string registryWorldId, out string reason)
        {
            reason = null;
            if (!loaded || corrupt || acceptedStateBytes != null) { reason = corrupt ? holdReason : "Recovery state already exists or is unavailable"; return false; }
            Guid id;
            if (!Guid.TryParse(registryWorldId, out id) || id == Guid.Empty) { reason = "Loaded depot registry has no valid world identity"; return false; }
            AcceptedState initial = new AcceptedState { SchemaVersion = 1, WorldId = id.ToString("D"), CheckpointId = Guid.NewGuid().ToString("N") };
            try
            {
                byte[] bytes = AcceptedStateCodec.Serialize(initial);
                return TryReplaceAcceptedState(initial.WorldId, bytes, out reason);
            }
            catch (Exception ex) { reason = "Could not create initial recovery state: " + Bound(ex.Message, 192); return false; }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            if(observerFailureCount>0)
            {
                var loss=node.AddNode("SETTLEMENT_OBSERVER_LOSS");loss.AddValue("count",observerFailureCount);
                loss.AddValue("reason",observerFailureReason);loss.AddValue("ut",observerFailureUt.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            }
            if (preservedRawNodes.Count != 0)
            {
                foreach (ConfigNode raw in preservedRawNodes) node.AddNode(raw.CreateCopy());
                return;
            }
            if (acceptedStateBytes == null || string.IsNullOrEmpty(worldId)) return;
            // This cached node passed the full size and typed validation preflight.
            // Save capture performs no re-encoding or late size decision.
            if (acceptedCapsuleNode == null) throw new InvalidOperationException("Accepted recovery witness is missing; refusing to save without the accepted state.");
            node.AddNode(acceptedCapsuleNode.CreateCopy());
        }

        private void PreserveInvalid(ConfigNode source, string reason)
        {
            acceptedStateBytes = null;
            acceptedState = null;
            acceptedCapsule = null;
            acceptedCapsuleNode = null;
            acceptedStateHash = null;
            worldId = null;
            ConfigNode[] candidates = source.GetNodes("EXPANSE_RECOVERY");
            foreach (ConfigNode candidate in candidates) preservedRawNodes.Add(candidate.CreateCopy());
            corrupt = true;
            holdReason = reason;
        }

        private static ConfigNode CreateNode(RecoveryCapsule capsule)
        {
            ConfigNode node = new ConfigNode("EXPANSE_RECOVERY");
            node.AddValue("schemaVersion", capsule.SchemaVersion);
            node.AddValue("worldId", capsule.WorldId);
            node.AddValue("stateBytesBase64", capsule.StateBytesBase64);
            node.AddValue("stateSha256", capsule.StateSha256);
            return node;
        }

        private static string Bound(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "invalid data";
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= max ? value : value.Substring(0, max);
        }

        public void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
