using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        static void QuotePlanningProduction(ColonyPlanningQuote q,ColonyState state,ColonyRecord colony,ColonyEnvironment env,ColonyFoundingIntent? intent,HashSet<string> usedPlots)
        {
            if(intent?.Production==null)return;
            ColonyStateCodec.ValidateProductionIntent(intent.Production);ColonyStateCodec.ValidateProductionEnvironment(env.Production);
            var choice=intent.Production;
            if(choice.Mode=="importOnly")return;
            var recipe=env.Production.Recipes.SingleOrDefault(r=>r.Id==choice.RecipeId)??throw new InvalidDataException(env.Production.Reason.Length>0?env.Production.Reason:"Selected installed production recipe is unavailable.");
            if(choice.Mode=="compare")return; // Read-only alternative; no new spend.
            if(!recipe.Preconfigured)throw new InvalidDataException("The selected fresh package is not bound to the reviewed native loadout; an unpriced bay change is not authorized.");
            var template=env.Templates.SingleOrDefault(t=>t.Id==recipe.TemplateId&&t.Hash==recipe.TemplateHash);
            var hopper=env.Templates.SingleOrDefault(t=>t.Id==recipe.HopperTemplateId&&t.Hash==recipe.HopperTemplateHash);
            foreach(var t in new[]{template,hopper})
                if(t==null || !(t.RuntimeCertified&&t.CertificationEvidence.Length>0 || env.DevelopmentMode&&colony.Charter.Sandbox) || !colony.Charter.Sandbox&&t.RequiredTech.Any(tech=>!env.UnlockedTech.Contains(tech)))
                    throw new InvalidDataException("Production requires exact unlocked and certified building/feed packages (isolated candidates only under authorized sandbox development).");
            var demands=recipe.Feeds.GroupBy(f=>f.Resource,StringComparer.Ordinal).Select(g=>new ColonyWolfIngredient{Resource=g.Key,Points=checked(g.Sum(f=>f.WolfPoints)*choice.PackageCount)}).ToArray();
            var wolf=QuoteWolfSupplies(state,colony.Id,demands,env);
            for(int i=0;i<choice.PackageCount;i++)
            {
                int first=q.Buildings.Count;AddPlanningBuilding(q,"production",template,colony,usedPlots);
                if(q.Buildings.Count!=first+1)throw new InvalidDataException("Production building was not added to the paid bill.");
                var building=q.Buildings.Last();AddPlanningBuilding(q,"productionFeed",hopper,colony,usedPlots);var feed=q.Buildings.Last();
                q.ProductionInvestments.Add(new ColonyProductionInvestment{Id="production-"+i.ToString(CultureInfo.InvariantCulture),
                    Recipe=ColonyJson.Deserialize<ColonyProductionRecipe>(ColonyJson.Serialize(recipe,65536),65536),BuildingId=building.Id,HopperBuildingId=feed.Id,
                    Wolf=i==0?wolf:new ColonyWolfQuote(),ReviewHorizonSeconds=choice.ReviewHorizonSeconds,
                    NominalSuppliesPerDay=recipe.Outputs.Where(o=>o.Resource=="Supplies").Sum(o=>o.UnitsPerSecond)*ColonyLimits.KerbinDay});
            }
            QuoteProductionInventory(q,state,env,choice.Operations);
        }
        public static long ProductionExtraFunds(ColonyPlanningQuote q)=>q.ProductionInvestments.Sum(i=>i.Wolf.Funds);
        public static ColonyPlanningQuote QuoteProductionAlternative(ColonyState state,string colonyId,ColonyEnvironment env,ColonyFoundingIntent intent)
        {
            if(intent?.Production==null||intent.Production.Mode!="compare")throw new InvalidDataException("An explicit installed production comparison is required.");
            var alternative=ColonyJson.Deserialize<ColonyFoundingIntent>(ColonyJson.Serialize(intent,32768),32768);alternative.Production!.Mode="localInvestment";
            return QuoteFoundingPlan(state,colonyId,env,alternative);
        }
        public static long ProductionFutureFunds(ColonyPlan p)=>p.Production.Where(c=>c.WolfOrderId.Length==0&&c.State!="cancelled").Sum(c=>p.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Wolf.Funds);
        static List<ColonyProductionClaim> CreatePlanningProductionClaims(ColonyPlanningQuote q)=>q.ProductionInvestments.Select(i=>new ColonyProductionClaim{Id=i.Id,
            Inventory=i.Inventory==null?null:new ColonyProductionInventoryClaim{Endpoints=i.Inventory.Endpoints.Select(e=>new ColonyProductionEndpointReceipt{SpecId=e.Id}).ToList()}}).ToList();
        internal static bool ProductionHardwareSame(ColonyWolfQuote a,ColonyWolfQuote b)=>
            a.Funds==b.Funds&&a.LaborSeconds==b.LaborSeconds&&a.EstablishDepot==b.EstablishDepot&&
            ColonyJson.Serialize(a.Modules,262144).SequenceEqual(ColonyJson.Serialize(b.Modules,262144))&&
            ColonyJson.Serialize(a.Demands,65536).SequenceEqual(ColonyJson.Serialize(b.Demands,65536))&&
            (a.Demands.Count>0||a.Resource==b.Resource&&a.DesiredAvailable==b.DesiredAvailable);
        static bool RunPlanningProduction(ColonyState state,ColonyPlan plan,ColonyEnvironment env)
        {
            if(plan.Production.Count==0)return false;
            var owner=plan.Production.Single(c=>plan.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Wolf.Id.Length>0);
            var reviewed=plan.Quote.ProductionInvestments.Single(i=>i.Id==owner.Id).Wolf;
            if(owner.WolfOrderId.Length==0)
            {
                var demands=reviewed.Demands.Count>0?reviewed.Demands:new List<ColonyWolfIngredient>{new ColonyWolfIngredient{Resource=reviewed.Resource,Points=reviewed.DesiredAvailable}};
                var fresh=QuoteWolfSupplies(state,plan.ColonyId,demands,env);
                if(!ProductionHardwareSame(reviewed,fresh))throw new InvalidDataException("Reviewed production WOLF module purchase changed; no substitute hardware, free capacity or unreviewed price is accepted.");
                var cmd=PlanningCommand(state,env,plan,owner.Id+":wolf","approveWolfSupplies",new Dictionary<string,string>());
                cmd.QuoteId=fresh.Id;owner.WolfOrderId=cmd.OperationId;plan.RemainingFunds=checked(plan.RemainingFunds-reviewed.Funds);
                ReserveWolfQuote(state,cmd,env,fresh);plan.Reason="Reviewed compound WOLF dependency purchase reserved; actual payment/allocation and physical output still required.";return true;
            }
            var order=state.WolfOrders.Single(o=>o.Id==owner.WolfOrderId);
            if(order.State=="cancelled")throw new InvalidDataException("Production dependency child cancelled; the parent cannot invent replacement capacity.");
            if(order.State!="operational")return false;
            foreach(var claim in plan.Production.Where(c=>c.State=="planned"))
            {
                var item=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id);
                if(new[]{item.BuildingId,item.HopperBuildingId}.Any(id=>{var b=plan.Buildings.Single(x=>x.Id==id);return b.OrderId.Length==0||!state.Construction.Any(o=>o.Id==b.OrderId&&o.State=="operational");}))continue;
                if(plan.Workers.Where(w=>plan.Quote.BootstrapWorkers.Any(q=>q.Id==w.Id&&q.BuildingId==item.BuildingId)).Any(w=>w.State!="complete"))continue;
                if(ReleaseProductionOperatingReserves(state,plan,claim,item))return true;
                if(claim.Inventory!=null&&claim.Inventory.Endpoints.Any(e=>e.State!="applied"))continue;
                claim.State="ready";claim.Reason="Real paid buildings and named worker ready; native loadout/connect/start and active qualification are still required.";return true;
            }
            if(ApplyProductionOperatingPolicies(state,plan,env))return true;
            foreach(var claim in plan.Production.Where(c=>c.State=="observing"))
            {
                var observation=env.Production.Observations.SingleOrDefault(o=>o.PlanId==plan.Id&&o.InvestmentId==claim.Id&&o.ContextKey==env.ContextKey&&o.Active&&o.Qualified&&o.ObservedUt<=env.Ut&&env.Ut-o.ObservedUt<=10);
                if(observation==null)continue;
                double supplies=observation.NativeDeliveredOutputs.Where(r=>r.Resource=="Supplies").Sum(r=>r.UnitsPerSecond)*observation.NativeSampleSeconds;
                if(observation.NativeSampleSeconds<=0||supplies<=0||double.IsNaN(supplies)||double.IsInfinity(supplies)||observation.PartId!=claim.Steps.Last().PartId||observation.ModuleId!=claim.Steps.Last().ModuleId||observation.RecipeHash!=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id).Recipe.ConfigurationHash)continue;
                if(claim.Steps.Count!=5||claim.Steps.Any(s=>s.State!="applied"))throw new InvalidDataException("Productive state lacks all real hopper connection/start and cultivation-start receipts.");
                claim.OutputObservedUt=observation.ObservedUt;claim.OutputIntervalSeconds=observation.NativeSampleSeconds;claim.OutputSuppliesUnits=supplies;claim.OutputContextKey=env.ContextKey;claim.OutputPartId=observation.PartId;claim.OutputModuleId=observation.ModuleId;
                claim.OutputWitness=ProductionOutputWitness(plan,claim);
                claim.State="operational";claim.Reason="Actual active recipe, worker, power/input/background witnesses qualified. Native production owns outputs; only physical transfer receipts credit stock.";return true;
            }
            return false;
        }
        public static string ProductionOutputWitness(ColonyPlan plan,ColonyProductionClaim claim)
        {
            var terms=new Dictionary<string,object>{["PlanId"]=plan.Id,["InvestmentId"]=claim.Id,["RecipeHash"]=plan.Quote.ProductionInvestments.Single(i=>i.Id==claim.Id).Recipe.ConfigurationHash,
                ["ContextKey"]=claim.OutputContextKey,["PartId"]=claim.OutputPartId,["ModuleId"]=claim.OutputModuleId,["ObservedUt"]=claim.OutputObservedUt,["Seconds"]=claim.OutputIntervalSeconds,["SuppliesUnits"]=claim.OutputSuppliesUnits};
            // Retain an existing receipt only when its original complete numeric
            // projection matches exactly. R differs between Mono/framework and
            // .NET8; legacy G15/G17 spellings must preserve every binary64 bit.
            string current=System.Text.Encoding.UTF8.GetString(ColonyJson.Serialize(terms,65536));
            if(claim.OutputWitness.Length>0)
            {
                if(claim.OutputWitness==ColonyStateCodec.Hash(System.Text.Encoding.UTF8.GetBytes(current)))return claim.OutputWitness;
                var keys=new[]{"ObservedUt","Seconds","SuppliesUnits"};
                var variants=keys.Select(key=>new[]{"G15","G17"}.Select(format=>((double)terms[key]).ToString(format,CultureInfo.InvariantCulture))
                    .Where(text=>double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double decoded)&&BitConverter.DoubleToInt64Bits(decoded)==BitConverter.DoubleToInt64Bits((double)terms[key])).Distinct().ToArray()).ToArray();
                foreach(var ut in variants[0])foreach(var seconds in variants[1])foreach(var supplies in variants[2])
                {
                    string legacy=current;
                    var values=new[]{ut,seconds,supplies};
                    for(int i=0;i<keys.Length;i++)
                    {
                        string token="\""+keys[i]+"\":"+((double)terms[keys[i]]).ToString("R",CultureInfo.InvariantCulture);
                        // Match a whole top-level scalar, never a decimal prefix
                        // or an escaped field-like string in the context.
                        legacy=legacy.Replace(token+",","\""+keys[i]+"\":"+values[i]+",").Replace(token+"}","\""+keys[i]+"\":"+values[i]+"}");
                    }
                    if(claim.OutputWitness==ColonyStateCodec.Hash(System.Text.Encoding.UTF8.GetBytes(legacy)))return claim.OutputWitness;
                }
            }
            // New witnesses are independent of runtime decimal formatting.
            // DTO quantities remain numbers, and no stored receipt is rewritten.
            terms["WitnessFormat"]="binary64-v1";
            foreach(string key in new[]{"ObservedUt","Seconds","SuppliesUnits"})terms[key]=BitConverter.DoubleToInt64Bits((double)terms[key]).ToString("X16",CultureInfo.InvariantCulture);
            return ColonyStateCodec.Hash(ColonyJson.Serialize(terms,65536));
        }
        static void CancelPlanningProduction(ColonyPlan plan)
        {foreach(var c in plan.Production.Where(c=>c.State=="planned"&&c.WolfOrderId.Length==0)){c.State="cancelled";c.Reason="Unstarted production intent cancelled; paid native children retain their real receipts.";}}
        public static ColonyState PrepareProductionSteps(ColonyState prior,string planId,string investmentId,IReadOnlyList<ColonyProductionStep> steps)
        {
            if(steps==null||steps.Count!=5||steps.Any(s=>s.State!="prepared"||s.BeforeWitness.Length>0||s.AfterWitness.Length>0))throw new InvalidDataException("Production requires exactly two hopper connects, two hopper starts and one native cultivation start.");
            var state=ColonyStateCodec.Copy(prior);var claim=state.Plans.Single(p=>p.Id==planId).Production.Single(c=>c.Id==investmentId);
            if(claim.State!="ready"||claim.Steps.Count>0)throw new InvalidDataException("Production is not ready for first native preparation.");
            claim.Steps=steps.Select(s=>new ColonyProductionStep {Id=s.Id,Kind=s.Kind,FacilityId=s.FacilityId,VesselId=s.VesselId,PartId=s.PartId,ModuleId=s.ModuleId,
                OptionHash=s.OptionHash,State=s.State,HopperId=s.HopperId,BeforeWitness=s.BeforeWitness,AfterWitness=s.AfterWitness,Reason=s.Reason}).ToList();claim.State="configuring";
            return FinishPlanningTransition(state);
        }
        public static ColonyState MarkProductionApplying(ColonyState prior,string planId,string investmentId,string stepId,string beforeWitness)
        {
            var state=ColonyStateCodec.Copy(prior);var plan=state.Plans.Single(p=>p.Id==planId);var claim=plan.Production.Single(c=>c.Id==investmentId);var step=claim.Steps.Single(s=>s.Id==stepId);
            if(!PlanningActive(plan)||claim.State!="configuring"||step.State!="prepared"||claim.Steps.TakeWhile(s=>s.Id!=stepId).Any(s=>s.State!="applied"))throw new InvalidDataException("Native production step is not the exact next prepared child.");
            ColonyStateCodec.Text(beforeWitness,128,true);step.State="applying";step.BeforeWitness=beforeWitness;step.Reason="Native outcome unverified; do not resend.";
            state.Effects.Add(new ColonyEffect {Id=step.Id,OperationId=step.Id,ColonyId=plan.ColonyId,TargetId=plan.Id+":"+claim.Id,Kind="productionNative",State="applying",BeforeWitness=beforeWitness,Provider="Installed MKS/WOLF",Reason=step.Reason});
            return FinishPlanningTransition(state);
        }
        public static ColonyState CompleteProductionStep(ColonyState prior,string planId,string investmentId,string stepId,string actualAfterWitness,string actualHopperId="")
        {
            var state=ColonyStateCodec.Copy(prior);var claim=state.Plans.Single(p=>p.Id==planId).Production.Single(c=>c.Id==investmentId);var step=claim.Steps.Single(s=>s.Id==stepId);
            if(step.State!="applying")throw new InvalidDataException("Production receipt lacks its durable attempted boundary.");
            ColonyStateCodec.Text(actualHopperId,256);if(step.Kind=="hopperConnect"&&actualHopperId.Length==0||step.Kind!="hopperConnect"&&actualHopperId.Length>0)throw new InvalidDataException("Native connection receipt requires exactly its actual created HopperId.");step.HopperId=actualHopperId;
            ColonyStateCodec.Text(actualAfterWitness,128,true);step.State="applied";step.AfterWitness=actualAfterWitness;step.Reason="Exact native before/after readback confirmed.";
            var effect=state.Effects.Single(e=>e.Id==step.Id&&e.State=="applying");effect.State="applied";effect.AfterWitness=actualAfterWitness;effect.Reason=step.Reason;
            if(claim.Steps.All(s=>s.State=="applied")){claim.State="observing";claim.Reason="Native modules started; awaiting current final recipe and full productive qualification.";}
            return FinishPlanningTransition(state);
        }
        public static void HoldProductionEffect(ColonyState state,ColonyEffect effect,string reason)
        {
            if(effect.Kind=="productionRegistration")
            {var receipt=state.Plans.SelectMany(p=>p.Production.Where(c=>c.Inventory!=null).SelectMany(c=>c.Inventory!.Endpoints)).Single(e=>e.EffectId==effect.Id);receipt.State="held";receipt.Reason=reason.Length>512?reason.Substring(0,512):reason;return;}
            if(effect.Kind!="productionNative")return;
            var matches=state.Plans.SelectMany(p=>p.Production.SelectMany(c=>c.Steps.Where(s=>s.Id==effect.Id).Select(s=>new{Claim=c,Step=s}))).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("Production hold lost its exact saved native child.");
            matches[0].Step.State="held";matches[0].Step.Reason=reason.Length>512?reason.Substring(0,512):reason;matches[0].Claim.State="held";matches[0].Claim.Reason=matches[0].Step.Reason;
        }
        public static ColonyState HoldProductionStep(ColonyState prior,string planId,string investmentId,string stepId,string reason,string observedAfter="")
        {
            var state=ColonyStateCodec.Copy(prior);var claim=state.Plans.Single(p=>p.Id==planId).Production.Single(c=>c.Id==investmentId);var step=claim.Steps.Single(s=>s.Id==stepId);
            if(step.State!="applying")throw new InvalidDataException("Only an attempted native step can hold an unknown outcome.");
            step.State="held";step.AfterWitness=observedAfter;step.Reason=reason.Length>512?reason.Substring(0,512):reason;claim.State="held";claim.Reason=step.Reason;
            var effect=state.Effects.Single(e=>e.Id==step.Id);effect.State="held";effect.AfterWitness=observedAfter;effect.Reason=step.Reason;return FinishPlanningTransition(state);
        }
    }
}
