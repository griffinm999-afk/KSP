using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Expanse.TrackingStation.Core;
using Expanse.TrackingStation.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace Expanse.TrackingStation.Adapter
{
    /// <summary>Default Tracking Station presentation owner; native map and action backend remain alive.</summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    [DefaultExecutionOrder(1000)]
    public sealed class TrackingStationAddon : MonoBehaviour
    {
        private const string LockId = "EXPANSE_TRACKING_STATION_UI";
        private const float RefreshPeriod = 1f;
        private const float SaveDebounce = 2.5f;
        private TrackingWindow window;
        private TrackingPreferences preferences;
        private ApplicationLauncherButton launcherButton;
        private Texture2D launcherIcon;
        private BodyHierarchy hierarchy;
        private KspTrackingSnapshot snapshot;
        private string dataFingerprint;
        private string bodyFingerprint;
        private string saveFolder;
        private float nextRefresh;
        private float dirtySince = -1f;
        private string savedFingerprint;
        private bool applicationFocused = true;
        private bool dataWasUnavailable;
        private NativeTrackingInterface native;
        private bool customRequested = true, seedExpansion;
        private int startupFrame;

        private void Start()
        {
            window = new TrackingWindow();
            window.Visible = false;
            native = new NativeTrackingInterface();
            startupFrame = Time.frameCount;
            window.VesselSelected += OnVesselSelected;
            window.FocusRequested += OnFocus;
            window.FlyRequested += OnFly;
            window.BodyFocusRequested += OnBodyFocus;
            window.NativeActionRequested += OnNativeAction;
            window.LeaveRequested += OnLeave;
            window.StockViewRequested += ShowStock;
            preferences = new TrackingPreferences();
            GameEvents.onGUIApplicationLauncherReady.Add(AddLauncherButton);
            if (ApplicationLauncher.Ready) AddLauncherButton();
            LoadPreferencesIfReady();
            nextRefresh = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            LoadPreferencesIfReady();
            if (window == null) return;
            if (customRequested && !window.Visible && Time.frameCount > startupFrame) OpenWindow();
            if (window.Visible && Time.realtimeSinceStartup >= nextRefresh)
            {
                nextRefresh = Time.realtimeSinceStartup + RefreshPeriod;
                RefreshSnapshot();
            }
            MaintainInputLock();
            TrackPreferenceChanges();
        }

        private void OnGUI()
        {
            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION || window == null) return;
            if (launcherButton == null && !window.Visible)
            {
                var old = GUI.color;
                GUI.color = new Color(.08f, .12f, .17f, .95f);
                if (window.DrawFallbackToggle(new Rect(10, 10, 190, 34), "Expanse tracking")) ToggleWindow();
                GUI.color = old;
            }
            if (window.Visible)
            {
                window.ActionsAvailable = !OtherTrackingLock();
                try { window.OnGUI(); }
                catch (Exception ex) { HideWindow(); Debug.LogError("[ExpanseTrackingStation] Native interface restored after UI failure: " + ex); }
                MaintainInputLock();
            }
        }

        private void LateUpdate()
        {
            if (window == null || !window.Visible || native == null) return;
            string diagnostic;
            if (!native.TryHide(out diagnostic)) { HideWindow(); window.SetStatus(diagnostic); return; }
            native.SetMapViewport(window.MapViewportNormalized);
        }

        private void AddLauncherButton()
        {
            if (launcherButton != null || ApplicationLauncher.Instance == null) return;
            launcherIcon = MakeIcon();
            launcherButton = ApplicationLauncher.Instance.AddModApplication(
                OpenFromLauncher, CloseFromLauncher, HoverLauncher, HoverOutLauncher,
                delegate { }, delegate { CloseFromLauncher(); },
                ApplicationLauncher.AppScenes.TRACKSTATION, launcherIcon);
        }

        private void OpenFromLauncher() { OpenWindow(); }
        private void CloseFromLauncher() { HideWindow(); }
        private void HoverLauncher() { ScreenMessages.PostScreenMessage("Expanse Tracking Station", 1.5f, ScreenMessageStyle.UPPER_CENTER); }
        private void HoverOutLauncher() { }

        private void ToggleWindow() { if (window.Visible) HideWindow(); else OpenWindow(); }
        private void OpenWindow()
        {
            customRequested = true;
            string diagnostic;
            if (native == null || !native.TryHide(out diagnostic)) return;
            window.Visible = true;
            RefreshSnapshot();
        }
        private void HideWindow()
        {
            customRequested = false;
            window.Visible = false;
            if (native != null) native.Restore();
            if (launcherButton != null) launcherButton.SetFalse(false);
            ReleaseInput();
            MarkPreferencesDirty();
        }
        private void ShowStock() { HideWindow(); }

        private void RefreshSnapshot()
        {
            var current = KspSnapshotAdapter.Capture();
            if (current == null || !current.IsAvailable)
            {
                dataWasUnavailable = true;
                if (window != null) window.SetStatus((snapshot == null ? "" : "Stale view: ") + (current == null ? "Tracking data unavailable." : current.Status));
                return;
            }
            var nextBodyFingerprint = FingerprintBodies(current.Bodies);
            var bodiesChanged = !string.Equals(bodyFingerprint, nextBodyFingerprint, StringComparison.Ordinal);
            if (bodiesChanged)
            {
                hierarchy = BodyHierarchy.Build(current.Bodies);
                bodyFingerprint = nextBodyFingerprint;
                if (seedExpansion && current.Bodies.Length > 0)
                {
                    foreach (var body in hierarchy.Traverse()) if (!body.IsUnknown && (body.Parent == null || body.Summary.IsStar)) window.View.ExpandedBodyKeys.Add(body.Summary.Key);
                    seedExpansion = false;
                }
            }
            var nextDataFingerprint = FingerprintData(current);
            snapshot = current;
            if (bodiesChanged || !string.Equals(dataFingerprint, nextDataFingerprint, StringComparison.Ordinal))
            {
                dataFingerprint = nextDataFingerprint;
                window.SetSnapshot(hierarchy, current.Vessels);
            }
            if (hierarchy != null && !window.View.IsAllScope && !hierarchy.Contains(window.View.ScopeBodyKey))
            {
                window.View.ScopeBodyKey = TrackingView.AllScope;
                window.Invalidate();
            }
            if (!string.IsNullOrEmpty(preferences.Diagnostic)) window.SetStatus(preferences.Diagnostic);
            else if (dataWasUnavailable) window.SetStatus("Tracking data refreshed.");
            dataWasUnavailable = false;
        }

        private void MaintainInputLock()
        {
            if (!applicationFocused || window == null || !window.Visible) { ReleaseInput(); return; }
            var mouse = Event.current == null ? new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) : Event.current.mousePosition;
            var inside = window.ContainsInput(mouse) || window.IsInputFocused;
            if (inside) InputLockManager.SetControlLock(ControlTypes.TRACKINGSTATION_UI | ControlTypes.MAP_UI | ControlTypes.CAMERACONTROLS, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        private void OnVesselSelected(string id) { }
        private static bool OtherTrackingLock()
        {
            try { return InputLockManager.lockStack.Any(pair => pair.Key != LockId && (pair.Value & (ulong)ControlTypes.TRACKINGSTATION_UI) != 0); }
            catch { return true; }
        }
        private void OnBodyFocus(string key)
        {
            if(OtherTrackingLock()){window.SetStatus("Stock controls are locked; finish the stock dialog first.");return;}
            ReleaseInput(); window.SetStatus(TrackingActions.FocusBody(KspSnapshotAdapter.ResolveBody(key)).Message);
        }
        private void OnNativeAction(string id,string action)
        {
            ReleaseInput(); var result=TrackingActions.InvokeNativeAction(id,action,LockId);window.SetStatus(result.Message);
            if(!result.Succeeded && result.Message.StartsWith("Stock action failed:",StringComparison.Ordinal)) HideWindow();
        }
        private void OnLeave()
        {
            ReleaseInput();var result=TrackingActions.LeaveTrackingStation(LockId);window.SetStatus(result.Message);
            if(result.Succeeded)HideWindow();
        }
        private void OnFocus(string id)
        {
            if(OtherTrackingLock()){window.SetStatus("Stock controls are locked; finish the stock dialog first.");return;}
            ReleaseInput();
            var result = TrackingActions.FocusVessel(id);
            window.SetStatus(result.Message);
        }
        private void OnFly(string id)
        {
            ReleaseInput();
            var result = TrackingActions.FlyVessel(id, LockId);
            window.SetStatus(result.Message);
            if(!result.Succeeded && result.Message.StartsWith("Stock Fly failed:",StringComparison.Ordinal)) HideWindow();
        }
        private void ReleaseInput() { InputLockManager.RemoveControlLock(LockId); }
        private void OnApplicationFocus(bool focused) { applicationFocused = focused; if (!focused) ReleaseInput(); }
        private void OnDisable() { ReleaseInput(); if(native!=null)native.Restore();if(window!=null)window.Visible=false; }

        private void LoadPreferencesIfReady()
        {
            var currentFolder = HighLogic.SaveFolder;
            if (string.IsNullOrEmpty(currentFolder) || preferences == null) return;
            if (string.Equals(saveFolder, currentFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrEmpty(saveFolder)) SavePreferences();
            saveFolder = currentFolder;
            preferences = new TrackingPreferences();
            preferences.Load(saveFolder);
            seedExpansion = preferences.IsFresh;
            window.ApplyView(preferences.View);
            window.GroupByBody = preferences.GroupByBody;
            window.FiltersCollapsed = preferences.FiltersCollapsed;
            window.NamedViews = preferences.NamedViews;
            window.WindowRect = preferences.WindowRect;
            window.Scale = preferences.Scale;
            window.FocusFollowsSelection = preferences.FocusFollowsSelection;
            snapshot = null; dataFingerprint = null; bodyFingerprint = null;
            window.SetSnapshot(BodyHierarchy.Build(new BodySummary[0]), new VesselSummary[0]);
            dataWasUnavailable = false;
            if (!string.IsNullOrEmpty(preferences.Diagnostic)) window.SetStatus(preferences.Diagnostic);
            savedFingerprint = PreferenceFingerprint();
            dirtySince = -1f;
        }

        private void TrackPreferenceChanges()
        {
            if (preferences == null || !preferences.Loaded || string.IsNullOrEmpty(saveFolder)) return;
            var current = PreferenceFingerprint();
            if (!string.Equals(savedFingerprint, current, StringComparison.Ordinal))
            {
                if (dirtySince < 0) dirtySince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - dirtySince >= SaveDebounce) SavePreferences();
            }
        }
        private void MarkPreferencesDirty() { if (dirtySince < 0) dirtySince = Time.realtimeSinceStartup; }
        private void SavePreferences()
        {
            if (preferences == null || !preferences.Loaded || string.IsNullOrEmpty(saveFolder)) return;
            preferences.NamedViews = window.NamedViews ?? new List<SavedTrackingView>();
            preferences.UpdateFromWindow(window.SnapshotView(), window.WindowRect, window.Scale, window.GroupByBody);
            preferences.FocusFollowsSelection = window.FocusFollowsSelection;
            preferences.FiltersCollapsed = window.FiltersCollapsed;
            if (preferences.Save(saveFolder)) { savedFingerprint = PreferenceFingerprint(); dirtySince = -1f; }
            else if (!string.IsNullOrEmpty(preferences.Diagnostic)) window.SetStatus(preferences.Diagnostic);
        }
        private string PreferenceFingerprint()
        {
            var view = window.SnapshotView();
            var b = new StringBuilder().Append(view.ScopeBodyKey).Append('|').Append(view.IncludeDescendants).Append('|').Append(view.SearchText).Append('|').Append(view.Crew).Append('|').Append(view.ShowEmpty).Append('|').Append(view.SortField).Append('|').Append(view.SortDescending).Append('|').Append(window.GroupByBody).Append('|').Append(window.Scale).Append('|').Append(window.FocusFollowsSelection).Append(window.FiltersCollapsed);
            foreach (var x in view.TypeFilters.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) b.Append('|').Append(x);
            foreach (var x in view.SituationFilters.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) b.Append('|').Append(x);
            foreach (var x in view.ExpandedBodyKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) b.Append('|').Append(x);
            foreach (var named in window.NamedViews ?? new List<SavedTrackingView>())
            {
                b.Append('|').Append(named.Name).Append('|').Append(named.ScopeBodyKey).Append('|').Append(named.IncludeDescendants).Append('|').Append(named.SearchText).Append('|').Append(named.Crew).Append('|').Append(named.ShowEmpty).Append('|').Append(named.SortField).Append('|').Append(named.SortDescending).Append('|').Append(named.GroupByBody);
                foreach (var item in named.TypeFilters ?? new string[0]) b.Append("|type:").Append(item);
                foreach (var item in named.SituationFilters ?? new string[0]) b.Append("|situation:").Append(item);
                foreach (var item in named.ExpandedBodyKeys ?? new string[0]) b.Append("|expanded:").Append(item);
            }
            return b.ToString();
        }
        private static string FingerprintBodies(IEnumerable<BodySummary> bodies) { return string.Join(";", (bodies ?? Enumerable.Empty<BodySummary>()).Select(x => x.Key + "|" + x.ParentKey + "|" + x.Name + "|" + x.IsStar).OrderBy(x => x, StringComparer.Ordinal)); }
        private static string FingerprintData(KspTrackingSnapshot value) { return string.Join(";", value.Vessels.Select(x => x.Id + "|" + x.Name + "|" + x.BodyKey + "|" + x.Type + "|" + x.Situation + "|" + x.CrewCount + "|" + x.CrewKnown).OrderBy(x => x, StringComparer.Ordinal)); }

        private void OnDestroy()
        {
            SavePreferences();
            ReleaseInput();
            if(native!=null)native.Dispose();
            if(window!=null){window.BodyFocusRequested-=OnBodyFocus;window.NativeActionRequested-=OnNativeAction;window.LeaveRequested-=OnLeave;window.Dispose();}
            if (window != null) { window.VesselSelected -= OnVesselSelected; window.FocusRequested -= OnFocus; window.FlyRequested -= OnFly; window.StockViewRequested -= ShowStock; }
            GameEvents.onGUIApplicationLauncherReady.Remove(AddLauncherButton);
            if (launcherButton != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(launcherButton);
            if (launcherIcon != null) Destroy(launcherIcon);
        }

        private static Texture2D MakeIcon()
        {
            var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            var pixels = new Color32[32 * 32];
            for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
            {
                var edge = x < 2 || y < 2 || x > 29 || y > 29;
                var ring = (x - 16) * (x - 16) + (y - 16) * (y - 16) < 115;
                pixels[y * 32 + x] = edge ? new Color32(185, 215, 232, 255) : ring ? new Color32(48, 147, 184, 255) : new Color32(22, 35, 49, 255);
            }
            texture.SetPixels32(pixels); texture.Apply(false, true); return texture;
        }
    }
}
