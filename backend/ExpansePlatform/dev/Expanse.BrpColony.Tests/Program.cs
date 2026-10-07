extern alias CecilInstalled;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using BackgroundResourceProcessing;
using BackgroundResourceProcessing.Behaviour;
using BackgroundResourceProcessing.Core;
using Expanse.BrpColony;
using Expanse.WorldBridge;
using BrpConverter=BackgroundResourceProcessing.Core.ResourceConverter;
using Cecil=CecilInstalled::Mono.Cecil;

static class Program
{
    static readonly Assembly Brp=typeof(BackgroundResourceProcessor).Assembly;
    static readonly Type Engine=Brp.GetType("BackgroundResourceProcessing.Core.ResourceProcessor",true);
    static int checks;
    static readonly List<string> Errors=new List<string>();
    public class LogSink:DispatchProxy
    {
        protected override object Invoke(MethodInfo method,object[] args)
        {if(method.Name=="Error")Errors.Add((string)args[0]);return null;}
    }
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Near(double actual,double expected,double tolerance,string message)=>Check(Math.Abs(actual-expected)<=tolerance,message+": actual="+actual.ToString("R",CultureInfo.InvariantCulture)+" expected="+expected.ToString("R",CultureInfo.InvariantCulture));
    static object Invoke(object owner,string method,params object[] args)=>owner.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length).Invoke(owner,args);
    static object Field(object owner,string name)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(owner);
    static int Add(object engine,string name,object item)
    {var field=Engine.GetField(name);var list=field.GetValue(engine);int index=(int)Invoke(list,"Add",item);field.SetValue(engine,list);return index;}
    static void Register()
    {
        var settings=Brp.GetType("BackgroundResourceProcessing.DebugSettings",true);var instance=settings.GetProperty("Instance",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).GetValue(null);
        Invoke(instance,"ConfigureUnityExternal");
        var log=Brp.GetType("BackgroundResourceProcessing.LogUtil",true);var sink=log.GetField("Sink",BindingFlags.Static|BindingFlags.Public);sink.SetValue(null,DispatchProxy.Create(sink.FieldType,typeof(LogSink)));
        var register=typeof(ConverterBehaviour).GetMethod("RegisterAll",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(IEnumerable<Type>)},null);
        register.Invoke(null,new object[]{new[]{typeof(ExpanseColonyProportionalBehaviour),typeof(ConstantConverter)}});
    }
    static ConfigNode Recipe(bool farm=false,double multiplier=1)
    {
        var n=new ConfigNode("COLONY_RECIPE");n.AddValue("Contract",ColonyRecipe.Contract);n.AddValue("Status","supported");n.AddValue("Profile",farm?"cultivate-s":"ranger");n.AddValue("FlightId",1);n.AddValue("ModuleId",7);n.AddValue("Multiplier",multiplier.ToString("R",CultureInfo.InvariantCulture));n.AddValue("TakeAmount",1);n.AddValue("FillAmount",.95);n.AddValue("BindingHash","fixture-paid-owner");
        if(farm)
        {
            Row(n,"INPUT_RESOURCE","Substrate",.0026);Row(n,"INPUT_RESOURCE","Water",.0026);Row(n,"INPUT_RESOURCE","Fertilizer",.000026);Row(n,"INPUT_RESOURCE","ElectricCharge",5.49);Row(n,"INPUT_RESOURCE","Machinery",.000002);
            Row(n,"OUTPUT_RESOURCE","Supplies",.00026);Row(n,"OUTPUT_RESOURCE","Recyclables",.000002,true);Row(n,"REQUIRED_RESOURCE","Machinery",100);
        }
        else{Row(n,"INPUT_RESOURCE","Plutonium-238",1e-6,false,ResourceFlowMode.NO_FLOW);Row(n,"OUTPUT_RESOURCE","ElectricCharge",50,true);Row(n,"REQUIRED_RESOURCE","Plutonium-238",20);}
        return n;
    }
    static void Row(ConfigNode n,string kind,string name,double ratio,bool dump=false,ResourceFlowMode flow=ResourceFlowMode.ALL_VESSEL)
    {var r=n.AddNode(kind);r.AddValue("ResourceName",name);r.AddValue("Ratio",ratio.ToString("R",CultureInfo.InvariantCulture));r.AddValue("DumpExcess",dump);r.AddValue("FlowMode",flow);}
    sealed class Fixture
    {
        internal readonly object engine=Activator.CreateInstance(Engine,true);
        internal readonly BackgroundResourceProcessor processor=(BackgroundResourceProcessor)FormatterServices.GetUninitializedObject(typeof(BackgroundResourceProcessor));
        internal readonly ExpanseColonyProportionalBehaviour behaviour;
        internal readonly BrpConverter converter;
        internal double now;
        internal bool valid=true;
        internal bool authorityReady=true;
        internal Fixture(bool farm=false,double amount=19.947034447,double multiplier=1,double start=0)
        {
            typeof(BackgroundResourceProcessor).GetField("processor",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(processor,engine);now=start;Engine.GetField("lastUpdate").SetValue(engine,now);
            if(farm){Inventory("Machinery",amount,100);Inventory("Substrate",200,200);Inventory("Water",200,200);Inventory("Fertilizer",20,20);Inventory("ElectricCharge",10000000,10000000);Inventory("Supplies",0,100);Inventory("Recyclables",100,100);}
            else{Inventory("Plutonium-238",amount,20);Inventory("ElectricCharge",1000,1000);}
            behaviour=new ExpanseColonyProportionalBehaviour(Recipe(farm,multiplier),(r,v)=>{if(!authorityReady)throw new ColonyAuthorityUnavailableException("Selected paid colony authority unavailable.");if(!valid)throw new InvalidOperationException("Fixture paid owner changed.");},()=>{});
            converter=new BrpConverter(behaviour){FlightId=1,ModuleId=7};
            // Installed lifecycle calls Refresh before insertion and edges.
            converter.Refresh(State());Check(string.IsNullOrEmpty(behaviour.HoldReason),"Bootstrap recipe rejected: "+behaviour.HoldReason);Check(!behaviour.Bound&&double.IsPositiveInfinity(converter.NextChangepoint),"Bootstrap must await actual rates/edges");
            Add(engine,"converters",converter);
            for(int index=0;index<processor.Inventories.Count;index++)
            {
                var inventory=processor.Inventories[index];
                if(behaviour.CurrentRecord.GetNodes("INPUT_RESOURCE").Any(n=>n.GetValue("ResourceName")==inventory.ResourceName))converter.Pull.Add(index);
                if(behaviour.CurrentRecord.GetNodes("OUTPUT_RESOURCE").Any(n=>n.GetValue("ResourceName")==inventory.ResourceName))converter.Push.Add(index);
                if(inventory.ResourceName=="Supplies")converter.Constraint.Add(index);
            }
            Rates();
        }
        internal ResourceInventory Inventory(string name,double amount,double capacity,uint flight=1)
        {
            var snapshot=(ProtoPartResourceSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartResourceSnapshot));snapshot.resourceName=name;snapshot.amount=amount;snapshot.maxAmount=capacity;snapshot.flowState=true;
            var i=new ResourceInventory{FlightId=flight,ResourceName=name,Amount=amount,MaxAmount=capacity,OriginalAmount=amount};
            typeof(ResourceInventory).GetField("protoSnapshot",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(i,snapshot);
            int index=Add(engine,"inventories",i);
            var ids=(IDictionary)Field(engine,"inventoryIds");ids.Add(i.Id,index);return i;
        }
        internal VesselState State()=>new VesselState(now){Processor=processor};
        internal ResourceInventory Tank(string name)=>processor.Inventories.Single(i=>i.ResourceName==name&&i.FlightId==1);
        internal void Rates()
        {
            Invoke(engine,"UpdateConstraintState");Invoke(engine,"ComputeRateSolution");Invoke(engine,"ComputeRates");
            behaviour.OnRatesComputed(processor,converter,new ConverterBehaviour.RateCalculatedEvent{CurrentTime=now});
        }
        internal void Refresh(){converter.Refresh(State());Rates();}
        internal void Step(double time)
        {
            Invoke(engine,"UpdateState",time,false);now=time;
            foreach(var row in processor.Inventories){row.OriginalAmount=row.Amount;row.Snapshot.amount=row.Amount;}
            Refresh();
        }
        internal void Advance(double end)
        {int steps=0;while(now<end){Check(++steps<100000,"Changepoint did not progress");double next=Math.Min(end,converter.NextChangepoint);Check(next>now,"Zero-time loop");Step(next);}}
        internal void SolverEvent(double time)
        {
            Invoke(engine,"UpdateState",time,false);now=time;
            foreach(var row in processor.Inventories){row.OriginalAmount=row.Amount;row.Snapshot.amount=row.Amount;}
            Invoke(engine,"UpdateBehaviours",State());Invoke(engine,"UpdateConstraintState");Invoke(engine,"ComputeRates");
            // Actual installed dispatch visits our converter even when only
            // another behavior refreshed. Do not directly refresh our vector.
            Invoke(processor,"DispatchOnRatesComputed",time);
        }
    }
    static void UnrelatedEvents()
    {
        var f=new Fixture(amount:20);var pulse=new ConstantConverter();pulse.Outputs.Add(new ResourceRatio{ResourceName="ElectricCharge",Ratio=1,DumpExcess=true});
        var other=new BrpConverter(pulse){FlightId=2,ModuleId=9};other.Push.Add(1);other.Refresh(f.State());Add(f.engine,"converters",other);
        double initialDeadline=f.converter.NextChangepoint,external=100,lastEpoch=f.behaviour.SampleUt;int refreshes=0;
        while(f.now<10000)
        {
            double next=Math.Min(10000,Math.Min(external,f.converter.NextChangepoint));Check(next>f.now,"Repeated unrelated native events failed to progress");
            if(next==external){var ratio=pulse.Outputs[0];ratio.Ratio=ratio.Ratio==1?2:1;pulse.Outputs[0]=ratio;other.NextChangepoint=next;external+=100;}
            f.SolverEvent(next);
            Check(f.behaviour.Bound&&f.behaviour.HoldReason.Length==0,"Unrelated-event regression silently held instead of refreshing");
            if(f.behaviour.SampleUt!=lastEpoch){refreshes++;lastEpoch=f.behaviour.SampleUt;}
            else Check(f.converter.NextChangepoint<=initialDeadline,"Unrelated callback extended vector epoch deadline");
            initialDeadline=f.converter.NextChangepoint;
        }
        Check(refreshes>=4,"Repeated unrelated events starved proportional refresh");double exact=20*Math.Exp(-5e-8*10000);
        double error=(exact-f.Tank("Plutonium-238").Amount)/20;Check(error>=-1e-12&&error<=ProportionalMath.CumulativeStockError(5e-8,10000)+1e-12,"Unrelated event cumulative stock error exceeds bound");
        // A real BRP input producer throttles the farm, then doubles. The
        // spent Machinery drift budget must survive the changed solver rate.
        var farm=new Fixture(true,100,1);farm.Tank("Substrate").Amount=farm.Tank("Substrate").OriginalAmount=farm.Tank("Substrate").Snapshot.amount=0;
        var feed=new ConstantConverter();feed.Outputs.Add(new ResourceRatio{ResourceName="Substrate",Ratio=.0013});var supply=new BrpConverter(feed){FlightId=2,ModuleId=10};supply.Push.Add(1);supply.Refresh(farm.State());Add(farm.engine,"converters",supply);farm.Refresh();
        Near(farm.converter.Rate,.5,1e-12,"Actual BRP farm must start half rate");var supplyRatio=feed.Outputs[0];supplyRatio.Ratio=.0026;feed.Outputs[0]=supplyRatio;supply.NextChangepoint=100;farm.SolverEvent(100);
        Near(farm.converter.Rate,1,1e-12,"Actual BRP farm rate increase missing");Check(farm.behaviour.SampleUt==0&&farm.converter.NextChangepoint<=5050.000001,"Rate increase reset spent Machinery vector budget");
        farm.SolverEvent(farm.converter.NextChangepoint);Check(farm.behaviour.Bound&&farm.behaviour.SampleUt==farm.now&&farm.converter.NextChangepoint>farm.now,"Rate increase exhausted budget without a fresh progressing epoch");
        var stalled=new Fixture(true,100,1);stalled.Tank("Substrate").Amount=0;stalled.Refresh();Near(stalled.Tank("Machinery").Rate,0,0,"Farm fixture must start stalled");
        stalled.Tank("Substrate").Amount=stalled.Tank("Substrate").OriginalAmount=stalled.Tank("Substrate").Snapshot.amount=200;stalled.SolverEvent(100);
        Check(stalled.converter.NextChangepoint>100&&!double.IsInfinity(stalled.converter.NextChangepoint),"Zero-to-positive rate failed to schedule epoch");
        // Independent bound uses actual epoch movement and a changed slope.
        double nextFast=ProportionalMath.NextInEpoch(100,new[]{19.9999},new[]{20d},new[]{-2e-6},new[]{20d});
        Check(nextFast<=1050.0000001,"Rate increase allocated a second full drift budget");
        var transient=new Fixture();transient.authorityReady=false;transient.SolverEvent(100);
        Check(!transient.behaviour.Bound&&transient.converter.NextChangepoint==100,"Callback authority loss must request immediate held refresh");
        double stock=transient.Tank("Plutonium-238").Amount;transient.SolverEvent(100);double retry=transient.converter.NextChangepoint;
        Check(retry>100&&!double.IsInfinity(retry)&&transient.converter.Inputs.Count==0,"Callback transient loss must install empty bounded retry");
        var retrySave=new ConfigNode();transient.behaviour.Save(retrySave);var retryCold=(ExpanseColonyProportionalBehaviour)ConverterBehaviour.Load(retrySave);
        typeof(ExpanseColonyProportionalBehaviour).GetField("validate",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(retryCold,(Action<ConfigNode,Vessel>)((r,v)=>{throw new ColonyAuthorityUnavailableException("Selected paid colony authority unavailable.");}));
        typeof(ExpanseColonyProportionalBehaviour).GetField("provenance",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(retryCold,(Action)(()=>{}));var retryVector=retryCold.GetResources(transient.State());
        Check(retryVector.Inputs.Count==0&&retryVector.NextChangepoint==102,"Cold retry count must preserve bounded nonproducing backoff");
        var unrepresentable=retryCold.GetResources(new VesselState(1e20){Processor=transient.processor});Check(double.IsPositiveInfinity(unrepresentable.NextChangepoint)&&retryCold.HoldReason.Contains("cannot represent"),"Unrepresentable authority retry must hold rather than spin");
        transient.authorityReady=true;transient.SolverEvent(retry);Check(transient.behaviour.Bound,"Callback transient authority loss became permanent");Near(transient.Tank("Plutonium-238").Amount,stock,0,"Callback startup retry replayed held interval");
        var stopped=new Fixture(true,1e-6,.05);Near(stopped.behaviour.Factor,1e-8,1e-22,"Effective-stop fixture raw requirement factor");
        foreach(var row in stopped.converter.Inputs.Values)Near(row.Ratio,0,0,"Post-multiplier native stop retained input");foreach(var row in stopped.converter.Outputs.Values)Near(row.Ratio,0,0,"Post-multiplier native stop retained output");Near(stopped.Tank("Machinery").Rate,0,0,"Effective stop consumed Machinery");Check(double.IsPositiveInfinity(stopped.converter.NextChangepoint),"Effective stop must not spin");
        double stop=ProportionalMath.Next(0,new[]{2.0001e-6},new[]{100d},new[]{-2e-12},2e-8);Check(stop<=50.000001,"Multiplier stop crossing omitted");
    }
    static void NativeSolver()
    {
        var f=new Fixture();Check(f.behaviour.Bound,"Actual BRP owner did not bind");Near(f.behaviour.Factor,19.947034447/20,1e-14,"Own fraction");Near(f.converter.Rate,1,1e-10,"Dumped full EC must permit fuel decay");Check(f.converter.Required.Count==0,"Dumped EC acquired a fill or hard Pu threshold");
        double fuel=f.Tank("Plutonium-238").Amount;f.Advance(21600);double exact=fuel*Math.Exp(-1e-6/20*21600);double error=exact-f.Tank("Plutonium-238").Amount;
        Check(error>=-1e-12&&error/fuel<=ProportionalMath.CumulativeStockError(1e-6/20,21600)+1e-12,"Euler cumulative stock error exceeds analytic bound");
        Near(f.Tank("ElectricCharge").Amount,1000,1e-9,"DumpExcess storage remains capped");
        var generic=new ConstantConverter();generic.Inputs.Add(new ResourceRatio{ResourceName="Plutonium-238",Ratio=1e-6,FlowMode=ResourceFlowMode.NO_FLOW});generic.Outputs.Add(new ResourceRatio{ResourceName="ElectricCharge",Ratio=50,DumpExcess=true});generic.Required.Add(new ResourceConstraint{ResourceName="Plutonium-238",Amount=20});
        var original=new BrpConverter(generic){FlightId=2,ModuleId=8};original.Pull.Add(0);original.Push.Add(1);original.Constraint.Add(0);original.Refresh(f.State());Add(f.engine,"converters",original);Invoke(f.engine,"UpdateConstraintState");Invoke(f.engine,"ComputeRates");Near(original.Rate,0,1e-12,"Installed hard threshold regression fixture");
        var farm=new Fixture(true,99.998650110,.6);Check(farm.behaviour.Bound,"Farm owner rejected");Near(farm.converter.Rate,1,1e-9,"Farm dropped below Machinery100 must operate");Check(farm.converter.Required.Count==1,"Farm must retain only Supplies fill constraint");
        double scale=99.998650110/100*.6;foreach(var r in farm.converter.Inputs.Values)Near(r.Ratio,ColonyRecipe.Read(Recipe(true,.6)).Inputs.Single(i=>i.ResourceName==r.ResourceName).Ratio*scale,1e-12,"Farm input scaled once");
        farm.Tank("Supplies").Amount=farm.Tank("Supplies").OriginalAmount=farm.Tank("Supplies").Snapshot.amount=96;farm.Refresh();Near(farm.converter.Rate,0,1e-12,"Nondump fill enforced");
    }
    static void ReloadAndMutation()
    {
        var f=new Fixture();f.Advance(1000);double before=f.Tank("Plutonium-238").Amount;
        var node=new ConfigNode();f.behaviour.Save(node);var cold=(ExpanseColonyProportionalBehaviour)ConverterBehaviour.Load(node);Check(!cold.Bound&&cold.CurrentRecord!=null,"Cold behavior must preserve recipe but regenerate binding");
        var coldVector=cold.GetResources(f.State());Check(coldVector.Inputs.Count==0&&cold.HoldReason.Length>0,"Cold absent paid authority must explicitly hold");Near(f.Tank("Plutonium-238").Amount,before,0,"Cold hold must not mutate physical stock");
        var fuel=f.Tank("Plutonium-238");fuel.Amount=fuel.OriginalAmount=fuel.Snapshot.amount=20;
        Check(ExpanseColonyProportionalBehaviour.InvalidateAfterPhysicalMutation(f.processor,f.now)=="","Paid refill invalidation failed");Check(!f.behaviour.Bound&&f.converter.NextChangepoint==f.now,"Mutation must invalidate current factor at same caught-up UT");
        Check(ExpanseColonyProportionalBehaviour.InvalidateAfterPhysicalMutation(f.processor,f.now)=="","Same UT repeated invalidation failed");f.Refresh();Near(f.behaviour.Factor,1,0,"Refill re-reads current own stock");Near(fuel.Amount,20,0,"Refresh replayed pre-refill interval");f.Step(f.now+100);Near(fuel.Amount,19.9999,1e-11,"Post-refill fuel charged exactly once");
        Check(ExpanseColonyProportionalBehaviour.InvalidateAfterPhysicalMutation(f.processor,f.now-1).Length>0,"Wrong caught-up UT must fail held");f.Refresh();Check(f.converter.Inputs.Count==0&&!f.behaviour.Bound,"Mutation failure leaves held empty recipe");Check(double.IsPositiveInfinity(f.converter.NextChangepoint),"Held failure must not loop immediately");
        var changed=new Fixture();changed.valid=false;changed.Refresh();Check(changed.behaviour.HoldReason.Length>0&&changed.converter.Inputs.Count==0,"Changed paid owner retained recipe");
        Check(double.IsPositiveInfinity(changed.converter.NextChangepoint),"Permanent paid mismatch must not auto retry");
        var startup=new Fixture();startup.authorityReady=false;startup.Refresh();double stock=startup.Tank("Plutonium-238").Amount;
        Check(!startup.behaviour.Bound&&startup.converter.Inputs.Count==0&&startup.converter.NextChangepoint>startup.now&&!double.IsInfinity(startup.converter.NextChangepoint),"Cold temporary authority loss must retry at positive future UT");Near(startup.Tank("Plutonium-238").Rate,0,0,"Temporary startup hold must cease production");startup.authorityReady=true;startup.Step(startup.converter.NextChangepoint);Check(startup.behaviour.Bound,"Authority becoming ready must rebind without visit/mutation");Near(startup.Tank("Plutonium-238").Amount,stock,0,"Startup retry must not replay held interval");
        var replacement=new BrpConverter(changed.behaviour){FlightId=1,ModuleId=7};changed.valid=true;changed.Refresh();changed.behaviour.OnRatesComputed(changed.processor,replacement,new ConverterBehaviour.RateCalculatedEvent{CurrentTime=changed.now});Check(!changed.behaviour.Bound&&changed.behaviour.HoldReason.Length>0,"Converter replacement accepted stale ownership");
        var remapped=new Fixture();var old=remapped.Tank("Plutonium-238");var ids=(IDictionary)Field(remapped.engine,"inventoryIds");ids.Remove(old.Id);
        var listField=Engine.GetField("inventories");var list=listField.GetValue(remapped.engine);list.GetType().GetProperty("Item").SetValue(list,null,new object[]{0});listField.SetValue(remapped.engine,list);
        remapped.Inventory("Water",20,20,2);var fresh=remapped.Inventory("Plutonium-238",15,20);int newIndex=(int)ids[fresh.Id];remapped.converter.Pull.Clear();remapped.converter.Pull.Add(newIndex);
        Check(ExpanseColonyProportionalBehaviour.InvalidateAfterPhysicalMutation(remapped.processor,remapped.now)=="","Inventory replacement invalidation failed");remapped.Refresh();Near(remapped.behaviour.Factor,.75,0,"Replacement index must read fresh owner stock");Check(remapped.behaviour.Bound&&!ReferenceEquals(old,fresh),"Regenerated owner accepted durable old inventory");
    }
    static void MathReference()
    {
        Near(ProportionalMath.Factor(new[]{5d,9d},new[]{10d,-10d}),.1,1e-15,"Min + negative requirement");Near(ProportionalMath.Factor(new[]{20d},new[]{10d}),1,0,"Upper clamp");Near(ProportionalMath.Factor(new[]{10d},new[]{-10d}),0,0,"Negative stop");Near(ProportionalMath.Factor(new[]{1e-10},new[]{1d}),0,0,"Stock 1e-9 stop");
        bool rejected=false;try{ProportionalMath.Next(1e20,new[]{10d},new[]{20d},new[]{-1e-3});}catch(InvalidOperationException){rejected=true;}Check(rejected,"Unrepresentable large UT interval must hold");
        double large=1e12,cp=ProportionalMath.Next(large,new[]{10d},new[]{20d},new[]{-1e-6});Check(cp>large&&cp-large<=1000,"Representable large UT must round within analytic budget");
        foreach(double seconds in new[]{21600d,129600d,1e7})
        {
            double k=5e-8,now=0,amount=20;
            while(now<seconds){double next=ProportionalMath.Next(now,new[]{amount},new[]{20d},new[]{-k*amount});double dt=Math.Min(next-now,seconds-now);amount-=k*amount*dt;now+=dt;}
            double exact=20*Math.Exp(-k*seconds);Check(Math.Abs(exact-amount)/20<=ProportionalMath.CumulativeStockError(k,seconds)+1e-11,"Independent exponential bound horizon");
            // An independently computed product with arbitrary cold cuts has
            // the same cumulative bound, not a per-step inference.
            double cutStock=20;for(int i=0;i<10000;i++){double h=seconds/10000;cutStock*=1-k*h;}
            if(k*seconds/10000<=ProportionalMath.RelativeDrift)Check((exact-cutStock)/20<=ProportionalMath.CumulativeStockError(k,seconds)+1e-11,"Independent cold-cut bound");
        }
    }
    static void ColdLoad()
    {
        // Installed Harmony's MonoMod runtime detour is Mono-only and cannot
        // self-test on CoreCLR8. Invoke the production interception bodies
        // around actual installed Load, never a replacement solver/serializer.
        Check(Engine.GetMethod("Load",new[]{typeof(ConfigNode),typeof(Vessel)})?.ReturnType==typeof(void),"Installed Load signature differs");
        var warm=new Fixture();warm.Tank("ElectricCharge").Amount=warm.Tank("ElectricCharge").Snapshot.amount=warm.Tank("ElectricCharge").OriginalAmount=0;
        var water=warm.Inventory("Water",100,100);var unrelated=new ConstantConverter();unrelated.Inputs.Add(new ResourceRatio{ResourceName="Water",Ratio=1});unrelated.Outputs.Add(new ResourceRatio{ResourceName="ElectricCharge",Ratio=2,DumpExcess=true});
        var other=new BrpConverter(unrelated){FlightId=2,ModuleId=9};other.Pull.Add(2);other.Push.Add(1);other.Refresh(warm.State());Add(warm.engine,"converters",other);warm.Rates();
        var save=new ConfigNode();Invoke(warm.engine,"Save",save);string original=save.ToString();
        // Remove registration rather than remove a live/native DLL. This is
        // the installed serializer's exact absent-optional behavior path.
        var register=typeof(ConverterBehaviour).GetMethod("RegisterAll",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(IEnumerable<Type>)},null);
        var registryType=typeof(ConverterBehaviour).BaseType;var registry=registryType.GetField("registry",BindingFlags.Static|BindingFlags.NonPublic);var savedRegistry=registry.GetValue(null);
        registry.SetValue(null,new Dictionary<string,Type>{{nameof(ConstantConverter),typeof(ConstantConverter)}});
        var missing=Activator.CreateInstance(Engine,true);GuardedLoad(missing,save);Check(save.ToString()==original,"Load guard changed incoming saved node");
        var missingInventories=((IEnumerable)Field(missing,"inventories")).Cast<ResourceInventory>().ToArray();var missingConverters=((IEnumerable)Field(missing,"converters")).Cast<BrpConverter>().ToArray();
        Check(missingConverters[0].Behaviour==null&&missingConverters[0].Inputs.Count==0,"Missing optional behavior retained owned vector");Near(missingInventories[0].Rate,0,0,"Missing optional cached fuel rate survived");Near(missingInventories[1].Rate,2,1e-12,"Unrelated EC contribution lost");Near(missingInventories[2].Rate,-1,1e-12,"Unrelated feed contribution lost");
        Invoke(missing,"UpdateState",10d,false);Near(missingInventories[0].Amount,warm.Tank("Plutonium-238").Amount,0,"Missing optional cold load advanced owned stock");Near(missingInventories[2].Amount,90,1e-12,"Missing optional stopped unrelated real converter");
        registry.SetValue(null,savedRegistry);
        var held=Activator.CreateInstance(Engine,true);GuardedLoad(held,save);var heldRows=((IEnumerable)Field(held,"converters")).Cast<BrpConverter>().ToArray();var heldModule=(BackgroundResourceProcessor)FormatterServices.GetUninitializedObject(typeof(BackgroundResourceProcessor));typeof(BackgroundResourceProcessor).GetField("processor",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(heldModule,held);
        Invoke(held,"UpdateBehaviours",new VesselState(0){Processor=heldModule});Invoke(held,"ComputeRates");var owned=(ExpanseColonyProportionalBehaviour)heldRows[0].Behaviour;
        Check(owned.HoldReason.Length>0&&!owned.Bound&&heldRows[0].Inputs.Count==0,"Present-held cold recipe escaped owner validation");Near(((IEnumerable)Field(held,"inventories")).Cast<ResourceInventory>().First().Rate,0,0,"Held optional retained old fuel rate");
        // Bind a fixture ledger to the deserialized production behavior while
        // retaining installed Load, Refresh, solver and remapped native IDs.
        typeof(ExpanseColonyProportionalBehaviour).GetField("validate",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owned,(Action<ConfigNode,Vessel>)((r,v)=>{}));typeof(ExpanseColonyProportionalBehaviour).GetField("provenance",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owned,(Action)(()=>{}));
        heldRows[0].NextChangepoint=0;Invoke(held,"UpdateBehaviours",new VesselState(0){Processor=heldModule});Invoke(held,"ComputeRates");owned.OnRatesComputed(heldModule,heldRows[0],new ConverterBehaviour.RateCalculatedEvent{CurrentTime=0});
        Check(owned.Bound&&heldRows[0].NextChangepoint>0,"Present-valid cold recipe failed regenerated owner binding");
        // No selected converter: original tree and native loaded rates remain.
        var plain=save.CreateCopy();plain.RemoveNode(plain.GetNodes("CONVERTER")[0]);var plainText=plain.ToString();var plainState=new ColonyBrpColdRecipeGuard.LoadState();Check(ReferenceEquals(ColonyBrpColdRecipeGuard.PrepareCopy(plain,plainState),plain)&&!plainState.Targeted&&plain.ToString()==plainText,"Unrelated load changed");
        // A real solver exception is surfaced before ComputeRates swallows it:
        // poison only cached derived ratio, never a physical quantity. Finalizer
        // must install the conservative processor failure fence.
        var bad=save.CreateCopy();bad.GetNodes("CONVERTER")[1].GetNode("INPUT_RESOURCE").SetValue("Ratio","NaN",false);
        var failed=Activator.CreateInstance(Engine,true);GuardedLoad(failed,bad);Check(ColonyBrpColdRecipeGuard.Failure(failed).Length>0,"Forced native recomputation failure lacks stored fence");Check(double.IsPositiveInfinity((double)Field(failed,"nextChangepoint")),"Failure fence retained time continuation");
        var failedStock=((IEnumerable)Field(failed,"inventories")).Cast<ResourceInventory>().ToArray();Check(failedStock.All(i=>i.Rate==0),"Failure fence retained stale inventory rates");double[] amounts=failedStock.Select(i=>i.Amount).ToArray();Invoke(failed,"UpdateState",10d,false);Check(failedStock.Select(i=>i.Amount).SequenceEqual(amounts),"Failure fence changed inventories");
    }
    static void GuardedLoad(object engine,ConfigNode node)
    {
        var type=typeof(ColonyBrpColdLoadAddon);object[] prefix={node,null};type.GetMethod("Prefix",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,prefix);Exception error=null;
        try{Invoke(engine,"Load",prefix[0],null);type.GetMethod("Postfix",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{engine,prefix[1]});}
        catch(Exception ex){error=ex.InnerException??ex;}
        var remaining=type.GetMethod("Finalizer",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{engine,prefix[1],error}) as Exception;if(remaining!=null)throw remaining;
    }
    static void Metadata()
    {
        var assembly=typeof(ColonyRecipe).Assembly;var attrs=assembly.GetCustomAttributesData();var identity=attrs.Single(a=>a.AttributeType.Name=="KSPAssembly");
        Check((string)identity.ConstructorArguments[0].Value=="Expanse.BrpColony"&&assembly.GetName().Name=="Expanse.BrpColony","KSP/MM optional names differ");
        var deps=attrs.Where(a=>a.AttributeType.Name=="KSPAssemblyDependency").ToDictionary(a=>(string)a.ConstructorArguments[0].Value,a=>string.Join(".",a.ConstructorArguments.Skip(1).Select(v=>v.Value.ToString())));
        Check(deps.Count==4&&deps["BackgroundResourceProcessing"]=="0.2.7"&&deps["USITools"]=="0.0.0"&&deps["HarmonyKSP"]=="1.0.0"&&deps["KSPBurst"]=="1.5.5","Optional dependency metadata differs from installed registration");
        var brpIdentity=Brp.GetCustomAttributesData().Single(a=>a.AttributeType.Name=="KSPAssembly");Check((string)brpIdentity.ConstructorArguments[0].Value=="BackgroundResourceProcessing"&&string.Join(".",brpIdentity.ConstructorArguments.Skip(1).Select(v=>v.Value.ToString()))=="0.2.7","Installed BRP KSP identity changed");
        Check(!typeof(USITools.USI_Converter).Assembly.GetCustomAttributesData().Any(a=>a.AttributeType.Name=="KSPAssembly"),"Installed USITools now has a different registered version");
        string mainPath=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..","src","Expanse.WorldBridge","bin","Release","net472","Expanse.WorldBridge.dll"));
        var main=Cecil.AssemblyDefinition.ReadAssembly(mainPath);Check(!main.MainModule.AssemblyReferences.Any(a=>a.Name.StartsWith("BackgroundResourceProcessing",StringComparison.Ordinal)||a.Name=="Expanse.BrpColony"),"Detached main Bridge metadata acquired optional assembly dependency");
        {
            var optional=Cecil.AssemblyDefinition.ReadAssembly(assembly.Location);
            var detachedIdentity=optional.CustomAttributes.Single(a=>a.AttributeType.Name=="KSPAssembly");
            Check(optional.Name.Name=="Expanse.BrpColony"&&(string)detachedIdentity.ConstructorArguments[0].Value==optional.Name.Name,"Detached optional KSP/MM names differ");
            var detachedDeps=optional.CustomAttributes.Where(a=>a.AttributeType.Name=="KSPAssemblyDependency").ToDictionary(a=>(string)a.ConstructorArguments[0].Value,a=>string.Join(".",a.ConstructorArguments.Skip(1).Select(v=>v.Value.ToString())));
            Check(detachedDeps.Count==4&&deps.All(d=>detachedDeps.ContainsKey(d.Key)&&detachedDeps[d.Key]==d.Value),"Detached optional dependencies differ");
        }
        string ksp=@"C:\Kerbal Space Program\KSP_x64_Data\Managed";
        // Inspect installed IL without invoking Unity logging or loading any
        // candidate DLL into a game domain. This independently establishes the
        // stock filter's order relative to Assembly.LoadFrom and GetTypes.
        {
            var stock=Cecil.AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(ksp,"Assembly-CSharp.dll"));
            var loader=stock.MainModule.Types.Single(t=>t.FullName=="AssemblyLoader");var loaded=loader.NestedTypes.Single(t=>t.Name=="LoadedAssembly");var list=loader.NestedTypes.Single(t=>t.Name=="LoadedAssembyList");
            var calls=loader.Methods.Single(m=>m.Name=="LoadAssemblies").Body.Instructions.Select(i=>i.Operand as Cecil.MethodReference).Where(m=>m!=null).ToList();
            int sort=calls.FindIndex(m=>m.Name=="SortAssemblies"),load=calls.FindIndex(m=>m.Name=="Load"&&m.DeclaringType.Name=="LoadedAssembly"),types=calls.FindIndex(m=>m.Name=="GetTypes");
            Check(sort>=0&&sort<load&&load<types,"Stock dependency sort must precede managed load and GetTypes");
            var sortCalls=list.Methods.Single(m=>m.Name=="SortAssemblies").Body.Instructions.Select(i=>i.Operand as Cecil.MethodReference).Where(m=>m!=null).ToList();
            Check(sortCalls.Any(m=>m.Name=="CheckDependencies")&&sortCalls.Any(m=>m.Name=="get_dependenciesMet"),"Stock sort no longer checks and filters dependency state");
            var ctorCalls=loaded.Methods.Single(m=>m.IsConstructor&&!m.IsStatic).Body.Instructions.Select(i=>i.Operand as Cecil.MethodReference).Where(m=>m!=null).ToList();
            Check(ctorCalls.Any(m=>m.Name=="ReadAssembly"&&m.DeclaringType.FullName=="Mono.Cecil.AssemblyDefinition")&&!ctorCalls.Any(m=>m.Name=="LoadFrom"),"Stock candidate metadata reader must remain detached");
            var loadCalls=loaded.Methods.Single(m=>m.Name=="Load").Body.Instructions.Select(i=>i.Operand as Cecil.MethodReference).Where(m=>m!=null).ToList();
            Check(loadCalls.Any(m=>m.Name=="get_dependenciesMet")&&loadCalls.Any(m=>m.Name=="LoadFrom"),"Stock managed Load lost dependency guard");
        }
        // These are the installed loader registrations, not managed versions.
        string installedRoot=System.IO.Path.GetFullPath(System.IO.Path.Combine(ksp,"..",".."));
        foreach(string path in new[]{"GameData/000_Harmony/HarmonyInstallChecker.dll","GameData/000_KSPBurst/Plugins/KSPBurst.dll","GameData/000_USITools/USITools.dll"})
        {
            var provider=Cecil.AssemblyDefinition.ReadAssembly(System.IO.Path.Combine(installedRoot,path));
            var stamp=provider.CustomAttributes.SingleOrDefault(a=>a.AttributeType.Name=="KSPAssembly");string name=stamp==null?System.IO.Path.GetFileNameWithoutExtension(path):(string)stamp.ConstructorArguments[0].Value;
            string version=stamp==null?"0.0.0":string.Join(".",stamp.ConstructorArguments.Skip(1).Select(v=>v.Value.ToString()).Concat(stamp.ConstructorArguments.Count==3?new[]{"0"}:new string[0]));
            Console.WriteLine("PROVIDER "+path+" managed="+provider.Name.FullName+" loader="+name+","+version);
            Check(deps.ContainsKey(name)&&new Version(version)>=new Version(deps[name]),"Installed dependency loader stamp does not satisfy declared minimum: "+name);
        }
        Console.WriteLine("PASS "+checks+" metadata checks; stock loader/ModuleManager primary evidence establishes pre-GetTypes filtering. Native startup absence witness remains required.");
    }
    static int Main(string[] args)
    {try{if(args.Contains("--metadata")){Metadata();return 0;}Register();if(args.Contains("--events")){UnrelatedEvents();Console.WriteLine("PASS "+checks+" repeated-event/epoch/transient/effective-stop checks against installed BRP.");return 0;}MathReference();NativeSolver();ReloadAndMutation();ColdLoad();Console.WriteLine("PASS "+checks+" checks; installed BRP solver/serializer/Load plus production interception bodies. Native Harmony startup is unqualified.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}

