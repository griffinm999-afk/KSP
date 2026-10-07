using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static bool IsDependentUtilityModule(PartModule module)
        {
            string name=module.GetType().FullName;
            return name=="USITools.USI_EfficiencyBoosterSwapOption"||name=="USITools.USIAnimation"||name=="CommNetAntennasConsumptor.ModuleAntennaToggler"||name=="ModuleDeployableRadiator";
        }
        static bool TryAntennaUtilityDemand(PartModule module,Vessel vessel,out double demand)
        {
            demand=0;
            try
            {
            var generator=module as ModuleGenerator;
            if(generator==null||vessel==null||module.part==null||module.part.vessel!=vessel||vessel.parts==null||!vessel.parts.Contains(module.part)||module.part.Modules.Count>128||module.part.Modules.Cast<PartModule>().Count(m=>ReferenceEquals(m,module))!=1||module.resHandler==null||module.resHandler.partModule!=module||module.resHandler.inputResources==null||module.resHandler.outputResources==null||module.resHandler.inputResources.Count>64)return false;
            return ColonyUtilityModuleBounds.TryAntennaDemand(UtilityModuleIdentity(module),vessel.loaded,
                module.GetType().GetMethod("FixedUpdate",BindingFlags.Public|BindingFlags.Instance)?.DeclaringType==typeof(ModuleGenerator),
                module.resHandler.GetType()==typeof(ModuleResourceHandler),generator.isThrottleControlled,
                module.resHandler.inputResources.Select(r=>r.name).ToArray(),module.resHandler.inputResources.Select(r=>(double)r.rate).ToArray(),module.resHandler.outputResources.Count,out demand);
            }
            catch { demand=0;return false; }
        }
        // This native animation has no independent tick demand. Only an
        // initialized, motionless terminal state with no costs or hidden
        // owners can be treated as a passive auxiliary. Housing and deployment
        // remain separate physical observations; no native setting is changed.
        static bool TryOwnerlessUtilityAnimation(USITools.USIAnimation animation, PartModule[] members)
        {
            if (animation == null || UtilityModuleIdentity(animation) != "USITools.USIAnimation|USITools|1.0.0.0" ||
                !(ReviewedPrivateField(animation, "_hasBeenInitialized") is bool initialized) || !initialized ||
                animation.resHandler == null || animation.resHandler.GetType() != typeof(ModuleResourceHandler) || animation.resHandler.partModule != animation ||
                animation.resHandler.inputResources == null || animation.resHandler.outputResources == null ||
                animation.resHandler.inputResources.Count != 0 || animation.resHandler.outputResources.Count != 0 ||
                animation.ResourceCosts != string.Empty || animation.ResCosts == null || animation.ResCosts.Count != 0 ||
                !Finite(animation.partialDeployCostPaid) || animation.partialDeployCostPaid != 0 ||
                animation.secondaryAnimationName != string.Empty || animation.inflatedMultiplier != -1f || animation.shedOnInflate ||
                members == null || members.Length > 128 || members.Any(m => m == null || m.part != animation.part) ||
                members.Count(m => ReferenceEquals(m, animation)) != 1 ||
                members.Any(m => m is IAnimatedModule || m is BaseConverter || m is ModuleControlSurface || m.GetType().FullName == "SystemHeat.ModuleSystemHeatFissionReactor")) return false;
            var linked = ReviewedPrivateField(animation, "_Modules") as IEnumerable;
            if (linked == null || linked.Cast<object>().Any()) return false;
            var deployment = animation.DeployAnimation;
            if (deployment == null || deployment[animation.deployAnimationName] == null) return false;
            return ColonyUtilityModuleBounds.OwnerlessAnimationTerminal(animation.isDeployed, deployment.isPlaying,
                deployment[animation.deployAnimationName].normalizedTime);
        }

        static bool TryDependentUtilityModule(PartModule module,Vessel vessel,HashSet<PartModule> accounted,ReactorUtilityWitness reactors)
        {
            try
            {
                if(module==null||vessel==null||!vessel.loaded||vessel.parts==null||module.part==null||module.part.vessel!=vessel||!vessel.parts.Contains(module.part)||module.resHandler==null||module.resHandler.partModule!=module)return false;
                var members=module.part.Modules.Cast<PartModule>().ToArray();
                if(members.Length>128||members.Any(m=>m==null||m.part!=module.part)||members.Count(m=>ReferenceEquals(m,module))!=1)return false;
                bool resources=module.resHandler.inputResources.Count!=0||module.resHandler.outputResources.Count!=0;
                string identity=UtilityModuleIdentity(module);
                if(identity=="USITools.USI_EfficiencyBoosterSwapOption|USITools|1.0.0.0")
                {
                    var options=members.OfType<USITools.AbstractSwapOption>().ToArray();
                    var controllers=members.OfType<USITools.USI_SwapController>().ToArray();
                    var bays=members.OfType<USITools.USI_SwappableBay>().ToArray();
                    var owners=members.OfType<USITools.ISwappableConverter>().Where(c=>!c.IsStandalone).Cast<PartModule>().ToArray();
                    if(controllers.Length!=1||UtilityModuleIdentity(controllers[0])!="USITools.USI_SwapController|USITools|1.0.0.0"||controllers[0].Loadouts==null||!controllers[0].Loadouts.SequenceEqual(options)||options.Count(o=>ReferenceEquals(o,module))!=1||bays.Length==0||bays.Length>16||owners.Length==0||owners.Length>16)return false;
                    var nativeOwners=ReviewedPrivateField(controllers[0],"_converters") as IEnumerable;
                    if(nativeOwners==null||!nativeOwners.Cast<object>().SequenceEqual(owners.Cast<object>())||owners.Length!=bays.Length)return false;
                    if(bays.Select(b=>b.moduleIndex).Distinct().Count()!=bays.Length)return false;
                    foreach(var bay in bays)
                    {
                        if(UtilityModuleIdentity(bay)!="USITools.USI_SwappableBay|USITools|1.0.0.0"||!ReferenceEquals(ReviewedPrivateField(bay,"_controller"),controllers[0])||bay.moduleIndex<0||bay.moduleIndex>=owners.Length||bay.currentLoadout<0||bay.currentLoadout>=options.Length)return false;
                        var owner=owners[bay.moduleIndex];
                        if(UtilityModuleIdentity(owner)!="USITools.USI_Converter|USITools|1.0.0.0"||!accounted.Contains(owner)||!ReferenceEquals(ReviewedPrivateField(owner,"_swapOption"),options[bay.currentLoadout]))return false;
                    }
                    return ColonyUtilityModuleBounds.DependentProof(true,true,resources,true,true,true,true);
                }
                if(identity=="CommNetAntennasConsumptor.ModuleAntennaToggler|CommNetAntennasConsumptor|3.5.8.0")
                {
                    var antennas=members.Where(m=>UtilityModuleIdentity(m)==ColonyUtilityModuleBounds.AntennaIdentity).ToArray();
                    var transmitters=members.OfType<ModuleDataTransmitter>().ToArray();
                    return ColonyUtilityModuleBounds.DependentProof(true,true,resources,true,antennas.Length==1&&transmitters.Length==1&&ReferenceEquals(ReviewedPrivateField(antennas[0],"moduleDT"),transmitters[0]),true,antennas.Length==1&&transmitters.Length==1&&accounted.Contains(antennas[0])&&accounted.Contains(transmitters[0]));
                }
                if(identity=="USITools.USIAnimation|USITools|1.0.0.0")
                {
                    var animation=module as USITools.USIAnimation;
                    if(TryOwnerlessUtilityAnimation(animation,members))return true;
                    if(TryResourceFreeOwnedUtilityAnimation(animation,members,accounted))return true;
                    if(animation==null||!(ReviewedPrivateField(animation,"_hasBeenInitialized") is bool initialized)||!initialized||!animation.isDeployed||!Finite(animation.partialDeployCostPaid)||animation.partialDeployCostPaid!=0)return false;
                    var deployment=animation.DeployAnimation;
                    if(deployment==null||deployment.isPlaying||deployment[animation.deployAnimationName]==null||!Finite(deployment[animation.deployAnimationName].normalizedTime)||deployment[animation.deployAnimationName].normalizedTime<.9999)return false;
                    var linked=ReviewedPrivateField(animation,"_Modules") as IEnumerable;
                    if(linked==null)return false;
                    var native=linked.Cast<object>().ToArray();var expected=members.OfType<IAnimatedModule>().Cast<object>().ToArray();
                    if(native.Length>128||native.Length!=expected.Length||native.Distinct().Count()!=native.Length||!native.SequenceEqual(expected)||native.Any(o=>!(o is PartModule owner)||!accounted.Contains(owner)))return false;
                    // Include actual recipe/reactor owners even when the installed animation's interface list is empty.
                    var owners=members.Where(m=>m is BaseConverter||m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").ToArray();
                    return ColonyUtilityModuleBounds.DependentProof(true,true,resources,true,true,true,owners.Length>0&&owners.All(accounted.Contains));
                }
                if(identity=="ModuleDeployableRadiator|Assembly-CSharp|0.0.0.0")
                {
                    var deploy=module as ModuleDeployableRadiator;
                    var owners=members.Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatRadiator").ToArray();
                    return ColonyUtilityModuleBounds.DependentProof(true,true,resources,true,owners.Length==1,deploy!=null&&deploy.deployState==ModuleDeployablePart.DeployState.EXTENDED,reactors.Qualified&&owners.Length==1&&reactors.Accounted.Contains(owners[0])&&accounted.Contains(owners[0]));
                }
                return false;
            }
            catch { return false; }
        }
        // EC accounting is distinct from physical pose/deployment qualification.
        // In the pinned native implementation Initialize/CheckAnimationState
        // synchronously discovers/enables the linked owners when deployed.
        // This restricted non-inflatable branch has no independent tick demand
        // or delayed resource callback; clip playback is not a power consumer.
        // General/inflatable animations retain the existing terminal-state gates.
        static bool TryResourceFreeOwnedUtilityAnimation(USITools.USIAnimation animation,PartModule[] members,HashSet<PartModule> accounted)
        {
            if(animation==null || UtilityModuleIdentity(animation)!="USITools.USIAnimation|USITools|1.0.0.0" ||
                !(ReviewedPrivateField(animation,"_hasBeenInitialized") is bool initialized)||!initialized||!animation.isDeployed||
                animation.inflatable||animation.inflatedMultiplier!=-1f||animation.shedOnInflate||animation.secondaryAnimationName!=string.Empty||
                animation.ResourceCosts!=string.Empty||animation.ResCosts==null||animation.ResCosts.Count!=0||
                !Finite(animation.partialDeployCostPaid)||animation.partialDeployCostPaid!=0||!Finite(animation.inflatedCost)||animation.inflatedCost!=0||
                animation.resHandler==null||animation.resHandler.GetType()!=typeof(ModuleResourceHandler)||animation.resHandler.partModule!=animation||
                animation.resHandler.inputResources==null||animation.resHandler.outputResources==null||animation.resHandler.inputResources.Count!=0||animation.resHandler.outputResources.Count!=0||
                members==null||members.Length>128||members.Any(m=>m==null||m.part!=animation.part||m is ModuleControlSurface)||members.Count(m=>ReferenceEquals(m,animation))!=1)return false;
            var linked=ReviewedPrivateField(animation,"_Modules") as IEnumerable;
            if(linked==null)return false;
            var native=linked.Cast<object>().Take(129).ToArray();var expected=members.OfType<IAnimatedModule>().Cast<object>().ToArray();
            if(native.Length>128||native.Length!=expected.Length||native.Distinct().Count()!=native.Length||!native.SequenceEqual(expected)||native.Any(o=>!(o is PartModule owner)||!accounted.Contains(owner)))return false;
            return members.Where(m=>m is BaseConverter||m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").All(accounted.Contains);
        }
        // Report live failed-proof inputs without changing any readiness gate.
        // This is included only in the bounded utility reason; no per-tick log.
        static string UtilityAnimationFailure(PartModule module,HashSet<PartModule> accounted)
        {
            if(!(module is USITools.USIAnimation animation))return "";
            try
            {
                var members=module.part.Modules.Cast<PartModule>().ToArray();
                var deployment=animation.DeployAnimation;var clip=deployment==null ? null : deployment[animation.deployAnimationName];
                var linked=ReviewedPrivateField(animation,"_Modules") as IEnumerable;
                var actual=linked==null ? null : linked.Cast<object>().Take(129).ToArray();
                var expected=members.OfType<IAnimatedModule>().Cast<object>().ToArray();
                var recipes=members.Where(m=>m is BaseConverter||m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").ToArray();
                string gate=!(ReviewedPrivateField(animation,"_hasBeenInitialized") is bool initialized)||!initialized ? "initialization" :
                    !animation.isDeployed ? "deployment" : !Finite(animation.partialDeployCostPaid)||animation.partialDeployCostPaid!=0 ? "partial-cost" :
                    deployment==null||clip==null ? "clip" : deployment.isPlaying ? "playing" : !Finite(clip.normalizedTime)||clip.normalizedTime<.9999 ? "terminal-time" :
                    module.resHandler==null||module.resHandler.inputResources.Count!=0||module.resHandler.outputResources.Count!=0 ? "independent-resources" :
                    actual==null||actual.Length>128||actual.Length!=expected.Length||actual.Distinct().Count()!=actual.Length||!actual.SequenceEqual(expected)||actual.Any(o=>!(o is PartModule owner)||!accounted.Contains(owner)) ? "linked-owners" :
                    recipes.Length==0||!recipes.All(accounted.Contains) ? "recipe-owners" : "other-owner/context";
                string owners=string.Join(",",members.Where(m=>m is IAnimatedModule||m is BaseConverter||m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").Take(16).Select(m=>m.GetType().FullName+":"+accounted.Contains(m)));
                return Bound("Animation part="+module.part.persistentId+" gate="+gate+" init="+ReviewedPrivateField(animation,"_hasBeenInitialized")+" deployed="+animation.isDeployed+" partial="+animation.partialDeployCostPaid+" clip="+animation.deployAnimationName+" time="+(clip==null ? "missing" : clip.normalizedTime.ToString("R",System.Globalization.CultureInfo.InvariantCulture))+" playing="+(deployment==null ? "missing" : deployment.isPlaying.ToString())+" primaryPlaying="+(deployment==null ? "missing" : deployment.IsPlaying(animation.deployAnimationName).ToString())+" enabled/speed/length/wrap="+(clip==null ? "missing" : clip.enabled+"/"+clip.speed+"/"+clip.length+"/"+clip.wrapMode)+" culling="+(deployment==null ? "missing" : deployment.cullingType.ToString())+" handler="+(module.resHandler==null ? "missing" : module.resHandler.inputResources.Count+"/"+module.resHandler.outputResources.Count)+" linked="+(actual==null ? "missing" : string.Join(",",actual.Take(16).Select(o=>o==null ? "null" : o.GetType().FullName)))+" owners="+owners,1024);
            }
            catch(Exception ex){return "Animation observation unavailable: "+Bound(ex.Message,128);}
        }
    }
}
