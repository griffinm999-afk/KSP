using System.Reflection;
using System.Runtime.Serialization;
using Expanse.WorldBridge;
public static class Qualification
{
    static object New(Type t)=>FormatterServices.GetUninitializedObject(t);
    static FieldInfo Field(Type t,string name){for(;t!=null;t=t.BaseType){var f=t.GetField(name,BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(f!=null)return f;}throw new Exception("Missing field "+name);}
    static void Set(object o,string name,object value)=>Field(o is Type t?t:o.GetType(),name).SetValue(o is Type?null:o,value);
    static object Get(object o,string name)=>Field(o.GetType(),name).GetValue(o);
    public static int Run()
    {
        var bridge=typeof(WorldBridgeAddon).Assembly;var runtime=bridge.GetType("Expanse.WorldBridge.ColonyRuntime");var ownerType=typeof(LifeSupport.ModuleLifeSupportSystem);Set(runtime,"lifeSupportOwnerType",ownerType);
        var owner=New(ownerType);var addon=New(typeof(WorldBridgeAddon));var vessel=(Vessel)New(typeof(Vessel));vessel.loaded=true;vessel.vesselType=VesselType.Base;
        var part=(Part)New(typeof(Part));part.vessel=vessel;Set(owner,"vessel",vessel);Set(owner,"_crewPart",part);Set(owner,"_currentCrewCount",5);Set(owner,"LastUpdateTime",98d);
        var settings=New(typeof(LifeSupport.LifeSupportConfig));Set(settings,"<SupplyAmount>k__BackingField",.0005f);Set(settings,"<WasteAmount>k__BackingField",.0005f);Set(settings,"<ECAmount>k__BackingField",.01f);
        var status=New(typeof(LifeSupport.VesselSupplyStatus));Set(status,"<RecyclerMultiplier>k__BackingField",.5f);Set(owner,"_vesselStatus",status);
        var scenario=New(typeof(LifeSupport.LifeSupportScenario));var persistence=New(typeof(LifeSupport.LifeSupportPersistance));Set(persistence,"_Settings",settings);Set(scenario,"<settings>k__BackingField",persistence);Set(typeof(LifeSupport.LifeSupportScenario),"<Instance>k__BackingField",scenario);
        var broker=(IResourceBroker)New(typeof(ResourceBroker));var converter=(ResourceConverter)New(typeof(ResourceConverter));Set(converter,"_broker",broker);Set(owner,"_resourceBroker",broker);Set(owner,"_resourceConverter",converter);
        Set(addon,"sessionId","fixture");Set(addon,"loadEpoch","load1");var scope=new LifeSupportTelemetryScope();Set(addon,"lifeSupportScopes",scope);
        var context=typeof(WorldBridgeAddon).GetProperty("ProductionContext",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(addon).ToString();
        var method=typeof(WorldBridgeAddon).GetMethod("QualifyLifeSupport",BindingFlags.Instance|BindingFlags.NonPublic);int cases=0;
        ConversionRecipe Recipe(bool ec=false){var r=new ConversionRecipe();r.Inputs.Add(new ResourceRatio(ec?"ElectricCharge":"Supplies",ec?(double)(.01f*5):(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL});if(!ec)r.Outputs.Add(new ResourceRatio("Mulch",(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL});return r;}
        object Call(ConversionRecipe r,Part p=null,ResourceConverter c=null,PartModule module=null,float efficiency=1,double seconds=2,double ut=100)=>method.Invoke(addon,new object[]{c??converter,seconds,r,p??part,module,efficiency,ut});
        void Case(bool expected,Action mutate=null,Action restore=null,bool ec=false,bool bind=true,Part p=null,ResourceConverter c=null,PartModule module=null,float efficiency=1,double seconds=2,double ut=100)
        {var f=scope.Enter(owner,context,true);var r=Recipe(ec);if(bind)scope.Bind(owner,r,ec);mutate?.Invoke();object result=Call(r,p,c,module,efficiency,seconds,ut);restore?.Invoke();scope.Exit(f);if((result!=null)!=expected)throw new Exception("Qualification case failed "+cases);cases++;}
        Case(true);Case(true,ec:true);Case(false,bind:false);Case(false,efficiency:.5f);Case(false,seconds:0);Case(false,ut:97);Case(false,seconds:99);
        Case(false,()=>Set(owner,"_currentCrewCount",0),()=>Set(owner,"_currentCrewCount",5));Case(false,()=>vessel.loaded=false,()=>vessel.loaded=true);Case(false,()=>vessel.vesselType=VesselType.EVA,()=>vessel.vesselType=VesselType.Base);
        Case(false,()=>Set(owner,"_resourceConverter",null),()=>Set(owner,"_resourceConverter",converter));Case(false,()=>Set(owner,"_resourceBroker",null),()=>Set(owner,"_resourceBroker",broker));Case(false,()=>Set(persistence,"_Settings",null),()=>Set(persistence,"_Settings",settings));
        Case(false,module:(PartModule)New(typeof(ModuleResourceConverter)));
        var wrongPart=(Part)New(typeof(Part));wrongPart.vessel=vessel;Case(false,p:wrongPart);var wrongConverter=(ResourceConverter)New(typeof(ResourceConverter));Set(wrongConverter,"_broker",broker);Case(false,c:wrongConverter);
        var otherVessel=(Vessel)New(typeof(Vessel));otherVessel.loaded=true;Case(false,()=>part.vessel=otherVessel,()=>part.vessel=vessel);
        var outer=scope.Enter(owner,context,true);var original=Recipe();scope.Bind(owner,original,false);var child=scope.Enter(owner,context,false);if(Call(original)!=null)throw new Exception("Nested sentinel leaked");scope.Exit(child);if(Call(original)==null||Call(original)!=null)throw new Exception("One use provenance failed");scope.Exit(outer);cases+=2;
        // Exact getters, when already initialized, are pure native recipe construction.
        // Do not call FixedUpdate, resource APIs or lazy state getters in fixtures.
        var nativeSupply=ownerType.GetProperty("SupplyRecipe",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(owner) as ConversionRecipe;
        var nativeEc=ownerType.GetProperty("ECRecipe",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(owner) as ConversionRecipe;
        if(nativeSupply.Inputs[0].Ratio!=(double)(.0005f*5*.5f)||nativeSupply.Outputs[0].ResourceName!="Mulch"||nativeEc.Inputs[0].Ratio!=(double)(.01f*5))throw new Exception("Native initialized recipe differs");cases+=2;
        foreach(var pair in new[]{(recipe:nativeSupply,ec:false),(recipe:nativeEc,ec:true)}){var f=scope.Enter(owner,context,true);scope.Bind(owner,pair.recipe,pair.ec);if(Call(pair.recipe)==null)throw new Exception("Exact native initialized recipe rejected");scope.Exit(f);cases++;}
        // Exercise the actual Bridge mean-mode guard with detached managed fields.
        // A nonzero fake cached pointer avoids Unity's external liveness probe;
        // no engine object is constructed or native engine method invoked.
        Set(vessel,"m_CachedPtr",new IntPtr(1));vessel.id=Guid.NewGuid();vessel.packed=false;
        var comparer=new VesselReferenceComparer();
        Set(addon,"productionModes",Activator.CreateInstance(Field(typeof(WorldBridgeAddon),"productionModes").FieldType,new object[]{comparer}));Set(addon,"averageModes",Activator.CreateInstance(Field(typeof(WorldBridgeAddon),"averageModes").FieldType,new object[]{comparer}));
        var accumulator=new PowerAverageAccumulator();Set(addon,"powerAverage",accumulator);var guard=typeof(WorldBridgeAddon).GetMethod("EnsureAverageMode",BindingFlags.Instance|BindingFlags.NonPublic);
        void Complete(){accumulator.Reset(context);accumulator.Tick(context,0,100);accumulator.Add(vessel.id.ToString("D"),100,102,4,2,"fulfilled-part-requests",false,0);accumulator.Tick(context,60,102);}
        if(!(bool)guard.Invoke(addon,new object[]{vessel}))throw new Exception("Initial mean mode missing");Complete();vessel.packed=true;guard.Invoke(addon,new object[]{vessel});if(accumulator.Observe(vessel.id.ToString("D"),60)!=null)throw new Exception("Unannounced packing retained mean");cases++;
        Complete();var mode=(ProductionTelemetryMode)typeof(WorldBridgeAddon).GetMethod("GetProductionMode",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(addon,new object[]{vessel});mode.Transition(false);mode.Transition(true);guard.Invoke(addon,new object[]{vessel});if(accumulator.Observe(vessel.id.ToString("D"),60)!=null)throw new Exception("Same UT roundtrip retained mean");cases++;
        Complete();Set(addon,"productionModes",Activator.CreateInstance(Field(typeof(WorldBridgeAddon),"productionModes").FieldType,new object[]{comparer}));guard.Invoke(addon,new object[]{vessel});if(accumulator.Observe(vessel.id.ToString("D"),60)!=null)throw new Exception("Mode owner replacement retained mean");cases++;
        mode=(ProductionTelemetryMode)typeof(WorldBridgeAddon).GetMethod("GetProductionMode",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(addon,new object[]{vessel});mode.Transition(false);if((bool)guard.Invoke(addon,new object[]{vessel}))throw new Exception("Pending mode transition accepted");cases++;
        var producer=(BaseConverter)New(typeof(ModuleResourceConverter));Set(producer,"lastUpdateTime",50d);var endMethod=typeof(WorldBridgeAddon).GetMethod("NativeProducerProcessedEnd",BindingFlags.Static|BindingFlags.NonPublic);
        if((double)endMethod.Invoke(null,new object[]{producer,100d,2d})!=50d)throw new Exception("Producer catch-up interval relabeled current");cases++;
        Set(producer,"lastUpdateTime",101d);if(endMethod.Invoke(null,new object[]{producer,100d,2d})!=null)throw new Exception("Future producer interval accepted");cases++;
        return cases;
    }
    sealed class VesselReferenceComparer:IEqualityComparer<Vessel>
    {public bool Equals(Vessel a,Vessel b)=>ReferenceEquals(a,b);public int GetHashCode(Vessel v)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v);}
}
