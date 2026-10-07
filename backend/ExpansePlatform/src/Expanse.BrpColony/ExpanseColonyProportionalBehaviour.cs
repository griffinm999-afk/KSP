using System;
using System.Collections.Generic;
using System.Linq;
using BackgroundResourceProcessing;
using BackgroundResourceProcessing.Behaviour;
using BackgroundResourceProcessing.Core;
using BrpConverter=BackgroundResourceProcessing.Core.ResourceConverter;

namespace Expanse.BrpColony
{
    public sealed class ExpanseColonyProportionalBehaviour : ConverterBehaviour
    {
        public string Contract=>ColonyRecipe.Contract;
        public string HoldReason {get;private set;}="Not yet bound to a current native owner.";
        public double Factor {get;private set;}
        public double SampleUt {get;private set;}
        public bool Bound {get;private set;}
        public double RelativeErrorLimit=>ProportionalMath.RelativeDrift;
        ConfigNode record;
        ColonyRecipe recipe;
        bool invalidated;
        string bindingFailure="";
        int authorityRetries;
        double[] epochAmounts;
        double epochDeadline=double.PositiveInfinity;
        readonly Action<ConfigNode,Vessel> validate=ColonyGate.Validate;
        readonly Action provenance=ColonyGate.Provenance;
        public ExpanseColonyProportionalBehaviour() { }
        public ExpanseColonyProportionalBehaviour(ConfigNode node,string hold)
        {record=node?.CreateCopy();HoldReason=hold;try{recipe=ColonyRecipe.Read(record);}catch(Exception ex){if(record!=null||string.IsNullOrEmpty(HoldReason))HoldReason=ex.Message;}}
        internal ExpanseColonyProportionalBehaviour(ConfigNode node,Action<ConfigNode,Vessel> validator,Action sourceCheck):this(node,"")
        {validate=validator;provenance=sourceCheck;}
        public ConfigNode CurrentRecord=>record?.CreateCopy();
        static bool Consumes(BrpConverter converter,string name)
        {foreach(var ratio in converter.Inputs.Values)if(ratio.ResourceName==name&&ratio.Ratio>0)return true;return false;}
        static bool Produces(BrpConverter converter,string name)
        {foreach(var ratio in converter.Outputs.Values)if(ratio.ResourceName==name&&ratio.Ratio>0)return true;return false;}
        double OutputCapacity(BackgroundResourceProcessor processor,ResourceRatio output)
        {
            // Initial capture occurs before edges/self registration. The two
            // supported profiles use NO_FLOW fuel and ALL_VESSEL farm outputs.
            // Re-read real current inventories; no prefab/saved index is used.
            if(output.FlowMode!=ResourceFlowMode.ALL_VESSEL)throw new InvalidOperationException("Unreviewed nondumped output flow.");
            if(Vessel!=null&&Vessel.loaded)
            {
                var part=Vessel.parts.Single(p=>p.flightID==recipe.FlightId);double amount,capacity;
                part.GetConnectedResourceTotals(output.ResourceName.GetHashCode(),output.FlowMode,out amount,out capacity,false);
                if(!ProportionalMath.Finite(capacity)||capacity<=0)throw new InvalidOperationException("Loaded nondumped output storage unavailable.");
                return capacity;
            }
            double total=0;
            foreach(var row in processor.Inventories.Where(i=>i!=null&&!i.ModuleId.HasValue&&i.ResourceName==output.ResourceName))
            {
                if(row.Snapshot==null||!row.Snapshot.flowState)continue;
                if(!ProportionalMath.Finite(row.MaxAmount)||row.MaxAmount<0||row.MaxAmount!=row.Snapshot.maxAmount)throw new InvalidOperationException("Actual nondumped output capacity differs.");
                total+=row.MaxAmount;
            }
            if(!ProportionalMath.Finite(total)||total<=0)throw new InvalidOperationException("Nondumped output storage unavailable.");
            return total;
        }
        ResourceInventory[] Local(BackgroundResourceProcessor processor)
        {
            if(ReferenceEquals(processor,null)||processor.Inventories.Count>4096)throw new InvalidOperationException("Bounded owner inventories unavailable.");
            if(recipe==null)throw new InvalidOperationException(HoldReason.Length>0?HoldReason:"Missing supported colony recipe.");
            var result=new List<ResourceInventory>();
            foreach(var req in recipe.Requirements)
            {
                // Scan current identities, never serialized indexes. Part-local
                // requirement amount ignores resource flow/other farm stock.
                var rows=processor.Inventories.Where(i=>i!=null&&i.FlightId==recipe.FlightId&&!i.ModuleId.HasValue&&i.ResourceName==req.ResourceName).Take(2).ToArray();
                if(rows.Length!=1||!ProportionalMath.Finite(rows[0].Amount)||!ProportionalMath.Finite(rows[0].MaxAmount)||rows[0].Amount<0||rows[0].Amount>rows[0].MaxAmount)throw new InvalidOperationException("Requirement owner is absent, duplicated or invalid.");
                result.Add(rows[0]);
            }
            return result.ToArray();
        }
        public override ConverterResources GetResources(VesselState state)
        {
            Bound=false;
            try
            {
                if(bindingFailure.Length>0)throw new InvalidOperationException(bindingFailure);
                if(record==null)throw new InvalidOperationException(HoldReason.Length>0?HoldReason:"Missing supported colony recipe.");
                recipe=ColonyRecipe.Read(record);provenance();validate(record,Vessel);
                if(state==null||ReferenceEquals(state.Processor,null)||!ReferenceEquals(state.Processor.Vessel,Vessel)||!ProportionalMath.Finite(state.CurrentTime))throw new InvalidOperationException("Current native processor/time unavailable.");
                var local=Local(state.Processor);
                epochAmounts=local.Select(i=>i.Amount).ToArray();epochDeadline=double.PositiveInfinity;
                Factor=ProportionalMath.Factor(epochAmounts,recipe.Requirements.Select(r=>r.Ratio).ToArray());SampleUt=state.CurrentTime;invalidated=false;HoldReason="";authorityRetries=0;
                double effective=Factor*recipe.Multiplier;if(effective<=ProportionalMath.StopFactor)effective=0;
                var result=new ConverterResources{Inputs=ColonyRecipe.Scale(recipe.Inputs,effective),Outputs=ColonyRecipe.Scale(recipe.Outputs,effective)};
                // Requirement inventory floors are deliberately absent. Native
                // dumped outputs bypass FillAmount storage tests.
                foreach(var output in recipe.Outputs.Where(o=>!o.DumpExcess&&recipe.Fill<1))
                {
                    double capacity=OutputCapacity(state.Processor,output);
                    result.Requirements.Add(new ResourceConstraint{ResourceName=output.ResourceName,Amount=capacity*recipe.Fill,Constraint=Constraint.AT_MOST,FlowMode=output.FlowMode});
                }
                // Bootstrap: rates/edges do not exist until OnRatesComputed.
                // Native BRP dispatches that callback before registering time.
                result.NextChangepoint=double.PositiveInfinity;return result;
            }
            catch(ColonyAuthorityUnavailableException ex)
            {
                HoldReason=ex.Message;Factor=0;double delay=Math.Min(60,Math.Pow(2,Math.Min(authorityRetries,6)));authorityRetries=Math.Min(7,authorityRetries+1);
                double next=state==null?double.PositiveInfinity:state.CurrentTime+delay;
                if(state==null||!ProportionalMath.Finite(state.CurrentTime)||state.CurrentTime<0||!ProportionalMath.Finite(next)||next<=state.CurrentTime)
                {next=double.PositiveInfinity;HoldReason+=" Retry UT cannot represent a strictly future bounded interval.";}
                return new ConverterResources{NextChangepoint=next};
            }
            catch(Exception ex){HoldReason=(ex.InnerException??ex).Message;Factor=0;return new ConverterResources();}
        }
        public override void OnRatesComputed(BackgroundResourceProcessor processor,BrpConverter converter,RateCalculatedEvent evt)
        {
            if(HoldReason.Length>0)return;
            try
            {
                validate(record,Vessel);
                var mapped=processor.Converters.Where(c=>c.FlightId==recipe.FlightId&&c.ModuleId==recipe.ModuleId).Take(2).ToArray();
                if(mapped.Length!=1||!ReferenceEquals(mapped[0],converter)||!ReferenceEquals(converter.Behaviour,this))throw new InvalidOperationException("Proportional recipe has no single current converter owner.");
                var local=Local(processor);
                foreach(var own in local)
                {
                    int? index=processor.GetInventoryIndex(new InventoryId(own));
                    if(!index.HasValue||!ReferenceEquals(processor.Inventories[index.Value],own)||!converter.Pull.Contains(index.Value))throw new InvalidOperationException("Regenerated proportional Pull owner differs.");
                    if(processor.Converters.Any(c=>!ReferenceEquals(c,converter)&&c.Pull.Contains(index.Value)&&Consumes(c,own.ResourceName)))throw new InvalidOperationException("Shared consumed requirement needs a separate allocator.");
                    if(processor.Converters.Any(c=>!ReferenceEquals(c,converter)&&c.Push.Contains(index.Value)&&Produces(c,own.ResourceName)))throw new InvalidOperationException("Shared produced requirement needs a separate allocator.");
                    // First supported farm has one own Machinery tank. Do not
                    // certify distributed Machinery allocation as local decay.
                    if(converter.Pull.Any(i=>processor.Inventories[i].ResourceName==own.ResourceName&&processor.Inventories[i].FlightId!=recipe.FlightId))throw new InvalidOperationException("Consumed requirement has an unreviewed remote inventory path.");
                }
                double next=ProportionalMath.NextInEpoch(evt.CurrentTime,local.Select(i=>i.Amount).ToArray(),recipe.Requirements.Select(r=>r.Ratio).ToArray(),local.Select(i=>i.Rate).ToArray(),epochAmounts,Math.Max(ProportionalMath.StopFactor,ProportionalMath.StopFactor/recipe.Multiplier));
                epochDeadline=Math.Min(epochDeadline,next);converter.NextChangepoint=epochDeadline;Bound=true;
                if(epochDeadline==evt.CurrentTime)processor.SuppressNoProgressError();
            }
            catch(ColonyAuthorityUnavailableException ex)
            {
                // Install an empty recipe before elapsed production, then let
                // GetResources schedule its bounded transient-only retry.
                HoldReason=ex.Message;Bound=false;converter.NextChangepoint=evt.CurrentTime;processor.SuppressNoProgressError();
            }
            catch(Exception ex)
            {
                bindingFailure=HoldReason=(ex.InnerException??ex).Message;Bound=false;
                // Request one native refresh to install the held empty vector.
                // No amounts are written and subsequent held refresh is final.
                converter.NextChangepoint=evt.CurrentTime;processor.SuppressNoProgressError();
            }
        }
        public void Invalidate(BrpConverter converter,double ut)
        {
            if(!ProportionalMath.Finite(ut)||ut<0||converter==null||!ReferenceEquals(converter.Behaviour,this))throw new InvalidOperationException("Invalid mutation owner/time.");
            if(invalidated&&converter.NextChangepoint==ut)return;
            invalidated=true;Bound=false;HoldReason="Physical inventory mutation awaits native proportional refresh.";converter.NextChangepoint=ut;
        }
        void Hold(BrpConverter converter,double ut,string reason)
        {bindingFailure=HoldReason=reason;Bound=false;Factor=0;if(ProportionalMath.Finite(ut)&&ut>=0)converter.NextChangepoint=ut;}
        protected override void OnSave(ConfigNode node)
        {base.OnSave(node);if(record!=null)node.AddNode(record.CreateCopy());node.AddValue("holdReason",HoldReason);node.AddValue("bindingFailure",bindingFailure);node.AddValue("authorityRetries",authorityRetries);}
        protected override void OnLoad(ConfigNode node)
        {base.OnLoad(node);record=node.GetNode("COLONY_RECIPE")?.CreateCopy();bindingFailure=node.GetValue("bindingFailure")??"";Bound=false;HoldReason=record==null?(node.GetValue("holdReason")??"Missing supported colony recipe."):"Cold proportional state awaits fresh owner/index binding.";if(node.HasValue("authorityRetries")&&(!int.TryParse(node.GetValue("authorityRetries"),out authorityRetries)||authorityRetries<0||authorityRetries>7))bindingFailure="Cold authority retry state is invalid.";if(record!=null)try{recipe=ColonyRecipe.Read(record);}catch(Exception ex){HoldReason=ex.Message;}}
        public static string InvalidateAfterPhysicalMutation(object owner,double ut)
        {
            var processor=owner as BackgroundResourceProcessor;if(ReferenceEquals(processor,null))return "";
            var rows=processor.Converters.Where(c=>c.Behaviour is ExpanseColonyProportionalBehaviour).Take(513).ToArray();
            if(rows.Length==0)return "";
            try
            {
                if(rows.Length>512)throw new InvalidOperationException("Proportional mutation owner bound exceeded.");
                if(!ProportionalMath.Finite(ut)||processor.LastChangepoint!=ut)throw new InvalidOperationException("Proportional mutation does not share the exactly caught-up UT.");
                string missingRecipeHold="";
                foreach(var converter in rows)
                {
                    var behaviour=(ExpanseColonyProportionalBehaviour)converter.Behaviour;
                    // An already empty capture has no requirement inventory to
                    // refresh. Preserve its diagnostic and invalidate the other
                    // real owners without poisoning them with a null dereference.
                    if(behaviour.recipe==null)
                    {
                        behaviour.Bound=false;behaviour.Factor=0;
                        if(behaviour.HoldReason.Length==0)behaviour.HoldReason="Missing supported colony recipe.";
                        if(missingRecipeHold.Length==0)missingRecipeHold=behaviour.HoldReason;
                        continue;
                    }
                    foreach(var row in behaviour.Local(processor))
                        if(row.Snapshot==null||row.Amount!=row.Snapshot.amount||row.OriginalAmount!=row.Amount||row.MaxAmount!=row.Snapshot.maxAmount)throw new InvalidOperationException("Physical mutation inventory readback is not synchronized.");
                    behaviour.Invalidate(converter,ut);
                }
                return missingRecipeHold;
            }
            catch(Exception ex)
            {
                string reason="Proportional physical refresh hold: "+(ex.InnerException??ex).Message;
                foreach(var converter in rows)((ExpanseColonyProportionalBehaviour)converter.Behaviour).Hold(converter,processor.LastChangepoint,reason);
                return reason;
            }
        }
    }
}
