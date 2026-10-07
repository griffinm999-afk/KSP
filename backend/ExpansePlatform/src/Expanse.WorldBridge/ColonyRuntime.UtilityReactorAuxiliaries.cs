using System;
using System.Collections;
using System.Globalization;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        const string NfeIdentity="|NearFutureElectrical|1.0.0.0";
        static bool IsReactorAuxiliary(PartModule module)
        {
            string identity=UtilityModuleIdentity(module);
            return identity=="NearFutureElectrical.ModuleUpdateOverride"+NfeIdentity ||
                identity=="NearFutureElectrical.FissionGenerator"+NfeIdentity ||
                identity=="NearFutureElectrical.RadioactiveStorageContainer"+NfeIdentity ||
                identity=="ModuleDeployableSolarPanel|Assembly-CSharp|0.0.0.0" ||
                identity=="Kopernicus.Components.KopernicusSolarPanel|Kopernicus|1.0.244.0";
        }
        static void AuxiliaryRequire(bool valid,string reason)
        {if(!valid)throw new InvalidOperationException(reason);}
        static int AuxiliaryIndex(PartModule module,Vessel vessel,ProtoPartSnapshot proto)
        {
            AuxiliaryRequire(module!=null && vessel!=null && module.part!=null,"Auxiliary native owner is absent.");
            var native=vessel.loaded ? module.part.Modules.Cast<PartModule>().ToArray() : proto?.partInfo?.partPrefab?.Modules.Cast<PartModule>().ToArray();
            AuxiliaryRequire(native!=null && native.Length<=128 && native.Count(m=>ReferenceEquals(m,module))==1,"Auxiliary native module membership is ambiguous.");
            if(vessel.loaded)AuxiliaryRequire(module.part.vessel==vessel && vessel.parts!=null && vessel.parts.Contains(module.part),"Auxiliary loaded owner changed.");
            else AuxiliaryRequire(vessel.protoVessel!=null && vessel.protoVessel.protoPartSnapshots.Contains(proto),"Auxiliary actual proto owner changed.");
            int index=Array.FindIndex(native,m=>ReferenceEquals(m,module));
            var info=vessel.loaded ? module.part.partInfo : proto.partInfo;
            var config=UtilityNativeConfiguration(info,index);
            AuxiliaryRequire(config.GetValue("name")==module.moduleName,"Auxiliary installed configuration/order changed.");
            if(!vessel.loaded)AuxiliaryRequire(index<proto.modules.Count && proto.modules[index].moduleName==module.moduleName,"Auxiliary saved module order changed.");
            return index;
        }
        static object AuxiliaryState(PartModule module,Vessel vessel,ProtoPartSnapshot proto,string name)
        {
            if(vessel.loaded)return UtilityRead(module,name);
            int index=AuxiliaryIndex(module,vessel,proto);var node=proto.modules[index].moduleValues;
            AuxiliaryRequire(node!=null && node.GetValues(name).Length==1,"Auxiliary saved "+name+" is absent or ambiguous.");return node.GetValue(name);
        }
        static bool AuxiliaryBool(PartModule module,Vessel vessel,ProtoPartSnapshot proto,string name)
        {
            AuxiliaryRequire(bool.TryParse(Convert.ToString(AuxiliaryState(module,vessel,proto,name),CultureInfo.InvariantCulture),out bool value),"Auxiliary "+name+" is invalid.");return value;
        }
        static double AuxiliaryNumber(PartModule module,Vessel vessel,ProtoPartSnapshot proto,string name)
        {
            AuxiliaryRequire(double.TryParse(Convert.ToString(AuxiliaryState(module,vessel,proto,name),CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out double value) && Finite(value),"Auxiliary "+name+" is invalid.");return value;
        }
        static void AuxiliaryNoHandler(PartModule module,Vessel vessel,ProtoPartSnapshot proto)
        {
            int index=AuxiliaryIndex(module,vessel,proto);
            var config=UtilityNativeConfiguration(vessel.loaded ? module.part.partInfo : proto.partInfo,index);
            AuxiliaryRequire(config.GetNodes("RESOURCE").Length==0 && config.GetNodes("INPUT_RESOURCE").Concat(config.GetNodes("OUTPUT_RESOURCE")).All(n=>!n.HasValue("name") || n.GetValue("name")==""),"Auxiliary has configured independent handler resources.");
            if(vessel.loaded)AuxiliaryRequire(module.resHandler!=null && module.resHandler.GetType()==typeof(ModuleResourceHandler) && module.resHandler.partModule==module && module.resHandler.inputResources.Count==0 && module.resHandler.outputResources.Count==0,"Auxiliary actual independent handler resources changed.");
        }
        static PartModule RequireInactiveLegacyReactor(PartModule module,Vessel vessel,ProtoPartSnapshot proto,ReactorUtilityWitness reactors)
        {
            AuxiliaryIndex(module,vessel,proto);
            var info=vessel.loaded ? module.part.partInfo : proto.partInfo;
            AuxiliaryRequire(info.name=="Duna.PDU" && reactors.Qualified,"Legacy auxiliary requires the actual qualified Duna SystemHeat reactor owner.");
            var members=(vessel.loaded ? module.part.Modules : proto.partInfo.partPrefab.Modules).Cast<PartModule>().ToArray();
            var sh=members.Where(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor").ToArray();
            AuxiliaryRequire(sh.Length==1 && reactors.Accounted.Contains(sh[0]),"Legacy auxiliary SystemHeat owner is not accounted.");
            var legacy=members.Where(m=>UtilityModuleIdentity(m)=="NearFutureElectrical.FissionReactor"+NfeIdentity).ToArray();
            AuxiliaryRequire(legacy.Length==1 && !AuxiliaryBool(legacy[0],vessel,proto,"IsActivated") && AuxiliaryNumber(legacy[0],vessel,proto,"AvailablePower")==0,"Legacy native reactor is active or still distributing thermal power.");
            var update=members.Where(m=>UtilityModuleIdentity(m)=="NearFutureElectrical.ModuleUpdateOverride"+NfeIdentity).ToArray();
            var generators=members.Where(m=>UtilityModuleIdentity(m)=="NearFutureElectrical.FissionGenerator"+NfeIdentity).ToArray();
            var cores=members.Where(m=>UtilityModuleIdentity(m)=="NearFutureElectrical.ModuleCoreHeatNoCatchup"+NfeIdentity).ToArray();
            AuxiliaryRequire(update.Length==1 && generators.Length==1 && cores.Length==1,"Legacy native stack is incomplete or ambiguous.");
            foreach(var owner in legacy.Concat(update).Concat(generators))AuxiliaryNoHandler(owner,vessel,proto);
            AuxiliaryRequire(AuxiliaryNumber(generators[0],vessel,proto,"CurrentGeneration")==0 && AuxiliaryNumber(generators[0],vessel,proto,"CurrentHeatUsed")==0,"Legacy native generator retains thermal/electrical output.");
            if(vessel.loaded)
            {
                var delegated=ReviewedPrivateField(update[0],"reactors") as IEnumerable;
                AuxiliaryRequire(delegated!=null && delegated.Cast<object>().SequenceEqual(legacy.Cast<object>()) && ReferenceEquals(ReviewedPrivateField(legacy[0],"core"),cores[0]),"Legacy native callback/core owner changed.");
                var converter=legacy[0] as BaseConverter;
                AuxiliaryRequire(converter!=null && !converter.GeneratesHeat && converter.lastHeatFlux==0,"Inactive legacy reactor retains generated heat.");
            }
            return legacy[0];
        }
        static bool TryReactorAuxiliary(PartModule module,Vessel vessel,ProtoPartSnapshot proto,ReactorUtilityWitness reactors,ConfigNode[] backgroundAdapters,out bool heatSafe,out string reason)
        {
            heatSafe=true;reason="";
            try
            {
                AuxiliaryIndex(module,vessel,proto);string identity=UtilityModuleIdentity(module);
                if(identity=="ModuleDeployableSolarPanel|Assembly-CSharp|0.0.0.0" || identity=="Kopernicus.Components.KopernicusSolarPanel|Kopernicus|1.0.244.0")
                {
                    var members=(vessel.loaded ? module.part.Modules : proto.partInfo.partPrefab.Modules).Cast<PartModule>().ToArray();
                    var stock=members.Where(m=>UtilityModuleIdentity(m)=="ModuleDeployableSolarPanel|Assembly-CSharp|0.0.0.0").Cast<ModuleDeployableSolarPanel>().ToArray();
                    var kop=members.Where(m=>UtilityModuleIdentity(m)=="Kopernicus.Components.KopernicusSolarPanel|Kopernicus|1.0.244.0").ToArray();
                    AuxiliaryRequire(stock.Length==1 && kop.Length==1,"Solar output-only pairing is ambiguous.");
                    int i=AuxiliaryIndex(stock[0],vessel,proto);var config=UtilityNativeConfiguration(vessel.loaded ? module.part.partInfo : proto.partInfo,i);
                    AuxiliaryRequire(config.GetValue("resourceName")=="ElectricCharge" && double.TryParse(config.GetValue("chargeRate"),NumberStyles.Float,CultureInfo.InvariantCulture,out double rate) && Finite(rate) && rate>=0 && rate<=1e6 && config.GetNodes("INPUT_RESOURCE").Length==0 && config.GetNodes("RESOURCE").Length==0 && config.GetNodes("OUTPUT_RESOURCE").Length==0,"Solar installed resource/output terms changed.");
                    AuxiliaryNoHandler(kop[0],vessel,proto);
                    if(vessel.loaded)
                    {
                        var supported=UtilityRead(kop[0],"SolarPanel");
                        AuxiliaryRequire(supported!=null && supported.GetType().FullName=="Kopernicus.Components.KopernicusSolarPanel+StockPanel" && ReferenceEquals(UtilityRead(supported,"TargetModule"),stock[0]),"Solar native stock/Kopernicus binding changed.");
                        AuxiliaryRequire(Convert.ToString(UtilityRead(kop[0].GetType(),"resourceName"),CultureInfo.InvariantCulture)=="ElectricCharge" && stock[0].resourceName=="ElectricCharge" && stock[0].resHandler!=null && stock[0].resHandler.inputResources.Count==0 && stock[0].resHandler.outputResources.Count==1 && stock[0].resHandler.outputResources.All(r=>r.name=="ElectricCharge" && Finite(r.rate) && r.rate==0),"Solar actual input/output resources changed.");
                        AuxiliaryRequire(UtilityNumber(kop[0],"nominalRate",double.NaN)>=0 && Finite(UtilityNumber(kop[0],"nominalRate",double.NaN)) && UtilityNumber(kop[0],"currentOutput",double.NaN)>=0 && Finite(UtilityNumber(kop[0],"currentOutput",double.NaN)),"Solar native output sign/rating is invalid.");
                        // Native Kopernicus uses negative RequestResource. Invalid
                        // overfilled tanks could invert its requested sign.
                        AuxiliaryRequire(vessel.parts.Where(p=>p!=null).Select(p=>p.Resources.Get("ElectricCharge")).Where(r=>r!=null).All(r=>Finite(r.amount)&&Finite(r.maxAmount)&&r.amount>=0&&r.amount<=r.maxAmount),"Solar actual EC inventory is invalid.");
                    }
                    else
                    {
                        AuxiliaryBool(stock[0],vessel,proto,"isEnabled");AuxiliaryBool(kop[0],vessel,proto,"isEnabled");
                        string saved=Convert.ToString(AuxiliaryState(stock[0],vessel,proto,"deployState"),CultureInfo.InvariantCulture);
                        AuxiliaryRequire(saved=="RETRACTED" || saved=="EXTENDED" || saved=="BROKEN","Solar saved state is moving or unknown.");
                    }
                    reason="Exact native stock/Kopernicus EC output-only pair accounted; no intermittent generation credited.";return true;
                }
                RequireInactiveLegacyReactor(module,vessel,proto,reactors);
                if(identity=="NearFutureElectrical.RadioactiveStorageContainer"+NfeIdentity)
                {
                    AuxiliaryNoHandler(module,vessel,proto);
                    AuxiliaryRequire(Convert.ToString(UtilityRead(module,"DangerousFuel"),CultureInfo.InvariantCulture)=="DepletedFuel" && Convert.ToString(UtilityRead(module,"SafeFuel"),CultureInfo.InvariantCulture)=="EnrichedUranium" && UtilityNumber(module,"HeatFluxPerWasteUnit",double.NaN)==5,"Legacy radioactive storage native terms changed.");
                    if(vessel.loaded)AuxiliaryRequire(ReviewedPrivateField(module,"transferring") is bool transferring && !transferring,"Legacy native fuel/waste transfer is active or unknown.");
                    double amount,capacity;
                    if(vessel.loaded){var tank=module.part.Resources.Get("DepletedFuel");AuxiliaryRequire(tank!=null,"Legacy decay waste tank absent.");amount=tank.amount;capacity=tank.maxAmount;}
                    else{var tank=proto.resources.SingleOrDefault(r=>r.resourceName=="DepletedFuel");AuxiliaryRequire(tank!=null,"Legacy saved decay waste tank absent.");amount=tank.amount;capacity=tank.maxAmount;}
                    AuxiliaryRequire(Finite(amount)&&Finite(capacity)&&amount>=0&&amount<=capacity&&capacity==40,"Legacy decay waste inventory differs from the reviewed native tank.");
                    heatSafe=RadioactiveDecayBound(module,vessel,proto,reactors,backgroundAdapters,amount,capacity,out reason);
                    return true;
                }
                AuxiliaryRequire(identity=="NearFutureElectrical.ModuleUpdateOverride"+NfeIdentity || identity=="NearFutureElectrical.FissionGenerator"+NfeIdentity,"Legacy auxiliary implementation changed.");
                reason="Exact inactive legacy NFE owner stack accounted; no legacy generation credited.";return true;
            }
            catch(Exception ex){heatSafe=false;reason="Native auxiliary hold "+module?.moduleName+": "+Bound(ex.Message,256);return false;}
        }
        // This bound is recomputed from current native stock on EVERY utility
        // observation. A retained SystemHeat proof never bypasses it. Services
        // may repair/refuel a held facility; the next observation must re-evaluate.
        static bool RadioactiveDecayBound(PartModule module,Vessel vessel,ProtoPartSnapshot proto,ReactorUtilityWitness reactors,ConfigNode[] backgroundAdapters,double waste,double capacity,out string reason)
        {
            var info=vessel.loaded ? module.part.partInfo : proto.partInfo;
            var members=(vessel.loaded ? module.part.Modules : info.partPrefab.Modules).Cast<PartModule>().ToArray();
            AuxiliaryRequire(members.Count(m=>UtilityModuleIdentity(m)=="NearFutureElectrical.RadioactiveStorageContainer"+NfeIdentity)==1,"Multiple native decay sources require a summed thermal bound.");
            var sh=members.Single(m=>m.GetType().FullName=="SystemHeat.ModuleSystemHeatFissionReactor");
            AuxiliaryRequire(SupportedSystemHeat(sh) && reactors.Accounted.Contains(sh),"Decay bound requires its exact accounted native SystemHeat owner.");
            foreach(var owner in members.OfType<BaseConverter>())AuxiliaryRequire(!AuxiliaryBool(owner,vessel,proto,"IsActivated"),"Another local converter is active; decay feed/heat ownership is unknown.");
            foreach(var owner in members.Where(m=>m!=sh && m.resHandler!=null))AuxiliaryRequire(!owner.resHandler.inputResources.Concat(owner.resHandler.outputResources).Any(r=>r.name=="EnrichedUranium" || r.name=="DepletedFuel"),"Another local handler feeds reactor fuel/waste.");
            var definition=PartResourceLibrary.Instance.GetDefinition("EnrichedUranium");
            AuxiliaryRequire(definition!=null && definition.resourceFlowMode==ResourceFlowMode.NO_FLOW,"Decay fuel is not an exclusive local NO_FLOW resource.");
            var resourceNames=vessel.loaded ? module.part.Resources.Cast<PartResource>().Select(r=>r.resourceName).ToArray() : proto.resources.Select(r=>r.resourceName).ToArray();
            AuxiliaryRequire(resourceNames.All(name=>
            {
                var resource=PartResourceLibrary.Instance.GetDefinition(name);
                return resource!=null && Finite(resource.density) && resource.density>=0 && Finite(resource.specificHeatCapacity) && resource.specificHeatCapacity>=0;
            }),"Native resource thermal mass can be negative or unknown.");
            double fuel;
            if(vessel.loaded)
            {
                var tank=module.part.Resources.Get("EnrichedUranium");AuxiliaryRequire(tank!=null && tank.flowState && tank.maxAmount==40,"Current local decay fuel tank is unavailable or changed.");fuel=tank.amount;
                RequireDecayRecipe((ReviewedPrivateField(sh,"inputs") as IEnumerable)?.Cast<object>().ToArray(),(ReviewedPrivateField(sh,"outputs") as IEnumerable)?.Cast<object>().ToArray());
            }
            else
            {
                var tank=proto.resources.SingleOrDefault(r=>r.resourceName=="EnrichedUranium");AuxiliaryRequire(tank!=null && tank.flowState && tank.maxAmount==40,"Actual saved local decay fuel tank is unavailable or changed.");fuel=tank.amount;
                var attached=UtilityRead(vessel,"vesselModules") as IEnumerable;var processors=attached?.Cast<object>().Where(p=>p!=null && p.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
                AuxiliaryRequire(processors!=null && processors.Length==1 && RemoteBrpInventoryGateway.SupportedProviderAvailable,"Current native decay background observation is unavailable.");
                var converters=(UtilityRead(processors[0],"Converters") as IEnumerable)?.Cast<object>().Take(513).ToArray();
                AuxiliaryRequire(converters!=null && converters.Length<=512 && converters.All(c=>UtilityRead(c,"FlightId") is uint flight && flight!=0 && UtilityRead(c,"ModuleId") is uint mid && mid!=0),"Actual decay background recipe owner identity is unknown.");
                var storageSaved=proto.modules[AuxiliaryIndex(module,vessel,proto)].moduleValues;
                uint storageId=0;
                if(storageSaved.HasValue("persistentId"))AuxiliaryRequire(uint.TryParse(storageSaved.GetValue("persistentId"),out storageId) && storageId!=0,"Saved radioactive module identity is invalid.");
                AuxiliaryRequire(storageId==0 || !converters.Any(c=>UtilityRead(c,"FlightId") is uint flight && flight==proto.flightID && UtilityRead(c,"ModuleId") is uint mid && mid==storageId),"Actual radioactive module has an unreviewed background/catch-up recipe.");
                int index=AuxiliaryIndex(sh,vessel,proto);uint id;
                AuxiliaryRequire(uint.TryParse(proto.modules[index].moduleValues.GetValue("persistentId"),out id) && id!=0,"Saved decay reactor identity is absent.");
                var recipes=converters.Where(c=>UtilityRead(c,"FlightId") is uint flight && flight==proto.flightID && UtilityRead(c,"ModuleId") is uint mid && mid==id).ToArray();
                AuxiliaryRequire(recipes.Length==1,"Decay stock lacks one exact current native reactor recipe.");
                RequireDecayRecipe(AuxiliaryRecipeRows(recipes[0],"Inputs"),AuxiliaryRecipeRows(recipes[0],"Outputs").Where(r=>Convert.ToString(UtilityRead(r,"ResourceName"),CultureInfo.InvariantCulture)!="ElectricCharge").ToArray());
            }
            AuxiliaryRequire(Finite(fuel) && fuel>=0 && fuel<=40,"Current local decay fuel is invalid.");
            AuxiliaryRequire(backgroundAdapters!=null && !backgroundAdapters.Any(n=>n==null || n.GetValue("name")=="RadioactiveStorageContainer" || n.GetValue("name")=="NearFutureElectrical.RadioactiveStorageContainer"),"A radioactive catch-up/background adapter requires separate thermal accounting.");
            // Native RSC only adds heat in Flight FixedUpdate; its pinned type
            // has no OnLoad/catch-up update. Proto uses the actual saved baseline.
            // FlightIntegrator caps skin at max(0.1,dryThermalMass/2), then
            // subtracts it. Positive resource thermal mass is never credited.
            var config=info.partConfig;double configuredModifier=1;
            if(config.HasValue("thermalMassModifier"))AuxiliaryRequire(double.TryParse(config.GetValue("thermalMassModifier"),NumberStyles.Float,CultureInfo.InvariantCulture,out configuredModifier),"Configured native thermal modifier is malformed.");
            AuxiliaryRequire(configuredModifier==1 && info.partPrefab.thermalMassModifier==1,"Native thermal mass modifier differs from the reviewed package.");
            foreach(var owner in members)
            {
                string identity=UtilityModuleIdentity(owner);
                if(identity=="TweakScale.TweakScale|Scale|3.3.2.0")AuxiliaryRequire(!AuxiliaryBool(owner,vessel,proto,"isEnabled") && AuxiliaryNumber(owner,vessel,proto,"currentScaleFactor")==1 && AuxiliaryNumber(owner,vessel,proto,"extraMass")==0,"Saved/current dynamic scaling changes the dry mass baseline.");
                else if(owner is IPartMassModifier)
                {
                    if(identity=="kOS.Module.kOSProcessor|kOS|1.6.0.1")AuxiliaryRequire(AuxiliaryNumber(owner,vessel,proto,"baseModuleMass")==0 && (vessel.loaded ? UtilityNumber(owner,"AdditionalMass",double.NaN)==0 : AuxiliaryNumber(owner,vessel,proto,"diskSpaceMassFactor")>=0 && AuxiliaryNumber(owner,vessel,proto,"diskSpace")==AuxiliaryNumber(owner,vessel,proto,"baseDiskSpace")),"Native kOS variable mass is outside this zero-addon dry mass bound.");
                    else if(identity=="KIS.ModuleKISInventory|KIS|1.29.8039.40483")AuxiliaryRequire(vessel.loaded ? UtilityNumber(owner,"contentsMass",double.NaN)==0 : proto.modules[AuxiliaryIndex(owner,vessel,proto)].moduleValues.GetNodes("ITEM").Length==0,"Cargo-dependent dry mass is outside this empty-inventory bound.");
                    else throw new InvalidOperationException("Unknown native dry mass modifier "+owner.GetType().FullName+".");
                }
            }
            double mass=vessel.loaded ? module.part.mass : proto.mass;
            double baselineMass;
            AuxiliaryRequire(double.TryParse(config.GetValue("mass"),NumberStyles.Float,CultureInfo.InvariantCulture,out baselineMass),"Installed native dry mass baseline is malformed.");
            double cp=PhysicsGlobals.StandardSpecificHeatCapacity;
            AuxiliaryRequire(Finite(mass)&&Finite(baselineMass)&&Finite(cp)&&mass>0&&baselineMass>0&&cp>0,"Native dry mass/heat capacity baseline is unavailable.");
            double dry=Math.Min(mass,baselineMass)*cp*configuredModifier;
            double lower=Math.Max(.1,dry-Math.Max(.1,dry*.5));
            double temperature=vessel.loaded ? module.part.temperature : proto.temperature;
            double skin=vessel.loaded ? module.part.skinTemperature : proto.skinTemperature;
            double limit=Math.Min(info.partPrefab.maxTemp,info.partPrefab.skinMaxTemp);
            double pending=0;
            if(vessel.loaded)
            {
                AuxiliaryRequire(module.part.thermalMassModifier==configuredModifier && Finite(module.part.thermalMass) && module.part.thermalMass>=lower && Finite(module.part.thermalInternalFlux),"Current native internal thermal mass/pending energy is unknown.");
                pending=Math.Max(0,module.part.thermalInternalFlux);
                limit=Math.Min(limit,Math.Min(module.part.maxTemp,module.part.skinMaxTemp));
            }
            AuxiliaryRequire(Finite(temperature)&&Finite(skin)&&temperature>0&&skin>0&&Finite(limit)&&limit>100,"Native saved/current decay temperature baseline is unavailable.");
            double horizon=RequiredUtilityEndurance(vessel),wasteUpper=Math.Min(capacity,waste+fuel);
            AuxiliaryRequire(Finite(horizon)&&horizon>0 && Finite(wasteUpper),"Decay reserve horizon is invalid.");
            // RSC casts amount to float BEFORE its float multiply. One upward
            // float ULP bounds both rounding stages, not an arbitrary tolerance.
            float nativeHeat=(float)wasteUpper*5f;
            int bits=BitConverter.ToInt32(BitConverter.GetBytes(nativeHeat),0);
            double heatUpper=nativeHeat==0 ? 0 : BitConverter.ToSingle(BitConverter.GetBytes(bits+1),0);
            double rise=(heatUpper*horizon+pending)/lower;
            double peak=Math.Max(temperature,skin)+rise;
            AuxiliaryRequire(Finite(heatUpper)&&Finite(rise)&&Finite(peak),"Native decay energy bound overflows.");
            bool safe=peak<=limit-100;
            reason="Live recomputed native Part decay bound: fuel/waste="+fuel.ToString("R",CultureInfo.InvariantCulture)+"/"+waste.ToString("R",CultureInfo.InvariantCulture)+"; immediate waste upper="+wasteUpper.ToString("R",CultureInfo.InvariantCulture)+"; float heat upper="+heatUpper.ToString("R",CultureInfo.InvariantCulture)+" kW; reserve="+horizon.ToString("R",CultureInfo.InvariantCulture)+" s; dry internal lower="+lower.ToString("R",CultureInfo.InvariantCulture)+" kJ/K; pending="+pending.ToString("R",CultureInfo.InvariantCulture)+" kJ; baseline="+Math.Max(temperature,skin).ToString("R",CultureInfo.InvariantCulture)+" K; adiabatic peak="+peak.ToString("R",CultureInfo.InvariantCulture)+" K; limit="+limit.ToString("R",CultureInfo.InvariantCulture)+" K; safe="+safe+". No cooling credit; "+(vessel.loaded ? "current native temperature/energy" : "actual saved baseline, no native radioactive catch-up")+"; stock/additions re-evaluated each observation.";
            return safe;
        }
        static object[] AuxiliaryRecipeRows(object recipe,string name)
        {
            var rows=(UtilityRead(recipe,name) as IEnumerable)?.Cast<object>().Take(18).Select(e=>UtilityRead(e,"Value")).ToArray();
            AuxiliaryRequire(rows!=null && rows.Length<=17 && rows.All(r=>r!=null),"Current decay reactor recipe is unavailable or exceeds its bound.");return rows;
        }
        static void RequireDecayRecipe(object[] inputs,object[] outputs)
        {
            AuxiliaryRequire(inputs!=null && outputs!=null && inputs.Length==1 && outputs.Length==1 &&
                Convert.ToString(UtilityRead(inputs[0],"ResourceName"),CultureInfo.InvariantCulture)=="EnrichedUranium" && Convert.ToString(UtilityRead(outputs[0],"ResourceName"),CultureInfo.InvariantCulture)=="DepletedFuel" &&
                Convert.ToString(UtilityRead(inputs[0],"FlowMode"),CultureInfo.InvariantCulture)=="NO_FLOW" && Convert.ToString(UtilityRead(outputs[0],"FlowMode"),CultureInfo.InvariantCulture)=="NO_FLOW" &&
                PositiveFinite(UtilityNumber(inputs[0],"Ratio",double.NaN)) && UtilityNumber(inputs[0],"Ratio",double.NaN)==UtilityNumber(outputs[0],"Ratio",double.NaN),"Decay requires exact local NO_FLOW one-for-one native fuel/waste stoichiometry.");
        }
        static bool TryInactiveLegacyCore(PartModule module,Vessel vessel,ProtoPartSnapshot proto,ReactorUtilityWitness reactors,out string reason)
        {
            reason="";
            try{AuxiliaryRequire(UtilityModuleIdentity(module)=="NearFutureElectrical.ModuleCoreHeatNoCatchup"+NfeIdentity,"Legacy core implementation changed.");RequireInactiveLegacyReactor(module,vessel,proto,reactors);return true;}
            catch(Exception ex){reason="Native legacy core hold: "+Bound(ex.Message,256);return false;}
        }
    }
}
