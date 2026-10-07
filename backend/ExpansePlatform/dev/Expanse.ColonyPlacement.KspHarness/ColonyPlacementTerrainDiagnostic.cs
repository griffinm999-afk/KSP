using System;
using System.Globalization;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;
using UnityEngine;

internal static class ColonyPlacementTerrainDiagnostic
{
    // Separate raw diagnostic evidence, never a replacement for Survey.Clear or
    // final physical contacts. The full installed package bounds stay unchanged.
    internal static void Add(CelestialBody body, ColonyTemplate template, double latitude, double longitude, double height, ConfigNode row, double headingDegrees = 0)
    {
        var node = row.AddNode("RAW_LOADED_TERRAIN_DIAGNOSTIC");
        node.AddValue("headingDegrees", R(headingDegrees));
        node.AddValue("frameRule", "LookRotation(AngleAxis(headingDegrees, current native center normal) * projected loaded north, current native center normal)");
        node.AddValue("method", "Independent exact continuous native PQS mesh rays across unchanged full package bounds at <=1m spacing. Scatter colliders are excluded from ground. Raw heights/normals permit checking smaller actually measured support/shape polygons; no clearance/certification authority.");
        try
        {
            Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, height); Vector3 radial = (Vector3)(surface - body.position).normalized; RaycastHit center; string terrainReason;
            Need(ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out center, out terrainReason), terrainReason);
            Vector3 north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(latitude + .001, longitude, height) - surface), center.normal).normalized;
            Need(north.sqrMagnitude > .9f, "Loaded north unavailable");
            Need(!double.IsNaN(headingDegrees) && !double.IsInfinity(headingDegrees) && headingDegrees >= 0 && headingDegrees <= 360, "Raw diagnostic heading is invalid");
            Quaternion frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)headingDegrees, center.normal) * north, center.normal);
            node.AddValue("centerPoint", center.point.ToString("F9")); node.AddValue("centerNormal", center.normal.ToString("F9")); node.AddValue("surfaceFrame", frame.ToString("F9"));
            int nx = Math.Max(2, (int)Math.Ceiling(template.MaxX - template.MinX)), nz = Math.Max(2, (int)Math.Ceiling(template.MaxZ - template.MinZ));
            Need((nx + 1) * (nz + 1) <= 4096, "Raw terrain diagnostic exceeds4096 rays");
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
            {
                double px = template.MinX + (template.MaxX - template.MinX) * x / nx, pz = template.MinZ + (template.MaxZ - template.MinZ) * z / nz;
                Vector3 point = center.point + frame * new Vector3((float)px, 0, (float)pz); RaycastHit hit;
                Need(ColonyPlacementParallaxSurface.TerrainRay(body, point + center.normal * 20, -center.normal, 40, out hit, out terrainReason), terrainReason);
                double h = Vector3.Dot(hit.point - center.point, center.normal); min = Math.Min(min, h); max = Math.Max(max, h);
                var sample = node.AddNode("TERRAIN"); sample.AddValue("x", R(px)); sample.AddValue("z", R(pz)); sample.AddValue("heightAboveCenterPlane", R(h));
                sample.AddValue("actualColliderName", hit.collider.name); sample.AddValue("actualColliderParent", hit.collider.transform.parent == null ? "absent" : hit.collider.transform.parent.name); sample.AddValue("actualColliderOwnerPart", hit.collider.GetComponentInParent<Part>() != null);
                var normal = Quaternion.Inverse(frame) * hit.normal; sample.AddValue("independentNormal", R(normal.x) + "," + R(normal.y) + "," + R(normal.z));
                sample.AddValue("independentSlopeDegrees", R(Vector3.Angle(hit.normal, (hit.point - (Vector3)body.position).normalized)));
            }
            node.AddValue("sampleCount", (nx + 1) * (nz + 1)); node.AddValue("rawCenterPlaneResidualRangeMetres", R(max - min)); node.AddValue("complete", true);
        }
        catch (Exception e) { node.AddValue("complete", false); node.AddValue("reason", e.Message); }
    }
    private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
}
