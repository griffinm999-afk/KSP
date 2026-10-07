using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        static void MigratePhysicalProcurement(ColonyState state)
        {
            if(state.PhysicalTransfers==null)state.PhysicalTransfers=new List<ColonyPhysicalTransfer>();
            if(state.PhysicalLots==null)state.PhysicalLots=new List<ColonyPhysicalLot>();
            if(state.PhysicalPolicies==null)state.PhysicalPolicies=new List<ColonyPhysicalPolicy>();
        }
        public static void ValidateLocalStocks(List<ColonyLocalStock> stocks)
        {
            Rows(stocks,1024);Unique(stocks.Select(s=>s.Id));
            foreach(var s in stocks)
            {
                Text(s.Id,128,true);Id(s.ColonyId);Id(s.FacilityId);Id(s.VesselId);if(s.PartId==0)Fail("Physical stock lacks an actual persistent part identity.");
                Text(s.PartName,160);Text(s.DepotId,128);Text(s.Resource,128,true);Quantity(s.Amount);Quantity(s.Capacity);Quantity(s.PhysicalReserve);
                Text(s.Provider,128);Text(s.ProviderVersion,128);Text(s.MembershipHash,128);Text(s.AccessWitness,128);Text(s.ContextKey,256,true);Time(s.ObservedUt);Text(s.Reason,512);
                Choice(s.StockKind,"warehouse","maintenanceReserve");Text(s.WorkerWitness,512);
                if(s.CanApply&&s.StockKind=="maintenanceReserve"&&(!s.QualifiedWorker||s.WorkerWitness.Length==0))Fail("Maintenance reserve lacks actual qualified repair-worker evidence.");
                if(s.Amount>s.Capacity||s.PhysicalReserve>s.Capacity)Fail("Physical stock observation violates capacity.");
                if(s.CanApply&&(!s.Current||!s.WarehouseEnabled||!s.FlowAllowed||!s.WithinRange||s.DepotId.Length==0||s.Provider.Length==0||s.MembershipHash.Length==0||s.AccessWitness.Length==0))Fail("Applicable physical stock lacks qualified access evidence.");
            }
        }
        static void ValidatePhysicalProcurement(ColonyState state)
        {
            Rows(state.PhysicalTransfers,256);Rows(state.PhysicalLots,1024);Rows(state.PhysicalPolicies,ColonyLimits.Colonies);Unique(state.PhysicalTransfers.Select(o=>o.Id));Unique(state.PhysicalLots.Select(l=>l.PartId+":"+l.Resource));Unique(state.PhysicalPolicies.Select(p=>p.ColonyId));
            if(state.PhysicalTransfers.Where(ColonyEngine.PhysicalPending).GroupBy(o=>o.PartId+":"+o.Resource).Any(g=>g.Count()>1))Fail("One physical tank has multiple colony claims.");
            foreach(var p in state.PhysicalPolicies)
            {Id(p.ColonyId);if(!state.Colonies.Any(c=>c.Id==p.ColonyId))Fail("Physical policy lacks colony.");Time(p.NextReviewUt);Range(p.CadenceSeconds,5,ColonyLimits.KerbinDay);Quantity(p.MaximumTransfer);if(p.MaximumTransfer==0||p.MaximumTransfer>1000*ColonyLimits.Units)Fail("Physical batch exceeds bounded transfer model.");Range(p.NativeInputTargetFraction,0,.5);Text(p.Reason,512);}
            foreach(var lot in state.PhysicalLots)
            {
                Id(lot.ColonyId);Id(lot.FacilityId);if(lot.PartId==0)Fail("Physical lot lacks persistent part.");Text(lot.Resource,128,true);Quantity(lot.ImportedAttribution);Range(lot.LastPhysicalAmount,0,ColonyLimits.MaxQuantity/(double)ColonyLimits.Units);Text(lot.Witness,4096);
                if(!state.Colonies.Any(c=>c.Id==lot.ColonyId&&c.Facilities.Any(f=>f.Id==lot.FacilityId&&f.PartIds.Contains(lot.PartId))))Fail("Physical provenance lost its adopted facility owner.");
                if(lot.ImportedAttribution>0&&!lot.HadImportedStock)Fail("Physical imported origin marker was erased.");
                var delivered=state.PhysicalTransfers.Where(o=>o.PartId==lot.PartId&&o.Resource==lot.Resource&&o.Direction=="toPhysical"&&o.State=="complete"&&o.ImportedAttribution>0);
                if(delivered.Any()&&!lot.HadImportedStock)Fail("Completed imported delivery lost its persistent origin marker.");
                // Imported origin may exceed current observed amount after native
                // consumption. It is retained as a conservative provenance debt.
            }
            foreach(var op in state.PhysicalTransfers)
            {
                Id(op.Id);Id(op.ColonyId);Id(op.FacilityId);Text(op.LocalStockId,128,true);Text(op.DepotId,128,true);Text(op.Resource,128,true);if(op.PartId==0)Fail("Transfer lacks persistent part.");
                if(op.PlanId.Length>0)Id(op.PlanId);Choice(op.State,"reserved","held","complete","cancelled");Choice(op.Direction,"toColony","toPhysical");Quantity(op.Amount);Quantity(op.ImportedAttribution);Quantity(op.PlannedMaterialAmount);Time(op.CreatedUt);Time(op.CompletedUt);
                Range(op.PhysicalBefore,0,ColonyLimits.MaxQuantity/(double)ColonyLimits.Units);Range(op.PhysicalAfter,0,ColonyLimits.MaxQuantity/(double)ColonyLimits.Units);Text(op.Provider,128,true);Text(op.MembershipHash,128,true);Text(op.AccessWitness,128,true);Text(op.BeforeWitness,4096);Text(op.AfterWitness,4096);Text(op.Reason,512);Text(op.Provenance,512);
                var colony=state.Colonies.SingleOrDefault(c=>c.Id==op.ColonyId);if(colony==null||!colony.Stock.Any(s=>s.Resource==op.Resource)||!colony.Facilities.Any(f=>f.Id==op.FacilityId&&f.PartIds.Contains(op.PartId)))Fail("Physical transfer lacks adopted tank and owned stock.");
                if(op.Amount==0||op.ImportedAttribution>op.Amount||op.PlannedMaterialAmount>op.Amount||op.Direction=="toPhysical"&&op.PlannedMaterialAmount>0)Fail("Physical transfer manifest is invalid.");
                bool attempted=op.State=="held"||op.State=="complete";
                if(op.OwnedSourceDebited!=(attempted&&op.Direction=="toPhysical"))Fail("Physical transfer source escrow state disagrees with its attempt.");
                if(attempted&&(op.BeforeWitness.Length==0||op.AfterWitness.Length==0||op.PhysicalAfter!=(op.Direction=="toColony"?op.PhysicalBefore-op.Amount/(double)ColonyLimits.Units:op.PhysicalBefore+op.Amount/(double)ColonyLimits.Units)))Fail("Physical attempt lost exact before/after witness.");
                if(op.State=="complete"&&op.CompletedUt<op.CreatedUt)Fail("Physical transfer completion predates reservation.");
                var effect=state.Effects.SingleOrDefault(e=>e.Kind=="physicalTransfer"&&e.TargetId==op.Id);
                if(ColonyEngine.PhysicalPending(op)&&(effect==null||effect.OperationId!=op.Id||effect.ColonyId!=op.ColonyId||effect.FundsDelta!=0||effect.State!=(op.State=="held"?"held":"prepared")))Fail("Pending physical transfer lost durable external effect.");
                if(ColonyEngine.PhysicalPending(op)&&state.ServiceOperations.Any(s=>(s.State=="reserved"||s.State=="held")&&s.PartId==op.PartId&&s.DestinationResource==op.Resource))Fail("Physical and maintenance claims overlap one tank.");
                if(op.PlanId.Length>0&&!state.Plans.Any(p=>p.Id==op.PlanId&&p.ColonyId==op.ColonyId))Fail("Physical procurement lost its approved plan lineage.");
            }
            foreach(var plan in state.Plans)
            {
                foreach(var resource in plan.Imports.Select(i=>i.Resource).Distinct(StringComparer.Ordinal))
                {
                    long substituted=plan.Imports.Where(i=>i.Resource==resource).Sum(i=>i.SubstitutedLocallyAmount);
                    long witnessed=state.PhysicalTransfers.Where(o=>o.PlanId==plan.Id&&o.Resource==resource&&o.Direction=="toColony"&&o.State=="complete").Sum(o=>o.Amount);
                    if(substituted>witnessed)Fail("Plan replaced purchased cargo without exact completed physical transfer proof.");
                }
                if(plan.State=="complete"||plan.State=="cancelled")continue;
                foreach(var claim in plan.Claims)
                {
                    long promised=state.PhysicalTransfers.Where(o=>o.PlanId==plan.Id&&o.Resource==claim.Resource&&ColonyEngine.PhysicalPending(o)).Sum(o=>o.PlannedMaterialAmount)+plan.Incoming.Where(i=>i.Resource==claim.Resource&&!i.Credited).Sum(i=>i.MaterialAmount);
                    if(claim.Reserved+promised>claim.Remaining)Fail("Physical and imported plan materials were promised twice.");
                }
            }
        }
    }
}
