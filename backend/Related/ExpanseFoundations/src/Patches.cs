using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Expanse.Foundations
{
    [KSPAddon(KSPAddon.Startup.Instantly,true)]
    public sealed class FoundationBootstrap : MonoBehaviour
    {
        void Awake()
        {
            DontDestroyOnLoad(this);
            try
            {
                new Harmony("expanse.foundations").PatchAll(typeof(FoundationBootstrap).Assembly);
                Hold.HooksReady=true;Debug.Log("[Foundations] 0.2.0: early unpack, surface-pose and topology hooks installed.");
            }
            catch(Exception e){Debug.LogError("[Foundations] Disabled: required hooks failed. "+e);}
        }
    }
    [HarmonyPatch(typeof(Vessel),"GoOffRails")]
    static class UnpackGate
    {
        static bool Prefix(Vessel __instance)
        {
            if(!Hold.IsHeld(__instance))return true;
            Hold.Apply(Hold.Get(__instance));return false;
        }
    }
    [HarmonyPatch(typeof(Vessel),"get_HoldPhysics")]
    static class HoldPhysicsState
    {
        // Stock deployables use this property to suppress load/warp g-force
        // damage. A foundation really is in physics hold, without a timer hack.
        static void Postfix(Vessel __instance,ref bool __result) {if(Hold.IsHeld(__instance))__result=true;}
    }
    [HarmonyPatch(typeof(Part),"Unpack")]
    static class PartUnpackGate
    {
        static bool Prefix(Part __instance) {return Hold.AllowedUnpack==__instance||!Hold.IsHeld(__instance.vessel);}
        static void Postfix(Part __instance)
        {
            if(Hold.IsHeld(__instance.vessel))
            {
                if(__instance.rb!=null)__instance.rb.isKinematic=true;
                if(__instance.servoRb!=null)__instance.servoRb.isKinematic=true;
            }
        }
    }
    [HarmonyPatch(typeof(VesselPrecalculate),"SetLandedPosRot")]
    static class SurfacePoseGate
    {
        static bool Prefix(Vessel ___vessel)
        {
            // Unloaded bases have no part geometry to place. Stock uses the last
            // derived surface coordinates; the precise anchor remains save-owned.
            if(!___vessel.loaded)return true;
            if(!Hold.IsHeld(___vessel))return true;
            Hold.Apply(Hold.Get(___vessel));return false;
        }
    }
    [HarmonyPatch(typeof(ModuleDockingNode),"FindNodeApproaches")]
    static class DockingDiscovery
    {
        static bool Prefix(ModuleDockingNode __instance,ref ModuleDockingNode __result)
        {
            if(!Hold.IsHeld(__instance.vessel))return true;
            __result=Find(__instance,false);return false;
        }
        static void Postfix(ModuleDockingNode __instance,ref ModuleDockingNode __result)
        {if(__result==null&&!Hold.IsHeld(__instance.vessel))__result=Find(__instance,true);}
        static ModuleDockingNode Find(ModuleDockingNode source,bool heldOnly)
        {
            if(source.nodeTransform==null||source.nodeTypes==null)return null;
            foreach(var v in FlightGlobals.VesselsLoaded)
            {
                if(v==source.vessel||!v.loaded||Hold.IsHeld(v)!=heldOnly)continue;
                foreach(var candidate in v.dockingPorts)
                {
                    var other=candidate as ModuleDockingNode;
                    if(other==null||other.part==null||other.part.State==PartStates.DEAD||other.state!="Ready"||other.nodeTransform==null||other.nodeTypes==null)continue;
                    if(!source.nodeTypes.Overlaps(other.nodeTypes)||source.gendered!=other.gendered||(source.gendered&&source.genderFemale==other.genderFemale))continue;
                    if(source.snapRotation!=other.snapRotation||(source.snapRotation&&source.snapOffset!=other.snapOffset))continue;
                    if(source.CheckDockContact(source,other,source.acquireRange,source.acquireMinFwdDot,source.acquireMinRollDot))return other;
                }
            }
            return null;
        }
    }
    [HarmonyPatch(typeof(Part),"Couple")]
    static class CouplingTransaction
    {
        static bool Prefix(Part __instance,Part tgtPart,ref Anchor __state)
        {
            bool source=Hold.IsHeld(__instance.vessel),target=tgtPart!=null&&Hold.IsHeld(tgtPart.vessel);
            if(!source&&!target)return true;
            if(source&&target)
            {
                if(__instance.vessel!=tgtPart.vessel)Hold.Say("Release one foundation before joining two anchored bases.");
                return false;
            }
            var held=source?__instance.vessel:tgtPart.vessel;
            var binding=Hold.Get(held);
            if(binding==null||binding.Problem!="")
            {Hold.Say("Foundation anchor is unavailable; coupling was cancelled.");return false;}
            __state=binding.Anchor;
            if(source)
            {
                // Stock DockToVessel may have aligned the source foundation to
                // the visiting ship. Restore it, moving the ship by the same
                // rigid transform before stock makes the joint.
                var old=Hold.World(held.rootPart);
                Hold.Apply(binding);
                var delta=Hold.World(held.rootPart)*old.Inverse;
                Hold.TransformVessel(tgtPart.vessel,delta);
            }
            return true;
        }
        static void Postfix(Part __instance,Part tgtPart,Anchor __state)
        {
            if(__state==null)return;
            Hold.ScheduleReconcile(__state);
        }
    }
    [HarmonyPatch(typeof(Part),"decouple")]
    static class DecoupleTransaction
    {
        static void Prefix(Part __instance,ref SplitState __state)
        {
            var binding=Hold.Get(__instance.vessel);
            if(binding!=null)__state=new SplitState {Anchor=binding.Anchor,Old=__instance.vessel};
        }
        static void Postfix(Part __instance,SplitState __state)
        {
            if(__state==null)return;
            SplitState.Finish(__state,__instance.vessel);
        }
    }
    sealed class SplitState
    {
        public Anchor Anchor;
        public Vessel Old;
        public static void Finish(SplitState state,Vessel split)
        {
            if(split==null||state.Old==null)return;
            bool referenceOnSplit=split.parts.Any(p=>p.persistentId==state.Anchor.ReferenceId);
            var anchored=referenceOnSplit?split:state.Old;
            var departing=referenceOnSplit?state.Old:split;
            Hold.Detached(departing,state.Anchor);
            Hold.Reconcile(anchored);
        }
    }
    [HarmonyPatch(typeof(Part),"Undock")]
    static class UndockTransaction
    {
        static void Prefix(Part __instance,ref SplitState __state)
        {
            var binding=Hold.Get(__instance.vessel);
            if(binding!=null)__state=new SplitState {Anchor=binding.Anchor,Old=__instance.vessel};
        }
        static void Postfix(Part __instance,SplitState __state)
        {
            if(__state==null)return;
            SplitState.Finish(__state,__instance.vessel);
        }
    }
    // Stock EVA construction does not use Part.Couple/decouple. It creates a
    // fresh Part and calls OnAttachFlight; the dragged original is detached
    // through OnDetachFlight. Commit only after stock has completed each edit.
    [HarmonyPatch(typeof(Part),"OnDetachFlight")]
    static class ConstructionDetach
    {
        static void Prefix(Part __instance,ref Anchor __state)
        {
            var binding=Hold.Get(__instance.vessel);
            if(binding!=null)__state=binding.Anchor;
        }
        static void Postfix(Anchor __state) {Hold.ScheduleReconcile(__state);}
    }
    [HarmonyPatch(typeof(Part),"OnAttachFlight")]
    static class ConstructionAttach
    {
        static void Postfix(Part __instance)
        {
            var binding=Hold.Get(__instance.vessel);
            if(binding!=null)
            {
                if(__instance.rb!=null)__instance.rb.isKinematic=true;
                if(__instance.servoRb!=null)__instance.servoRb.isKinematic=true;
                Hold.ScheduleReconcile(binding.Anchor);
            }
        }
    }
    // Stock portraits prohibit EVA because the cabin is packed. Temporarily
    // expose only this query as unpacked; restore it even if another patch throws.
    [HarmonyPatch(typeof(KSP.UI.Screens.Flight.KerbalPortrait),"CanEVA")]
    static class EvaQuery
    {
        static void Prefix(KSP.UI.Screens.Flight.KerbalPortrait __instance,out Part __state)
        {
            __state=null;var crew=__instance.crewMember;
            if(crew==null||crew.InPart==null||!Hold.IsHeld(crew.InPart.vessel)||!crew.InPart.packed)return;
            __state=crew.InPart;__state.packed=false;
        }
        static Exception Finalizer(Exception __exception,Part __state)
        {if(__state!=null)__state.packed=true;return __exception;}
    }
    [HarmonyPatch]
    static class UsiHoldGuard
    {
        static bool Prepare() {return AccessTools.TypeByName("USITools.ModuleStabilization")!=null;}
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var type=AccessTools.TypeByName("USITools.ModuleStabilization");
            var method=type==null?null:AccessTools.Method(type,"FixedUpdate");
            if(method!=null)yield return method;
        }
        static bool Prefix(MonoBehaviour __instance) {return !Hold.IsHeld(__instance.GetComponent<Vessel>());}
    }
}
