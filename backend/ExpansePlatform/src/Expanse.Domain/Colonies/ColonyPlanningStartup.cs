using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    // Zero material quantities explicitly decline that initial service buffer.
    // Only Supplies levels have a documented population/cadence derivation.
    public sealed class ColonyStartupIntent
    {
        public bool SuppliesReorderEnabled { get; set; }
        public long SuppliesReorderPointMicroUnits { get; set; }
        public long SuppliesTargetMicroUnits { get; set; }
        public double ReorderCadenceSeconds { get; set; } = 21600;
        public bool ServiceEnabled { get; set; }
        public double ServiceTargetFillFraction { get; set; } = .95;
        public double ServiceCadenceSeconds { get; set; } = 21600;
        public long MachineryReserveMicroUnits { get; set; }
        public long MaterialKitsReserveMicroUnits { get; set; }
        public long EnrichedUraniumReserveMicroUnits { get; set; }
        public bool LocalProcurementEnabled { get; set; }
    }
    public sealed class ColonyStartupPolicies
    {
        public bool SuppliesReorderEnabled { get; set; }
        public long SuppliesReorderPointMicroUnits { get; set; }
        public long SuppliesTargetMicroUnits { get; set; }
        public double ReorderCadenceSeconds { get; set; }
        public bool ServiceEnabled { get; set; }
        public double ServiceTargetFillFraction { get; set; }
        public double ServiceCadenceSeconds { get; set; }
        public bool LocalProcurementEnabled { get; set; }
        public double LocalProcurementCadenceSeconds { get; set; } = 5;
        public long LocalProcurementMaximumMicroUnits { get; set; } = 1000 * ColonyLimits.Units;
        public double NativeInputBufferFraction { get; set; } = .5;
        public int SupportedPeople { get; set; }
        public List<MaterialRequirement> StockTargets { get; set; } = new List<MaterialRequirement>();
        // Extra owned material above the separately protected support floor.
        public List<MaterialRequirement> ReserveMaterials { get; set; } = new List<MaterialRequirement>();
        public List<ColonyStartupReorder> Reorders { get; set; } = new List<ColonyStartupReorder>();
    }
    public sealed class ColonyStartupReorder
    {
        public string Resource { get; set; } = "";
        public bool Enabled { get; set; }
        public long ReorderPoint { get; set; }
        public long TargetAmount { get; set; }
        public double CadenceSeconds { get; set; }
    }

    public static partial class ColonyEngine
    {
        static int PlanningStartupPopulation(ColonyPlanningQuote q, ColonyRecord colony, ColonyEnvironment env)
        {
            if (!env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id, out var present))
                throw new InvalidDataException("A complete actual site population is required to quote startup reserves.");
            return Math.Max(q.TargetPopulation, present.Concat(colony.Residents.Where(r=>r.Status!="missing").Select(r=>r.RosterId))
                .Concat(q.Residents.Where(r=>r.Kind=="recruit").Select(r=>r.RosterId)).Distinct(StringComparer.Ordinal).Count());
        }
        static void QuoteStartupPolicies(ColonyPlanningQuote q, ColonyState state, ColonyRecord colony, ColonyEnvironment env)
        {
            var intent=q.FoundingIntent?.Startup;
            if(intent==null)return;
            ColonyStateCodec.ValidateStartupPolicyIntent(intent);
            int people=PlanningStartupPopulation(q,colony,env);
            long cadenceConsumption=checked((long)decimal.Ceiling((decimal)people*env.Support.MicroUnitsPerPersonDay*(decimal)intent.ReorderCadenceSeconds/(decimal)ColonyLimits.KerbinDay));
            long suppliesFloor=Math.Max(q.StartupSupportReserve,colony.Stock.SingleOrDefault(s=>s.Resource=="Supplies")?.SupportFloor??0);
            var suppliers=state.Suppliers.Where(s=>s.Resource=="Supplies"&&(s.DestinationBody.Length==0||s.DestinationBody==colony.Site.Body))
                .Concat(colony.Logistics.State=="none"?env.EconomyPolicies.Where(p=>p.Body==colony.Site.Body).SelectMany(p=>p.Suppliers).Where(s=>s.Resource=="Supplies"&&(s.DestinationBody.Length==0||s.DestinationBody==colony.Site.Body)):Enumerable.Empty<ColonySupplier>());
            double lead=suppliers.Select(s=>s.TravelSeconds).DefaultIfEmpty(0).Max()*env.Planning.ImportDelayFactor;
            long leadBurn=checked((long)decimal.Ceiling((decimal)people*env.Support.MicroUnitsPerPersonDay*(decimal)lead/(decimal)ColonyLimits.KerbinDay));
            long minimumPoint=checked(suppliesFloor+(intent.SuppliesReorderEnabled?leadBurn:0));
            long point=intent.SuppliesReorderPointMicroUnits==0?minimumPoint:intent.SuppliesReorderPointMicroUnits;
            long target=intent.SuppliesTargetMicroUnits==0?checked(Math.Max(point,minimumPoint)+(intent.SuppliesReorderEnabled?cadenceConsumption:0)):intent.SuppliesTargetMicroUnits;
            if(point<minimumPoint || target<point || target<suppliesFloor)
                throw new InvalidDataException("Reviewed Supplies reorder levels must cover the actual present and promised population reserve plus delayed supplier transit; minimum point is "+minimumPoint.ToString(CultureInfo.InvariantCulture)+" microunits.");
            var p=new ColonyStartupPolicies {SuppliesReorderEnabled=intent.SuppliesReorderEnabled,SuppliesReorderPointMicroUnits=point,
                SuppliesTargetMicroUnits=target,ReorderCadenceSeconds=intent.ReorderCadenceSeconds,ServiceEnabled=intent.ServiceEnabled,
                ServiceTargetFillFraction=intent.ServiceTargetFillFraction,ServiceCadenceSeconds=intent.ServiceCadenceSeconds,
                LocalProcurementEnabled=intent.LocalProcurementEnabled,SupportedPeople=people};
            p.StockTargets.Add(new MaterialRequirement{Resource="Supplies",Amount=target});
            foreach(var item in new[] {new MaterialRequirement{Resource="Machinery",Amount=intent.MachineryReserveMicroUnits},
                new MaterialRequirement{Resource="MaterialKits",Amount=intent.MaterialKitsReserveMicroUnits},
                new MaterialRequirement{Resource="EnrichedUranium",Amount=intent.EnrichedUraniumReserveMicroUnits}})
                if(item.Amount>0)p.StockTargets.Add(item);
            foreach(var item in p.StockTargets)
            {
                long floor=item.Resource=="Supplies"?suppliesFloor:colony.Stock.SingleOrDefault(s=>s.Resource==item.Resource)?.SupportFloor??0;
                long extra=Math.Max(0,item.Amount-floor);
                if(extra>0)p.ReserveMaterials.Add(new MaterialRequirement{Resource=item.Resource,Amount=extra});
            }
            p.StockTargets=p.StockTargets.OrderBy(m=>m.Resource,StringComparer.Ordinal).ToList();
            p.ReserveMaterials=p.ReserveMaterials.OrderBy(m=>m.Resource,StringComparer.Ordinal).ToList();
            foreach(var item in p.StockTargets)p.Reorders.Add(new ColonyStartupReorder {Resource=item.Resource,
                Enabled=item.Resource=="Supplies"?p.SuppliesReorderEnabled:p.ServiceEnabled,
                ReorderPoint=item.Resource=="Supplies"?point:item.Amount,TargetAmount=item.Amount,
                CadenceSeconds=item.Resource=="Supplies"?p.ReorderCadenceSeconds:p.ServiceCadenceSeconds});
            q.StartupPolicies=p;
        }
        public static List<MaterialRequirement> PlanningMaterialRequirements(ColonyPlanningQuote q) =>
            q.Materials.Concat(q.StartupPolicies?.ReserveMaterials??new List<MaterialRequirement>()).Concat(ProductionOperatingMaterials(q)).GroupBy(m=>m.Resource,StringComparer.Ordinal)
                .Select(g=>new MaterialRequirement{Resource=g.Key,Amount=checked(g.Sum(m=>m.Amount))}).OrderBy(m=>m.Resource,StringComparer.Ordinal).ToList();
        static bool ReleasePlanningStartupReserves(ColonyState state,ColonyPlan plan)
        {
            if(plan.Quote.StartupPolicies==null||plan.StartupReservesReleased)return false;
            var colony=Colony(state,plan.ColonyId);
            foreach(var material in plan.Quote.StartupPolicies.ReserveMaterials)
            {
                var claim=plan.Claims.Single(c=>c.Resource==material.Resource);
                if(claim.Remaining!=material.Amount || claim.Reserved!=material.Amount)
                    throw new InvalidDataException("Waiting for exact owned startup operating stock: "+material.Resource+".");
            }
            var supplies=Stock(colony,"Supplies");
            if(supplies.Amount-supplies.Reserved<plan.Quote.StartupSupportReserve)
                throw new InvalidDataException("The reviewed actual-population support reserve is not yet owned.");
            foreach(var material in plan.Quote.StartupPolicies.ReserveMaterials)
            {
                var claim=plan.Claims.Single(c=>c.Resource==material.Resource);
                Stock(colony,material.Resource).Reserved-=material.Amount;claim.Reserved-=material.Amount;claim.Remaining-=material.Amount;
            }
            plan.StartupReservesReleased=true;
            Log(state,state.SimulatedUt,colony.Id,PlanningChildId(plan.Id,"startup-reserves"),"startupReserves",
                "Reviewed operating stock is owned; construction claims released after actual commissioning. No new stock was created.");
            return true;
        }
        static void ApplyPlanningStartupPolicies(ColonyState state,ColonyPlan plan,ColonyEnvironment env)
        {
            var p=plan.Quote.StartupPolicies;
            if(p==null||plan.StartupPoliciesApplied)return;
            if(!plan.StartupReservesReleased || !Colony(state,plan.ColonyId).SupportCommissionedUt.HasValue)
                throw new InvalidDataException("Reviewed startup stock and real support commissioning must complete before policy activation.");
            foreach(var reorder in p.Reorders)ConfigureReorder(state,PlanningCommand(state,env,plan,"startup-reorder-"+reorder.Resource,"configureReorderPolicy",new Dictionary<string,string>{
                ["Resource"]=reorder.Resource,["Enabled"]=reorder.Enabled.ToString(),["ReorderPointMicroUnits"]=reorder.ReorderPoint.ToString(CultureInfo.InvariantCulture),
                ["TargetMicroUnits"]=reorder.TargetAmount.ToString(CultureInfo.InvariantCulture),["CadenceSeconds"]=reorder.CadenceSeconds.ToString("R",CultureInfo.InvariantCulture)}),env);
            ExecuteServices(state,PlanningCommand(state,env,plan,"startup-service","configureServicePolicy",new Dictionary<string,string>{
                ["AutomaticEnabled"]=p.ServiceEnabled.ToString(),["TargetFillFraction"]=p.ServiceTargetFillFraction.ToString("R",CultureInfo.InvariantCulture),
                ["CadenceSeconds"]=p.ServiceCadenceSeconds.ToString("R",CultureInfo.InvariantCulture)}),env);
            ExecutePhysicalProcurement(state,PlanningCommand(state,env,plan,"startup-local","configurePhysicalProcurement",new Dictionary<string,string>{
                ["Enabled"]=p.LocalProcurementEnabled.ToString()}),env);
            // Configure uses the current/default local bounds. Install the exact
            // reviewed bounds too, rather than inheriting a previous wider policy.
            var local=state.PhysicalPolicies.Single(x=>x.ColonyId==plan.ColonyId);
            local.CadenceSeconds=p.LocalProcurementCadenceSeconds;local.MaximumTransfer=p.LocalProcurementMaximumMicroUnits;local.NativeInputTargetFraction=p.NativeInputBufferFraction;
            plan.StartupPoliciesApplied=true;
            Log(state,env.Ut,plan.ColonyId,PlanningChildId(plan.Id,"startup-policies"),"startupPolicies",
                "Reviewed enabled/declined Supplies, installed service and local warehouse policies applied; future purchases retain cash floor, budget and shared fleet limits.");
        }
    }
    public static partial class ColonyStateCodec
    {
        static partial void ValidateStartupIntent(ColonyFoundingIntent intent) { if(intent.Startup!=null)ValidateStartupPolicyIntent(intent.Startup); }
        public static void ValidateStartupPolicyIntent(ColonyStartupIntent p)
        {
            Quantity(p.SuppliesReorderPointMicroUnits);Quantity(p.SuppliesTargetMicroUnits);Quantity(p.MachineryReserveMicroUnits);
            Quantity(p.MaterialKitsReserveMicroUnits);Quantity(p.EnrichedUraniumReserveMicroUnits);
            Range(p.ReorderCadenceSeconds,21600,30*ColonyLimits.KerbinDay);Range(p.ServiceCadenceSeconds,21600,365*ColonyLimits.KerbinDay);Range(p.ServiceTargetFillFraction,.01,1);
            if(p.SuppliesTargetMicroUnits>0&&p.SuppliesReorderPointMicroUnits>p.SuppliesTargetMicroUnits)Fail("Startup Supplies target is below its reorder point.");
        }
        static void ValidatePlanningStartup(ColonyPlan plan,ColonyRecord colony)
        {
            var p=plan.Quote.StartupPolicies;
            if(p==null){if(plan.StartupPoliciesApplied||plan.StartupReservesReleased)Fail("Startup progress lacks reviewed terms.");return;}
            if(plan.Quote.FoundingIntent?.Startup==null||plan.Quote.Kind!="founding")Fail("Startup policies lack exact founding intent.");
            var intent=plan.Quote.FoundingIntent!.Startup!;
            if(p.SuppliesReorderEnabled!=intent.SuppliesReorderEnabled||p.ServiceEnabled!=intent.ServiceEnabled||p.LocalProcurementEnabled!=intent.LocalProcurementEnabled||
                p.ReorderCadenceSeconds!=intent.ReorderCadenceSeconds||p.ServiceCadenceSeconds!=intent.ServiceCadenceSeconds||p.ServiceTargetFillFraction!=intent.ServiceTargetFillFraction||
                intent.SuppliesReorderPointMicroUnits>0&&p.SuppliesReorderPointMicroUnits!=intent.SuppliesReorderPointMicroUnits||
                intent.SuppliesTargetMicroUnits>0&&p.SuppliesTargetMicroUnits!=intent.SuppliesTargetMicroUnits)Fail("Resolved startup policies substituted explicit reviewed intent.");
            Materials(p.StockTargets);Materials(p.ReserveMaterials);Rows(p.StockTargets,4);Rows(p.ReserveMaterials,4);Count(p.SupportedPeople,512);
            Quantity(p.SuppliesReorderPointMicroUnits);Quantity(p.SuppliesTargetMicroUnits);Range(p.ReorderCadenceSeconds,21600,30*ColonyLimits.KerbinDay);
            Range(p.ServiceCadenceSeconds,21600,365*ColonyLimits.KerbinDay);Range(p.ServiceTargetFillFraction,.01,1);
            if(p.LocalProcurementCadenceSeconds!=5||p.LocalProcurementMaximumMicroUnits!=1000*ColonyLimits.Units||p.NativeInputBufferFraction!=.5)Fail("Startup local procurement limits differ from reviewed bounded defaults.");
            if(p.SuppliesReorderPointMicroUnits<plan.Quote.StartupSupportReserve||p.SuppliesTargetMicroUnits<p.SuppliesReorderPointMicroUnits||
                !p.StockTargets.Any(m=>m.Resource=="Supplies"&&m.Amount==p.SuppliesTargetMicroUnits)||p.StockTargets.Any(m=>m.Resource!="Supplies"&&m.Resource!="Machinery"&&m.Resource!="MaterialKits"&&m.Resource!="EnrichedUranium"))Fail("Startup stock targets differ from the supported resource contract.");
            foreach(var material in p.ReserveMaterials)
                if(!p.StockTargets.Any(m=>m.Resource==material.Resource&&m.Amount>=material.Amount))Fail("Operating reserve lacks its stock target.");
            foreach(var item in new[]{new MaterialRequirement{Resource="Machinery",Amount=intent.MachineryReserveMicroUnits},
                new MaterialRequirement{Resource="MaterialKits",Amount=intent.MaterialKitsReserveMicroUnits},new MaterialRequirement{Resource="EnrichedUranium",Amount=intent.EnrichedUraniumReserveMicroUnits}})
                if(item.Amount==0?p.StockTargets.Any(m=>m.Resource==item.Resource):!p.StockTargets.Any(m=>m.Resource==item.Resource&&m.Amount==item.Amount))Fail("Startup service buffer substituted a different reviewed quantity.");
            Rows(p.Reorders,4);Unique(p.Reorders.Select(r=>r.Resource));
            if(p.Reorders.Count!=p.StockTargets.Count)Fail("Startup reorder terms differ from reviewed operating resources.");
            foreach(var reorder in p.Reorders)
            {
                Text(reorder.Resource,128,true);Quantity(reorder.ReorderPoint);Quantity(reorder.TargetAmount);Range(reorder.CadenceSeconds,21600,365*ColonyLimits.KerbinDay);
                var target=p.StockTargets.SingleOrDefault(m=>m.Resource==reorder.Resource);
                if(target==null||reorder.TargetAmount!=target.Amount||reorder.ReorderPoint!=(reorder.Resource=="Supplies"?p.SuppliesReorderPointMicroUnits:target.Amount)||
                    reorder.Enabled!=(reorder.Resource=="Supplies"?p.SuppliesReorderEnabled:p.ServiceEnabled)||
                    reorder.CadenceSeconds!=(reorder.Resource=="Supplies"?p.ReorderCadenceSeconds:p.ServiceCadenceSeconds))Fail("Startup recurring policy differs from its reviewed exact terms.");
            }
            if(plan.StartupPoliciesApplied&&!plan.StartupReservesReleased || plan.State=="complete"&&(!plan.StartupReservesReleased||!plan.StartupPoliciesApplied))Fail("Plan completed before reviewed operating stock/policies.");
            if(plan.StartupReservesReleased&&plan.Buildings.Any(b=>b.OrderId.Length==0))Fail("Startup reserve released before construction committed.");
        }
    }
}
