using System;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // Read native collider geometry in the requested surface frame. A world AABB
    // is already expanded by world-axis rotation and must not be rotated again.
    // Mesh local bounds remain a conservative complete-mesh enclosure; box and
    // native sphere/capsule support intervals use their actual shape parameters.
    internal static class ColonyPlacementColliderGeometry
    {
        internal static bool TryBounds(Collider collider, Vector3 origin, Quaternion inverseFrame, out Vector3 minimum, out Vector3 maximum, out string reason)
        {
            minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            reason = "";
            float rotationNorm = Quaternion.Dot(inverseFrame, inverseFrame);
            if (collider == null || !Finite(origin) || !Finite(inverseFrame.x) || !Finite(inverseFrame.y) || !Finite(inverseFrame.z) || !Finite(inverseFrame.w) || !Finite(rotationNorm) || Math.Abs(rotationNorm - 1) > .001)
            { reason = "Collider or finite normalized surface frame missing"; return false; }
            var box = collider as BoxCollider;
            var mesh = collider as MeshCollider;
            if (box != null || mesh != null)
            {
                Bounds local;
                if (box != null) local = new Bounds(box.center, box.size);
                else
                {
                    if (mesh.sharedMesh == null) { reason = "Native mesh collider has no shared mesh"; return false; }
                    local = mesh.sharedMesh.bounds;
                }
                if (!Finite(local.center) || !Finite(local.extents) || local.extents.x < 0 || local.extents.y < 0 || local.extents.z < 0)
                { reason = "Native local collider bounds are invalid"; return false; }
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 point = inverseFrame * (collider.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z))) - origin);
                    if (!Finite(point)) { reason = "Native collider transform is not finite"; return false; }
                    minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
                }
                return ValidBounds(minimum, maximum, out reason);
            }
            // Native analytic shape scale cannot safely be reconstructed from
            // lossyScale when nonuniform ancestor transforms introduce shear.
            // Reject that hierarchy rather than understate its physical extent.
            Matrix4x4 matrix = collider.transform.localToWorldMatrix;
            Vector3 a = new Vector3(matrix.m00, matrix.m10, matrix.m20), b = new Vector3(matrix.m01, matrix.m11, matrix.m21), c = new Vector3(matrix.m02, matrix.m12, matrix.m22);
            if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(a.sqrMagnitude) || !Finite(b.sqrMagnitude) || !Finite(c.sqrMagnitude) || a.sqrMagnitude <= 0 || b.sqrMagnitude <= 0 || c.sqrMagnitude <= 0 ||
                Math.Abs(Vector3.Dot(a.normalized, b.normalized)) > .00001 || Math.Abs(Vector3.Dot(a.normalized, c.normalized)) > .00001 || Math.Abs(Vector3.Dot(b.normalized, c.normalized)) > .00001)
            { reason = "Native analytic collider hierarchy is sheared or singular; geometry unqualified"; return false; }
            var scale = collider.transform.lossyScale;
            scale = new Vector3(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
            if (!Finite(scale) || scale.x <= 0 || scale.y <= 0 || scale.z <= 0) { reason = "Native analytic collider scale invalid"; return false; }
            var sphere = collider as SphereCollider;
            if (sphere != null)
            {
                float radius = sphere.radius * Math.Max(scale.x, Math.Max(scale.y, scale.z));
                Vector3 center = inverseFrame * (sphere.transform.TransformPoint(sphere.center) - origin);
                if (!Finite(center) || !Finite(radius) || radius < 0) { reason = "Native sphere geometry invalid"; return false; }
                minimum = center - Vector3.one * radius; maximum = center + Vector3.one * radius; return ValidBounds(minimum, maximum, out reason);
            }
            var capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                int direction = capsule.direction;
                if (direction < 0 || direction > 2) { reason = "Native capsule axis invalid"; return false; }
                float axial = direction == 0 ? scale.x : direction == 1 ? scale.y : scale.z;
                float transverse = direction == 0 ? Math.Max(scale.y, scale.z) : direction == 1 ? Math.Max(scale.x, scale.z) : Math.Max(scale.x, scale.y);
                float radius = capsule.radius * transverse;
                float halfSegment = Math.Max(0, capsule.height * axial / 2 - radius);
                Vector3 axis = direction == 0 ? Vector3.right : direction == 1 ? Vector3.up : Vector3.forward;
                Vector3 center = inverseFrame * (capsule.transform.TransformPoint(capsule.center) - origin);
                axis = inverseFrame * capsule.transform.TransformDirection(axis).normalized;
                if (!Finite(center) || !Finite(axis) || !Finite(radius) || !Finite(halfSegment) || radius < 0) { reason = "Native capsule geometry invalid"; return false; }
                Vector3 extent = new Vector3(Math.Abs(axis.x), Math.Abs(axis.y), Math.Abs(axis.z)) * halfSegment + Vector3.one * radius;
                minimum = center - extent; maximum = center + extent; return ValidBounds(minimum, maximum, out reason);
            }
            reason = "Unsupported native collider geometry: " + collider.GetType().FullName;
            return false;
        }

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidBounds(Vector3 minimum, Vector3 maximum, out string reason)
        {
            bool valid = Finite(minimum) && Finite(maximum) && maximum.x >= minimum.x && maximum.y >= minimum.y && maximum.z >= minimum.z;
            reason = valid ? "" : "Transformed native collider bounds are invalid";
            return valid;
        }
    }
}
