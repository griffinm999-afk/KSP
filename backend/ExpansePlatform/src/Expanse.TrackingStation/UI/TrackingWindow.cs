using System;
using System.Collections.Generic;
using System.Linq;
using Expanse.TrackingStation.Core;
using UnityEngine;

namespace Expanse.TrackingStation.UI
{
    /// <summary>Default full-screen Tracking Station shell. The map is native; all records are snapshots.</summary>
    public sealed class TrackingWindow : IDisposable
    {
        private sealed class Row { public BodyBranchResult Branch; public VesselSummary Vessel; public int Depth; }
        private readonly List<Row> rows = new List<Row>();
        private BodyHierarchy hierarchy;
        private IReadOnlyList<VesselSummary> vessels = new VesselSummary[0];
        private TrackingQueryResult result;
        private HashSet<string> scopedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string[] types = new string[0], situations = new string[0];
        private bool dirty = true, grouped = true;
        private string status = string.Empty, menu = string.Empty, viewName = string.Empty;
        private Vector2 treeScroll, menuScroll, cardScroll;
        private int typePage;
        private Rect menuAnchor, menuRect, cardRect;
        private TrackingShellLayout layout;
        private GUISkin privateSkin, sourceSkin;
        private GUIStyle text, muted, brand, section, wrapped, rowStyle, selectedRow, tealButton, cardTitle;
        private float skinFont;
        private static readonly Color Panel = new Color(.045f,.075f,.095f,.99f);
        private static readonly Color Teal = new Color(.075f,.32f,.36f,1f);
        private static readonly Color Line = new Color(.16f,.25f,.29f,1f);

        public TrackingWindow() { View = new TrackingView { ShowEmpty = false }; NamedViews = new List<SavedTrackingView>(); Scale = 1; }
        public bool Visible { get; set; }
        public bool FiltersCollapsed { get; set; }
        public float Scale { get; set; }
        public Rect WindowRect { get; set; } // legacy geometry retained only for config compatibility
        public TrackingView View { get; private set; }
        public List<SavedTrackingView> NamedViews { get; set; }
        public string SelectedVesselId { get; private set; }
        public string Status { get { return status; } }
        public bool IsInputFocused { get; private set; }
        public bool FocusFollowsSelection { get; set; }
        public bool ActionsAvailable { get; set; } = true;
        public Rect MapViewportNormalized
        {
            get { var l=layout??TrackingShellLayout.Create(Screen.width,Screen.height,GameSettings.UI_SCALE,Scale);return new Rect(l.SidebarWidth/l.Width,l.FooterHeight/l.Height,1-l.SidebarWidth/l.Width,1-(l.HeaderHeight+l.FooterHeight)/l.Height); }
        }
        public bool GroupByBody { get { return grouped; } set { grouped = value; dirty = true; } }
        public event Action<string> VesselSelected, FocusRequested, FlyRequested, BodyFocusRequested;
        public event Action<string,string> NativeActionRequested;
        public event Action StockViewRequested, LeaveRequested;

        public void SetSnapshot(BodyHierarchy nextHierarchy, IReadOnlyList<VesselSummary> nextVessels)
        {
            hierarchy = nextHierarchy; vessels = nextVessels ?? new VesselSummary[0]; dirty = true;
            types = Enum.GetNames(typeof(VesselType)).Concat(vessels.Select(v => v.Type)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToArray();
            situations = Enum.GetNames(typeof(Vessel.Situations)).Concat(vessels.Select(v => v.Situation)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToArray();
            if (SelectedVesselId != null && !vessels.Any(v => v.Id == SelectedVesselId)) SelectedVesselId = null;
        }
        public void SetStatus(string message) { status = message ?? string.Empty; }
        public void Invalidate() { dirty = true; }
        public TrackingView SnapshotView() { return View.Clone(); }
        public void ApplyView(TrackingView view) { View = (view ?? new TrackingView()).Clone(); menu = string.Empty; dirty = true; treeScroll = Vector2.zero; }
        public bool ContainsInput(Vector2 physical)
        {
            var shell = layout ?? TrackingShellLayout.Create(Screen.width, Screen.height, GameSettings.UI_SCALE, Scale);
            var p = physical / shell.Scale;
            return p.x <= shell.SidebarWidth || p.y <= shell.HeaderHeight || p.y >= shell.Height - shell.FooterHeight || cardRect.Contains(p) || (!string.IsNullOrEmpty(menu) && menuRect.Contains(p));
        }
        public bool DrawFallbackToggle(Rect rect, string caption)
        {
            var old = GUI.skin; EnsureSkin(old, 16);
            try { GUI.skin = privateSkin; return ControlButton(rect, caption); }
            finally { GUI.skin = old; }
        }
        public void OnGUI()
        {
            if (!Visible) return;
            layout = TrackingShellLayout.Create(Screen.width, Screen.height, GameSettings.UI_SCALE, Scale);
            layout.FiltersCollapsed = FiltersCollapsed;
            WindowRect = new Rect(0, 0, Screen.width, Screen.height);
            var oldSkin = GUI.skin; var oldMatrix = GUI.matrix; var oldColor = GUI.color; var oldEnabled = GUI.enabled;
            EnsureSkin(oldSkin, layout.FontSize);
            try
            {
                GUI.skin = privateSkin; GUI.color = Color.white;
                GUI.matrix = Matrix4x4.Scale(new Vector3(layout.Scale, layout.Scale, 1));
                EnsureQuery(); DrawHeader(); DrawSidebar(); DrawSelectionCard(); DrawFooter(); DrawMenu();
                IsInputFocused = GUI.GetNameOfFocusedControl() == "tracking-search" || GUI.GetNameOfFocusedControl() == "tracking-view-name";
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                { if (menu.Length > 0) menu = string.Empty; else if (!string.IsNullOrEmpty(View.SearchText)) { View.SearchText = string.Empty; dirty = true; } else SelectedVesselId = null; GUI.FocusControl(null); Event.current.Use(); }
                if (!string.IsNullOrEmpty(GUI.tooltip))
                {
                    var tipWidth = Math.Min(520, layout.Width - 32);
                    var mouse = Event.current.mousePosition;
                    var tipHeight = Math.Min(130, wrapped.CalcHeight(new GUIContent(GUI.tooltip), tipWidth - 20) + 18);
                    var tip = new Rect(Math.Max(8, Math.Min(mouse.x + 15, layout.Width - tipWidth - 8)), Math.Max(8, Math.Min(mouse.y + 24, layout.Height - tipHeight - 8)), tipWidth, tipHeight);
                    Fill(tip, Panel); GUI.Label(Inset(tip, 9), GUI.tooltip, wrapped);
                }
            }
            finally { GUI.skin = oldSkin; GUI.matrix = oldMatrix; GUI.color = oldColor; GUI.enabled = oldEnabled; }
        }
        private void EnsureSkin(GUISkin skin, float fontSize)
        {
            if (privateSkin != null && sourceSkin == skin && skinFont == fontSize) return;
            if (privateSkin != null) UnityEngine.Object.Destroy(privateSkin);
            sourceSkin = skin; skinFont = fontSize;
            privateSkin = UnityEngine.Object.Instantiate(skin);
            privateSkin.name = "ExpanseTrackingStationPrivateSkin";
            foreach (var style in new[] { privateSkin.button, privateSkin.textField, privateSkin.textArea, privateSkin.toggle, privateSkin.box, privateSkin.label })
            {
                style.font = skin.font; style.fontSize = (int)fontSize; style.fixedHeight = 0; style.fixedWidth = 0;
                style.contentOffset = Vector2.zero; style.padding = new RectOffset(10, 10, 4, 4); style.margin = new RectOffset(0, 0, 0, 0);
                style.richText = false; style.wordWrap = false; style.clipping = TextClipping.Clip;
            }
            privateSkin.button.alignment = TextAnchor.MiddleCenter;
            privateSkin.textField.alignment = TextAnchor.MiddleLeft;
            privateSkin.window.padding = new RectOffset(0,0,0,0);
            privateSkin.verticalScrollbar.fixedWidth = 16; privateSkin.horizontalScrollbar.fixedHeight = 16;
            text = new GUIStyle(privateSkin.label) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0,0,0,0) }; text.normal.textColor = new Color(.90f,.94f,.97f);
            muted = new GUIStyle(text); muted.normal.textColor = new Color(.62f,.71f,.77f);
            brand = new GUIStyle(text) { fontSize = layout != null && layout.Compact ? 18 : 22, fontStyle = FontStyle.Bold };
            section = new GUIStyle(text) { fontSize = (int)fontSize + 2, fontStyle = FontStyle.Bold };
            wrapped = new GUIStyle(text) { wordWrap = true, alignment = TextAnchor.UpperLeft };
            rowStyle = new GUIStyle(text); selectedRow = new GUIStyle(text) { fontStyle = FontStyle.Bold };
            tealButton = new GUIStyle(privateSkin.button); tealButton.normal.textColor = new Color(.45f,1f,.93f);
            cardTitle = new GUIStyle(wrapped) { fontSize = (int)fontSize + 4, fontStyle = FontStyle.Bold };
        }
        private void DrawHeader()
        {
            var l = layout; Fill(new Rect(0,0,l.Width,l.HeaderHeight), Panel); Fill(new Rect(0,l.HeaderHeight-1,l.Width,1),Line);
            GUI.Label(new Rect(l.Padding,3,l.Width-220,28), l.Compact?"EXPANSE TRACKING":"E X P A N S E  |  TRACKING STATION", brand);
            GUI.Label(new Rect(l.Padding,29,l.Width-220,17), "KERBAL SPACE PROGRAM   /   FLEET OPERATIONS", muted);
            if (ControlButton(new Rect(l.Width-185,7,165,32), "Stock interface")) RequestStock();
            DrawTopTypes();
        }
        private void DrawTopTypes()
        {
            var l=layout; var y=48f; var x=l.Padding;
            if(ControlButton(new Rect(x,y,55,26),new GUIContent("All","Show all vessel types"),View.TypeFilters.Count==0?tealButton:privateSkin.button))
            { View.TypeFilters.Clear();dirty=true; }
            x+=63;
            var itemWidth=l.Compact?76f:92f;
            var capacity=Math.Max(1,(int)((l.Width-x-l.Padding-70)/(itemWidth+4)));
            var pages=Math.Max(1,(types.Length+capacity-1)/capacity);
            typePage=Math.Max(0,Math.Min(typePage,pages-1));
            foreach(var vesselType in types.Skip(typePage*capacity).Take(capacity))
            {
                var selected=View.TypeFilters.Contains(vesselType);
                if(selected)Fill(new Rect(x,y,itemWidth,26),Teal);
                if(ControlButton(new Rect(x,y,itemWidth,26),new GUIContent(vesselType,"Toggle "+vesselType+" vessels; multiple types may be selected"),selected?tealButton:privateSkin.button))
                { Toggle(View.TypeFilters,vesselType);dirty=true; }
                x+=itemWidth+4;
            }
            if(pages>1)
            {
                var enabled=GUI.enabled;
                try
                {
                    GUI.enabled=enabled&&typePage>0;
                    if(ControlButton(new Rect(l.Width-l.Padding-64,y,30,26),new GUIContent("<","Previous vessel types")))typePage--;
                    GUI.enabled=enabled&&typePage<pages-1;
                    if(ControlButton(new Rect(l.Width-l.Padding-30,y,30,26),new GUIContent(">","More vessel types")))typePage++;
                }
                finally{GUI.enabled=enabled;}
            }
        }

        private void DrawSidebar()
        {
            var l = layout; var bottom = l.Height - l.FooterHeight;
            Fill(new Rect(0,l.HeaderHeight,l.SidebarWidth,bottom-l.HeaderHeight),Panel); Fill(new Rect(l.SidebarWidth-1,l.HeaderHeight,1,bottom-l.HeaderHeight),Line);
            var x = l.Padding; var w = l.InnerWidth; var y = l.HeaderHeight+l.Padding;
            if(ControlButton(new Rect(x,y,w,l.TitleHeight),new GUIContent((FiltersCollapsed?"> ":"v ")+"VESSELS", "Show or hide search and filter controls; active filters are preserved"),section))
            { FiltersCollapsed=!FiltersCollapsed;l.FiltersCollapsed=FiltersCollapsed;menu=string.Empty;GUI.FocusControl(null);IsInputFocused=false; }
            y += l.TitleHeight+l.Gap;
            if(!FiltersCollapsed)
            {
            GUI.SetNextControlName("tracking-search");
            var nextSearch = GUI.TextField(new Rect(x,y,w-48,l.SearchHeight),View.SearchText ?? string.Empty,256);
            if (nextSearch != View.SearchText) { View.SearchText = nextSearch; dirty = true; treeScroll = Vector2.zero; }
            if (string.IsNullOrEmpty(View.SearchText) && GUI.GetNameOfFocusedControl() != "tracking-search") GUI.Label(new Rect(x+11,y+2,w-80,l.SearchHeight-4),"Search vessels or bodies…",muted);
            if (ControlButton(new Rect(x+w-43,y,43,l.SearchHeight),new GUIContent("X","Clear search"))) { View.SearchText = string.Empty; dirty = true; }
            y += l.SearchHeight+l.Gap;
            var filterWidth = (w-2*l.Gap)/3;
            Dropdown(new Rect(x,y,filterWidth,l.FilterHeight), "Type: " + FilterCaption(View.TypeFilters),"Type");
            Dropdown(new Rect(x+filterWidth+l.Gap,y,filterWidth,l.FilterHeight), "State: " + FilterCaption(View.SituationFilters),"Situation");
            Dropdown(new Rect(x+2*(filterWidth+l.Gap),y,filterWidth,l.FilterHeight), "Crew: " + View.Crew,"Crew"); y += l.FilterHeight+l.Gap;
            var quickWidth = (w-2*l.Gap)/3;
            Quick(new Rect(x,y,quickWidth,l.QuickHeight),"All vessels",0); Quick(new Rect(x+quickWidth+l.Gap,y,quickWidth,l.QuickHeight),"Facilities",1); Quick(new Rect(x+2*(quickWidth+l.Gap),y,quickWidth,l.QuickHeight),"Stations",2); y += l.QuickHeight+l.Gap;
            if (l.TwoToolbarRows)
            {
                Dropdown(new Rect(x,y,(w-l.Gap)/2,l.ToolbarHeight),grouped ? "Group: System" : "Group: None","Group");
                Dropdown(new Rect(x+(w+l.Gap)/2,y,(w-l.Gap)/2,l.ToolbarHeight),"Sort: "+View.SortField+(View.SortDescending?" v":" ^"),"Sort"); y += l.ToolbarHeight;
                ExpandControls(new Rect(x,y,w,l.ToolbarHeight)); y += l.ToolbarHeight+l.Gap;
            }
            else
            {
                Dropdown(new Rect(x,y,w*.26f,l.ToolbarHeight),grouped ? "Group: System" : "Group: None","Group");
                Dropdown(new Rect(x+w*.28f,y,w*.27f,l.ToolbarHeight),"Sort: "+View.SortField+(View.SortDescending?" v":" ^"),"Sort");
                ExpandControls(new Rect(x+w*.57f,y,w*.43f,l.ToolbarHeight)); y += l.ToolbarHeight+l.Gap;
            }
            }
            var tree = new Rect(x,y,w,Math.Max(28,l.TreeBottom-y));
            treeScroll = GUI.BeginScrollView(tree,treeScroll,new Rect(0,0,w-18,rows.Count*l.RowHeight));
            try
            {
                var first = Math.Max(0,(int)(treeScroll.y/l.RowHeight)-1); var last = Math.Min(rows.Count,(int)Math.Ceiling((treeScroll.y+tree.height)/l.RowHeight)+1);
                for (var i=first;i<last;i++) DrawRow(rows[i],i,w-18);
            }
            finally { GUI.EndScrollView(); }
            if (result == null || result.MatchCount == 0)
                GUI.Label(new Rect(x+8,y+7,w-18,48),result == null ? "Loading fleet records…" : result.TotalInScope == 0 ? "No vessels in this scope" : "No vessels match these filters",wrapped);
            Fill(new Rect(x,l.TreeBottom,w,1),Line);
            var count = result == null ? "…" : result.MatchCount + " / " + result.TotalInScope + " vessels";
            GUI.Label(new Rect(x,l.TreeBottom+7,w*.54f,25),new GUIContent(count,ScopeName()+"; matching / total"),text);
            if (ControlButton(new Rect(x+w*.54f,l.TreeBottom+5,w*.46f,32),new GUIContent(View.TypeFilters.SetEquals(new[]{"Debris"}) ? "[x] Debris" : "[ ] Debris","Quick filter: Debris only. Type dropdown includes flags and every other type."))) { if (!View.TypeFilters.Remove("Debris")) { View.TypeFilters.Clear(); View.TypeFilters.Add("Debris"); } dirty=true; }
            var summary = "Filters: list only  ·  " + ScopeName();
            GUI.Label(new Rect(x,l.TreeBottom+35,w,26),new GUIContent(summary,ActiveFilters()),muted);
        }
        private bool ControlButton(Rect rect, string caption, GUIStyle style = null)
        { return ControlButton(rect, new GUIContent(caption), style); }
        private bool ControlButton(Rect rect, GUIContent content, GUIStyle style = null)
        {
            var fitted = style ?? privateSkin.button;
            if (fitted.CalcSize(content).x > rect.width - 3)
            {
                fitted = new GUIStyle(fitted);
                while (fitted.fontSize > 11 && fitted.CalcSize(content).x > rect.width - 3) fitted.fontSize--;
            }
            return GUI.Button(rect, content, fitted);
        }
        private void Dropdown(Rect rect,string caption,string key)
        {
            var display=caption+"  v";
            if(privateSkin.button.CalcSize(new GUIContent(display)).x>rect.width-4)display=(key=="Situation"?"State":key)+" v";
            if (ControlButton(rect,new GUIContent(display,caption+"; open "+key+" choices"))) { menu = menu==key ? string.Empty : key; menuAnchor=rect; menuScroll=Vector2.zero; GUI.FocusControl(null); }
        }
        private string FilterCaption(ISet<string> values) { return values.Count==0 ? "All" : values.Count==1 ? values.First() : values.Count+" selected"; }
        private void Quick(Rect rect,string caption,int kind)
        {
            var active = kind==0 ? View.IsAllScope && View.TypeFilters.Count==0 && View.SituationFilters.Count==0 && View.Crew==CrewFilter.Any : kind==1 ? View.TypeFilters.SetEquals(new[]{"Base","Rover"}) && View.SituationFilters.SetEquals(new[]{"LANDED","SPLASHED"}) : View.TypeFilters.SetEquals(new[]{"Station"});
            if (active) Fill(rect,Teal);
            var display=caption;
            if(privateSkin.button.CalcSize(new GUIContent(display)).x>rect.width-4)display=kind==0?"All":kind==1?"Surface":"Stations";
            if (ControlButton(rect,new GUIContent(display,kind==1?"Facilities: Base or Rover, Landed or Splashed":caption),active?tealButton:privateSkin.button))
            { View.ScopeBodyKey=TrackingView.AllScope; View.SearchText=string.Empty; View.TypeFilters.Clear(); View.SituationFilters.Clear(); View.Crew=CrewFilter.Any; if(kind==1){View.TypeFilters.Add("Base");View.TypeFilters.Add("Rover");View.SituationFilters.Add("LANDED");View.SituationFilters.Add("SPLASHED");} if(kind==2)View.TypeFilters.Add("Station"); dirty=true; menu=string.Empty; }
        }
        private void ExpandControls(Rect rect)
        {
            var old=GUI.enabled; GUI.enabled=old && string.IsNullOrWhiteSpace(View.SearchText);
            try { if(ControlButton(new Rect(rect.x,rect.y,rect.width/2-3,rect.height),new GUIContent("Expand","Expand current scope"))) ExpandScope(true); if(ControlButton(new Rect(rect.x+rect.width/2+3,rect.y,rect.width/2-3,rect.height),new GUIContent("Collapse","Collapse current scope"))) ExpandScope(false); }
            finally { GUI.enabled=old; }
        }
        private void DrawRow(Row row,int index,float width)
        {
            var l=layout; var rect=new Rect(0,index*l.RowHeight,width,l.RowHeight-2);
            var active = row.Branch!=null ? string.Equals(View.ScopeBodyKey,row.Branch.Body.Summary.Key,StringComparison.OrdinalIgnoreCase) : row.Vessel.Id==SelectedVesselId;
            if(active)Fill(rect,Teal); else if(index%2==0)Fill(rect,new Color(.055f,.09f,.11f,1));
            var indent=Math.Min(row.Depth*(l.Compact?15:22),width*.20f);
            if(row.Branch!=null)
            {
                var b=row.Branch; var body=b.Body; var system=body.Summary.IsStar || body.Children.Count>0;
                var enabled=GUI.enabled; GUI.enabled=enabled && string.IsNullOrWhiteSpace(View.SearchText);
                try { if(GUI.Button(new Rect(indent,rect.y+2,32,l.RowHeight-6),b.IsExpanded?"-":">",rowStyle))Toggle(View.ExpandedBodyKeys,body.Summary.Key); }
                finally{GUI.enabled=enabled;}
                var count=b.MatchCount+" / "+b.TotalCount;
                var name=body.Summary.Name+(system?" system":"");
                var exactWidth=system?Math.Max(56,privateSkin.button.CalcSize(new GUIContent("Body")).x+4):0;
                var countWidth=Math.Min(width*.30f,muted.CalcSize(new GUIContent(count)).x+4);
                if(GUI.Button(new Rect(indent+35,rect.y+2,Math.Max(50,width-indent-countWidth-exactWidth-43),l.RowHeight-6),new GUIContent(name,name+"; subtree "+count),active?selectedRow:rowStyle)) { View.ScopeBodyKey=body.Summary.Key; View.IncludeDescendants=system; dirty=true; if(FocusFollowsSelection && BodyFocusRequested!=null)BodyFocusRequested(body.Summary.Key); }
                if(system && GUI.Button(new Rect(width-countWidth-exactWidth-4,rect.y+2,exactWidth,l.RowHeight-6),new GUIContent("Body","Only vessels at "+body.Summary.Name))) { View.ScopeBodyKey=body.Summary.Key;View.IncludeDescendants=false;dirty=true; }
                GUI.Label(new Rect(width-countWidth,rect.y+2,countWidth,l.RowHeight-6),count,muted); return;
            }
            var v=row.Vessel;
            if(GUI.Button(new Rect(indent+22,rect.y+2,width-indent-28,l.RowHeight-6),new GUIContent(v.Name,v.Name+"\n"+v.Type+" · "+v.Situation),active?selectedRow:rowStyle)) { SelectedVesselId=v.Id;cardScroll=Vector2.zero;if(VesselSelected!=null)VesselSelected(v.Id);if(FocusFollowsSelection && FocusRequested!=null)FocusRequested(v.Id); }
        }
        private void DrawSelectionCard()
        {
            var l=layout; cardRect=new Rect(l.CardLeft,l.CardTop,l.CardWidth,l.CardHeight); Fill(cardRect,Panel); Border(cardRect,Line);
            var v=vessels.FirstOrDefault(item=>item.Id==SelectedVesselId); var p=l.Padding; var contentWidth=cardRect.width-(l.Compact?170:210)-2*p;
            var name=v==null ? ScopeName() : v.Name;
            var details=v==null ? (result==null?"Loading vessel summaries":result.MatchCount+" matching / "+result.TotalInScope+" vessels in scope") : BodyName(v.BodyKey)+"\n"+v.Type+" · "+v.Situation+" · Crew: "+(v.CrewKnown?v.CrewCount.ToString():"unknown");
            var viewRect=new Rect(cardRect.x+p,cardRect.y+p,contentWidth,cardRect.height-2*p-44);
            var nameHeight=cardTitle.CalcHeight(new GUIContent(name),contentWidth-18);var textHeight=wrapped.CalcHeight(new GUIContent(details),contentWidth-18);
            cardScroll=GUI.BeginScrollView(viewRect,cardScroll,new Rect(0,0,contentWidth-18,nameHeight+textHeight+12));
            try {GUI.Label(new Rect(0,0,contentWidth-18,nameHeight),name,cardTitle);GUI.Label(new Rect(0,nameHeight+10,contentWidth-18,textHeight),details,wrapped);}
            finally{GUI.EndScrollView();}
            var bx=cardRect.xMax-(l.Compact?150:185)-p; var bw=l.Compact?150:185;
            var enabled=GUI.enabled;GUI.enabled=enabled&&ActionsAvailable;
            try
            {
                if(v!=null)
                {
                    if(ControlButton(new Rect(bx,cardRect.y+p,bw,34),"Focus vessel",tealButton)&&FocusRequested!=null)FocusRequested(v.Id);
                    if(ControlButton(new Rect(bx,cardRect.y+p+42,bw,34),"Fly",tealButton)&&FlyRequested!=null)FlyRequested(v.Id);
                    var actionWidth=(cardRect.width-2*p-16)/3;var actionY=cardRect.yMax-44;
                    var names=new[]{"Recover","Track","End / terminate"};var actions=new[]{"Recover","Track","Terminate"};
                    for(var i=0;i<names.Length;i++)if(ControlButton(new Rect(cardRect.x+p+i*(actionWidth+8),actionY,actionWidth,34),new GUIContent(names[i],"Uses stock eligibility and stock confirmation: "+names[i]))&&NativeActionRequested!=null)NativeActionRequested(v.Id,actions[i]);
                }
                else if(!View.IsAllScope)
                {
                    if(ControlButton(new Rect(bx,cardRect.y+p,bw,34),View.IncludeDescendants?"Focus system":"Focus body",tealButton)&&BodyFocusRequested!=null)BodyFocusRequested(View.ScopeBodyKey);
                }
                else GUI.Label(new Rect(bx,cardRect.y+p,bw,70),"Select a vessel or system to inspect",wrapped);
            }
            finally{GUI.enabled=enabled;}
        }
        private void DrawFooter()
        {
            var l=layout; var y=l.Height-l.FooterHeight; Fill(new Rect(0,y,l.Width,l.FooterHeight),Panel); Fill(new Rect(0,y,l.Width,1),Line);
            var bh=32;var by=y+(l.FooterHeight-bh)/2;
            if(ControlButton(new Rect(l.Padding,by,l.Compact?140:180,bh),"Space center")&&LeaveRequested!=null)LeaveRequested();
            var x=l.Width-l.Padding;
            x-=l.Compact?112:155;Dropdown(new Rect(x,by,l.Compact?112:155,bh),"Scale "+Math.Round(Scale*100)+"%","Scale");
            x-=l.Gap+(l.Compact?75:105);Dropdown(new Rect(x,by,l.Compact?75:105,bh),"Views","Views");
            var followWidth=l.Compact?140:285;x-=l.Gap+followWidth;
            if(ControlButton(new Rect(x,by,followWidth,bh),new GUIContent((FocusFollowsSelection?"[x] ":"[ ] ")+(l.Compact?"Follow focus":"Focus follows selection"),"Optional map focus on selection. Never flies.")))FocusFollowsSelection=!FocusFollowsSelection;
            var statusX=l.Padding+(l.Compact?150:200);var statusWidth=Math.Max(20,x-statusX-l.Gap);
            GUI.Label(new Rect(statusX,y+8,statusWidth,l.FooterHeight-16),new GUIContent(status.Length==0?"TRACKING STATION  /  LIVE MAP · RECORDED VESSELS":status,status),muted);
        }
        private void DrawMenu()
        {
            if(string.IsNullOrEmpty(menu))return;
            var l=layout;var itemHeight=l.Compact?34:42;var values=MenuValues();
            var wantedWidth=Math.Max(300,values.Length==0?360:values.Max(v=>privateSkin.button.CalcSize(new GUIContent(MenuCaption(v))).x+58));
            var width=Math.Min(l.Width-2*l.Padding,Math.Min(menu=="Views"?520:l.SidebarWidth-2*l.Padding,wantedWidth));
            var height=Math.Min(l.Height-l.HeaderHeight-l.FooterHeight-20,68+values.Length*itemHeight+(menu=="Views"?85:0));
            var x=Math.Max(l.Padding,Math.Min(menuAnchor.x,l.Width-width-l.Padding));var y=Math.Min(menuAnchor.yMax+6,l.Height-l.FooterHeight-height-8);y=Math.Max(l.HeaderHeight+8,y);
            menuRect=new Rect(x,y,width,height);Fill(menuRect,Panel);Border(menuRect,new Color(.16f,.60f,.61f));
            GUI.Label(new Rect(x+12,y+7,width-65,28),menu+" filters / view",section);
            if(ControlButton(new Rect(x+width-43,y+5,34,30),"X")){menu=string.Empty;return;}
            var viewport=new Rect(x+10,y+42,width-20,height-50);var contentHeight=values.Length*itemHeight+(menu=="Views"?82:0);
            menuScroll=GUI.BeginScrollView(viewport,menuScroll,new Rect(0,0,viewport.width-18,contentHeight));
            try
            {
                var cy=0f;var cw=viewport.width-18;
                if(menu=="Views")
                {
                    GUI.SetNextControlName("tracking-view-name");viewName=GUI.TextField(new Rect(0,cy,Math.Max(70,cw-110),34),viewName,64);
                    if(ControlButton(new Rect(cw-105,cy,105,34),"Save view"))SaveView();cy+=44;
                }
                foreach(var value in values)
                {
                    if(menu=="Views")
                    {if(ControlButton(new Rect(0,cy,cw-68,itemHeight-4),new GUIContent(value,value))){var saved=NamedViews.FirstOrDefault(v=>v.Name==value);if(saved!=null){ApplyView(saved.ToView());grouped=saved.GroupByBody;}}if(ControlButton(new Rect(cw-64,cy,64,itemHeight-4),"Delete")){NamedViews.RemoveAll(v=>v.Name==value);}}
                    else if(ControlButton(new Rect(0,cy,cw,itemHeight-4),new GUIContent(MenuCaption(value),value)))ApplyMenu(value);
                    cy+=itemHeight;
                }
            }
            finally{GUI.EndScrollView();}
        }
        private string[] MenuValues()
        {
            switch(menu){case "Type":return new[]{"Any"}.Concat(types).ToArray();case "Situation":return new[]{"Any"}.Concat(situations).ToArray();case "Crew":return Enum.GetNames(typeof(CrewFilter));case "Sort":return Enum.GetNames(typeof(TrackingSortField));case "Group":return new[]{"System","None","Show empty branches","Clear filters"};case "Scale":return new[]{"75%","85%","100%","125%","150%","200%"};case "Views":return(NamedViews??new List<SavedTrackingView>()).Select(v=>v.Name).ToArray();default:return new string[0];}
        }
        private string MenuCaption(string value)
        {var selected=menu=="Type"?value=="Any"?View.TypeFilters.Count==0:View.TypeFilters.Contains(value):menu=="Situation"?value=="Any"?View.SituationFilters.Count==0:View.SituationFilters.Contains(value):menu=="Crew"?View.Crew.ToString()==value:menu=="Sort"?View.SortField.ToString()==value:menu=="Group"?value=="System"?grouped:value=="None"?!grouped:value=="Show empty branches"&&View.ShowEmpty:false;return(selected?"[x] ":"[ ] ")+value+(menu=="Sort"&&selected?(View.SortDescending?" v":" ^"):string.Empty);}
        private void ApplyMenu(string value)
        {
            if(menu=="Type"){if(value=="Any")View.TypeFilters.Clear();else Toggle(View.TypeFilters,value);}
            else if(menu=="Situation"){if(value=="Any")View.SituationFilters.Clear();else Toggle(View.SituationFilters,value);}
            else if(menu=="Crew")View.Crew=(CrewFilter)Enum.Parse(typeof(CrewFilter),value);
            else if(menu=="Sort"){var field=(TrackingSortField)Enum.Parse(typeof(TrackingSortField),value);View.SortDescending=View.SortField==field&&!View.SortDescending;View.SortField=field;}
            else if(menu=="Group"){if(value=="System")grouped=true;else if(value=="None")grouped=false;else if(value=="Show empty branches")View.ShowEmpty=!View.ShowEmpty;else{View.TypeFilters.Clear();View.SituationFilters.Clear();View.Crew=CrewFilter.Any;}}
            else if(menu=="Scale"){Scale=float.Parse(value.TrimEnd('%'),System.Globalization.CultureInfo.InvariantCulture)/100f;menu=string.Empty;}
            dirty=true;
        }
        private void SaveView()
        {var name=viewName.Trim();if(name.Length==0){status="Enter a view name first.";return;}if(NamedViews==null)NamedViews=new List<SavedTrackingView>();var i=NamedViews.FindIndex(v=>string.Equals(v.Name,name,StringComparison.OrdinalIgnoreCase));if(i<0&&NamedViews.Count>=24){status="Delete a named view first (24 maximum).";return;}var saved=SavedTrackingView.FromView(name,View);saved.GroupByBody=grouped;if(i>=0)NamedViews[i]=saved;else NamedViews.Add(saved);status="Saved view: "+name;}
        private void EnsureQuery()
        {
            if(!dirty||hierarchy==null)return;result=TrackingQuery.Execute(vessels,hierarchy,View);scopedIds=new HashSet<string>(result.Vessels.Select(v=>v.Id),StringComparer.OrdinalIgnoreCase);
            if(SelectedVesselId!=null&&!scopedIds.Contains(SelectedVesselId)){SelectedVesselId=null;status="Selected vessel is outside the current view.";}
            rows.Clear();if(grouped)foreach(var branch in result.Branches)Flatten(branch,0);else foreach(var vessel in result.Vessels)rows.Add(new Row{Vessel=vessel});dirty=false;
        }
        private void Flatten(BodyBranchResult branch,int depth){if(!branch.IsVisible)return;rows.Add(new Row{Branch=branch,Depth=depth});if(!branch.IsExpanded)return;foreach(var vessel in branch.Vessels)if(scopedIds.Contains(vessel.Id))rows.Add(new Row{Vessel=vessel,Depth=depth+1});foreach(var child in branch.Children)Flatten(child,depth+1);}
        private void Toggle(ISet<string> set,string key){if(!set.Remove(key))set.Add(key);dirty=true;}
        private void ExpandScope(bool expand){if(hierarchy==null||!string.IsNullOrWhiteSpace(View.SearchText))return;foreach(var body in hierarchy.Traverse())if(View.IsAllScope||hierarchy.IsDescendantOrSelf(body.Summary.Key,View.ScopeBodyKey)){View.ExpandedBodyKeys.Remove(body.Summary.Key);if(expand)View.ExpandedBodyKeys.Add(body.Summary.Key);}dirty=true;}
        private string ScopeName(){return View.IsAllScope?"All vessels":BodyName(View.ScopeBodyKey)+(View.IncludeDescendants?" system":" body");}
        private string BodyName(string key){return hierarchy==null||hierarchy.Get(key)==null?"Unassigned body":hierarchy.Get(key).Summary.Name;}
        private string ActiveFilters(){return "Type: "+FilterCaption(View.TypeFilters)+"; situation: "+FilterCaption(View.SituationFilters)+"; crew: "+View.Crew+". Empty selections mean Any; dimensions combine with AND.";}
        private void RequestStock(){Visible=false;menu=string.Empty;IsInputFocused=false;GUI.FocusControl(null);if(StockViewRequested!=null)StockViewRequested();}
        public void Dispose(){if(privateSkin!=null)UnityEngine.Object.Destroy(privateSkin);privateSkin=null;sourceSkin=null;}
        private static Rect Inset(Rect rect,float padding){return new Rect(rect.x+padding,rect.y+padding,rect.width-2*padding,rect.height-2*padding);}
        private static void Fill(Rect rect,Color color){var old=GUI.color;GUI.color=color;GUI.DrawTexture(rect,Texture2D.whiteTexture);GUI.color=old;}
        private static void Border(Rect rect,Color color){Fill(new Rect(rect.x,rect.y,rect.width,1),color);Fill(new Rect(rect.x,rect.yMax-1,rect.width,1),color);Fill(new Rect(rect.x,rect.y,1,rect.height),color);Fill(new Rect(rect.xMax-1,rect.y,1,rect.height),color);}
    }
}

