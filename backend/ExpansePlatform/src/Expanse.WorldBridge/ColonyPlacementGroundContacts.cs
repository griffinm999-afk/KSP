using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // Native Unity contacts only. This observer changes no collider, rigidbody,
    // force, terrain, pose, module state or economic balance.
    public sealed class ColonyPlacementContactObserver : MonoBehaviour
    {
        internal struct Contact
        {
            public Vector3d Position;
            public Vector3 Normal;
            public uint PartId;
            public Collider Collider;
        }
        internal Vessel Owner;
        internal string Operation;
        internal struct ColliderPair : IEquatable<ColliderPair>
        {
            internal Collider Own, Terrain;
            internal ColliderPair(Collider own, Collider terrain) { Own = own; Terrain = terrain; }
            public bool Equals(ColliderPair other) { return ReferenceEquals(Own, other.Own) && ReferenceEquals(Terrain, other.Terrain); }
            public override bool Equals(object value) { return value is ColliderPair && Equals((ColliderPair)value); }
            public override int GetHashCode() { return RuntimeHelpers.GetHashCode(Own) * 397 ^ RuntimeHelpers.GetHashCode(Terrain); }
        }
        internal sealed class TerrainContactSet
        {
            internal readonly List<Contact> Contacts = new List<Contact>();
            internal float ObservedRealtime = -1;
        }
        internal readonly Dictionary<ColliderPair, TerrainContactSet> TerrainContacts = new Dictionary<ColliderPair, TerrainContactSet>();
        private readonly ContactPoint[] buffer = new ContactPoint[64];

        private void OnCollisionEnter(Collision collision) { Observe(collision); }
        private void OnCollisionStay(Collision collision) { Observe(collision); }
        private void OnCollisionExit(Collision collision)
        {
            if (collision == null || collision.collider == null || collision.collider.gameObject.layer != 15) return;
            RemovePairs(collision, collision.GetContacts(buffer));
        }
        private void RemovePairs(Collision collision, int count)
        {
            bool identified = false;
            for (int i = 0; i < count; i++)
            {
                var point = buffer[i];
                if (point.thisCollider == null || point.otherCollider == null || point.otherCollider != collision.collider) continue;
                identified = true; TerrainContacts.Remove(new ColliderPair(point.thisCollider, point.otherCollider));
            }
            // This Unity version exposes no own collider on a zero-contact exit.
            // Never guess which foot left: invalidate this observer's terrain
            // pairs and require subsequent native stay callbacks to restore them.
            if (!identified || count >= buffer.Length)
                foreach (var key in TerrainContacts.Keys.Where(k => ReferenceEquals(k.Terrain, collision.collider)).ToArray()) TerrainContacts.Remove(key);
        }
        internal bool CurrentPair(ColliderPair pair, TerrainContactSet set, Rigidbody rigidbody)
        {
            return pair.Own != null && pair.Own.enabled && !pair.Own.isTrigger && pair.Own.gameObject.activeInHierarchy &&
                ColonyPlacementParallaxSurface.IsContinuousGround(pair.Terrain, Owner.mainBody) && pair.Terrain.enabled &&
                set.ObservedRealtime >= 0 && (Time.realtimeSinceStartup - set.ObservedRealtime <= .5f || rigidbody.IsSleeping());
        }
        private void Observe(Collision collision)
        {
            if (Owner == null || Owner.mainBody == null || collision == null || !ColonyPlacementParallaxSurface.IsContinuousGround(collision.collider, Owner.mainBody)) return;
            int count = collision.GetContacts(buffer);
            if (count >= buffer.Length) { TerrainContacts.Clear(); return; }
            var root = Owner.rootPart; if (root == null) return;
            var rotation = Quaternion.Inverse(root.transform.rotation);
            var updates = new Dictionary<ColliderPair, TerrainContactSet>();
            for (int i = 0; i < count; i++)
            {
                var point = buffer[i];
                var part = point.thisCollider == null ? null : point.thisCollider.GetComponentInParent<Part>();
                if (!ColonyPlacementParallaxSurface.IsContinuousGround(point.otherCollider, Owner.mainBody) || part == null || part.vessel != Owner || !Owner.parts.Contains(part)) continue;
                var key = new ColliderPair(point.thisCollider, point.otherCollider);
                TerrainContactSet set;
                if (!updates.TryGetValue(key, out set)) { set = new TerrainContactSet { ObservedRealtime = Time.realtimeSinceStartup }; updates.Add(key, set); }
                // Root-local coordinates stay small and survive a floating-origin
                // shift without rounding a 200–600 km body vector to float.
                set.Contacts.Add(new Contact { Position = (Vector3d)(rotation * (point.point - root.transform.position)), Normal = rotation * point.normal, PartId = part.persistentId, Collider = point.thisCollider });
            }
            if (TerrainContacts.Count + updates.Keys.Count(k => !TerrainContacts.ContainsKey(k)) > 64) { TerrainContacts.Clear(); return; }
            foreach (var pair in updates) TerrainContacts[pair.Key] = pair.Value;
            if (updates.Count == 0) RemovePairs(collision, count);
        }
    }

    internal static class ColonyPlacementGroundContacts
    {
        internal static void Attach(Vessel vessel, string operation)
        {
            foreach (var body in vessel.parts.Where(p => p.rb != null).Select(p => p.rb).Distinct())
            {
                var observer = body.gameObject.GetComponent<ColonyPlacementContactObserver>();
                if (observer == null) observer = body.gameObject.AddComponent<ColonyPlacementContactObserver>();
                if (observer.Owner != vessel || observer.Operation != operation) observer.TerrainContacts.Clear();
                observer.Owner = vessel; observer.Operation = operation;
            }
        }

        internal static bool ReadWitness(Vessel vessel, ColonyPlacementRequest request, out ConfigNode evidence, out string reason)
        {
            evidence = null; reason = null;
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.mainBody == null || vessel.rootPart == null) { reason = "Actual unpacked physical footing provider is unavailable"; return false; }
            var contacts = new List<ColonyPlacementContactObserver.Contact>();
            foreach (var rigidbody in vessel.parts.Where(p => p.rb != null).Select(p => p.rb).Distinct())
            {
                var observer = rigidbody.gameObject.GetComponent<ColonyPlacementContactObserver>();
                if (observer == null || observer.Owner != vessel || observer.Operation != request.OperationId) continue;
                // Unity stops collision-stay callbacks on a sleeping body. Its
                // native contacts remain admissible only while it still sleeps;
                // moving bodies require a current collision callback.
                foreach (var pair in observer.TerrainContacts)
                {
                    if (!observer.CurrentPair(pair.Key, pair.Value, rigidbody)) continue;
                    contacts.AddRange(pair.Value.Contacts);
                }
                if (contacts.Count > 4096) { reason = "Actual ground contact inventory exceeds 4096-point bound"; return false; }
            }
            if (contacts.Count < 3) { reason = "Waiting for at least three actual native terrain contact points"; return false; }
            var body = vessel.mainBody;
            var center = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, 0);
            var radial = (center - body.position).normalized;
            var rotation = vessel.rootPart.transform.rotation;
            var worldContacts = contacts.Select(c => new { Native = c, Point = (Vector3d)(rotation * (Vector3)c.Position) + (Vector3d)vessel.rootPart.transform.position, Normal = rotation * c.Normal }).ToArray();
            foreach (var contact in worldContacts)
            {
                var up = (contact.Point - body.position).normalized; RaycastHit surface; string terrainReason;
                var collider = contact.Native.Collider;
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    ((Vector3)contact.Point - collider.ClosestPoint((Vector3)contact.Point)).magnitude > Math.Max(.02, Physics.defaultContactOffset * 2) ||
                    !ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)(contact.Point + up * 2), (Vector3)(-up), 4, out surface, out terrainReason) ||
                    Math.Abs(Vector3d.Dot(contact.Point - (Vector3d)surface.point, up)) > Math.Max(.02, Physics.defaultContactOffset * 2))
                { reason = "Native contact no longer matches current loaded terrain within collision skin"; return false; }
            }
            var origin = worldContacts[0].Point;
            var normal = worldContacts.Aggregate(Vector3.zero, (sum, c) => sum + c.Normal).normalized;
            if (normal.sqrMagnitude < .9f || Vector3d.Dot(radial, normal) < .9) { reason = "Actual support normals do not form a qualified upward footing plane"; return false; }
            var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, 0) - center), normal).normalized;
            if (north.sqrMagnitude < .9f) { reason = "Actual support footprint frame is unavailable"; return false; }
            var frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, normal) * north, normal);
            var inverse = Quaternion.Inverse(frame);
            var projected = worldContacts.Select(c => inverse * (Vector3)(c.Point - origin)).ToArray();
            double step = projected.Max(p => p.y) - projected.Min(p => p.y);
            double slope = worldContacts.Max(c => Vector3.Angle(c.Normal, (Vector3)(c.Point - body.position).normalized));
            if (step > request.MaximumSupportGapMetres + .002) { reason = "Actual native support contact height variation exceeds declared support gap"; return false; }
            if (slope > request.MaximumSlopeDegrees + .05) { reason = "An independent actual native support normal exceeds declared terrain slope"; return false; }
            var hull = Hull(projected.Select(p => new Vector2(p.x, p.z)).ToArray());
            if (hull.Count < 3) { reason = "Actual native terrain contacts do not span a support polygon"; return false; }
            double area = 0;
            for (int i = 0; i < hull.Count; i++) area += (double)hull[i].x * hull[(i + 1) % hull.Count].y - (double)hull[i].y * hull[(i + 1) % hull.Count].x;
            area = Math.Abs(area) / 2;
            if (area < .01) { reason = "Actual native support polygon is too small to qualify building footing"; return false; }
            // Project the actual centre of mass along gravity, rather than along
            // the sloping plane normal, onto the measured contact plane.
            var centerOfMass = vessel.GetWorldPos3D();
            var gravityProjection = centerOfMass - radial * (Vector3d.Dot(centerOfMass - origin, normal) / Vector3d.Dot(radial, normal));
            var localMass = inverse * (Vector3)(gravityProjection - origin); var mass = new Vector2(localMass.x, localMass.z);
            double supportMargin = double.PositiveInfinity;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i]; var b = hull[(i + 1) % hull.Count];
                supportMargin = Math.Min(supportMargin, Cross(a, b, mass) / (b - a).magnitude);
                if (supportMargin < .02) { reason = "Actual gravity projection of centre of mass lacks 0.02 m margin inside native support contacts"; return false; }
            }
            evidence = new ConfigNode("COLONY_NATIVE_FOOTING_WITNESS");
            evidence.AddValue("operationId", request.OperationId); evidence.AddValue("requestFingerprint", request.Fingerprint()); evidence.AddValue("vesselId", vessel.id.ToString("D")); evidence.AddValue("body", body.bodyName);
            evidence.AddValue("observedUt", Write(Planetarium.GetUniversalTime())); evidence.AddValue("method", "Native Unity terrain collision contacts; independently measured normals/contact heights and gravity-projected centre of mass inside support polygon");
            evidence.AddValue("contactCount", contacts.Count); evidence.AddValue("supportPolygonAreaSquareMetres", Write(area)); evidence.AddValue("maximumIndependentSlopeDegrees", Write(slope)); evidence.AddValue("contactHeightVariationMetres", Write(step));
            evidence.AddValue("contactInventoryMethod", "Current native own-collider/terrain pairs; unidentified exits invalidate this observer's matching terrain pairs until native callbacks restore them");
            evidence.AddValue("minimumCentreOfMassSupportMarginMetres", ".02");
            evidence.AddValue("actualCentreOfMassSupportMarginMetres", Write(supportMargin));
            evidence.AddValue("projectedCentreOfMassX", Write(mass.x)); evidence.AddValue("projectedCentreOfMassZ", Write(mass.y));
            foreach (var contact in contacts.OrderBy(c => c.PartId).ThenBy(c => c.Position.x).ThenBy(c => c.Position.y).ThenBy(c => c.Position.z))
            {
                var node = evidence.AddNode("CONTACT"); node.AddValue("partPersistentId", contact.PartId);
                node.AddValue("rootLocalPosition", Write(contact.Position.x) + "," + Write(contact.Position.y) + "," + Write(contact.Position.z));
                node.AddValue("rootLocalNormal", Write(contact.Normal.x) + "," + Write(contact.Normal.y) + "," + Write(contact.Normal.z));
            }
            foreach (var point in hull) { var node = evidence.AddNode("SUPPORT_HULL"); node.AddValue("x", Write(point.x)); node.AddValue("z", Write(point.y)); }
            return true;
        }
        private static string Write(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static double Cross(Vector2 a, Vector2 b, Vector2 c) { return (double)(b.x - a.x) * (c.y - a.y) - (double)(b.y - a.y) * (c.x - a.x); }
        private static List<Vector2> Hull(Vector2[] input)
        {
            var points = input.OrderBy(p => p.x).ThenBy(p => p.y).ToArray(); var unique = new List<Vector2>();
            foreach (var point in points) if (unique.Count == 0 || (point - unique[unique.Count - 1]).sqrMagnitude > .000001f) unique.Add(point);
            if (unique.Count < 3) return unique;
            var lower = new List<Vector2>(); foreach (var point in unique) { while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(point); }
            var upper = new List<Vector2>(); for (int i = unique.Count - 1; i >= 0; i--) { var point = unique[i]; while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(point); }
            lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1); lower.AddRange(upper); return lower;
        }
    }
}
