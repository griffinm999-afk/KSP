using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // These modules have no continuous EC demand of their own. Energy-using
        // modules are accounted separately; an unreviewed module blocks certification.
        static readonly HashSet<string> PassiveUtilityModules=new HashSet<string>(new[]{
            "ModuleCargoPart","ModuleInventoryPart","ModuleScienceContainer","ModuleScienceExperiment","ModuleSAS","ModuleKerbNetAccess","ModuleAntennaInfo",
            "FlagDecal","PlanetaryModule","ModuleKPBSDependentLight","ModuleAnimateGeneric","ModuleConnectedLivingSpace","ModuleFreeIva","ModuleIVASwitch","ModuleB9PartInfo","ModuleB9PartSwitch",
            "KISInventory","ModuleKISInventory","ModuleKISItem","ModuleKISPickup","USI_ModuleRecycleablePart","USI_ModuleFieldRepair","ModuleAutoRepairer","ModuleLogisticsConsumer","ModuleResourceDistributor","USI_ModuleResourceWarehouse","ModulePowerCoupler",
            "ModuleGroundPart","ModuleGroundSciencePart","ModuleGroundExperiment","ExpanseLandingController","ExpanseColonyPlacementMarker","ColonyPlacementMarker","ExpanseAnchor","KOSNameTag","TweakScale",
            "ModuleTripLogger","ModuleStructuralNode","ModulePartVariants","FXModuleLookAtConstraint",
            "FMRS_PM","MechJebCore","RMMModule","ProbeControlRoomPart","RasterPropMonitorComputer","SCANRPMStorage","TrajectoriesVesselSettings","FlightEngineerModule"
            ,"MKSModule","ModuleColonyRewards","ModuleColorChanger","ModuleExperienceManagement","ModuleKonstructionForeman","ModuleKonstructionHelper","ModuleOrbitalLogistics","ModulePlanetaryLogistics","ModuleProbeControlPoint","USI_InertialDampener"
        },StringComparer.Ordinal);
        sealed class UtilityStorageSample {public string Epoch;public double Ut,Amount;}
        static readonly Dictionary<string,UtilityStorageSample> UtilityStorageSamples=new Dictionary<string,UtilityStorageSample>(StringComparer.Ordinal);

        private void PopulateUtilityEnvironment(ColonyEnvironment env)
        {
            PopulateUtilityEnvironment(env, new ColonyEnvironmentConfigObservation());
        }
        private void PopulateUtilityEnvironment(ColonyEnvironment env, ColonyEnvironmentConfigObservation environmentConfigs)
        {
            if(state==null || FlightGlobals.Vessels==null)return;
            EnsureUtilityObservers(ContextKey);
            EnsureRecipeObserver(ContextKey);
            var backgroundAdapters=ReadUtilityAdapterConfiguration(environmentConfigs);
            var instructionObservation=CreateUtilityInstructionObservation();
            foreach(var facility in state.Colonies.SelectMany(c=>c.Facilities))
            {
                var vessels=FlightGlobals.Vessels.Where(v=>v!=null && v.id.ToString("D")==facility.VesselId).Take(2).ToArray();
                if(vessels.Length!=1)continue;
                env.Services.Utilities.Add(ReadUtilities(vessels[0],facility.Id,ContextKey,env.Ut,backgroundAdapters,instructionObservation));
            }
            ApplyDistributionWitnesses(env.Services.Utilities,ContextKey,env.Ut,backgroundAdapters,instructionObservation);
        }
        public static ColonyQualification QualifyPlacedFacility(Vessel vessel,ColonyTemplate template)
        {
            if(vessel==null || template==null || Current==null)return new ColonyQualification {Provider="KSP.Utility.Provider",Context="unavailable"};
            double ut=Planetarium.GetUniversalTime();
            EnsureUtilityObservers(Current.ContextKey);EnsureRecipeObserver(Current.ContextKey);
            var backgroundAdapters=ReadUtilityAdapterConfiguration();
            var instructionObservation=CreateUtilityInstructionObservation();
            var report=ReadUtilities(vessel,"",Current.ContextKey,ut,backgroundAdapters,instructionObservation);
            var reports=new List<ColonyUtilityReport>{report};
            // Commissioning and management use the same current whole-site
            // distribution budget, including the candidate before registration.
            if(Current.state!=null && FlightGlobals.Vessels!=null)
            foreach(var id in Current.state.Colonies.SelectMany(c=>c.Facilities).Select(f=>f.VesselId).Distinct(StringComparer.Ordinal))
            {
                var owner=FlightGlobals.Vessels.SingleOrDefault(v=>v!=null && v.id.ToString("D")==id);
                if(owner!=null && owner!=vessel)reports.Add(ReadUtilities(owner,"",Current.ContextKey,ut,backgroundAdapters,instructionObservation));
            }
            ApplyDistributionWitnesses(reports,Current.ContextKey,ut,backgroundAdapters,instructionObservation);
            bool homes=template.Homes>0 && vessel.loaded && vessel.parts!=null && vessel.parts.Where(p=>p!=null && QualifyHomePart(vessel,p,template)).Sum(p=>p.CrewCapacity)>=template.Homes;
            string staffWitness;bool staffing=QualifyActualStaffing(vessel,template,out staffWitness);
            return new ColonyQualification {Provider="KSP.StockContinuousUtility.v1",Context=report.Context,ObservedUt=report.ObservedUt,
                EvidenceHash=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(report.Evidence+"|"+report.Reason+"|"+report.InputWitness+"|"+staffWitness)),PlacementStable=vessel.LandedOrSplashed,
                PowerReliable=ColonyUtilityQualification.PowerSupported(report),HeatSafe=ColonyUtilityQualification.HeatSupported(report),
                InputsAccessible=report.InputsAccessible,BackgroundSupported=report.BackgroundProviderQualified,
                HousingCertified=homes,StaffingQualified=staffing,ReactorContinuation=LoadedReactorProof(vessel)};
        }
        private static bool IsSupportedWorkModule(PartModule module,ProtoCrewMember crew)
        {
            if(module==null)return false;
            if(module.moduleName=="ModuleAutoRepairer")return crew==null || crew.experienceTrait!=null && crew.experienceTrait.TypeName=="Engineer";
            if(!(module is BaseConverter))return false;
            // Native specialist configuration is authoritative. Do not turn a
            // command seat or remote contract into an industrial worker.
            if(!UtilityBool(module,"UseSpecialistBonus"))return false;
            string effect=Convert.ToString(UtilityRead(module,"ExperienceEffect"),CultureInfo.InvariantCulture);
            return !string.IsNullOrWhiteSpace(effect) && (crew==null || crew.HasEffect(effect));
        }
        private static bool QualifyActualStaffing(Vessel vessel,ColonyTemplate template,out string witness)
        {
            witness="No physical workers required.";if(template.Workers==0)return true;
            var game=HighLogic.CurrentGame;var world=FlightGlobals.Vessels;
            if(game==null || game.CrewRoster==null || world==null || world.Count>2048 || !vessel.loaded || vessel.parts==null){witness="Current actual loaded workplace/roster provider unavailable.";return false;}
            var qualified=new List<string>();
            var operatorFacility=PowerOperatorFacility(vessel);var operatorEvidence="";
            foreach(var part in vessel.parts.Where(p=>p!=null && p.CrewCapacity>0 && p.protoModuleCrew.Count<=p.CrewCapacity))
            foreach(var crew in part.protoModuleCrew.Where(c=>c!=null && c.type==ProtoCrewMember.KerbalType.Crew && c.rosterStatus==ProtoCrewMember.RosterStatus.Assigned))
            {
                if(crew.experienceTrait==null || template.WorkerTrait.Length>0 && crew.experienceTrait.TypeName!=template.WorkerTrait)continue;
                bool nativeWork=part.Modules.Cast<PartModule>().Any(m=>IsSupportedWorkModule(m,crew));
                string operatorWitness="";bool operatorWork=crew.experienceTrait.TypeName=="Engineer"&&TryPaidPowerOperatorCabin(operatorFacility,vessel,part.persistentId,template,out operatorWitness);
                if(!nativeWork&&!operatorWork)continue;
                if(!ReferenceEquals(game.CrewRoster[crew.name],crew) || qualified.Any(q=>q.StartsWith(crew.name+"|",StringComparison.Ordinal)))continue;
                int membership=0;bool complete=true;
                foreach(var member in world.Where(v=>v!=null))
                {
                    if(member.loaded && member.parts!=null)membership+=member.parts.Where(p=>p!=null).Sum(p=>p.protoModuleCrew.Count(c=>c!=null && c.name==crew.name));
                    else if(member.protoVessel!=null)foreach(var proto in member.protoVessel.protoPartSnapshots.Where(p=>p!=null))
                    {
                        if(!proto.protoCrewNames.SequenceEqual(proto.protoModuleCrew.Where(c=>c!=null).Select(c=>c.name)))complete=false;
                        membership+=proto.protoModuleCrew.Count(c=>c!=null && c.name==crew.name);
                    }
                    else complete=false;
                }
                if(complete && membership==1){qualified.Add(crew.name+"|"+crew.experienceTrait.TypeName+"|"+part.persistentId);if(operatorWork)operatorEvidence=operatorWitness;}
            }
            witness="Actual same-vessel declared workplace occupants: "+string.Join(";",qualified.OrderBy(x=>x,StringComparer.Ordinal))+ (operatorEvidence.Length==0?"":"; "+operatorEvidence);
            return qualified.Count>=template.Workers;
        }
        public static bool QualifyHomePart(Vessel vessel,Part part,ColonyTemplate template)
        {
            if(vessel==null || part==null || template==null || part.vessel!=vessel || part.partInfo==null || part.partInfo.name!="KKAOSS.Habitat.MK2.g" || part.CrewCapacity!=4)return false;
            var markers=part.Modules.OfType<ColonyPlacementMarker>().ToArray();
            // Explicit template mapping is checked by the construction provider;
            // both currently reviewed home parts are deployed Habitat MK2 modules.
            var deployment=part.Modules.Cast<PartModule>().SingleOrDefault(m=>m.moduleName=="PlanetaryModule");
            return markers.Length==1 && template.HomeCraftPartIds.Contains(markers[0].craftPartId) && (markers[0].craftPartId==101 || markers[0].craftPartId==103) && markers[0].templateSha256==template.CraftSha256 &&
                deployment!=null && Convert.ToString(UtilityRead(deployment,"moduleStatus"),CultureInfo.InvariantCulture)=="Deployed" && part.protoModuleCrew.Count<=part.CrewCapacity;
        }
        static ColonyUtilityReport ReadUtilities(Vessel vessel,string facilityId,string context,double ut,ConfigNode[] backgroundAdapters,ColonyUtilityInstructionObservation instructionObservation)
        {
            var report=new ColonyUtilityReport {FacilityId=facilityId,VesselId=vessel.id.ToString("D"),ContextKey=context,ObservedUt=ut,Context=vessel.loaded ? vessel.packed ? "loaded-packed" : "loaded-unpacked" : "unloaded-proto"};
            var reasons=new List<string>();
            if(!vessel.LandedOrSplashed || vessel.mainBody==null){report.Reason="Facility is not landed in a current body context.";return report;}
            var parts=vessel.loaded && vessel.parts!=null ? vessel.parts.Where(p=>p!=null).ToArray() : vessel.protoVessel==null ? new Part[0] : vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null && p.partInfo!=null).Select(p=>p.partInfo.partPrefab).ToArray();
            if(parts.Length==0 || parts.Length>512){report.Reason="Actual part utility inventory is absent or exceeds the bounded provider.";return report;}
            double generation=0,demand=0,ec=0,capacity=0,temp=0,margin=double.MaxValue,core=0,coreMargin=double.MaxValue;
            bool complete=true,thermal=true,connected=true;
            var profiler=Current==null ? null : Current.performance;
            long utilityStarted=profiler==null ? 0 : profiler.Start();
            object backgroundProcessor=null;string catchupReason="";
            if(!vessel.loaded)
            {
                long catchupStarted=profiler==null ? 0 : profiler.Start();
                try {TryCatchUpUtilityProcessor(vessel,ut,out backgroundProcessor,out catchupReason);}
                finally {if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityCatchUp,catchupStarted);}
            }
            var reactors=vessel.loaded ? ReadLoadedReactors(vessel,ut) : ReadUnloadedReactors(vessel,facilityId,ut,backgroundProcessor);
            var unloaded=vessel.loaded ? null : ObserveUnloadedUtilityDependencies(vessel,ut,backgroundProcessor,backgroundAdapters);
            if(unloaded!=null && !unloaded.Valid){complete=false;reasons.Add(unloaded.Reason);}
            var fixedGenerators=ReadFixedGenerators(vessel,context,ut,backgroundProcessor);
            generation+=fixedGenerators.Generation;
            if(fixedGenerators.Present)reasons.Add(fixedGenerators.Reason);
            if(Finite(fixedGenerators.Endurance)&&fixedGenerators.Endurance<double.MaxValue)report.ContinuousFuelEnduranceSeconds=fixedGenerators.Endurance;
            if(reactors.Present)
            {
                generation+=reactors.Generation;thermal&=reactors.HeatQualified;
                if(reactors.Qualified){core=Math.Max(core,reactors.Core);coreMargin=Math.Min(coreMargin,reactors.Margin);}
                if(Finite(reactors.Endurance) && reactors.Endurance<double.MaxValue)report.ContinuousFuelEnduranceSeconds=Math.Min(report.ContinuousFuelEnduranceSeconds??double.MaxValue,reactors.Endurance);
                reasons.Add(reactors.Reason);
            }
            if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilitySources,utilityStarted);
            utilityStarted=profiler==null ? 0 : profiler.Start();
            var rtgFlightIds=new HashSet<uint>();
            var accountedModules=new HashSet<PartModule>();
            var dependentModules=new List<PartModule>();
            for(int partIndex=0;partIndex<parts.Length;partIndex++)
            {
                var part=parts[partIndex];
                var proto=unloaded==null ? null : unloaded.Part(partIndex);
                if(vessel.loaded)
                {
                    temp=Math.Max(temp,Math.Max(part.temperature,part.skinTemperature));margin=Math.Min(margin,Math.Min(part.maxTemp-part.temperature,part.skinMaxTemp-part.skinTemperature));
                    var resource=part.Resources.Get("ElectricCharge");if(resource!=null){ec+=resource.amount;capacity+=resource.maxAmount;connected&=resource.flowState;}
                }
                foreach(var module in part.Modules.Cast<PartModule>())
                {
                    if(module==null)continue;
                    if(IsDependentUtilityModule(module))
                    {
                        if(vessel.loaded)dependentModules.Add(module);
                        else
                        {
                            string dependencyReason="Actual unloaded dependency observation is absent.";
                            if(unloaded==null || !unloaded.TryDependency(proto,module,reactors,out dependencyReason)){complete=false;reasons.Add(dependencyReason);}
                        }
                        continue;
                    }
                    accountedModules.Add(module);
                    if(fixedGenerators.Accounted.Contains(module))continue; // Fixed source has no EC input; output is counted exactly once above.
                    if(reactors.Qualified && reactors.Accounted.Contains(module))
                    {
                        demand+=module.resHandler.inputResources.Where(r=>r.name=="ElectricCharge").Sum(r=>(double)r.rate);
                        continue;
                    }
                    var generator=module as ModuleGenerator;
                    if(generator!=null)
                    {
                        if(generator.GetType()==typeof(ModuleGenerator) && UtilityModuleIdentity(generator)=="ModuleGenerator|Assembly-CSharp|0.0.0.0" && part.partInfo!=null && part.partInfo.name=="rtg" && generator.isAlwaysActive && !generator.isThrottleControlled && generator.resHandler.inputResources.Count==0)
                        {
                            double rate=generator.resHandler.outputResources.Where(r=>r.name=="ElectricCharge").Sum(r=>(double)r.rate);
                            if(rate>0 && rate<=.75 && (!vessel.loaded || generator.generatorIsActive && UtilityBool(generator,"moduleIsEnabled") && generator.efficiency>=.99)){generation+=rate;if(vessel.loaded)rtgFlightIds.Add(part.flightID);}
                            else reasons.Add("Actual continuous RTG output is not active at the reviewed rate.");
                        }
                        else
                        {
                            double antennaDemand;
                            if(TryAntennaUtilityDemand(module,vessel,out antennaDemand) || unloaded!=null && unloaded.TryAntennaDemand(proto,module,out antennaDemand))demand+=antennaDemand;
                            else {complete=false;accountedModules.Remove(module);reasons.Add("Unqualified generator input/output demand "+module.moduleName+"; a prefab or unknown generator cannot certify demand.");}
                        }
                        continue;
                    }
                    var coreHeat=module as ModuleCoreHeat;
                    if(coreHeat!=null)
                    {
                        if(vessel.loaded){core=Math.Max(core,coreHeat.CoreTemperature);coreMargin=Math.Min(coreMargin,coreHeat.CoreShutdownTemp-coreHeat.CoreTemperature);}
                        if(UtilityModuleIdentity(module)=="NearFutureElectrical.ModuleCoreHeatNoCatchup"+NfeIdentity)
                        {
                            bool inactive=TryInactiveLegacyCore(module,vessel,proto,reactors,out string coreReason);
                            thermal&=inactive;if(!inactive)reasons.Add(coreReason);continue;
                        }
                        // The installed stock RTG equilibrates passively at 350 K.
                        // Active reactor/drill cores require real radiator qualification.
                        thermal&=part.partInfo!=null && part.partInfo.name=="rtg" && coreHeat.CoreTempGoal<=350 && coreHeat.CoreShutdownTemp>=10000 && coreHeat.MaxCoolant==0;
                        continue;
                    }
                    if(module is ModuleCommand){demand+=module.resHandler.inputResources.Where(r=>r.name=="ElectricCharge").Sum(r=>(double)r.rate);continue;}
                    // The installed USI controller only discovers current
                    // range/crew; transfer evidence is observed separately.
                    if(module is USITools.ModulePowerDistributor)continue;
                    if(module is ModuleReactionWheel){demand+=module.resHandler.inputResources.Where(r=>r.name=="ElectricCharge").Sum(r=>(double)r.rate);continue;}
                    if(module is ModuleDockingNode){demand+=module.resHandler.inputResources.Where(r=>r.name=="ElectricCharge").Sum(r=>(double)r.rate);continue;}
                    var lab=module as ModuleScienceLab;
                    if(lab!=null){if(lab.processingData && lab.processResources!=null)demand+=lab.processResources.Where(r=>r.name=="ElectricCharge").Sum(r=>r.amount);continue;}
                    var converter=module as BaseConverter;
                    if(converter!=null)
                    {
                        if(vessel.loaded && converter.IsActivated)
                        {
                            double converterDemand;string converterReason;
                            if(ReadConverterDemandBound(converter,context,ut,out converterDemand,out converterReason))demand+=converterDemand;
                            else {complete=false;accountedModules.Remove(module);reasons.Add(converterReason);}
                        }
                        continue;
                    }
                    if(module.moduleName=="ModuleScienceConverter")
                    {
                        if(vessel.loaded && UtilityBool(module,"IsActivated")){complete=false;accountedModules.Remove(module);reasons.Add("Active science conversion demand is not qualified.");}continue;
                    }
                    var light=module as ModuleLight;if(light!=null){if(light.useResources && light.resourceName=="ElectricCharge")demand+=light.resourceAmount;continue;}
                    var transmitter=module as ModuleDataTransmitter;if(transmitter!=null){if(vessel.loaded && transmitter.IsBusy()){complete=false;accountedModules.Remove(module);reasons.Add("Active transmission demand is not a steady qualified utility load.");}continue;}
                    if(IsReactorAuxiliary(module))
                    {
                        bool observed=TryReactorAuxiliary(module,vessel,proto,reactors,backgroundAdapters,out bool auxiliaryHeatSafe,out string auxiliaryReason);
                        thermal&=auxiliaryHeatSafe;
                        if(!observed){complete=false;accountedModules.Remove(module);}
                        if(auxiliaryReason.Length>0)reasons.Add(auxiliaryReason);continue;
                    }
                    if(module.moduleName=="kOSProcessor")
                    {
                        double processorDemand;double perInstruction=UtilityNumber(module,"ECPerInstruction",double.NaN),perByte=UtilityNumber(module,"ECPerBytePerSecond",double.NaN),disk=UtilityNumber(module,"diskSpace",double.NaN);
                        if(!instructionObservation.TryDemand(perInstruction,perByte,disk,Time.fixedDeltaTime,out processorDemand)){complete=false;reasons.Add("kOS current instruction/disk demand bound is unavailable.");}
                        else demand+=processorDemand;
                        continue;
                    }
                    if(!PassiveUtilityModules.Contains(module.moduleName) && !IsReviewedPassiveUtilityModule(module,vessel) && !IsReviewedPassiveReactorUtilityModule(module)){complete=false;accountedModules.Remove(module);reasons.Add("Unqualified utility module "+module.moduleName+".");}
                }
            }
            foreach(var module in dependentModules)
            {
                if(TryDependentUtilityModule(module,vessel,accountedModules,reactors))accountedModules.Add(module);
                else {complete=false;reasons.Add("Dependent utility module "+module.moduleName+" lacks exact settled current owner accounting. "+UtilityAnimationFailure(module,accountedModules));}
            }
            if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityModules,utilityStarted);
            utilityStarted=profiler==null ? 0 : profiler.Start();
            if(!vessel.loaded)
            {
                double backgroundDemand;
                report.BackgroundProviderQualified=ReadBackgroundContinuousWitness(vessel,ut,generation,reactors,fixedGenerators,backgroundProcessor,backgroundAdapters,out backgroundDemand,out string backgroundReason);if(!report.BackgroundProviderQualified)reasons.Add(backgroundReason+" "+catchupReason);
                demand=Math.Max(demand,backgroundDemand);
                foreach(var part in vessel.protoVessel.protoPartSnapshots)foreach(var r in part.resources.Where(r=>r.resourceName=="ElectricCharge")){ec+=r.amount;capacity+=r.maxAmount;connected&=r.flowState;}
                foreach(var snapshot in vessel.protoVessel.protoPartSnapshots)
                {
                    double savedTemperature=UtilityNumber(snapshot,"temperature",double.NaN),savedSkin=UtilityNumber(snapshot,"skinTemperature",double.NaN);
                    if(!Finite(savedTemperature) || !Finite(savedSkin) || snapshot.partInfo==null){thermal=false;continue;}
                    temp=Math.Max(temp,Math.Max(savedTemperature,savedSkin));margin=Math.Min(margin,Math.Min(snapshot.partInfo.partPrefab.maxTemp-savedTemperature,snapshot.partInfo.partPrefab.skinMaxTemp-savedSkin));
                }
                // A prior commissioned thermal proof plus unchanged reviewed
                // native heat-free/passive sources is required; proto temperatures are saved
                // observations, never presented as fresh measurements.
                var authority=Current==null || Current.state==null ? null : Current.state.Colonies.SelectMany(c=>c.Facilities).SingleOrDefault(f=>f.Id==facilityId && f.VesselId==vessel.id.ToString("D"));
                thermal&=reactors.Present ? reactors.HeatQualified && report.BackgroundProviderQualified : authority!=null && authority.Qualification.HeatSafe && authority.Qualification.BackgroundSupported && authority.Qualification.EvidenceHash.Length>0 && report.BackgroundProviderQualified;
                if(!thermal)reasons.Add("Unloaded passive thermal continuation lacks a prior commissioned physical proof.");
            }
            else
            {
                var modules=UtilityRead(vessel,"vesselModules") as IEnumerable;
                // Installed BRP LoadVessel deliberately clears converter recipes
                // while loaded; the native stock modules own loaded production.
                // Qualify the attached background capability from the exact
                // reviewed adapter and actual native source hardware, then demand actual
                // current recipes and catch-up once the vessel is unloaded.
                report.BackgroundProviderQualified=RemoteBrpInventoryGateway.SupportedProviderAvailable && (reactors.Present ? reactors.Qualified && reactors.Proof!=null : HasBackgroundGeneratorAdapter(backgroundAdapters) && rtgFlightIds.Count>0) && modules!=null &&
                    modules.Cast<object>().Count(x=>x!=null && x.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor")==1;
                if(fixedGenerators.Qualified&&(!reactors.Present||reactors.Qualified&&reactors.Proof!=null)&&HasFixedGeneratorBackgroundAdapter(backgroundAdapters)&&RemoteBrpInventoryGateway.SupportedProviderAvailable&&modules!=null&&modules.Cast<object>().Count(x=>x!=null&&x.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor")==1)report.BackgroundProviderQualified=true;
                reasons.Add(report.BackgroundProviderQualified ? "Loaded background capability: actual attached supported BRP processor and reviewed native continuous source; background production is not observed while loaded." : "Background capability awaits an attached supported processor and a reviewed source; reactors require actual stable full-power thermal proof.");
            }
            if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityBackground,utilityStarted);
            var definition=PartResourceLibrary.Instance.GetDefinition("ElectricCharge");
            // Installed stock ElectricCharge uses STAGE_PRIORITY_FLOW. Stock
            // Part.GetConnectedResourceTotals resolves all four of these modes
            // through the same vessel-wide resource set; only draw order differs.
            bool vesselWideFlow=definition!=null && (definition.resourceFlowMode==ResourceFlowMode.ALL_VESSEL ||
                definition.resourceFlowMode==ResourceFlowMode.ALL_VESSEL_BALANCE || definition.resourceFlowMode==ResourceFlowMode.STAGE_PRIORITY_FLOW ||
                definition.resourceFlowMode==ResourceFlowMode.STAGE_PRIORITY_FLOW_BALANCE);
            connected&=vesselWideFlow;
            if(!vesselWideFlow)reasons.Add("Actual ElectricCharge flow mode is not a qualified vessel-wide native path.");
            report.ElectricCharge=ec;report.ConnectedCapacity=capacity;report.NominalGenerationEcPerSecond=generation;report.NominalDemandEcPerSecond=demand;
            UtilityStorageSample sample;
            if(vessel.loaded && TimeWarp.CurrentRate<=1 && UtilityStorageSamples.TryGetValue(report.VesselId,out sample) && sample.Epoch==context && ut>sample.Ut && ut-sample.Ut<=30)
            {report.NetStorageEcPerSecond=(ec-sample.Amount)/(ut-sample.Ut);report.WindowSeconds=ut-sample.Ut;}
            if(UtilityStorageSamples.Count>=128 && !UtilityStorageSamples.ContainsKey(report.VesselId))UtilityStorageSamples.Clear();
            UtilityStorageSamples[report.VesselId]=new UtilityStorageSample {Epoch=context,Ut=ut,Amount=ec};
            report.ActualConnectedPath=connected && capacity>0;report.FullDemandAccounted=complete;report.ContinuousSourceQualified=!CheatOptions.InfiniteElectricity && generation>0 && (vessel.loaded || report.BackgroundProviderQualified);
            report.DistributionReachQualified=connected;report.DistributionWitness="Same physical vessel "+vessel.id+"; actual ElectricCharge flow mode="+(definition==null?"unavailable":definition.resourceFlowMode.ToString())+"; enabled owned tank path="+connected+". Remote PDU reach is not inferred.";
            report.MaximumPartTemperature=temp;report.MinimumTemperatureMargin=margin;if(vessel.loaded){report.MaximumCoreTemperature=core;report.MinimumCoreShutdownMargin=coreMargin;}
            report.HeatRejectionQualified=thermal && margin>=100 && (!vessel.loaded || coreMargin>=100);
            report.Evidence="Qualified continuous source rating="+generation.ToString("R",CultureInfo.InvariantCulture)+" EC/s; current module demand upper bound="+demand.ToString("R",CultureInfo.InvariantCulture)+" EC/s; modules="+parts.Sum(p=>p.Modules.Count)+"; background adapter="+report.BackgroundProviderQualified+"; actual thermal package="+thermal+"; "+reactors.Evidence+" "+fixedGenerators.Evidence+" "+report.DistributionWitness+(unloaded==null ? "" : " "+unloaded.Evidence);
            if(!report.FullDemandAccounted)reasons.Add("Full demand is not accounted; short storage deltas cannot certify reliability.");
            if(generation<demand*1.1)reasons.Add("Continuous same-vessel supply lacks the required 10% demand margin.");
            if(!report.HeatRejectionQualified)reasons.Add("Active heat rejection or current part/core margin is unqualified.");
            utilityStarted=profiler==null ? 0 : profiler.Start();
            report.InputsAccessible=ReadPhysicalInputWitness(vessel,context,ut,reactors,out string inputWitness);report.InputWitness=inputWitness;
            if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityInputs,utilityStarted);
            if(!report.InputsAccessible)reasons.Add(inputWitness);
            report.Reason=Bound(reasons.Count==0 ? "Self-contained continuous stock RTG utility package qualified from actual parts and conservative demand bounds." : string.Join(" ",reasons.Distinct().Take(12)),4096);return report;
        }
        static object UtilityRead(object instance,string name)
        {
            return ColonyUtilityReflection.Read(instance,name);
        }
        static bool UtilityBool(object instance,string name) => UtilityRead(instance,name) is bool value && value;
        static double UtilityNumber(object instance,string name,double fallback){var value=UtilityRead(instance,name);return value==null ? fallback : Convert.ToDouble(value,CultureInfo.InvariantCulture);}
        static bool TryKosInstructionBound(out double bound)
        {
            bound=0;var types=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("kOS.Safe.Utilities.SafeHouse",false)).Where(t=>t!=null).ToArray();
            if(types.Length!=1)return false;bound=UtilityNumber(UtilityRead(types[0],"Config"),"InstructionsPerUpdate",double.NaN);return Finite(bound) && bound>0 && bound<=100000;
        }
        static ColonyUtilityInstructionObservation CreateUtilityInstructionObservation()=>new ColonyUtilityInstructionObservation(()=>
        {double bound;return TryKosInstructionBound(out bound) ? bound : (double?)null;});
        // GameDatabase.GetConfigNodes walks every installed config. Select these
        // node references once per utility observation, not once per facility.
        // There is no persistent cache: subsequent observations and each native
        // commissioning preflight query again; all node values remain live reads.
        static ConfigNode[] ReadUtilityAdapterConfiguration()
        {
            var profiler=Current==null ? null : Current.performance;
            long started=profiler==null ? 0 : profiler.Start();
            try {return GameDatabase.Instance==null ? new ConfigNode[0] : GameDatabase.Instance.GetConfigNodes("BACKGROUND_CONVERTER");}
            finally {if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityConfigurationQuery,started);}
        }
        static ConfigNode[] ReadUtilityAdapterConfiguration(ColonyEnvironmentConfigObservation environmentConfigs)
        {
            var profiler=Current==null ? null : Current.performance;
            long started=profiler==null ? 0 : profiler.Start();
            try {return environmentConfigs.BackgroundConverters;}
            finally {if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityConfigurationQuery,started);}
        }
        static bool HasBackgroundGeneratorAdapter(ConfigNode[] nodes) => nodes.Any(n=>n.GetValue("name")=="ModuleGenerator" && n.GetValue("adapter")=="BackgroundGenericConverter" && n.GetValue("ActiveCondition")=="!%isThrottleControlled && (%isAlwaysActive || %generatorIsActive)");
        static bool TryCatchUpUtilityProcessor(Vessel vessel,double ut,out object processor,out string reason)
        {
            processor=null;reason="Supported BRP processor/catch-up is unavailable.";
            if(!RemoteBrpInventoryGateway.SupportedProviderAvailable)return false;
            try
            {
                var modules=UtilityRead(vessel,"vesselModules") as IEnumerable;if(modules==null)return false;
                var matches=modules.Cast<object>().Where(x=>x!=null && x.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();if(matches.Length!=1)return false;
                var update=matches[0].GetType().GetMethod("UpdateBackgroundState",BindingFlags.Public|BindingFlags.Instance,null,Type.EmptyTypes,null);if(update==null)return false;
                if(!vessel.loaded)update.Invoke(matches[0],null);
                double age=ut-UtilityNumber(matches[0],"LastChangepoint",double.NaN);if(!vessel.loaded && (!Finite(age) || age<0 || age>10)){reason="Native BRP utility state is stale after catch-up.";return false;}
                processor=matches[0];reason="Exact native BRP catch-up delegated once; no wrapper resources produced.";return true;
            }
            catch(Exception ex){reason="BRP catch-up unavailable: "+Bound(ex.Message,256);return false;}
        }
        static bool HasFixedGeneratorBackgroundAdapter(ConfigNode[] nodes)=>ProportionalType(ProportionalBehaviour)!=null&&nodes.Count(n=>n.GetValue("name")=="USI_Converter")==1&&nodes.Any(n=>n.GetValue("name")=="USI_Converter"&&n.GetValue("adapter")=="ExpanseColonyProportionalAdapter"&&n.GetValue("UsePreparedRecipe")=="true");
        static bool ReadBackgroundContinuousWitness(Vessel vessel,double ut,double required,ReactorUtilityWitness reactors,FixedGeneratorWitness fixedGenerators,object processor,ConfigNode[] backgroundAdapters,out double fullInputDemand,out string reason)
        {
            fullInputDemand=0;
            reason="Supported BRP continuous generator witness unavailable.";
            var profiler=Current==null ? null : Current.performance;
            long started=profiler==null ? 0 : profiler.Start();
            try
            {
                if(!RemoteBrpInventoryGateway.SupportedProviderAvailable || processor==null || !HasBackgroundGeneratorAdapter(backgroundAdapters)&&!reactors.Qualified&&!(fixedGenerators.Qualified&&HasFixedGeneratorBackgroundAdapter(backgroundAdapters)))return false;
            }
            finally {if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityBackgroundAdapters,started);}
            started=profiler==null ? 0 : profiler.Start();
            try
            {
                double age=ut-UtilityNumber(processor,"LastChangepoint",double.NaN);if(!vessel.loaded && (!Finite(age) || age<0 || age>10)){reason="BRP utility state is stale after actual provider catch-up.";return false;}
                double output=(reactors.Qualified ? reactors.Generation : 0)+fixedGenerators.Generation;var flightIds=new HashSet<uint>(vessel.protoVessel.protoPartSnapshots.Where(p=>p.partName=="rtg").Select(p=>p.flightID));
                var converters=((IEnumerable)UtilityRead(processor,"Converters")).Cast<object>().Take(513).ToArray();if(converters.Length>512){reason="BRP utility recipe observation bound exceeded.";return false;}
                foreach(var converter in converters)
                {
                    var inputs=((IEnumerable)UtilityRead(converter,"Inputs")).Cast<object>().Take(65).ToArray();if(inputs.Length>64)return false;
                    foreach(var input in inputs){var ratio=UtilityRead(input,"Value");if((string)UtilityRead(ratio,"ResourceName")=="ElectricCharge")fullInputDemand+=UtilityNumber(ratio,"Ratio",double.NaN);}
                    var id=UtilityRead(converter,"FlightId");if(id==null || !flightIds.Contains(Convert.ToUInt32(id,CultureInfo.InvariantCulture)) || inputs.Length!=0)continue;
                    foreach(var entry in ((IEnumerable)UtilityRead(converter,"Outputs")).Cast<object>())
                    {var ratio=UtilityRead(entry,"Value");if((string)UtilityRead(ratio,"ResourceName")=="ElectricCharge")output+=UtilityNumber(ratio,"Ratio",0);}
                }
                reason="Actual BRP owned reviewed RTG/reactor output="+output.ToString("R",CultureInfo.InvariantCulture)+" EC/s; full recipe input demand="+fullInputDemand.ToString("R",CultureInfo.InvariantCulture)+" EC/s; age="+age.ToString("R",CultureInfo.InvariantCulture)+"s. "+reactors.Evidence;return (!reactors.Present || reactors.Qualified) && output>=required && required>0 && Finite(fullInputDemand) && fullInputDemand>=0;
            }
            catch(Exception ex){reason="BRP utility witness unavailable: "+Bound(ex.Message,256);return false;}
            finally {if(profiler!=null)profiler.End(ColonyPerformanceChannel.UtilityBackgroundRecipes,started);}
        }
    }
}
