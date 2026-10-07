using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens;
using KSP.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Expanse.TrackingStation.Adapter
{
    /// <summary>
    /// Reversible presentation handoff. Native objects and button eligibility remain alive.
    /// Only alpha/raycast presentation and the map-camera viewport belong to this adapter.
    /// </summary>
    public sealed class NativeTrackingInterface : IDisposable
    {
        private sealed class GroupState
        {
            public CanvasGroup Group;
            public float Alpha;
            public bool BlocksRaycasts;
            public bool Added;
        }

        private sealed class CameraState
        {
            public Camera Camera;
            public Rect Rect;
        }

        private sealed class CanvasState
        {
            public Canvas Canvas;
            public bool Enabled;
        }

        private sealed class BehaviourState
        {
            public Behaviour Behaviour;
            public bool Enabled;
        }

        private readonly List<GroupState> groups = new List<GroupState>();
        private readonly List<CameraState> cameras = new List<CameraState>();
        private readonly List<CanvasState> legacyCanvases = new List<CanvasState>();
        private readonly List<BehaviourState> legacyDisplays = new List<BehaviourState>();
        private static readonly string[] LegacyDisplayNames = {
            "KerbalEngineer.TrackingStation.DisplayStackTS",
            "KerbalEngineer.TrackingStation.SectionEditorTS"
        };
        private float nextDisplayScan;
        private static readonly FieldInfo SidebarField = typeof(SpaceTracking).GetField("sideBarScrollRect", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo[] MapFxFields = new[] { "mapFxCameraNear", "mapFxCameraFar" }
            .Select(name => typeof(MapView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)).Where(field => field != null).ToArray();
        private SpaceTracking owner;
        private MapViewFiltering mapFilters;
        private bool disposed;
        public bool IsHidden { get; private set; }

        public bool TryHide(out string diagnostic)
        {
            diagnostic = string.Empty;
            if (disposed) { diagnostic = "Tracking interface has been disposed."; return false; }
            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION)
            {
                Restore();
                diagnostic = "Tracking Station is not active.";
                return false;
            }

            var tracking = SpaceTracking.Instance;
            if (tracking == null || tracking.listContainer == null || tracking.FlyButton == null || tracking.LeaveBtn == null)
            {
                diagnostic = "Waiting for the stock Tracking Station to finish loading.";
                return false;
            }

            try
            {
                if (owner != null && owner != tracking) Restore();
                owner = tracking;
                var targets = CollectBoundTargets(tracking);
                var common = CommonAncestor(targets);
                // Never suppress the shared UI canvas or an unrelated scene ancestor.
                var ownsCommon = common != null &&
                    (common == tracking.transform || common.IsChildOf(tracking.transform)) &&
                    common.GetComponent<RectTransform>() != null && common.GetComponent<Canvas>() == null;
                if (ownsCommon)
                    Suppress(common.gameObject);
                else
                {
                    // This field is serialized in the installed KSP 1.12.5 assembly.
                    // A missing binding falls back to public list/control references.
                    var scroll = SidebarField == null ? null : SidebarField.GetValue(tracking) as ScrollRect;
                    if (scroll != null) Suppress(scroll.gameObject);
                    foreach (var target in targets) Suppress(target.gameObject);
                }
                if (mapFilters == null) mapFilters = UnityEngine.Object.FindObjectOfType<MapViewFiltering>();
                if (mapFilters != null && mapFilters.GetComponent<RectTransform>() != null && mapFilters.GetComponent<Canvas>() == null)
                    Suppress(mapFilters.gameObject);
                SuppressLegacyInterface();
                IsHidden = groups.Any(state => state.Group != null);
                return IsHidden;
            }
            catch (Exception ex)
            {
                Restore();
                diagnostic = "Stock interface restored: " + ex.Message;
                return false;
            }
        }

        public void SetMapViewport(Rect normalized)
        {
            if (disposed || !IsHidden || HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
            if (!Finite(normalized.x) || !Finite(normalized.y) || !Finite(normalized.width) || !Finite(normalized.height)) return;
            if (normalized.x < 0 || normalized.y < 0 || normalized.width <= 0 || normalized.height <= 0 ||
                normalized.xMax > 1.001f || normalized.yMax > 1.001f) return;
            try
            {
                ApplyViewport(PlanetariumCamera.Camera, normalized);
                // Planet bodies are rendered by ScaledCamera, not by the orbit
                // camera alone. All world layers must share the same viewport or
                // body meshes drift away from their orbit lines and map markers.
                var scaled = ScaledCamera.Instance;
                if (scaled != null)
                {
                    ApplyViewport(scaled.cam, normalized);
                    ApplyViewport(scaled.galaxyCamera, normalized);
                }
                var map = MapView.fetch;
                if (map == null) return;
                ApplyViewport(map.VectorCamera, normalized);
                // KSP's map FX cameras must follow the same viewport as the body/line cameras.
                // Screen UI cameras are deliberately excluded.
                foreach (var field in MapFxFields) ApplyViewport(field.GetValue(map) as Camera, normalized);
            }
            catch (Exception ex)
            {
                RestoreCameras();
                Debug.LogWarning("[ExpanseTrackingStation] Map viewport restored: " + ex.Message);
            }
        }

        public void Restore()
        {
            RestoreCameras();
            foreach (var state in legacyCanvases)
                if (state.Canvas != null) state.Canvas.enabled = state.Enabled;
            foreach (var state in legacyDisplays)
                if (state.Behaviour != null) state.Behaviour.enabled = state.Enabled;
            legacyCanvases.Clear();
            legacyDisplays.Clear();
            nextDisplayScan = 0;
            foreach (var state in groups)
            {
                if (state.Group == null) continue;
                try
                {
                    state.Group.alpha = state.Alpha;
                    state.Group.blocksRaycasts = state.BlocksRaycasts;
                    if (state.Added) UnityEngine.Object.Destroy(state.Group);
                }
                catch (Exception ex) { Debug.LogWarning("[ExpanseTrackingStation] Native presentation restore: " + ex.Message); }
            }
            groups.Clear();
            owner = null;
            mapFilters = null;
            IsHidden = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            Restore();
            disposed = true;
        }

        private void Suppress(GameObject target)
        {
            if (target == null) return;
            var state = groups.FirstOrDefault(item => item.Group != null && item.Group.gameObject == target);
            if (state == null)
            {
                var group = target.GetComponent<CanvasGroup>();
                var added = group == null;
                if (added) group = target.AddComponent<CanvasGroup>();
                state = new GroupState { Group = group, Alpha = group.alpha, BlocksRaycasts = group.blocksRaycasts, Added = added };
                groups.Add(state);
            }
            state.Group.alpha = 0;
            state.Group.blocksRaycasts = false;
            // Do not change interactable, enabled, or activeSelf: stock guards still own them.
        }

        private void SuppressLegacyInterface()
        {
            var master = UIMasterController.Instance;
            if (master != null)
            {
                // These presentation canvases contain stock chrome and UIApp panels,
                // including panels opened later by hover or keyboard shortcuts.
                // Keep their controllers alive. Do NOT suppress dialogCanvas: stock
                // recovery/termination confirmations must remain visible and usable.
                SuppressCanvasTree(master.mainCanvas, master);
                SuppressCanvasTree(master.appCanvas, master);
                SuppressCanvasTree(master.actionCanvas, master);
                SuppressCanvasTree(master.tooltipCanvas, master);
            }
            if (Time.realtimeSinceStartup >= nextDisplayScan)
            {
                nextDisplayScan = Time.realtimeSinceStartup + 1f;
                // KER's tracking display is IMGUI, outside KSP's canvas hierarchy.
                // Bind only its verified presentation components, never its core.
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "KerbalEngineer");
                if (assembly != null)
                    foreach (var name in LegacyDisplayNames)
                    {
                        var type = assembly.GetType(name, false);
                        if (type == null || !typeof(Behaviour).IsAssignableFrom(type)) continue;
                        foreach (var item in UnityEngine.Object.FindObjectsOfType(type))
                        {
                            var behaviour = item as Behaviour;
                            if (behaviour == null || legacyDisplays.Any(state => state.Behaviour == behaviour)) continue;
                            legacyDisplays.Add(new BehaviourState { Behaviour = behaviour, Enabled = behaviour.enabled });
                        }
                    }
            }
            // Enforce ownership after stock/mod callbacks without altering their
            // stored state. Stock interface / scene exit restores every prior flag.
            foreach (var state in legacyDisplays)
                if (state.Behaviour != null) state.Behaviour.enabled = false;
        }

        private void SuppressCanvas(Canvas canvas)
        {
            if (canvas == null) return;
            if (!legacyCanvases.Any(state => state.Canvas == canvas))
                legacyCanvases.Add(new CanvasState { Canvas = canvas, Enabled = canvas.enabled });
            canvas.enabled = false;
        }

        private void SuppressCanvasTree(Canvas root, UIMasterController master)
        {
            if (root == null) return;
            // Nested canvases can render independently. Claim those too, including
            // newly spawned UIApp frames, but preserve dialogs and debug messages.
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                if (Within(canvas, master.dialogCanvas) || Within(canvas, master.screenMessageCanvas) || Within(canvas, master.debugCanvas)) continue;
                SuppressCanvas(canvas);
            }
        }

        private static bool Within(Canvas candidate, Canvas protectedRoot)
        {
            return protectedRoot != null && (candidate == protectedRoot || candidate.transform.IsChildOf(protectedRoot.transform));
        }

        private void ApplyViewport(Camera camera, Rect normalized)
        {
            if (camera == null) return;
            if (!cameras.Any(state => state.Camera == camera)) cameras.Add(new CameraState { Camera = camera, Rect = camera.rect });
            if (camera.rect != normalized) camera.rect = normalized;
        }

        private void RestoreCameras()
        {
            foreach (var state in cameras)
            {
                if (state.Camera == null) continue;
                try { state.Camera.rect = state.Rect; }
                catch (Exception ex) { Debug.LogWarning("[ExpanseTrackingStation] Camera restore: " + ex.Message); }
            }
            cameras.Clear();
        }

        private static List<Transform> CollectBoundTargets(SpaceTracking tracking)
        {
            var result = new List<Transform>();
            Add(result, tracking.listContainer);
            Add(result, tracking.missionsListContainer);
            Add(result, tracking.tabsToggleGroup);
            Add(result, tracking.tglTrackedObjects);
            Add(result, tracking.tglMissionObjectives);
            Add(result, tracking.FlyButton);
            Add(result, tracking.DeleteButton);
            Add(result, tracking.RecoverButton);
            Add(result, tracking.TrackButton);
            Add(result, tracking.LeaveBtn);
            return result;
        }

        private static void Add(List<Transform> result, Component component)
        {
            if (component != null && !result.Contains(component.transform)) result.Add(component.transform);
        }

        private static Transform CommonAncestor(IReadOnlyList<Transform> targets)
        {
            if (targets.Count == 0) return null;
            for (var candidate = targets[0]; candidate != null; candidate = candidate.parent)
                if (targets.All(target => target == candidate || target.IsChildOf(candidate))) return candidate;
            return null;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
