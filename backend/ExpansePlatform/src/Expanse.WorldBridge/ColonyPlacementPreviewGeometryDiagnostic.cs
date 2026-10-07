using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // Observation only. Called once before the unchanged strict preview checks.
    // No registration, assembly, collider changes, paid-state writes or physics tuning.
    internal static class ColonyPlacementPreviewGeometryDiagnostic
    {
        private const int MaximumColliders = 512, MaximumParts = 256, MaximumRequests = 128;
        private static readonly HashSet<string> observed = new HashSet<string>(StringComparer.Ordinal);
        private static bool requestBoundReported;
        internal static void Observe(Collider[] colliders, Quaternion inverseFrame, ColonyPlacementRequest request)
        {
            try
            {
                string fingerprint = request.Fingerprint();
                string key = request.OperationId + "/" + fingerprint;
                if (observed.Contains(key)) return;
                if (observed.Count >= MaximumRequests)
                { if (!requestBoundReported) { requestBoundReported = true; Debug.LogWarning("[ExpanseColonyPreviewGeometry] request observation bound reached; no qualification claimed"); } return; }
                observed.Add(key);
                if (colliders == null || colliders.Length == 0 || colliders.Length > MaximumColliders)
                { Debug.LogWarning("[ExpanseColonyPreviewGeometry] op=" + Safe(request.OperationId, 40) + "; collider inventory absent/exceeds512; no complete geometry claimed"); return; }
                var rows = colliders.Select(c => new Row { Collider = c, Part = c == null ? null : c.GetComponentInParent<Part>(), Path = Path(c) })
                    .OrderBy(r => r.Part == null ? 0 : r.Part.craftID).ThenBy(r => r.Path, StringComparer.Ordinal).ToArray();
                var parts = rows.Select(r => r.Part).Distinct().ToArray();
                if (parts.Length > MaximumParts)
                { Debug.LogWarning("[ExpanseColonyPreviewGeometry] op=" + Safe(request.OperationId, 40) + "; part inventory exceeds256; no complete geometry claimed"); return; }
                string prefix = "[ExpanseColonyPreviewGeometry] op=" + Safe(request.OperationId, 40);
                Debug.Log(prefix + "; fingerprint=" + Safe(fingerprint, 64) + "; craftSha=" + Safe(request.TemplateSha256, 64) +
                    "; body=" + Safe(request.BodyName, 80) + "; lat=" + R(request.Latitude) + "; lon=" + R(request.Longitude) + "; heading=" + R(request.HeadingDegrees) +
                    "; colliders=" + rows.Length + "; parts=" + parts.Length + "; frame=root-relative/template-oriented/inverse-surface; certified x=[" + R(request.MinX) + "," + R(request.MaxX) +
                    "],z=[" + R(request.MinZ) + "," + R(request.MaxZ) + "],height=" + R(request.MaximumHeight) + "; preview only; deployment unqualified");
                bool complete = parts.All(p => p != null && p.craftID != 0) && parts.Select(p => p == null ? 0 : p.craftID).Distinct().Count() == parts.Length;
                Vector3 minimum = Positive(), maximum = Negative();
                var perPart = new Dictionary<Part, Extents>();
                int ordinal = 0;
                foreach (var row in rows)
                {
                    Vector3 min, max; string reason;
                    bool valid = ColonyPlacementColliderGeometry.TryBounds(row.Collider, Vector3.zero, inverseFrame, out min, out max, out reason);
                    valid = valid && Finite(min) && Finite(max) && min.x <= max.x && min.y <= max.y && min.z <= max.z;
                    string identity = "; ordinal=" + ordinal++ + "; craft=" + (row.Part == null ? "unknown" : row.Part.craftID.ToString(CultureInfo.InvariantCulture)) +
                        "; part=" + Safe(row.Part == null || row.Part.partInfo == null ? "unknown" : row.Part.partInfo.name, 48) +
                        "; collider=" + Safe(row.Collider == null ? "null" : row.Collider.GetType().Name, 40) + "; path=" + row.Path;
                    if (!valid) { complete = false; Debug.LogWarning(prefix + identity + "; bounds invalid; reason=" + Safe(reason, 160)); continue; }
                    minimum = Vector3.Min(minimum, min); maximum = Vector3.Max(maximum, max);
                    if (row.Part == null) complete = false;
                    else
                    {
                        Extents part;
                        if (!perPart.TryGetValue(row.Part, out part)) perPart.Add(row.Part, part = new Extents());
                        part.Minimum = Vector3.Min(part.Minimum, min); part.Maximum = Vector3.Max(part.Maximum, max); part.Count++;
                    }
                    Debug.Log(prefix + identity + "; surfaceMin=" + V(min) + "; surfaceMax=" + V(max));
                }
                foreach (var row in perPart.OrderBy(p => p.Key.craftID))
                    Debug.Log(prefix + "; partUnion craft=" + row.Key.craftID.ToString(CultureInfo.InvariantCulture) + "; colliders=" + row.Value.Count +
                        "; surfaceMin=" + V(row.Value.Minimum) + "; surfaceMax=" + V(row.Value.Maximum));
                bool finite = Finite(minimum) && Finite(maximum);
                Debug.Log(prefix + "; fullUnion complete=" + (complete && finite) + "; surfaceMin=" + (finite ? V(minimum) : "unavailable") +
                    "; surfaceMax=" + (finite ? V(maximum) : "unavailable") + "; previewHeight=" + (finite ? R(maximum.y - minimum.y) : "unavailable") +
                    "; strict validation continues unchanged; deployment unqualified");
            }
            catch (Exception ex)
            {
                // A diagnostic failure cannot authorize or alter the placement path.
                Debug.LogWarning("[ExpanseColonyPreviewGeometry] observation unavailable: " + Safe(ex.GetType().Name + ": " + ex.Message, 240) + "; no qualification claimed");
            }
        }
        private sealed class Row { internal Collider Collider; internal Part Part; internal string Path; }
        private sealed class Extents { internal Vector3 Minimum = Positive(), Maximum = Negative(); internal int Count; }
        private static Vector3 Positive() { return new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity); }
        private static Vector3 Negative() { return new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity); }
        private static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string V(Vector3 value) { return "(" + R(value.x) + "," + R(value.y) + "," + R(value.z) + ")"; }
        private static string Safe(string value, int limit)
        { value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '); return value.Length <= limit ? value : value.Substring(0, limit); }
        private static string Path(Collider collider)
        {
            if (collider == null) return "null";
            var part = collider.GetComponentInParent<Part>(); var labels = new List<string>(); var cursor = collider.transform;
            int depth = 0;
            for (; cursor != null && (part == null || cursor != part.transform) && depth < 16; depth++, cursor = cursor.parent)
                labels.Add(Safe(cursor.name, 32) + "[" + cursor.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + "]");
            if (depth == 16 && cursor != null && (part == null || cursor != part.transform)) labels.Add("ancestor-truncated");
            labels.Reverse();
            var peers = collider.GetComponents<Collider>(); int component = Array.IndexOf(peers, collider);
            return Safe(string.Join("/", labels.ToArray()), 240) + "#component=" + component.ToString(CultureInfo.InvariantCulture);
        }
    }
}
