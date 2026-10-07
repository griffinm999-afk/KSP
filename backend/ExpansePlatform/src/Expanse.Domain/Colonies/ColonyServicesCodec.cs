using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        static void MigrateServices(ColonyState state)
        {
            if(state==null)return;
            if(state.ServiceOperations==null)state.ServiceOperations=new List<ColonyServiceOperation>();
            if(state.ServicePolicies==null)state.ServicePolicies=new List<ColonyServicePolicy>();
        }
        static long ServiceReserved(ColonyState state,string colonyId,string resource) => state.ServiceOperations.Where(x=>x.ColonyId==colonyId && x.SourceResource==resource && x.State=="reserved" && !x.SourceDebited).Sum(x=>x.Amount);
        static void ValidateServices(ColonyState state)
        {
            Rows(state.ServiceOperations,256);Rows(state.ServicePolicies,ColonyLimits.Colonies);Unique(state.ServiceOperations.Select(x=>x.Id));Unique(state.ServicePolicies.Select(x=>x.ColonyId));
            foreach(var policy in state.ServicePolicies)
            {
                Id(policy.ColonyId);if(!state.Colonies.Any(x=>x.Id==policy.ColonyId))Fail("Service policy lacks a colony.");Range(policy.CadenceSeconds,21600,365*ColonyLimits.KerbinDay);Range(policy.TargetFillFraction,.01,1);
            }
            foreach(var op in state.ServiceOperations)
            {
                Id(op.Id);Id(op.ColonyId);Id(op.FacilityId);Text(op.DepotId,128,true);Text(op.SourceResource,128,true);Text(op.DestinationResource,128,true);Quantity(op.Amount);if(op.Amount==0 || op.PartId==0)Fail("Empty service transfer.");
                Choice(op.State,"reserved","held","complete","cancelled");Time(op.CreatedUt);Time(op.CompletedUt);Text(op.Provider,128);Text(op.BeforeWitness,4096);Text(op.AfterWitness,4096);Text(op.Reason,512);
                var colony=state.Colonies.SingleOrDefault(x=>x.Id==op.ColonyId);if(colony==null || !colony.Facilities.Any(x=>x.Id==op.FacilityId && x.PartIds.Contains(op.PartId)) || !colony.Stock.Any(x=>x.Resource==op.SourceResource))Fail("Service lacks actual member part or owned source store.");
                if((op.State=="held" || op.State=="complete")!=op.SourceDebited)Fail("Service source debit provenance is inconsistent.");
                if(op.State=="held" && (op.BeforeWitness.Length==0 || !state.Effects.Any(x=>x.TargetId==op.Id && x.Kind=="serviceTransfer" && x.State=="held")))Fail("Held service lacks durable exact before-witness effect.");
                if(op.State=="reserved" && state.Effects.Count(x=>x.TargetId==op.Id && x.Kind=="serviceTransfer" && x.State=="prepared")!=1)Fail("Service reservation lacks exactly one unstarted physical effect.");
                if(op.State=="complete" && (op.BeforeWitness.Length==0 || op.AfterWitness.Length==0 || op.CompletedUt<op.CreatedUt))Fail("Completed service lacks exact physical readback.");
            }
            var active=state.ServiceOperations.Where(x=>x.State=="reserved" || x.State=="held");
            if(active.GroupBy(x=>x.FacilityId+":"+x.PartId+":"+x.DestinationResource,StringComparer.Ordinal).Any(x=>x.Count()>1))Fail("Installed service tank reserved twice.");
        }
    }
}
