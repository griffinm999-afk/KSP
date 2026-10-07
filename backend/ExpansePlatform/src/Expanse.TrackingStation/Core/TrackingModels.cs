using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.TrackingStation.Core
{
    public sealed class VesselSummary
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string BodyKey { get; private set; }
        public string Type { get; private set; }
        public string Situation { get; private set; }
        public int CrewCount { get; private set; }
        public bool CrewKnown { get; private set; }

        public VesselSummary(string id, string name, string bodyKey, string type, string situation, int crewCount)
            : this(id, name, bodyKey, type, situation, crewCount, true) { }

        public VesselSummary(string id, string name, string bodyKey, string type, string situation, int crewCount, bool crewKnown)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            BodyKey = bodyKey ?? string.Empty;
            Type = type ?? string.Empty;
            Situation = situation ?? string.Empty;
            CrewCount = Math.Max(0, crewCount);
            CrewKnown = crewKnown;
        }
    }

    public sealed class BodySummary
    {
        public string Key { get; private set; }
        public string Name { get; private set; }
        public string ParentKey { get; private set; }
        public bool IsStar { get; private set; }

        public BodySummary(string key, string name, string parentKey)
            : this(key, name, parentKey, false) { }

        public BodySummary(string key, string name, string parentKey, bool isStar)
        {
            Key = key ?? string.Empty;
            Name = name ?? string.Empty;
            ParentKey = parentKey ?? string.Empty;
            IsStar = isStar;
        }
    }

    public enum CrewFilter { Any, Crewed, Uncrewed }
    public enum TrackingSortField { Name, Type, Body, Situation, Crew }

    public sealed class TrackingView
    {
        public const string AllScope = "all";
        public string ScopeBodyKey { get; set; }
        public bool IncludeDescendants { get; set; }
        public string SearchText { get; set; }
        public HashSet<string> TypeFilters { get; private set; }
        public HashSet<string> SituationFilters { get; private set; }
        public CrewFilter Crew { get; set; }
        public bool ShowEmpty { get; set; }
        public TrackingSortField SortField { get; set; }
        public bool SortDescending { get; set; }
        public HashSet<string> ExpandedBodyKeys { get; private set; }

        // Short adapter-friendly alias; the persisted representation remains ScopeBodyKey.
        public string Scope { get { return ScopeBodyKey; } set { ScopeBodyKey = value; } }
        public CrewFilter CrewFilter { get { return Crew; } set { Crew = value; } }

        public TrackingView()
        {
            ScopeBodyKey = AllScope;
            SearchText = string.Empty;
            TypeFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SituationFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExpandedBodyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Crew = CrewFilter.Any;
            ShowEmpty = true;
            SortField = TrackingSortField.Name;
        }

        public bool IsAllScope { get { return string.IsNullOrEmpty(ScopeBodyKey) || string.Equals(ScopeBodyKey, AllScope, StringComparison.OrdinalIgnoreCase); } }
        public TrackingView Clone()
        {
            var copy = new TrackingView
            {
                ScopeBodyKey = ScopeBodyKey,
                IncludeDescendants = IncludeDescendants,
                SearchText = SearchText,
                Crew = Crew,
                ShowEmpty = ShowEmpty,
                SortField = SortField,
                SortDescending = SortDescending
            };
            foreach (var value in TypeFilters) copy.TypeFilters.Add(value);
            foreach (var value in SituationFilters) copy.SituationFilters.Add(value);
            foreach (var value in ExpandedBodyKeys) copy.ExpandedBodyKeys.Add(value);
            return copy;
        }
    }

    public sealed class SavedTrackingView
    {
        public bool GroupByBody { get; set; } = true;
        public string Name { get; set; }
        public string ScopeBodyKey { get; set; }
        public bool IncludeDescendants { get; set; }
        public string SearchText { get; set; }
        public string[] TypeFilters { get; set; }
        public string[] SituationFilters { get; set; }
        public CrewFilter Crew { get; set; }
        public bool ShowEmpty { get; set; }
        public TrackingSortField SortField { get; set; }
        public bool SortDescending { get; set; }
        public string[] ExpandedBodyKeys { get; set; }

        public TrackingView ToView()
        {
            var view = new TrackingView
            {
                ScopeBodyKey = ScopeBodyKey ?? TrackingView.AllScope,
                IncludeDescendants = IncludeDescendants,
                SearchText = SearchText ?? string.Empty,
                Crew = Crew,
                ShowEmpty = ShowEmpty,
                SortField = SortField,
                SortDescending = SortDescending
            };
            AddAll(view.TypeFilters, TypeFilters);
            AddAll(view.SituationFilters, SituationFilters);
            AddAll(view.ExpandedBodyKeys, ExpandedBodyKeys);
            return view;
        }

        public static SavedTrackingView FromView(string name, TrackingView view)
        {
            if (view == null) throw new ArgumentNullException("view");
            return new SavedTrackingView
            {
                Name = name ?? string.Empty,
                ScopeBodyKey = view.ScopeBodyKey,
                IncludeDescendants = view.IncludeDescendants,
                SearchText = view.SearchText,
                TypeFilters = view.TypeFilters.ToArray(),
                SituationFilters = view.SituationFilters.ToArray(),
                Crew = view.Crew,
                ShowEmpty = view.ShowEmpty,
                SortField = view.SortField,
                SortDescending = view.SortDescending,
                ExpandedBodyKeys = view.ExpandedBodyKeys.ToArray()
            };
        }

        private static void AddAll(HashSet<string> target, IEnumerable<string> values)
        {
            if (values == null) return;
            foreach (var value in values) if (!string.IsNullOrEmpty(value)) target.Add(value);
        }
    }
}
