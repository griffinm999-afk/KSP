using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Expanse.Domain.Colonies;
using HarmonyLib;
using USITools;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class DistributionTank
        {
            public Vessel Vessel;public Part Part;public PartResource Resource;public double Before,Capacity;public bool Flow;
        }
        sealed class DistributionSample
        {
            public string Context;public double Ut;public Vessel Receiver;public uint CouplerPart;public List<DistributionTank> Tanks;
        }
        sealed class DistributionDelivery
        {
            public string Context,Source,Receiver,Evidence;public double Ut,Delivered;
            public Vessel SourceVessel,ReceiverVessel;public Part CouplerPart;
            public ModuleLogisticsConsumer Consumer;public ModulePowerCoupler Coupler;
            public ModulePowerDistributor[] Distributors;public DistributionTank[] PathTanks;
        }
        static readonly Dictionary<string,DistributionDelivery> DistributionDeliveries=new Dictionary<string,DistributionDelivery>(StringComparer.Ordinal);
        static Harmony distributionHarmony;
        static MethodInfo distributionTarget,distributionPrefix,distributionPostfix,distributionFinalizer;
        static string distributionContext="",distributionFailure="";
        static bool distributionAttempted;
        [ThreadStatic] static bool distributionObserving;
        static void EnsureUtilityObservers(string context)
        {
            if(distributionContext!=context){DistributionDeliveries.Clear();distributionContext=context;}
            if(distributionAttempted)return;
            var type=typeof(ModuleLogisticsConsumer);
            if(type.Assembly.GetName().Version!=new Version(1,0,0,0))return;
            distributionAttempted=true;
            try
            {
                distributionTarget=type.GetMethod("CheckLogistics",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(List<ResourceRatio>),typeof(bool)},null);
                if(distributionTarget==null || distributionTarget.ReturnType!=typeof(void))throw new MissingMethodException("Installed USI logistics callback signature changed.");
                distributionPrefix=typeof(ColonyRuntime).GetMethod(nameof(DistributionPrefix),BindingFlags.NonPublic|BindingFlags.Static);
                distributionPostfix=typeof(ColonyRuntime).GetMethod(nameof(DistributionPostfix),BindingFlags.NonPublic|BindingFlags.Static);
                distributionFinalizer=typeof(ColonyRuntime).GetMethod(nameof(DistributionFinalizer),BindingFlags.NonPublic|BindingFlags.Static);
                distributionHarmony=new Harmony("Expanse.WorldBridge.ColonyActualPowerDistribution");
                distributionHarmony.Patch(distributionTarget,new HarmonyMethod(distributionPrefix),new HarmonyMethod(distributionPostfix),finalizer:new HarmonyMethod(distributionFinalizer));
            }
            catch(Exception ex){distributionFailure="Actual distribution observer unavailable: "+Bound(ex.Message,256);StopUtilityObservers();distributionAttempted=true;}
        }
        static void StopUtilityObservers()
        {
            try
            {
                if(distributionHarmony!=null && distributionTarget!=null){if(distributionPrefix!=null)distributionHarmony.Unpatch(distributionTarget,distributionPrefix);if(distributionPostfix!=null)distributionHarmony.Unpatch(distributionTarget,distributionPostfix);if(distributionFinalizer!=null)distributionHarmony.Unpatch(distributionTarget,distributionFinalizer);}
            }
            catch { }
            distributionHarmony=null;DistributionDeliveries.Clear();distributionContext="";distributionObserving=false;distributionAttempted=false;StopRecipeObserver();
        }
        static bool DistributionCandidate(Vessel source,Vessel receiver)
        {
            if(source==null || receiver==null || source==receiver || !source.loaded || !source.LandedOrSplashed || source.mainBody!=receiver.mainBody || source.parts==null || source.parts.Count>512)return false;
            double range=LogisticsTools.GetRange(source,receiver);if(!Finite(range) || range<0 || range>2000)return false;
            return source.parts.Where(p=>p!=null).SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<ModulePowerDistributor>().Any(m=>m.ActiveDistributionRange>=range);
        }
        static void DistributionPrefix(ModuleLogisticsConsumer __instance,List<ResourceRatio> resList,bool output,out DistributionSample __state)
        {
            __state=null;
            try
            {
                if(distributionObserving || Current==null || Current.ContextKey!=distributionContext || output || resList==null || resList.Count>64 || !resList.Any(r=>r.ResourceName=="ElectricCharge") || __instance==null || __instance.vessel==null || !__instance.vessel.loaded || !__instance.vessel.LandedOrSplashed || __instance.part.FindModuleImplementing<ModulePowerCoupler>()==null || FlightGlobals.Vessels==null || FlightGlobals.Vessels.Count>2048 || TimeWarp.CurrentRate>1 || CheatOptions.InfiniteElectricity)return;
                var receiver=__instance.vessel;var sources=FlightGlobals.Vessels.Where(v=>DistributionCandidate(v,receiver)).Take(33).ToArray();if(sources.Length==0 || sources.Length>32)return;
                var tanks=new List<DistributionTank>();
                foreach(var vessel in sources.Concat(new[]{receiver}))
                {
                    if(vessel.parts==null || vessel.parts.Count>512)return;
                    foreach(var part in vessel.parts.Where(p=>p!=null))
                    {
                        var resource=part.Resources.Get("ElectricCharge");if(resource==null)continue;
                        if(!Finite(resource.amount) || !Finite(resource.maxAmount) || resource.amount<0 || resource.amount>resource.maxAmount || !resource.flowState || tanks.Count>=4096)return;
                        tanks.Add(new DistributionTank {Vessel=vessel,Part=part,Resource=resource,Before=resource.amount,Capacity=resource.maxAmount,Flow=resource.flowState});
                    }
                }
                __state=new DistributionSample {Context=Current.ContextKey,Ut=Planetarium.GetUniversalTime(),Receiver=receiver,CouplerPart=__instance.part.persistentId,Tanks=tanks};distributionObserving=true;
            }
            catch { __state=null; }
        }
        static void DistributionPostfix(DistributionSample __state)
        {
            if(__state==null)return;
            distributionObserving=false;
            try
            {
                if(Current==null || Current.ContextKey!=__state.Context || __state.Context!=distributionContext || Planetarium.GetUniversalTime()!=__state.Ut || __state.Tanks.Any(t=>t.Vessel==null || t.Part==null || t.Part.vessel!=t.Vessel || !ReferenceEquals(t.Part.Resources.Get("ElectricCharge"),t.Resource) || t.Resource.maxAmount!=t.Capacity || t.Resource.flowState!=t.Flow || !Finite(t.Resource.amount) || t.Resource.amount<0 || t.Resource.amount>t.Capacity))return;
                double received=__state.Tanks.Where(t=>t.Vessel==__state.Receiver).Sum(t=>t.Resource.amount-t.Before);
                var debits=__state.Tanks.Where(t=>t.Vessel!=__state.Receiver).GroupBy(t=>t.Vessel).Select(g=>new{Vessel=g.Key,Amount=g.Sum(t=>t.Before-t.Resource.amount),Rows=g.ToArray()}).ToArray();
                double sent=debits.Sum(d=>d.Amount);if(received<=1e-9 || sent<=1e-9 || debits.Any(d=>d.Amount<-1e-9) || Math.Abs(sent-received)>Math.Max(1e-7,sent*1e-9))return;
                foreach(var debit in debits.Where(d=>d.Amount>1e-9))
                {
                    string key=debit.Vessel.id+"|"+__state.Receiver.id;
                    if(DistributionDeliveries.Count>=128 && !DistributionDeliveries.ContainsKey(key))return;
                    string rows=string.Join(";",debit.Rows.Concat(__state.Tanks.Where(t=>t.Vessel==__state.Receiver)).Where(t=>Math.Abs(t.Before-t.Resource.amount)>1e-9).Select(t=>t.Vessel.id+"/"+t.Part.persistentId+":"+t.Before.ToString("R",CultureInfo.InvariantCulture)+"→"+t.Resource.amount.ToString("R",CultureInfo.InvariantCulture)));
                    var couplerPart=__state.Receiver.parts.SingleOrDefault(p=>p!=null && p.persistentId==__state.CouplerPart);if(couplerPart==null)continue;
                    DistributionDeliveries[key]=new DistributionDelivery {Context=__state.Context,Ut=__state.Ut,Source=debit.Vessel.id.ToString("D"),Receiver=__state.Receiver.id.ToString("D"),Delivered=debit.Amount,Evidence="Actual synchronous native USI EC transfer; receiver coupler part="+__state.CouplerPart+"; tank before/after="+Bound(rows,3072),
                        SourceVessel=debit.Vessel,ReceiverVessel=__state.Receiver,CouplerPart=couplerPart,Consumer=couplerPart.FindModuleImplementing<ModuleLogisticsConsumer>(),Coupler=couplerPart.FindModuleImplementing<ModulePowerCoupler>(),
                        Distributors=debit.Vessel.parts.Where(p=>p!=null).SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<ModulePowerDistributor>().ToArray(),PathTanks=debit.Rows.Concat(__state.Tanks.Where(t=>t.Vessel==__state.Receiver)).ToArray()};
                }
            }
            catch { }
        }
        static Exception DistributionFinalizer(Exception __exception,DistributionSample __state)
        {
            if(__state!=null)distributionObserving=false;return __exception;
        }
        static void ApplyDistributionWitnesses(List<ColonyUtilityReport> reports,string context,double ut,ConfigNode[] backgroundAdapters,ColonyUtilityInstructionObservation instructionObservation)
        {
            // A successful transfer proves a native path for this exact loaded
            // topology. No later demand is required while buffers remain charged.
            // Invalidated paths are discarded, so unload/reload cannot revive an
            // old native module or tank witness even in the same save epoch.
            foreach(var key in DistributionDeliveries.Where(p=>!CurrentDistributionPath(p.Value,context,ut)).Select(p=>p.Key).ToArray())DistributionDeliveries.Remove(key);
            foreach(var report in reports)
            {
                var deliveries=DistributionDeliveries.Values.Where(d=>d.Context==context && d.Receiver==report.VesselId && ut>=d.Ut && ut-d.Ut<=10).Take(33).ToArray();
                if(deliveries.Length==0){if(distributionFailure.Length>0)report.Reason=Bound(report.Reason+" "+distributionFailure,4096);continue;}
                report.DistributionWitness=Bound(report.DistributionWitness+" "+string.Join(" ",deliveries.Select(d=>d.Evidence+"; sampled received="+d.Delivered.ToString("R",CultureInfo.InvariantCulture)+" EC at UT="+d.Ut.ToString("R",CultureInfo.InvariantCulture))),4096);
                // These are individual conserved delivery samples, not a rate
                // integral or a claim that the same charge can be delivered again.
                report.Evidence=Bound(report.Evidence+" "+report.DistributionWitness,4096);
                report.Reason=Bound(report.Reason+" Current native remote delivery is observed; prospective remote reliability requires the sender's full shared generation/demand/endurance budget.",4096);
            }
            QualifyLoadedDistributionBudget(reports,context,ut,backgroundAdapters,instructionObservation);
        }
        static bool CurrentDistributionPath(DistributionDelivery path,string context,double ut)
        {
            if(path==null || path.Context!=context || ut<path.Ut || path.SourceVessel==null || path.ReceiverVessel==null || !path.ReceiverVessel.loaded || !path.ReceiverVessel.LandedOrSplashed || !DistributionCandidate(path.SourceVessel,path.ReceiverVessel) ||
                path.CouplerPart==null || path.CouplerPart.vessel!=path.ReceiverVessel || path.CouplerPart.FindModuleImplementing<ModuleLogisticsConsumer>()!=path.Consumer || path.CouplerPart.FindModuleImplementing<ModulePowerCoupler>()!=path.Coupler || path.Consumer==null || path.Coupler==null ||
                path.Distributors==null || path.Distributors.Length==0 || path.PathTanks==null || path.PathTanks.Length==0)return false;
            var distributors=path.SourceVessel.parts.Where(p=>p!=null).SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<ModulePowerDistributor>().ToArray();
            if(!distributors.SequenceEqual(path.Distributors))return false;
            var currentTanks=path.SourceVessel.parts.Concat(path.ReceiverVessel.parts).Where(p=>p!=null).Select(p=>p.Resources.Get("ElectricCharge")).Where(r=>r!=null).ToArray();
            if(currentTanks.Length!=path.PathTanks.Length || !currentTanks.SequenceEqual(path.PathTanks.Select(t=>t.Resource)))return false;
            return path.PathTanks.All(t=>t.Vessel!=null && t.Vessel.loaded && t.Part!=null && t.Part.vessel==t.Vessel && ReferenceEquals(t.Part.Resources.Get("ElectricCharge"),t.Resource) && t.Resource.maxAmount==t.Capacity && t.Resource.flowState==t.Flow && t.Flow && Finite(t.Resource.amount) && t.Resource.amount>=0 && t.Resource.amount<=t.Capacity);
        }
        static void QualifyLoadedDistributionBudget(List<ColonyUtilityReport> reports,string context,double ut,ConfigNode[] backgroundAdapters,ColonyUtilityInstructionObservation instructionObservation)
        {
            if(Current==null || FlightGlobals.Vessels==null || FlightGlobals.Vessels.Count>2048 || TimeWarp.CurrentRate>1 || CheatOptions.InfiniteElectricity)return;
            double cadence;
            try{cadence=USITools.Logistics.LogisticsSetup.Instance.Config.LogisticsTime;}catch{return;}
            if(!PositiveFinite(cadence) || cadence>60)return;
            var world=FlightGlobals.Vessels.Where(v=>v!=null && v.loaded && v.LandedOrSplashed && v.parts!=null && v.parts.Count<=512).Take(129).ToArray();if(world.Length>128)return;
            // Use the native same-part coupler/consumer requirement. Every
            // reachable receiver counts, including receivers outside a colony.
            var receivers=world.Where(v=>v.parts.Any(p=>p!=null && p.FindModuleImplementing<ModuleLogisticsConsumer>()!=null && p.FindModuleImplementing<ModulePowerCoupler>()!=null)).ToArray();
            var original=reports.ToArray();
            foreach(var sourceReport in original.Where(r=>r.ContinuousSourceQualified && r.FullDemandAccounted && r.ActualConnectedPath && r.HeatRejectionQualified && r.NominalGenerationEcPerSecond.HasValue && r.NominalDemandEcPerSecond.HasValue).ToArray())
            {
                var source=world.SingleOrDefault(v=>v.id.ToString("D")==sourceReport.VesselId);if(source==null)continue;
                var peers=receivers.Where(v=>DistributionCandidate(source,v)).Take(33).ToArray();if(peers.Length==0 || peers.Length>32)continue;
                var peerReports=peers.Select(v=>original.SingleOrDefault(r=>r.VesselId==v.id.ToString("D")) ?? ReadUtilities(v,"",context,ut,backgroundAdapters,instructionObservation)).ToArray();
                if(peerReports.Any(r=>!r.FullDemandAccounted || !r.ActualConnectedPath || !r.NominalDemandEcPerSecond.HasValue || !Finite(r.NominalDemandEcPerSecond.Value) || r.NominalDemandEcPerSecond.Value<0))continue;
                double total=peerReports.Sum(r=>r.NominalDemandEcPerSecond.Value),available=sourceReport.NominalGenerationEcPerSecond.Value-sourceReport.NominalDemandEcPerSecond.Value;
                // Native callbacks pull 10% tank capacity below 50% fill. A
                // conservative 5% capacity cadence budget leaves a full extra
                // polling interval. This is a declared prospective load bound,
                // not measured throughput. It is limited to normal loaded time.
                if(total<=0 || available<total*1.1 || !sourceReport.ConnectedCapacity.HasValue || !sourceReport.ElectricCharge.HasValue || sourceReport.ElectricCharge.Value<sourceReport.ConnectedCapacity.Value*.75 || total*cadence*20>sourceReport.ConnectedCapacity.Value)continue;
                if(peerReports.Any(r=>!r.ConnectedCapacity.HasValue || r.NominalDemandEcPerSecond.Value*cadence*20>r.ConnectedCapacity.Value || !r.ElectricCharge.HasValue || r.ElectricCharge.Value<r.ConnectedCapacity.Value*.25))continue;
                foreach(var receiverReport in reports.Where(r=>peerReports.Any(p=>p.VesselId==r.VesselId)))
                {
                    // A conserved transfer must have proved this exact native
                    // loaded topology. Revalidate it and the whole budget now;
                    // a fully charged receiver legitimately receives no new EC.
                    if(!DistributionDeliveries.TryGetValue(sourceReport.VesselId+"|"+receiverReport.VesselId,out var delivered) || !CurrentDistributionPath(delivered,context,ut))continue;
                    // Do not combine competing supply packages into a fictitious
                    // shared grid. The first complete source budget is sufficient.
                    if(receiverReport.ContinuousSourceQualified)continue;
                    double share=available*receiverReport.NominalDemandEcPerSecond.Value/total;
                    receiverReport.NominalGenerationEcPerSecond=share;receiverReport.ContinuousSourceQualified=true;receiverReport.DistributionReachQualified=true;
                    receiverReport.ContinuousFuelEnduranceSeconds=sourceReport.ContinuousFuelEnduranceSeconds;
                    receiverReport.DistributionWitness=Bound(receiverReport.DistributionWitness+" Qualified loaded USI source="+sourceReport.VesselId+"; all "+peers.Length+" reachable receiver demands="+total.ToString("R",CultureInfo.InvariantCulture)+" EC/s; source generation minus own demand="+available.ToString("R",CultureInfo.InvariantCulture)+" EC/s; this receiver's proportional continuous allocation bound="+share.ToString("R",CultureInfo.InvariantCulture)+" EC/s; native polling cadence="+cadence.ToString("R",CultureInfo.InvariantCulture)+"s; actual buffers and 10% headroom checked.",4096);
                    receiverReport.Reason=Bound(receiverReport.Reason+" Current exact native transfer plus complete loaded source/shared-receiver budget qualifies prospective power at normal time. Packed/unloaded background distribution is not inferred.",4096);
                }
            }
        }
    }
}
