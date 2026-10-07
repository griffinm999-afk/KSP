using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        public static object FoundingIntentTerms(ColonyFoundingIntent intent)
        {
            var terms=typeof(ColonyFoundingIntent).GetProperties().ToDictionary(p=>p.Name,p=>p.GetValue(intent,null));
            if(intent.Production==null)terms.Remove("Production");
            else {var production=typeof(ColonyProductionIntent).GetProperties().ToDictionary(p=>p.Name,p=>p.GetValue(intent.Production,null));if(intent.Production.Operations==null)production.Remove("Operations");terms["Production"]=production;}return terms;
        }
        public static void ValidateProductionIntent(ColonyProductionIntent intent)
        {
            if(intent==null)Fail("Missing production intent.");Choice(intent!.Mode,"importOnly","compare","localInvestment");Text(intent.RecipeId,128);
            Range(intent.PackageCount,1,4);Range(intent.ReviewHorizonSeconds,ColonyLimits.KerbinDay,30*ColonyLimits.KerbinDay);
            if(intent.Mode!="importOnly"&&intent.RecipeId.Length==0)Fail("Select an exact installed production recipe for comparison or local investment.");
            if(intent.Operations!=null)ValidateProductionOperations(intent.Operations);
        }
        public static string ProductionRecipeHash(ColonyProductionRecipe recipe)
        {var terms=typeof(ColonyProductionRecipe).GetProperties().Where(p=>p.Name!="ConfigurationHash").ToDictionary(p=>p.Name,p=>p.GetValue(recipe,null));return Hash(ColonyJson.Serialize(terms,65536));}
        static void ProductionRates(List<ColonyProductionRate> rates,bool allowNetWithdrawal=false)
        {Rows(rates,16);Unique(rates.Select(r=>r.Resource));foreach(var r in rates){Text(r.Resource,128,true);Range(r.UnitsPerSecond,allowNetWithdrawal?-1e9:0,1e9);}}
        public static void ValidateProductionRecipe(ColonyProductionRecipe recipe)
        {
            if(recipe==null)Fail("Missing production recipe.");Text(recipe!.Id,128,true);Text(recipe.Name,160,true);Text(recipe.TemplateId,128,true);Text(recipe.TemplateHash,64,true);
            Text(recipe.HopperTemplateId,128,true);Text(recipe.HopperTemplateHash,64,true);Text(recipe.PartName,128,true);Text(recipe.ModuleName,64,true);Text(recipe.OptionName,160,true);Text(recipe.NativeExperienceEffect,128,true);
            Text(recipe.OptionHash,64,true);Range(recipe.OptionIndex,0,63);Text(recipe.EstimateBasis,512,true);
            if(recipe.CraftPartId==0||recipe.ConfigurationHash.Length!=64||recipe.TemplateHash.Length!=64||recipe.HopperTemplateHash.Length!=64||recipe.OptionHash.Length!=64||recipe.ConfigurationHash!=ProductionRecipeHash(recipe))Fail("Production catalog lacks exact template and native option identity.");
            ProductionRates(recipe.Inputs);ProductionRates(recipe.Outputs);Materials(recipe.RequiredInputs);Rows(recipe.Feeds,2);if(recipe.Feeds.Select(f=>f.CraftPartId).Distinct().Count()!=recipe.Feeds.Count)Fail("Duplicate feed craft mapping.");
            if(recipe.Id!="cultivate-substrate-v1"||recipe.ModuleName!="USI_Converter"||recipe.OptionName!="Cultivate(S)"||recipe.NativeExperienceEffect!="BotanySkill"||recipe.Feeds.Count!=2||!recipe.Feeds.Any(f=>f.Resource=="Substrate")||!recipe.Feeds.Any(f=>f.Resource=="Water")||!recipe.Outputs.Any(r=>r.Resource=="Supplies"&&r.UnitsPerSecond>0))Fail("Production provider supports only the reviewed Cultivate(S), Substrate/Water route.");
            foreach(var f in recipe.Feeds){Text(f.PartName,128,true);Text(f.Resource,128,true);Text(f.OptionHash,64,true);Range(f.OptionIndex,0,63);Range(f.WolfPoints,1,1000);Range(f.NominalUnitsPerSecond,0,1e9);if(f.CraftPartId==0||f.OptionHash.Length!=64||f.NominalUnitsPerSecond<=0)Fail("Feed hopper identity/rate is unqualified.");}
        }
        public static void ValidateProductionEnvironment(ColonyProductionEnvironment env)
        {
            if(env==null)Fail("Missing production environment.");Rows(env!.Recipes,16);Rows(env.Observations,256);Text(env.Reason,512);Unique(env.Recipes.Select(r=>r.Id));
            ValidateProductionRegistry(env.Registry);
            foreach(var r in env.Recipes)ValidateProductionRecipe(r);
            Unique(env.Observations.Select(o=>o.PlanId+":"+o.InvestmentId));
            foreach(var o in env.Observations){Id(o.PlanId);Text(o.InvestmentId,128,true);Text(o.ContextKey,256,true);Id(o.FacilityId);Time(o.ObservedUt);Text(o.RecipeHash,64);Text(o.Witness,128);Text(o.Reason,512);Range(o.CurrentNativeEfficiency,0,10000);ProductionRates(o.ObservedRecipeInputs);ProductionRates(o.ObservedRecipeOutputs);ProductionRates(o.PhysicalNetOutputs,true);Range(o.PhysicalSampleSeconds,0,21600);ProductionRates(o.NativeDeliveredInputs);ProductionRates(o.NativeDeliveredOutputs);Range(o.NativeSampleSeconds,0,21600);Text(o.NativeStatus,256);Text(o.Provider,512);Text(o.NetAttribution,256);if(o.Qualified&&(!o.Active||o.PartId==0||o.ModuleId==0||o.Witness.Length==0||o.RecipeHash.Length!=64))Fail("Productive observation lacks actual active recipe/module identity.");}
        }
        static void ValidateProductionPlans(ColonyState state,ColonyPlan plan,ColonyRecord colony)
        {
            var items=plan.Quote.ProductionInvestments;Rows(items,4);Rows(plan.Production,4);Unique(items.Select(i=>i.Id));Unique(plan.Production.Select(c=>c.Id));
            if(items.Count!=plan.Production.Count)Fail("Production claims differ from the immutable approved investment bill.");
            if(items.Count==0)return;
            var intent=plan.Quote.FoundingIntent?.Production;if(intent==null||intent.Mode!="localInvestment"||intent.PackageCount!=items.Count)Fail("Production spending lacks explicit reviewed local-investment intent.");
            ValidateProductionIntent(intent!);
            if(items.Count(i=>i.Wolf.Id.Length>0)!=1)Fail("Production requires exactly one shared compound WOLF purchase.");
            var owned=items.Single(i=>i.Wolf.Id.Length>0);var ownerClaim=plan.Production.Single(c=>c.Id==owned.Id);
            foreach(var item in items)
            {
                Text(item.Id,128,true);ValidateProductionRecipe(item.Recipe);Text(item.BuildingId,128,true);Text(item.HopperBuildingId,128,true);Text(item.Downside,512,true);
                Range(item.ReviewHorizonSeconds,ColonyLimits.KerbinDay,30*ColonyLimits.KerbinDay);Range(item.NominalSuppliesPerDay,0,1e15);
                if(item.Recipe.Id!=intent!.RecipeId||!item.Recipe.Preconfigured||item.ReviewHorizonSeconds!=intent.ReviewHorizonSeconds||item.NominalSuppliesPerDay!=item.Recipe.Outputs.Where(o=>o.Resource=="Supplies").Sum(o=>o.UnitsPerSecond)*ColonyLimits.KerbinDay)Fail("Production estimate or recipe differs from approved intent.");
                var building=plan.Buildings.SingleOrDefault(b=>b.Id==item.BuildingId);var feed=plan.Buildings.SingleOrDefault(b=>b.Id==item.HopperBuildingId);
                if(building==null||feed==null||building.TemplateId!=item.Recipe.TemplateId||building.TemplateHash!=item.Recipe.TemplateHash||feed.TemplateId!=item.Recipe.HopperTemplateId||feed.TemplateHash!=item.Recipe.HopperTemplateHash)Fail("Production lost actual paid construction line identity.");
                var c=plan.Production.Single(p=>p.Id==item.Id);Choice(c.State,"planned","ready","configuring","observing","operational","held","cancelled");Text(c.Reason,512);Rows(c.Steps,5);Unique(c.Steps.Select(s=>s.Id));
                ValidateProductionInventory(state,plan,item,c,intent.Operations);
                Text(c.OutputWitness,64);Text(c.OutputContextKey,256);Time(c.OutputObservedUt);Range(c.OutputIntervalSeconds,0,21600);Range(c.OutputSuppliesUnits,0,1e15);
                if(c.OutputWitness.Length>0 && (c.OutputWitness!=ColonyEngine.ProductionOutputWitness(plan,c)||c.OutputIntervalSeconds<=0||c.OutputSuppliesUnits<=0||c.OutputContextKey.Length==0||c.Steps.Count!=5||c.OutputPartId!=c.Steps.Last().PartId||c.OutputModuleId!=c.Steps.Last().ModuleId))Fail("Production first-delivery receipt lost exact module, recipe, context, finite interval or actual quantity.");
                if(c.State=="operational"&&c.OutputWitness.Length==0)Fail("Idle activation is not proof of real productive output.");
                if(c.WolfOrderId.Length>0)
                {Id(c.WolfOrderId);if(c.Id!=ownerClaim.Id||c.WolfOrderId!=ColonyEngine.PlanningChildId(plan.Id,c.Id+":wolf")||!state.WolfOrders.Any(w=>w.Id==c.WolfOrderId&&w.ColonyId==plan.ColonyId&&w.Quote.Funds==item.Wolf.Funds&&w.Quote.LaborSeconds==item.Wolf.LaborSeconds&&ColonyEngine.ProductionHardwareSame(item.Wolf,w.Quote)))Fail("Production WOLF child lacks exact paid reservation lineage.");}
                if(c.Steps.Count==0 && new[]{"configuring","observing","operational","held"}.Contains(c.State)||c.Steps.Count>0&&c.Steps.Count!=5)Fail("Production native state lacks all five exact steps.");
                if(new[]{"ready","configuring","observing","operational","held"}.Contains(c.State)&&ownerClaim.WolfOrderId.Length==0)Fail("Production native phase lacks the reviewed WOLF child.");
                foreach(var step in c.Steps)
                {
                    int index=c.Steps.IndexOf(step);bool farm=index==4;var selected=farm?null:item.Recipe.Feeds[index/2];
                    string suffix=farm?"start:cultivation":(index%2==0?"connect:":"start:")+selected!.CraftPartId;
                    string requiredKind=!farm&&index%2==0?"hopperConnect":"converterStart";
                    var line=farm?building:feed;var order=state.Construction.SingleOrDefault(o=>o.Id==line!.OrderId);
                    if(step.Id!=ColonyEngine.PlanningChildId(plan.Id,c.Id+":"+suffix)||step.Kind!=requiredKind||step.OptionHash!=(farm?item.Recipe.OptionHash:selected!.OptionHash)||order==null||order.FacilityId!=step.FacilityId)Fail("Native production steps differ from the five exact reviewed deterministic children.");
                    Id(step.Id);Id(step.FacilityId);Id(step.VesselId);Text(step.OptionHash,64,true);Choice(step.Kind,"hopperConnect","converterStart");Choice(step.State,"prepared","applying","applied","held");Text(step.BeforeWitness,128);Text(step.AfterWitness,128);Text(step.Reason,512);
                    Text(step.HopperId,256);if(step.Kind!="hopperConnect"&&step.HopperId.Length>0||step.Kind=="hopperConnect"&&step.State=="applied"&&step.HopperId.Length==0)Fail("Production connection lost the exact native created HopperId.");
                    if(step.State=="prepared"&&state.Effects.Any(e=>e.Id==step.Id))Fail("Unattempted production step has an external effect receipt.");
                    if(step.PartId==0||step.ModuleId==0||step.OptionHash.Length!=64||!colony.Facilities.Any(f=>f.Id==step.FacilityId&&f.VesselId==step.VesselId&&f.PartIds.Contains(step.PartId)))Fail("Production step is not a real registered module.");
                    if(step.State!="prepared")
                    {var effect=state.Effects.SingleOrDefault(e=>e.Id==step.Id&&e.OperationId==step.Id&&e.Kind=="productionNative"&&e.TargetId==plan.Id+":"+c.Id&&e.ColonyId==plan.ColonyId);if(effect==null||effect.State!=step.State||effect.BeforeWitness!=step.BeforeWitness||step.BeforeWitness.Length==0||step.State=="applied"&&(step.AfterWitness.Length==0||step.AfterWitness!=effect.AfterWitness))Fail("Native production step lost durable before/after effect receipt.");}
                }
                if(c.State=="operational"&&c.Steps.Any(s=>s.State!="applied")||plan.State=="complete"&&c.State!="operational")Fail("Productive/completed plan lacks all actual native activation receipts.");
            }
            var q=owned.Wolf;WolfIngredients(q.Demands);ValidateWolfDepot(q.Before);ValidateWolfDepot(q.After);Rows(q.Modules,32);foreach(var m in q.Modules){ValidateWolfRecipe(m.Recipe);Range(m.Count,1,64);}
            var demands=items.SelectMany(i=>i.Recipe.Feeds).GroupBy(f=>f.Resource,StringComparer.Ordinal).Select(g=>new ColonyWolfIngredient{Resource=g.Key,Points=g.Sum(f=>f.WolfPoints)}).OrderBy(x=>x.Resource,StringComparer.Ordinal).ToArray();
            if(q.Id!=WolfQuoteHash(q)||q.Modules.Sum(m=>m.Count)>64||q.Funds!=checked((q.EstablishDepot?q.DepotConstruction.Funds:0)+q.Modules.Sum(m=>checked(m.Recipe.Funds*m.Count)))||q.LaborSeconds!=(q.EstablishDepot?q.DepotConstruction.LaborSeconds:0)+q.Modules.Sum(m=>m.Recipe.LaborSeconds*m.Count)||WolfDepotHash(q.After)!=WolfDepotHash(ColonyEngine.ExpectedWolfDepot(q))||!ColonyEngine.WolfDemandsSatisfied(q.After,q)||q.Before.Body!=colony.Site.Body||q.Before.Biome!=colony.Site.Biome||q.Demands.Count!=demands.Length||demands.Any(d=>!q.Demands.Any(x=>x.Resource==d.Resource&&x.Points==d.Points)))Fail("Production dependency bill violates whole-depot/material/funding conservation.");
        }
    }
}
