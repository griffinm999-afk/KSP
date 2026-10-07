using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;
using UnityEngine;

// Actual terrain below the witnessed support polygon, kept separately from the
// immutable contact seal. Diagnostic only; no new qualification authority.
internal static class ColonyPlacementSupportTerrainDiagnostic
{
    internal static void Write(string root, ColonyPlacementRequest request, ColonyPlacementStatus status, ConfigNode footing, bool cold)
    {
        string name = "colony-placement-support-terrain-" + request.OperationId + (cold ? "-cold-" + System.Diagnostics.Process.GetCurrentProcess().Id : "") + ".cfg";
        string path = Path.Combine(root, name); if (File.Exists(path)) throw new InvalidOperationException("Original support terrain observation already exists");
        var row = new ConfigNode("COLONY_NATIVE_SUPPORT_TERRAIN_WITNESS"); row.AddValue("operationId", request.OperationId); row.AddValue("requestFingerprint", request.Fingerprint()); row.AddValue("vesselId", status.VesselId); row.AddValue("body", request.BodyName); row.AddValue("cold", cold); row.AddValue("observedUt", R(Planetarium.GetUniversalTime())); row.AddValue("footingSeal", status.AfterWitness.Split(';').Single(x => x.StartsWith("nativeFooting=", StringComparison.Ordinal)));
        row.AddValue("method", "Actual loaded terrain grid plus independent rays beneath the original sealed native contact positions transformed by the current exact anchored root. Fit only inside this measured contact polygon; a complete reservation residual alone is not under-support qualification. Observation changes no terrain, parts, physics or certificates.");
        row.AddValue("headingDegrees", R(request.HeadingDegrees));
        try
        {
            var vessel = FlightGlobals.Vessels.Single(v => v.id.ToString("D") == status.VesselId); if (!vessel.loaded || vessel.rootPart == null || vessel.mainBody.bodyName != request.BodyName) throw new InvalidOperationException("Exact anchored loaded native root unavailable");
            var body = vessel.mainBody; Vector3d surface = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, status.SurveyTerrainHeight); Vector3 radial = (Vector3)(surface - body.position).normalized; RaycastHit center; string terrainReason;
            if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out center, out terrainReason)) throw new InvalidOperationException(terrainReason);
            var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, status.SurveyTerrainHeight) - surface), center.normal).normalized;
            if (north.sqrMagnitude < .9f) throw new InvalidOperationException("Actual support north missing");
            var frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, center.normal) * north, center.normal); var inverse = Quaternion.Inverse(frame);
            row.AddValue("currentRootWorldPosition", vessel.rootPart.transform.position.ToString("F9")); row.AddValue("currentRootWorldRotation", vessel.rootPart.transform.rotation.ToString("F9")); row.AddValue("centerPoint", center.point.ToString("F9")); row.AddValue("centerNormal", center.normal.ToString("F9")); row.AddValue("surfaceFrame", frame.ToString("F9"));
            var contacts = footing.GetNodes("CONTACT"); if (contacts.Length < 3 || contacts.Length > 4096) throw new InvalidOperationException("Actual sealed support contacts out of bounds");
            foreach (var contact in contacts)
            {
                var values = contact.GetValue("rootLocalPosition").Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); if (values.Length != 3) throw new InvalidOperationException("Native sealed contact position malformed");
                var point = vessel.rootPart.transform.TransformPoint(new Vector3(values[0], values[1], values[2])); var local = inverse * (point - center.point); RaycastHit hit;
                if (!ColonyPlacementParallaxSurface.TerrainRay(body, point + center.normal * 20, -center.normal, 40, out hit, out terrainReason)) throw new InvalidOperationException(terrainReason);
                var p = row.AddNode("SEALED_CONTACT_CURRENT_TERRAIN"); p.AddValue("partPersistentId", contact.GetValue("partPersistentId")); p.AddValue("surfaceX", R(local.x)); p.AddValue("surfaceZ", R(local.z)); p.AddValue("nativeContactHeightAboveCenterPlane", R(local.y)); p.AddValue("terrainHeightAboveCenterPlane", R(Vector3.Dot(hit.point - center.point, center.normal))); p.AddValue("currentTerrainToSealedContactGapMetres", R(Vector3.Dot(point - hit.point, center.normal))); p.AddValue("independentTerrainNormal", (inverse * hit.normal).ToString("F9")); p.AddValue("independentTerrainSlopeDegrees", R(Vector3.Angle(hit.normal, (hit.point - (Vector3)body.position).normalized))); p.AddValue("terrainCollider", hit.collider.name); p.AddValue("terrainOwnerPart", hit.collider.GetComponentInParent<Part>() != null);
            }
            var template = new ColonyTemplate { MinX = request.MinX, MaxX = request.MaxX, MinZ = request.MinZ, MaxZ = request.MaxZ };
            ColonyPlacementTerrainDiagnostic.Add(body, template, request.Latitude, request.Longitude, status.SurveyTerrainHeight, row, request.HeadingDegrees);
            row.AddValue("complete", true);
        }
        catch (Exception e) { row.AddValue("complete", false); row.AddValue("reason", e.Message); }
        var text = row.ToString(); if (System.Text.Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new InvalidOperationException("Support terrain observation exceeds2MiB bound"); File.WriteAllText(path, text);
    }
    private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
}
