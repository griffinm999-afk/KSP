using System;
using System.Collections.Generic;
using System.Linq;
namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        static IEnumerable<Tuple<ColonyPlan,ColonyProductionClaim,ColonyProductionInventoryQuote>> OperatingProduction(ColonyState state)=>state.Plans.Where(p=>p.State!="cancelled").SelectMany(p=>p.Production
            .Where(c=>c.Inventory?.PoliciesApplied==true&&c.Inventory.Endpoints.All(e=>e.State=="applied")&&c.State!="held"&&c.State!="cancelled")
            .Select(c=>Tuple.Create(p,c,p.Quote.ProductionInvestments.Single(i=>i.Id==c.Id).Inventory!)));
        static ColonyProductionEndpointReceipt? OperatingEndpoint(ColonyProductionClaim c,string role,ColonyProductionInventoryQuote quote)=>c.Inventory!.Endpoints.SingleOrDefault(r=>quote.Endpoints.Single(e=>e.Id==r.SpecId).Role==role);
        public static bool ProductionFuelTargetAllowed(ColonyState state,ColonyServiceTarget target)=>target.SourceResource=="Plutonium-238"&&target.DestinationResource=="Plutonium-238"&&target.Capacity==20*ColonyLimits.Units&&
            OperatingProduction(state).Any(x=>x.Item1.ColonyId==target.ColonyId&&x.Item3.AutomaticInputRefill&&x.Item3.Resources.Any(r=>r.Resource=="Plutonium-238")&&
                OperatingEndpoint(x.Item2,"cultivation",x.Item3) is ColonyProductionEndpointReceipt e&&e.DepotId==target.DepotId&&e.FacilityId==target.FacilityId&&e.MemberPartIds.Contains(target.PartId));
        // One exact registered-tank reservation per call. The existing physical
        // worker owns durable debit/credit, synchronization and actual readback.
        sealed class ProductionTankCandidate
        {internal string Cursor="",Direction="";internal long Amount;internal ColonyLocalStock? Local;internal ColonyServiceTarget? Service;}
        public static ColonyState RunProductionOperatingTransfers(ColonyState prior,ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(prior);ValidateEnvironment(prior,env);
            return RunProductionOperatingTransfersValidated(prior,env);
        }

        static ColonyState RunProductionOperatingTransfersValidated(ColonyState prior,ColonyEnvironment env)
        {
            ValidateEnvironment(prior,env);
            ColonyStateCodec.ValidateLocalStocks(env.Planning.LocalStocks);
            if(prior.SimulatedUt!=env.Ut||prior.Effects.Any(e=>e.State=="held"||e.State=="applying"))return prior;
            var candidates=new List<ProductionTankCandidate>();
            foreach(var owned in OperatingProduction(prior))
            {
                var plan=owned.Item1;var claim=owned.Item2;var terms=owned.Item3;var colony=Colony(prior,plan.ColonyId);
                foreach(var local in env.Planning.LocalStocks.Where(l=>l.ColonyId==plan.ColonyId&&AccessibleLocal(l,colony,env)).OrderBy(l=>l.PartId))
                {
                    var endpoint=claim.Inventory!.Endpoints.SingleOrDefault(e=>e.DepotId==local.DepotId&&e.FacilityId==local.FacilityId&&e.MemberPartIds.Contains(local.PartId)&&e.MembershipHash==local.MembershipHash);if(endpoint==null)continue;
                    string role=terms.Endpoints.Single(e=>e.Id==endpoint.SpecId).Role;
                    bool intake=terms.AutomaticIntake&&!local.NativeInput&&(role=="cultivation"&&local.Resource=="Supplies"||role=="rawFeed"&&(local.Resource=="Water"||local.Resource=="Substrate"));
                    bool fill=terms.AutomaticInputRefill&&role=="cultivation"&&local.NativeInput&&(local.Resource=="Water"||local.Resource=="Substrate"||local.Resource=="Fertilizer"&&terms.Resources.Any(r=>r.Resource=="Fertilizer"));
                    if(!intake&&!fill||prior.PhysicalTransfers.Any(o=>o.PartId==local.PartId&&o.Resource==local.Resource&&(PhysicalPending(o)||o.CompletedUt>env.Ut-5)))continue;
                    var stock=colony.Stock.SingleOrDefault(s=>s.Resource==local.Resource);if(stock==null)continue;
                    double fraction=terms.Resources.SingleOrDefault(r=>r.Resource==local.Resource)?.NativeTargetFraction??.5;
                    long amount=intake?Math.Min(local.Amount-local.PhysicalReserve,stock.Capacity-stock.Amount-stock.IncomingReserved):Math.Min((long)decimal.Floor(local.Capacity*(decimal)fraction)-local.Amount,stock.Amount-stock.Reserved-stock.SupportFloor);
                    amount=Math.Min(1000*ColonyLimits.Units,amount);if(amount<=0||claim.OutputWitness.Length>0&&!MeaningfulAutomaticPhysicalBatch(local,stock,amount,fill))continue;
                    candidates.Add(new ProductionTankCandidate{Cursor=PlanningChildId(prior.WorldId,"production-tank:"+local.PartId+":"+local.Resource+":physical"),Direction=intake?"toColony":"toPhysical",Amount=amount,Local=local});
                }
                if(!terms.AutomaticInputRefill)continue;
                foreach(var resource in terms.Resources.Where(r=>r.Resource=="Machinery"||r.Resource=="Plutonium-238"))
                foreach(var target in env.Services.Targets.Where(t=>t.ColonyId==plan.ColonyId&&t.DestinationResource==resource.Resource&&t.Current&&t.CanApply&&t.QualifiedWorker&&t.ContextKey==env.ContextKey&&t.ObservedUt==env.Ut))
                {
                    var endpoint=OperatingEndpoint(claim,"cultivation",terms);if(endpoint==null||endpoint.DepotId!=target.DepotId||endpoint.FacilityId!=target.FacilityId||!endpoint.MemberPartIds.Contains(target.PartId))continue;
                    if(target.DestinationResource=="Plutonium-238"&&!ProductionFuelTargetAllowed(prior,target)||!colony.Facilities.Any(f=>f.Id==target.FacilityId&&f.State!="retired"&&f.PartIds.Contains(target.PartId)))continue;
                    if(prior.ServiceOperations.Any(o=>o.PartId==target.PartId&&o.DestinationResource==target.DestinationResource&&(o.State!="complete"&&o.State!="cancelled"||o.CompletedUt>env.Ut-resource.CadenceSeconds)))continue;
                    var stock=colony.Stock.SingleOrDefault(s=>s.Resource==resource.Resource);if(stock==null)continue;
                    long amount=Math.Min((long)decimal.Floor(target.Capacity*(decimal)resource.NativeTargetFraction)-target.Amount,stock.Amount-stock.Reserved-stock.SupportFloor);if(amount<=0)continue;
                    candidates.Add(new ProductionTankCandidate{Cursor=PlanningChildId(prior.WorldId,"production-tank:"+target.PartId+":"+target.DestinationResource+":service"),Amount=amount,Service=target});
                }
            }
            // The most recent durable scheduling journal entry is a bounded
            // round-robin cursor shared by physical and service work. It survives
            // terminal transfer compaction without adding a persisted DTO. Stable
            // cursor IDs also handle the previous tank becoming ineligible.
            string cursor=prior.Journal.LastOrDefault(j=>j.Kind=="productionTankScheduled")?.OperationId??"";
            foreach(var candidate in candidates.OrderBy(c=>string.CompareOrdinal(c.Cursor,cursor)>0?0:1).ThenBy(c=>c.Cursor,StringComparer.Ordinal))
            {
                ColonyState next;string colonyId,resource;
                if(candidate.Local is ColonyLocalStock local)
                {
                    var q=QuotePhysicalTransfer(prior,local.ColonyId,local.Id,candidate.Direction,candidate.Amount,env);if(!q.CanApprove)continue;
                    next=ColonyStateCodec.Copy(prior);if(!MakeProductionTransferRoom(next))continue;ReservePhysicalTransfer(next,Guid.NewGuid().ToString("D"),q,env,"");colonyId=local.ColonyId;resource=local.Resource;
                }
                else
                {
                    var target=candidate.Service!;if(prior.ServiceOperations.Count>=256&&!prior.ServiceOperations.Any(o=>o.State=="complete"||o.State=="cancelled"))continue;
                    next=ColonyStateCodec.Copy(prior);if(!CompactTerminalEffects(next,1))continue;ReserveService(next,Guid.NewGuid().ToString("D"),target,candidate.Amount,env);colonyId=target.ColonyId;resource=target.DestinationResource;
                }
                Log(next,env.Ut,colonyId,candidate.Cursor,"productionTankScheduled","Reviewed "+resource+" inventory/service work reserved; exact provider receipt required.");return FinishPlanningTransition(next);
            }
            return prior;
        }
        static bool MakeProductionTransferRoom(ColonyState state)
        {
            // Provenance remains in PhysicalLots and journal; pending holds and
            // material claims are never evicted to make an automatic slot.
            if(!CompactTerminalEffects(state,1))return false;
            while(state.PhysicalTransfers.Count>=256)
            {int index=state.PhysicalTransfers.FindIndex(o=>o.PlanId.Length==0&&o.PlannedMaterialAmount==0&&(o.State=="complete"||o.State=="cancelled")&&
                !state.Effects.Any(e=>e.Kind=="physicalTransfer"&&e.TargetId==o.Id&&e.State!="applied"&&e.State!="cancelled"));if(index<0)return false;state.PhysicalTransfers.RemoveAt(index);}return true;
        }
    }
}
