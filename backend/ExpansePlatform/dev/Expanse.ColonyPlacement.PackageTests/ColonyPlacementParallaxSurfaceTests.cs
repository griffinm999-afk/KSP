using System;
using System.Collections.Generic;
using Expanse.WorldBridge;
using UnityEngine;

internal static class ColonyPlacementParallaxSurfaceTests
{
    internal static void Run(Action<bool, string> check)
    {
        // The native collider uses meshLOD1 local bounds, including a nonzero
        // mesh origin. Exercise that physical origin, full nonuniform scale and
        // world orientation against independent analytical expected intervals.
        float half = (float)Math.Sqrt(.5);
        var local = new Bounds(new Vector3(2, -1, .5f), new Vector3(2, 4, 6));
        var actual = ColonyPlacementParallaxSurface.TransformMeshBounds(local, new Vector3(10, 20, 30), new Quaternion(0, half, 0, half), new Vector3(2, 3, 4), new Vector3(9, 18, 27), Quaternion.identity);
        check(Near(actual.min, new Vector3(-9, -7, -3)) && Near(actual.max, new Vector3(15, 5, 1)), "Latent native collider origin/scale/world rotation was understated");
        var projected = ColonyPlacementParallaxSurface.TransformMeshBounds(local, Vector3.zero, new Quaternion(0, half, 0, half), new Vector3(2, 3, 4), Vector3.zero, new Quaternion(0, -half, 0, half));
        check(Near(projected.min, new Vector3(2, -9, -10)) && Near(projected.max, new Vector3(6, 3, 14)), "Native collider was rotated into surface axes twice or dropped offset center");
        foreach (var scale in new[] { Vector3.zero, new Vector3(1, -1, 1), new Vector3(float.NaN, 1, 1) })
        {
            bool rejected = false;
            try { ColonyPlacementParallaxSurface.TransformMeshBounds(local, Vector3.zero, Quaternion.identity, scale, Vector3.zero, Quaternion.identity); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Invalid latent collider scale could qualify a clear surface");
        }
        foreach (var rotation in new[] { new Quaternion(0, 0, 0, 0), new Quaternion(0, 0, 0, 2), new Quaternion(float.NaN, 0, 0, 1) })
        {
            bool rejected = false;
            try { ColonyPlacementParallaxSurface.TransformMeshBounds(local, Vector3.zero, rotation, Vector3.one, Vector3.zero, Quaternion.identity); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Invalid native collider rotation could qualify a clear surface");
        }
        var old = new ColonyPlacementStatus { OperationId = "old", SurfaceCollisionWitness = new string('a', 64) };
        check(ColonyPlacementScenario.CopyStatus(old).SurfaceCollisionWitness == old.SurfaceCollisionWitness, "Published status clone lost exact provider witness");
        var node = ColonyPlacementCodec.WriteStatus(old); node.RemoveValues("SurfaceCollisionWitness");
        check(ColonyPlacementCodec.ReadStatus(node).SurfaceCollisionWitness == "", "Older native receipt was lost or assigned fabricated scatter proof");
        node.AddValue("SurfaceCollisionWitness", "a"); node.AddValue("SurfaceCollisionWitness", "b"); bool duplicate = false;
        try { ColonyPlacementCodec.ReadStatus(node); } catch (FormatException) { duplicate = true; }
        check(duplicate, "Duplicate optional native collision authority accepted");
        check(ColonyPlacementParallaxSurface.CompleteCounts(17, 17, 17), "Exact native distribution count rejected");
        foreach (var counts in new[] { new[] { 17, 16, 16 }, new[] { 17, 17, 16 }, new[] { 0, 0, 0 }, new[] { -1, -1, -1 } })
            check(!ColonyPlacementParallaxSurface.CompleteCounts(counts[0], counts[1], counts[2]), "Truncated/pending native positions qualified as complete");
        check(ColonyPlacementParallaxSurface.RadialCapsOverlap(new Vector3d(1, .001, 0), .0005, new Vector3d(320000, 0, 0), 110), "Thin neighboring radial terrain strip omitted from expanded collision reach");
        check(!ColonyPlacementParallaxSurface.RadialCapsOverlap(new Vector3d(1, .1, 0), .0005, new Vector3d(320000, 0, 0), 110), "Distant irrelevant terrain failed conservative radial exclusion");
        check(ColonyPlacementParallaxSurface.RadialCapsOverlap(new Vector3d(1, 0, 0), 1, new Vector3d(320000, 0, 0), 1), "Broad cube root omitted before descendant inspection");
        check(ColonyPlacementParallaxSurface.BoundedScaleDistribution(Vector3.one, new Vector3(2, 3, 4), 0) && ColonyPlacementParallaxSurface.BoundedScaleDistribution(Vector3.one, new Vector3(2, 3, 4), 1), "Valid full native scale interpolation range rejected");
        foreach (float randomness in new[] { -.01f, 1.01f, float.NaN, float.PositiveInfinity })
            check(!ColonyPlacementParallaxSurface.BoundedScaleDistribution(Vector3.one, Vector3.one, randomness), "Native unclamped scale randomness understated latent root reach");
        check(!ColonyPlacementParallaxSurface.BoundedScaleDistribution(new Vector3(2, 1, 1), Vector3.one, .5f) && !ColonyPlacementParallaxSurface.BoundedScaleDistribution(Vector3.zero, Vector3.one, .5f), "Invalid native min/max scale underestimated potential collider reach");
        var localLeaf = new Node { Relevant = true, Ready = true };
        var thinUnbuilt = new Node { Relevant = true, Ready = false };
        var distantPaused = new Node { Relevant = false, Ready = false };
        var root = new Node { Relevant = true, Children = new[] { localLeaf, thinUnbuilt, distantPaused, new Node { Relevant = false } } };
        int visited; bool held = false;
        try { Collect(root, out visited); } catch (InvalidOperationException) { held = true; }
        check(held, "Unbuilt relevant thin child without collider/scatter dictionary entry was accepted");
        thinUnbuilt.Ready = true; var leaves = Collect(root, out visited);
        check(leaves.Count == 2 && visited == 5 && leaves.Contains(thinUnbuilt), "Relevant neighboring leaf lost or distant paused leaf blocked complete coverage");
        root.Children[1] = null; held = false;
        try { Collect(root, out visited); } catch (InvalidOperationException) { held = true; }
        check(held, "Missing native subdivision child was inferred clear from collider absence");
        root.Children[1] = root; held = false;
        try { Collect(root, out visited); } catch (InvalidOperationException) { held = true; }
        check(held, "Cyclic native terrain tree escaped bounded traversal");
        for (int flags = 0; flags < 64; flags++)
            check(ColonyPlacementParallaxSurface.NativeLeafReady((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0) == (flags == 7), "Inactive/unbuilt/hidden/cached/collapsing terrain leaf qualified");
    }
    private sealed class Node { internal bool Relevant, Ready; internal Node[] Children; }
    private static HashSet<Node> Collect(Node root, out int visited) => ColonyPlacementParallaxSurface.CollectRelevantLeaves(new[] { root }, n => n.Relevant, n => n.Children, n => n.Ready, 32, out visited);
    private static bool Near(Vector3 a, Vector3 b) => (a - b).magnitude < .00001;
}
