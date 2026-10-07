using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.WorldBridge;

internal static class ColonyPlacementGroundPositioningTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (bool prior in new[] { false, true })
        {
            bool native = prior; int writes = 0;
            var lease = new ColonyPlacementTemporaryFlag(true, false, () => native, v => { native = v; writes++; });
            check(native && lease.Owned && lease.Before == prior, "Exact fresh native lease did not preserve original flag");
            try { throw new InvalidOperationException("Actual boundary interruption analogue"); }
            catch (InvalidOperationException) { lease.Dispose(); }
            check(native == prior && !lease.Owned && writes == 2, "Interrupted external operation left a temporary native flag owned");
            lease.Dispose();
            check(writes == 2 && native == prior, "Repeated cleanup rewrote native state");
        }
        foreach (var gates in new[] { new[] { false, false }, new[] { true, true }, new[] { false, true } })
        {
            bool native = false; int writes = 0; bool rejected = false;
            try { new ColonyPlacementTemporaryFlag(gates[0], gates[1], () => native, v => { native = v; writes++; }); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && !native && writes == 0, "Unreviewed or active native vessel flag was changed");
        }
        bool flag = false; int calls = 0; bool failed = false;
        try { new ColonyPlacementTemporaryFlag(true, false, () => flag, v => { calls++; flag = v; if (v) throw new InvalidOperationException("Native setter interruption"); }); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && !flag && calls == 2, "Partially failing native flag application did not restore prior value");
        var nativeSaves = typeof(GamePersistence).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "SaveGame").ToArray();
        check(nativeSaves.Length > 0 && nativeSaves.All(ColonyPlacementGroundPositioning.IsNativeSaveMethod), "Installed native save entry points were not recognized exactly");
        check(!ColonyPlacementGroundPositioning.IsNativeSaveMethod(typeof(ColonyPlacementScenario).GetMethod("OnSave")) &&
              !ColonyPlacementGroundPositioning.IsNativeSaveMethod(typeof(ColonyPlacementGroundPositioningTests).GetMethod("SaveGame", BindingFlags.NonPublic | BindingFlags.Static)) &&
              !ColonyPlacementGroundPositioning.IsNativeSaveMethod(null), "Observer or unrelated similarly named method could fence native flags");
        CheckSnapshotProvenance(check);
    }

    private static void SaveGame() { }
    private static ColonyPlacementRecord Record()
    {
        return new ColonyPlacementRecord
        {
            Request = new ColonyPlacementRequest { OperationId = "bd906d01-0447-4205-ad6a-f6ab2388037a", WorldId = "66b680a9-aef5-41d2-8c7a-835e5baf53b0", BodyName = "Duna", TemplateSha256 = new string('a', 64) },
            Status = new ColonyPlacementStatus { Stage = ColonyPlacementStage.Created, AssemblyAttempted = true, RequestFingerprint = new string('b', 64), VesselId = "7587d3ae-3254-4018-9be6-9aef09f68f47", VesselPersistentId = 123 }
        };
    }
    private static ConfigNode Evidence(ColonyPlacementRecord record, bool restored)
    {
        var node = new ConfigNode("COLONY_NATIVE_GROUND_POSITIONING_WITNESS");
        node.AddValue("operationId", record.Request.OperationId); node.AddValue("requestFingerprint", record.Status.RequestFingerprint);
        node.AddValue("vesselId", record.Status.VesselId); node.AddValue("vesselPersistentId", 123); node.AddValue("body", "Duna");
        node.AddValue("templateSha256", record.Request.TemplateSha256.ToUpperInvariant()); node.AddValue("leaseState", restored ? "restored" : "applying");
        node.AddValue("skipGroundPositioningBefore", false); node.AddValue("skipGroundPositioningApplied", true); node.AddValue("activeVesselUntouched", true);
        if (restored) { node.AddValue("skipGroundPositioningAfter", false); node.AddValue("restored", true); }
        return node;
    }
    private static void Bind(ColonyPlacementRecord record, ConfigNode evidence)
    { record.Status.AfterWitness = "nativeGroundPositioning=" + ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(evidence.ToString())); }
    private static void CheckSnapshotProvenance(Action<bool, string> check)
    {
        foreach (bool restored in new[] { false, true })
        {
            var record = Record(); var node = Evidence(record, restored); Bind(record, node);
            ColonyPlacementGroundPositioning.ReadEvidence(record, node);
            check(record.GroundPositioningEvidence != null && !record.GroundPositioningSnapshotRestored && (restored || record.Status.Stage == ColonyPlacementStage.RecoveryHold),
                "Snapshot loading mutated native state or failed to hold an interrupted applying lease");
        }
        foreach (var field in new[] { "operationId", "requestFingerprint", "vesselId", "vesselPersistentId", "body", "templateSha256", "skipGroundPositioningApplied", "activeVesselUntouched", "skipGroundPositioningAfter" })
        {
            var record = Record(); var node = Evidence(record, true); node.SetValue(field, field == "vesselPersistentId" ? "124" : field == "skipGroundPositioningAfter" ? "True" : "wrong", true); Bind(record, node);
            bool rejected = false;
            try { ColonyPlacementGroundPositioning.ReadEvidence(record, node); } catch (FormatException) { rejected = true; }
            check(rejected && record.GroundPositioningEvidence == null, "Hash-bound mismatching native snapshot provenance accepted: " + field);
        }
        var altered = Record(); var unsealed = Evidence(altered, true); Bind(altered, unsealed); unsealed.SetValue("skipGroundPositioningBefore", true, true);
        bool sealRejected = false; try { ColonyPlacementGroundPositioning.ReadEvidence(altered, unsealed); } catch (FormatException) { sealRejected = true; }
        check(sealRejected, "Altered native snapshot escaped its original placement seal");
    }
}
