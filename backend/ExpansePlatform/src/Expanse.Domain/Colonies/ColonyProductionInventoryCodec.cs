using System;
using System.Collections.Generic;
using System.Linq;
namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        public static void ValidateProductionRegistry(ColonyProductionRegistryAuthority r)
        {
            if(r==null)Fail("Missing scoped registry authority.");Text(r!.WorldId,36);Text(r.Witness,64);Text(r.Reason,512);Range(r.Revision,0,long.MaxValue);Count(r.ColonyEndpoints,128);Count(r.TotalMembers,4096);
            if(r.MaximumColonyEndpoints!=128||r.MaximumMembers!=4096)Fail("Unsupported scoped registry limits.");if(r.Ready){Id(r.WorldId);ProductionInventoryHash(r.Witness);}
        }
        public static void ValidateProductionOperations(ColonyProductionOperationsIntent operations)
        {
            if(operations==null)Fail("Missing production operations intent.");Rows(operations!.Resources,3);Unique(operations.Resources.Select(r=>r.Resource));
            if(!operations.RegisterCreatedEndpoints&&(operations.AutomaticIntake||operations.AutomaticInputRefill||operations.Resources.Any(r=>r.InitialReserve>0||r.ReorderEnabled)))Fail("Automatic production stock operations require explicitly approved paid-created endpoint registration.");
            foreach(var r in operations.Resources)
            {
                Choice(r.Resource,"Fertilizer","Machinery","Plutonium-238");Quantity(r.InitialReserve);Quantity(r.ReorderPoint);Quantity(r.TargetAmount);Range(r.CadenceSeconds,21600,30*ColonyLimits.KerbinDay);Range(r.NativeTargetFraction,.01,1);
                if(r.ReorderPoint>r.TargetAmount||r.ReorderEnabled&&r.TargetAmount==0)Fail("Production reorder target must cover its point and be positive when enabled.");
                if(r.Resource=="Fertilizer"&&r.NativeTargetFraction>.5)Fail("Warehouse native input fill is bounded to 50% capacity.");
                if(!operations.AutomaticInputRefill&&r.ReorderEnabled)Fail("A production recurring refill purchase requires explicit automatic input refill consent.");
            }
        }
        static void ValidateProductionInventory(ColonyState state,ColonyPlan plan,ColonyProductionInvestment item,ColonyProductionClaim claim,ColonyProductionOperationsIntent? operations)
        {
            if(item.Inventory==null){if(claim.Inventory!=null)Fail("Physical inventory progress has no reviewed authority.");if(operations?.RegisterCreatedEndpoints==true)Fail("Reviewed registration intent lost endpoint specs.");return;}
            if(operations==null||!operations.RegisterCreatedEndpoints||claim.Inventory==null)Fail("Paid-created registry mutation lacks explicit immutable consent.");
            var q=item.Inventory!;var c=claim.Inventory!;Id(q.RegistryWorldId);Range(q.RegistryRevision,0,long.MaxValue);ProductionInventoryHash(q.RegistryWitness);
            if(q.AutomaticIntake!=operations!.AutomaticIntake||q.AutomaticInputRefill!=operations.AutomaticInputRefill)Fail("Production operating consent changed.");
            ValidateProductionOperations(new ColonyProductionOperationsIntent{RegisterCreatedEndpoints=true,AutomaticIntake=q.AutomaticIntake,AutomaticInputRefill=q.AutomaticInputRefill,Resources=q.Resources});
            var normalized=operations.Resources.OrderBy(r=>r.Resource,StringComparer.Ordinal).ToList();
            if(!ColonyJson.Serialize(q.Resources,65536).SequenceEqual(ColonyJson.Serialize(normalized,65536)))Fail("Operating resource terms differ from reviewed intent.");
            Materials(q.ReserveMaterials);var expected=q.Resources.Where(r=>r.InitialReserve>0).Select(r=>new MaterialRequirement{Resource=r.Resource,Amount=r.InitialReserve}).ToList();
            if(!ColonyJson.Serialize(expected,65536).SequenceEqual(ColonyJson.Serialize(q.ReserveMaterials,65536)))Fail("Initial production buffers differ from reviewed quantities.");
            Rows(q.Endpoints,2);Rows(c.Endpoints,2);Unique(q.Endpoints.Select(e=>e.Id));Unique(c.Endpoints.Select(e=>e.SpecId));if(q.Endpoints.Count!=2||c.Endpoints.Count!=2)Fail("Each production package requires exactly its two reviewed endpoints.");
            foreach(var spec in q.Endpoints)
            {
                bool farm=spec.Role=="cultivation";Choice(spec.Role,"cultivation","rawFeed");Text(spec.Id,128,true);Text(spec.BuildingId,128,true);Text(spec.Hash,64,true);Rows(spec.MemberCraftPartIds,64);
                var required=farm?new uint[]{100,101,104,106,107,108}:new uint[]{106,119};
                if(spec.Id!=item.Id+(farm?":farm":":feed")||spec.BuildingId!=(farm?item.BuildingId:item.HopperBuildingId)||spec.AnchorCraftPartId!=(farm?100u:106u)||!spec.MemberCraftPartIds.SequenceEqual(required)||spec.Hash!=ColonyEngine.ProductionEndpointSpecHash(spec))Fail("Selected endpoint differs from exact paid craft resource mapping.");
                var receipt=c.Endpoints.SingleOrDefault(e=>e.SpecId==spec.Id);if(receipt==null)Fail("Missing exact endpoint receipt owner.");
                Choice(receipt!.State,"prepared","applying","applied","held");Text(receipt.Reason,512);Text(receipt.BeforeWitness,128);Text(receipt.AfterWitness,128);Text(receipt.MembershipHash,64);Range(receipt.BeforeRegistryRevision,0,long.MaxValue);Rows(receipt.MemberPartIds,64);
                if(receipt.State=="prepared")
                {if(receipt.EffectId.Length>0||receipt.DepotId.Length>0||receipt.FacilityId.Length>0||receipt.VesselId.Length>0||receipt.AnchorPartId!=0||receipt.MemberPartIds.Count>0||receipt.BeforeWitness.Length>0||receipt.AfterWitness.Length>0||receipt.MembershipHash.Length>0)Fail("Unattempted registration has external identity or receipt.");continue;}
                Id(receipt.EffectId);Id(receipt.DepotId);Id(receipt.FacilityId);Id(receipt.VesselId);
                var building=plan.Buildings.Single(b=>b.Id==spec.BuildingId);var order=state.Construction.SingleOrDefault(o=>o.Id==building.OrderId);var colony=state.Colonies.Single(co=>co.Id==plan.ColonyId);var facility=colony.Facilities.SingleOrDefault(f=>f.Id==receipt.FacilityId);
                if(receipt.EffectId!=ColonyEngine.PlanningChildId(plan.Id,spec.Id+":register")||receipt.DepotId!=ColonyEngine.PlanningChildId(plan.Id,spec.Id+":depot")||order==null||order.FacilityId!=receipt.FacilityId||facility==null||facility.VesselId!=receipt.VesselId||receipt.MemberPartIds.Count!=required.Length||receipt.MemberPartIds.Any(id=>id==0||!facility.PartIds.Contains(id))||receipt.MemberPartIds.Distinct().Count()!=required.Length||!receipt.MemberPartIds.Contains(receipt.AnchorPartId))Fail("Registration lost exact deterministic paid construction/member lineage.");
                var effect=state.Effects.SingleOrDefault(e=>e.Id==receipt.EffectId&&e.OperationId==receipt.EffectId&&e.Kind=="productionRegistration"&&e.ColonyId==plan.ColonyId&&e.TargetId==plan.Id+":"+spec.Id);
                if(effect==null||effect.State!=receipt.State||effect.BeforeWitness!=receipt.BeforeWitness||receipt.BeforeWitness.Length==0||receipt.State=="applied"&&(receipt.AfterWitness.Length==0||effect.AfterWitness!=receipt.AfterWitness||receipt.MembershipHash.Length!=64))Fail("Registration lost durable exact before/after receipt.");
                if(receipt.State=="applied")ProductionInventoryHash(receipt.MembershipHash);
                if(receipt.State=="applied"&&receipt.AfterWitness!=ColonyEngine.ProductionRegistrationAfterWitness(spec,receipt,receipt.MembershipHash))Fail("Registration receipt no longer binds exact spec, members and registry membership.");
            }
            if(c.PoliciesApplied&&(!c.ReservesReleased||c.Endpoints.Any(e=>e.State!="applied")))Fail("Production operating policies precede actual registration/reserve release.");
            if(c.ReservesReleased&&new[]{item.BuildingId,item.HopperBuildingId}.Any(id=>plan.Buildings.Single(b=>b.Id==id).OrderId.Length==0))Fail("Operating reserves released before actual construction commitment.");
            if(new[]{"ready","configuring","observing","operational"}.Contains(claim.State)&&c.Endpoints.Any(e=>e.State!="applied"))Fail("Native production started before authorized registrations completed.");
            if(plan.State=="complete"&&(!c.ReservesReleased||!c.PoliciesApplied))Fail("Founding completed without approved production operating terms.");
        }
        static void ProductionInventoryHash(string value){Text(value,64,true);if(value.Length!=64||!value.All(Uri.IsHexDigit))Fail("Scoped inventory authority requires an exact SHA-256 witness.");}
        static void ValidateProductionEndpointReservations(ColonyState state)
        {
            var receipts=state.Plans.SelectMany(p=>p.Production.Where(c=>c.Inventory!=null).SelectMany(c=>c.Inventory!.Endpoints)).Where(r=>r.State!="prepared").ToArray();
            Unique(receipts.Select(r=>r.DepotId));Unique(receipts.Select(r=>r.EffectId));
            if(receipts.SelectMany(r=>r.MemberPartIds).GroupBy(id=>id).Any(g=>g.Count()>1))Fail("Paid endpoint receipts overlap actual physical member ownership.");
        }
    }
}
