using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Expanse.WorldBridge
{
    // Ownership is a short in-memory native operation, never a permanent vessel
    // setting or a replacement for actual collision/gravity/contact qualification.
    internal sealed class ColonyPlacementTemporaryFlag : IDisposable
    {
        private readonly Action<bool> write;
        internal readonly bool Before;
        internal bool Owned { get; private set; }
        internal ColonyPlacementTemporaryFlag(bool exactFreshAssembly, bool activeVessel, Func<bool> read, Action<bool> write)
        {
            if (!exactFreshAssembly || activeVessel || read == null || write == null) throw new InvalidOperationException("Temporary native pose flag requires an exact fresh non-active assembly");
            this.write = write; Before = read(); Owned = true;
            try { write(true); }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (!Owned) return;
            write(Before); Owned = false;
        }
    }

    internal static class ColonyPlacementGroundPositioning
    {
        private sealed class Lease { internal ColonyPlacementRecord Record; internal Vessel Vessel; }
        private static readonly List<Lease> leases = new List<Lease>();

        internal static void Begin(ColonyPlacementRecord record, Vessel vessel)
        {
            bool exact = record != null && vessel != null && record.Status.Stage == ColonyPlacementStage.Created && record.Status.AssemblyAttempted &&
                vessel.id.ToString("D") == record.Status.VesselId && vessel.persistentId == record.Status.VesselPersistentId && vessel.mainBody != null && vessel.mainBody.bodyName == record.Request.BodyName &&
                vessel.parts.Count == record.Status.PartCount && vessel.parts.Select(p => p.persistentId).OrderBy(x => x).SequenceEqual(record.Status.PartPersistentIds.OrderBy(x => x)) &&
                vessel.parts.All(p => p.Modules.OfType<ColonyPlacementMarker>().Count(m => m.operationId == record.Request.OperationId && m.requestFingerprint == record.Status.RequestFingerprint && string.Equals(m.templateSha256, record.Request.TemplateSha256, StringComparison.OrdinalIgnoreCase) && m.worldId == record.Request.WorldId) == 1) &&
                ColonyPlacementRequest.Finite(record.Status.ActualMaximumSlopeDegrees) && record.Status.ActualMaximumSlopeDegrees <= record.Request.MaximumSlopeDegrees &&
                ColonyPlacementRequest.Finite(record.Status.ActualMaximumSupportGapMetres) && record.Status.ActualMaximumSupportGapMetres <= record.Request.MaximumSupportGapMetres;
            if (!exact || record.GroundPositioning != null || leases.Count >= ColonyPlacementRecovery.MaximumOperations) throw new InvalidOperationException("Fresh full-footprint pose preservation ownership is unavailable");
            var evidence = new ConfigNode("COLONY_NATIVE_GROUND_POSITIONING_WITNESS");
            evidence.AddValue("operationId", record.Request.OperationId); evidence.AddValue("requestFingerprint", record.Status.RequestFingerprint); evidence.AddValue("vesselId", vessel.id);
            evidence.AddValue("vesselPersistentId", vessel.persistentId); evidence.AddValue("body", record.Request.BodyName);
            evidence.AddValue("templateSha256", record.Request.TemplateSha256.ToLowerInvariant()); evidence.AddValue("leaseState", "applying");
            evidence.AddValue("maximumFullFootprintSlopeDegrees", record.Status.ActualMaximumSlopeDegrees); evidence.AddValue("fullFootprintResidualMetres", record.Status.ActualMaximumSupportGapMetres);
            evidence.AddValue("activeVesselUntouched", FlightGlobals.ActiveVessel != vessel); evidence.AddValue("packedBefore", vessel.packed); evidence.AddValue("skipGroundPositioningBefore", vessel.skipGroundPositioning);
            record.GroundPositioningEvidence = evidence;
            record.GroundPositioning = new ColonyPlacementTemporaryFlag(exact, FlightGlobals.ActiveVessel == vessel, () => vessel.skipGroundPositioning, value => vessel.skipGroundPositioning = value);
            evidence.AddValue("skipGroundPositioningApplied", vessel.skipGroundPositioning); leases.Add(new Lease { Record = record, Vessel = vessel });
            Seal(record);
            Debug.Log("[ExpanseColonyPlacement] Temporary native ground pose preserved operation=" + record.Request.OperationId + " before=" + record.GroundPositioning.Before + " applied=" + vessel.skipGroundPositioning);
        }

        internal static void Refresh()
        {
            foreach (var lease in leases.ToArray())
            {
                if (lease.Vessel == null || !HighLogic.LoadedSceneIsFlight || HighLogic.CurrentGame == null ||
                    lease.Record.Status.Stage == ColonyPlacementStage.RecoveryHold || !lease.Vessel.packed && !lease.Vessel.HoldPhysics && !lease.Vessel.easingInToSurface)
                    Restore(lease.Record, "Actual first unpack/easing completion or effect cleanup");
            }
        }

        internal static void Restore(ColonyPlacementRecord record, string reason)
        {
            var lease = leases.SingleOrDefault(l => ReferenceEquals(l.Record, record));
            if (lease == null) return;
            // Retain the exact native object captured at assembly, even if damaged;
            // never locate or alter a replacement/current active vessel by name.
            if (lease.Vessel != null) record.GroundPositioning.Dispose();
            var evidence = record.GroundPositioningEvidence;
            evidence.AddValue("restoreReason", reason); evidence.AddValue("nativeObjectStillPresent", lease.Vessel != null);
            evidence.AddValue("skipGroundPositioningAfter", lease.Vessel != null ? lease.Vessel.skipGroundPositioning : record.GroundPositioning.Before);
            evidence.AddValue("restored", lease.Vessel == null || !record.GroundPositioning.Owned);
            evidence.AddValue("packedAfter", lease.Vessel != null && lease.Vessel.packed); evidence.AddValue("easingAfter", lease.Vessel != null && lease.Vessel.easingInToSurface);
            evidence.SetValue("leaseState", "restored", true); Seal(record);
            Debug.Log("[ExpanseColonyPlacement] Temporary native ground pose restored operation=" + record.Request.OperationId + " prior=" + record.GroundPositioning.Before + " reason=" + reason);
            leases.Remove(lease);
        }

        internal static void RestoreAll(string reason)
        { foreach (var lease in leases.ToArray()) Restore(lease.Record, reason); }

        internal static void SaveFence()
        {
            // Game.Updated also serializes scenarios for ordinary observers. No
            // event uniquely identifies a filesystem save before ProtoVessel's
            // cached flag is taken. Only the exact native save entry point fences
            // a live lease; sealed applying snapshots remain safe after cold load.
            if (leases.Count == 0 || !(new StackTrace().GetFrames() ?? new StackFrame[0]).Take(64).Any(f => IsNativeSaveMethod(f.GetMethod()))) return;
            foreach (var lease in leases.ToArray())
            {
                var record = lease.Record;
                Restore(record, "Native save interrupted temporary pose preservation before first unpack");
                record.StableSince = -1; record.Status.Stage = ColonyPlacementStage.RecoveryHold;
                record.Status.Reason = "Native save interrupted first unpack; review exact original physical assembly before recovery";
            }
        }

        internal static bool IsNativeSaveMethod(MethodBase method)
        { return method != null && method.DeclaringType == typeof(GamePersistence) && method.Name == "SaveGame"; }

        internal static void Seal(ColonyPlacementRecord record)
        {
            string seal = ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(record.GroundPositioningEvidence.ToString()));
            var other = (record.Status.AfterWitness ?? "").Split(';').Where(v => !v.StartsWith("nativeGroundPositioning=", StringComparison.Ordinal));
            record.Status.AfterWitness = string.Join(";", other) + ";nativeGroundPositioning=" + seal;
        }

        internal static void ReadEvidence(ColonyPlacementRecord record, ConfigNode saved)
        {
            var evidence = saved.CreateCopy(); evidence.name = "COLONY_NATIVE_GROUND_POSITIONING_WITNESS";
            bool prior, after; uint persistentId;
            string seal = ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(evidence.ToString()));
            if (evidence.ToString().Length > 4096 || evidence.nodes.Count != 0 || evidence.GetValue("operationId") != record.Request.OperationId ||
                evidence.GetValue("requestFingerprint") != record.Status.RequestFingerprint || evidence.GetValue("vesselId") != record.Status.VesselId || evidence.GetValue("body") != record.Request.BodyName ||
                !uint.TryParse(evidence.GetValue("vesselPersistentId"), NumberStyles.None, CultureInfo.InvariantCulture, out persistentId) || persistentId != record.Status.VesselPersistentId ||
                !string.Equals(evidence.GetValue("templateSha256"), record.Request.TemplateSha256, StringComparison.OrdinalIgnoreCase) ||
                evidence.GetValue("skipGroundPositioningApplied") != "True" || evidence.GetValue("activeVesselUntouched") != "True" ||
                !bool.TryParse(evidence.GetValue("skipGroundPositioningBefore"), out prior) || !(record.Status.AfterWitness ?? "").Split(';').Contains("nativeGroundPositioning=" + seal))
                throw new FormatException("Native ground positioning evidence lost its exact native flag/placement seal");
            string phase = evidence.GetValue("leaseState");
            if (phase == "restored")
            {
                if (!bool.TryParse(evidence.GetValue("skipGroundPositioningAfter"), out after) || prior != after || evidence.GetValue("restored") != "True")
                    throw new FormatException("Native ground positioning restoration differs from its prior flag");
            }
            else if (phase == "applying" && record.Status.AssemblyAttempted && (record.Status.Stage == ColonyPlacementStage.Created || record.Status.Stage == ColonyPlacementStage.Settling || record.Status.Stage == ColonyPlacementStage.RecoveryHold))
            {
                record.Status.Stage = ColonyPlacementStage.RecoveryHold;
                record.Status.Reason = "Selected native snapshot interrupted first unpack; review exact original physical assembly before recovery";
            }
            else throw new FormatException("Unknown or inconsistent native ground positioning lease phase");
            record.GroundPositioningEvidence = evidence;
            // OnLoad may run while HighLogic still points at the previous Game.
            // Never write any proto/live vessel here; reconcile after selection.
        }

        internal static void RestoreInterruptedSnapshot(ColonyPlacementRecord record)
        {
            var evidence = record.GroundPositioningEvidence;
            if (evidence == null || record.Status.Stage != ColonyPlacementStage.RecoveryHold ||
                record.GroundPositioningSnapshotRestored || (evidence.GetValue("leaseState") != "applying" && evidence.GetValue("restoreReason") != "Native save interrupted temporary pose preservation before first unpack")) return;
            var game = HighLogic.CurrentGame;
            var scenario = ColonyPlacementScenario.Instance;
            if (game == null || scenario == null || !scenario.Ready || scenario.WorldId != record.Request.WorldId ||
                game.scenarios == null || !game.scenarios.Any(s => ReferenceEquals(s, scenario.snapshot) && ReferenceEquals(s.moduleRef, scenario)) ||
                !scenario.Records.Values.Any(r => ReferenceEquals(r, record)) || game.flightState == null || game.flightState.protoVessels == null || FlightGlobals.Bodies == null) return;
            var protos = game.flightState.protoVessels.Where(p => p.vesselID.ToString("D") == record.Status.VesselId && p.persistentId == record.Status.VesselPersistentId).ToArray();
            if (protos.Length != 1) return;
            var proto = protos[0];
            int bodyIndex = proto.orbitSnapShot == null ? -1 : proto.orbitSnapShot.ReferenceBodyIndex;
            if (bodyIndex < 0 || bodyIndex >= FlightGlobals.Bodies.Count || FlightGlobals.Bodies[bodyIndex].bodyName != record.Request.BodyName ||
                proto.protoPartSnapshots.Count != record.Status.PartCount ||
                !proto.protoPartSnapshots.Select(p => p.persistentId).OrderBy(x => x).SequenceEqual(record.Status.PartPersistentIds.OrderBy(x => x)) ||
                !proto.protoPartSnapshots.Select(p => p.flightID).OrderBy(x => x).SequenceEqual(record.Status.FlightIds.OrderBy(x => x)) ||
                !proto.protoPartSnapshots.All(p => p.modules.Count(m => m.moduleName == "ColonyPlacementMarker" && m.moduleValues.GetValue("operationId") == record.Request.OperationId && m.moduleValues.GetValue("requestFingerprint") == record.Status.RequestFingerprint && m.moduleValues.GetValue("worldId") == record.Request.WorldId && m.moduleValues.GetValue("colonyId") == record.Request.ColonyId && m.moduleValues.GetValue("plotId") == record.Request.PlotId && string.Equals(m.moduleValues.GetValue("templateSha256"), record.Request.TemplateSha256, StringComparison.OrdinalIgnoreCase)) == 1)) return;
            // ProtoVessel captures this native flag before serializing modules.
            // An interrupted save can therefore retain an earlier temporary true
            // even though cleanup restored the exact live object. Only this
            // explicit held provenance may normalize that captured native flag.
            var native = proto.vesselRef;
            if (native != null && (native == FlightGlobals.ActiveVessel || ColonyPlacementRuntime.ValidateBuilding(record, native, false) != null)) return;
            bool prior = bool.Parse(evidence.GetValue("skipGroundPositioningBefore"));
            proto.skipGroundPositioning = prior;
            if (native != null) native.skipGroundPositioning = prior;
            record.GroundPositioningSnapshotRestored = true;
            if (evidence.GetValue("leaseState") == "applying")
            {
                evidence.SetValue("leaseState", "restored", true); evidence.AddValue("restoreReason", "Exact selected native snapshot cleanup before explicit recovery");
                evidence.AddValue("skipGroundPositioningAfter", prior); evidence.AddValue("restored", true); evidence.AddValue("nativeObjectStillPresent", native != null); Seal(record);
            }
            Debug.Log("[ExpanseColonyPlacement] Exact selected native snapshot flag cleanup operation=" + record.Request.OperationId + " prior=" + prior + " loadedObject=" + (native != null));
        }
    }
}
