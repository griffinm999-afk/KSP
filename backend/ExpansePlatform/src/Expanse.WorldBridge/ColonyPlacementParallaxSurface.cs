using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed class ColonyPlacementSurfaceCheck
    {
        public bool Ready { get; internal set; }
        public bool Clear { get; internal set; }
        public string Reason { get; internal set; } = "";
        public string Witness { get; internal set; } = "";
        public int QuadCount { get; internal set; }
        public int InstanceCount { get; internal set; }
    }

    // Parallax activates scatter collisions asynchronously near EACH loaded
    // vessel's physical bounds. A distant small reference cannot expose all
    // obstacles at a future building. Inspect its exact native collision data
    // without creating/registering/activating colliders or completing jobs.
    public static class ColonyPlacementParallaxSurface
    {
        private const string ReviewedAssemblyHash = "faf58f3940e961bf6af4e059c4b5c86a7718a67097ea91bf9c79511765644605";
        private const string ReviewedComputeBundleHash = "569ef71deda2d547c1fab4670f8eb1a5a4f398819ade3cd0ea2ad675ff030d3c";
        private const int MaximumQuads = 4096, MaximumInstances = 100000;
        private static Assembly reviewedAssembly;
        private static string assemblyHash;
        private static string computeBundleHash;

        // Select only the actual body's continuous PQ mesh. A Parallax object
        // is parented below a PQ too, so GetComponentInParent<PQ>() alone would
        // incorrectly call its rock mesh support ground.
        public static bool TerrainRay(CelestialBody body, Vector3 origin, Vector3 direction, float distance, out RaycastHit hit, out string reason)
        {
            hit = default(RaycastHit); reason = "Loaded continuous native PQS terrain unavailable";
            if (body == null || body.pqsController == null || !Finite(origin) || !Finite(direction) || !Finite(distance) || distance <= 0) return false;
            var hits = Physics.RaycastAll(origin, direction, distance, 1 << 15, QueryTriggerInteraction.Ignore);
            if (hits.Length > 256) { reason = "Terrain collider identity query exceeds256-hit bound"; return false; }
            foreach (var candidate in hits.OrderBy(x => x.distance))
            {
                if (IsContinuousGround(candidate.collider, body))
                { hit = candidate; reason = ""; return true; }
            }
            return false;
        }
        public static bool IsContinuousGround(Collider collider, CelestialBody body)
        { var quad = collider == null ? null : collider.GetComponentInParent<PQ>(); return body != null && quad != null && quad.sphereRoot == body.pqsController && collider == quad.meshCollider; }

        public static ColonyPlacementSurfaceCheck Check(CelestialBody body, double latitude, double longitude, double height, double heading,
            double minX, double maxX, double minZ, double maxZ, double maximumHeight, double clearance)
        {
            var result = new ColonyPlacementSurfaceCheck();
            try
            {
                Need(body != null && body.pqsController != null && ColonyPlacementRequest.Finite(latitude) && ColonyPlacementRequest.Finite(longitude) && ColonyPlacementRequest.Finite(height) &&
                    ColonyPlacementRequest.Finite(heading) && ColonyPlacementRequest.Range(minX, -100, 100) && ColonyPlacementRequest.Range(maxX, -100, 100) && minX < maxX &&
                    ColonyPlacementRequest.Range(minZ, -100, 100) && ColonyPlacementRequest.Range(maxZ, -100, 100) && minZ < maxZ && ColonyPlacementRequest.Range(maximumHeight, .1, 100) && ColonyPlacementRequest.Range(clearance, 0, 100), "Finite bounded surface collision terms missing");
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.IndexOf("Parallax", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var terms = new[] { latitude, longitude, height, heading, minX, maxX, minZ, maxZ, maximumHeight, clearance };
                if (assemblies.Length == 0) { result.Ready = result.Clear = true; result.Witness = BasicWitness("native-pqs;no-loaded-parallax-assembly", body.bodyName, terms); return result; }
                var providers = assemblies.Where(a => a.GetType("Parallax.CollisionManager", false) != null).ToArray();
                Need(providers.Length == 1, "Installed Parallax collision provider is unknown or ambiguous; qualified site observation unavailable");
                var assembly = providers[0];
                if (!ReferenceEquals(reviewedAssembly, assembly))
                {
                    string location = assembly.Location;
                    Need(!string.IsNullOrEmpty(location) && System.IO.File.Exists(location), "Loaded Parallax collision assembly identity unavailable");
                    assemblyHash = ColonyPlacementRequest.Hash(System.IO.File.ReadAllBytes(location)); reviewedAssembly = assembly;
                }
                Need(assemblyHash == ReviewedAssemblyHash, "Installed Parallax collision assembly differs from the reviewed native provider");
                var manager = assembly.GetType("Parallax.CollisionManager", true); var component = assembly.GetType("Parallax.ScatterComponent", true);
                var bodyCatalog = ReadStatic(assembly.GetType("Parallax.ConfigLoader", true), "parallaxScatterBodies") as IDictionary;
                Need(bodyCatalog != null && bodyCatalog.Count <= 256, "Native Parallax configured-body collision catalog unavailable");
                if (!bodyCatalog.Contains(body.bodyName)) { result.Ready = result.Clear = true; result.Witness = BasicWitness("native-pqs;parallax=" + assemblyHash + ";body-not-configured", body.bodyName, terms); return result; }
                ValidateComputeBundle();
                Need((bool)ReadStatic(manager, "initialized"), "Parallax collision manager has not initialized the current body");
                var scatterManager = assembly.GetType("Parallax.ScatterManager", true);
                var liveCollisionManager = ReadStatic(manager, "Instance"); var liveScatterManager = ReadStatic(scatterManager, "Instance");
                Need(LiveProvider(liveCollisionManager) && LiveProvider(liveScatterManager), "Native Parallax collision/scatter manager is destroyed, disabled or inactive");
                var scatterBody = ReadStatic(manager, "currentScatterBody");
                Need(scatterBody != null && (string)Read(scatterBody, "planetName") == body.bodyName, "Parallax collision manager belongs to a different body");
                var scatters = (Array)ReadStatic(manager, "collideableScatters");
                Need(scatters != null && scatters.Length <= 256, "Parallax collision scatter catalog unavailable or exceeds256 types");
                Need(ReferenceEquals(scatterBody, bodyCatalog[body.bodyName]), "Native Parallax current body differs from its configured identity");
                Need(ReferenceEquals(scatters, Read(scatterBody, "collideableScatters")) && (int)ReadStatic(manager, "numCollideableScatters") == scatters.Length, "Native current-body collision species array/count is incomplete");
                var fastScatters = (Array)Read(scatterBody, "fastScatters"); Need(fastScatters != null && fastScatters.Length <= 1024, "Native current-body scatter species catalog unavailable");
                foreach (var scatter in scatters) Need(fastScatters.Cast<object>().Any(s => ReferenceEquals(s, scatter)), "Native collision scatter species belongs to a different catalog");
                var activeRenderers = Read(liveScatterManager, "activeScatterRenderers") as IList; var rendererCatalog = Read(liveScatterManager, "fastScatterRenderers") as IDictionary;
                Need(activeRenderers != null && activeRenderers.Count <= 4096 && rendererCatalog != null && rendererCatalog.Count <= 4096, "Native active scatter renderer catalog missing or unbounded");
                foreach (var scatter in scatters) ValidateRenderer(scatter, body.bodyName, activeRenderers, rendererCatalog);
                if (scatters.Length == 0) { result.Ready = result.Clear = true; result.Witness = BasicWitness("native-pqs;parallax=" + assemblyHash + ";collideable-types=0", body.bodyName, terms); return result; }
                var quads = ReadStatic(component, "scatterQuadData") as IDictionary;
                Need(quads != null && quads.Count <= MaximumQuads, "Parallax visible-quad data unavailable or exceeds4096 bound");
                Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, height); Vector3 radial = (Vector3)(surface - body.position).normalized; RaycastHit center; string rayReason;
                Need(TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out center, out rayReason), rayReason);
                var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(latitude + .001, longitude, height) - surface), center.normal).normalized;
                Need(north.sqrMagnitude > .9f, "Parallax site north frame unavailable");
                var frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)heading, center.normal) * north, center.normal); var inverse = Quaternion.Inverse(frame);
                double largestRadius = 0;
                foreach (var scatter in scatters)
                {
                    var mesh = CollisionMesh(scatter); var distribution = Read(scatter, "distributionParams"); var maxScale = (Vector3)Read(distribution, "maxScale"); var minScale = (Vector3)Read(distribution, "minScale"); float randomness = (float)Read(distribution, "scaleRandomness");
                    Need(BoundedScaleDistribution(minScale, maxScale, randomness), "Native Parallax collision min/max scale or unclamped randomness cannot prove maximum collider reach");
                    Need(!(bool)Read(distribution, "fixedAltitude"), "Native collideable fixed-altitude scatter has no reviewed quad-reach bound");
                    foreach (var corner in Corners(mesh.bounds)) largestRadius = Math.Max(largestRadius, Vector3.Scale(corner, maxScale).magnitude);
                }
                Need(largestRadius <= 1000, "Native scatter reach exceeds1000m conservative collision query bound");
                int nx = Math.Max(2, (int)Math.Ceiling(maxX - minX)), nz = Math.Max(2, (int)Math.Ceiling(maxZ - minZ));
                Need((nx + 1) * (nz + 1) <= 4096, "Complete collision-provider footprint exceeds4096 terrain points");
                var required = new HashSet<PQ>(); double low = double.PositiveInfinity, high = double.NegativeInfinity;
                for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
                {
                    var point = center.point + frame * new Vector3((float)(minX + (maxX - minX) * x / nx), 0, (float)(minZ + (maxZ - minZ) * z / nz)); RaycastHit hit;
                    Need(TerrainRay(body, point + center.normal * 20, -center.normal, 40, out hit, out rayReason), rayReason);
                    var quad = hit.collider.GetComponentInParent<PQ>();
                    Need(quad.subdivision == body.pqsController.maxLevel && quads.Contains(quad), "Full footprint Parallax max-resolution collision distribution is not loaded; approach the site and wait");
                    required.Add(quad); double y = Vector3.Dot(hit.point - center.point, center.normal); low = Math.Min(low, y); high = Math.Max(high, y);
                }
                var envelope = new Bounds(new Vector3((float)((minX + maxX) / 2), (float)((low + high + maximumHeight) / 2), (float)((minZ + maxZ) / 2)),
                    new Vector3((float)(maxX - minX + 2 * clearance), (float)(high - low + maximumHeight + .1), (float)(maxZ - minZ + 2 * clearance)));
                // Traverse the actual six-root native tree, independently of
                // published scatter rows and physics colliders. An unbuilt thin
                // neighboring child must not disappear between sample rays.
                AddRequiredNativeLeaves(body, center.point + frame * envelope.center, envelope.extents.magnitude + largestRadius, quads, required);
                var witness = new StringBuilder(); witness.Append("native-pqs;parallax=").Append(assemblyHash).Append(";compute=").Append(computeBundleHash).Append(";body=").Append(body.bodyName).Append(';');
                foreach (var term in terms) witness.Append(term.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                string blocked = null; int sourceVertexCount = 0;
                foreach (var quad in required)
                {
                    Need(quad.meshRenderer != null, "Native collision quad renderer missing");
                    Need(Finite(quad.meshRenderer.bounds.center) && Finite(quad.meshRenderer.bounds.extents), "Native collision quad world bounds invalid");
                    var scale = quad.transform.lossyScale; Need(Finite(scale) && scale.x > 0 && Math.Abs(scale.x - 1) < .00001 && Math.Abs(scale.y - 1) < .00001 && Math.Abs(scale.z - 1) < .00001, "Native PQ collision hierarchy scale differs from reviewed unit-scale transform");
                    var matrix = quad.transform.localToWorldMatrix; var a = new Vector3(matrix.m00, matrix.m10, matrix.m20); var b = new Vector3(matrix.m01, matrix.m11, matrix.m21); var c = new Vector3(matrix.m02, matrix.m12, matrix.m22);
                    Need(Finite(a) && Finite(b) && Finite(c) && Math.Abs(a.sqrMagnitude - 1) < .00002 && Math.Abs(b.sqrMagnitude - 1) < .00002 && Math.Abs(c.sqrMagnitude - 1) < .00002 && Math.Abs(Vector3.Dot(a, b)) < .00001 && Math.Abs(Vector3.Dot(a, c)) < .00001 && Math.Abs(Vector3.Dot(b, c)) < .00001, "Native PQ collision hierarchy is sheared or not unit scale");
                    result.QuadCount++;
                    var data = quads[quad]; var rows = Read(data, "quadScatters") as IList;
                    Need(ReferenceEquals(Read(data, "quad"), quad) && ReferenceEquals(Read(data, "body"), scatterBody), "Native scatter quad distribution belongs to another terrain/body");
                    ValidateQuadBuffers(data);
                    var vertices = Read(data, "vertices") as Vector3[];
                    Need(vertices != null && vertices.Length >= 3 && vertices.Length <= 1048576 - sourceVertexCount, "Native terrain source vertices are missing or exceed1048576 observation bound"); sourceVertexCount += vertices.Length;
                    var sphereRotation = (QuaternionD)body.pqsController.transform.rotation;
                    foreach (var vertex in vertices)
                    {
                        Need(Finite(vertex), "Native terrain source vertex is not finite"); var radialVertex = (Vector3d)quad.transform.TransformPoint(vertex) - (Vector3d)body.pqsController.transform.position;
                        Need(radialVertex.magnitude > 1 && Vector3d.Dot(radialVertex, sphereRotation * quad.positionPlanePosition) > 0 && RadialCapsOverlap(sphereRotation * quad.positionPlanePosition, quad.scalePlaneRelative, radialVertex, 0), "Native terrain vertex violates positive-height cube radial coverage assumptions");
                    }
                    Need(rows != null && rows.Count <= 256, "Native scatter quad distribution list missing or unbounded");
                    witness.Append(quad.name).Append('@').Append(quad.subdivision).Append(';');
                    var species = new HashSet<int>();
                    foreach (var row in rows)
                    {
                        var scatter = Read(row, "scatter"); if (!(bool)Read(scatter, "collideable")) continue;
                        Need(ReferenceEquals(Read(row, "parent"), data), "Native scatter row belongs to another quad distribution");
                        int scatterIndex = (int)Read(scatter, "collideableArrayIndex"); Need(scatterIndex >= 0 && scatterIndex < scatters.Length && ReferenceEquals(scatters.GetValue(scatterIndex), scatter) && fastScatters.Cast<object>().Any(s => ReferenceEquals(s, scatter)), "Native collider species differs from current body catalog/index");
                        Need(species.Add(scatterIndex), "Native collision quad has duplicate species distributions");
                        Need(!(bool)Read(row, "cleaned") && !(bool)Read(row, "paused") && (bool)Read(row, "eventAdded"), "Relevant Parallax scatter distribution is pending/paused; wait for native GPU readback");
                        var collision = Read(row, "collisionData");
                        Need((bool)Read(row, "collidersAdded") && collision != null, "Relevant Parallax collision positions await native GPU readback");
                        Need(ReferenceEquals(Read(collision, "scatterSystemQuad"), data) && (int)Read(collision, "collideableScattersIndex") == scatterIndex, "Native collision position data belongs to another quad/species");
                        var positions = Read(collision, "quadLocalData"); var arrayType = positions.GetType();
                        Need((bool)Property(positions, "IsCreated"), "Native Parallax collision positions were disposed");
                        int count = (int)Property(positions, "Length"); Need(CompleteCounts((int)Read(row, "realCount"), count, (int)Read(collision, "dataCount")), "Native collision GPU readback was truncated or disagrees with its actual distribution count");
                        Need(count <= MaximumInstances - result.InstanceCount, "Native collision enumeration exceeds100000-instance bound");
                        var item = arrayType.GetProperty("Item"); Need(item != null, "Native collision position indexer unavailable"); var mesh = CollisionMesh(scatter); var distribution = Read(scatter, "distributionParams");
                        for (int i = 0; i < count; i++)
                        {
                            result.InstanceCount++; var position = item.GetValue(positions, new object[] { i }); var local = (Vector3)Read(position, "localPos"); var instanceScale = (Vector3)Read(position, "localScale"); float angle = (float)Read(position, "rotation");
                            var maxScale = (Vector3)Read(distribution, "maxScale");
                            Need(Finite(local) && Finite(instanceScale) && instanceScale.x > 0 && instanceScale.y > 0 && instanceScale.z > 0 && instanceScale.x <= maxScale.x + .00001 && instanceScale.y <= maxScale.y + .00001 && instanceScale.z <= maxScale.z + .00001 && Finite(angle), "Native Parallax instance transform exceeds its bounded distribution");
                            var world = quad.transform.TransformPoint(local); if ((inverse * (world - center.point) - envelope.center).magnitude > envelope.extents.magnitude + largestRadius) continue;
                            Vector3 normal;
                            if ((int)Read(distribution, "alignToTerrainNormal") == 1)
                            {
                                var normalMethod = data.GetType().GetMethod("GetTerrainNormal", BindingFlags.Public | BindingFlags.Instance); Need(normalMethod != null, "Native scatter terrain-normal readback unavailable");
                                normal = (Vector3)normalMethod.Invoke(data, new[] { Read(position, "index") });
                            }
                            else normal = (quad.transform.position - quad.sphereRoot.transform.position).normalized;
                            Need(Finite(normal) && normal.sqrMagnitude > .9f, "Native scatter collision orientation invalid");
                            var rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.AngleAxis(angle, Vector3.up);
                            Bounds shape = TransformMeshBounds(mesh.bounds, world, rotation, instanceScale, center.point, inverse);
                            witness.Append((string)Read(scatter, "scatterName")).Append(':').Append(i).Append(':'); AppendVector(witness, shape.min); AppendVector(witness, shape.max);
                            if (shape.Intersects(envelope) && blocked == null) blocked = "Complete deployment/support/access envelope intersects latent native Parallax scatter " + (string)Read(scatter, "scatterName") + "; select a clear plot (active colliders alone cannot prove clearance)";
                        }
                        Need(ReferenceEquals(Read(row, "collisionData"), collision) && ReferenceEquals(Read(row, "parent"), data) && (bool)Property(positions, "IsCreated") && CompleteCounts((int)Read(row, "realCount"), (int)Property(positions, "Length"), (int)Read(collision, "dataCount")) && !(bool)Read(row, "cleaned") && !(bool)Read(row, "paused") && (bool)Read(row, "eventAdded") && (bool)Read(row, "collidersAdded"), "Native scatter collision data changed during the read-only observation");
                    }
                    Need(ReferenceEquals(quads[quad], data) && ReferenceEquals(Read(data, "quadScatters"), rows), "Native collision quad was replaced during observation");
                    ValidateQuadBuffers(data);
                }
                Need(result.QuadCount >= required.Count, "Native collision data does not cover required terrain quads");
                Need(ReferenceEquals(ReadStatic(manager, "currentScatterBody"), scatterBody) && ReferenceEquals(ReadStatic(component, "scatterQuadData"), quads) && (bool)ReadStatic(manager, "initialized"), "Native collision provider/body changed during observation");
                Need(ReferenceEquals(ReadStatic(manager, "Instance"), liveCollisionManager) && ReferenceEquals(ReadStatic(scatterManager, "Instance"), liveScatterManager) && LiveProvider(liveCollisionManager) && LiveProvider(liveScatterManager) && ReferenceEquals(ReadStatic(manager, "collideableScatters"), scatters) && (int)ReadStatic(manager, "numCollideableScatters") == scatters.Length, "Native live collision/scatter provider changed during observation");
                foreach (var scatter in scatters) ValidateRenderer(scatter, body.bodyName, activeRenderers, rendererCatalog);
                var finalCoverage = new HashSet<PQ>(); AddRequiredNativeLeaves(body, center.point + frame * envelope.center, envelope.extents.magnitude + largestRadius, quads, finalCoverage);
                Need(finalCoverage.IsSubsetOf(required), "Native collision reach terrain hierarchy changed during observation");
                result.Ready = true; result.Clear = blocked == null; result.Reason = blocked ?? "Complete native PQS and latent Parallax collision envelope readback is clear";
                result.Witness = ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(witness.ToString()));
            }
            catch (Exception e) { result.Ready = result.Clear = false; result.Reason = "Surface collision provider awaits qualification: " + e.GetBaseException().Message; }
            return result;
        }

        internal static Bounds TransformMeshBounds(Bounds local, Vector3 world, Quaternion rotation, Vector3 scale, Vector3 origin, Quaternion inverse)
        {
            Need(Finite(local.center) && Finite(local.extents) && local.extents.x >= 0 && local.extents.y >= 0 && local.extents.z >= 0 && Finite(world) && Finite(origin) && Finite(scale) && scale.x > 0 && scale.y > 0 && scale.z > 0 && Finite(rotation.x) && Finite(rotation.y) && Finite(rotation.z) && Finite(rotation.w) && Math.Abs(Quaternion.Dot(rotation, rotation) - 1) < .00001 && Finite(inverse.x) && Finite(inverse.y) && Finite(inverse.z) && Finite(inverse.w) && Math.Abs(Quaternion.Dot(inverse, inverse) - 1) < .00001, "Native collision mesh transform is invalid");
            Bounds result = default(Bounds); bool first = true;
            foreach (var corner in Corners(local)) { var p = inverse * (world + rotation * Vector3.Scale(corner, scale) - origin); Need(Finite(p), "Native collision bound not finite"); if (first) { result = new Bounds(p, Vector3.zero); first = false; } else result.Encapsulate(p); }
            return result;
        }
        private static void AddRequiredNativeLeaves(CelestialBody body, Vector3 reachWorldCenter, double reachRadius, IDictionary published, HashSet<PQ> required)
        {
            var sphere = body.pqsController; var roots = sphere.quads;
            Need(roots != null && roots.Length == 6 && PQS.cacheMeshSize == 1f, "Native six-root PQS unit cube coverage is unavailable");
            Need(ColonyPlacementRequest.Finite(sphere.radius) && sphere.radius > 0 && ColonyPlacementRequest.Finite(sphere.meshVertMin) && ColonyPlacementRequest.Finite(sphere.meshVertMax) && sphere.meshVertMin <= sphere.meshVertMax && sphere.radius + sphere.meshVertMin > 0, "Native terrain built-vertex tracker cannot prove positive radial height before coverage pruning");
            var sphereScale = sphere.transform.lossyScale;
            Need(Finite(sphereScale) && Math.Abs(sphereScale.x - 1) < .00001 && Math.Abs(sphereScale.y - 1) < .00001 && Math.Abs(sphereScale.z - 1) < .00001, "Native PQS cube coverage transform is not unit scale");
            var matrix = sphere.transform.localToWorldMatrix; var a = new Vector3(matrix.m00, matrix.m10, matrix.m20); var b = new Vector3(matrix.m01, matrix.m11, matrix.m21); var c = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            Need(Finite(a) && Finite(b) && Finite(c) && Math.Abs(a.sqrMagnitude - 1) < .00002 && Math.Abs(b.sqrMagnitude - 1) < .00002 && Math.Abs(c.sqrMagnitude - 1) < .00002 && Math.Abs(Vector3.Dot(a, b)) < .00001 && Math.Abs(Vector3.Dot(a, c)) < .00001 && Math.Abs(Vector3.Dot(b, c)) < .00001, "Native PQS cube hierarchy is sheared or not unit scale before radial coverage pruning");
            var rotation = (QuaternionD)sphere.transform.rotation; var reach = (Vector3d)reachWorldCenter - (Vector3d)sphere.transform.position;
            int visited;
            var leaves = CollectRelevantLeaves(roots, quad =>
            {
                Need(quad != null && quad.sphereRoot == sphere && quad.subdivision >= 0 && quad.subdivision <= sphere.maxLevel, "Native terrain tree contains an absent or foreign node");
                return RadialCapsOverlap(rotation * quad.positionPlanePosition, quad.scalePlaneRelative, reach, reachRadius);
            }, quad =>
            {
                if (!quad.isSubdivided) return null;
                var children = quad.subNodes;
                Need(children != null && children.Length == 4 && children.All(child => child != null && child.parent == quad && child.subdivision == quad.subdivision + 1 && child.sphereRoot == sphere), "Relevant native terrain subdivision has an absent/foreign child; wait for native build");
                return children;
            }, quad => quad.subdivision == sphere.maxLevel && NativeLeafReady(quad.isActive, quad.isBuilt, quad.isVisible, quad.isCached, quad.isPendingCollapse, quad.isForcedInvisible) && quad.meshCollider != null && quad.meshCollider.enabled && published.Contains(quad), MaximumQuads, out visited);
            foreach (var leaf in leaves) required.Add(leaf);
            Need(leaves.Count > 0, "Native latent-collision reach has no complete relevant terrain leaves");
        }

        // BuildQuad maps [-.5,.5]^2 through quadMatrix; native SetupQuad and
        // Subdivide give a cube-plane half-width scalePlaneRelative. Every
        // positive-height mesh triangle and barycentric scatter root lies in
        // this radial cap. A ball enclosing the whole requested envelope plus
        // the largest collider radius encloses every potentially intruding root.
        // The angular safety padding only expands provider coverage; it changes
        // no slope, support, geometry or clearance acceptance tolerance.
        internal static bool RadialCapsOverlap(Vector3d cubeCenter, double halfWidth, Vector3d reachCenter, double reachRadius)
        {
            double cn = cubeCenter.magnitude, rn = reachCenter.magnitude;
            Need(Finite(cubeCenter) && Finite(reachCenter) && ColonyPlacementRequest.Finite(halfWidth) && halfWidth > 0 && ColonyPlacementRequest.Finite(reachRadius) && reachRadius >= 0 && cn > 0 && rn > reachRadius, "Native PQ radial reach bounds invalid");
            double squareRadius = Math.Sqrt(2) * halfWidth;
            if (squareRadius >= cn) return true;
            double separation = Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(cubeCenter, reachCenter) / (cn * rn))));
            return separation <= Math.Asin(squareRadius / cn) + Math.Asin(reachRadius / rn) + .000001;
        }
        internal static HashSet<T> CollectRelevantLeaves<T>(IEnumerable<T> roots, Func<T, bool> relevant, Func<T, IList<T>> children, Func<T, bool> ready, int maximum, out int visited) where T : class
        {
            var seen = new HashSet<T>(); var leaves = new HashSet<T>(); var pending = new Stack<T>(roots); visited = 0;
            while (pending.Count != 0)
            {
                var node = pending.Pop();
                Need(node != null && seen.Add(node) && ++visited <= maximum, "Native terrain tree is missing, cyclic or exceeds4096 nodes");
                if (!relevant(node)) continue;
                var descendants = children(node);
                if (descendants != null)
                { Need(descendants.Count == 4 && descendants.All(child => child != null), "Relevant native terrain subdivision is incomplete"); foreach (var child in descendants) pending.Push(child); }
                else { Need(ready(node), "Relevant native terrain leaf is inactive, unbuilt, hidden, collapsing or lacks complete max-resolution Parallax distribution; approach and wait"); leaves.Add(node); }
            }
            return leaves;
        }
        internal static bool NativeLeafReady(bool active, bool built, bool visible, bool cached, bool collapsing, bool forcedInvisible) => active && built && visible && !cached && !collapsing && !forcedInvisible;
        private static bool LiveProvider(object instance)
        { var native = instance as Behaviour; return native != null && native.isActiveAndEnabled; }
        private static void ValidateRenderer(object scatter, string body, IList active, IDictionary catalog)
        {
            var renderer = Read(scatter, "renderer") as Component; string name = (string)Read(scatter, "scatterName");
            // Native Start deliberately creates these renderer components on an
            // inactive GameObject, then calls Enable/Render explicitly. Their
            // exact active catalog and valid buffers are the native authority.
            Need(renderer != null && (string)Read(renderer, "planetName") == body && ReferenceEquals(Read(renderer, "scatter"), scatter) && active.Cast<object>().Any(r => ReferenceEquals(r, renderer)) && catalog.Contains(name) && ReferenceEquals(catalog[name], renderer), "Native current-body collision renderer is destroyed, unloaded or belongs to another catalog");
            foreach (var field in new[] { "outputLOD0", "outputLOD1", "outputLOD2" }) Need(ValidBuffer(Read(renderer, field)), "Native current-body collision renderer buffers are disposed: " + field);
            var nativeShader = Read(scatter, "shader") as ComputeShader;
            Need(nativeShader != null && nativeShader.name == "TerrainScatters(Clone)", "Native current-body scatter distribution shader clone is destroyed or differs from reviewed provider");
            CollisionMesh(scatter);
        }
        private static void ValidateQuadBuffers(object data)
        { foreach (var field in new[] { "sourceVertsBuffer", "sourceNormalsBuffer", "sourceTrianglesBuffer", "sourceColorsBuffer", "sourceUVsBuffer", "sourceDirsFromCenterBuffer" }) Need(ValidBuffer(Read(data, field)), "Native published quad was cleaned or its source GPU buffers are disposed: " + field); }
        private static bool ValidBuffer(object value) { var buffer = value as ComputeBuffer; return buffer != null && buffer.IsValid(); }
        private static void ValidateComputeBundle()
        {
            Need(Application.platform == RuntimePlatform.WindowsPlayer && !SystemInfo.graphicsDeviceVersion.StartsWith("OpenGL", StringComparison.OrdinalIgnoreCase), "Native scatter distribution shader platform differs from reviewed Windows bundle");
            var lists = GameDatabase.Instance.GetConfigs("ParallaxAssetBundleList");
            Need(lists != null && lists.Length == 1, "Native Parallax asset bundle list is absent or ambiguous");
            var nodes = lists[0].config.GetNodes("ComputeShaders");
            Need(nodes.Length == 1 && nodes[0].GetValues("path").SequenceEqual(new[] { "ParallaxContinued/Shaders/ParallaxCompute" }), "Native collision compute shader path differs from reviewed package");
            if (computeBundleHash == null)
            {
                var path = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "ParallaxContinued", "Shaders", "ParallaxCompute-windows.unity3d");
                Need(System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length <= 1024 * 1024, "Native reviewed scatter compute bundle is missing or unbounded");
                computeBundleHash = ColonyPlacementRequest.Hash(System.IO.File.ReadAllBytes(path));
            }
            Need(computeBundleHash == ReviewedComputeBundleHash, "Native collision distribution shader bundle differs from reviewed barycentric provider");
            var shaders = ReadStatic(reviewedAssembly.GetType("Parallax.AssetBundleLoader", true), "parallaxComputeShaders") as IDictionary;
            var shader = shaders == null || !shaders.Contains("TerrainScatters") ? null : shaders["TerrainScatters"] as ComputeShader;
            Need(shader != null && shader.name == "TerrainScatters", "Native loaded scatter compute shader object is missing or destroyed");
        }
        private static Mesh CollisionMesh(object scatter)
        { var mesh = Read(Read(scatter, "renderer"), "meshLOD1") as Mesh; Need(mesh != null && mesh.vertexCount > 0, "Native Parallax meshLOD1 collision mesh unavailable"); return mesh; }
        private static IEnumerable<Vector3> Corners(Bounds bounds)
        { for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)); }
        private static object ReadStatic(Type type, string field) { var f = type.GetField(field, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); Need(f != null, "Native collision field unavailable: " + field); return f.GetValue(null); }
        private static object Read(object instance, string field) { Need(instance != null, "Native collision object unavailable: " + field); var f = instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); Need(f != null, "Native collision field unavailable: " + field); return f.GetValue(instance); }
        private static object Property(object instance, string property) { var p = instance.GetType().GetProperty(property); Need(p != null, "Native collision property unavailable: " + property); return p.GetValue(instance, null); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector3d value) => ColonyPlacementRequest.Finite(value.x) && ColonyPlacementRequest.Finite(value.y) && ColonyPlacementRequest.Finite(value.z);
        private static void AppendVector(StringBuilder text, Vector3 vector) { text.Append(vector.x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(vector.y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(vector.z.ToString("R", CultureInfo.InvariantCulture)).Append(';'); }
        private static string BasicWitness(string mode, string body, double[] terms) { return ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(mode + ";body=" + body + ";" + string.Join(";", terms.Select(t => t.ToString("R", CultureInfo.InvariantCulture))))); }
        internal static bool CompleteCounts(int nativeDistributed, int nativePositions, int nativeDataCount) => nativeDistributed > 0 && nativeDistributed == nativePositions && nativeDistributed == nativeDataCount;
        internal static bool BoundedScaleDistribution(Vector3 min, Vector3 max, float randomness) => Finite(min) && Finite(max) && Finite(randomness) && randomness >= 0 && randomness <= 1 && min.x > 0 && min.y > 0 && min.z > 0 && min.x <= max.x && min.y <= max.y && min.z <= max.z;
        private static void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    }
}
