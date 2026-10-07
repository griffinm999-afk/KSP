using Expanse.TrackingStation.Core;
using System.Diagnostics;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            HierarchyIsCycleSafeAndPreservesMultipleRoots();
            DuplicateBodiesAreDisambiguatedAndRenderedOnce();
            QueryAppliesAndOrScopeCountsAndNaturalSort();
            EverySortFieldReversesWithStableSecondaryNames();
            CanonicalSearchAndDeepStarAncestryWork();
            SearchRevealDoesNotMutateOrdinaryExpansion();
            StateReconcilesRemovedBodiesSelectionAndNamedViews();
            LargeFleetQueryRemainsOfflineAndUnique();
            FullShellGeometryFitsPhysicalScreens(args.Contains("--layout-report"));
            Console.WriteLine("Expanse.TrackingStation.Tests: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Expanse.TrackingStation.Tests: FAIL");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void HierarchyIsCycleSafeAndPreservesMultipleRoots()
    {
        var hierarchy = BodyHierarchy.Build(new[]
        {
            new BodySummary("Alpha", "Alpha", "", true),
            new BodySummary("AlphaMoon", "Alpha Moon", "Alpha"),
            new BodySummary("Beta", "Beta", "", true),
            new BodySummary("MissingChild", "Missing Child", "NoSuchBody"),
            new BodySummary("CycleA", "Cycle A", "CycleB"),
            new BodySummary("CycleB", "Cycle B", "CycleA"),
            new BodySummary("Self", "Self", "Self")
        });

        Check(hierarchy.Roots.Any(x => x.Summary.Key == "Alpha"), "Alpha is a root");
        Check(hierarchy.Roots.Any(x => x.Summary.Key == "Beta"), "Beta is a root");
        Check(hierarchy.Unknown.Children.Any(x => x.Summary.Key == "MissingChild"), "missing parent is unknown");
        Check(hierarchy.Unknown.Children.Any(x => x.Summary.Key == "CycleA"), "cycle A is unknown");
        Check(hierarchy.Unknown.Children.Any(x => x.Summary.Key == "CycleB"), "cycle B is unknown");
        Check(hierarchy.Roots.Any(x => x.Summary.Key == "Self"), "self parent is treated as a root");
        Check(hierarchy.AncestorKeys("CycleA").Count < 4, "cycle ancestry terminates");
        Check(hierarchy.IsDescendantOrSelf("AlphaMoon", "Alpha"), "descendant scope works");
    }

    private static void QueryAppliesAndOrScopeCountsAndNaturalSort()
    {
        var bodies = TestBodies();
        var vessels = TestVessels();
        var hierarchy = BodyHierarchy.Build(bodies);
        var view = new TrackingView { ShowEmpty = false };
        view.TypeFilters.Add("Probe");
        view.TypeFilters.Add("Station");
        view.SituationFilters.Add("Landed");
        view.SituationFilters.Add("Docked");
        view.SituationFilters.Add("Orbiting");
        var result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.TotalInScope == 5, "duplicate GUID is removed from scope count");
        Check(result.MatchCount == 3, "type and situation filters combine as AND across dimensions");
        Check(result.Vessels.Select(v => v.Id).SequenceEqual(new[] { "v1", "v2", "v3" }), "stable natural name ordering");

        view.TypeFilters.Clear();
        view.SituationFilters.Clear();
        view.ScopeBodyKey = "Alpha";
        view.IncludeDescendants = false;
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.TotalInScope == 2 && result.MatchCount == 2, "exact body scope");
        Check(result.Branches.Single(x => x.Body.Summary.Key == "Beta").TotalCount == 1, "branch totals keep global denominator outside scope");
        view.IncludeDescendants = true;
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.TotalInScope == 3 && result.MatchCount == 3, "descendant body scope");
        var alpha = result.Branches.Single(x => x.Body.Summary.Key == "Alpha");
        Check(alpha.TotalCount == 3 && alpha.MatchCount == 3, "branch total and match counts");

        view.ScopeBodyKey = TrackingView.AllScope;
        view.ShowEmpty = false;
        view.TypeFilters.Add("Station");
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(!result.VisibleBodyKeys.Contains("Beta"), "empty branch hidden");
        view.ShowEmpty = true;
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.VisibleBodyKeys.Contains("Beta"), "show empty reveals all branches");

        view.TypeFilters.Clear();
        view.Crew = CrewFilter.Uncrewed;
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.MatchCount == 2 && !result.Vessels.Any(v => v.Id == "v5"), "unknown crew is neither uncrewed nor crewed");
        view.Crew = CrewFilter.Crewed;
        result = TrackingQuery.Execute(vessels, hierarchy, view);
        Check(result.MatchCount == 2, "crewed filter");

        view.Crew = CrewFilter.Any;
        view.ScopeBodyKey = "EmptyBody";
        view.ShowEmpty = false;
        result = TrackingQuery.Execute(vessels, BodyHierarchy.Build(bodies.Concat(new[] { new BodySummary("EmptyBody", "Empty", "Alpha") })), view);
        Check(result.TotalInScope == 0 && result.Branches.Any(x => x.Body.Summary.Key == "Alpha"), "empty selected scope remains recoverable");
    }

    private static void SearchRevealDoesNotMutateOrdinaryExpansion()
    {
        var hierarchy = BodyHierarchy.Build(TestBodies());
        var view = new TrackingView { SearchText = "alpha moon hopper", ShowEmpty = false };
        view.ExpandedBodyKeys.Add("Beta");
        var before = view.ExpandedBodyKeys.ToArray();
        var result = TrackingQuery.Execute(TestVessels(), hierarchy, view);
        Check(result.MatchCount == 1 && result.Vessels[0].Id == "v3", "multiword search matches vessel and body fields");
        Check(result.AutoExpandedBodyKeys.Contains("Alpha"), "search reveals ancestor");
        Check(view.ExpandedBodyKeys.SequenceEqual(before), "search overlay leaves base expansion unchanged");
        view.SearchText = string.Empty;
        result = TrackingQuery.Execute(TestVessels(), hierarchy, view);
        Check(result.AutoExpandedBodyKeys.Count == 0, "clearing search restores reveal overlay");
        Check(result.ExpandedBodyKeys.Count == before.Length && before.All(result.ExpandedBodyKeys.Contains), "clearing search restores ordinary expansion");
    }

    private static void EverySortFieldReversesWithStableSecondaryNames()
    {
        var hierarchy = BodyHierarchy.Build(new[] { new BodySummary("red", "Red", ""), new BodySummary("blue", "Blue", "") });
        var fleet = new[] {
            new VesselSummary("a", "Hopper 10", "red", "Probe", "Orbiting", 0),
            new VesselSummary("b", "Hopper 2", "blue", "Ship", "Landed", 2),
            new VesselSummary("c", "Alpha", "blue", "Probe", "Landed", 0, false)
        };
        var expected = new Dictionary<TrackingSortField, string[]> {
            { TrackingSortField.Name, new[] { "c,b,a", "a,b,c" } },
            { TrackingSortField.Type, new[] { "c,a,b", "b,c,a" } },
            { TrackingSortField.Body, new[] { "c,b,a", "a,c,b" } },
            { TrackingSortField.Situation, new[] { "c,b,a", "a,c,b" } },
            { TrackingSortField.Crew, new[] { "a,b,c", "c,b,a" } }
        };
        foreach (var pair in expected)
            for (var direction = 0; direction < 2; direction++)
            {
                var view = new TrackingView { SortField = pair.Key, SortDescending = direction == 1 };
                var result = TrackingQuery.Execute(fleet.Reverse(), hierarchy, view);
                Check(string.Join(",", result.Vessels.Select(v => v.Id)) == pair.Value[direction], pair.Key + " sort direction and stable name ties");
            }
        var longDigits = new[] {
            new VesselSummary("huge", "Hopper 999999999999999999999999999999999999999", "red", "Probe", "Orbiting", 0),
            new VesselSummary("short", "Hopper 20", "red", "Probe", "Orbiting", 0)
        };
        Check(TrackingQuery.Execute(longDigits, hierarchy, new TrackingView()).Vessels[0].Id == "short", "numeric runs cannot overflow");
    }

    private static void CanonicalSearchAndDeepStarAncestryWork()
    {
        var hierarchy = BodyHierarchy.Build(new[] {
            new BodySummary("starA", "Alpha", "", true),
            new BodySummary("starB", "Beta", "starA", true),
            new BodySummary("planet", "Localized Planet", "starB"),
            new BodySummary("moon", "Localized Moon", "planet"),
            new BodySummary("submoon", "Localized Submoon", "moon")
        });
        var fleet = new[] { new VesselSummary("deep", "Research 2", "submoon", "Probe", "Orbiting", 0) };
        var result = TrackingQuery.Execute(fleet, hierarchy, new TrackingView { SearchText = "STARb submoon research", ScopeBodyKey = "starB", IncludeDescendants = true });
        Check(result.MatchCount == 1, "canonical keys and multiple tokens search deep ancestry");
        Check(result.AutoExpandedBodyKeys.Contains("starA") && result.AutoExpandedBodyKeys.Contains("moon"), "nested star/deep moon reveal all ancestors");
    }

    private static void StateReconcilesRemovedBodiesSelectionAndNamedViews()
    {
        var state = new TrackingStationState(TestBodies(), TestVessels());
        state.View.ScopeBodyKey = "AlphaMoon";
        state.View.ExpandedBodyKeys.Add("AlphaMoon");
        state.SelectVessel("v3");
        state.SaveView("moon");
        state.Update(new[] { new BodySummary("Alpha", "Alpha", "", true) }, new[] { TestVessels()[0] });
        Check(state.View.IsAllScope, "stale scope falls back to all");
        Check(state.View.ExpandedBodyKeys.Count == 0, "stale expanded bodies are removed");
        Check(state.SelectedVesselId == null, "stale selected vessel is cleared");
        Check(state.LoadView("moon"), "named view can be loaded");
        Check(state.View.IsAllScope, "loaded named view is reconciled against current bodies");
        Check(state.DeleteView("moon"), "named view deletes");
    }

    private static void LargeFleetQueryRemainsOfflineAndUnique()
    {
        var bodies = new[] { new BodySummary("Root", "Root", "") };
        var fleet = Enumerable.Range(0, 10000).Select(i =>
            new VesselSummary("fleet-" + i, "Hopper " + i, "Root", i % 2 == 0 ? "Probe" : "Station", "Orbiting", i % 3)).ToList();
        fleet.Add(fleet[123]);
        var stopwatch = Stopwatch.StartNew();
        var result = TrackingQuery.Execute(fleet, BodyHierarchy.Build(bodies), new TrackingView());
        stopwatch.Stop();
        Check(result.TotalInScope == 10000 && result.MatchCount == 10000 && result.Vessels.Count == 10000, "10000 fleet query deduplicates GUIDs");
        Check(result.Vessels[2].Name == "Hopper 2" && result.Vessels[10].Name == "Hopper 10", "natural numeric ordering scales without integer parsing");
        Console.WriteLine("10,000-vessel query: " + stopwatch.ElapsedMilliseconds + " ms");
    }

    private static void DuplicateBodiesAreDisambiguatedAndRenderedOnce()
    {
        var hierarchy = BodyHierarchy.Build(new[]
        {
            new BodySummary("Twin", "Twin A", ""),
            new BodySummary("Twin", "Twin B", ""),
            new BodySummary("Broken", "Broken", "Missing")
        });
        Check(hierarchy.Nodes.ContainsKey("Twin") && hierarchy.Nodes.ContainsKey("Twin#2"), "duplicate body keys are deterministic");
        Check(hierarchy.Traverse().Count(n => n.Summary.Key == "Broken") == 1, "malformed branch traverses once");
    }

    private static BodySummary[] TestBodies()
    {
        return new[]
        {
            new BodySummary("Alpha", "Alpha", "", true),
            new BodySummary("AlphaMoon", "Alpha Moon", "Alpha"),
            new BodySummary("Beta", "Beta", "", true)
        };
    }

    private static VesselSummary[] TestVessels()
    {
        return new[]
        {
            new VesselSummary("v1", "Hopper 2", "Alpha", "Probe", "Landed", 0),
            new VesselSummary("v2", "Hopper 10", "Alpha", "Probe", "Orbiting", 2),
            new VesselSummary("v3", "Moon Hopper", "AlphaMoon", "Station", "Docked", 3),
            new VesselSummary("v4", "Debris 1", "Beta", "Debris", "Orbiting", 0),
            new VesselSummary("v5", "Unknown Crew", "NoBody", "Ship", "Orbiting", 0, false),
            new VesselSummary("v2", "Duplicate", "Beta", "Probe", "Landed", 5)
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void FullShellGeometryFitsPhysicalScreens(bool report)
    {
        var cases = new[] { new[] {3840f,2160f,2f,1f}, new[] {3840f,2160f,2f,.75f}, new[] {3840f,2160f,2f,1.5f}, new[] {2560f,1440f,2f,1f}, new[] {1920f,1080f,1f,1f}, new[] {1280f,720f,2f,1f}, new[] {640f,480f,2f,1f} };
        var metrics = new List<object>();
        foreach (var c in cases)
        {
            var l=TrackingShellLayout.Create(c[0],c[1],c[2],c[3]);
            Check(Math.Abs(l.SidebarWidth/l.Width-.28f)<.0001f,"full-height browser occupies 28 percent of screen");
            Check(l.TreeBottom-l.TreeTop>=2*l.RowHeight,"controls leave at least two virtualized tree rows");
            Check(l.CardLeft>=l.SidebarWidth && l.CardLeft+l.CardWidth<=l.Width-l.Padding+.01f,"selection card stays in native map region");
            Check(l.CardTop>=l.HeaderHeight && l.CardTop+l.CardHeight<l.Height-l.FooterHeight,"selection card and actions stay above footer");
            Check(l.InnerWidth>=170,"search and compact dropdown controls remain reachable");
            if(c[0]==3840 && c[3]==1){Check(l.Width==1920 && l.Height==1080 && l.FontSize*l.Scale==28,"game UI scale 2 produces 28px controls without UI_SCALE_APPS multiplication");}
            var expandedTop=l.TreeTop;
            l.FiltersCollapsed=true;
            Check(expandedTop-l.TreeTop>=130,"collapsing filters returns their space to the fleet tree");
            Check(l.TreeTop==l.HeaderHeight+l.Padding+l.TitleHeight+l.Gap,"collapsed tree begins immediately below Vessels heading");
            l.FiltersCollapsed=false;
            metrics.Add(new { screenWidth=c[0],screenHeight=c[1],gameUiScale=c[2],pluginMultiplier=c[3],layout=l,physicalFontSize=l.FontSize*l.Scale,visibleTreeRows=(int)((l.TreeBottom-l.TreeTop)/l.RowHeight) });
        }
        if(report)Console.WriteLine("LAYOUT_JSON:"+System.Text.Json.JsonSerializer.Serialize(metrics,new System.Text.Json.JsonSerializerOptions{IncludeFields=true}));
    }
}
