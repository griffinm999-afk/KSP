using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;
using UnityEngine;

// Derived dev-only observer. Uses the actual frozen product survey/check APIs;
// creates no objects, edits no stock, and publishes no qualification flag.
internal static class ColonyPlacementSiteGridDiagnostic
{
    internal static void Write(CelestialBody body, double latitude, double longitude, string id, ConfigNode evidence)
    {
        Need(FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody == body && ColonyRuntime.Current != null, "Actual selected body/context required");
        var catalog = ColonyTemplateCatalog.LoadInstalled(); Need(catalog.Issues.Count == 0, "Exact installed catalog required");
        var template = catalog.Templates.Single(t => t.Id == id);
        evidence.AddValue("method", "49 read-only exact single-template product surveys, 20m grid; actual continuous PQS heights and completed latent collider envelopes; preview only. No assembly/resources/physics/certificate changes.");
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++)
        {
            double lat = latitude + z * 20 / body.Radius * 180 / Math.PI;
            double lon = (longitude + x * 20 / (body.Radius * Math.Cos(latitude * Math.PI / 180)) * 180 / Math.PI + 540) % 360 - 180;
            var result = ColonySiteSurvey.Survey(template, body.bodyName, lat, lon, 0, new ColonyPlot[0], ColonyRuntime.Current.ContextKey);
            var row = evidence.AddNode("ACTUAL_LOADED_SURVEY"); row.AddValue("templateId", id); row.AddValue("templateHash", template.Hash); row.AddValue("craftSha256", template.CraftSha256);
            row.AddValue("latitude", R(lat)); row.AddValue("longitude", R(lon)); row.AddValue("clear", result.Clear); row.AddValue("reason", result.Reason);
            row.AddValue("sampleCount", result.SampleCount); row.AddValue("actualMaximumSlopeDegrees", R(result.MaximumSlopeDegrees)); row.AddValue("actualSupportGapMetres", R(result.MaximumSupportGapMetres)); row.AddValue("terrainWitness", result.TerrainWitness); row.AddValue("surfaceCollisionWitness", result.SurfaceCollisionWitness); row.AddValue("contextKey", result.ContextKey);
            if (result.Plot != null) row.AddValue("trustedSurveyHash", result.Plot.SurveyHash);
            ColonyPlacementTerrainDiagnostic.Add(body, template, lat, lon, result.TerrainHeight, row);
            AddCollisionDetails(body, template, lat, lon, result.TerrainHeight, row);
        }
        Need(System.Text.Encoding.UTF8.GetByteCount(evidence.ToString()) <= 12 * 1024 * 1024, "49-site diagnostic exceeds12MiB bound");
    }

    private static void AddCollisionDetails(CelestialBody body, ColonyTemplate template, double lat, double lon, double height, ConfigNode row)
    {
        var node = row.AddNode("NATIVE_LATENT_COLLIDER_DIAGNOSTIC");
        try
        {
            var check = ColonyPlacementParallaxSurface.Check(body, lat, lon, height, 0, template.MinX, template.MaxX, template.MinZ, template.MaxZ, template.MaximumHeight, template.ClearanceMetres);
            node.AddValue("productReady", check.Ready); node.AddValue("productClear", check.Clear); node.AddValue("productReason", check.Reason); node.AddValue("productWitness", check.Witness); node.AddValue("productQuadCount", check.QuadCount); node.AddValue("productInstanceCount", check.InstanceCount);
            if (!check.Ready) { node.AddValue("complete", false); return; }
            var surface = body.GetWorldSurfacePosition(lat, lon, height); var radial = (Vector3)(surface - body.position).normalized; RaycastHit center; string reason;
            Need(ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out center, out reason), reason);
            var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(lat + .001, lon, height) - surface), center.normal).normalized;
            var frame = Quaternion.LookRotation(north, center.normal); var inverse = Quaternion.Inverse(frame);
            int nx = Math.Max(2, (int)Math.Ceiling(template.MaxX - template.MinX)), nz = Math.Max(2, (int)Math.Ceiling(template.MaxZ - template.MinZ)); double low = double.PositiveInfinity, high = double.NegativeInfinity;
            Need((nx + 1) * (nz + 1) <= 4096, "Diagnostic terrain points exceed4096");
            for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
            {
                var p = center.point + frame * new Vector3((float)(template.MinX + (template.MaxX - template.MinX) * x / nx), 0, (float)(template.MinZ + (template.MaxZ - template.MinZ) * z / nz)); RaycastHit hit;
                Need(ColonyPlacementParallaxSurface.TerrainRay(body, p + center.normal * 20, -center.normal, 40, out hit, out reason), reason);
                double y = Vector3.Dot(hit.point - center.point, center.normal); low = Math.Min(low, y); high = Math.Max(high, y);
            }
            var envelope = new Bounds(new Vector3((float)((template.MinX + template.MaxX) / 2), (float)((low + high + template.MaximumHeight) / 2), (float)((template.MinZ + template.MaxZ) / 2)), new Vector3((float)(template.MaxX - template.MinX + 2 * template.ClearanceMetres), (float)(high - low + template.MaximumHeight + .1), (float)(template.MaxZ - template.MinZ + 2 * template.ClearanceMetres)));
            node.AddValue("envelopeCenterWorld", V(center.point + frame * envelope.center)); node.AddValue("envelopeMinInSurfaceFrame", V(envelope.min)); node.AddValue("envelopeMaxInSurfaceFrame", V(envelope.max)); node.AddValue("surfaceOrigin", V(center.point)); node.AddValue("surfaceRotation", frame.ToString("F9"));
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetType("Parallax.CollisionManager", false) != null);
            var manager = assembly.GetType("Parallax.CollisionManager"); var scatters = (Array)Static(manager, "collideableScatters");
            var quads = (IDictionary)Static(assembly.GetType("Parallax.ScatterComponent"), "scatterQuadData"); double largest = 0;
            foreach (var scatter in scatters)
            {
                var mesh = (Mesh)Read(Read(scatter, "renderer"), "meshLOD1"); var max = (Vector3)Read(Read(scatter, "distributionParams"), "maxScale");
                foreach (var corner in Corners(mesh.bounds)) largest = Math.Max(largest, Vector3.Scale(corner, max).magnitude);
            }
            var relevant = new HashSet<PQ>(); var product = typeof(ColonyPlacementParallaxSurface);
            product.GetMethod("AddRequiredNativeLeaves", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { body, center.point + frame * envelope.center, envelope.extents.magnitude + largest, quads, relevant });
            var transform = product.GetMethod("TransformMeshBounds", BindingFlags.Static | BindingFlags.NonPublic); Need(transform != null && relevant.Count <= 4096, "Reviewed geometry helper or quad bound missing");
            int visited = 0, intersecting = 0;
            foreach (var quad in relevant) foreach (var data in (IList)Read(quads[quad], "quadScatters"))
            {
                var scatter = Read(data, "scatter"); if (!(bool)Read(scatter, "collideable")) continue;
                var collision = Read(data, "collisionData"); Need(collision != null, "Native diagnostic positions pending"); var positions = Read(collision, "quadLocalData"); Need((bool)Property(positions, "IsCreated"), "Native diagnostic positions disposed");
                int count = (int)Property(positions, "Length"); Need(count > 0 && count <= 100000 - visited, "Diagnostic collision inventory exceeds100000"); var item = positions.GetType().GetProperty("Item");
                var mesh = (Mesh)Read(Read(scatter, "renderer"), "meshLOD1"); var distribution = Read(scatter, "distributionParams");
                for (int i = 0; i < count; i++)
                {
                    visited++; var position = item.GetValue(positions, new object[] { i }); var world = quad.transform.TransformPoint((Vector3)Read(position, "localPos")); var scale = (Vector3)Read(position, "localScale");
                    Vector3 normal = (int)Read(distribution, "alignToTerrainNormal") == 1 ? (Vector3)quads[quad].GetType().GetMethod("GetTerrainNormal").Invoke(quads[quad], new[] { Read(position, "index") }) : (quad.transform.position - quad.sphereRoot.transform.position).normalized;
                    var rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis((float)Read(position, "rotation"), Vector3.up);
                    var bounds = (Bounds)transform.Invoke(null, new object[] { mesh.bounds, world, rotation, scale, center.point, inverse }); if (!bounds.Intersects(envelope)) continue;
                    Need(++intersecting <= 256, "Intersecting collider diagnostic exceeds256; no truncated clear claim"); var rock = node.AddNode("INTERSECTING_NATIVE_COLLIDER");
                    rock.AddValue("species", (string)Read(scatter, "scatterName")); rock.AddValue("quad", quad.name); rock.AddValue("nativePositionIndex", i); rock.AddValue("triangleIndex", Read(position, "index")); rock.AddValue("worldRoot", V(world)); rock.AddValue("nativeScale", V(scale)); rock.AddValue("nativeWorldRotation", rotation.ToString("F9")); rock.AddValue("meshLOD1LocalMin", V(mesh.bounds.min)); rock.AddValue("meshLOD1LocalMax", V(mesh.bounds.max)); rock.AddValue("minInSurfaceFrame", V(bounds.min)); rock.AddValue("maxInSurfaceFrame", V(bounds.max));
                    var wb = (Bounds)transform.Invoke(null, new object[] { mesh.bounds, world, rotation, scale, Vector3.zero, Quaternion.identity }); rock.AddValue("worldAabbMin", V(wb.min)); rock.AddValue("worldAabbMax", V(wb.max));
                }
            }
            node.AddValue("observedPositions", visited); node.AddValue("intersectingColliderCount", intersecting); node.AddValue("largestConfiguredColliderRadiusMetres", R(largest)); node.AddValue("complete", true);
        }
        catch (Exception ex) { node.AddValue("complete", false); node.AddValue("reason", ex.GetBaseException().Message); }
    }
    private static IEnumerable<Vector3> Corners(Bounds bounds) { for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)); }
    private static object Read(object value, string field) => value.GetType().GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
    private static object Static(Type type, string field) => type.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    private static object Property(object value, string property) => value.GetType().GetProperty(property).GetValue(value, null);
    private static string V(Vector3 value) => R(value.x) + "," + R(value.y) + "," + R(value.z);
    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
