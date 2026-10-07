using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.TrackingStation.Core
{
    public sealed class BodyNode
    {
        private readonly List<BodyNode> children = new List<BodyNode>();
        internal BodyNode(BodySummary summary, bool isUnknown)
        {
            Summary = summary;
            IsUnknown = isUnknown;
        }
        public BodySummary Summary { get; private set; }
        public bool IsUnknown { get; private set; }
        public BodyNode Parent { get; internal set; }
        public IReadOnlyList<BodyNode> Children { get { return children; } }
        internal void AddChild(BodyNode child) { children.Add(child); }
    }

    public sealed class BodyHierarchy
    {
        public const string UnknownBodyKey = "__unknown__";
        private readonly Dictionary<string, BodyNode> byKey;
        private readonly List<BodyNode> roots;

        private BodyHierarchy(Dictionary<string, BodyNode> byKey, List<BodyNode> roots)
        {
            this.byKey = byKey;
            this.roots = roots;
        }

        public IReadOnlyDictionary<string, BodyNode> Nodes { get { return byKey; } }
        public IReadOnlyList<BodyNode> Roots { get { return roots; } }
        public BodyNode Unknown { get; private set; }

        public static BodyHierarchy Build(IEnumerable<BodySummary> summaries)
        {
            var map = new Dictionary<string, BodyNode>(StringComparer.OrdinalIgnoreCase);
            var unknown = new BodyNode(new BodySummary(UnknownBodyKey, "Unknown", string.Empty), true);
            map[UnknownBodyKey] = unknown;
            if (summaries != null)
            {
                foreach (var summary in summaries)
                {
                    if (summary == null || string.IsNullOrEmpty(summary.Key)) continue;
                    var key = summary.Key;
                    if (map.ContainsKey(key))
                    {
                        var suffix = 2;
                        var disambiguated = key + "#" + suffix;
                        while (map.ContainsKey(disambiguated)) disambiguated = key + "#" + (++suffix);
                        key = disambiguated;
                        var disambiguatedSummary = new BodySummary(key, summary.Name, summary.ParentKey, summary.IsStar);
                        map.Add(key, new BodyNode(disambiguatedSummary, false));
                        continue;
                    }
                    map.Add(key, new BodyNode(summary, false));
                }
            }

            var candidates = new Dictionary<BodyNode, BodyNode>();
            foreach (var node in map.Values.Where(n => !n.IsUnknown))
            {
                BodyNode parent;
                if (string.IsNullOrEmpty(node.Summary.ParentKey))
                    candidates[node] = null;
                else if (string.Equals(node.Summary.ParentKey, node.Summary.Key, StringComparison.OrdinalIgnoreCase))
                    candidates[node] = null;
                else
                    candidates[node] = map.TryGetValue(node.Summary.ParentKey, out parent) ? parent : unknown;
            }

            // Break every directed cycle at the cycle members. This keeps all nodes visible
            // while ensuring all ancestry and descendant traversals terminate.
            var cycleMembers = new HashSet<BodyNode>();
            foreach (var node in candidates.Keys.ToList())
            {
                var seen = new Dictionary<BodyNode, int>();
                var path = new List<BodyNode>();
                var current = node;
                while (current != null && !current.IsUnknown)
                {
                    int at;
                    if (seen.TryGetValue(current, out at))
                    {
                        for (var i = at; i < path.Count; i++) cycleMembers.Add(path[i]);
                        break;
                    }
                    seen[current] = path.Count;
                    path.Add(current);
                    current = candidates[current];
                }
            }

            foreach (var node in map.Values) node.Parent = null;
            foreach (var node in map.Values.Where(n => !n.IsUnknown))
            {
                BodyNode parent = cycleMembers.Contains(node) ? unknown : candidates[node];
                node.Parent = parent;
                if (parent != null) parent.AddChild(node);
            }

            var rootList = new List<BodyNode> { unknown };
            rootList.AddRange(map.Values.Where(n => !n.IsUnknown && n.Parent == null));
            // Unknown owns malformed bodies through its child list; direct roots remain top-level roots.
            rootList = rootList.GroupBy(n => n.Summary.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            return new BodyHierarchy(map, rootList) { Unknown = unknown };
        }

        public bool Contains(string key)
        {
            return !string.IsNullOrEmpty(key) && byKey.ContainsKey(key);
        }

        public BodyNode Get(string key)
        {
            BodyNode node;
            return key != null && byKey.TryGetValue(key, out node) ? node : null;
        }

        public IReadOnlyList<string> AncestorKeys(string key)
        {
            var result = new List<string>();
            var node = Get(key);
            var seen = new HashSet<BodyNode>();
            while (node != null && seen.Add(node))
            {
                result.Add(node.Summary.Key);
                node = node.Parent;
            }
            return result;
        }

        public bool IsDescendantOrSelf(string key, string ancestorKey)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(ancestorKey)) return false;
            var node = Get(key);
            var ancestor = Get(ancestorKey);
            if (node == null || ancestor == null) return false;
            var seen = new HashSet<BodyNode>();
            while (node != null && seen.Add(node))
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = node.Parent;
            }
            return false;
        }

        public IEnumerable<BodyNode> Traverse()
        {
            var seen = new HashSet<BodyNode>();
            foreach (var root in roots)
                foreach (var node in Traverse(root, seen)) yield return node;
            foreach (var node in byKey.Values)
                if (seen.Add(node)) yield return node;
        }

        private static IEnumerable<BodyNode> Traverse(BodyNode node, HashSet<BodyNode> seen)
        {
            if (node == null || !seen.Add(node)) yield break;
            yield return node;
            foreach (var child in node.Children)
                foreach (var nested in Traverse(child, seen)) yield return nested;
        }
    }
}
