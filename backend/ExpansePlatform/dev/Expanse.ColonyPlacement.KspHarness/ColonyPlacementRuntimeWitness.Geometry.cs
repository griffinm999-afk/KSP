using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.WorldBridge;
using UnityEngine;

public sealed partial class ColonyPlacementRuntimeWitness
{
    private ConfigNode sweep;
    private float lastSweepFixedTime;
    private int sweepFrames;
    private readonly Dictionary<uint, string> deploymentPhases = new Dictionary<uint, string>();
    private ColonyPlacementContactDiagnostic contactDiagnostic;

    private void ResetSweep() { sweep = null; sweepFrames = 0; lastSweepFixedTime = -1; deploymentPhases.Clear(); }
    private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    private object ActualRecord()
    {
        var records = (System.Collections.IDictionary)typeof(ColonyPlacementScenario).GetField("Records", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ColonyPlacementScenario.Instance);
        return records[operation];
    }
    private void ObserveDeploymentSweep(ColonyPlacementStatus status)
    {
        if (lastSweepFixedTime == Time.fixedTime) return;
        var marked = Marked(operation); if (marked.Length != 1 || !marked[0].loaded || marked[0].parts.Any(p => !p.started)) return;
        var vessel = marked[0]; var request = pendingRequest;
        if (contactDiagnostic != null) contactDiagnostic.Observe(vessel, status);
        Check(request != null, "New assembly sweep lacks exact authorized request");
        lastSweepFixedTime = Time.fixedTime;
        Check(++sweepFrames <= 4096, "Actual deployment sweep exceeds 4096 fixed-time observations");
        var body = vessel.mainBody; var surface = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, status.SurveyTerrainHeight);
        var radial = (surface - body.position).normalized; RaycastHit center;
        Check(Physics.Raycast((Vector3)(surface + radial * 50), (Vector3)(-radial), out center, 100, 1 << 15, QueryTriggerInteraction.Ignore), "Deployment sweep lacks loaded terrain center");
        var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, status.SurveyTerrainHeight) - surface), center.normal).normalized;
        Check(north.sqrMagnitude > .9f, "Deployment sweep north frame unavailable");
        var frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, center.normal) * north, center.normal); var inverse = Quaternion.Inverse(frame);
        if (sweep == null)
        {
            sweep = new ConfigNode("COLONY_NATIVE_DEPLOYMENT_SWEEP_WITNESS");
            sweep.AddValue("operationId", operation); sweep.AddValue("requestFingerprint", request.Fingerprint()); sweep.AddValue("body", body.bodyName); sweep.AddValue("vesselId", vessel.id);
            sweep.AddValue("method", "Read-only native box geometry, conservative complete mesh local bounds, sphere/capsule support intervals transformed into same-altitude loaded surface frame at distinct native fixed times; complete declared terrain grid <=1m; unknown shapes hold");
            int nx = Math.Max(2, (int)Math.Ceiling(request.MaxX - request.MinX)), nz = Math.Max(2, (int)Math.Ceiling(request.MaxZ - request.MinZ));
            Check((nx + 1) * (nz + 1) <= 4096, "Sweep terrain grid exceeds 4096 point bound");
            for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
            {
                double lx = request.MinX + (request.MaxX - request.MinX) * x / nx, lz = request.MinZ + (request.MaxZ - request.MinZ) * z / nz;
                var point = center.point + frame * new Vector3((float)lx, 0, (float)lz); RaycastHit hit;
                Check(Physics.Raycast(point + center.normal * 20, -center.normal, out hit, 40, 1 << 15, QueryTriggerInteraction.Ignore), "Sweep complete terrain footprint ray missing");
                var row = sweep.AddNode("TERRAIN"); row.AddValue("x", R(lx)); row.AddValue("z", R(lz)); row.AddValue("heightAboveCenterPlane", R(Vector3.Dot(hit.point - center.point, center.normal)));
                var localNormal = inverse * hit.normal; row.AddValue("independentNormal", R(localNormal.x) + "," + R(localNormal.y) + "," + R(localNormal.z));
                row.AddValue("independentSlopeDegrees", R(Vector3.Angle(hit.normal, (Vector3)((Vector3d)hit.point - body.position).normalized)));
            }
        }
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity, maxY = double.NegativeInfinity; int count = 0;
        foreach (var collider in vessel.parts.SelectMany(p => p.GetComponentsInChildren<Collider>()).Where(c => c.enabled && !c.isTrigger && c.gameObject.layer != 21).Distinct())
        {
            Check(++count <= 4096, "Deployment sweep collider inventory exceeds bound"); var b = ColonyPlacementColliderShapeWitness.Read(collider,center.point,inverse);
            minX=Math.Min(minX,b.min.x); maxX=Math.Max(maxX,b.max.x); minZ=Math.Min(minZ,b.min.z); maxZ=Math.Max(maxZ,b.max.z); maxY=Math.Max(maxY,b.max.y);
        }
        Check(count > 0, "Actual deployment sweep contains no physical colliders");
        bool within = minX >= request.MinX - .02 && maxX <= request.MaxX + .02 && minZ >= request.MinZ - .02 && maxZ <= request.MaxZ + .02 && maxY <= request.MaximumHeight + .02;
        var sample = sweep.AddNode("FIXED_TIME"); sample.AddValue("fixedTime", R(Time.fixedTime)); sample.AddValue("observedUt", R(Planetarium.GetUniversalTime())); sample.AddValue("stage", status.Stage); sample.AddValue("colliderCount", count);
        sample.AddValue("minX", R(minX)); sample.AddValue("maxX", R(maxX)); sample.AddValue("minZ", R(minZ)); sample.AddValue("maxZ", R(maxZ)); sample.AddValue("maxY", R(maxY)); sample.AddValue("strictEnvelopeWithin02m", within);
        bool deploymentChanged = false;
        foreach (var part in vessel.parts) foreach (PartModule module in part.Modules)
        {
            if (module.GetType().FullName != "PlanetarySurfaceStructures.PlanetaryModule") continue;
            var marker = part.Modules.OfType<ColonyPlacementMarker>().Single(); var field = module.GetType().GetField("moduleStatus");
            Check(field != null, "Actual native deployment phase field unavailable"); string phase = field.GetValue(module).ToString(), previous;
            if (deploymentPhases.TryGetValue(marker.craftPartId, out previous) && previous == phase) continue;
            deploymentPhases[marker.craftPartId] = phase;
            deploymentChanged = true;
            var row = sample.AddNode("NATIVE_DEPLOYMENT"); row.AddValue("craftPartId", marker.craftPartId); row.AddValue("persistentId", part.persistentId); row.AddValue("phase", phase); row.AddValue("actualCrewCapacity", part.CrewCapacity);
        }
        if (deploymentChanged || !within) SaveSweep();
        Check(within, "Actual native deployment sweep exceeds declared package envelope at strict 0.02m allowance");
    }
    private void SaveSweep()
    {
        if (sweep == null) return;
        string path = Path.Combine(root, "colony-placement-sweep-" + operation + ".cfg"); CheckPath(path);
        var content = sweep.ToString(); Check(Encoding.UTF8.GetByteCount(content) <= 8 * 1024 * 1024, "Native sweep witness exceeds 8MiB bound"); File.WriteAllText(path, content);
    }
    private void ExportQualifiedGeometry(object record, ColonyPlacementRequest request, ColonyPlacementStatus status)
    {
        var positioningField = record.GetType().GetField("GroundPositioningEvidence");
        var positioning = positioningField == null ? null : (ConfigNode)positioningField.GetValue(record);
        if (positioning != null)
        {
            Check(positioning.GetValue("restored") == "True" && positioning.GetValue("skipGroundPositioningBefore") == positioning.GetValue("skipGroundPositioningAfter") && positioning.GetValue("activeVesselUntouched") == "True", "Temporary native ground positioning flag was not restored or altered control");
            string positioningHash = Sha(Encoding.UTF8.GetBytes(positioning.ToString()));
            Check(status.AfterWitness.Split(';').Contains("nativeGroundPositioning=" + positioningHash), "Native temporary ground pose evidence differs from anchor seal");
            string positioningPath = Path.Combine(root, "colony-placement-ground-positioning-" + operation + ".cfg"); CheckPath(positioningPath);
            if (coldReload) Check(File.Exists(positioningPath) && new FileInfo(positioningPath).Length <= 4096 && Sha(Encoding.UTF8.GetBytes(File.ReadAllText(positioningPath))) == positioningHash, "Cold native ground pose preservation differs from original evidence");
            else { Check(!File.Exists(positioningPath), "Original native ground pose preservation evidence exists"); File.WriteAllText(positioningPath, positioning.ToString()); }
            Line("PASS actual temporary native ground pose preservation/restoration sealed " + positioningHash);
        }
        string path = Path.Combine(root, "colony-placement-footing-" + operation + ".cfg"); CheckPath(path);
        var field = record.GetType().GetField("GroundContactEvidence"); Check(field != null, "Installed product lacks actual native footing witness field");
        var footing = (ConfigNode)field.GetValue(record);
        if (coldReload)
        {
            Check(File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024, "Cold proof lacks original preserved native footing evidence");
            footing = ConfigNode.Parse(File.ReadAllText(path)).GetNode("COLONY_NATIVE_FOOTING_WITNESS");
        }
        Check(footing != null && footing.GetValue("operationId") == operation && footing.GetValue("requestFingerprint") == request.Fingerprint() && footing.GetValue("vesselId") == status.VesselId,
            "Actual native footing evidence lost immutable placement lineage");
        string hash = Sha(Encoding.UTF8.GetBytes(footing.ToString()));
        Check(status.AfterWitness != null && status.AfterWitness.Split(';').Contains("nativeFooting=" + hash), "Actual native footing evidence differs from saved placement seal");
        if (!coldReload)
        {
            Check(sweepFrames > 0, "Native deployment sweep was not observed"); SaveSweep();
            Check(!File.Exists(path), "Original native footing evidence already exists; no overwrite permitted"); File.WriteAllText(path, footing.ToString());
        }
        Line("PASS actual native footing sealed " + hash + "; deployment fixed-time observations=" + sweepFrames + "; cold=" + coldReload);
        ColonyPlacementSupportTerrainDiagnostic.Write(root, request, status, footing, coldReload);
    }
    private void ExportUnexpectedHold(ColonyPlacementStatus status)
    {
        if (!requireStrictEnvelope || coldReload) return;
        bool strict = requireStrictEnvelope;
        try
        {
            SaveSweep(); var buildings = Marked(operation);
            if (buildings.Length == 1 && buildings[0].loaded)
            { requireStrictEnvelope = false; WritePhysicalEnvelope(buildings[0], pendingRequest, status); WritePackagePhysicalWitness(buildings[0], pendingRequest, status); }
        }
        catch (Exception e) { Line("HELD_DIAGNOSTIC_REJECTED actual damaged/changed assembly: " + e); }
        finally
        {
            requireStrictEnvelope = strict;
            string name = "colony-test-held-" + operation; string path = Path.Combine(root, "saves", expectedFolder, name + ".sfs"); CheckPath(path);
            Check(!File.Exists(path), "Unexpected hold diagnostic save already exists; original remains unchanged");
            GamePersistence.SaveGame(name, expectedFolder, SaveMode.OVERWRITE);
            Check(File.Exists(path), "Actual unexpected held native diagnostic save unavailable");
            Line("UNEXPECTED_NATIVE_HOLD preserved without recovery/respawn; save=" + name + "; sha256=" + Sha(File.ReadAllBytes(path)));
        }
    }
}
