using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        public sealed class ConstructionActivationPlan
        {
            internal Vessel Vessel;
            internal PartModule[] Reactors,Radiators,Generators;
            internal string Context,OrderId,Binding;
            internal MethodInfo RadiatorActivate,ReactorEnable,ManualControl,GeneratorStart;
            internal FieldInfo ThrottleControl;
            public string BeforeSettings {get;internal set;}
            public string TargetSettings {get;internal set;}
        }
        // Settings/resources are read only; stock lazy persistent module IDs may
        // initialize. Strict placement observation must already have established
        // the paid craft/member/Foundation lineage before invoking this method.
        public ConstructionActivationPlan PrepareActivation(Vessel vessel,ColonyTemplate template,ColonyFacility facility,ConstructionOrder order)
            =>PrepareActivationPlan(vessel,template,facility,order,true);
        private ConstructionActivationPlan PrepareActivationPlan(Vessel vessel,ColonyTemplate template,ColonyFacility facility,ConstructionOrder order,bool initialFuelWitness)
        {
            if(vessel==null || !vessel.loaded || vessel.parts==null || vessel.parts.Count>512 || template==null || facility==null || order==null ||
                facility.VesselId!=vessel.id.ToString("D") || facility.ConstructionOrderId!=order.Id || facility.PlacementOperationId!=order.Placement.OperationId ||
                facility.CraftSha256!=template.CraftSha256 || order.TemplateId!=template.Id || order.TemplateHash!=template.Hash ||
                !facility.PartIds.OrderBy(x=>x).SequenceEqual(vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x)))throw new InvalidOperationException("Native activation lacks the exact loaded paid craft mapping.");
            var modules=vessel.parts.SelectMany(p=>p.Modules.Cast<PartModule>()).ToArray();
            var reactors=modules.Where(m=>m.moduleName=="ModuleSystemHeatFissionReactor").OrderBy(m=>m.part.persistentId).ThenBy(m=>NativeModuleId(m)).ToArray();
            var radiators=modules.Where(m=>m.moduleName=="ModuleSystemHeatRadiator").OrderBy(m=>m.part.persistentId).ThenBy(m=>NativeModuleId(m)).ToArray();
            var generators=modules.Where(IsReviewedFixedGenerator).OrderBy(m=>m.part.persistentId).ThenBy(m=>NativeModuleId(m)).ToArray();
            if(generators.Length>16)throw new InvalidOperationException("Initial fixed Generator module bound exceeded.");
            foreach(var generator in generators)RequireFixedGeneratorHardware((USITools.USI_Converter)generator);
            if(reactors.Length>16 || radiators.Length>64 || reactors.Any(m=>!SupportedSystemHeat(m)) || radiators.Any(m=>!SupportedSystemHeat(m)))throw new InvalidOperationException("Initial activation module/version inventory is not supported.");
            if(reactors.Length>0 && (radiators.Length==0 || reactors.Any(m=>!UtilityBool(m,"moduleIsEnabled") || UtilityBool(m,"HibernateOnWarp") || UtilityBool(m,"Hibernating") || !UtilityBool(m,"GeneratesElectricity")) ||
                radiators.Any(m=>!UtilityBool(m,"moduleIsEnabled") || m.part.Modules.Cast<PartModule>().OfType<ModuleDeployableRadiator>().Any(d=>d.deployState!=ModuleDeployablePart.DeployState.EXTENDED))))
                throw new InvalidOperationException("Initial reactor package must have enabled genuine electricity hardware, no hibernation and physically extended radiator panels.");
            var plan=new ConstructionActivationPlan {Vessel=vessel,Reactors=reactors,Radiators=radiators,Generators=generators,Context=ContextKey,OrderId=order.Id,Binding=ColonyEngine.ConstructionActivationBinding(state,order)};
            // Preflight every required API and public PAW field before saving
            // intent or allowing the first native radiator call.
            if(reactors.Length>0)
            {
                plan.RadiatorActivate=radiators[0].GetType().GetMethod("Activate",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                plan.ReactorEnable=reactors[0].GetType().GetMethod("EnableReactor",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                plan.ManualControl=reactors[0].GetType().GetMethod("SetManualControl",BindingFlags.Instance|BindingFlags.Public,null,new[]{typeof(bool)},null);
                plan.ThrottleControl=reactors[0].GetType().GetField("CurrentReactorThrottle",BindingFlags.Instance|BindingFlags.Public);
                if(plan.RadiatorActivate==null || plan.ReactorEnable==null || plan.ManualControl==null || plan.ThrottleControl==null || plan.ThrottleControl.FieldType!=typeof(float) ||
                    reactors.Any(m=>m.GetType()!=reactors[0].GetType()) || radiators.Any(m=>m.GetType()!=radiators[0].GetType()))throw new InvalidOperationException("Native activation API signatures differ from the reviewed installed provider.");
            }
            if(generators.Length>0)
            {
                plan.GeneratorStart=typeof(BaseConverter).GetMethod("StartResourceConverter",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null);
                if(plan.GeneratorStart==null||plan.GeneratorStart.ReturnType!=typeof(void)||generators.Any(g=>g.GetType().GetMethod("StartResourceConverter",BindingFlags.Instance|BindingFlags.Public,null,Type.EmptyTypes,null)?.DeclaringType!=typeof(BaseConverter)))throw new InvalidOperationException("Fixed native Generator initial activation API changed.");
                foreach(var generator in generators)
                {
                    var marker=generator.part.Modules.OfType<ColonyPlacementMarker>().SingleOrDefault();var fuel=generator.part.Resources.Get("Plutonium-238");
                    var reviewed=marker==null?null:template.StartupContents.SingleOrDefault(r=>r.CraftPartId==marker.craftPartId&&r.ResourceName=="Plutonium-238");
                    if(marker==null||!string.Equals(marker.templateSha256,template.CraftSha256,StringComparison.OrdinalIgnoreCase)||reviewed==null||
                        reviewed.Amount<=0||reviewed.Amount>20*ColonyLimits.Units||fuel==null||!fuel.flowState||fuel.maxAmount!=20||!Finite(fuel.amount)||fuel.amount<0||fuel.amount>20||
                        initialFuelWitness&&!ColonyFixedGeneratorBounds.InitialFuelWitness(reviewed.Amount/(double)ColonyLimits.Units,fuel.amount,fuel.maxAmount))
                        throw new InvalidOperationException("Initial fixed Generator lacks exact positive reviewed paid fuel mapping and current bounded local startup stock.");
                }
            }
            plan.BeforeSettings=ReadActivationSettings(plan,false);plan.TargetSettings=ReadActivationSettings(plan,true);return plan;
        }
        static uint NativeModuleId(PartModule module)
        {
            if(module==null||module.part==null)throw new InvalidOperationException("Native module identity lacks its exact part.");
            uint id=module.PersistentId;
            if(id==0)
            {
                var vessel=module.part.vessel;
                if(!HighLogic.LoadedSceneIsFlight||HighLogic.CurrentGame==null||vessel==null||!vessel.loaded||vessel.parts==null||
                    FlightGlobals.Vessels==null||!FlightGlobals.Vessels.Contains(vessel)||
                    !vessel.parts.Contains(module.part)||module.part.Modules.Cast<PartModule>().Count(m=>ReferenceEquals(m,module))!=1)
                    throw new InvalidOperationException("Lazy native module identity requires an exact current loaded part/module.");
                id=module.GetPersistentId(); // Stock allocates a nonzero per-part unique ID and persists it at the normal save boundary.
            }
            if(id==0)throw new InvalidOperationException("Native module lacks an exact persistent identity.");return id;
        }
        static string ReadActivationSettings(ConstructionActivationPlan plan,bool target)
        {
            return string.Join("|",plan.Reactors.Select(m=>"reactor:"+m.part.persistentId+":"+NativeModuleId(m)+":Enabled="+(target || UtilityBool(m,"Enabled"))+":Manual="+(target || UtilityBool(m,"ManualControl"))+":Throttle="+
                (target ? "100" : UtilityNumber(m,"CurrentReactorThrottle",double.NaN).ToString("R",CultureInfo.InvariantCulture))+":Hibernate="+UtilityBool(m,"HibernateOnWarp"))
                .Concat(plan.Radiators.Select(m=>"radiator:"+m.part.persistentId+":"+NativeModuleId(m)+":Cooling="+(target || UtilityBool(m,"IsCooling"))+":Deployment="+
                    string.Join(",",m.part.Modules.Cast<PartModule>().OfType<ModuleDeployableRadiator>().Select(d=>d.deployState.ToString()))))
                .Concat(plan.Generators.Select(m=>"fixedGenerator:"+m.part.persistentId+":"+NativeModuleId(m)+":Active="+(target||((BaseConverter)m).IsActivated))));
        }
        // The fresh applying object is accepted before any native call. This
        // method is never invoked for a loaded saved applying/held child.
        public void ActivatePreparedActivation(ConstructionActivationPlan plan,ColonyState freshApplying)
        {
            var game=HighLogic.CurrentGame;
            Action check=()=>{if(Current!=this || state!=freshApplying || plan.Context!=ContextKey || !ReferenceEquals(game,HighLogic.CurrentGame) ||
                plan.Vessel==null || !plan.Vessel.loaded || plan.Vessel.parts==null || !plan.Vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x).SequenceEqual(state.Colonies.SelectMany(c=>c.Facilities).Single(f=>f.ConstructionOrderId==plan.OrderId).PartIds.OrderBy(x=>x)) ||
                ColonyEngine.ConstructionActivationBinding(state,state.Construction.Single(o=>o.Id==plan.OrderId))!=plan.Binding)throw new InvalidOperationException("Selected paid world/member context changed during initial native activation.");};
            check();if(ReadActivationSettings(plan,false)!=plan.BeforeSettings)throw new InvalidOperationException("Native settings changed after activation preflight.");
            foreach(var radiator in plan.Radiators)
            {
                check();if(UtilityBool(radiator,"IsCooling"))continue;
                plan.RadiatorActivate.Invoke(radiator,null);check();
            }
            foreach(var reactor in plan.Reactors)
            {
                check();if(!UtilityBool(reactor,"ManualControl"))
                {plan.ManualControl.Invoke(reactor,new object[]{true});check();}
                if(!UtilityBool(reactor,"ManualControl"))throw new InvalidOperationException("Native reactor denied manual full-power commissioning.");
                // This is the native public PAW control, never simulated throttle,
                // fuel, heat, integrity or generated electricity.
                plan.ThrottleControl.SetValue(reactor,100f);check();
                if(!UtilityBool(reactor,"Enabled")){plan.ReactorEnable.Invoke(reactor,null);check();}
            }
            foreach(var generator in plan.Generators)
            {
                check();RequireFixedGeneratorHardware((USITools.USI_Converter)generator);
                if(!((BaseConverter)generator).IsActivated){plan.GeneratorStart.Invoke(generator,null);check();}
                if(!((BaseConverter)generator).IsActivated)throw new InvalidOperationException("Native fixed Generator denied its initial activation.");
            }
        }
        public string ObservePreparedActivation(ConstructionActivationPlan plan)=>ReadActivationSettings(plan,false);
        private bool EnsureConstructionActivation(Vessel vessel,ColonyTemplate template,ColonyFacility facility,ConstructionOrder order)
        {
            string id=ColonyEngine.ConstructionActivationId(order);var priorEffect=state.Effects.SingleOrDefault(e=>e.Id==id);
            if(priorEffect!=null&&priorEffect.State=="applied")
            {
                if(!ColonyEngine.IsConstructionActivationEffect(state,order.Id,id))throw new InvalidOperationException("Applied initial activation lost its exact paid construction lineage.");
                return true; // Later shutdown/fuel exhaustion is a utility outage; no startup preflight or event is repeated.
            }
            if(priorEffect!=null&&!ColonyEngine.IsConstructionActivationEffect(state,order.Id,id))throw new InvalidOperationException("Pending initial activation lost its exact paid construction lineage.");
            // A retained applying/held child only observes unchanged hardware
            // and settings. Fuel may have fallen to zero since a successful
            // native call whose acknowledgement was lost; no event repeats.
            var plan=PrepareActivationPlan(vessel,template,facility,order,priorEffect==null);
            if(priorEffect!=null)
            {
                var reconciled=ColonyEngine.ObserveConstructionActivation(state,order.Id,ObservePreparedActivation(plan));
                if(!ReferenceEquals(state,reconciled))Accept(reconciled);
                return state.Effects.Single(e=>e.Id==id).State=="applied";
            }
            if(plan.Reactors.Length==0&&plan.Generators.Length==0)return true;
            var applying=ColonyEngine.PrepareConstructionActivation(state,order.Id,ContextKey,plan.BeforeSettings,plan.TargetSettings);
            var complete=ColonyEngine.ObserveConstructionActivation(applying,order.Id,plan.TargetSettings);
            // A distinct impossible setting represents any uncertain outcome.
            var held=ColonyEngine.ObserveConstructionActivation(applying,order.Id,"unresolved native activation outcome");
            byte[] applyingBytes=ColonyStateCodec.Serialize(applying),completeBytes=ColonyStateCodec.Serialize(complete),heldBytes=ColonyStateCodec.Serialize(held);
            string applyingHash=ColonyStateCodec.Hash(applyingBytes),completeHash=ColonyStateCodec.Hash(completeBytes),heldHash=ColonyStateCodec.Hash(heldBytes);
            var game=HighLogic.CurrentGame;string epoch=loadEpoch;
            state=applying;acceptedBytes=applyingBytes;acceptedHash=applyingHash;
            try
            {
                ActivatePreparedActivation(plan,applying);
                if(!ReferenceEquals(game,HighLogic.CurrentGame) || epoch!=loadEpoch || state!=applying)throw new InvalidOperationException("Selected world changed across initial activation.");
                if(ObservePreparedActivation(plan)!=plan.TargetSettings)throw new InvalidOperationException("Native initial activation target settings did not read back exactly.");
                state=complete;acceptedBytes=completeBytes;acceptedHash=completeHash;return true;
            }
            catch(Exception ex)
            {
                if(ReferenceEquals(game,HighLogic.CurrentGame) && epoch==loadEpoch && state==applying)
                {
                    state=held;acceptedBytes=heldBytes;acceptedHash=heldHash;
                    // The preallocated generic hold is authoritative first. A
                    // richer diagnostic may fail without losing that fence.
                    try{Accept(ColonyEngine.HoldEffect(state,id,Bound("Initial native activation interrupted: "+(ex.InnerException ?? ex).Message+". No native events repeated; exact target readback required.",512)));}catch{}
                }
                return false;
            }
        }
    }
}
