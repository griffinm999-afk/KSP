using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Expanse.TrackingStation.Core;
using UnityEngine;

namespace Expanse.TrackingStation.Adapter
{
    /// <summary>Owns only browser metadata under PluginData; it never touches KSP saves.</summary>
    public sealed class TrackingPreferences
    {
        private const int Schema = 1;
        private const int MaxViews = 24;
        private const char Separator = '\u001f';
        public TrackingView View { get; private set; }
        public List<SavedTrackingView> NamedViews { get; set; }
        public float Scale { get; set; }
        public bool GroupByBody { get; set; }
        public bool FiltersCollapsed { get; set; }
        public bool Loaded { get; private set; }
        public bool IsFresh { get; private set; }
        public bool FocusFollowsSelection { get; set; }
        public string Diagnostic { get; private set; }
        private Rect windowRect;

        public Rect WindowRect { get { return windowRect; } set { windowRect = value; } }

        public TrackingPreferences()
        {
            View = new TrackingView { ShowEmpty = false };
            NamedViews = new List<SavedTrackingView>();
            NamedViews.Add(SavedTrackingView.FromView("All vessels", View));
            var stations = new TrackingView { ShowEmpty = false }; stations.TypeFilters.Add("Station");
            NamedViews.Add(SavedTrackingView.FromView("Stations", stations));
            var facilities = new TrackingView { ShowEmpty = false }; facilities.TypeFilters.Add("Base"); facilities.TypeFilters.Add("Rover"); facilities.SituationFilters.Add("LANDED"); facilities.SituationFilters.Add("SPLASHED");
            NamedViews.Add(SavedTrackingView.FromView("Colony facilities (types)", facilities));
            windowRect = new Rect(48, 48, 940, 680);
            Scale = 1f;
            GroupByBody = true;
        }

        public static string GetPath(string saveFolder)
        {
            if (string.IsNullOrEmpty(saveFolder)) return null;
            try
            {
                var identity = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath ?? string.Empty, "saves", saveFolder));
                using (var sha = SHA256.Create())
                {
                    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()));
                    var hash = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
                    return Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "ExpanseTrackingStation", "PluginData", "view-" + hash + ".cfg");
                }
            }
            catch { return null; }
        }

        public void Load(string saveFolder)
        {
            Loaded = false; IsFresh = false; Diagnostic = string.Empty;
            var path = GetPath(saveFolder);
            if (path == null || !File.Exists(path)) { Loaded = true; IsFresh = true; return; }
            try
            {
                var node = ConfigNode.Load(path);
                if (node == null) throw new InvalidDataException("configuration could not be parsed");
                var root = node.HasNode("TRACKING_STATION") ? node.GetNode("TRACKING_STATION") : node;
                int schema;
                if (!int.TryParse(root.GetValue("schema"), NumberStyles.Integer, CultureInfo.InvariantCulture, out schema) || schema != Schema)
                    throw new InvalidDataException("unsupported preferences schema");
                View = ReadView(root.GetNode("VIEW")) ?? new TrackingView();
                GroupByBody = ReadBool(root, "groupByBody", true);
                FiltersCollapsed = ReadBool(root, "filtersCollapsed", false);
                FocusFollowsSelection = ReadBool(root, "focusFollowsSelection", false);
                NamedViews.Clear();
                var nodes = root.GetNodes("NAMED_VIEW");
                if (nodes != null) foreach (var child in nodes.Take(MaxViews)) { var read = ReadSaved(child); if (read != null && !string.IsNullOrEmpty(read.Name)) NamedViews.Add(read); }
                windowRect = new Rect(ReadFloat(root, "x", 48), ReadFloat(root, "y", 48), ReadFloat(root, "width", 940), ReadFloat(root, "height", 680));
                Scale = Mathf.Clamp(ReadFloat(root, "scale", 1), .75f, 2);
                ClampGeometry();
                Loaded = true;
            }
            catch (Exception ex) { Diagnostic = "Preferences were not loaded: " + ex.Message; /* preserve corrupt file */ }
        }

        public bool Save(string saveFolder)
        {
            if (!Loaded) return false;
            var path = GetPath(saveFolder);
            if (path == null) return false;
            try
            {
                ClampGeometry();
                var root = new ConfigNode("TRACKING_STATION");
                root.AddValue("schema", Schema.ToString(CultureInfo.InvariantCulture));
                root.AddValue("x", windowRect.x.ToString("R", CultureInfo.InvariantCulture)); root.AddValue("y", windowRect.y.ToString("R", CultureInfo.InvariantCulture));
                root.AddValue("width", windowRect.width.ToString("R", CultureInfo.InvariantCulture)); root.AddValue("height", windowRect.height.ToString("R", CultureInfo.InvariantCulture));
            root.AddValue("scale", Scale.ToString("R", CultureInfo.InvariantCulture)); root.AddValue("groupByBody", GroupByBody);
                root.AddValue("focusFollowsSelection", FocusFollowsSelection);
                root.AddValue("filtersCollapsed", FiltersCollapsed);
                WriteView(root.AddNode("VIEW"), View);
                foreach (var named in NamedViews.Take(MaxViews)) WriteSaved(root.AddNode("NAMED_VIEW"), named);
                var directory = Path.GetDirectoryName(path); if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                var temporary = path + ".tmp";
                root.Save(temporary);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                return true;
            }
            catch (Exception ex) { Diagnostic = "Preferences were not saved: " + ex.Message; return false; }
        }

        public void SetView(TrackingView view)
        {
            if (view == null) return;
            View = view.Clone();
        }

        public void UpdateFromWindow(TrackingView view, Rect rect, float scale, bool grouped)
        {
            SetView(view); WindowRect = rect; Scale = scale; GroupByBody = grouped;
        }

        public void ClampGeometry()
        {
            Scale = Mathf.Clamp(Scale, .75f, 2f);
            var rect = windowRect;
            rect.width = Mathf.Clamp(rect.width, 420f, 1600f); rect.height = Mathf.Clamp(rect.height, 320f, 1200f);
            rect.x = Mathf.Max(0, rect.x); rect.y = Mathf.Max(0, rect.y); windowRect = rect;
        }

        private static void WriteView(ConfigNode node, TrackingView view)
        {
            if (node == null || view == null) return;
            node.AddValue("scope", view.ScopeBodyKey); node.AddValue("descendants", view.IncludeDescendants); node.AddValue("search", view.SearchText);
            node.AddValue("crew", view.Crew); node.AddValue("showEmpty", view.ShowEmpty);
            node.AddValue("sort", view.SortField); node.AddValue("descending", view.SortDescending);
            foreach (var type in view.TypeFilters.Take(256)) node.AddValue("type", type);
            foreach (var situation in view.SituationFilters.Take(256)) node.AddValue("situation", situation);
            foreach (var expanded in view.ExpandedBodyKeys.Take(256)) node.AddValue("expanded", expanded);
        }

        private static void WriteSaved(ConfigNode node, SavedTrackingView view)
        {
            if (node == null || view == null) return;
            node.AddValue("name", view.Name); node.AddValue("groupByBody", view.GroupByBody); WriteView(node, view.ToView());
        }

        private static TrackingView ReadView(ConfigNode node)
        {
            if (node == null) return null;
            var search = Value(node, "search", string.Empty); if (search.Length > 256) search = search.Substring(0, 256);
            var view = new TrackingView { ScopeBodyKey = Value(node, "scope", TrackingView.AllScope), SearchText = search, IncludeDescendants = ReadBool(node, "descendants", false), ShowEmpty = ReadBool(node, "showEmpty", true), SortDescending = ReadBool(node, "descending", false) };
            CrewFilter crew; if (Enum.TryParse(Value(node, "crew", "Any"), true, out crew) && Enum.IsDefined(typeof(CrewFilter), crew)) view.Crew = crew;
            TrackingSortField sort; if (Enum.TryParse(Value(node, "sort", "Name"), true, out sort) && Enum.IsDefined(typeof(TrackingSortField), sort)) view.SortField = sort;
            Add(view.TypeFilters, node.GetValues("type")); Add(view.SituationFilters, node.GetValues("situation")); Add(view.ExpandedBodyKeys, node.GetValues("expanded"));
            return view;
        }

        private static SavedTrackingView ReadSaved(ConfigNode node)
        {
            var view = ReadView(node); if (view == null) return null;
            var saved = SavedTrackingView.FromView(Value(node, "name", string.Empty), view);
            saved.GroupByBody = ReadBool(node, "groupByBody", true);
            return saved;
        }
        private static void Add(ISet<string> target, IEnumerable<string> values) { if (values == null) return; foreach (var value in values.Where(x => !string.IsNullOrEmpty(x)).Take(256)) target.Add(value.Length > 256 ? value.Substring(0, 256) : value); }
        private static string Value(ConfigNode n, string key, string fallback) { var value = n == null ? null : n.GetValue(key); return value ?? fallback; }
        private static bool ReadBool(ConfigNode n, string key, bool fallback) { bool value; return bool.TryParse(Value(n, key, fallback.ToString()), out value) ? value : fallback; }
        private static float ReadFloat(ConfigNode n, string key, float fallback) { float value; return float.TryParse(Value(n, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value) ? value : fallback; }
    }
}
