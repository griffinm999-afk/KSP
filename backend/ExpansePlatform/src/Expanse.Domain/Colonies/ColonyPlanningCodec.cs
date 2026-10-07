using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        static void MigratePlanning(ColonyState state)
        {
            if(state.Plans==null) state.Plans=new List<ColonyPlan>();
            if(state.ReorderPolicies==null) state.ReorderPolicies=new List<ColonyReorderPolicy>();
        }
        public static string PlanningQuoteHash(ColonyPlanningQuote q)
        {
            // Display forecasts/reasons are not purchase terms. Retain the exact
            // immutable bill, module/plot/dependency identity and authority hash.
            var terms=new ColonyPlanningQuote { Kind=q.Kind,ColonyId=q.ColonyId,ContextKey=q.ContextKey,Revision=q.Revision,AuthorityHash=q.AuthorityHash,
                SupportPolicyHash=q.SupportPolicyHash,Buildings=q.Buildings,Imports=q.Imports,Materials=q.Materials,ExistingIncoming=q.ExistingIncoming,
                StagingPolicyId=q.StagingPolicyId,StagingPolicyHash=q.StagingPolicyHash,StagingFunds=q.StagingFunds,StartupSupportReserve=q.StartupSupportReserve,
                TargetPopulation=q.TargetPopulation,
                TotalFunds=q.TotalFunds,LaborSeconds=q.LaborSeconds,BootstrapWorkers=q.BootstrapWorkers,
                FoundingIntent=q.FoundingIntent,Residents=q.Residents,StartupPolicies=q.StartupPolicies,ProductionInvestments=q.ProductionInvestments };
            // Preserve the original saved quote byte projection when adding a
            // progress-only substitution field. Newly loaded old paid plans must
            // retain their original authority hash, not be silently requoted.
            var projection=typeof(ColonyPlanningQuote).GetProperties().ToDictionary(p=>p.Name,p=>p.GetValue(terms,null));
            // Empty additive worker contracts preserve pre-existing paid quotes.
            if(q.BootstrapWorkers.Count==0)projection.Remove("BootstrapWorkers");
            if(q.FoundingIntent==null)projection.Remove("FoundingIntent");
            else projection["FoundingIntent"]=FoundingIntentTerms(q.FoundingIntent);
            if(q.ProductionInvestments.Count==0)projection.Remove("ProductionInvestments");
            else projection["ProductionInvestments"]=q.ProductionInvestments.Select(i=>{var row=typeof(ColonyProductionInvestment).GetProperties().ToDictionary(p=>p.Name,p=>p.GetValue(i,null));if(i.Inventory==null)row.Remove("Inventory");return row;}).ToList();
            if(q.Residents.Count==0)projection.Remove("Residents");
            if(q.StartupPolicies==null)projection.Remove("StartupPolicies");
            projection.Remove("MakeImportComparisons");
            projection["Imports"]=q.Imports.Select(i=>new Dictionary<string,object> {
                ["Id"]=i.Id,["SupplierId"]=i.SupplierId,["SupplierTermsHash"]=i.SupplierTermsHash,["Resource"]=i.Resource,["Amount"]=i.Amount,
                ["FundsPerUnit"]=i.FundsPerUnit,["FreightFunds"]=i.FreightFunds,["Funds"]=i.Funds,["TravelSeconds"]=i.TravelSeconds,["ShipmentId"]=i.ShipmentId }).ToList();
            return Hash(ColonyJson.Serialize(projection,ColonyLimits.MaxBytes));
        }
        public static void ValidatePlanningEnvironment(ColonyPlanningEnvironment env)
        {
            if(env==null) Fail("Missing planning environment.");
            Rows(env!.Roles,32); Rows(env.Assets,1024); Rows(env.SurveyedPlots,64);
            ValidateLocalStocks(env.LocalStocks);
            Range(env.CadenceSeconds,60,30*ColonyLimits.KerbinDay); Range(env.ImportDelayFactor,1,10); Text(env.SurveyFailure,512);
            Unique(env.Roles.Select(r=>r.Role+":"+r.TemplateId));
            foreach(var role in env.Roles) { Choice(role.Role,"housing","workshop","storage","power","lamp");Text(role.TemplateId,128,true);Count(role.MinimumCount,64); }
            Unique(env.Assets.Select(a=>a.ColonyId+":"+a.FacilityId+":"+a.Role));
            foreach(var asset in env.Assets) { Id(asset.ColonyId);Id(asset.FacilityId);Text(asset.Role,32,true);Text(asset.Name,160);Text(asset.ContextKey,256,true);Text(asset.Witness,128);Text(asset.IdentityHash,64);Count(asset.Homes,512);if(asset.Qualified && asset.Witness.Length==0) Fail("Qualified asset lacks provider evidence."); }
        }
        public static void ValidateReorderPolicy(ColonyReorderPolicy p)
        {
            Id(p.ColonyId);Text(p.Resource,128,true);Quantity(p.ReorderPoint);Quantity(p.TargetAmount);Time(p.NextReviewUt);Text(p.Reason,512);
            Range(p.CadenceSeconds,60,30*ColonyLimits.KerbinDay);
            if(p.TargetAmount<p.ReorderPoint || p.TargetAmount==0) Fail("Reorder target must be positive and at least its reorder point.");
        }
        static void ValidatePlanning(ColonyState state)
        {
            Rows(state.Plans,64);Rows(state.ReorderPolicies,ColonyLimits.Resources*ColonyLimits.Colonies);Unique(state.Plans.Select(p=>p.Id));
            Unique(state.ReorderPolicies.Select(p=>p.ColonyId+":"+p.Resource));
            foreach(var p in state.ReorderPolicies)
            { ValidateReorderPolicy(p);var colony=state.Colonies.SingleOrDefault(c=>c.Id==p.ColonyId);if(colony==null || !colony.Stock.Any(s=>s.Resource==p.Resource && s.Capacity>=p.TargetAmount)) Fail("Reorder lacks owned receiving storage."); }
            if(state.Plans.Where(p=>p.State!="cancelled"&&p.State!="complete").GroupBy(p=>p.ColonyId).Any(g=>g.Count()>1)) Fail("More than one active plan owns a colony.");
            foreach(var plan in state.Plans)
            {
                Id(plan.Id);Id(plan.ColonyId);Choice(plan.State,"approved","procuring","constructing","complete","cancelled");Time(plan.CreatedUt);Funds(plan.RemainingFunds);Text(plan.Reason,512);
                Quantity(plan.PreviousSupportFloor);
                var colony=state.Colonies.SingleOrDefault(c=>c.Id==plan.ColonyId);if(colony==null) Fail("Plan refers to a missing colony.");
                var q=plan.Quote;if(q==null)Fail("Missing reviewed plan quote.");Rows(q!.BootstrapWorkers,64);Rows(plan.Workers,64);Rows(q.Residents,64);Rows(plan.Residents,64);
                if(!q.CanApprove || q.ColonyId!=plan.ColonyId || q.Id!=PlanningQuoteHash(q)) Fail("Plan immutable quote lineage is invalid.");
                Text(q!.Id,64,true);Text(q.ContextKey,256,true);Text(q.AuthorityHash,64,true);Text(q.SupportPolicyHash,128,true);Choice(q.Kind,"founding","growth");Count(q.TargetPopulation,512);
                Funds(q.TotalFunds);Funds(q.StagingFunds);Quantity(q.StartupSupportReserve);Time(q.LaborSeconds);Text(q.Rationale,512);Text(q.MakeOrImport,512);
                Rows(q.Buildings,64);Rows(q.Imports,64);Materials(q.Materials);Rows(q.ExistingIncoming,ColonyLimits.Shipments);
                if(q.Imports.Any(i=>i.SubstitutedLocallyAmount!=0))Fail("Immutable quote cannot contain local substitution progress.");
                Rows(q.Blockers,64);Rows(q.ExistingAssets,ColonyLimits.Facilities);foreach(string asset in q.ExistingAssets)Text(asset,256);
                Rows(q.MakeImportComparisons,ColonyLimits.Resources);
                foreach(var comparison in q.MakeImportComparisons)
                {Text(comparison.Resource,128,true);Quantity(comparison.ReviewedImportAmount);Quantity(comparison.AccessibleLocalAmount);Funds(comparison.ReviewedMaximumImportFunds);Funds(comparison.LocalTransferFunds);
                    Time(comparison.SupplierTravelSeconds);Time(comparison.ConservativeImportSeconds);Range(comparison.ImportMassTonnes,0,1e15);Range(comparison.ImportVolumeLiters,0,1e18);Text(comparison.UnsupportedReason,512);Text(comparison.Rationale,512);
                    if(comparison.ProductionInvestmentSupported&&!q.ProductionInvestments.Any(i=>i.Recipe.Outputs.Any(o=>o.Resource==comparison.Resource&&o.UnitsPerSecond>0))&&!(comparison.Resource=="Supplies"&&q.FoundingIntent?.Production?.Mode=="compare"))Fail("Production comparison lacks an explicit installed investment/compare intent.");}
                Rows(plan.Buildings,64);Rows(plan.Imports,64);Rows(plan.Claims,ColonyLimits.Resources);Rows(plan.Incoming,128);
                Unique(plan.Buildings.Select(b=>b.Id));Unique(plan.Imports.Select(i=>i.Id));Unique(plan.Claims.Select(c=>c.Resource));Unique(plan.Incoming.Select(i=>i.ShipmentId));
                ValidatePlanningWorkers(state,plan,colony!);
                ValidatePlanningResidents(state,plan,colony!);
                ValidatePlanningStartup(plan,colony!);
                ValidateProductionPlans(state,plan,colony!);
                if(plan.Buildings.Count!=q.Buildings.Count || plan.Imports.Count!=q.Imports.Count) Fail("Plan changed its immutable bill.");
                var allRequirements=ColonyEngine.PlanningMaterialRequirements(q);
                if(plan.Claims.Count!=allRequirements.Count || plan.Claims.Any(c=>!allRequirements.Any(m=>m.Resource==c.Resource))) Fail("Plan material claim owners are incomplete.");
                var requirements=q.Buildings.SelectMany(b=>b.Materials).GroupBy(m=>m.Resource).Select(g=>new MaterialRequirement {Resource=g.Key,Amount=g.Sum(m=>m.Amount)}).ToList();
                if(requirements.Count!=q.Materials.Count || requirements.Any(r=>!q.Materials.Any(m=>m.Resource==r.Resource&&m.Amount==r.Amount))) Fail("Plan aggregate material bill differs from its building lines.");
                foreach(var b in plan.Buildings)
                {
                    var quoted=q.Buildings.SingleOrDefault(x=>x.Id==b.Id);if(quoted==null) Fail("Plan building lacks immutable quote line.");
                    Text(b.Id,128,true);Text(b.TemplateId,128,true);Text(b.TemplateHash,128,true);Text(b.Name,160);Id(b.PlotId);Funds(b.Funds);Time(b.LaborSeconds);Materials(b.Materials);Rows(b.Dependencies,64);Unique(b.Dependencies);
                    if(b.OrderId.Length>0) { Id(b.OrderId);if(!state.Construction.Any(o=>o.Id==b.OrderId && o.ColonyId==plan.ColonyId && o.TemplateId==b.TemplateId && o.TemplateHash==b.TemplateHash && o.PlotId==b.PlotId && o.Funds==b.Funds)) Fail("Plan building child lost its exact quote identity."); }
                    if(b.Dependencies.Any(d=>d==b.Id||!plan.Buildings.Any(x=>x.Id==d))) Fail("Invalid plan dependency.");
                    var comparison=ColonyJson.Deserialize<ColonyPlanningBuilding>(ColonyJson.Serialize(b,65536),65536);comparison.OrderId="";
                    if(Hash(ColonyJson.Serialize(comparison,65536))!=Hash(ColonyJson.Serialize(quoted,65536))) Fail("Plan building terms changed.");
                }
                foreach(var i in plan.Imports)
                {
                    var quoted=q.Imports.SingleOrDefault(x=>x.Id==i.Id);if(quoted==null) Fail("Plan import lacks immutable quote line.");
                    Text(i.Id,128,true);Text(i.SupplierId,128,true);Text(i.SupplierTermsHash,64,true);Text(i.Resource,128,true);Quantity(i.Amount);Funds(i.Funds);Funds(i.FundsPerUnit);Funds(i.FreightFunds);Time(i.TravelSeconds);
                    if(i.Amount==0||i.TravelSeconds<=0||i.Funds!=checked(ColonyEngine.ScaledProduct(i.Amount,i.FundsPerUnit)+i.FreightFunds)) Fail("Invalid plan import price or amount.");
                    Quantity(i.SubstitutedLocallyAmount);if(i.SubstitutedLocallyAmount>i.Amount)Fail("Local substitution exceeds quoted cargo maximum.");
                    if(i.ShipmentId.Length>0) { Id(i.ShipmentId);if(!state.Shipments.Any(s=>s.Id==i.ShipmentId&&s.ColonyId==plan.ColonyId&&s.SupplierId==i.SupplierId&&s.Amount==ColonyEngine.PlanningImportAmount(i)&&s.Funds==ColonyEngine.PlanningImportFunds(i)&&s.TravelSeconds==i.TravelSeconds)) Fail("Plan shipment lost exact saved terms."); }
                    var comparison=ColonyJson.Deserialize<ColonyPlanningImport>(ColonyJson.Serialize(i,65536),65536);comparison.ShipmentId="";comparison.SubstitutedLocallyAmount=0;
                    if(Hash(ColonyJson.Serialize(comparison,65536))!=Hash(ColonyJson.Serialize(quoted,65536))) Fail("Plan import terms changed.");
                }
                if(q.TotalFunds!=checked(q.StagingFunds+q.Buildings.Sum(b=>b.Funds)+q.Imports.Sum(i=>i.Funds)+q.Residents.Sum(r=>r.Fare)+ColonyEngine.ProductionExtraFunds(q))) Fail("Plan itemized bill does not sum to its total.");
                bool active=plan.State!="complete"&&plan.State!="cancelled";
                long future=checked((plan.StagingFundsTransferred?0:q.StagingFunds)+plan.Buildings.Where(b=>b.OrderId.Length==0).Sum(b=>b.Funds)+plan.Imports.Where(i=>i.ShipmentId.Length==0).Sum(ColonyEngine.PlanningImportFunds)+ColonyEngine.PlanningResidentFutureFunds(plan)+ColonyEngine.ProductionFutureFunds(plan));
                if(active && plan.RemainingFunds!=future || !active && plan.RemainingFunds!=0) Fail("Plan future-funding reservation mismatch.");
                foreach(var claim in plan.Claims)
                {
                    Text(claim.Resource,128,true);Quantity(claim.Remaining);Quantity(claim.Reserved);
                    long requirement=plan.Buildings.Where(b=>b.OrderId.Length==0).SelectMany(b=>b.Materials).Where(m=>m.Resource==claim.Resource).Sum(m=>m.Amount);
                    if(!plan.StartupReservesReleased)requirement=checked(requirement+(q.StartupPolicies?.ReserveMaterials.Where(m=>m.Resource==claim.Resource).Sum(m=>m.Amount)??0));
                    requirement=checked(requirement+ColonyEngine.ProductionOperatingRemaining(plan,claim.Resource));
                    if(claim.Reserved>claim.Remaining||active&&claim.Remaining!=requirement||!active&&(claim.Remaining!=0||claim.Reserved!=0)) Fail("Plan material claim changed or exceeds requirement.");
                    if(claim.Reserved>0&&!colony!.Stock.Any(s=>s.Resource==claim.Resource)) Fail("Plan reserved material without owned stock.");
                }
                foreach(var incoming in plan.Incoming)
                {
                    Id(incoming.ShipmentId);Text(incoming.Resource,128,true);Quantity(incoming.Amount);Quantity(incoming.MaterialAmount);
                    var shipment=state.Shipments.SingleOrDefault(s=>s.Id==incoming.ShipmentId && s.ColonyId==plan.ColonyId && s.Kind=="import" && s.Resource==incoming.Resource);
                    if(shipment==null||incoming.Amount==0||incoming.Amount>shipment.Amount||incoming.MaterialAmount>incoming.Amount||incoming.Credited&&shipment.State!="arrived") Fail("Invalid assigned incoming plan cargo.");
                    if(active&&shipment!.State=="arrived"&&!incoming.Credited) Fail("Arrived plan cargo did not atomically transfer its claim.");
                }
                if(active && plan.Buildings.Where(b=>b.OrderId.Length==0).Any(b=>!colony!.Plots.Any(p=>p.Id==b.PlotId&&p.ReservedBy==plan.Id&&p.TemplateId==b.TemplateId&&p.TemplateHash==b.TemplateHash&&p.SurveyHash.Length>0))) Fail("Plan lost its surveyed plot claim.");
            }
            foreach(var supplier in state.Suppliers) if(ColonyEngine.PlanningSupplierReserved(state,supplier.Id)>supplier.Available-supplier.Reserved) Fail("Future colony supplier claims oversubscribe finite stock.");
            foreach(var shipment in state.Shipments) if(ColonyEngine.PlanningIncomingReserved(state,shipment.Id)>shipment.Amount) Fail("Incoming cargo assigned twice.");
            ValidatePlanningWorkerReservations(state);
            ValidatePlanningResidentReservations(state);
            ValidateGrowthProposals(state);
            ValidateProductionEndpointReservations(state);
        }
    }
}
