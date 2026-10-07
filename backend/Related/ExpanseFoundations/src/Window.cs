using System;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Expanse.Foundations
{
    public static class Settings
    {
        public static double SettleSpeed=0.01,SettleAngle=0.05,SettleSeconds=2;
        public static void Reload()
        {
            var n=ConfigNode.Load(Path.Combine(KSPUtil.ApplicationRootPath,"GameData/ExpanseFoundations/Settings.cfg"));
            n=n==null?null:n.GetNode("FOUNDATIONS");if(n==null)return;
            SettleSpeed=Read(n,"settleSpeed",0.01,0.000001,0.01);
            SettleAngle=Read(n,"settleAngle",0.05,0.000001,0.1);
            SettleSeconds=Read(n,"settleSeconds",2,2,30);
        }
        static double Read(ConfigNode n,string k,double fallback,double min,double max)
        {try{return Math.Max(min,Math.Min(max,Number.Read(n.GetValue(k),1)[0]));}catch{return fallback;}}
    }
    [KSPAddon(KSPAddon.Startup.Flight,false)]
    public sealed class FoundationWindow : MonoBehaviour
    {
        ApplicationLauncherButton button;
        Texture2D icon,background;
        Rect window=new Rect(180,100,450,290);
        bool open,details;
        Vessel settling;
        readonly Settler settleGate=new Settler();
        string status="";
        GUIStyle panel,title,body,muted,action;
        const string LockName="ExpanseFoundations.Window";
        void Start()
        {
            Settings.Reload();GameEvents.onGUIApplicationLauncherReady.Add(AddButton);GameEvents.onVesselWasModified.Add(VesselModified);AddButton();
        }
        void VesselModified(Vessel v) {Hold.Invalidate(v);}
        void AddButton()
        {
            if(button!=null||ApplicationLauncher.Instance==null||!ApplicationLauncher.Ready)return;
            icon=new Texture2D(38,38,TextureFormat.RGBA32,false);
            for(int y=0;y<38;y++)for(int x=0;x<38;x++)
            {
                bool mark=(y>=7&&y<=11&&x>=6&&x<=31)||(x>=16&&x<=21&&y>=11&&y<=29)||(y>=24&&y<=29&&x>=10&&x<=27);
                icon.SetPixel(x,y,mark?new Color(0.35f,0.85f,0.72f):new Color(0.08f,0.12f,0.16f));
            }
            icon.Apply();button=ApplicationLauncher.Instance.AddModApplication(()=>open=true,()=>{open=false;Unlock();},null,null,null,null,ApplicationLauncher.AppScenes.FLIGHT,icon);
        }
        void Update()
        {
            Hold.ProcessPending();
            // Snapshot avoids destruction callbacks changing this collection.
            // Only held loaded bases are visited; no UT-driven whole-save scan.
            if(FlightGlobals.ActiveVessel!=null)Hold.Get(FlightGlobals.ActiveVessel);
            foreach(var b in Hold.Bindings.Values.ToArray())
            {
                if(b.Vessel==null){continue;}
                if(!b.Vessel.loaded)continue;
                Hold.PrepareAdapters(b);
                if(b.Vessel.isActiveVessel)InputLockManager.RemoveControlLock("physicsHold");
            }
            if(settling!=null)
            {
                if(settling!=FlightGlobals.ActiveVessel){settling=null;settleGate.Reset();return;}
                var reason=Hold.Eligibility(settling);
                if(reason!=null){status=reason;settleGate.Reset();}
                else
                {
                    status="Waiting for the base to settle…";
                    if(settleGate.Ready(Hold.BodyWorld(settling.mainBody).Inverse*Hold.World(settling.rootPart),Time.realtimeSinceStartup,true,Settings.SettleSeconds,Settings.SettleAngle))
                    {Hold.AnchorBase(settling);settling=null;settleGate.Reset();status=Hold.LastMessage;}
                }
            }
        }
        void LateUpdate()
        {
            foreach(var b in Hold.Bindings.Values.ToArray())if(b.Vessel!=null&&b.Vessel.loaded)Hold.Apply(b);
        }
        void Styles()
        {
            if(panel!=null)return;
            background=new Texture2D(1,1);background.SetPixel(0,0,new Color(0.07f,0.095f,0.12f,0.98f));background.Apply();
            panel=new GUIStyle(GUI.skin.window){padding=new RectOffset(18,18,12,16)};panel.normal.background=background;
            title=new GUIStyle(GUI.skin.label){fontSize=19,fontStyle=FontStyle.Bold,wordWrap=true};title.normal.textColor=new Color(0.4f,0.9f,0.76f);
            body=new GUIStyle(GUI.skin.label){fontSize=14,wordWrap=true};body.normal.textColor=new Color(0.91f,0.94f,0.97f);
            muted=new GUIStyle(body){fontSize=12};muted.normal.textColor=new Color(0.67f,0.74f,0.8f);
            action=new GUIStyle(GUI.skin.button){fontSize=14,fixedHeight=34};
        }
        void OnGUI()
        {
            if(!open){Unlock();return;}
            Styles();window=GUILayout.Window(10947231,window,Draw,"",panel,GUILayout.Width(450));
            if(window.Contains(new Vector2(Input.mousePosition.x,Screen.height-Input.mousePosition.y)))
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS,LockName);
            else Unlock();
        }
        void Draw(int id)
        {
            GUILayout.BeginHorizontal();GUILayout.Label("EXPANSE FOUNDATIONS",title);
            if(GUILayout.Button("×",GUILayout.Width(28))){open=false;Unlock();}GUILayout.EndHorizontal();
            var v=FlightGlobals.ActiveVessel;var b=Hold.Get(v);
            GUILayout.Label(v==null?"Select a base":v.vesselName,body);
            GUILayout.Label("Development preview · 0.2.0",muted);GUILayout.Space(10);
            if(b!=null)
            {
                GUILayout.Label(b.Problem!=""?b.Problem:"Anchored · Position and orientation held",body);
                if(GUILayout.Button("Release base",action))Hold.Release(v);
                details=GUILayout.Toggle(details,"Show measured pose and anchor details");
                if(details)
                {
                    GUILayout.Label("Body: "+b.Anchor.Body+" · "+b.Members.Count+" fixed parts",muted);
                    GUILayout.Label("Before last correction: "+(b.PositionError*1000).ToString("F4")+" mm · "+b.AngleError.ToString("F6")+"°",muted);
                    GUILayout.Label("Largest placed-part error: "+(b.AppliedPositionError*1000).ToString("F4")+" mm · "+b.AppliedAngleError.ToString("F6")+"°",muted);
                    GUILayout.Label("Saved pose stays unchanged. Large frame-origin changes can affect the pre-correction reading.",muted);
                    if(GUILayout.Button("Export measurements"))Export(b);
                }
            }
            else if(Hold.IsHeld(v))GUILayout.Label("Waiting for saved anchor data. Physics remains held.",body);
            else if(settling!=null)
            {GUILayout.Label(status,body);if(GUILayout.Button("Cancel",action)){settling=null;settleGate.Reset();}}
            else
            {GUILayout.Label("Hold this base at its settled surface position.",body);if(GUILayout.Button("Anchor base",action)){settling=v;settleGate.Reset();}}
            GUILayout.Space(8);
            GUILayout.Label("Stock docking and EVA part construction are enabled. Joining two anchored bases, KAS links, robotics and modded construction are not supported yet.",muted);
            GUILayout.Label(Hold.LastMessage,muted);
            if(GUILayout.Button("Reload settling settings")){Settings.Reload();Hold.Say("Settling settings reloaded.");}
            GUI.DragWindow(new Rect(0,0,450,45));
        }
        static void Export(Binding b)
        {
            var folder=Path.Combine(KSPUtil.ApplicationRootPath,"Logs/ExpanseFoundations");Directory.CreateDirectory(folder);
            var n=b.Anchor.Save();n.AddValue("sampleUT",Number.Write(Planetarium.GetUniversalTime()));n.AddValue("positionErrorMetres",Number.Write(b.PositionError));n.AddValue("angleErrorDegrees",Number.Write(b.AngleError));
            string name="anchor-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            n.Save(Path.Combine(folder,name+".cfg"));
            File.WriteAllLines(Path.Combine(folder,name+".csv"),new[]{"realSeconds,universalTime,maxPartPositionErrorMetres,maxPartAngleErrorDegrees"}.Concat(b.Samples).ToArray());
            Hold.Say("Measurement exported to Logs/ExpanseFoundations.");
        }
        static void Unlock(){InputLockManager.RemoveControlLock(LockName);}
        void OnDestroy()
        {
            Unlock();GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);GameEvents.onVesselWasModified.Remove(VesselModified);
            if(button!=null&&ApplicationLauncher.Instance!=null)ApplicationLauncher.Instance.RemoveModApplication(button);
            if(icon!=null)Destroy(icon);if(background!=null)Destroy(background);
        }
    }
}
