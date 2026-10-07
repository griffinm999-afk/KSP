using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Expanse.Foundations
{
    // A marker is saved by stock VesselModule persistence. It carries identity
    // only. All coordinates live in the save's Scenario, never a sidecar file.
    public sealed class FoundationMarker : VesselModule
    {
        [KSPField(isPersistant=true)] public string foundationId="";
        public override bool ShouldBeActive() {return HighLogic.LoadedSceneIsFlight&&vessel.loaded;}
        public override void OnLoadVessel() {Hold.Invalidate(vessel);}
        public override void OnUnloadVessel() {Hold.Invalidate(vessel);}
        public void OnDestroy() {Hold.Invalidate(vessel);}
    }
    public sealed class Binding
    {
        public Vessel Vessel;
        public Anchor Anchor;
        public Part ReferencePart;
        public readonly Dictionary<Part,Member> Members=new Dictionary<Part,Member>();
        public readonly List<Part> Order=new List<Part>();
        public bool AdaptersReady;
        public string Problem="";
        public double PositionError,AngleError,MaxPositionError;
        public double AppliedPositionError,AppliedAngleError;
        public readonly Queue<string> Samples=new Queue<string>();
        public float NextSample;
    }
    public static class Hold
    {
        public static readonly Dictionary<Vessel,Binding> Bindings=new Dictionary<Vessel,Binding>();
        static readonly HashSet<Vessel> freeVessels=new HashSet<Vessel>();
        static readonly HashSet<Vessel> releasing=new HashSet<Vessel>();
        static readonly Dictionary<string,int> pendingReconcile=new Dictionary<string,int>();
        public static Part AllowedUnpack;
        public static string LastMessage="Ready";
        public static bool HooksReady;
        static readonly FieldInfo Frames=typeof(Vessel).GetField("framesAtStartup",BindingFlags.NonPublic|BindingFlags.Instance);
        static readonly FieldInfo Override=typeof(Vessel).GetField("useFramesAtStartupOverride",BindingFlags.NonPublic|BindingFlags.Instance);
        static readonly FieldInfo WheelSetup=typeof(ModuleWheelBase).GetField("setup",BindingFlags.NonPublic|BindingFlags.Instance);
        static readonly MethodInfo WheelInit=typeof(ModuleWheelBase).GetMethod("wheelSetup",BindingFlags.NonPublic|BindingFlags.Instance);
        public static void Say(string text) {LastMessage=text;Debug.Log("[Foundations] "+text);ScreenMessages.PostScreenMessage(text,5,ScreenMessageStyle.UPPER_CENTER);}
        public static DVector D(Vector3d v) {return new DVector(v.x,v.y,v.z);}
        public static Vector3d V(DVector v) {return new Vector3d(v.X,v.Y,v.Z);}
        public static DRotation D(Quaternion q) {return new DRotation(q.x,q.y,q.z,q.w).Unit();}
        public static Quaternion Q(DRotation q) {return new Quaternion((float)q.X,(float)q.Y,(float)q.Z,(float)q.W);}
        public static Pose World(Part p) {return new Pose(D(p.transform.position),D(p.transform.rotation));}
        public static Pose BodyWorld(CelestialBody b) {return new Pose(D(b.position),D(b.bodyTransform.rotation));}
        public static void TransformVessel(Vessel v,Pose delta)
        {
            var placed=v.parts.Select(p=>new {Part=p,Pose=delta*World(p)}).ToArray();
            Pose vesselPose=delta*new Pose(D(v.transform.position),D(v.transform.rotation));
            v.transform.SetPositionAndRotation((Vector3)V(vesselPose.Position),Q(vesselPose.Rotation));
            foreach(var item in placed)
            {
                item.Part.transform.SetPositionAndRotation((Vector3)V(item.Pose.Position),Q(item.Pose.Rotation));
                if(item.Part.rb!=null){item.Part.rb.position=item.Part.transform.position;item.Part.rb.rotation=item.Part.transform.rotation;}
            }
        }
        public static FoundationMarker Marker(Vessel v) {return v==null?null:v.GetComponent<FoundationMarker>();}
        public static void Invalidate(Vessel v) {if(!ReferenceEquals(v,null)){Bindings.Remove(v);freeVessels.Remove(v);}}
        public static void Clear() {Bindings.Clear();freeVessels.Clear();releasing.Clear();pendingReconcile.Clear();AllowedUnpack=null;}
        public static void ScheduleReconcile(Anchor a)
        {if(a!=null)pendingReconcile[a.Id]=0;}
        public static void ProcessPending()
        {
            var registry=FoundationRegistry.Instance;
            if(registry==null||!registry.Ready||FlightGlobals.VesselsLoaded==null)return;
            foreach(var id in pendingReconcile.Keys.ToArray())
            {
                Anchor a;if(!registry.Anchors.TryGetValue(id,out a)){pendingReconcile.Remove(id);continue;}
                var v=FlightGlobals.VesselsLoaded.FirstOrDefault(x=>x!=null&&x.loaded&&x.parts!=null&&x.parts.Any(p=>p!=null&&p.persistentId==a.ReferenceId));
                if(v==null)continue;
                try {Reconcile(v);pendingReconcile.Remove(id);}
                catch(Exception e)
                {
                    int tries=++pendingReconcile[id];
                    if(tries==1)
                    {
                        Debug.LogWarning("[Foundations] Topology retry after "+e.GetType().Name+": "+e.Message+
                            "; vessel="+v.vesselName+"; parts="+string.Join(",",v.parts.Select(p=>p==null?"null":p.persistentId+":"+p.State).ToArray()));
                    }
                    if(tries>120){Debug.LogException(e);Say("Foundation change did not finish. Keep this save; see KSP.log.");pendingReconcile.Remove(id);}
                }
            }
        }
        public static Binding Get(Vessel v)
        {
            if(v==null||!v.loaded||v.parts==null)return null;
            Binding bound;
            if(Bindings.TryGetValue(v,out bound))
            {
                if(bound.ReferencePart!=null&&bound.ReferencePart.vessel==v)return bound;
                Bindings.Remove(v);
            }
            if(freeVessels.Contains(v))return null;
            var r=FoundationRegistry.Instance;if(r==null||!r.Ready)return null;
            var marker=Marker(v);Anchor a=null;
            if(marker!=null&&!string.IsNullOrEmpty(marker.foundationId))r.Anchors.TryGetValue(marker.foundationId,out a);
            // KSP can copy a VesselModule marker onto both halves of a split.
            // Only the half containing the persistent reference owns the hold.
            if(a!=null&&!v.parts.Any(p=>p!=null&&p.persistentId==a.ReferenceId))
            {marker.foundationId="";a=null;}
            if(a==null)a=r.Anchors.Values.FirstOrDefault(x=>v.parts.Any(p=>p!=null&&p.persistentId==x.ReferenceId));
            if(a==null){freeVessels.Add(v);return null;}
            bound=new Binding {Vessel=v,Anchor=a,ReferencePart=v.parts.FirstOrDefault(p=>p!=null&&p.persistentId==a.ReferenceId)};
            if(v.parts.Any(p=>p==null))
            {
                bound.Problem="KSP has a destroyed part in this vessel's parts list. Foundation is suspended; inspect the vessel and KSP.log.";
                Bindings[v]=bound;return bound;
            }
            foreach(var p in v.parts)
            {
                var m=a.Members.FirstOrDefault(x=>x.Id==p.persistentId && (x.Id!=0||x.FlightId==p.flightID));
                if(m!=null)bound.Members.Add(p,m);
            }
            bound.Order.AddRange(bound.Members.Keys.OrderBy(Depth));
            if(marker!=null)marker.foundationId=a.Id;
            if(v.mainBody==null||v.mainBody.bodyName!=a.Body||Math.Abs(v.mainBody.Radius-a.BodyRadius)>0.01)
                bound.Problem="The saved body has changed. Foundation is suspended; release only after inspecting the site.";
            else if(bound.Members.Count!=v.parts.Count)
                bound.Problem="Part membership changed outside a supported operation. Foundation remains held.";
            Bindings[v]=bound;return bound;
        }
        static int Depth(Part p) {int depth=0;while(p.parent!=null&&depth<10000){p=p.parent;depth++;}return depth;}
        public static bool IsHeld(Vessel v)
        {
            if(v==null||releasing.Contains(v))return false;
            Binding cached;
            if(Bindings.TryGetValue(v,out cached)&&cached.ReferencePart!=null&&cached.ReferencePart.vessel==v)return true;
            var registry=FoundationRegistry.Instance;
            var m=Marker(v);
            if(registry!=null&&registry.Ready&&v.parts!=null)
            {
                Anchor known;
                if(m!=null&&!string.IsNullOrEmpty(m.foundationId)&&registry.Anchors.TryGetValue(m.foundationId,out known))
                    return v.parts.Any(p=>p!=null&&p.persistentId==known.ReferenceId);
                return registry.Anchors.Values.Any(a=>v.parts.Any(p=>p!=null&&p.persistentId==a.ReferenceId));
            }
            return m!=null&&!string.IsNullOrEmpty(m.foundationId);
        }
        // Called only at a completed KSP topology change, never from the frame
        // maintenance loop. The saved surface pose is deliberately unchanged.
        public static void Reconcile(Vessel v)
        {
            var registry=FoundationRegistry.Instance;
            if(v==null||!v.loaded||v.parts==null||registry==null||!registry.Ready)return;
            Anchor a=registry.Anchors.Values.FirstOrDefault(x=>v.parts.Any(p=>p!=null&&p.persistentId==x.ReferenceId));
            if(a==null)return;
            if(v.parts.Any(p=>p==null))throw new InvalidOperationException("A destroyed part is still in KSP's vessel parts list");
            if(v.mainBody==null||v.mainBody.bodyName!=a.Body||Math.Abs(v.mainBody.Radius-a.BodyRadius)>0.01)
            {Say("Foundation topology changed on an unexpected body. The anchor was retained.");return;}
            Pose frame=BodyWorld(v.mainBody)*a.Surface;
            var ids=new HashSet<uint>(v.parts.Select(p=>p.persistentId));
            if(ids.Count!=v.parts.Count||ids.Contains(0))
            {Say("Part identity is incomplete after construction. The anchor was retained.");return;}
            foreach(var other in registry.Anchors.Values)
                if(other!=a&&v.parts.Any(p=>p.persistentId==other.ReferenceId))
                {Say("Release one foundation before joining two anchored bases.");return;}
            if(a.Members.Count==ids.Count&&a.Members.All(m=>ids.Contains(m.Id)))
            {
                var existing=Marker(v);if(existing!=null)existing.foundationId=a.Id;
                Invalidate(v);return;
            }
            int before=a.Members.Count;
            var next=a.Members.Where(m=>ids.Contains(m.Id)).ToList();
            foreach(var p in v.parts)
                if(!next.Any(m=>m.Id==p.persistentId))
                {
                    next.Add(new Member {Id=p.persistentId,FlightId=p.flightID,
                        Local=frame.Inverse*World(p),TetherWasOn=TetherActive(p),
                        BodyWasKinematic=p.rb!=null&&p.rb.isKinematic,
                        ServoWasKinematic=p.servoRb!=null&&p.servoRb.isKinematic});
                }
            a.Members.Clear();a.Members.AddRange(next);
            foreach(var p in v.parts)if(!p.packed)SetTether(p,false);
            a.Revision++;
            var marker=Marker(v);if(marker!=null)marker.foundationId=a.Id;
            Invalidate(v);
            if(!v.packed)v.GoOnRails();
            var bound=Get(v);Apply(bound);PrepareAdapters(bound);
            Say("Foundation updated: "+before+" to "+a.Members.Count+" parts.");
        }
        public static void Detached(Vessel v,Anchor formerAnchor)
        {
            if(v==null||!v.loaded||v.parts==null||formerAnchor==null)return;
            var registry=FoundationRegistry.Instance;
            if(registry==null||!registry.Ready)return;
            if(registry.Anchors.Values.Any(a=>v.parts.Any(p=>p.persistentId==a.ReferenceId)))
            {Reconcile(v);return;}
            var marker=Marker(v);if(marker!=null)marker.foundationId="";
            Invalidate(v);
            if(v.packed)v.GoOffRails();
            foreach(var p in v.parts)
            {
                var old=formerAnchor.Members.FirstOrDefault(m=>m.Id==p.persistentId);
                if(old==null)continue;
                p.packed=false;
                if(p.rb!=null){p.rb.isKinematic=old.BodyWasKinematic;p.rb.angularVelocity=Vector3.zero;}
                if(p.servoRb!=null)p.servoRb.isKinematic=old.ServoWasKinematic;
                p.ResumeVelocity();p.ResetJoints();
                SetTether(p,old.TetherWasOn);
            }
            v.permanentGroundContact=false;
        }
        public static string Eligibility(Vessel v)
        {
            if(!HooksReady)return "Required KSP hooks are unavailable. See KSP.log.";
            if(v==null||!v.loaded||v.rootPart==null||v.isEVA||v.vesselType==VesselType.Flag)return "Select a landed base.";
            if(!v.Landed||v.Splashed)return "The base must be resting on solid ground.";
            if(TimeWarp.CurrentRate!=1)return "Return to 1× time before anchoring or releasing.";
            if(v.packed||v.HoldPhysics||v.easingInToSurface||v.gravityMultiplier<0.999)return "Waiting for stock physics easing to finish…";
            if(!Number.Finite(v.srfSpeed)||v.srfSpeed>Settings.SettleSpeed)return "Waiting for the base to settle…";
            foreach(var p in v.parts)
            {
                if(p.persistentId==0)return "Part identity is not ready yet.";
                var rotation=p.transform.rotation;
                if(!D(p.transform.position).Finite||!Number.Finite(rotation.x)||!Number.Finite(rotation.y)||!Number.Finite(rotation.z)||!Number.Finite(rotation.w))
                    return "A part has invalid geometry. Restore a valid vessel before anchoring.";
                foreach(PartModule m in p.Modules)
                {
                    if(m is ModuleEngines && ((ModuleEngines)m).finalThrust>0.001f)return "Shut down thrust before anchoring.";
                    string n=m.GetType().FullName;
                    if(n.StartsWith("KAS.",StringComparison.Ordinal)||m.moduleName.StartsWith("KAS",StringComparison.Ordinal))return "Remove KAS link hardware from this base before anchoring: "+p.partInfo.title;
                    if(n.IndexOf("Robotic",StringComparison.OrdinalIgnoreCase)>=0||n.IndexOf("InfernalRobotics",StringComparison.OrdinalIgnoreCase)>=0)
                        return "Robotic assemblies are not supported in this preview: "+p.partInfo.title;
                    if(n.IndexOf("ParkingBrake",StringComparison.OrdinalIgnoreCase)>=0||n.IndexOf("PhysicsHold",StringComparison.OrdinalIgnoreCase)>=0)
                        return "Another physics-hold module is attached: "+p.partInfo.title;
                }
            }
            return null;
        }
        public static bool AnchorBase(Vessel v)
        {
            var reason=Eligibility(v);if(reason!=null){Say(reason);return false;}
            var r=FoundationRegistry.Instance;if(r==null||!r.Ready){Say("Waiting for this save's foundation registry.");return false;}
            if(IsHeld(v))return true;
            var reference=v.rootPart;
            if(reference.Modules.OfType<ModuleDeployablePart>().Any()||reference.Modules.OfType<ModuleWheelBase>().Any())
            {Say("Choose a craft with a fixed structural root before using this preview.");return false;}
            Pose frame=World(reference);
            var a=new Anchor {Id=Guid.NewGuid().ToString("D"),Name=v.vesselName,Body=v.mainBody.bodyName,BodyRadius=v.mainBody.Radius,
                ReferenceId=reference.persistentId,Surface=BodyWorld(v.mainBody).Inverse*frame,Revision=1,GroundContactWasOn=v.permanentGroundContact};
            foreach(var p in v.parts)a.Members.Add(new Member {Id=p.persistentId,FlightId=p.flightID,Local=frame.Inverse*World(p),TetherWasOn=TetherActive(p),
                BodyWasKinematic=p.rb!=null&&p.rb.isKinematic,ServoWasKinematic=p.servoRb!=null&&p.servoRb.isKinematic});
            r.Anchors.Add(a.Id,a);var marker=Marker(v);
            if(marker==null){r.Anchors.Remove(a.Id);Say("The foundation vessel marker did not load. Revisit this vessel.");return false;}
            marker.foundationId=a.Id;Invalidate(v);
            try
            {
                // Register first: even callbacks during GoOnRails cannot unpack a member.
                foreach(var p in v.parts)SetTether(p,false);
                v.GoOnRails();var b=Get(v);Apply(b);PrepareAdapters(b);Say("Anchored "+v.vesselName+". Position and orientation are held.");return true;
            }
            catch(Exception e)
            {
                Debug.LogException(e);Say("Foundation setup needs attention. The base remains packed; use Release base to recover.");return false;
            }
        }
        static bool TetherActive(Part p)
        {
            foreach(PartModule m in p.Modules)if(m.moduleName=="USI_InertialDampener")
            {var f=m.GetType().GetField("isActive");if(f!=null&&f.FieldType==typeof(bool))return (bool)f.GetValue(m);}
            return false;
        }
        static void SetTether(Part p,bool enabled)
        {
            foreach(PartModule m in p.Modules)if(m.moduleName=="USI_InertialDampener")
            {
                var method=m.GetType().GetMethod("SetActive",new[]{typeof(bool)});if(method==null)throw new InvalidOperationException("Unsupported USI tether API");method.Invoke(m,new object[]{enabled});
                var type=m.GetType().Assembly.GetType("USITools.ModuleStabilization");
                var field=type==null?null:type.GetField("onStabilizationToggle",BindingFlags.Public|BindingFlags.Static);
                var evt=field==null?null:field.GetValue(null) as EventData<Vessel,bool,bool>;
                if(evt==null)throw new InvalidOperationException("Unsupported USI stabilization API");
                evt.Fire(p.vessel,enabled,false);
            }
        }
        public static void Apply(Binding b)
        {
            if(b==null||b.Vessel==null||!b.Vessel.loaded||b.Problem!="")return;
            var v=b.Vessel;Pose frame=BodyWorld(v.mainBody)*b.Anchor.Surface;
            // SetLandedPosRot is intercepted, so stock pristine orgPos/orgRot are
            // never overwritten. Approved animations operate below part transforms.
            b.AppliedPositionError=0;b.AppliedAngleError=0;
            foreach(var p in b.Order)
            {
                if(p==null||p.State==PartStates.DEAD)continue;
                Pose pose=frame*b.Members[p].Local;
                if(p.persistentId==b.Anchor.ReferenceId)
                {
                    b.PositionError=(D(p.transform.position)-pose.Position).Length;
                    b.AngleError=D(p.transform.rotation).AngleDegrees(pose.Rotation);
                    b.MaxPositionError=Math.Max(b.MaxPositionError,b.PositionError);
                }
                if(p.rb!=null)p.rb.isKinematic=true;
                if(p.servoRb!=null)p.servoRb.isKinematic=true;
                p.transform.SetPositionAndRotation((Vector3)V(pose.Position),Q(pose.Rotation));
                if(p.rb!=null){p.rb.position=p.transform.position;p.rb.rotation=p.transform.rotation;}
                b.AppliedPositionError=Math.Max(b.AppliedPositionError,(D(p.transform.position)-pose.Position).Length);
                b.AppliedAngleError=Math.Max(b.AppliedAngleError,D(p.transform.rotation).AngleDegrees(pose.Rotation));
            }
            v.Landed=true;v.permanentGroundContact=true;
            v.srfRelRotation=Quaternion.Inverse(v.mainBody.bodyTransform.rotation)*v.transform.rotation;
            // Used by KSP's save and unloaded-vessel representation; the saved
            // anchor itself is never populated from these derived fields.
            v.latitude=v.mainBody.GetLatitude(v.transform.position);
            v.longitude=v.mainBody.GetLongitude(v.transform.position);
            v.altitude=v.mainBody.GetAltitude(v.transform.position);
            if(v.isActiveVessel)InputLockManager.RemoveControlLock("physicsHold");
            if(Time.realtimeSinceStartup>=b.NextSample)
            {
                b.NextSample=Time.realtimeSinceStartup+0.5f;
                b.Samples.Enqueue(Number.Write(Time.realtimeSinceStartup)+","+Number.Write(Planetarium.GetUniversalTime())+","+Number.Write(b.AppliedPositionError)+","+Number.Write(b.AppliedAngleError));
                while(b.Samples.Count>2048)b.Samples.Dequeue();
            }
        }
        public static void PrepareAdapters(Binding b)
        {
            if(b==null||b.AdaptersReady||b.Problem!=""||b.Vessel.parts.Any(p=>!p.started))return;
            foreach(var p in b.Vessel.parts)
            {
                SetTether(p,false);
                // Adapted from PhysicsHold's selective-unpack concept. Unlike the
                // original, the part's structural rigidbody remains kinematic.
                if(p==b.Vessel.rootPart||p.Modules.OfType<ModuleWheelBase>().Any())continue;
                bool animate=p.children.Count==0&&p.Modules.OfType<ModuleDeployablePart>().Any();
                bool docking=p.Modules.OfType<ModuleDockingNode>().Any();
                if(animate||docking)
                {
                    AllowedUnpack=p;
                    try {p.Unpack();}finally {AllowedUnpack=null;if(p.rb!=null)p.rb.isKinematic=true;}
                }
            }
            b.AdaptersReady=true;
        }
        public static bool Release(Vessel v)
        {
            if(v==null||TimeWarp.CurrentRate!=1||!v.loaded){Say("Release at 1× while visiting the base.");return false;}
            var b=Get(v);var marker=Marker(v);
            if(b==null){Say("Saved anchor could not be resolved. It has been retained; inspect KSP.log.");return false;}
            if(b.Problem!=""){Say(b.Problem);return false;}
            bool skippedGround=v.skipGroundPositioning,skippedDropped=v.skipGroundPositioningForDroppedPart;
            try
            {
                Apply(b);releasing.Add(v);
                // Stock unpack positions from pristine geometry. Skip terrain
                // repositioning, and restore held transforms before the solver.
                v.skipGroundPositioning=true;v.skipGroundPositioningForDroppedPart=true;
                if(Frames==null||Override==null)throw new MissingFieldException("Vessel physics hold fields");
                Frames.SetValue(v,Time.frameCount-1000);Override.SetValue(v,false);
                foreach(var wheel in v.FindPartModulesImplementing<ModuleWheelBase>())
                    if(WheelSetup!=null&&WheelInit!=null&&!(bool)WheelSetup.GetValue(wheel))
                    {wheel.StopAllCoroutines();WheelInit.Invoke(wheel,null);}
                v.GoOffRails();
                if(v.packed)throw new InvalidOperationException("KSP is not ready to resume surface physics yet");
                // Apply temporarily restores kinematic state while placing parts.
                Apply(b);
                foreach(var pair in b.Members)
                {
                    Part p=pair.Key;p.packed=false;
                    if(p.rb!=null){p.rb.isKinematic=pair.Value.BodyWasKinematic;if(!p.rb.isKinematic)p.rb.angularVelocity=Vector3.zero;}
                    if(p.servoRb!=null){p.servoRb.isKinematic=pair.Value.ServoWasKinematic;if(!p.servoRb.isKinematic)p.servoRb.angularVelocity=Vector3.zero;}
                    p.ResumeVelocity();p.ResetJoints();SetTether(p,pair.Value.TetherWasOn);
                }
                v.permanentGroundContact=b.Anchor.GroundContactWasOn;
                if(marker!=null)marker.foundationId="";
                FoundationRegistry.Instance.Anchors.Remove(b.Anchor.Id);Invalidate(v);
                Say("Released "+v.vesselName+" to normal surface physics.");return true;
            }
            catch(Exception e)
            {
                Debug.LogException(e);releasing.Remove(v);
                if(!v.packed)v.GoOnRails();Apply(b);Say("Release postponed: "+e.Message);return false;
            }
            finally {releasing.Remove(v);v.skipGroundPositioning=skippedGround;v.skipGroundPositioningForDroppedPart=skippedDropped;}
        }
    }
}
