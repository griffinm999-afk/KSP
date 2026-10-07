using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.TrackingStation.Core
{
    public sealed class TrackingStationState
    {
        private readonly Dictionary<string, SavedTrackingView> savedViews = new Dictionary<string, SavedTrackingView>(StringComparer.OrdinalIgnoreCase);
        private List<VesselSummary> vessels;

        public TrackingStationState(IEnumerable<BodySummary> bodies, IEnumerable<VesselSummary> vessels)
        {
            View = new TrackingView();
            Update(bodies, vessels);
        }

        public BodyHierarchy Hierarchy { get; private set; }
        public IReadOnlyList<VesselSummary> Vessels { get { return vessels; } }
        public TrackingView View { get; private set; }
        public string SelectedVesselId { get; private set; }
        public IReadOnlyDictionary<string, SavedTrackingView> SavedViews { get { return savedViews; } }

        public void SelectVessel(string vesselId)
        {
            SelectedVesselId = vessels.Any(v => string.Equals(v.Id, vesselId, StringComparison.OrdinalIgnoreCase)) ? vesselId : null;
        }

        public void Update(IEnumerable<BodySummary> bodies, IEnumerable<VesselSummary> nextVessels)
        {
            Hierarchy = BodyHierarchy.Build(bodies ?? Enumerable.Empty<BodySummary>());
            vessels = Deduplicate(nextVessels);
            ReconcileView();
            if (SelectedVesselId != null && !vessels.Any(v => string.Equals(v.Id, SelectedVesselId, StringComparison.OrdinalIgnoreCase)))
                SelectedVesselId = null;
        }

        public void SetView(TrackingView view)
        {
            View = (view ?? new TrackingView()).Clone();
            ReconcileView();
        }

        public TrackingQueryResult Query()
        {
            return TrackingQuery.Execute(vessels, Hierarchy, View);
        }

        public void SaveView(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A view name is required.", "name");
            savedViews[name.Trim()] = SavedTrackingView.FromView(name.Trim(), View);
        }

        public bool LoadView(string name)
        {
            SavedTrackingView saved;
            if (string.IsNullOrWhiteSpace(name) || !savedViews.TryGetValue(name.Trim(), out saved)) return false;
            View = saved.ToView();
            ReconcileView();
            return true;
        }

        public bool DeleteView(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && savedViews.Remove(name.Trim());
        }

        private void ReconcileView()
        {
            if (View == null) View = new TrackingView();
            if (!View.IsAllScope && !Hierarchy.Contains(View.ScopeBodyKey)) View.ScopeBodyKey = TrackingView.AllScope;
            View.ExpandedBodyKeys.RemoveWhere(key => !Hierarchy.Contains(key));
        }

        private static List<VesselSummary> Deduplicate(IEnumerable<VesselSummary> source)
        {
            var result = new List<VesselSummary>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return result;
            foreach (var vessel in source)
                if (vessel != null && ids.Add(vessel.Id)) result.Add(vessel);
            return result;
        }
    }
}
