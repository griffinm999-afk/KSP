using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.WorldBridge;
using UnityEngine;

// Explicitly armed read-only diagnostics. No collider, joint, pose, resource,
// crash tolerance or native physics setting is changed.
internal sealed class ColonyPlacementContactDiagnostic : IDisposable
{
    private readonly string operation, path;
    private readonly ConfigNode witness;
    private readonly ColonyPlacementRequest request;
    private readonly HashSet<uint> members = new HashSet<uint>();
    private readonly HashSet<string> poses = new HashSet<string>(StringComparer.Ordinal);
    private int eventCount;
    private bool disposed;
    internal ColonyPlacementContactDiagnostic(string root, ColonyPlacementRequest request)
    {
        this.request = request; operation = request.OperationId; path = Path.Combine(root, "colony-placement-contact-diagnostic-" + operation + ".cfg");
        if (File.Exists(path)) throw new InvalidOperationException("Original contact diagnosis already exists");
        witness = new ConfigNode("COLONY_READ_ONLY_CONTACT_DIAGNOSTIC");
        witness.AddValue("operationId", operation); witness.AddValue("requestFingerprint", request.Fingerprint()); witness.AddValue("craftSha256", request.TemplateSha256);
        witness.AddValue("method", "Native ground-explosion event before destruction, native Crash/Collision EventReport velocity/target and exact onVesselGoOffRails event after native Part.Unpack/ResumeVelocity and before GoOffRails returns; full per-part shape/terrain/joint/rigidbody snapshots. Observation only; missing event is unknown.");
        GameEvents.onPartExplodeGroundCollision.Add(Ground);
        GameEvents.onCrash.Add(Report); GameEvents.onCollision.Add(Report); GameEvents.onVesselGoOffRails.Add(OffRails); Flush();
    }
    internal void Observe(Vessel vessel, ColonyPlacementStatus status, string nativeEventPhase = null)
    {
        foreach (var part in vessel.parts) members.Add(part.persistentId);
        string phase = nativeEventPhase ?? (vessel.packed ? "started-packed-before-unpack" : "started-unpacked-after-unpack");
        if (!poses.Add(phase)) return;
        var row = witness.AddNode("POSE"); row.AddValue("phase", phase); row.AddValue("fixedTime", R(Time.fixedTime)); row.AddValue("ut", R(Planetarium.GetUniversalTime()));
        row.AddValue("vesselId", vessel.id); row.AddValue("landed", vessel.Landed); row.AddValue("packed", vessel.packed); row.AddValue("holdPhysics", vessel.HoldPhysics); row.AddValue("easing", vessel.easingInToSurface); row.AddValue("srfSpeed", R(vessel.srfSpeed));
        foreach (var part in vessel.parts)
        {
            var p = row.AddNode("PART"); p.AddValue("persistentId", part.persistentId); p.AddValue("flightId", part.flightID); p.AddValue("name", part.partInfo.name);
            p.AddValue("worldPosition", part.transform.position.ToString("F9")); p.AddValue("localPosition", part.transform.localPosition.ToString("F9")); p.AddValue("worldRotation", part.transform.rotation.ToString("F9"));
            p.AddValue("mass", R(part.mass)); p.AddValue("crashTolerance", R(part.crashTolerance)); p.AddValue("parentPersistentId", part.parent == null ? 0 : part.parent.persistentId);
            var rootPart = vessel.rootPart; p.AddValue("nativeOrgPos", part.orgPos.ToString("F9")); p.AddValue("nativeOrgRot", part.orgRot.ToString("F9"));
            if (rootPart != null) { p.AddValue("actualRootRelativePosition", rootPart.transform.InverseTransformPoint(part.transform.position).ToString("F9")); p.AddValue("actualRootRelativeRotation", (Quaternion.Inverse(rootPart.transform.rotation) * part.transform.rotation).ToString("F9")); }
            foreach (var marker in part.Modules.OfType<ColonyPlacementMarker>()) p.AddValue("craftPartId", marker.craftPartId);
            foreach (var node in part.attachNodes)
            {
                var attachment = p.AddNode("ACTUAL_ATTACHMENT_NODE"); attachment.AddValue("id", node.id); attachment.AddValue("localPosition", node.position.ToString("F9")); attachment.AddValue("worldPosition", part.transform.TransformPoint(node.position).ToString("F9")); attachment.AddValue("localOrientation", node.orientation.ToString("F9")); attachment.AddValue("attachedPartPersistentId", node.attachedPart == null ? 0 : node.attachedPart.persistentId);
            }
            if (part.rb != null) { p.AddValue("rigidbodyName", part.rb.name); p.AddValue("rigidbodyVelocity", part.rb.velocity.ToString("F9")); p.AddValue("rigidbodyAngularVelocity", part.rb.angularVelocity.ToString("F9")); p.AddValue("rigidbodyMass", R(part.rb.mass)); p.AddValue("isKinematic", part.rb.isKinematic); }
            Shapes(part, p.AddNode("LIVE_COLLIDERS")); Shapes(part.partInfo.partPrefab, p.AddNode("LOADED_PREFAB_COLLIDERS"));
        }
        var colliders = vessel.parts.SelectMany(p => p.GetComponentsInChildren<Collider>()).Where(c => c.enabled && !c.isTrigger && c.gameObject.layer != 21).Distinct().ToArray();
        if (colliders.Length > 128) throw new InvalidOperationException("Contact diagnostic exceeds128 collider bound");
        TerrainClearance(vessel, status, colliders, row.AddNode("ACTUAL_COLLIDER_TERRAIN_CLEARANCE"));
        foreach (var joint in vessel.parts.SelectMany(p => p.GetComponentsInChildren<ConfigurableJoint>()).Distinct())
        {
            var entry = row.AddNode("ACTUAL_NATIVE_JOINT"); entry.AddValue("name", joint.name); entry.AddValue("ownerPersistentId", joint.GetComponentInParent<Part>() == null ? 0 : joint.GetComponentInParent<Part>().persistentId);
            var host = joint.transform.TransformPoint(joint.anchor); entry.AddValue("hostWorldAnchor", host.ToString("F9")); entry.AddValue("localAnchor", joint.anchor.ToString("F9")); entry.AddValue("connectedLocalAnchor", joint.connectedAnchor.ToString("F9")); entry.AddValue("autoConfigureConnectedAnchor", joint.autoConfigureConnectedAnchor);
            if (joint.connectedBody != null) { var target = joint.connectedBody.transform.TransformPoint(joint.connectedAnchor); entry.AddValue("connectedBody", joint.connectedBody.name); entry.AddValue("connectedPartPersistentId", joint.connectedBody.GetComponentInParent<Part>() == null ? 0 : joint.connectedBody.GetComponentInParent<Part>().persistentId); entry.AddValue("connectedWorldAnchor", target.ToString("F9")); entry.AddValue("worldAnchorSeparationMetres", R(Vector3.Distance(host, target))); }
            else entry.AddValue("connectedBody", "absent");
            entry.AddValue("xMotion", joint.xMotion); entry.AddValue("yMotion", joint.yMotion); entry.AddValue("zMotion", joint.zMotion); entry.AddValue("targetRotation", joint.targetRotation.ToString("F9"));
        }
        for (int i = 0; i < colliders.Length; i++) for (int j = i + 1; j < colliders.Length; j++)
        {
            var a = colliders[i]; var b = colliders[j];
            if (a.GetComponentInParent<Part>() == b.GetComponentInParent<Part>() || !a.bounds.Intersects(b.bounds)) continue;
            var pair = row.AddNode("OVERLAPPING_WORLD_AABB_ONLY"); pair.AddValue("first", a.name); pair.AddValue("second", b.name); pair.AddValue("firstOwner", a.GetComponentInParent<Part>().persistentId); pair.AddValue("secondOwner", b.GetComponentInParent<Part>().persistentId);
            pair.AddValue("ignoredByNativePhysics", Physics.GetIgnoreCollision(a, b)); pair.AddValue("warning", "Conservative world AABB overlap is not actual shape penetration");
            pair.AddValue("sharedAttachedRigidbody", a.attachedRigidbody != null && a.attachedRigidbody == b.attachedRigidbody);
            if (Supported(a) || Supported(b))
            {
                Vector3 direction; float distance;
                bool overlap = Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out direction, out distance);
                pair.AddValue("nativeComputePenetrationOverlap", overlap); pair.AddValue("penetrationDistance", R(distance)); pair.AddValue("separationDirection", direction.ToString("F9"));
            }
            else pair.AddValue("nativeComputePenetration", "Unknown: neither shape is box/sphere/capsule/convex mesh");
        }
        Flush();
    }
    private void TerrainClearance(Vessel vessel, ColonyPlacementStatus status, Collider[] colliders, ConfigNode row)
    {
        row.AddValue("method", "Read-only native Collider.Raycast from below loaded terrain along measured center normal on5x5 surface-frame points per actual shape bound. Misses are explicit; finite successful lowest shape hits diagnose signed physical clearance, not certified whole-mesh support. No synchronization or physics changes.");
        try
        {
            var body = vessel.mainBody; Vector3d surface = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, status.SurveyTerrainHeight); Vector3 radial = (Vector3)(surface - body.position).normalized; RaycastHit center;
            if (!Physics.Raycast((Vector3)surface + radial * 50, -radial, out center, 100, 1 << 15, QueryTriggerInteraction.Ignore)) throw new InvalidOperationException("Native diagnostic terrain center missing");
            Vector3 north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, status.SurveyTerrainHeight) - surface), center.normal).normalized;
            if (north.sqrMagnitude < .9f) throw new InvalidOperationException("Native diagnostic north missing");
            Quaternion frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, center.normal) * north, center.normal); Quaternion inverse = Quaternion.Inverse(frame);
            row.AddValue("centerPoint", center.point.ToString("F9")); row.AddValue("centerNormal", center.normal.ToString("F9")); row.AddValue("surfaceFrame", frame.ToString("F9")); row.AddValue("terrainHeight", R(status.SurveyTerrainHeight));
            row.AddValue("centerTerrainCollider", center.collider.name); row.AddValue("centerTerrainTransform", center.collider.transform.name); row.AddValue("centerTerrainOwnerPart", center.collider.GetComponentInParent<Part>() != null);
            foreach (var c in colliders)
            {
                var entry = row.AddNode("ACTUAL_COLLIDER"); var owner = c.GetComponentInParent<Part>(); entry.AddValue("name", c.name); entry.AddValue("ownerPersistentId", owner == null ? 0 : owner.persistentId); entry.AddValue("type", c.GetType().FullName);
                Bounds bounds = ColonyPlacementColliderShapeWitness.Read(c, center.point, inverse); entry.AddValue("shapeMinimum", bounds.min.ToString("F9")); entry.AddValue("shapeMaximum", bounds.max.ToString("F9"));
                int hits = 0, misses = 0; double minimum = double.PositiveInfinity;
                for (int x = 0; x < 5; x++) for (int z = 0; z < 5; z++)
                {
                    float px = bounds.min.x + bounds.size.x * ((x + .001f) / 4.002f), pz = bounds.min.z + bounds.size.z * ((z + .001f) / 4.002f);
                    Vector3 point = center.point + frame * new Vector3(px, 0, pz); RaycastHit ground, shape;
                    if (!Physics.Raycast(point + center.normal * 20, -center.normal, out ground, 40, 1 << 15, QueryTriggerInteraction.Ignore)) { misses++; continue; }
                    if (!c.Raycast(new Ray(ground.point - center.normal * 30, center.normal), out shape, 60)) { misses++; continue; }
                    double clearance = Vector3.Dot(shape.point - ground.point, center.normal); minimum = Math.Min(minimum, clearance); hits++;
                    var sample = entry.AddNode("NATIVE_SURFACE_RAY"); sample.AddValue("x", R(px)); sample.AddValue("z", R(pz)); sample.AddValue("signedPhysicalClearanceMetres", R(clearance)); sample.AddValue("shapePoint", shape.point.ToString("F9")); sample.AddValue("terrainPoint", ground.point.ToString("F9")); sample.AddValue("terrainNormal", ground.normal.ToString("F9")); sample.AddValue("terrainCollider", ground.collider.name); sample.AddValue("terrainOwnerPart", ground.collider.GetComponentInParent<Part>() != null);
                }
                entry.AddValue("successfulActualShapeRays", hits); entry.AddValue("missingActualShapeOrTerrainRays", misses); if (hits != 0) entry.AddValue("minimumObservedSignedPhysicalClearanceMetres", R(minimum));
            }
            row.AddValue("completeObservation", true);
        }
        catch (Exception e) { row.AddValue("completeObservation", false); row.AddValue("reason", e.Message); }
    }
    private static void Shapes(Part part, ConfigNode parent)
    {
        int count = 0;
        foreach (var c in part.GetComponentsInChildren<Collider>(true).Distinct())
        {
            if (++count > 128) throw new InvalidOperationException("Part shape diagnostic exceeds128 collider bound");
            var row = parent.AddNode("COLLIDER"); row.AddValue("name", c.name); row.AddValue("type", c.GetType().FullName); row.AddValue("enabled", c.enabled); row.AddValue("active", c.gameObject.activeInHierarchy); row.AddValue("trigger", c.isTrigger); row.AddValue("layer", c.gameObject.layer);
            row.AddValue("partLocalPosition", part.transform.InverseTransformPoint(c.transform.position).ToString("F9")); row.AddValue("partLocalRotation", (Quaternion.Inverse(part.transform.rotation) * c.transform.rotation).ToString("F9")); row.AddValue("lossyScale", c.transform.lossyScale.ToString("F9"));
            var box = c as BoxCollider; var mesh = c as MeshCollider;
            if (box != null) { row.AddValue("localCenter", box.center.ToString("F9")); row.AddValue("localSize", box.size.ToString("F9")); }
            if (mesh != null && mesh.sharedMesh != null) { row.AddValue("localCenter", mesh.sharedMesh.bounds.center.ToString("F9")); row.AddValue("localSize", mesh.sharedMesh.bounds.size.ToString("F9")); row.AddValue("convex", mesh.convex); }
        }
    }
    private static bool Supported(Collider c)
    { var mesh = c as MeshCollider; return c is BoxCollider || c is SphereCollider || c is CapsuleCollider || mesh != null && mesh.convex; }
    private bool Own(Part part)
    { return part != null && (members.Contains(part.persistentId) || part.Modules.OfType<ColonyPlacementMarker>().Any(m => m.operationId == operation)); }
    private ConfigNode Event(string kind, Part part)
    {
        if (++eventCount > 256) throw new InvalidOperationException("Contact event diagnostic exceeds256 events");
        var row = witness.AddNode("EVENT"); row.AddValue("kind", kind); row.AddValue("ut", R(Planetarium.GetUniversalTime())); row.AddValue("fixedTime", R(Time.fixedTime));
        row.AddValue("persistentId", part.persistentId); row.AddValue("name", part.partInfo == null ? "unknown" : part.partInfo.name); row.AddValue("crashTolerance", R(part.crashTolerance));
        if (part.rb != null) { row.AddValue("rigidbodyVelocity", part.rb.velocity.ToString("F9")); row.AddValue("rigidbodyAngularVelocity", part.rb.angularVelocity.ToString("F9")); }
        return row;
    }
    private void Ground(Part part)
    { try { if (Own(part)) { Event("native-ground-explosion-before-destruction", part); Flush(); } } catch (Exception e) { Debug.LogError("[ColonyPlacement contact diagnostic] " + e); } }
    private void OffRails(Vessel vessel)
    {
        try
        {
            if (vessel == null || !vessel.parts.Any(Own)) return;
            var status = ColonyPlacementScenario.Instance == null ? null : ColonyPlacementScenario.Instance.GetStatus(operation);
            if (status == null) throw new InvalidOperationException("Exact native off-rails event lacks operation receipt");
            Observe(vessel, status, "native-offrails-after-resume-before-gooffrails-return");
        }
        catch (Exception e) { Debug.LogError("[ColonyPlacement contact diagnostic] " + e); }
    }
    private void Report(EventReport report)
    {
        try { if (report == null || !Own(report.origin)) return;
        var row = Event(report.eventType.ToString(), report.origin); row.AddValue("sender", report.sender); row.AddValue("other", report.other); row.AddValue("nativeRelativeCollisionVelocity", R(report.param)); Flush(); }
        catch (Exception e) { Debug.LogError("[ColonyPlacement contact diagnostic] " + e); }
    }
    private static string R(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    private void Flush()
    {
        string content = witness.ToString(); if (System.Text.Encoding.UTF8.GetByteCount(content) > 2 * 1024 * 1024) throw new InvalidOperationException("Contact witness exceeds2MiB bound"); File.WriteAllText(path, content);
    }
    public void Dispose()
    { if (disposed) return; disposed = true; GameEvents.onPartExplodeGroundCollision.Remove(Ground); GameEvents.onCrash.Remove(Report); GameEvents.onCollision.Remove(Report); GameEvents.onVesselGoOffRails.Remove(OffRails); Flush(); }
}
