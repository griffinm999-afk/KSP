using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using KSP.UI.Screens;
using System.Text.RegularExpressions;

namespace Expanse.WorldBridge
{
    // Read model supplied by the save authority. Fallback inspection reads only
    // the active vessel and never infers site membership or service eligibility.
    public sealed class ColonyMaintenanceRow
    {
        public string Id, Name, State, Quantity, Basis, Detail, ServiceReason;
        public bool CanService;
        public string ColonyId, FacilityId;
        public Dictionary<string,string> ServiceFields;
    }
    public sealed class ColonyMaintenanceSnapshot
    {
        public string ContextKey, Status, Reason;
        public long Revision;
        public readonly Dictionary<string, ColonyMaintenanceRow[]> Sections = new Dictionary<string, ColonyMaintenanceRow[]>(StringComparer.Ordinal);
    }
    public sealed class ColonyMaintenanceServiceRequest
    {
        public string OperationId, ContextKey, FacilityId, ColonyId;
        public long ExpectedRevision;
        public Dictionary<string,string> Fields;
    }
    public sealed class ColonyMaintenanceServiceResult
    {
        public bool Terminal;
        public string Message;
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ColonyMaintenanceWindow : MonoBehaviour
    {
        public static Func<ColonyMaintenanceSnapshot> Capture;
        public static Func<ColonyMaintenanceServiceRequest, ColonyMaintenanceServiceResult> SubmitService;
        private static readonly string[] Tabs = { "Facilities", "Machinery", "Staffing", "Power", "Inputs", "Service log" };
        private ApplicationLauncherButton launcher;
        private Texture2D icon, surface, selectedSurface;
        private GUIStyle panelStyle, textStyle, mutedStyle, numberStyle, headerStyle, nameStyle, buttonStyle;
        private Rect window = new Rect(80, 80, 760, 480);
        private bool visible;
        private bool diagnostics;
        private int tab;
        private Vector2 scroll, detailScroll, statusScroll, serviceReasonScroll;
        private float lastRefresh;
        private string selectedId, feedback;
        private ColonyMaintenanceSnapshot snapshot;
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ColonyMaintenanceServiceRequest> pending = new Dictionary<string, ColonyMaintenanceServiceRequest>(StringComparer.Ordinal);
        private void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
        }
        private void AddButton()
        {
            if (launcher != null || ApplicationLauncher.Instance == null) return;
            icon = ColonyServiceIcon();
            launcher = ApplicationLauncher.Instance.AddModApplication(() => { visible = true; Refresh(); }, () => visible = false,
                () => ScreenMessages.PostScreenMessage("Expanse colony maintenance · facilities, staffing, supplies and power", 2f, ScreenMessageStyle.UPPER_CENTER), () => { }, () => { }, () => visible = false,
                ApplicationLauncher.AppScenes.FLIGHT, icon);
        }
        private void Refresh()
        {
            try
            {
                var next = Capture == null ? InspectActiveVessel() : Capture();
                if (next == null) next = new ColonyMaintenanceSnapshot { Status = "Unavailable", Reason = "Colony authority is loading." };
                if (snapshot == null || next.ContextKey != snapshot.ContextKey) { selectedId = null; expanded.Clear(); feedback = null; detailScroll = statusScroll = serviceReasonScroll = Vector2.zero; }
                snapshot = next;
            }
            catch (Exception ex) { snapshot = new ColonyMaintenanceSnapshot { Status = "Unavailable", Reason = "Maintenance readback failed: " + ex.GetType().Name }; }
            lastRefresh = Time.realtimeSinceStartup;
        }
        private void OnGUI()
        {
            if (!visible) return;
            if (snapshot == null || Time.realtimeSinceStartup - lastRefresh >= 1f) Refresh();
            EnsureStyles();
            window.width = Mathf.Min(760, Mathf.Max(320, Screen.width - 16));
            window.height = Mathf.Min(480, Mathf.Max(280, Screen.height - 16));
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            window = GUI.Window(GetInstanceID(), window, Draw, "Expanse colony maintenance", panelStyle);
        }
        private void Draw(int id)
        {
            float width = window.width - 24;
            if (GUI.Button(new Rect(window.width - 32, 4, 24, 22), "×", buttonStyle)) { visible = false; if (launcher != null) launcher.SetFalse(false); }
            diagnostics=GUI.Toggle(new Rect(window.width-137,4,98,22),diagnostics,"Diagnostics",buttonStyle);
            int previous = tab;
            tab = GUI.Toolbar(new Rect(12, 30, width, 28), tab, width<620 ? new[]{"Sites","Machine","Crew","Power","Inputs","History"} : Tabs, buttonStyle);
            if (previous != tab) { scroll = detailScroll = serviceReasonScroll = Vector2.zero; selectedId = null; }
            ScrollText(new Rect(12, 63, width, 34), Display(snapshot.Status + " · " + snapshot.Reason), mutedStyle, ref statusScroll);
            ColonyMaintenanceRow[] source;
            if (!snapshot.Sections.TryGetValue(Tabs[tab], out source)) source = new ColonyMaintenanceRow[0];
            var rows = source.Take(256).ToArray();
            var selected = rows.FirstOrDefault(r => r.Id == selectedId);
            bool narrow = width < 620;
            float nameWidth = narrow ? width * .49f : width * .37f;
            float stateWidth = narrow ? width * .25f : width * .24f;
            float quantityWidth = narrow ? width * .26f - 18 : width * .21f;
            float basisWidth = narrow ? 0 : width - nameWidth - stateWidth - quantityWidth - 18;
            float headerY = 100, tableY = 124;
            bool stackedService=width<500;
            float footerHeight=Mathf.Min(stackedService ? selected==null ? 118 : 148 : selected==null ? 88 : 124,window.height-tableY-52);
            float tableHeight = Mathf.Max(40, window.height - tableY - footerHeight - 12);
            Header(12, headerY, nameWidth, "FACILITY / ITEM");
            Header(12 + nameWidth, headerY, stateWidth, "STATE");
            Header(12 + nameWidth + stateWidth, headerY, quantityWidth, "QUANTITY");
            if (!narrow) Header(12 + nameWidth + stateWidth + quantityWidth, headerY, basisWidth, "BASIS");
            float contentHeight = rows.Sum(r => RowHeight(r, width - 30)) + 8;
            scroll = GUI.BeginScrollView(new Rect(12, tableY, width, tableHeight), scroll, new Rect(0, 0, width - 18, Mathf.Max(tableHeight, contentHeight)));
            float y = 0;
            foreach (var row in rows)
            {
                float height = RowHeight(row, width - 30);
                if (y + height >= scroll.y && y <= scroll.y + tableHeight)
                {
                    if (row.Id == selectedId) GUI.DrawTexture(new Rect(0, y, width - 18, height), selectedSurface);
                    if (GUI.Button(new Rect(0, y, 20, 28), expanded.Contains(row.Id) ? "−" : "+", buttonStyle))
                    { if (!expanded.Add(row.Id)) expanded.Remove(row.Id); SelectRow(row.Id); }
                    if (GUI.Button(new Rect(22, y, Mathf.Max(20, nameWidth - 24), 28), new GUIContent(Fit(row.Name, nameWidth - 28, nameStyle), row.Name), nameStyle)) SelectRow(row.Id);
                    GUI.Label(new Rect(nameWidth, y + 4, stateWidth - 6, 24), new GUIContent(Fit(row.State, stateWidth - 10, textStyle), row.State), textStyle);
                    GUI.Label(new Rect(nameWidth + stateWidth, y + 4, quantityWidth - 6, 24), new GUIContent(Fit(row.Quantity, quantityWidth - 10, numberStyle), row.Quantity), numberStyle);
                    if (!narrow) GUI.Label(new Rect(nameWidth + stateWidth + quantityWidth, y + 4, basisWidth - 4, 24), new GUIContent(Fit(row.Basis, basisWidth - 8, mutedStyle), row.Basis), mutedStyle);
                    if (expanded.Contains(row.Id)) GUI.Label(new Rect(24, y + 30, width - 48, height - 32), Display(row.Detail), textStyle);
                }
                y += height;
            }
            if (rows.Length == 0) GUI.Label(new Rect(8, 12, width - 40, 60), "No data available for this tab. " + snapshot.Reason, textStyle);
            GUI.EndScrollView();
            float footerY = tableY + tableHeight + 8;
            GUI.Box(new Rect(12, footerY, width, footerHeight), GUIContent.none, panelStyle);
            var detail = Display(selected == null ? "Select a row for prerequisites and service eligibility." : selected.Name + " · " + selected.Basis + "\n" + selected.Detail);
            float detailHeight = Mathf.Min(selected==null ? 26 : 52,Mathf.Max(22,footerHeight-(stackedService ? 94 : 60)));
            ScrollText(new Rect(20, footerY + 5, width - 16, detailHeight), detail, textStyle, ref detailScroll);
            bool wasEnabled = GUI.enabled;
            ColonyMaintenanceServiceRequest pendingRequest = null;
            if (!String.IsNullOrEmpty(snapshot.ContextKey)) pending.TryGetValue(snapshot.ContextKey, out pendingRequest);
            GUI.enabled = SubmitService != null && !String.IsNullOrEmpty(snapshot.ContextKey) && (pendingRequest != null || selected != null && selected.CanService);
            if (GUI.Button(new Rect(20, footerY + detailHeight + 10, 160, 26), pendingRequest == null ? "Deliver reviewed service" : "Reconcile service", buttonStyle))
            {
                // Adapter must use the operation marker and reconcile witnesses;
                // UI never calls arbitrary PartModule actions.
                var request = pendingRequest ?? new ColonyMaintenanceServiceRequest { OperationId = Guid.NewGuid().ToString("D"), ContextKey = snapshot.ContextKey, FacilityId = selected.FacilityId,ColonyId=selected.ColonyId,Fields=new Dictionary<string,string>(selected.ServiceFields,StringComparer.Ordinal), ExpectedRevision = snapshot.Revision };
                pending[request.ContextKey] = request;
                try { var result = SubmitService(request); feedback = result.Message; if (result.Terminal) pending.Remove(request.ContextKey); Refresh(); }
                catch (Exception ex) { feedback = "Outcome unknown; reconcile " + request.OperationId + ". " + ex.GetType().Name; }
            }
            GUI.enabled = wasEnabled;
            string reason = selected == null ? "Choose a facility." : SubmitService == null ? "A qualified service adapter is unavailable." : selected.ServiceReason;
            var reasonBounds=stackedService ? new Rect(20,footerY+detailHeight+40,width-16,Mathf.Max(20,footerHeight-detailHeight-48)) : new Rect(190,footerY+detailHeight+9,width-196,42);
            ScrollText(reasonBounds, Display(feedback ?? reason), mutedStyle, ref serviceReasonScroll);
            GUI.DragWindow(new Rect(0, 0, window.width - 38, 27));
        }
        private void SelectRow(string id)
        {
            if (selectedId != id) detailScroll = serviceReasonScroll = Vector2.zero;
            selectedId = id;
        }
        private static void ScrollText(Rect viewport, string text, GUIStyle style, ref Vector2 position)
        {
            // Clip long failure explanations to their reserved space. Keep the
            // complete text scrollable without moving the fixed table header.
            var content = new GUIContent(Safe(text));
            float contentWidth = Mathf.Max(20, viewport.width - 22);
            float contentHeight = Mathf.Max(viewport.height, style.CalcHeight(content, contentWidth));
            position.y = Mathf.Clamp(position.y, 0, Mathf.Max(0, contentHeight - viewport.height));
            position = GUI.BeginScrollView(viewport, position, new Rect(0, 0, contentWidth, contentHeight));
            GUI.Label(new Rect(0, 0, contentWidth, contentHeight), content, style);
            GUI.EndScrollView();
        }
        private float RowHeight(ColonyMaintenanceRow row, float width)
        {
            return expanded.Contains(row.Id) ? 34 + textStyle.CalcHeight(new GUIContent(Safe(row.Detail)), Mathf.Max(40, width - 18)) : 30;
        }
        private void Header(float x, float y, float width, string label) { GUI.Label(new Rect(x + 3, y, width - 6, 22), label, headerStyle); }
        private static string Safe(string value) { return String.IsNullOrEmpty(value) ? "—" : value.Length > 4096 ? value.Substring(0, 4096) + "…" : value; }
        private string Display(string value)=>diagnostics ? Safe(value) : Regex.Replace(Safe(value),@"\b(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32,64})\b","[technical reference]");
        private static string Fit(string value, float width, GUIStyle style)
        {
            value = Safe(value);
            if (style.CalcSize(new GUIContent(value)).x <= width) return value;
            int low = 0, high = value.Length;
            while (low < high) { int middle = (low + high + 1) / 2; if (style.CalcSize(new GUIContent(value.Substring(0, middle) + "…")).x <= width) low = middle; else high = middle - 1; }
            return value.Substring(0, low) + "…";
        }
        private static Texture2D ColonyServiceIcon()
        {
            // Native 32px colony silhouette with a service spanner; transparent
            // edge and bright geometry remain legible in the stock launcher.
            var pixels=new Color32[32*32];var ink=new Color32(231,243,246,255);var accent=new Color32(61,177,186,255);
            Action<int,int,Color32> dot=(x,y,color)=>{if(x>=0 && x<32 && y>=0 && y<32)pixels[y*32+x]=color;};
            Action<int,int,int,int,Color32> line=(x0,y0,x1,y1,color)=>
            {
                int dx=Math.Abs(x1-x0),sx=x0<x1 ? 1 : -1,dy=-Math.Abs(y1-y0),sy=y0<y1 ? 1 : -1,error=dx+dy;
                while(true){dot(x0,y0,color);dot(x0+1,y0,color);if(x0==x1 && y0==y1)break;int e=2*error;if(e>=dy){error+=dy;x0+=sx;}if(e<=dx){error+=dx;y0+=sy;}}
            };
            for(int y=4;y<=15;y++)for(int x=3;x<=28;x++)if((x>=3 && x<=9) || (x>=13 && x<=19) || (x>=23 && x<=28))dot(x,y,accent);
            line(2,4,29,4,ink);line(9,10,24,10,ink);line(3,16,6,20,ink);line(6,20,10,16,ink);line(13,16,16,21,ink);line(16,21,20,16,ink);line(23,16,26,20,ink);line(26,20,29,16,ink);
            for(int y=8;y<=12;y++)for(int x=15;x<=17;x++)dot(x,y,ink);
            line(19,20,27,28,ink);line(19,20,17,23,ink);line(17,23,18,26,ink);line(18,26,21,27,ink);line(21,27,24,25,ink);line(27,28,29,27,ink);
            var texture=new Texture2D(32,32,TextureFormat.RGBA32,false);texture.SetPixels32(pixels);texture.Apply();return texture;
        }
        private void EnsureStyles()
        {
            if (panelStyle != null) return;
            surface = Solid(new Color32(44, 48, 51, 255)); selectedSurface = Solid(new Color32(68, 75, 80, 255));
            panelStyle = new GUIStyle(GUI.skin.window) { fontSize = 13, padding = new RectOffset(10, 10, 24, 8) };
            panelStyle.normal.background = surface;
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, padding = new RectOffset(3, 3, 2, 2) };
            textStyle.normal.textColor = new Color32(224, 229, 232, 255);
            mutedStyle = new GUIStyle(textStyle); mutedStyle.normal.textColor = new Color32(177, 188, 194, 255);
            numberStyle = new GUIStyle(textStyle) { alignment = TextAnchor.UpperRight, wordWrap = false };
            headerStyle = new GUIStyle(mutedStyle) { fontSize = 10, fontStyle = FontStyle.Bold, wordWrap = false };
            nameStyle = new GUIStyle(textStyle) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, wordWrap = false, padding = new RectOffset(3, 3, 3, 3) };
        }
        private static Texture2D Solid(Color32 color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false); texture.SetPixel(0, 0, color); texture.Apply(false, true); return texture;
        }
        private static ColonyMaintenanceSnapshot InspectActiveVessel()
        {
            var vessel = FlightGlobals.ActiveVessel;
            var result = new ColonyMaintenanceSnapshot { Status = "Read-only inspection", Reason = "Active vessel only; colony membership and service eligibility are not inferred." };
            if (vessel == null || !vessel.loaded || vessel.parts == null) { result.Reason = "No loaded active vessel is available."; return result; }
            string basis = vessel.packed ? "Loaded / packed" : "Loaded / unpacked";
            result.ContextKey = vessel.id.ToString("D");
            result.Sections["Facilities"] = new[] { new ColonyMaintenanceRow { Id = vessel.id.ToString("D"), Name = vessel.vesselName, State = vessel.situation.ToString(), Quantity = vessel.parts.Count + " parts", Basis = basis, Detail = "Body " + (vessel.mainBody == null ? "unknown" : vessel.mainBody.bodyName) + "; latitude " + vessel.latitude.ToString("0.00000") + "; longitude " + vessel.longitude.ToString("0.00000") + ". Certified homes, grid coverage and service history require the colony authority.", ServiceReason = "No qualified service adapter is connected." } };
            var machinery = new List<ColonyMaintenanceRow>(); var power = new List<ColonyMaintenanceRow>(); var inputs = new List<ColonyMaintenanceRow>(); var staffing = new List<ColonyMaintenanceRow>();
            foreach (var part in vessel.parts.Where(p => p != null).Take(4096))
            {
                string name = part.partInfo == null ? part.name : part.partInfo.title;
                foreach (PartResource resource in part.Resources)
                {
                    if (resource == null) continue;
                    var row = new ColonyMaintenanceRow { Id = part.persistentId + ":" + resource.resourceName, Name = name, State = resource.resourceName, Quantity = resource.amount.ToString("0.##") + " / " + resource.maxAmount.ToString("0.##"), Basis = basis,
                        Detail = "Physical part tank. Resource flow " + (resource.flowState ? "enabled" : "disabled") + ". Accessible reserve, utility distribution and ownership transfers are not established by this reading.", ServiceReason = "Service eligibility is unavailable." };
                    if (resource.resourceName == "Machinery") machinery.Add(row);
                    if (resource.resourceName == "ElectricCharge") power.Add(row);
                    if (resource.resourceName != "ElectricCharge" && resource.resourceName != "Machinery") inputs.Add(row);
                }
                foreach (var crew in part.protoModuleCrew)
                    staffing.Add(new ColonyMaintenanceRow { Id = crew.name, Name = crew.name, State = crew.trait, Quantity = "Level " + crew.experienceLevel, Basis = basis,
                        Detail = "Occupies " + name + " [" + part.persistentId + "]. Resident/job status is not inferred. Legacy auto repair requires an Engineer in the workshop part itself.", ServiceReason = "Staff assignments require the colony authority." });
            }
            result.Sections["Machinery"] = machinery.ToArray(); result.Sections["Power"] = power.ToArray(); result.Sections["Inputs"] = inputs.ToArray(); result.Sections["Staffing"] = staffing.ToArray();
            result.Sections["Service log"] = new ColonyMaintenanceRow[0];
            return result;
        }
        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (launcher != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(launcher);
            if (icon != null) Destroy(icon); if (surface != null) Destroy(surface); if (selectedSurface != null) Destroy(selectedSurface);
        }
    }
}
