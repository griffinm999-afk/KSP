using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;
using UnityEngine;

// Read-only candidate selection from actual continuous PQ mesh triangle edges.
// No game objects, collider state, terrain, native events, saves or certificates.
internal static class ColonyPlacementTerrainEdgesDiagnostic
{
    private sealed class Edge
    {
        internal PQ Quad; internal Mesh Mesh; internal int A, B, TriangleA, TriangleB = -1;
        internal Vector3 PointA, PointB, NormalA, NormalB;
        internal float Distance, Angle, SlopeA, SlopeB;
    }

    internal static void Write(CelestialBody body, double latitude, double longitude, string id, ConfigNode evidence, double headingDegrees = 0)
    {
        Need(ColonyPlacementRequest.Finite(headingDegrees) && headingDegrees >= 0 && headingDegrees < 360, "Finite native candidate heading0..360 required");
        Need(FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody == body && ColonyRuntime.Current != null, "Actual selected body/context required");
        var catalog = ColonyTemplateCatalog.LoadInstalled(); Need(catalog.Issues.Count == 0, "Exact installed catalog required");
        var template = catalog.Templates.Single(t => t.Id == id);
        var quads = new HashSet<PQ>(); var center = body.GetWorldSurfacePosition(latitude, longitude, Height(body, latitude, longitude));
        Need((center - FlightGlobals.ActiveVessel.GetWorldPos3D()).magnitude <= 1000, "Loaded edge scout must stay within1000m of actual selected reference");
        for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
        {
            double lat = latitude + z * 150 / body.Radius * 180 / Math.PI;
            double lon = longitude + x * 150 / (body.Radius * Math.Cos(latitude * Math.PI / 180)) * 180 / Math.PI;
            var surface = body.GetWorldSurfacePosition(lat, lon, Height(body, lat, lon)); var radial = (Vector3)(surface - body.position).normalized; RaycastHit hit; string reason;
            if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out hit, out reason))
            { evidence.AddNode("INCOMPLETE_NATIVE_QUAD_SEED").AddValue("reason", reason); continue; }
            var quad = hit.collider.GetComponentInParent<PQ>();
            Need(quad != null && quad.sphereRoot == body.pqsController && hit.collider == quad.meshCollider, "Exact native continuous PQ collider required");
            quads.Add(quad);
        }
        Need(quads.Count > 0 && quads.Count <= 9, "Bounded actual current PQ mesh inventory unavailable");
        var candidates = new List<Edge>(); int trianglesObserved = 0;
        foreach (var quad in quads)
        {
            Need(quad.isActive && quad.isBuilt && quad.isVisible && quad.subdivision == quad.sphereRoot.maxLevel && quad.meshCollider != null && quad.meshCollider.enabled, "Native candidate PQ mesh is not actually built/current maximum detail");
            var mesh = quad.meshCollider.sharedMesh; Need(mesh != null && mesh.isReadable, "Native candidate collision mesh is unreadable");
            var vertices = mesh.vertices; var triangles = mesh.triangles;
            Need(vertices.Length > 0 && vertices.Length <= 4096 && triangles.Length % 3 == 0 && triangles.Length <= 12288 && trianglesObserved + triangles.Length / 3 <= 36864, "Native mesh candidate inventory exceeds reviewed bounds");
            var edges = new Dictionary<long, Edge>(); trianglesObserved += triangles.Length / 3;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Need(a >= 0 && a < vertices.Length && b >= 0 && b < vertices.Length && c >= 0 && c < vertices.Length, "Native triangle indices invalid");
                var pa = quad.meshCollider.transform.TransformPoint(vertices[a]); var pb = quad.meshCollider.transform.TransformPoint(vertices[b]); var pc = quad.meshCollider.transform.TransformPoint(vertices[c]);
                Need(Finite(pa) && Finite(pb) && Finite(pc), "Native transformed triangle is nonfinite");
                var normal = Vector3.Cross(pb - pa, pc - pa); Need(normal.sqrMagnitude > .000001, "Native triangle is degenerate"); normal.Normalize();
                if (Vector3.Dot(normal, pa - (Vector3)body.position) < 0) normal = -normal;
                Add(edges, quad, mesh, a, b, pa, pb, normal, i / 3); Add(edges, quad, mesh, b, c, pb, pc, normal, i / 3); Add(edges, quad, mesh, c, a, pc, pa, normal, i / 3);
            }
            foreach (var edge in edges.Values.Where(e => e.TriangleB >= 0))
            {
                var middle = (edge.PointA + edge.PointB) * .5f; edge.Distance = Vector3.Distance(middle, (Vector3)center);
                var radial = (middle - (Vector3)body.position).normalized; edge.Angle = Vector3.Angle(edge.NormalA, edge.NormalB);
                edge.SlopeA = Vector3.Angle(edge.NormalA, radial); edge.SlopeB = Vector3.Angle(edge.NormalB, radial);
                // These are candidate filters, never support/clearance authority.
                if (edge.Distance <= 200 && edge.Angle >= .15f && edge.SlopeA <= template.MaximumSlopeDegrees && edge.SlopeB <= template.MaximumSlopeDegrees) candidates.Add(edge);
            }
        }
        evidence.AddValue("method", "Read-only native PQ MeshCollider sharedMesh interior-edge candidates with differing adjacent normals. At most9 seed quads/36864 triangles/48 unchanged product surveys. Candidate enumeration is not a complete site search or physical certificate.");
        evidence.AddValue("nativeQuadCount", quads.Count); evidence.AddValue("nativeTrianglesObserved", trianglesObserved); evidence.AddValue("candidateInteriorEdgeCount", candidates.Count);
        evidence.AddValue("headingDegrees", R(headingDegrees));
        foreach (var edge in candidates.OrderBy(e => e.Distance).ThenByDescending(e => e.Angle).Take(48))
        {
            Need(edge.Quad != null && edge.Quad.meshCollider != null && edge.Quad.meshCollider.sharedMesh == edge.Mesh && edge.Quad.isBuilt && edge.Quad.isVisible, "Native source mesh changed during read-only candidate inspection");
            var middle = (edge.PointA + edge.PointB) * .5f; double lat = body.GetLatitude((Vector3d)middle), lon = body.GetLongitude((Vector3d)middle);
            var result = ColonySiteSurvey.Survey(template, body.bodyName, lat, lon, headingDegrees, new ColonyPlot[0], ColonyRuntime.Current.ContextKey);
            var row = evidence.AddNode("ACTUAL_NATIVE_EDGE_SURVEY"); row.AddValue("quad", edge.Quad.name); row.AddValue("meshInstanceId", edge.Mesh.GetInstanceID()); row.AddValue("vertexA", edge.A); row.AddValue("vertexB", edge.B); row.AddValue("triangleA", edge.TriangleA); row.AddValue("triangleB", edge.TriangleB);
            row.AddValue("edgePointAWorld", V(edge.PointA)); row.AddValue("edgePointBWorld", V(edge.PointB)); row.AddValue("adjacentNormalAWorld", V(edge.NormalA)); row.AddValue("adjacentNormalBWorld", V(edge.NormalB)); row.AddValue("adjacentNormalAngleDegrees", R(edge.Angle)); row.AddValue("adjacentSlopeADegrees", R(edge.SlopeA)); row.AddValue("adjacentSlopeBDegrees", R(edge.SlopeB));
            row.AddValue("latitude", R(lat)); row.AddValue("longitude", R(lon)); row.AddValue("actualNativeBiome", ScienceUtil.GetExperimentBiome(body, lat, lon)); row.AddValue("templateId", id); row.AddValue("templateHash", template.Hash); row.AddValue("craftSha256", template.CraftSha256); row.AddValue("clear", result.Clear); row.AddValue("reason", result.Reason); row.AddValue("sampleCount", result.SampleCount); row.AddValue("actualMaximumSlopeDegrees", R(result.MaximumSlopeDegrees)); row.AddValue("actualSupportGapMetres", R(result.MaximumSupportGapMetres)); row.AddValue("terrainWitness", result.TerrainWitness); row.AddValue("surfaceCollisionWitness", result.SurfaceCollisionWitness); row.AddValue("contextKey", result.ContextKey);
            if (result.Plot != null) row.AddValue("trustedSurveyHash", result.Plot.SurveyHash);
            row.AddValue("headingDegrees", R(headingDegrees));
            ColonyPlacementTerrainDiagnostic.Add(body, template, lat, lon, result.TerrainHeight, row, headingDegrees);
        }
        Need(System.Text.Encoding.UTF8.GetByteCount(evidence.ToString()) <= 12 * 1024 * 1024, "Native edge evidence exceeds12MiB");
    }

    private static void Add(Dictionary<long, Edge> edges, PQ quad, Mesh mesh, int a, int b, Vector3 pa, Vector3 pb, Vector3 normal, int triangle)
    {
        if (a > b) { int swap = a; a = b; b = swap; var point = pa; pa = pb; pb = point; }
        long key = ((long)a << 32) | (uint)b; Edge edge;
        if (!edges.TryGetValue(key, out edge)) edges.Add(key, new Edge { Quad = quad, Mesh = mesh, A = a, B = b, PointA = pa, PointB = pb, NormalA = normal, TriangleA = triangle });
        else { Need(edge.TriangleB < 0, "Native candidate mesh edge has more than2 adjacent faces"); edge.TriangleB = triangle; edge.NormalB = normal; }
    }
    private static double Height(CelestialBody body, double latitude, double longitude)
    {
        var radial = QuaternionD.AngleAxis(longitude, Vector3d.down) * QuaternionD.AngleAxis(latitude, Vector3d.forward) * Vector3d.right;
        double height = body.pqsController.GetSurfaceHeight(radial) - body.Radius; Need(ColonyPlacementRequest.Finite(height), "Actual native PQS estimate is nonfinite"); return height;
    }
    private static bool Finite(Vector3 p) => ColonyPlacementRequest.Finite(p.x) && ColonyPlacementRequest.Finite(p.y) && ColonyPlacementRequest.Finite(p.z);
    private static string V(Vector3 p) => R(p.x) + "," + R(p.y) + "," + R(p.z);
    private static string R(double n) => n.ToString("R", CultureInfo.InvariantCulture);
    private static void Need(bool b, string reason) { if (!b) throw new InvalidOperationException(reason); }
}
