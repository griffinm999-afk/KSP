using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public static class ColonyPhysicalStockPresentation
{
    public static ColonyManagementPresentation Present(ColonyManagementPresentation view,ColonyManagementSnapshot snapshot,IEnumerable<ColonyManagementAction> actions)
    {
        var colony=snapshot.State?.Colonies.SingleOrDefault(c=>c.Id==view.ColonyId);
        if(colony is null)return view;
        var stocks=snapshot.Planning.LocalStocks.Where(s=>s.ColonyId==colony.Id).ToArray();
        string Units(long value)=>(value/(decimal)ColonyLimits.Units).ToString("0.######",CultureInfo.CurrentCulture);
        string Name(ColonyLocalStock stock)=>(colony.Facilities.FirstOrDefault(f=>f.Id==stock.FacilityId)?.Name ?? "Facility")+" · "+stock.PartName+" · "+stock.Resource;
        var rows=stocks.Select(s=>new ColonyManagementRow("local-stock:"+s.Id,Name(s),s.Current && s.CanApply ? "Physical transfer available" : "Physical access held",Units(s.Amount)+" / "+Units(s.Capacity),s.Provider,
            s.Reason+"; physical reserve "+Units(s.PhysicalReserve)+"; enabled warehouse "+s.WarehouseEnabled+"; resource flow "+s.FlowAllowed+"; range "+s.WithinRange+". Transfer needs a reviewed exact amount and conserved before/after witness. Physical tanks and colony-owned stock are separate inventories."));
        var operations=snapshot.State!.PhysicalTransfers.Where(p=>p.ColonyId==colony.Id).Select(p=>new ColonyManagementRow("physical-transfer:"+p.Id,"Stock transfer · "+p.Resource,p.State,Units(p.Amount),p.Provider,p.Reason+"; direction "+p.Direction+"; before "+p.BeforeWitness+"; after "+p.AfterWitness));
        var policy=snapshot.State.PhysicalPolicies.SingleOrDefault(p=>p.ColonyId==colony.Id);
        var policyRow=new ColonyManagementRow("physical-policy","Local procurement policy",policy is null ? "Default local shortage review" : policy.Enabled ? "Enabled" : "Disabled","Conserved transfers",snapshot.Status,"Startup/reorder shortages may source actual accessible local tanks before paid imports. Native input filling needs explicit enablement and is limited to 50% capacity; protected service resources retain their qualified-worker path.");
        return view with {Sections=view.Sections.Select(s=>s.Key!="inventory" ? s : s with {
            Rows=rows.Concat(operations).Concat(new[]{policyRow}).Concat(s.Rows).ToArray(),
            Actions=s.Actions.Concat(actions.Where(a=>a.Kind is "reviewPhysicalTransfer" or "transferColonyStock" or "configurePhysicalProcurement" or "cancelPhysicalTransfer")).ToArray(),
            Fields=(s.Fields ?? []).Concat(new ColonyManagementField[]{
                new("LocalStockId","Registered physical tank","","Select the actual warehouse/provider witness; service-only resources use Maintenance.",stocks.Select(t=>new ColonyManagementChoice(t.Id,Name(t)+" · "+Units(t.Amount)+" available")).ToArray()),
                new("Direction","Transfer direction","toColony","Moves the exact same resource quantity between physical tanks and owned stock.",new[]{new ColonyManagementChoice("toColony","Physical tank → colony reserves"),new ColonyManagementChoice("toPhysical","Colony reserves → physical tank")}),
                new("TransferAmount","Transfer amount (resource units)","","At most six decimal places; protects physical/colony reserves and reservations."),
                new("PhysicalPolicyEnabled","Automatic local procurement and native input fill","false","Native input fill requires explicit enablement; every operation rechecks current warehouse, flow, range, owned stock and capacity.",new[]{new ColonyManagementChoice("false","Disabled"),new ColonyManagementChoice("true","Enabled")})}).ToArray()
        }).ToArray()};
    }
}
