using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed class ColonyPlacementMarker : PartModule
    {
        [KSPField(isPersistant = true)] public string operationId = "";
        [KSPField(isPersistant = true)] public string worldId = "";
        [KSPField(isPersistant = true)] public string colonyId = "";
        [KSPField(isPersistant = true)] public string plotId = "";
        [KSPField(isPersistant = true)] public string requestFingerprint = "";
        [KSPField(isPersistant = true)] public string templateSha256 = "";
        [KSPField(isPersistant = true)] public uint craftPartId;
    }

    internal sealed class ColonyPlacementRecord
    {
        public ColonyPlacementRequest Request;
        public ColonyPlacementStatus Status;
        public double StableSince = -1;
        public Quaternion StableRotation;
        public Vector3d StablePosition;
        public bool NeedsReconcile = true;
        // Derived from the exact immutable craft, never serialized as authority.
        public uint[] PlannedPlanetaryDeployments;
        public ConfigNode GroundContactEvidence;
        public ColonyPlacementTemporaryFlag GroundPositioning;
        public ConfigNode GroundPositioningEvidence;
        public bool GroundPositioningSnapshotRestored;
    }

    // Save-scoped authority for physical effects only. Escrow/material/funds remain
    // with the colony ledger, and no host-side truth is replayed after quickload.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public sealed class ColonyPlacementScenario : ScenarioModule
    {
        public static ColonyPlacementScenario Instance { get; private set; }
        internal readonly Dictionary<string, ColonyPlacementRecord> Records = new Dictionary<string, ColonyPlacementRecord>(StringComparer.Ordinal);
        private readonly List<ConfigNode> quarantined = new List<ConfigNode>();
        public bool Ready { get; private set; }
        public string HoldReason { get; private set; }
        public string WorldId { get; private set; }
        private long saveGeneration;

        public override void OnAwake() { base.OnAwake(); Instance = this; }
        public void OnDestroy() { ColonyPlacementGroundPositioning.RestoreAll("Placement scenario destroyed"); if (Instance == this) Instance = null; }

        public override void OnLoad(ConfigNode node)
        {
            ColonyPlacementGroundPositioning.RestoreAll("Selected native save loaded");
            base.OnLoad(node); Ready = false; HoldReason = null; Records.Clear(); quarantined.Clear();
            WorldId = node.GetValue("worldId"); saveGeneration = 0;
            try
            {
                if (node.ToString().Length > ColonyPlacementRecovery.MaximumPayloadCharacters) throw new FormatException("Placement payload exceeds 2 MiB character bound");
                var operations = node.GetNodes("PLACEMENT");
                if (operations.Length > ColonyPlacementRecovery.MaximumOperations) throw new FormatException("Placement queue exceeds 512 operation bound; retain receipts to prevent replay");
                if (node.HasValue("schema") && node.GetValue("schema") != "1") throw new FormatException("Unsupported placement schema");
                if (node.HasValue("saveGeneration")) saveGeneration = long.Parse(node.GetValue("saveGeneration"), CultureInfo.InvariantCulture);
                if (saveGeneration < 0) throw new FormatException("Invalid placement save generation");
                foreach (var op in operations)
                {
                    var request = ColonyPlacementCodec.ReadRequest(op.GetNode("REQUEST"));
                    string error = request.Validate(); if (error != null) throw new FormatException(error);
                    var status = ColonyPlacementCodec.ReadStatus(op.GetNode("WITNESS"));
                    if (status.OperationId != request.OperationId || status.ColonyId != request.ColonyId || status.PlotId != request.PlotId || status.RequestFingerprint != request.Fingerprint() || request.WorldId != WorldId)
                        throw new FormatException("Placement identity/fingerprint does not match its save");
                    if (Records.ContainsKey(request.OperationId)) throw new FormatException("Duplicate placement operation");
                    status.IncludedInSaveSerialization = true;
                    var record = new ColonyPlacementRecord { Request = request, Status = status };
                    var positioning = op.GetNodes("NATIVE_GROUND_POSITIONING");
                    if (positioning.Length > 1) throw new FormatException("Repeated native ground positioning evidence");
                    if (positioning.Length == 1) ColonyPlacementGroundPositioning.ReadEvidence(record, positioning[0]);
                    Records.Add(request.OperationId, record);
                }
                Ready = true;
            }
            catch (Exception e)
            {
                Records.Clear(); quarantined.Add(node.CreateCopy()); HoldReason = e.Message;
                Debug.LogError("[ExpanseColonyPlacement] Save retained for recovery: " + e.Message);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            ColonyPlacementGroundPositioning.SaveFence();
            base.OnSave(node);
            if (!Ready)
            {
                foreach (var raw in quarantined)
                {
                    foreach (ConfigNode.Value value in raw.values) node.AddValue(value.name, value.value);
                    foreach (ConfigNode child in raw.nodes) node.AddNode(child.CreateCopy());
                }
                return;
            }
            if (saveGeneration == long.MaxValue) { HoldReason = "Placement save generation exhausted"; Ready = false; return; }
            saveGeneration++;
            node.AddValue("schema", 1); node.AddValue("worldId", WorldId ?? ""); node.AddValue("saveGeneration", saveGeneration);
            foreach (var record in Records.Values.OrderBy(x => x.Request.OperationId, StringComparer.Ordinal))
            {
                record.Status.SaveGeneration = saveGeneration;
                record.Status.IncludedInSaveSerialization = true;
                var op = node.AddNode("PLACEMENT"); op.AddNode(ColonyPlacementCodec.WriteRequest(record.Request)); op.AddNode(ColonyPlacementCodec.WriteStatus(record.Status));
                if (record.GroundPositioningEvidence != null) { var evidence = record.GroundPositioningEvidence.CreateCopy(); evidence.name = "NATIVE_GROUND_POSITIONING"; op.AddNode(evidence); }
            }
        }

        public bool Enqueue(ColonyPlacementRequest request, out ColonyPlacementStatus status, out string reason)
        {
            status = null; reason = null;
            if (!Ready) { reason = HoldReason ?? "Waiting for the selected save's placement scenario"; return false; }
            if (request == null) { reason = "Placement request missing"; return false; }
            reason = request.Validate(); if (reason != null) return false;
            if (!string.IsNullOrEmpty(WorldId) && request.WorldId != WorldId) { reason = "Placement request belongs to another save/world"; return false; }
            ColonyPlacementRecord existing;
            if (Records.TryGetValue(request.OperationId, out existing))
            {
                status = CopyStatus(existing.Status);
                if (existing.Status.RequestFingerprint != request.Fingerprint()) { reason = "Operation ID was already used with different placement terms"; return false; }
                return true;
            }
            if (Records.Count >= ColonyPlacementRecovery.MaximumOperations) { reason = "Placement receipt capacity reached; no operation IDs may be silently discarded"; return false; }
            if (Records.Values.Any(x => x.Request.ColonyId == request.ColonyId && x.Request.PlotId == request.PlotId && x.Status.Stage != ColonyPlacementStage.Cancelled))
            { reason = "This plot already has a committed or unresolved placement"; return false; }
            var copy = request.Copy();
            var record = new ColonyPlacementRecord { Request = copy, Status = new ColonyPlacementStatus { OperationId = copy.OperationId, ColonyId = copy.ColonyId, PlotId = copy.PlotId, RequestFingerprint = copy.Fingerprint(), Stage = ColonyPlacementStage.AwaitingPlacement, Reason = "Construction complete; awaiting a loaded terrain placement window at 1×" } };
            long projected = ReservedPayloadCharacters(record);
            foreach (var saved in Records.Values) projected += ReservedPayloadCharacters(saved);
            if (projected > ColonyPlacementRecovery.MaximumPayloadCharacters - 1024) { reason = "Placement save payload is full, including reserved space for pending physical witnesses"; return false; }
            WorldId = request.WorldId; Records.Add(copy.OperationId, record); status = CopyStatus(record.Status); return true;
        }

        private static long ReservedPayloadCharacters(ColonyPlacementRecord record)
        {
            int statusLength = ColonyPlacementCodec.WriteStatus(record.Status).ToString().Length;
            bool pending = record.Status.Stage != ColonyPlacementStage.Anchored && record.Status.Stage != ColonyPlacementStage.Cancelled;
            // Room for two bounded witnesses plus all 256 part/flight IDs is reserved
            // before acceptance, so final assembly cannot overflow its save authority.
            return ColonyPlacementCodec.WriteRequest(record.Request).ToString().Length + (pending ? Math.Max(statusLength, 16384) : statusLength) + 128;
        }

        public ColonyPlacementStatus GetStatus(string operationId)
        { ColonyPlacementRecord record; return operationId != null && Records.TryGetValue(operationId, out record) ? CopyStatus(record.Status) : null; }
        public ColonyPlacementStatus[] GetStatuses() { return Records.Values.OrderBy(x => x.Request.OperationId, StringComparer.Ordinal).Select(x => CopyStatus(x.Status)).ToArray(); }

        // Cancellation proves no physical mutation was attempted. Existing or uncertain
        // buildings require an occupant/cargo-aware replacement operation elsewhere.
        public bool CancelUnstarted(string operationId, out string reason)
        {
            reason = null; ColonyPlacementRecord record;
            if (!Ready || operationId == null || !Records.TryGetValue(operationId, out record)) { reason = "Placement operation is unavailable"; return false; }
            if (record.Status.Stage == ColonyPlacementStage.Cancelled) return true;
            if (record.Status.AssemblyAttempted || (record.Status.Stage != ColonyPlacementStage.AwaitingPlacement && record.Status.Stage != ColonyPlacementStage.RecoveryHold)) { reason = "Placement may have created a building; cancellation cannot remove it or authorize refunds"; return false; }
            if (ColonyPlacementRuntime.MarkedVessels(operationId).Length != 0) { reason = "A physical operation marker exists; cancellation requires occupant/cargo-aware recovery"; return false; }
            record.Status.Stage = ColonyPlacementStage.Cancelled; record.Status.Reason = "Cancelled before physical assembly; caller may reconcile its recoverable escrow";
            record.Status.IncludedInSaveSerialization = false; return true;
        }

        public bool ReconcileExistingBuilding(string operationId, out string reason)
        {
            reason = null; ColonyPlacementRecord record;
            if (!Ready || operationId == null || !Records.TryGetValue(operationId, out record) || record.Status.Stage != ColonyPlacementStage.RecoveryHold)
            { reason = "This placement is not in a recoverable hold"; return false; }
            var buildings = ColonyPlacementRuntime.MarkedVessels(operationId);
            if (buildings.Length != 1) { reason = "Reconciliation requires exactly one marked existing building; absence or duplicates cannot authorize respawn/refund"; return false; }
            reason = ColonyPlacementRuntime.ValidateBuilding(record, buildings[0], false); if (reason != null) return false;
            record.Status.Stage = ColonyPlacementStage.Created; record.Status.Reason = "Existing building witnesses match; settlement/anchoring will resume without respawn";
            record.Status.IncludedInSaveSerialization = false; record.NeedsReconcile = true; return true;
        }

        public bool RetryUnattempted(string operationId, out string reason)
        { return RetryUnattempted(operationId, Guid.NewGuid().ToString("D"), out reason); }

        public bool RetryUnattempted(string operationId, string retryOperationId, out string reason)
        {
            reason = null; ColonyPlacementRecord record;
            Guid retry;
            if (!Guid.TryParseExact(retryOperationId, "D", out retry)) { reason = "Retry operation identity is invalid"; return false; }
            if (Ready && operationId != null && Records.TryGetValue(operationId, out record) && record.Status.RetryOperationId == retryOperationId)
                return true; // A repeated acknowledgement cannot re-arm a later failure/assembly.
            if (FlightGlobals.Vessels == null) { reason = "Current vessel inventory is unavailable; absence of a marked building is not proven"; return false; }
            if (!Ready || operationId == null || !Records.TryGetValue(operationId, out record) || record.Status.Stage != ColonyPlacementStage.RecoveryHold || record.Status.AssemblyAttempted)
            { reason = "Only a held operation with no assembly attempt can retry placement"; return false; }
            if (ColonyPlacementRuntime.MarkedVessels(operationId).Length != 0) { reason = "Unexpected operation marker requires reconciliation"; return false; }
            record.Status.Stage = ColonyPlacementStage.AwaitingPlacement; record.Status.Reason = "Validated no prior assembly or marked building; retry queued with original committed terms";
            record.Status.RetryOperationId = retryOperationId;
            record.Status.IncludedInSaveSerialization = false; record.NeedsReconcile = true; return true;
        }

        internal static ColonyPlacementStatus CopyStatus(ColonyPlacementStatus source)
        {
            return new ColonyPlacementStatus { OperationId = source.OperationId, ColonyId = source.ColonyId, PlotId = source.PlotId, RequestFingerprint = source.RequestFingerprint,
                Stage = source.Stage, Reason = source.Reason, VesselId = source.VesselId, FoundationId = source.FoundationId, VesselPersistentId = source.VesselPersistentId,
                PartPersistentIds = (uint[])source.PartPersistentIds.Clone(), FlightIds = (uint[])source.FlightIds.Clone(), PartCount = source.PartCount,
                CreatedUt = source.CreatedUt, AnchoredUt = source.AnchoredUt, ActualMaximumSlopeDegrees = source.ActualMaximumSlopeDegrees,
                ActualMaximumSupportGapMetres = source.ActualMaximumSupportGapMetres, SurveyTerrainHeight = source.SurveyTerrainHeight,
                MeasuredPositionErrorMetres = source.MeasuredPositionErrorMetres, MeasuredAngleErrorDegrees = source.MeasuredAngleErrorDegrees,
                SurfaceCollisionWitness = source.SurfaceCollisionWitness, BeforeWitness = source.BeforeWitness, AfterWitness = source.AfterWitness, AssemblyAttempted = source.AssemblyAttempted, RetryOperationId = source.RetryOperationId, SaveGeneration = source.SaveGeneration, IncludedInSaveSerialization = source.IncludedInSaveSerialization };
        }
    }

    internal static class ColonyPlacementCodec
    {
        internal static ConfigNode WriteRequest(ColonyPlacementRequest request)
        {
            var node = new ConfigNode("REQUEST"); WriteFields(node, request);
            foreach (var content in request.Contents) { var child = node.AddNode("CONTENT"); WriteFields(child, content); }
            return node;
        }
        internal static ColonyPlacementRequest ReadRequest(ConfigNode node)
        {
            if (node == null) throw new FormatException("Placement request node missing");
            var result = new ColonyPlacementRequest(); ReadFields(node, result);
            var children = node.GetNodes("CONTENT"); if (children.Length > 512) throw new FormatException("Content row bound exceeded");
            result.Contents = children.Select(x => { var content = new ColonyPlacementContent(); ReadFields(x, content); return content; }).ToArray(); return result;
        }
        internal static ConfigNode WriteStatus(ColonyPlacementStatus status) { var node = new ConfigNode("WITNESS"); WriteFields(node, status); return node; }
        internal static ColonyPlacementStatus ReadStatus(ConfigNode node)
        {
            if (node == null) throw new FormatException("Placement witness node missing");
            var result = new ColonyPlacementStatus(); ReadFields(node, result);
            Guid retry;
            if (result.RetryOperationId.Length > 0 && !Guid.TryParseExact(result.RetryOperationId, "D", out retry)) throw new FormatException("Placement retry identity is invalid");
            if (!string.IsNullOrEmpty(result.SurfaceCollisionWitness) && (result.SurfaceCollisionWitness.Length != 64 || result.SurfaceCollisionWitness.Any(c => !Uri.IsHexDigit(c)))) throw new FormatException("Native surface collision witness hash is invalid");
            if (!Enum.IsDefined(typeof(ColonyPlacementStage), result.Stage) || result.PartCount < 0 || result.PartCount > ColonyPlacementRecovery.MaximumParts ||
                result.PartPersistentIds.Length > ColonyPlacementRecovery.MaximumParts || result.FlightIds.Length > ColonyPlacementRecovery.MaximumParts ||
                result.PartPersistentIds.Any(x => x == 0) || result.PartPersistentIds.Distinct().Count() != result.PartPersistentIds.Length ||
                result.FlightIds.Any(x => x == 0) || result.FlightIds.Distinct().Count() != result.FlightIds.Length)
                throw new FormatException("Placement witness identities outside supported bounds");
            return result;
        }
        private static void WriteFields(ConfigNode node, object value)
        {
            foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object item = field.GetValue(value); if (field.FieldType == typeof(ColonyPlacementContent[])) continue;
                if (field.FieldType == typeof(uint[])) node.AddValue(field.Name, string.Join(",", ((uint[])item).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
                else if (field.FieldType == typeof(double)) node.AddValue(field.Name, ((double)item).ToString("R", CultureInfo.InvariantCulture));
                else node.AddValue(field.Name, item == null ? "" : Convert.ToString(item, CultureInfo.InvariantCulture));
            }
        }
        private static void ReadFields(ConfigNode node, object target)
        {
            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(ColonyPlacementContent[])) continue;
                var values = node.GetValues(field.Name);
                // Previous physical receipts predate the native scatter guard.
                // Preserve their identity with an explicit absent witness; never
                // fabricate a new collision-provider certificate on cold load.
                if (values.Length == 0 && target is ColonyPlacementStatus && (field.Name == "SurfaceCollisionWitness" || field.Name == "RetryOperationId")) continue;
                if (values.Length != 1) throw new FormatException("Missing or repeated placement field " + field.Name);
                string value = values[0]; if (value.Length > 16384) throw new FormatException("Placement field exceeds bound");
                object parsed;
                if (field.FieldType == typeof(uint[])) { var ids = value.Length == 0 ? new string[0] : value.Split(','); if (ids.Length > ColonyPlacementRecovery.MaximumParts) throw new FormatException("Identity array bound exceeded"); parsed = ids.Select(x => uint.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
                else if (field.FieldType == typeof(string)) parsed = value;
                else if (field.FieldType.IsEnum) parsed = Enum.Parse(field.FieldType, value, false);
                else parsed = Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture);
                if (parsed is double && !ColonyPlacementRequest.Finite((double)parsed)) throw new FormatException("Nonfinite placement witness");
                field.SetValue(target, parsed);
            }
        }
    }
}
