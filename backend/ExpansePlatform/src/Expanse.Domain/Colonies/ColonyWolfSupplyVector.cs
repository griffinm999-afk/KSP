using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        sealed class WolfSupplyCandidate
        {
            public List<ColonyWolfModule> Modules=new List<ColonyWolfModule>();
            public long Funds;
            public double Labor;
            public int Power,Count;
            public string Key=>string.Join("|",Modules.Select(m=>m.Recipe.Id+":"+m.Count.ToString(CultureInfo.InvariantCulture)));
        }
        // One real depot snapshot and one package: independent scalar quotes
        // cannot compose because their unchanged Power/vein witnesses overlap.
        public static ColonyWolfQuote QuoteWolfSupplies(ColonyState state,string colonyId,IReadOnlyList<ColonyWolfIngredient> demands,ColonyEnvironment env)
        {
            if(demands==null || demands.Count<1 || demands.Count>16)throw new InvalidDataException("Combined WOLF demand must contain one to sixteen named point requirements.");
            var requested=demands.Select(d=>d==null ? throw new InvalidDataException("Missing WOLF demand.") : new ColonyWolfIngredient {Resource=d.Resource,Points=d.Points})
                .OrderBy(d=>d.Resource,StringComparer.Ordinal).ToList();
            ColonyStateCodec.WolfIngredients(requested);
            // Preserve the original scalar binary quote projection, including
            // its existing selection behavior and all already paid old quotes.
            if(requested.Count==1)return QuoteWolfSupply(state,colonyId,requested[0].Resource,requested[0].Points,env);
            var q=CreateWolfSupplyQuote(state,colonyId,requested[0].Resource,requested[0].Points,env);
            q.Demands=requested;
            q.BalancePolicy+=" Compound selection compares one installed recipe type per raw commodity, shared Power, full price and sequential lead time; mixed-recipe global optimum is not claimed.";
            var colony=Colony(state,colonyId);var baseline=ExpectedWolfDepot(q);
            var frontier=new List<WolfSupplyCandidate>{new WolfSupplyCandidate()};
            foreach(var demand in requested.Where(d=>d.Resource!="Power"))
            {
                int deficit=Math.Max(0,demand.Points-WolfAvailable(baseline,demand.Resource));if(deficit==0)continue;
                var choices=new List<WolfSupplyCandidate>();
                var harvesters=env.Wolf.Recipes.Where(r=>r.Outputs.Count==1 && r.Outputs[0].Resource==demand.Resource &&
                    r.Inputs.Any(i=>i.Resource==demand.Resource+"Vein") && r.Inputs.All(i=>i.Resource=="Power" || i.Resource==demand.Resource+"Vein"))
                    .Where(r=>WolfTechAvailable(r,env,colony)).OrderBy(r=>r.Id,StringComparer.Ordinal).ToArray();
                foreach(var recipe in harvesters)
                {
                    int count=checked((deficit+recipe.Outputs[0].Points-1)/recipe.Outputs[0].Points);
                    if(count+(q.EstablishDepot ? 1 : 0)>64 || recipe.Inputs.Where(i=>i.Resource!= "Power").Any(i=>
                        checked(i.Points*count)>WolfAvailable(baseline,i.Resource)-requested.Where(d=>d.Resource==i.Resource).Select(d=>d.Points).DefaultIfEmpty(0).Single()))continue;
                    choices.Add(new WolfSupplyCandidate {Modules=new List<ColonyWolfModule>{new ColonyWolfModule {Recipe=CloneWolfRecipe(recipe),Count=count}},
                        Funds=checked(recipe.Funds*count),Labor=recipe.LaborSeconds*count,Power=checked(recipe.Inputs.Where(i=>i.Resource=="Power").Sum(i=>i.Points)*count),Count=count});
                }
                if(choices.Count==0)throw new InvalidDataException("No unlocked installed bounded "+demand.Resource+" harvester can meet the exact demand using actual "+demand.Resource+"Vein capacity; veins and crew points are not fabricated.");
                var next=new List<WolfSupplyCandidate>();int combinations=0;
                foreach(var prior in frontier)foreach(var choice in choices)
                {
                    if(++combinations>65536)throw new InvalidDataException("Combined installed WOLF candidate search exceeded its reviewed bound; reduce the demand vector.");
                    int count=checked(prior.Count+choice.Count);if(count+(q.EstablishDepot ? 1 : 0)>64)continue;
                    AddWolfFrontier(next,new WolfSupplyCandidate {Modules=prior.Modules.Concat(choice.Modules).ToList(),Funds=checked(prior.Funds+choice.Funds),
                        Labor=prior.Labor+choice.Labor,Power=checked(prior.Power+choice.Power),Count=count});
                    if(next.Count>4096)throw new InvalidDataException("Combined WOLF cost/power/lead-time frontier exceeded 4096 candidates; no optimality or allocation is inferred.");
                }
                if(next.Count==0)throw new InvalidDataException("Combined raw extraction exceeds the finite 64-module supplier package.");
                frontier=next;
            }
            int desiredPower=requested.Where(d=>d.Resource=="Power").Select(d=>d.Points).DefaultIfEmpty(0).Single();
            int availablePower=WolfAvailable(baseline,"Power");WolfSupplyCandidate? best=null;
            var powerRecipes=env.Wolf.Recipes.Where(r=>r.Inputs.Count==0 && r.Outputs.Count==1 && r.Outputs[0].Resource=="Power")
                .Where(r=>WolfTechAvailable(r,env,colony)).OrderBy(r=>r.Id,StringComparer.Ordinal).ToArray();
            foreach(var candidate in frontier)
            {
                int missing=Math.Max(0,checked(candidate.Power+desiredPower-availablePower));
                if(missing==0){best=BetterWolfCandidate(best,candidate);continue;}
                foreach(var power in powerRecipes)
                {
                    int count=checked((missing+power.Outputs[0].Points-1)/power.Outputs[0].Points);
                    if(candidate.Count+count+(q.EstablishDepot ? 1 : 0)>64)continue;
                    var complete=new WolfSupplyCandidate {Modules=new[]{new ColonyWolfModule {Recipe=CloneWolfRecipe(power),Count=count}}.Concat(candidate.Modules).ToList(),
                        Funds=checked(candidate.Funds+checked(power.Funds*count)),Labor=candidate.Labor+power.LaborSeconds*count,Power=candidate.Power,Count=candidate.Count+count};
                    best=BetterWolfCandidate(best,complete);
                }
            }
            if(best==null)throw new InvalidDataException("Shared Power dependency cannot be met by an unlocked installed input-free recipe within 64 purchased modules; crew points are not fabricated.");
            q.Modules=best.Modules;q.After=ExpectedWolfDepot(q);
            if(!WolfDemandsSatisfied(q.After,q))throw new InvalidDataException("Combined WOLF package does not satisfy every exact unallocated demand.");
            q.Funds=checked((q.EstablishDepot ? q.DepotConstruction.Funds : 0)+best.Funds);
            q.LaborSeconds=(q.EstablishDepot ? q.DepotConstruction.LaborSeconds : 0)+best.Labor;
            q.Id=ColonyStateCodec.WolfQuoteHash(q);return q;
        }
        static bool WolfTechAvailable(ColonyWolfRecipe recipe,ColonyEnvironment env,ColonyRecord colony)=>env.UnlockedTech.Contains(recipe.Tech) || env.DevelopmentMode && colony.Charter.Sandbox;
        static bool WolfDominates(WolfSupplyCandidate a,WolfSupplyCandidate b)=>a.Funds<=b.Funds && a.Power<=b.Power && a.Count<=b.Count && a.Labor<=b.Labor;
        static void AddWolfFrontier(List<WolfSupplyCandidate> frontier,WolfSupplyCandidate candidate)
        {
            foreach(var old in frontier)if(WolfDominates(old,candidate) && (old.Funds!=candidate.Funds || old.Power!=candidate.Power || old.Count!=candidate.Count || old.Labor!=candidate.Labor || string.CompareOrdinal(old.Key,candidate.Key)<=0))return;
            frontier.RemoveAll(old=>WolfDominates(candidate,old));frontier.Add(candidate);
        }
        static WolfSupplyCandidate BetterWolfCandidate(WolfSupplyCandidate? current,WolfSupplyCandidate candidate)
        {
            if(current==null || candidate.Funds<current.Funds || candidate.Funds==current.Funds && (candidate.Labor<current.Labor || candidate.Labor==current.Labor &&
                (candidate.Count<current.Count || candidate.Count==current.Count && string.CompareOrdinal(candidate.Key,current.Key)<0)))return candidate;
            return current;
        }
        internal static bool WolfDemandsSatisfied(ColonyWolfDepot depot,ColonyWolfQuote q)=>q.Demands!=null && q.Demands.Count>0
            ? q.Demands.All(d=>WolfAvailable(depot,d.Resource)>=d.Points) : WolfAvailable(depot,q.Resource)>=q.DesiredAvailable;
        static List<ColonyWolfIngredient> ReadWolfCommandDemands(ColonyCommand command)
        {
            if(command.Fields.Count<1 || command.Fields.Count>16 || command.Fields.Keys.Any(k=>!k.StartsWith("Demand:",StringComparison.Ordinal)))throw new InvalidDataException("Combined WOLF command requires only one to sixteen exact Demand:<Resource> point fields.");
            var rows=command.Fields.Select(p=>new ColonyWolfIngredient {Resource=p.Key.Substring(7),Points=int.Parse(p.Value,NumberStyles.None,CultureInfo.InvariantCulture)}).OrderBy(d=>d.Resource,StringComparer.Ordinal).ToList();
            ColonyStateCodec.WolfIngredients(rows);return rows;
        }
    }
}
