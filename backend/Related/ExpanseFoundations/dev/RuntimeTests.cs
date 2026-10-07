using System;
using System.Collections;
using System.IO;
using System.Linq;
using Expanse.Foundations;
using UnityEngine;
using Pose = Expanse.Foundations.Pose;

[KSPAddon(KSPAddon.Startup.MainMenu,true)]
public sealed class FoundationsRuntimeTests : MonoBehaviour
{
    string log;
    int checks;
    void Start()
    {
        // This assembly is never included in a distributable package.
        string root=Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\','/');
        if(!root.EndsWith("KSP-RMM-Dev",StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(root,"foundations-test-request.txt")))return;
        DontDestroyOnLoad(this);AudioListener.volume=0;
        log=Path.Combine(root,"foundations-tests-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".txt");File.WriteAllText(log,"START "+DateTime.UtcNow.ToString("O")+"\n");
        StartCoroutine(Supervise(Run()));
    }
    void Line(string s){File.AppendAllText(log,s+"\n");Debug.Log("[FoundationsTest] "+s);}
    void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    IEnumerator Supervise(IEnumerator inner)
    {
        while(true)
        {
            object next=null;bool more=false;
            try {more=inner.MoveNext();if(more)next=inner.Current;}
            catch(Exception e){Line("FAIL "+e);Application.Quit();yield break;}
            if(!more)yield break;yield return next;
        }
    }
    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(6);
        Check(Hold.HooksReady,"Harmony hooks missing");Line("Hooks installed");
        var a=new Anchor {Id=Guid.NewGuid().ToString(),Body="Minmus",BodyRadius=60000,Name="Test",ReferenceId=42,Revision=1,
            Surface=new Pose(new DVector(60001.12345678,1,3),new DRotation(.1,.2,.3,.9).Unit())};
        a.Members.Add(new Member {Id=42,FlightId=43,Local=new Pose(new DVector(0,0,0),DRotation.Identity),TetherWasOn=true});
        string exact=a.Save().ToString();
        for(int i=0;i<1000;i++){a=Anchor.Load(ConfigNode.Parse(a.Save().ToString()).GetNode("ANCHOR"));Check(a.Save().ToString()==exact,"KSP ConfigNode round trip changed pose");}
        Line("PASS 1000 native ConfigNode round trips");
        if(File.ReadAllText(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt")).Contains("Resume"))
        {
            HighLogic.SaveFolder="Foundations-Test";
            var resumeGame=GamePersistence.LoadGame("foundation-roundtrip",HighLogic.SaveFolder,true,false);
            Check(resumeGame!=null,"No recorded foundation test save");
            HighLogic.CurrentGame=resumeGame;
            resumeGame.startScene=GameScenes.FLIGHT;
            resumeGame.Start();
            yield return new WaitForSecondsRealtime(15);
            var resumeVessel=FlightGlobals.ActiveVessel;var resumeBinding=Hold.Get(resumeVessel);
            Check(resumeBinding!=null&&resumeVessel.packed,"Cold-start anchor did not restore");
            string stable=resumeBinding.Anchor.Save().ToString();
            for(int i=0;i<300;i++)
            {
                yield return new WaitForFixedUpdate();
                Check(resumeVessel.packed&&resumeVessel.parts.All(p=>p.rb==null||p.rb.isKinematic),"Cold-start structure became dynamic");
                Check(resumeBinding.Anchor.Save().ToString()==stable,"Cold-start anchor changed");
            }
            Line("PASS fresh-process anchor restore and 300 held physics steps");
            Line("Placed-part error: "+resumeBinding.AppliedPositionError+" m, "+resumeBinding.AppliedAngleError+" degrees");
            Check(resumeBinding.AppliedPositionError<=.001&&resumeBinding.AppliedAngleError<=.0001,"Cold-start precision budget exceeded");
            var resumeWindow=UnityEngine.Object.FindObjectOfType<FoundationWindow>();
            if(resumeWindow!=null)typeof(FoundationWindow).GetField("open",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(resumeWindow,true);
            yield return new WaitForSecondsRealtime(1);
            UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test.png"));
            yield return new WaitForSecondsRealtime(2);
            Check(Hold.Release(resumeVessel),"Cold-start release failed");
            yield return new WaitForSecondsRealtime(3);
            Check(!resumeVessel.packed&&!Hold.IsHeld(resumeVessel)&&resumeVessel.srfSpeed<.5,"Cold-start release impulse or hold remains");
            Line("PASS cold-start release; speed="+resumeVessel.srfSpeed+" m/s; assertions="+checks);
            File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));Application.Quit();yield break;
        }
        HighLogic.CurrentGame=GamePersistence.CreateNewGame("Foundations-Test",Game.Modes.SANDBOX,new GameParameters(),"Squad/Flags/default",GameScenes.SPACECENTER,EditorFacility.VAB);
        HighLogic.CurrentGame.Start();
        float deadline=Time.realtimeSinceStartup+90;
        while(!HighLogic.LoadedSceneIsGame||HighLogic.LoadedScene!=GameScenes.SPACECENTER){Check(Time.realtimeSinceStartup<deadline,"Space Center timeout");yield return null;}
        yield return new WaitForSecondsRealtime(3);
        string fixture=Path.Combine(KSPUtil.ApplicationRootPath,"foundations-fixture.craft");
        string requested=File.ReadAllText(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));
        ReduceStockFixture(fixture,requested.Contains("Docking"));
        FlightDriver.StartWithNewLaunch(fixture,"Squad/Flags/default","LaunchPad",new VesselCrewManifest());
        deadline=Time.realtimeSinceStartup+180;
        Vessel v=null;
        while(v==null||v.packed||!v.loaded||v.parts.Any(p=>!p.started))
        {Check(Time.realtimeSinceStartup<deadline,"Fixture load timeout");yield return null;v=FlightGlobals.ActiveVessel;}
        if(File.ReadAllText(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt")).Contains("Minmus"))
        {
            yield return new WaitForSecondsRealtime(5);
            int minmus=FlightGlobals.Bodies.FindIndex(body=>body.bodyName=="Minmus");
            Check(minmus>=0,"Minmus missing");
            FlightGlobals.fetch.SetVesselPosition(minmus,1,1,20,0,0,true,true,0.1);
            yield return new WaitForSecondsRealtime(20);
            v=FlightGlobals.ActiveVessel;
            Check(v.mainBody.bodyName=="Minmus","Minmus placement failed");
            Line("Fixture placed on Minmus with stock surface-placement easing.");
        }
        yield return new WaitForSecondsRealtime(8);
        deadline=Time.realtimeSinceStartup+90;
        var gate=new Settler();float nextReport=0;bool settled=false;
        while(!settled)
        {
            Check(Time.realtimeSinceStartup<deadline,"Fixture did not settle: "+Hold.Eligibility(v)+" speed="+v.srfSpeed+" angular="+v.angularVelocity.magnitude);
            var reason=Hold.Eligibility(v);
            settled=gate.Ready(Hold.BodyWorld(v.mainBody).Inverse*Hold.World(v.rootPart),Time.realtimeSinceStartup,reason==null,Settings.SettleSeconds,Settings.SettleAngle);
            if(Time.realtimeSinceStartup>nextReport){Line("Settling: "+reason+" speed="+v.srfSpeed+" angular="+v.angularVelocity.magnitude);nextReport=Time.realtimeSinceStartup+10;}
            yield return null;
        }
        Line("Fixture loaded: "+v.vesselName+" parts="+v.parts.Count+" marker="+(Hold.Marker(v)!=null));
        if(File.ReadAllText(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt")).Contains("FreeTopology"))
        {
            var freePanel=v.FindPartModulesImplementing<ModuleDeployableSolarPanel>()[0].part;
            freePanel.decouple();yield return new WaitForSecondsRealtime(1);
            var freeBase=v;
            Line("Free split: part valid="+(freePanel!=null)+" base="+freeBase.parts.Count);
            freePanel.Couple(freeBase.rootPart);yield return new WaitForSecondsRealtime(1);
            Line("Free join: part valid="+(freePanel!=null)+" count="+freeBase.parts.Count+" entries="+string.Join(",",freeBase.parts.Select(p=>p==null?"null":p.persistentId.ToString()).ToArray()));
            File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));Application.Quit();yield break;
        }
        Check(Hold.AnchorBase(v),"Could not anchor fixture: "+Hold.LastMessage);
        var b=Hold.Get(v);string pose=b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation;
        Check(b.Members.Count==v.parts.Count,"Membership mismatch");
        for(int i=0;i<300;i++)
        {
            yield return new WaitForFixedUpdate();
            Check(v.packed,"Held vessel unpacked");
            Check(v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Held member became dynamic");
            Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Anchor mutated");
        }
        Line("PASS loaded fixed-step hold");
        if(requested.Contains("StockConstruction"))
        {
            var removed=v.FindPartModulesImplementing<ModuleDeployableSolarPanel>()[0].part;
            Check(removed.FindModuleImplementing<ModuleCargoPart>()!=null,"Stock construction fixture is not cargo-capable");
            int oldCount=v.parts.Count;
            var position=removed.transform.position;var rotation=removed.transform.rotation;
            removed.OnDetachFlight();
            yield return new WaitForSecondsRealtime(1);
            Check(v.parts.Count==oldCount-1&&Hold.Get(v)!=null&&Hold.Get(v).Anchor.Members.Count==oldCount-1,"Stock EVA detachment did not update foundation");
            Line("PASS stock EVA construction detach path");
            var attached=removed.protoPartSnapshot.CreatePart();
            attached.isAttached=true;attached.partInfo=removed.partInfo;
            attached.transform.position=position;attached.transform.rotation=rotation;
            attached.OnAttachFlight(v.rootPart);
            Check(attached.rb==null||attached.rb.isKinematic,"New EVA part became dynamic during attachment");
            yield return new WaitForSecondsRealtime(2);
            b=Hold.Get(v);
            Check(b!=null&&v.packed&&v.parts.Count==oldCount&&b.Anchor.Members.Count==oldCount,"Stock EVA attach did not update foundation");
            Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Stock EVA construction moved anchor");
            Check(v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Stock EVA attach left dynamic part");
            Line("PASS stock EVA construction attachment; assertions="+checks);
            File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));Application.Quit();yield break;
        }
        if(requested.Contains("Docking"))
        {
            var ports=v.FindPartModulesImplementing<ModuleDockingNode>();
            Check(ports.Count==2,"Docking fixture needs two ports");
            // The first stock port faces skyward; the second faces through the
            // ground after this station subtree is landed on Minmus.
            var visitingPort=requested.Contains("DockingFSM")?ports[1]:ports[0];
            var basePort=requested.Contains("DockingFSM")?ports[0]:ports[1];
            if(requested.Contains("DockingFSM"))
            {
                var normal=v.mainBody.GetSurfaceNVector(v.latitude,v.longitude);
                Line("Initial ports: first relative="+Vector3.Dot(ports[0].nodeTransform.position-v.rootPart.transform.position,normal)+
                    " forwardUp="+Vector3.Dot(ports[0].nodeTransform.forward,normal)+
                    " second relative="+Vector3.Dot(ports[1].nodeTransform.position-v.rootPart.transform.position,normal)+
                    " forwardUp="+Vector3.Dot(ports[1].nodeTransform.forward,normal)+
                    " vesselAlt="+v.altitude+" terrainAlt="+v.terrainAltitude);
            }
            int originalCount=v.parts.Count;
            visitingPort.part.decouple();
            var visitor=visitingPort.vessel;
            Check(visitor!=v&&visitor.parts.Count==1&&Hold.Get(v)!=null,"Docking visitor did not separate cleanly");
            var up=v.mainBody.GetSurfaceNVector(v.latitude,v.longitude);
            Hold.TransformVessel(visitor,new Pose(Hold.D(up)*3,DRotation.Identity));
            yield return new WaitForSecondsRealtime(0.5f);
            Check(visitingPort.part!=null,"Docking visitor was destroyed");
            int cycles=requested.Contains("DockingCycles")?20:1;
            for(int cycle=0;cycle<cycles;cycle++)
            {
                Check(visitingPort!=null&&visitingPort.nodeTransform!=null,"Visitor port was destroyed before cycle "+cycle);
                var desiredRotation=Quaternion.LookRotation(-basePort.nodeTransform.forward,basePort.nodeTransform.up);
                var turn=desiredRotation*Quaternion.Inverse(visitingPort.nodeTransform.rotation);
                var desiredPosition=basePort.nodeTransform.position+basePort.nodeTransform.forward*.04f;
                var translation=desiredPosition-turn*visitingPort.nodeTransform.position;
                Hold.TransformVessel(visitor,new Pose(Hold.D(translation),Hold.D(turn)));
                if(requested.Contains("DockingFSM"))Line("Aligned visitor: baseAlt="+v.mainBody.GetAltitude(basePort.nodeTransform.position)+
                    " visitorAlt="+v.mainBody.GetAltitude(visitingPort.nodeTransform.position)+
                    " baseForwardUp="+Vector3.Dot(basePort.nodeTransform.forward,up)+
                    " visitorVesselAlt="+visitor.altitude+" terrainAlt="+visitor.terrainAltitude);
                if(visitingPort.part.rb!=null){visitingPort.part.rb.isKinematic=false;visitingPort.part.rb.velocity=Vector3.zero;visitingPort.part.rb.angularVelocity=Vector3.zero;}
                yield return new WaitForFixedUpdate();
                if(cycle==0&&!requested.Contains("DockingFSM"))
                {
                    Check(visitingPort.FindNodeApproaches()==basePort,"Visiting port cannot discover held docking port");
                    Check(basePort.FindNodeApproaches()==visitingPort,"Held docking port cannot discover visitor");
                    Line("PASS two-way docking discovery with packed base");
                }
                if(requested.Contains("DockingFSM"))Line("FSM after alignment: states="+basePort.state+"/"+visitingPort.state+" sameVessel="+(basePort.vessel==visitingPort.vessel));
                bool foundationSource=requested.Contains("DockingSource")||(cycles>1&&cycle%2==1);
                if(requested.Contains("DockingFSM"))
                {
                    float captureDeadline=Time.realtimeSinceStartup+12;
                    float nextFsmReport=0;
                    while(basePort.vessel!=visitingPort.vessel&&Time.realtimeSinceStartup<captureDeadline)
                    {
                        // The tiny, unpowered visitor has no stationkeeping.
                        // Keep its test port aligned so this checks the stock
                        // docking FSM rather than the fixture's orbital drift.
                        if(visitingPort==null||visitingPort.nodeTransform==null){Line("Visitor port was destroyed during FSM test");break;}
                        var aim=Quaternion.LookRotation(-basePort.nodeTransform.forward,basePort.nodeTransform.up);
                        var q=aim*Quaternion.Inverse(visitingPort.nodeTransform.rotation);
                        var p=basePort.nodeTransform.position+basePort.nodeTransform.forward*.04f-q*visitingPort.nodeTransform.position;
                        Hold.TransformVessel(visitingPort.vessel,new Pose(Hold.D(p),Hold.D(q)));
                        if(Time.realtimeSinceStartup>nextFsmReport)
                        {
                            nextFsmReport=Time.realtimeSinceStartup+2;
                            Line("Dock FSM states="+basePort.state+"/"+visitingPort.state+" other="+(basePort.otherNode==visitingPort)+"/"+(visitingPort.otherNode==basePort));
                        }
                        yield return null;
                    }
                    Check(basePort.vessel==visitingPort.vessel,"Stock docking FSM did not capture the held base; states="+basePort.state+"/"+visitingPort.state+
                        " packed="+basePort.part.packed+"/"+visitingPort.part.packed+" vesselPacked="+basePort.vessel.packed+"/"+visitingPort.vessel.packed+
                        " fsmStarted="+basePort.fsm.Started+"/"+visitingPort.fsm.Started+
                        " range="+Vector3.Distance(basePort.nodeTransform.position,visitingPort.nodeTransform.position));
                    Line("PASS stock docking FSM captured held port");
                }
                else if(foundationSource)basePort.DockToVessel(visitingPort);
                else visitingPort.DockToVessel(basePort);
                yield return new WaitForSecondsRealtime(cycles>1?.3f:2f);
                v=basePort.vessel;b=Hold.Get(v);
                Check(b!=null&&v.packed&&v.parts.Count==originalCount,"Docking did not create held combined vessel on cycle "+cycle);
                Check(b.Anchor.Members.Count==v.parts.Count,"Docking membership failed on cycle "+cycle);
                Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Docking changed anchor pose on cycle "+cycle);
                Check(v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Docking left dynamic foundation parts on cycle "+cycle);
                if(requested.Contains("DockingPersistence"))
                {
                    uint visitorId=visitingPort.part.persistentId,baseId=basePort.part.persistentId;
                    GamePersistence.SaveGame("foundation-docked",HighLogic.SaveFolder,SaveMode.OVERWRITE);
                    var savedDock=GamePersistence.LoadGame("foundation-docked",HighLogic.SaveFolder,true,false);
                    FlightDriver.StartAndFocusVessel(savedDock,savedDock.flightState.activeVesselIdx);
                    yield return new WaitForSecondsRealtime(12);
                    v=FlightGlobals.ActiveVessel;b=Hold.Get(v);
                    Check(b!=null&&v.packed&&b.Anchor.Members.Count==originalCount,"Docked vessel did not restore held");
                    Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Docked save changed anchor pose");
                    visitingPort=v.FindPartModulesImplementing<ModuleDockingNode>().First(p=>p.part.persistentId==visitorId);
                    basePort=v.FindPartModulesImplementing<ModuleDockingNode>().First(p=>p.part.persistentId==baseId);
                    Line("PASS docked save/reload retains membership and anchor");
                }
                var childPort=basePort.part.parent==visitingPort.part?basePort:visitingPort;
                Check(childPort.part.parent!=null,"Docking test could not identify the child port");
                childPort.part.Undock(childPort.vesselInfo);
                visitor=visitingPort.vessel;
                Check(!Hold.IsHeld(visitor)&&!visitor.packed&&visitingPort.part.rb!=null&&!visitingPort.part.rb.isKinematic,"Visitor was not released to physics on cycle "+cycle);
                if(cycles>1&&cycle+1<cycles)
                {
                    Hold.TransformVessel(visitor,new Pose(Hold.D(up)*3,DRotation.Identity));
                    visitingPort.part.rb.velocity=Vector3.zero;
                    visitingPort.part.rb.angularVelocity=Vector3.zero;
                    visitingPort.part.rb.isKinematic=true; // fixture parking between cycles
                }
                yield return new WaitForSecondsRealtime(cycles>1?.3f:2f);
                v=basePort.vessel;b=Hold.Get(v);visitor=visitingPort.vessel;
                Check(b!=null&&v.packed&&v.parts.Count==originalCount-1,"Undocking lost foundation on cycle "+cycle);
                Check(!Hold.IsHeld(visitor)&&!visitor.packed,"Visitor remained anchored after undock on cycle "+cycle);
                Check(b.Anchor.Members.Count==v.parts.Count,"Undock membership failed on cycle "+cycle);
                Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Undock changed anchor pose on cycle "+cycle);
                if((cycle+1)%5==0||cycles==1)Line("PASS docking/undocking cycles="+(cycle+1)+"; assertions="+checks);
            }
            File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));Application.Quit();yield break;
        }
        if(File.ReadAllText(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt")).Contains("Topology"))
        {
            var detached=v.FindPartModulesImplementing<ModuleDeployableSolarPanel>()[0].part;
            int originalCount=v.parts.Count;
            detached.decouple();
            var visitor=detached.vessel;
            Check(detached!=null&&visitor!=null,"Detached construction part was destroyed immediately");
            var surfaceUp=v.mainBody.GetSurfaceNVector(v.latitude,v.longitude);
            Hold.TransformVessel(visitor,new Pose(Hold.D(surfaceUp)*3,DRotation.Identity));
            yield return new WaitForSecondsRealtime(1);
            Check(visitor!=v&&v.parts.Count==originalCount-1,"Construction detachment did not split the vessel");
            Check(detached!=null,"Detached construction part was destroyed while awaiting attachment");
            Check(Hold.Get(v)!=null&&v.packed,"Foundation lost hold after part removal");
            Check(!Hold.IsHeld(visitor)&&!visitor.packed,"Detached part remained held");
            Check(Hold.Get(v).Anchor.Members.Count==v.parts.Count,"Anchor membership not pruned");
            Check(Hold.Get(v).Anchor.Surface.Position+"/"+Hold.Get(v).Anchor.Surface.Rotation==pose,"Detachment moved saved pose");
            Line("PASS held construction detachment; visitor physics restored");
            detached.Couple(v.rootPart);
            yield return new WaitForSecondsRealtime(2);
            v=detached.vessel;b=Hold.Get(v);
            Line("After attach: parts="+v.parts.Count+" anchor members="+(b==null?-1:b.Anchor.Members.Count)+" current="+string.Join(",",v.parts.Select(p=>p==null?"null":p.persistentId+":"+p.State).ToArray()));
            Check(b!=null&&v.packed&&v.parts.Count==originalCount,"Construction attachment did not restore held assembly");
            Check(b.Anchor.Members.Count==v.parts.Count,"Anchor membership not extended");
            Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Attachment moved saved pose");
            Check(v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Construction attachment left dynamic foundation parts");
            Line("PASS held construction attachment; anchor pose unchanged; assertions="+checks);
            File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));Application.Quit();yield break;
        }
        double inventory=0;
        foreach(var p in v.parts)foreach(PartResource resource in p.Resources){resource.amount*=0.5;inventory+=resource.amount;}
        v.vesselName="Renamed foundation test";
        yield return new WaitForFixedUpdate();
        Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Rename or inventory change moved anchor");
        Line("PASS rename and resource-mass change preserve anchor; resource units="+inventory);
        v.rootPart.transform.position+=Vector3.right*.25f;
        Hold.Apply(b);
        var expected=Hold.BodyWorld(v.mainBody)*b.Anchor.Surface;
        Check((Hold.World(v.rootPart).Position-expected.Position).Length<0.001,"Position repair exceeded 1 mm");
        for(int i=0;i<100;i++){v.GoOffRails();Check(v.packed&&v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Unpack gate failed");}
        Line("PASS pose correction and 100 explicit unpack attempts");
        var panels=v.FindPartModulesImplementing<ModuleDeployableSolarPanel>();
        foreach(var panel in panels)panel.Extend();
        deadline=Time.realtimeSinceStartup+60;
        while(panels.Any(p=>p.deployState==ModuleDeployablePart.DeployState.EXTENDING)&&Time.realtimeSinceStartup<deadline)yield return null;
        Line("Solar panels on fixture: "+panels.Count+"; states="+string.Join(",",panels.Select(p=>p.deployState.ToString()).ToArray()));
        if(panels.Count>0)Check(panels.All(p=>p.deployState==ModuleDeployablePart.DeployState.EXTENDED),"Solar panel did not extend");
        for(int i=0;i<100;i++)
        {
            TimeWarp.SetRate(5,true);yield return new WaitForSecondsRealtime(1);
            TimeWarp.SetRate(0,true);yield return new WaitForSecondsRealtime(1);
            Check(v.packed&&v.parts.All(p=>p.rb==null||p.rb.isKinematic),"Warp transition enabled dynamics");
            Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Warp changed saved pose");
            if((i+1)%20==0)Line("Warp cycles passed: "+(i+1));
        }
        Line("PASS 100 real high-warp entry/exit cycles");
        Check(panels.All(p=>p.deployState==ModuleDeployablePart.DeployState.EXTENDED),"Warp damaged solar arrays");
        Line("Placed-part error: "+b.AppliedPositionError+" m, "+b.AppliedAngleError+" degrees");
        Check(b.AppliedPositionError<=.001&&b.AppliedAngleError<=.0001,"Placed-part precision exceeds design budget");
        GamePersistence.SaveGame("foundation-roundtrip",HighLogic.SaveFolder,SaveMode.OVERWRITE);
        var saved=GamePersistence.LoadGame("foundation-roundtrip",HighLogic.SaveFolder,true,false);
        FlightDriver.StartAndFocusVessel(saved,saved.flightState.activeVesselIdx);
        yield return new WaitForSecondsRealtime(12);
        v=FlightGlobals.ActiveVessel;b=Hold.Get(v);
        Check(b!=null&&v.packed,"Saved foundation did not load held");
        Check(b.Anchor.Surface.Position+"/"+b.Anchor.Surface.Rotation==pose,"Save/load changed foundation");
        Line("PASS live save/reload");
        var window=UnityEngine.Object.FindObjectOfType<FoundationWindow>();
        if(window!=null)typeof(FoundationWindow).GetField("open",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(window,true);
        UnityEngine.ScreenCapture.CaptureScreenshot(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test.png"));
        yield return new WaitForSecondsRealtime(1);
        Check(Hold.Release(v),"Release failed: "+Hold.LastMessage);
        yield return new WaitForSecondsRealtime(3);
        Check(!v.packed&&!Hold.IsHeld(v),"Release left foundation held");
        Check(v.srfSpeed<0.5,"Release kicked base: "+v.srfSpeed+" m/s");
        Line("PASS release; speed="+v.srfSpeed+" m/s");
        Line("PASS runtime suite; assertions="+checks);
        File.Delete(Path.Combine(KSPUtil.ApplicationRootPath,"foundations-test-request.txt"));
        Application.Quit();
    }
    void ReduceStockFixture(string path,bool docking=false)
    {
        var craft=ConfigNode.Load(path);var original=craft.GetNodes("PART");
        if(!original.Any(p=>p.GetValue("part").StartsWith("largeSolarPanel_")))return;
        // Preserve an exact connected subtree of the local stock craft. No new
        // part positions, normals, or surface attachments are invented here.
        var selectedPorts=original.Where(p=>p.GetValue("part").StartsWith("dockingPort2_")).Take(2).ToArray();
        var kept=original.Where(p=>docking?(p.GetValue("part").StartsWith("mk2LanderCabin.v2_")||p.GetValue("part").StartsWith("crewCabin_")||p.GetValue("part").StartsWith("largeAdapter2_")||p.GetValue("part").StartsWith("advSasModule_")||p.GetValue("part").StartsWith("stationHub_")||selectedPorts.Contains(p)):(p.GetValue("part").StartsWith("mk2LanderCabin.v2_")||p.GetValue("part").StartsWith("crewCabin_")||p.GetValue("part").StartsWith("largeSolarPanel_"))).ToArray();
        var ids=kept.Select(p=>p.GetValue("part")).ToArray();
        Check(kept.Length==(docking?7:4),"Unexpected stock fixture layout");
        foreach(var part in kept)
        {
            foreach(string field in new[]{"link","sym","attN","srfN"})
            {
                var valid=part.GetValues(field).Where(value=>ids.Any(id=>value==id||value.Contains(","+id+"_")||value.EndsWith(","+id))).ToArray();
                part.RemoveValues(field);foreach(string value in valid)part.AddValue(field,value);
            }
        }
        Check(kept.Sum(p=>p.GetValues("link").Length)==kept.Length-1,"Fixture is not a connected tree");
        if(!docking)Check(kept.Skip(2).All(p=>p.GetValue("srfN")!=null),"Panel surface attachments missing");
        var root=kept[0];var cabin=kept[1];
        Check(root.GetValues("link").Contains(cabin.GetValue("part")),"Cabin not linked to command root");
        Check(cabin.GetValues("attN").Any(s=>s.StartsWith("top,"+root.GetValue("part")+"_")),"Cabin top attachment missing");
        craft.RemoveNodes("PART");foreach(var p in kept)craft.AddNode(p);
        craft.SetValue("ship","Foundations solar fixture");craft.Save(path);
        Line("PASS fixture: exact "+kept.Length+"-part stock subtree; connected tree and reciprocal cabin attachment retained.");
    }
}
