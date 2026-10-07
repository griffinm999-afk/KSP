using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Expanse.Domain.Colonies
{
    // Null retains the original building-only contract. The product sends an
    // explicit intent and displays the resolved named bill before approval.
    public sealed partial class ColonyFoundingIntent
    {
        public bool FillPopulationTarget { get; set; } = true;
        public int NewArrivalCount { get; set; }
        public List<string> ExistingResidentRosterIds { get; set; } = new List<string>();
        public List<string> RecruitRosterIds { get; set; } = new List<string>();
        public string PassengerRouteId { get; set; } = "";
        public ColonyStartupIntent? Startup { get; set; }
    }
    public sealed class ColonyPlanningResident
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "existing";
        public string RosterId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Trait { get; set; } = "";
        public string ExpectedRosterType { get; set; } = "Crew";
        public string SourceFacilityId { get; set; } = "";
        public string SourceCabinName { get; set; } = "";
        public string SourceVesselId { get; set; } = "";
        public uint SourcePartId { get; set; }
        public string HomeBuildingId { get; set; } = "";
        public string HomeFacilityId { get; set; } = "";
        public uint HomePartId { get; set; }
        public int HomeSlot { get; set; }
        public string RouteId { get; set; } = "";
        public string RouteHash { get; set; } = "";
        public long Fare { get; set; }
        public double TravelSeconds { get; set; }
    }
    public sealed class ColonyPlanningResidentClaim
    {
        public string Id { get; set; } = "";
        public string State { get; set; } = "planned";
        public string OperationId { get; set; } = "";
        public string HomeFacilityId { get; set; } = "";
        public uint HomePartId { get; set; }
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
    }
    public static partial class ColonyEngine
    {
        static void QuotePlanningResidents(ColonyPlanningQuote q,ColonyRecord colony,ColonyState state,ColonyEnvironment env,ColonyFoundingIntent? intent)
        {
            if(intent==null)return;
            ColonyStateCodec.ValidateFoundingIntent(intent);
            if(q.Kind!="founding")throw new InvalidDataException("Named founding intent cannot be attached to a different plan kind.");
            if(!env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id,out var present))throw new InvalidDataException("A complete actual site population is needed to review named residents.");
            int committed=colony.Residents.Count(r=>r.Status!="missing"),needed=colony.Charter.PopulationTarget-committed;
            if(needed<0 || needed>64 || intent.NewArrivalCount>needed)throw new InvalidDataException("Reviewed arrival count exceeds the remaining approved population target or the 64-person founding limit.");
            var existing=env.People.Roster.Where(p=>PlanningResidentCandidate(state,colony,q,env,p,false) && present.Contains(p.RosterId)).OrderBy(p=>p.RosterId,StringComparer.Ordinal).ToArray();
            var recruits=env.People.Roster.Where(p=>PlanningResidentCandidate(state,colony,q,env,p,true)).OrderBy(p=>p.Type=="Crew"?0:1).ThenBy(p=>p.RosterId,StringComparer.Ordinal).ToArray();
            var chosenExisting=intent.ExistingResidentRosterIds.Select(id=>existing.SingleOrDefault(p=>p.RosterId==id) ?? throw new InvalidDataException("Selected existing resident is absent, protected, already reserved, or assigned to the bootstrap crew: "+id)).ToList();
            var chosenRecruits=intent.RecruitRosterIds.Select(id=>recruits.SingleOrDefault(p=>p.RosterId==id) ?? throw new InvalidDataException("Selected recruit is no longer an available ordinary named roster record: "+id)).ToList();
            if(chosenRecruits.Count>intent.NewArrivalCount)throw new InvalidDataException("Selected recruits exceed the reviewed new-arrival count.");
            if(intent.FillPopulationTarget)
            {
                foreach(var p in recruits.Where(p=>!chosenRecruits.Any(c=>c.RosterId==p.RosterId)).Take(intent.NewArrivalCount-chosenRecruits.Count))chosenRecruits.Add(p);
                int count=needed-chosenRecruits.Count;
                foreach(var p in existing.Where(p=>!chosenExisting.Any(c=>c.RosterId==p.RosterId)).Take(Math.Max(0,count-chosenExisting.Count)))chosenExisting.Add(p);
                if(chosenRecruits.Count!=intent.NewArrivalCount || chosenExisting.Count+chosenRecruits.Count!=needed)throw new InvalidDataException("Not enough unreserved ordinary visitors or available named recruits to fill the reviewed target; select fewer arrivals or change the charter.");
            }
            else if(chosenRecruits.Count!=intent.NewArrivalCount || chosenExisting.Count+chosenRecruits.Count>needed)throw new InvalidDataException("Explicit named resident selections do not match arrival count or exceed the population target.");
            var route=chosenRecruits.Count==0?null:env.People.Routes.Where(r=>r.Body==colony.Site.Body && r.Qualified && r.Evidence.Length>0 && (!r.DevelopmentOnly || env.DevelopmentMode))
                .OrderBy(r=>r.Fare+r.RecruitmentFee).ThenBy(r=>r.Id,StringComparer.Ordinal).FirstOrDefault(r=>intent.PassengerRouteId.Length==0 || r.Id==intent.PassengerRouteId);
            if(chosenRecruits.Count>0 && route==null)throw new InvalidDataException("A qualified finite paid passenger route is required for the reviewed arrivals.");
            foreach(var p in chosenExisting.Concat(chosenRecruits))
            {
                bool recruit=chosenRecruits.Contains(p);
                var row=new ColonyPlanningResident {Id="resident-"+q.Residents.Count,Kind=recruit?"recruit":"existing",RosterId=p.RosterId,Name=p.Name,Trait=p.Trait,ExpectedRosterType=p.Type};
                if(recruit){row.RouteId=route!.Id;row.RouteHash=route.Hash;row.Fare=checked(route.Fare+(p.Type=="Applicant"?route.RecruitmentFee:0));row.TravelSeconds=route.TravelSeconds;}
                else
                {
                    row.SourceVesselId=p.VesselId;row.SourcePartId=p.PartId;
                    var source=colony.Facilities.SingleOrDefault(f=>f.VesselId==p.VesselId&&f.PartIds.Contains(p.PartId));
                    row.SourceFacilityId=source==null?"":source.Id;row.SourceCabinName=source==null?"Current witnessed colony cabin":source.Name;
                }
                AssignQuotedPlanningHome(q,colony,env,row);q.Residents.Add(row);
            }
            // Freeze names chosen by the default fill algorithm, so review and
            // approval cannot silently exchange a missing person for another.
            q.FoundingIntent=ColonyJson.Deserialize<ColonyFoundingIntent>(ColonyJson.Serialize(intent,32768),32768);
            q.FoundingIntent.FillPopulationTarget=false;q.FoundingIntent.ExistingResidentRosterIds=chosenExisting.Select(p=>p.RosterId).ToList();
            q.FoundingIntent.RecruitRosterIds=chosenRecruits.Select(p=>p.RosterId).ToList();q.FoundingIntent.PassengerRouteId=route==null?"":route.Id;
        }
        static bool PlanningResidentCandidate(ColonyState state,ColonyRecord colony,ColonyPlanningQuote q,ColonyEnvironment env,ColonyRosterWitness p,bool recruit) =>
            p.Current&&p.ContextKey==env.ContextKey&&!p.ProtectedMissionCrew && (recruit ? p.Status=="Available"&&(p.Type=="Crew"||p.Type=="Applicant") : p.Type=="Crew"&&p.Status=="Assigned"&&colony.VisitorRosterIds.Contains(p.RosterId)&&p.PartId!=0&&Guid.TryParse(p.VesselId,out _)&&env.People.Seats.Count(s=>s.Current&&s.ContextKey==env.ContextKey&&s.Occupants.Contains(p.RosterId))==1&&env.People.Seats.Any(s=>s.Current&&s.ContextKey==env.ContextKey&&s.VesselId==p.VesselId&&s.PartId==p.PartId&&s.Occupants.Contains(p.RosterId))) &&
            !q.BootstrapWorkers.Any(w=>w.RosterId==p.RosterId)&&!state.Colonies.Any(c=>c.Residents.Any(r=>r.RosterId==p.RosterId))&&!IsRosterReservedForPlanning(state,p.RosterId)&&
            !state.PeopleOperations.Any(o=>o.RosterId==p.RosterId&&o.State!="complete"&&o.State!="cancelled");

        static void AssignQuotedPlanningHome(ColonyPlanningQuote q,ColonyRecord colony,ColonyEnvironment env,ColonyPlanningResident row)
        {
            foreach(var home in colony.Facilities.Where(f=>f.State=="operational"&&f.CertifiedHomes>0&&f.Qualification.HousingCertified).OrderBy(f=>f.Id,StringComparer.Ordinal))
            {
                int promised=colony.Residents.Count(r=>r.HomeFacilityId==home.Id)+q.Residents.Count(r=>r.HomeFacilityId==home.Id);
                if(promised>=home.CertifiedHomes)continue;
                foreach(var seat in env.People.Seats.Where(s=>s.FacilityId==home.Id&&s.VesselId==home.VesselId&&s.Current&&s.ContextKey==env.ContextKey&&s.HousingCertified&&s.UtilitiesQualified).OrderBy(s=>s.PartId))
                {
                    int unavailable=seat.Occupants.Count(n=>n!=row.RosterId)+colony.Residents.Count(r=>r.HomeFacilityId==home.Id&&r.HomePartId==seat.PartId&&!seat.Occupants.Contains(r.RosterId))+q.Residents.Count(r=>r.HomeFacilityId==home.Id&&r.HomePartId==seat.PartId);
                    if(unavailable>=seat.Capacity)continue;
                    row.HomeFacilityId=home.Id;row.HomePartId=seat.PartId;return;
                }
            }
            foreach(var building in q.Buildings.Where(b=>b.Role=="housing"&&b.Homes>0))
            {
                int count=q.Residents.Count(r=>r.HomeBuildingId==building.Id);
                if(count>=building.Homes)continue;row.HomeBuildingId=building.Id;row.HomeSlot=count;return;
            }
            throw new InvalidDataException("No reviewed certified-home capacity remains for "+row.Name+".");
        }
        static long PlanningResidentSupportReserve(ColonyPlanningQuote q,ColonyRecord colony,ColonyEnvironment env)
        {
            if(q.Residents.Count==0)return 0;
            var present=env.People.PresentByColony[colony.Id];
            int people=present.Concat(colony.Residents.Where(r=>r.Status!="missing").Select(r=>r.RosterId)).Concat(q.Residents.Where(r=>r.Kind=="recruit").Select(r=>r.RosterId)).Distinct(StringComparer.Ordinal).Count();
            return checked((long)decimal.Ceiling((decimal)Math.Max(q.TargetPopulation,people)*env.Support.MicroUnitsPerPersonDay*(decimal)colony.Charter.ReserveDays));
        }
        internal static long PlanningResidentFutureFunds(ColonyPlan plan) => plan.Residents.Where(c=>c.OperationId.Length==0&&c.State!="cancelled").Sum(c=>plan.Quote.Residents.Single(r=>r.Id==c.Id).Fare);
        static void CancelPlanningResidents(ColonyPlan plan){foreach(var claim in plan.Residents.Where(c=>c.State=="planned"))claim.State="cancelled";}
        internal static bool PlanningResidentClaimPending(ColonyPlan plan,ColonyPlanningResidentClaim claim) => claim.State=="queued" || claim.State=="planned"&&PlanningActive(plan);
        static bool IsRosterReservedForPlanningResident(ColonyState state,string rosterId) => state.Plans.Any(p=>p.Residents.Any(c=>PlanningResidentClaimPending(p,c)&&p.Quote.Residents.Single(r=>r.Id==c.Id).RosterId==rosterId));
        static int PlanningPopulationReservations(ColonyState state,ColonyRecord colony,string exceptRoster) => state.Plans.Where(p=>p.ColonyId==colony.Id).Sum(p=>p.Residents.Count(c=>PlanningResidentClaimPending(p,c)&&
            p.Quote.Residents.Single(r=>r.Id==c.Id).RosterId!=exceptRoster&&!colony.Residents.Any(r=>r.RosterId==p.Quote.Residents.Single(q=>q.Id==c.Id).RosterId)));
        static bool IsOwnPlanningResidentCommand(ColonyState state,ColonyCommand command,string rosterId) => state.Plans.Any(p=>p.ColonyId==command.ColonyId&&p.Residents.Any(c=>c.State=="queued"&&c.OperationId==command.OperationId&&c.OperationId==PlanningChildId(p.Id,c.Id)&&
            c.HomeFacilityId==Field(command,"HomeFacilityId","")&&c.HomePartId==Integer(command,"HomePartId",0)&&p.Quote.Residents.Single(r=>r.Id==c.Id).RosterId==rosterId));

        static (string facility,uint part) ResolvePlanningResidentHome(ColonyState state,ColonyPlan plan,ColonyPlanningResident row,ColonyEnvironment env)
        {
            if(row.HomeBuildingId.Length==0)return (row.HomeFacilityId,row.HomePartId);
            var building=plan.Buildings.Single(b=>b.Id==row.HomeBuildingId);var order=state.Construction.SingleOrDefault(o=>o.Id==building.OrderId);
            if(order==null||order.State!="operational"||order.FacilityId.Length==0)throw new InvalidDataException("Waiting for the reviewed real housing building to commission for "+row.Name+".");
            var facility=Colony(state,plan.ColonyId).Facilities.Single(f=>f.Id==order.FacilityId&&f.ConstructionOrderId==order.Id);
            int offset=row.HomeSlot;
            foreach(var seat in env.People.Seats.Where(s=>s.FacilityId==facility.Id&&s.VesselId==facility.VesselId&&s.Current&&s.HousingCertified&&s.UtilitiesQualified&&s.ContextKey==env.ContextKey).OrderBy(s=>s.PartId))
            {if(offset<seat.Capacity)return(facility.Id,seat.PartId);offset-=seat.Capacity;}
            throw new InvalidDataException("Actual current certified rooms do not cover the reviewed home slot for "+row.Name+"; no replacement home was selected.");
        }
        static int PlanningHomeReservations(ColonyState state,ColonyEnvironment env,string colonyId,string facilityId,uint? partId,string exceptRoster)
        {
            int count=0;
            foreach(var plan in state.Plans.Where(p=>p.ColonyId==colonyId))foreach(var claim in plan.Residents.Where(c=>PlanningResidentClaimPending(plan,c)))
            {
                var row=plan.Quote.Residents.Single(r=>r.Id==claim.Id);if(row.RosterId==exceptRoster||Colony(state,colonyId).Residents.Any(r=>r.RosterId==row.RosterId))continue;
                try{var home=claim.HomeFacilityId.Length>0?(claim.HomeFacilityId,claim.HomePartId):ResolvePlanningResidentHome(state,plan,row,env);if(home.Item1==facilityId&&(!partId.HasValue||home.Item2==partId.Value))count++;}
                catch(InvalidDataException){} // An unbuilt home does not occupy an unrelated facility.
            }
            return count;
        }
        static string PlanningResidentWitness(ColonyRosterWitness person,string home,uint part,string designation) => ColonyStateCodec.Hash(ColonyJson.Serialize(new Dictionary<string,object>{
            ["RosterId"]=person.RosterId,["Name"]=person.Name,["Trait"]=person.Trait,["Type"]=person.Type,["Status"]=person.Status,["Vessel"]=person.VesselId,["Part"]=person.PartId,["Home"]=home,["HomePart"]=part,["Designation"]=designation},4096));
        static bool RunPlanningResident(ColonyState state,ColonyPlan plan,ColonyEnvironment env)
        {
            foreach(var claim in plan.Residents)
            {
                if(claim.State=="complete")continue;
                var row=plan.Quote.Residents.Single(r=>r.Id==claim.Id);
                if(claim.State=="cancelled")throw new InvalidDataException("Reviewed resident admission was cancelled: "+row.Name+"; no alternate person is chosen.");
                if(claim.State=="queued")
                {
                    var op=state.PeopleOperations.SingleOrDefault(o=>o.Id==claim.OperationId);
                    if(op==null||op.State=="cancelled")throw new InvalidDataException("Reviewed passenger child is unavailable or cancelled; review the plan.");
                    if(op.State!="complete")continue;
                    var admitted=Colony(state,plan.ColonyId).Residents.SingleOrDefault(r=>r.RosterId==row.RosterId&&r.ArrivalOperationId==op.Id&&r.Status=="resident"&&r.HomeFacilityId==claim.HomeFacilityId&&r.HomePartId==claim.HomePartId);
                    if(admitted==null||op.AfterWitness.Length==0)throw new InvalidDataException("Named passenger has no exact completed home/roster receipt.");
                    claim.State="complete";claim.AfterWitness=op.AfterWitness;return true;
                }
                var person=Person(env,row.RosterId);
                if(person.Name!=row.Name||person.Trait!=row.Trait||person.Type!=row.ExpectedRosterType||person.ProtectedMissionCrew)throw new InvalidDataException("Reviewed named Kerbal changed; no implicit replacement for "+row.Name+".");
                if(row.Kind=="existing"&&(person.VesselId!=row.SourceVesselId||person.PartId!=row.SourcePartId))throw new InvalidDataException("Reviewed existing resident moved from its source cabin; preserve its current crew membership and review again.");
                var home=ResolvePlanningResidentHome(state,plan,row,env);claim.HomeFacilityId=home.facility;claim.HomePartId=home.part;
                claim.OperationId=PlanningChildId(plan.Id,row.Id);claim.State="queued";claim.BeforeWitness=PlanningResidentWitness(person,home.facility,home.part,row.Kind=="existing"?"visitor":"available recruit");
                var command=PlanningCommand(state,env,plan,row.Id,row.Kind=="existing"?"assignResident":"recruitResident",new Dictionary<string,string>{
                    ["RosterId"]=row.RosterId,["HomeFacilityId"]=home.facility,["HomePartId"]=home.part.ToString(CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"});
                if(row.Kind=="existing")
                {
                    AssignResident(state,command,env);claim.State="complete";claim.AfterWitness=PlanningResidentWitness(person,home.facility,home.part,"resident");
                }
                else
                {
                    command.Fields["RouteId"]=row.RouteId;command.Fields["RouteHash"]=row.RouteHash;command.Fields["QuotedFunds"]=row.Fare.ToString(CultureInfo.InvariantCulture);
                    plan.RemainingFunds-=row.Fare;RecruitResident(state,command,env);
                }
                plan.Reason="Reviewed named resident "+row.Name+" "+(row.Kind=="existing"?"designated; actual source crew cabin preserved.":"reserved on the paid route; actual arrival readback remains required.");return true;
            }
            return false;
        }
        static ColonyState? ReconcileTerminalPlanningResidents(ColonyState prior)
        {
            var terminal=prior.Plans.SelectMany(p=>p.Residents.Where(c=>c.State=="queued").Select(c=>new{Plan=p,Claim=c,Op=prior.PeopleOperations.SingleOrDefault(o=>o.Id==c.OperationId)}))
                .FirstOrDefault(x=>x.Op!=null&&(x.Op.State=="complete"||x.Op.State=="cancelled"));
            if(terminal==null)return null;
            var state=ColonyStateCodec.Copy(prior);var claim=state.Plans.Single(p=>p.Id==terminal.Plan.Id).Residents.Single(c=>c.Id==terminal.Claim.Id);
            claim.State=terminal.Op!.State;claim.AfterWitness=terminal.Op.AfterWitness;
            return FinishPlanningTransition(state);
        }
    }
    public static partial class ColonyStateCodec
    {
        public static void ValidateFoundingIntent(ColonyFoundingIntent intent)
        {
            if(intent==null)Fail("Founding intent is missing.");Rows(intent!.ExistingResidentRosterIds,64);Rows(intent.RecruitRosterIds,64);Count(intent.NewArrivalCount,64);Text(intent.PassengerRouteId,128);
            Unique(intent.ExistingResidentRosterIds.Concat(intent.RecruitRosterIds));foreach(string id in intent.ExistingResidentRosterIds.Concat(intent.RecruitRosterIds))Text(id,160,true);
            ValidateStartupIntent(intent);
            if(intent.Production!=null)ValidateProductionIntent(intent.Production);
        }
        static partial void ValidateStartupIntent(ColonyFoundingIntent intent);
        static void ValidatePlanningResidents(ColonyState state,ColonyPlan plan,ColonyRecord colony)
        {
            Rows(plan.Quote.Residents,64);Rows(plan.Residents,64);Unique(plan.Quote.Residents.Select(r=>r.Id));Unique(plan.Quote.Residents.Select(r=>r.RosterId));Unique(plan.Residents.Select(r=>r.Id));
            if(plan.Quote.FoundingIntent!=null)ValidateFoundingIntent(plan.Quote.FoundingIntent);
            if(plan.Quote.Residents.Count!=plan.Residents.Count)Fail("Reviewed resident claims differ from the named founding bill.");
            if(plan.Quote.Residents.Count>0&&plan.Quote.FoundingIntent==null)Fail("Named residents lack their reviewed intent.");
            foreach(var row in plan.Quote.Residents)
            {
                Text(row.Id,128,true);Choice(row.Kind,"existing","recruit");Text(row.RosterId,160,true);Text(row.Name,160,true);Text(row.Trait,64,true);Choice(row.ExpectedRosterType,"Crew","Applicant");
                Text(row.SourceCabinName,160);Text(row.RouteId,128);Text(row.RouteHash,128);Funds(row.Fare);Time(row.TravelSeconds);Count(row.HomeSlot,512);
                if(row.HomeBuildingId.Length>0){Text(row.HomeBuildingId,128,true);var b=plan.Quote.Buildings.SingleOrDefault(x=>x.Id==row.HomeBuildingId&&x.Role=="housing");if(b==null||row.HomeSlot>=b.Homes||row.HomeFacilityId.Length>0||row.HomePartId!=0)Fail("Resident has no exact reviewed housing slot.");}
                else{Id(row.HomeFacilityId);if(row.HomePartId==0||!colony.Facilities.Any(f=>f.Id==row.HomeFacilityId&&f.HomePartPersistentIds.Contains(row.HomePartId)))Fail("Resident lacks an exact existing certified-home mapping.");}
                if(row.Kind=="existing"){Id(row.SourceVesselId);if(row.SourceFacilityId.Length>0)Id(row.SourceFacilityId);if(row.ExpectedRosterType!="Crew"||row.SourcePartId==0||row.Fare!=0||row.TravelSeconds!=0||row.RouteId.Length>0||row.RouteHash.Length>0)Fail("Existing designation contains transport terms or missing physical source.");}
                else if(row.SourceVesselId.Length>0||row.SourcePartId!=0||row.RouteId.Length==0||row.RouteHash.Length==0||row.Fare<=0||row.TravelSeconds<=0)Fail("Recruit lacks exact funded passenger terms.");
                var claim=plan.Residents.Single(c=>c.Id==row.Id);Choice(claim.State,"planned","queued","complete","cancelled");Text(claim.BeforeWitness,128);Text(claim.AfterWitness,4096);
                if(claim.OperationId.Length==0){if(claim.State=="queued"||claim.State=="complete"||claim.HomeFacilityId.Length>0||claim.HomePartId!=0)Fail("Unstarted resident claim contains a fabricated home or receipt.");continue;}
                Id(claim.OperationId);Id(claim.HomeFacilityId);if(claim.OperationId!=ColonyEngine.PlanningChildId(plan.Id,row.Id)||claim.HomePartId==0||claim.BeforeWitness.Length==0)Fail("Resident child lost exact lineage.");
                if(row.HomeBuildingId.Length==0){if(claim.HomeFacilityId!=row.HomeFacilityId||claim.HomePartId!=row.HomePartId)Fail("Existing reviewed home was substituted.");}
                else{var b=plan.Buildings.Single(x=>x.Id==row.HomeBuildingId);var o=state.Construction.SingleOrDefault(x=>x.Id==b.OrderId);if(o==null||o.FacilityId!=claim.HomeFacilityId)Fail("Resident home differs from its reviewed constructed building.");}
                if(!colony.Facilities.Any(f=>f.Id==claim.HomeFacilityId&&f.HomePartPersistentIds.Contains(claim.HomePartId)))Fail("Resolved resident home is outside certified mapping.");
                if(row.Kind=="recruit")
                {
                    var op=state.PeopleOperations.SingleOrDefault(o=>o.Id==claim.OperationId);
                    if(op==null||op.Kind!="arrival"||op.ColonyId!=plan.ColonyId||op.RosterId!=row.RosterId||op.RouteId!=row.RouteId||op.RouteHash!=row.RouteHash||op.Funds!=row.Fare||op.TravelSeconds!=row.TravelSeconds||op.HomeFacilityId!=claim.HomeFacilityId||op.HomePartId!=claim.HomePartId)Fail("Reviewed recruit child lost route/funds/home lineage.");
                    if(claim.State=="complete"&&(op!.State!="complete"||claim.AfterWitness!=op.AfterWitness))Fail("Resident arrival completion lacks exact physical receipt.");
                }
                if(claim.State=="complete"&&claim.AfterWitness.Length==0)Fail("Completed designation lacks its before/after witness.");
            }
            if(plan.State=="complete"&&plan.Residents.Any(c=>c.State!="complete"))Fail("Founding plan completed before its promised inhabitants.");
        }
        static void ValidatePlanningResidentReservations(ColonyState state)
        {
            var claims=state.Plans.SelectMany(p=>p.Residents.Where(c=>ColonyEngine.PlanningResidentClaimPending(p,c)).Select(c=>new{Plan=p,Claim=c,Row=p.Quote.Residents.Single(r=>r.Id==c.Id)})).ToArray();Unique(claims.Select(c=>c.Row.RosterId));
            foreach(var c in claims)if(state.PeopleOperations.Any(o=>o.RosterId==c.Row.RosterId&&o.State!="complete"&&o.State!="cancelled"&&o.Id!=c.Claim.OperationId))Fail("Another operation holds a reviewed resident roster identity.");
        }
    }
}
