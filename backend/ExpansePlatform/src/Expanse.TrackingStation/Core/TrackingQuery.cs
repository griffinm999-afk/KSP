using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Expanse.TrackingStation.Core
{
    public sealed class BodyBranchResult
    {
        internal BodyBranchResult(BodyNode body) { Body = body; Children = new List<BodyBranchResult>(); Vessels = new List<VesselSummary>(); }
        public BodyNode Body { get; private set; }
        public int MatchCount { get; internal set; }
        public int TotalCount { get; internal set; }
        public bool IsVisible { get; internal set; }
        public bool IsExpanded { get; internal set; }
        public IReadOnlyList<BodyBranchResult> Children { get; private set; }
        public IReadOnlyList<VesselSummary> Vessels { get; private set; }
        internal List<BodyBranchResult> MutableChildren { get { return (List<BodyBranchResult>)Children; } }
        internal List<VesselSummary> MutableVessels { get { return (List<VesselSummary>)Vessels; } }
    }

    public sealed class TrackingQueryResult
    {
        public IReadOnlyList<VesselSummary> Vessels { get; internal set; }
        public IReadOnlyList<BodyBranchResult> Branches { get; internal set; }
        public IReadOnlyCollection<string> ExpandedBodyKeys { get; internal set; }
        public IReadOnlyCollection<string> AutoExpandedBodyKeys { get; internal set; }
        public IReadOnlyCollection<string> VisibleBodyKeys { get; internal set; }
        public int TotalInScope { get; internal set; }
        public int MatchCount { get; internal set; }
        public int CurrentScopeCount { get { return TotalInScope; } }
        public int FilteredCount { get { return MatchCount; } }
    }

    public static class TrackingQuery
    {
        private sealed class IndexedVessel
        {
            public VesselSummary Vessel;
            public BodyNode Body;
            public string[] AncestorNames;
            public int Order;
        }

        public static TrackingQueryResult Execute(IEnumerable<VesselSummary> source, BodyHierarchy hierarchy, TrackingView view)
        {
            if (hierarchy == null) throw new ArgumentNullException("hierarchy");
            view = view ?? new TrackingView();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexed = new List<IndexedVessel>();
            var input = source ?? Enumerable.Empty<VesselSummary>();
            var order = 0;
            foreach (var vessel in input)
            {
                if (vessel == null || !unique.Add(vessel.Id)) continue;
                var body = hierarchy.Get(vessel.BodyKey) ?? hierarchy.Unknown;
                var ancestry = hierarchy.AncestorKeys(body.Summary.Key)
                    .Select(k => hierarchy.Get(k)).Where(n => n != null)
                    .SelectMany(n => new[] { n.Summary.Name, n.Summary.Key }).ToArray();
                indexed.Add(new IndexedVessel { Vessel = vessel, Body = body, AncestorNames = ancestry, Order = order++ });
            }

            var scoped = indexed.Where(x => InScope(x.Body, hierarchy, view)).ToList();
            var matched = scoped.Where(x => Matches(x, view)).ToList();
            var allMatching = indexed.Where(x => Matches(x, view)).ToList();
            var sorted = matched.OrderBy(x => x, new IndexedComparer(view.SortField, view.SortDescending)).Select(x => x.Vessel).ToList();
            var branchMap = hierarchy.Traverse().ToDictionary(n => n.Summary.Key, n => new BodyBranchResult(n), StringComparer.OrdinalIgnoreCase);
            foreach (var item in indexed)
            {
                var cursor = item.Body;
                var seen = new HashSet<BodyNode>();
                while (cursor != null && seen.Add(cursor))
                {
                    branchMap[cursor.Summary.Key].TotalCount++;
                    cursor = cursor.Parent;
                }
            }
            foreach (var item in allMatching)
            {
                branchMap[item.Body.Summary.Key].MutableVessels.Add(item.Vessel);
                var cursor = item.Body;
                var seen = new HashSet<BodyNode>();
                while (cursor != null && seen.Add(cursor))
                {
                    branchMap[cursor.Summary.Key].MatchCount++;
                    cursor = cursor.Parent;
                }
            }
            foreach (var pair in branchMap)
            {
                var node = pair.Value.Body;
                foreach (var child in node.Children)
                    pair.Value.MutableChildren.Add(branchMap[child.Summary.Key]);
                pair.Value.MutableVessels.Sort(new VesselComparer(view.SortField, view.SortDescending, hierarchy));
            }

            var expanded = new HashSet<string>(view.ExpandedBodyKeys ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
            var autoExpanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Tokenize(view.SearchText).Length > 0)
                foreach (var item in matched)
                    foreach (var key in hierarchy.AncestorKeys(item.Body.Summary.Key)) autoExpanded.Add(key);
            expanded.UnionWith(autoExpanded);

            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var branch in branchMap.Values)
            {
                branch.IsExpanded = expanded.Contains(branch.Body.Summary.Key);
                branch.IsVisible = view.ShowEmpty || branch.MatchCount > 0 || IsSelectedScopePath(branch.Body, hierarchy, view);
                if (branch.IsVisible) visible.Add(branch.Body.Summary.Key);
            }
            foreach (var branch in branchMap.Values)
                branch.MutableChildren.RemoveAll(child => !child.IsVisible);

            var roots = hierarchy.Roots.Select(r => branchMap[r.Summary.Key])
                .Where(r => r.IsVisible).ToList();
            return new TrackingQueryResult
            {
                Vessels = sorted,
                Branches = roots,
                ExpandedBodyKeys = expanded,
                AutoExpandedBodyKeys = autoExpanded,
                VisibleBodyKeys = visible,
                TotalInScope = scoped.Count,
                MatchCount = matched.Count
            };
        }

        private static bool IsRoot(BodyNode node) { return node != null && (node.IsUnknown || node.Parent == null); }

        private static bool IsSelectedScopePath(BodyNode branch, BodyHierarchy hierarchy, TrackingView view)
        {
            if (view.IsAllScope || branch == null || !hierarchy.Contains(view.ScopeBodyKey)) return false;
            return hierarchy.IsDescendantOrSelf(view.ScopeBodyKey, branch.Summary.Key);
        }

        private static bool InScope(BodyNode body, BodyHierarchy hierarchy, TrackingView view)
        {
            if (view.IsAllScope) return true;
            if (!hierarchy.Contains(view.ScopeBodyKey)) return true;
            return view.IncludeDescendants
                ? hierarchy.IsDescendantOrSelf(body.Summary.Key, view.ScopeBodyKey)
                : string.Equals(body.Summary.Key, view.ScopeBodyKey, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(IndexedVessel item, TrackingView view)
        {
            if (!AnyFilter(view.TypeFilters, item.Vessel.Type) || !AnyFilter(view.SituationFilters, item.Vessel.Situation)) return false;
            if (view.Crew == CrewFilter.Crewed && (!item.Vessel.CrewKnown || item.Vessel.CrewCount <= 0)) return false;
            if (view.Crew == CrewFilter.Uncrewed && (!item.Vessel.CrewKnown || item.Vessel.CrewCount != 0)) return false;
            var tokens = Tokenize(view.SearchText);
            if (tokens.Length == 0) return true;
            var haystack = new List<string> { item.Vessel.Name };
            haystack.AddRange(item.AncestorNames);
            var text = string.Join(" ", haystack).ToUpperInvariant();
            return tokens.All(token => text.IndexOf(token.ToUpperInvariant(), StringComparison.Ordinal) >= 0);
        }

        private static bool AnyFilter(ISet<string> filters, string value)
        {
            return filters == null || filters.Count == 0 || filters.Any(f => string.Equals(f, value, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] Tokenize(string text)
        {
            return (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private sealed class IndexedComparer : IComparer<IndexedVessel>
        {
            private readonly TrackingSortField field; private readonly bool descending;
            public IndexedComparer(TrackingSortField field, bool descending) { this.field = field; this.descending = descending; }
            public int Compare(IndexedVessel x, IndexedVessel y)
            {
                if (field == TrackingSortField.Crew && x.Vessel.CrewKnown != y.Vessel.CrewKnown)
                {
                    var knownResult = x.Vessel.CrewKnown ? -1 : 1;
                    return descending ? -knownResult : knownResult;
                }
                var result = NaturalComparer.Instance.Compare(Field(x), Field(y));
                if (descending) result = -result;
                if (result != 0) return result;
                result = NaturalComparer.Instance.Compare(x.Vessel.Name, y.Vessel.Name);
                if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(x.Vessel.Name, y.Vessel.Name);
                return result != 0 ? result : NaturalComparer.Instance.Compare(x.Vessel.Id, y.Vessel.Id);
            }
            private string Field(IndexedVessel x)
            {
                switch (field)
                {
                    case TrackingSortField.Type: return x.Vessel.Type;
                    case TrackingSortField.Body: return x.Body.Summary.Name;
                    case TrackingSortField.Situation: return x.Vessel.Situation;
                    case TrackingSortField.Crew: return x.Vessel.CrewCount.ToString(CultureInfo.InvariantCulture);
                    default: return x.Vessel.Name;
                }
            }
        }

        private sealed class VesselComparer : IComparer<VesselSummary>
        {
            private readonly TrackingSortField field; private readonly bool descending; private readonly BodyHierarchy hierarchy;
            public VesselComparer(TrackingSortField field, bool descending, BodyHierarchy hierarchy) { this.field = field; this.descending = descending; this.hierarchy = hierarchy; }
            public int Compare(VesselSummary x, VesselSummary y)
            {
                if (field == TrackingSortField.Crew && x.CrewKnown != y.CrewKnown)
                {
                    var knownResult = x.CrewKnown ? -1 : 1;
                    return descending ? -knownResult : knownResult;
                }
                var xv = field == TrackingSortField.Body ? hierarchy.Get(x.BodyKey) : null;
                var yv = field == TrackingSortField.Body ? hierarchy.Get(y.BodyKey) : null;
                var a = field == TrackingSortField.Body ? (xv == null ? string.Empty : xv.Summary.Name) : Value(x);
                var b = field == TrackingSortField.Body ? (yv == null ? string.Empty : yv.Summary.Name) : Value(y);
                var result = NaturalComparer.Instance.Compare(a, b);
                if (descending) result = -result;
                if (result != 0) return result;
                result = NaturalComparer.Instance.Compare(x.Name, y.Name);
                if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
                return result != 0 ? result : NaturalComparer.Instance.Compare(x.Id, y.Id);
            }
            private string Value(VesselSummary v)
            {
                if (field == TrackingSortField.Type) return v.Type;
                if (field == TrackingSortField.Situation) return v.Situation;
                if (field == TrackingSortField.Crew) return v.CrewCount.ToString(CultureInfo.InvariantCulture);
                return v.Name;
            }
        }

        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();
            public int Compare(string left, string right)
            {
                left = left ?? string.Empty; right = right ?? string.Empty;
                var i = 0; var j = 0;
                while (i < left.Length && j < right.Length)
                {
                    if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                    {
                        var si = i; while (i < left.Length && char.IsDigit(left[i])) i++;
                        var sj = j; while (j < right.Length && char.IsDigit(right[j])) j++;
                        var ai = left.Substring(si, i - si).TrimStart('0'); var bj = right.Substring(sj, j - sj).TrimStart('0');
                        if (ai.Length != bj.Length) return ai.Length.CompareTo(bj.Length);
                        var numeric = string.Compare(ai, bj, StringComparison.OrdinalIgnoreCase);
                        if (numeric != 0) return numeric;
                    }
                    else
                    {
                        var c = char.ToUpperInvariant(left[i]).CompareTo(char.ToUpperInvariant(right[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return (left.Length - i).CompareTo(right.Length - j);
            }
        }
    }
}
