using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP.UI.Screens;

namespace Expanse.WorldBridge
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class DepotWindowAddon : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private bool visible, confirmRegister;
        private string confirmUnregisterId;
        private Rect window = new Rect(180, 70, 460, 620);
        private Vector2 depotScroll;
        private Vector2 partScroll;
        private Vector2 anchorScroll;
        private Vector2 resourceScroll;
        private readonly HashSet<uint> selected = new HashSet<uint>();
        private List<Part> candidates = new List<Part>();
        private Texture2D icon;
        private Vessel cachedVessel;
        private float lastRefresh;
        private uint anchorChoice;
        private string feedback;
        private void Start() { GameEvents.onGUIApplicationLauncherReady.Add(AddButton); if (ApplicationLauncher.Ready) AddButton(); Refresh(); }
        private void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            icon = MakeIcon();
            button = ApplicationLauncher.Instance.AddModApplication(OnTrue, OnFalse, OnHover, OnHoverOut, OnEnable, OnDisable, ApplicationLauncher.AppScenes.FLIGHT, (Texture)icon);
        }
        private void OnTrue() { visible = true; Refresh(); }
        private void OnFalse() { visible = false; confirmRegister = false; confirmUnregisterId = null; }
        private void OnHover() { ScreenMessages.PostScreenMessage("Expanse Depot Registration", 2f, ScreenMessageStyle.UPPER_CENTER); }
        private void OnHoverOut() { }
        private void OnEnable() { }
        private void OnDisable() { visible = false; }
        private void Refresh()
        {
            candidates.Clear();
            Vessel v = FlightGlobals.ActiveVessel;
            if (cachedVessel != v) { selected.Clear(); anchorChoice = 0; cachedVessel = v; }
            if (!FoundationRegistrationContext.CanEnumerate(v)) return;
            HashSet<uint> registered = new HashSet<uint>();
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            if (registry != null) foreach (DepotRegistration depot in registry.Registrations) foreach (uint id in depot.MemberIds) registered.Add(id);
            foreach (Part p in v.parts) if (p.Resources != null && p.Resources.Count > 0 && p.persistentId != 0 && !registered.Contains(p.persistentId)) { candidates.Add(p); if (candidates.Count >= 4096) break; }
            lastRefresh = Time.realtimeSinceStartup;
        }
        private void OnGUI() { if (visible) window = GUILayout.Window(GetInstanceID(), window, Draw, "Expanse Depot Registration"); }
        private void Draw(int id)
        {
            DepotRegistryModule registry = DepotRegistryModule.Instance;
            GUILayout.Label("Choose depot tanks. Docked visitors are never selected automatically.");
            if (registry == null) { GUILayout.Label("Save registry is loading."); GUI.DragWindow(); return; }
            if (registry.IsCorrupt) { GUILayout.Label("Saved depot data is unavailable and preserved. Do not register over it."); GUI.DragWindow(); return; }
            if (!registry.IsReady) { GUILayout.Label("Save registry is loading."); GUI.DragWindow(); return; }
            if (registry.RegisteredDepotCount > 0)
            {
                GUILayout.Label("Registered depots (parts never overlap):");
                depotScroll = GUILayout.BeginScrollView(depotScroll, GUILayout.Height(76));
                foreach (DepotRegistration depot in registry.Registrations)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(depot.Label + "  ·  " + depot.MemberIds.Count + " parts  ·  anchor " + depot.Anchor);
                    if(depot.OwnerKind=="colony")GUILayout.Label("Paid colony",GUILayout.Width(76));
                    else if (GUILayout.Button(confirmUnregisterId == depot.DepotId ? "Confirm" : "Remove", GUILayout.Width(76)))
                    {
                        if (confirmUnregisterId == depot.DepotId) { bool removed = registry.Unregister(depot.DepotId); feedback = removed ? "Depot removed." : "Could not remove this depot."; confirmUnregisterId = null; Refresh(); }
                        else confirmUnregisterId = depot.DepotId;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            if (Time.realtimeSinceStartup - lastRefresh >= 1f || cachedVessel != FlightGlobals.ActiveVessel) Refresh();
            if (registry.RegisteredDepotCount >= 8) GUILayout.Label("Maximum of 8 depots reached.");
            else
            {
                GUILayout.Label("New depots support up to 64 selected parts. Legacy memberships remain readable.");
                if (candidates.Count == 0)
                    GUILayout.Label(FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.packed
                        ? "This vessel is packed. Select an unpacked vessel or an active base anchored by Expanse Foundations."
                        : "No unregistered resource-bearing parts were found on the active vessel.");
                else if (FoundationRegistrationContext.IsAnchored(FlightGlobals.ActiveVessel))
                    GUILayout.Label("Anchored base: tanks can be registered. Packed-base inventory transfers are not enabled yet.");
            }
            partScroll = GUILayout.BeginScrollView(partScroll, GUILayout.Height(190));
            foreach (Part p in candidates)
            {
                string resourceSummary = string.Join(", ", p.Resources.Cast<PartResource>().Select(r => r.resourceName + " " + r.amount.ToString("0.##") + "/" + r.maxAmount.ToString("0.##")).ToArray());
                bool check = selected.Contains(p.persistentId);
                bool next = GUILayout.Toggle(check, p.partInfo.title + "  [" + p.persistentId + "]  " + resourceSummary);
                if (next != check) confirmRegister = false;
                if (next) selected.Add(p.persistentId); else selected.Remove(p.persistentId);
            }
            GUILayout.EndScrollView();
            List<Part> chosen = candidates.Where(p => selected.Contains(p.persistentId)).ToList();
            if (chosen.Count > 0 && !chosen.Any(p => p.persistentId == anchorChoice)) anchorChoice = chosen[0].persistentId;
            var totals = chosen.SelectMany(p => p.Resources.Cast<PartResource>()).GroupBy(r => r.resourceName).Select(g => new { Name = g.Key, Amount = g.Sum(r => r.amount), Capacity = g.Sum(r => r.maxAmount) }).ToList();
            Part anchorPart = chosen.FirstOrDefault(p => p.persistentId == anchorChoice);
            GUILayout.Label("Preview: " + chosen.Count + " parts. Anchor: " + (anchorPart == null ? "none" : anchorPart.partInfo.title + " [" + anchorPart.persistentId + "]"));
            GUILayout.Label("Choose the anchor member:");
            anchorScroll = GUILayout.BeginScrollView(anchorScroll, GUILayout.Height(58));
            foreach (Part p in chosen) if (GUILayout.Toggle(anchorChoice == p.persistentId, p.partInfo.title + " [" + p.persistentId + "]") && anchorChoice != p.persistentId) { anchorChoice = p.persistentId; confirmRegister = false; }
            GUILayout.EndScrollView();
            GUILayout.Label("Resource totals:");
            resourceScroll = GUILayout.BeginScrollView(resourceScroll, GUILayout.Height(70));
            foreach (var total in totals) GUILayout.Label(total.Name + ": " + total.Amount.ToString("0.###") + " / " + total.Capacity.ToString("0.###"));
            GUILayout.EndScrollView();
            if (feedback != null) GUILayout.Label(feedback);
            if (registry.RegisteredDepotCount < 8 && chosen.Count > 0 && GUILayout.Button(confirmRegister ? "Confirm register selected depot" : "Register selected tanks…"))
            {
                if (confirmRegister)
                {
                    string depotLabel = FlightGlobals.ActiveVessel.vesselName;
                    bool success = registry.Register(depotLabel, anchorChoice, chosen.Select(p => p.persistentId));
                    feedback = success ? "Depot registered." : "Registration was rejected. Check the selected anchor and active vessel.";
                    confirmRegister = false; if (success) { selected.Clear(); Refresh(); }
                }
                else confirmRegister = true;
            }
            GUI.DragWindow();
        }
        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            if (icon != null) Destroy(icon);
        }

        private static Texture2D MakeIcon()
        {
            Texture2D texture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[32 * 32];
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                bool rim = x < 3 || y < 3 || x > 28 || y > 28;
                bool tank = x >= 10 && x <= 21 && y >= 7 && y <= 25;
                bool fluid = tank && y <= 17;
                pixels[y * 32 + x] = rim ? new Color32(205, 220, 230, 255) : !tank ? new Color32(25, 42, 58, 255) : fluid ? new Color32(61, 178, 210, 255) : new Color32(160, 190, 205, 255);
            }
            texture.SetPixels32(pixels); texture.Apply(false, true); return texture;
        }
    }
}
