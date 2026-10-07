using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static bool ReadPhysicalInputWitness(Vessel vessel,string context,double ut,ReactorUtilityWitness reactors,out string reason)
        {
            var evidence=new List<string>();
            try
            {
                if(vessel==null || PartResourceLibrary.Instance==null)throw new InvalidOperationException("Physical resource provider is unavailable.");
                if(reactors.Present && !reactors.Qualified)throw new InvalidOperationException(reactors.Reason);
                if(!vessel.loaded)
                {
                    // An inactive saved recipe consumes nothing. Active recipe
                    // continuation must be proven by the actual BRP processor,
                    // not by reading a part prefab or a WOLF allocation.
                    if(vessel.protoVessel==null || vessel.protoVessel.protoPartSnapshots.Count>512)throw new InvalidOperationException("Bounded actual proto inventory is unavailable.");
                    var activeModules=new List<Tuple<ProtoPartSnapshot,uint>>();
                    foreach(var part in vessel.protoVessel.protoPartSnapshots)
                    {
                        if(part==null || part.partInfo==null)throw new InvalidOperationException("Actual proto part definition is absent.");
                        foreach(var module in part.modules)
                        {
                            var prefabs=part.partInfo.partPrefab.Modules.Cast<PartModule>().Where(m=>m!=null && m.moduleName==module.moduleName).ToArray();
                            if(!prefabs.Any(m=>m is BaseConverter))continue;
                            if(!bool.TryParse(module.moduleValues.GetValue("IsActivated"),out bool active))throw new InvalidOperationException("Saved converter activation is unavailable.");
                            if(active)
                            {
                                if(!uint.TryParse(module.moduleValues.GetValue("persistentId"),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint moduleId) || moduleId==0)
                                    throw new InvalidOperationException("Active saved recipe lacks an exact native module identity.");
                                activeModules.Add(Tuple.Create(part,moduleId));
                            }
                        }
                    }
                    reason=activeModules.Count==0 ? "Current saved modules have no active material-conversion recipe; no physical input stock is credited. Resident consumables are accounted separately."
                        : ReadBrpMaterialInputs(vessel,ut,activeModules);
                    return true;
                }
                if(vessel.parts==null || vessel.parts.Count>512)throw new InvalidOperationException("Bounded actual loaded inventory is unavailable.");
                int activeCount=0;
                foreach(var part in vessel.parts.Where(p=>p!=null))
                foreach(var module in part.Modules.Cast<PartModule>().OfType<BaseConverter>().Where(m=>m.IsActivated))
                {
                    if(++activeCount>128)throw new InvalidOperationException("Active recipe observation exceeds its bound.");
                    if(!TryReadNativeRecipeInputs(module,context,ut,out ResourceRatio[] inputs,out string unavailable))throw new InvalidOperationException(unavailable);
                    foreach(var input in inputs.Where(r=>r.ResourceName!="ElectricCharge" && r.Ratio>0))
                    {
                        var definition=PartResourceLibrary.Instance.GetDefinition(input.ResourceName);
                        if(definition==null)throw new InvalidOperationException("Unknown native input "+input.ResourceName+".");
                        var flow=input.FlowMode==ResourceFlowMode.NULL ? definition.resourceFlowMode : input.FlowMode;
                        if(!Enum.IsDefined(typeof(ResourceFlowMode),flow) || flow==ResourceFlowMode.NULL)throw new InvalidOperationException("Unsupported input flow mode for "+input.ResourceName+".");
                        part.GetConnectedResourceTotals(definition.id,flow,out double amount,out double capacity);
                        if(!Finite(amount) || !Finite(capacity) || amount<0 || capacity<amount || capacity<=0)throw new InvalidOperationException("No valid native flow path/tank for "+input.ResourceName+" at part "+part.persistentId+".");
                        if(amount<=0)throw new InvalidOperationException(input.ResourceName+" feed tank is accessible but empty at part "+part.persistentId+"; fill it through a verified physical transfer.");
                        evidence.Add(part.persistentId+":"+input.ResourceName+"="+amount.ToString("R",CultureInfo.InvariantCulture)+"/"+capacity.ToString("R",CultureInfo.InvariantCulture)+" via "+flow);
                    }
                    // Stock ResourceConverter checks requirements on its own
                    // part, not connected tanks. Positive requirements throttle
                    // by amount/ratio; negative ones throttle by 1-amount/|ratio|.
                    foreach(var requirement in UtilityRecipes[module].Requirements.Where(r=>r.Ratio!=0))
                    {
                        var tank=part.Resources.Get(requirement.ResourceName);
                        if(tank==null || !Finite(tank.amount) || !Finite(tank.maxAmount) || tank.amount<0 || tank.amount>tank.maxAmount)throw new InvalidOperationException("Required installed "+requirement.ResourceName+" tank is absent or invalid at part "+part.persistentId+".");
                        double fraction=requirement.Ratio>0 ? tank.amount/requirement.Ratio : 1-tank.amount/Math.Abs(requirement.Ratio);
                        if(!Finite(fraction) || fraction<=0)throw new InvalidOperationException("Installed "+requirement.ResourceName+" prevents native recipe operation at part "+part.persistentId+"; service the actual module.");
                        evidence.Add(part.persistentId+":requirement:"+requirement.ResourceName+"="+tank.amount.ToString("R",CultureInfo.InvariantCulture));
                    }
                }
                reason=activeCount==0 ? "No active material-conversion recipe; no warehouse or WOLF stock is inferred. Resident consumables are accounted separately."
                    : "Current native feed/installed requirements for "+activeCount+" active recipes: "+string.Join(";",evidence)+". Current stock only; no guaranteed endurance or future warehouse transfer is inferred.";
                if(reactors.Present)reason+=" "+reactors.Evidence;
                reason=Bound(reason,4096);return true;
            }
            catch(Exception ex){reason="Physical input hold: "+Bound(ex.Message,1000);return false;}
        }

        static string ReadBrpMaterialInputs(Vessel vessel,double ut,List<Tuple<ProtoPartSnapshot,uint>> modules)
        {
            if(modules.Count>128 || !RemoteBrpInventoryGateway.SupportedProviderAvailable)throw new InvalidOperationException("Qualified bounded BRP recipe provider is unavailable.");
            var processors=((IEnumerable)UtilityRead(vessel,"vesselModules")).Cast<object>().Where(x=>x!=null && x.GetType().FullName=="BackgroundResourceProcessing.BackgroundResourceProcessor").ToArray();
            if(processors.Length!=1)throw new InvalidOperationException("Exact attached BRP processor is unavailable.");
            // ReadUtilities has already requested native catch-up. Do not perform
            // a second production step or read stale saved quantities here.
            double age=ut-UtilityNumber(processors[0],"LastChangepoint",double.NaN);
            if(!Finite(age) || age<0 || age>10)throw new InvalidOperationException("BRP input state is not current after catch-up.");
            var converters=((IEnumerable)UtilityRead(processors[0],"Converters")).Cast<object>().Take(513).ToArray();
            if(converters.Length>512)throw new InvalidOperationException("Background recipe count exceeds its bound.");
            var inventories=UtilityRead(processors[0],"Inventories");
            int count=Convert.ToInt32(UtilityRead(inventories,"Count"),CultureInfo.InvariantCulture);
            var item=inventories?.GetType().GetProperty("Item");
            if(item==null || count<0 || count>4096)throw new InvalidOperationException("Bounded actual BRP inventory index is unavailable.");
            var evidence=new List<string>();
            foreach(var module in modules)
            {
                var matching=converters.Where(c=>UtilityRead(c,"FlightId") is uint flight && flight==module.Item1.flightID &&
                    UtilityRead(c,"ModuleId") is uint id && id==module.Item2).ToArray();
                if(matching.Length==0 || matching.Length>16)throw new InvalidOperationException("Active saved recipe has no bounded exact BRP module mapping.");
                foreach(var converter in matching)
                {
                    RequireProportionalObservation(converter,vessel,module.Item1,module.Item2,ut,processors[0]);
                    string constraint=Convert.ToString(UtilityRead(converter,"ConstraintState"),CultureInfo.InvariantCulture);
                    double rate=UtilityNumber(converter,"Rate",double.NaN);
                    if(constraint!="ENABLED" && !(constraint=="BOUNDARY" && Finite(rate) && rate>0 && rate<=1))
                        throw new InvalidOperationException("Native BRP material requirements prevent operation at part "+module.Item1.persistentId+" ("+constraint+").");
                    var inputs=((IEnumerable)UtilityRead(converter,"Inputs")).Cast<object>().Select(e=>UtilityRead(e,"Value")).Take(65).ToArray();
                    var pulls=((IEnumerable)UtilityRead(converter,"Pull")).Cast<object>().Select(i=>Convert.ToInt32(i,CultureInfo.InvariantCulture)).Take(4097).ToArray();
                    if(inputs.Length>64 || pulls.Length>4096 || pulls.Any(i=>i<0 || i>=count))throw new InvalidOperationException("Native background feed mapping exceeds its bounds.");
                    foreach(var input in inputs)
                    {
                        string name=Convert.ToString(UtilityRead(input,"ResourceName"),CultureInfo.InvariantCulture);
                        double ratio=UtilityNumber(input,"Ratio",double.NaN);
                        if(string.IsNullOrEmpty(name) || !Finite(ratio) || ratio<0)throw new InvalidOperationException("Invalid native background input.");
                        if(name=="ElectricCharge" || ratio==0)continue;
                        double available=0;int paths=0;
                        foreach(int index in pulls)
                        {
                            // StableList enumeration skips deleted slots; retain
                            // its actual native index when following Pull bits.
                            var inventory=item.GetValue(inventories,new object[]{index});
                            if(inventory==null || Convert.ToString(UtilityRead(inventory,"ResourceName"),CultureInfo.InvariantCulture)!=name)continue;
                            if(UtilityRead(inventory,"ModuleId")!=null)throw new InvalidOperationException("Module-owned virtual inventory needs a separate physical-input provider.");
                            uint flight=Convert.ToUInt32(UtilityRead(inventory,"FlightId"),CultureInfo.InvariantCulture);
                            var parts=vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null && p.flightID==flight).ToArray();
                            var snapshot=UtilityRead(inventory,"Snapshot") as ProtoPartResourceSnapshot;
                            if(parts.Length!=1 || snapshot==null || !parts[0].resources.Any(r=>ReferenceEquals(r,snapshot)) || snapshot.resourceName!=name || !snapshot.flowState)
                                throw new InvalidOperationException("BRP feed is not a uniquely owned enabled physical tank.");
                            double amount=UtilityNumber(inventory,"Amount",double.NaN),capacity=UtilityNumber(inventory,"MaxAmount",double.NaN);
                            if(!Finite(amount) || !Finite(capacity) || amount<0 || capacity<amount || snapshot.amount!=amount || snapshot.maxAmount!=capacity ||
                                UtilityNumber(inventory,"OriginalAmount",double.NaN)!=amount)throw new InvalidOperationException("BRP feed quantities do not match current actual proto stock.");
                            available+=amount;paths++;
                        }
                        if(paths==0)throw new InvalidOperationException("No owned native BRP feed path for "+name+".");
                        if(!Finite(available) || available<=0)throw new InvalidOperationException(name+" background feed is accessible but empty.");
                        evidence.Add(module.Item1.persistentId+":"+name+"="+available.ToString("R",CultureInfo.InvariantCulture)+" across "+paths+" actual native pull paths");
                    }
                }
            }
            return Bound("Current owned BRP feed and native constraint witnesses: "+string.Join(";",evidence)+". Physical quantities only; no WOLF stock, warehouse promise or additional production credited.",4096);
        }
    }
}
