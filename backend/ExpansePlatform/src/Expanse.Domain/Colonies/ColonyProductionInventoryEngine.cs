using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static string ProductionEndpointSpecHash(ColonyProductionEndpointSpec spec)=>ColonyStateCodec.Hash(ColonyJson.Serialize(new Dictionary<string,object>{
            ["Id"]=spec.Id,["BuildingId"]=spec.BuildingId,["Role"]=spec.Role,["AnchorCraftPartId"]=spec.AnchorCraftPartId,["MemberCraftPartIds"]=spec.MemberCraftPartIds},65536));
        static void QuoteProductionInventory(ColonyPlanningQuote quote,ColonyState state,ColonyEnvironment env,ColonyProductionOperationsIntent? operations)
        {
            if(operations==null||quote.ProductionInvestments.Count==0)return;
            ColonyStateCodec.ValidateProductionOperations(operations);
            if(!operations.RegisterCreatedEndpoints)return;
            var registry=env.Production.Registry;
            bool Pending(ColonyPlan p,ColonyProductionEndpointReceipt r)=>r.State!="applied"&&(p.State!="cancelled"||r.State=="applying"||r.State=="held");
            int pending=state.Plans.SelectMany(p=>p.Production.Where(c=>c.Inventory!=null).SelectMany(c=>c.Inventory!.Endpoints.Where(e=>Pending(p,e)))).Count();
            int pendingMembers=state.Plans.Sum(p=>p.Production.Where(c=>c.Inventory!=null).Sum(c=>p.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Inventory!.Endpoints.Where(e=>c.Inventory!.Endpoints.Any(r=>r.SpecId==e.Id&&Pending(p,r))).Sum(e=>e.MemberCraftPartIds.Count)));
            if(!registry.Ready||registry.MaximumColonyEndpoints!=128||registry.MaximumMembers!=4096)throw new InvalidDataException(registry.Reason);
            int slots=quote.ProductionInvestments.Count*2,members=quote.ProductionInvestments.Count*8;
            if(registry.ColonyEndpoints+pending+slots>128||registry.TotalMembers+pendingMembers+members>4096)throw new InvalidDataException("Reviewed paid-colony inventory endpoints exceed the remaining scoped slots or member capacity.");
            foreach(var item in quote.ProductionInvestments)
            {
                var inventory=new ColonyProductionInventoryQuote{RegistryWorldId=registry.WorldId,RegistryRevision=registry.Revision,RegistryWitness=registry.Witness,
                    AutomaticIntake=operations.AutomaticIntake,AutomaticInputRefill=operations.AutomaticInputRefill};
                foreach(var spec in new[]{new ColonyProductionEndpointSpec{Id=item.Id+":farm",BuildingId=item.BuildingId,Role="cultivation",AnchorCraftPartId=100,MemberCraftPartIds=new List<uint>{100,101,104,106,107,108}},
                    new ColonyProductionEndpointSpec{Id=item.Id+":feed",BuildingId=item.HopperBuildingId,Role="rawFeed",AnchorCraftPartId=106,MemberCraftPartIds=new List<uint>{106,119}}})
                {spec.Hash=ProductionEndpointSpecHash(spec);inventory.Endpoints.Add(spec);}
                inventory.Resources=operations.Resources.Select(r=>new ColonyProductionOperatingResource{Resource=r.Resource,InitialReserve=r.InitialReserve,ReorderEnabled=r.ReorderEnabled,
                    ReorderPoint=r.ReorderPoint,TargetAmount=r.TargetAmount,CadenceSeconds=r.CadenceSeconds,NativeTargetFraction=r.NativeTargetFraction}).OrderBy(r=>r.Resource,StringComparer.Ordinal).ToList();
                inventory.ReserveMaterials=inventory.Resources.Where(r=>r.InitialReserve>0).Select(r=>new MaterialRequirement{Resource=r.Resource,Amount=r.InitialReserve}).ToList();item.Inventory=inventory;
            }
        }
        public static List<MaterialRequirement> ProductionOperatingMaterials(ColonyPlanningQuote quote)=>quote.ProductionInvestments.Where(i=>i.Inventory!=null).SelectMany(i=>i.Inventory!.ReserveMaterials)
            .GroupBy(m=>m.Resource,StringComparer.Ordinal).Select(g=>new MaterialRequirement{Resource=g.Key,Amount=checked(g.Sum(m=>m.Amount))}).OrderBy(m=>m.Resource,StringComparer.Ordinal).ToList();
        public static long ProductionOperatingRemaining(ColonyPlan plan,string resource)=>plan.Production.Where(c=>c.Inventory!=null&&!c.Inventory!.ReservesReleased)
            .Sum(c=>plan.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Inventory!.ReserveMaterials.Where(m=>m.Resource==resource).Sum(m=>m.Amount));
        public static List<ColonyStartupReorder> ProductionOperatingReorders(ColonyPlanningQuote quote)=>quote.ProductionInvestments.Where(i=>i.Inventory!=null).SelectMany(i=>i.Inventory!.Resources).Where(r=>r.TargetAmount>0)
            .GroupBy(r=>r.Resource,StringComparer.Ordinal).Select(g=>new ColonyStartupReorder{Resource=g.Key,Enabled=g.First().ReorderEnabled,ReorderPoint=checked(g.Sum(r=>r.ReorderPoint)),TargetAmount=checked(g.Sum(r=>r.TargetAmount)),CadenceSeconds=g.First().CadenceSeconds}).ToList();
        static void ValidateProductionPolicyCompatibility(ColonyPlanningQuote quote,ColonyState state,ColonyRecord colony,ColonyEnvironment env,ColonyEconomyPolicy? staging)
        {
            foreach(var reorder in ProductionOperatingReorders(quote))
            {
                var startup=quote.StartupPolicies?.Reorders.SingleOrDefault(r=>r.Resource==reorder.Resource);
                if(startup!=null&&!ReorderTermsSame(startup,reorder))throw new InvalidDataException("Startup and production recurring "+reorder.Resource+" instructions conflict; review one consistent enabled/declined policy.");
                var existing=state.ReorderPolicies.SingleOrDefault(r=>r.ColonyId==colony.Id&&r.Resource==reorder.Resource);
                if(existing!=null&&(existing.Enabled!=reorder.Enabled||existing.ReorderPoint!=reorder.ReorderPoint||existing.TargetAmount!=reorder.TargetAmount||existing.CadenceSeconds!=reorder.CadenceSeconds))throw new InvalidDataException("Existing "+reorder.Resource+" reorder differs; production approval will not silently replace it.");
                if(reorder.Enabled&&!state.Suppliers.Concat(staging?.Suppliers??new List<ColonySupplier>()).Any(s=>s.Resource==reorder.Resource&&(s.DestinationBody.Length==0||s.DestinationBody==colony.Site.Body)))throw new InvalidDataException("No finite supplier supports reviewed production "+reorder.Resource+" refill purchases.");
            }
        }
        static bool ReorderTermsSame(ColonyStartupReorder a,ColonyStartupReorder b)=>a.Resource==b.Resource&&a.Enabled==b.Enabled&&a.ReorderPoint==b.ReorderPoint&&a.TargetAmount==b.TargetAmount&&a.CadenceSeconds==b.CadenceSeconds;
        static bool ReleaseProductionOperatingReserves(ColonyState state,ColonyPlan plan,ColonyProductionClaim claim,ColonyProductionInvestment item)
        {
            if(item.Inventory==null||claim.Inventory==null||claim.Inventory.ReservesReleased)return false;
            var colony=Colony(state,plan.ColonyId);
            foreach(var material in item.Inventory.ReserveMaterials)
            {
                var owned=plan.Claims.Single(c=>c.Resource==material.Resource);
                if(owned.Reserved<material.Amount||owned.Remaining<material.Amount)throw new InvalidDataException("Awaiting exact paid operating reserve: "+material.Resource+".");
            }
            foreach(var material in item.Inventory.ReserveMaterials)
            {var owned=plan.Claims.Single(c=>c.Resource==material.Resource);owned.Reserved-=material.Amount;owned.Remaining-=material.Amount;Stock(colony,material.Resource).Reserved-=material.Amount;}
            claim.Inventory.ReservesReleased=true;Log(state,state.SimulatedUt,colony.Id,PlanningChildId(plan.Id,item.Id+":operating-reserves"),"productionOperatingStock","Actual paid buildings commissioned; existing owned operating stock released. No output or stock was created.");return true;
        }
        static bool ApplyProductionOperatingPolicies(ColonyState state,ColonyPlan plan,ColonyEnvironment env)
        {
            var claims=plan.Production.Where(c=>c.Inventory!=null).ToArray();if(claims.Length==0||claims.All(c=>c.Inventory!.PoliciesApplied))return false;
            if(claims.Any(c=>!c.Inventory!.ReservesReleased||c.Inventory.Endpoints.Any(e=>e.State!="applied")))return false;
            foreach(var reorder in ProductionOperatingReorders(plan.Quote))ConfigureReorder(state,PlanningCommand(state,env,plan,"production-reorder-"+reorder.Resource,"configureReorderPolicy",new Dictionary<string,string>{
                ["Resource"]=reorder.Resource,["Enabled"]=reorder.Enabled.ToString(),["ReorderPointMicroUnits"]=reorder.ReorderPoint.ToString(CultureInfo.InvariantCulture),["TargetMicroUnits"]=reorder.TargetAmount.ToString(CultureInfo.InvariantCulture),["CadenceSeconds"]=reorder.CadenceSeconds.ToString("R",CultureInfo.InvariantCulture)}),env);
            foreach(var claim in claims)claim.Inventory!.PoliciesApplied=true;return true;
        }
        public static ColonyState MarkProductionRegistrationApplying(ColonyState prior,string planId,string investmentId,string specId,string facilityId,string vesselId,uint anchor,IReadOnlyList<uint> members,long registryRevision,string beforeWitness)
        {
            var next=ColonyStateCodec.Copy(prior);var plan=next.Plans.Single(p=>p.Id==planId);var claim=plan.Production.Single(c=>c.Id==investmentId);var receipt=claim.Inventory!.Endpoints.Single(e=>e.SpecId==specId);
            if(!PlanningActive(plan)||receipt.State!="prepared")throw new InvalidDataException("Registration is not an unattempted reviewed paid child.");
            var spec=plan.Quote.ProductionInvestments.Single(i=>i.Id==investmentId).Inventory!.Endpoints.Single(e=>e.Id==specId);var building=plan.Buildings.Single(b=>b.Id==spec.BuildingId);var order=next.Construction.SingleOrDefault(o=>o.Id==building.OrderId);
            if(order==null||order.State!="operational"||!order.FundsPaid||!order.MaterialsConsumed||order.FacilityId!=facilityId||!Colony(next,plan.ColonyId).Facilities.Any(f=>f.Id==facilityId&&f.VesselId==vesselId&&f.State=="operational"))throw new InvalidDataException("Only actual paid commissioned construction may create its reviewed endpoint.");
            receipt.EffectId=PlanningChildId(plan.Id,specId+":register");receipt.DepotId=PlanningChildId(plan.Id,specId+":depot");receipt.FacilityId=facilityId;receipt.VesselId=vesselId;receipt.AnchorPartId=anchor;receipt.MemberPartIds=members.OrderBy(id=>id).ToList();receipt.BeforeRegistryRevision=registryRevision;
            receipt.State="applying";receipt.BeforeWitness=beforeWitness;receipt.Reason="Registration attempted boundary; exact readback required before another action.";
            next.Effects.Add(new ColonyEffect{Id=receipt.EffectId,OperationId=receipt.EffectId,ColonyId=plan.ColonyId,TargetId=plan.Id+":"+specId,Kind="productionRegistration",State="applying",BeforeWitness=beforeWitness,Provider="Paid colony inventory registry",Reason=receipt.Reason});return FinishPlanningTransition(next);
        }
        public static ColonyState CompleteProductionRegistration(ColonyState prior,string effectId,string membershipHash,string afterWitness)
        {
            var next=ColonyStateCodec.Copy(prior);var receipt=next.Plans.SelectMany(p=>p.Production.Where(c=>c.Inventory!=null).SelectMany(c=>c.Inventory!.Endpoints)).Single(e=>e.EffectId==effectId);
            if(receipt.State!="applying"&&receipt.State!="held")throw new InvalidDataException("Registration receipt lacks a retained actual attempt.");
            var plan=next.Plans.Single(p=>p.Production.Any(c=>c.Inventory!=null&&c.Inventory.Endpoints.Any(e=>e.EffectId==effectId)));var spec=plan.Quote.ProductionInvestments.SelectMany(i=>i.Inventory?.Endpoints??new List<ColonyProductionEndpointSpec>()).Single(e=>e.Id==receipt.SpecId);
            if(afterWitness!=ProductionRegistrationAfterWitness(spec,receipt,membershipHash))throw new InvalidDataException("Registration after witness does not bind its exact paid spec, members and membership hash.");
            receipt.State="applied";receipt.MembershipHash=membershipHash;receipt.AfterWitness=afterWitness;receipt.Reason="Exact deterministic endpoint, paid ownership and actual member readback confirmed; no stock created.";
            var effect=next.Effects.Single(e=>e.Id==effectId&&e.Kind=="productionRegistration");effect.State="applied";effect.AfterWitness=afterWitness;effect.Reason=receipt.Reason;return FinishPlanningTransition(next);
        }
        public static string ProductionRegistrationAfterWitness(ColonyProductionEndpointSpec spec,ColonyProductionEndpointReceipt receipt,string membershipHash)=>ColonyStateCodec.Hash(System.Text.Encoding.UTF8.GetBytes(spec.Hash+"|"+receipt.DepotId+"|"+receipt.FacilityId+"|"+receipt.VesselId+"|"+receipt.AnchorPartId+"|"+string.Join(",",receipt.MemberPartIds)+"|"+membershipHash));
        public static ColonyState RecordProductionInitialProvenance(ColonyState prior,string effectId,IReadOnlyList<ColonyPhysicalLot> actualInitialLots)
        {
            if(actualInitialLots==null||actualInitialLots.Count>64)throw new InvalidDataException("Paid initial-content provenance exceeds its bounded selected endpoint.");
            var next=ColonyStateCodec.Copy(prior);var plan=next.Plans.Single(p=>p.Production.Any(c=>c.Inventory!=null&&c.Inventory.Endpoints.Any(e=>e.EffectId==effectId)));
            var receipt=plan.Production.Where(c=>c.Inventory!=null).SelectMany(c=>c.Inventory!.Endpoints).Single(e=>e.EffectId==effectId&&e.State=="applied");
            foreach(var lot in actualInitialLots)
            {
                if(lot.ColonyId!=plan.ColonyId||lot.FacilityId!=receipt.FacilityId||!receipt.MemberPartIds.Contains(lot.PartId)||!lot.HadImportedStock||lot.ImportedAttribution<=0||lot.Witness!=receipt.AfterWitness)throw new InvalidDataException("Initial-content provenance is not exact paid-endpoint readback.");
                var existing=next.PhysicalLots.SingleOrDefault(l=>l.PartId==lot.PartId&&l.Resource==lot.Resource);
                if(existing!=null)continue; // Retained provenance cannot be reset by reconciliation.
                if(next.PhysicalLots.Count>=1024)throw new InvalidDataException("Physical provenance receipt capacity reached; no initial provenance may be discarded.");
                next.PhysicalLots.Add(new ColonyPhysicalLot{ColonyId=lot.ColonyId,FacilityId=lot.FacilityId,PartId=lot.PartId,Resource=lot.Resource,HadImportedStock=true,ImportedAttribution=lot.ImportedAttribution,LastPhysicalAmount=lot.LastPhysicalAmount,ProvenanceUncertain=lot.ProvenanceUncertain,Witness=lot.Witness});
            }
            return FinishPlanningTransition(next);
        }
    }
}
