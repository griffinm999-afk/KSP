using System;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        // Automatic charters admit one actual person through the existing paid
        // passenger transaction. Housing plans and uncertain effects keep priority.
        static ColonyState RunAutomaticImmigration(ColonyState prior, ColonyEnvironment env, out string waitingColony, out string waitingReason)
        {
            waitingColony="";waitingReason="";
            foreach(var colony in prior.Colonies.Where(c=>c.Charter.GrowthPolicy=="automatic").OrderBy(c=>c.Id,StringComparer.Ordinal))
            {
                if(colony.Residents.Count+PlanningPopulationReservations(prior,colony,"")>=Math.Min(colony.Charter.PopulationTarget,colony.Charter.ResidentLimit))continue;
                try
                {
                    if(colony.Status!="operational"||!colony.SupportCommissionedUt.HasValue)throw new InvalidDataException("Waiting for the colony's homes and resident support to commission.");
                    if(prior.Plans.Any(p=>p.ColonyId==colony.Id&&PlanningActive(p)))throw new InvalidDataException("Waiting for the existing paid colony plan to finish before another resident is booked.");
                    SupportForAdmission(colony,1,env);
                    var person=env.People.Roster.Where(p=>p.Current&&p.ContextKey==env.ContextKey&&!p.ProtectedMissionCrew&&p.Status=="Available"&&(p.Type=="Crew"||p.Type=="Applicant")&&
                        !prior.Colonies.Any(c=>c.Residents.Any(r=>r.RosterId==p.RosterId))&&!IsRosterReservedForPlanning(prior,p.RosterId)&&
                        !prior.PeopleOperations.Any(o=>o.RosterId==p.RosterId&&o.State!="complete"&&o.State!="cancelled"))
                        .OrderBy(p=>p.Type=="Crew"?0:1).ThenBy(p=>p.RosterId,StringComparer.Ordinal).FirstOrDefault();
                    if(person==null)throw new InvalidDataException("Waiting for an available ordinary named Kerbal; protected crew and existing reservations are preserved.");
                    var forecast=Forecast(prior,colony.Id,env,new[]{person.RosterId});
                    if(!forecast.Sustainable)throw new InvalidDataException("Waiting for sustainable support before paid immigration: "+string.Join(" ",forecast.Shortages));
                    var route=env.People.Routes.Where(r=>r.Body==colony.Site.Body&&r.Qualified&&r.Evidence.Length>0&&(!r.DevelopmentOnly||env.DevelopmentMode)&&
                        prior.PeopleOperations.Count(o=>o.RouteId==r.Id&&o.State!="complete"&&o.State!="cancelled")<r.ConcurrentSeats)
                        .OrderBy(r=>checked(r.Fare+(person.Type=="Applicant"?r.RecruitmentFee:0))).ThenBy(r=>r.Id,StringComparer.Ordinal).FirstOrDefault();
                    if(route==null)throw new InvalidDataException("Waiting for a qualified paid passenger route with a free booking to this colony body.");
                    ColonySeatWitness? home=null;
                    foreach(var seat in env.People.Seats.Where(s=>s.Current&&s.ContextKey==env.ContextKey&&s.HousingCertified&&s.UtilitiesQualified&&s.CrewMutationSupported)
                        .OrderBy(s=>s.FacilityId,StringComparer.Ordinal).ThenBy(s=>s.PartId))
                    {
                        try{home=Home(prior,colony,env,seat.FacilityId,seat.PartId,person.RosterId);break;}
                        catch(InvalidDataException){/* A full or foreign cabin is not an admission slot. */}
                    }
                    if(home==null)throw new InvalidDataException("Waiting for a current certified home cabin with an unreserved seat, qualified utilities and crew-mutation provider.");
                    long fare=checked(route.Fare+(person.Type=="Applicant"?route.RecruitmentFee:0));
                    var command=new ColonyCommand {Kind="recruitResident",OperationId=Guid.NewGuid().ToString("D"),ColonyId=colony.Id,ContextKey=env.ContextKey,ExpectedRevision=prior.Revision,
                        Fields=new System.Collections.Generic.Dictionary<string,string>{["RosterId"]=person.RosterId,["HomeFacilityId"]=home.FacilityId,["HomePartId"]=home.PartId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["RouteId"]=route.Id,["RouteHash"]=route.Hash,["QuotedFunds"]=fare.ToString(System.Globalization.CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"}};
                    var result=Execute(prior,command,env);if(result.Outcome=="accepted")return result.State;
                    throw new InvalidDataException(result.Reason);
                }
                catch(Exception ex) when(ex is InvalidDataException||ex is ArgumentException||ex is OverflowException)
                {if(waitingColony.Length==0){waitingColony=colony.Id;waitingReason=ex.Message;}}
            }
            return prior;
        }
        static ColonyState RecordImmigrationWaiting(ColonyState prior,ColonyEnvironment env,string colonyId,string reason)
        {
            if(colonyId.Length==0)return prior;
            reason="Automatic immigration: "+reason;if(reason.Length>512)reason=reason.Substring(0,512);
            if(prior.Journal.LastOrDefault(j=>j.ColonyId==colonyId&&j.Kind=="immigrationWaiting")?.Detail==reason)return prior;
            var state=ColonyStateCodec.Copy(prior);Log(state,env.Ut,colonyId,colonyId,"immigrationWaiting",reason);return FinishPlanningTransition(state);
        }

        static string ExecutePeople(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            switch (command.Kind)
            {
                case "recruitResident": return RecruitResident(state, command, env);
                case "assignResident": return AssignResident(state, command, env);
                case "departResident": return DepartResident(state, command, env);
                case "cancelPassenger": return CancelPassenger(state, command, env);
                case "transferColonyWorker": return TransferColonyWorker(state,command,env);
                case "cancelWorkerTransfer": return CancelWorkerTransfer(state,command);
                default: return ExecuteServices(state, command, env);
            }
        }

        static ColonyRosterWitness Person(ColonyEnvironment env, string id)
        {
            var person = env.People.Roster.SingleOrDefault(x => x.RosterId == id);
            if (person == null || !person.Current || person.ContextKey != env.ContextKey)
                throw new InvalidDataException("The named KSP roster record has no current selected-save witness.");
            if (person.Status == "Missing" || person.Status == "Dead" || person.Status == "Unowned")
                throw new InvalidDataException("The named Kerbal is missing, dead or not controlled by this roster.");
            return person;
        }

        static void ProtectMissionCrew(ColonyRosterWitness person, ColonyCommand command)
        {
            if (person.ProtectedMissionCrew && Field(command, "ExplicitMissionCrewAssignment", "false") != "true")
                throw new InvalidDataException("Mission crew require explicit reassignment; Jeb, Bill, Bob and Val remain astronauts by default.");
        }

        static ColonySeatWitness Home(ColonyState state, ColonyRecord colony, ColonyEnvironment env, string facilityId, uint partId, string personId)
        {
            var facility = colony.Facilities.SingleOrDefault(x => x.Id == facilityId);
            if (facility == null || facility.State != "operational" || !facility.Qualification.HousingCertified || facility.CertifiedHomes <= 0)
                throw new InvalidDataException("Home is not an operational certified housing facility.");
            int residents = colony.Residents.Count(x => x.HomeFacilityId == facilityId && x.RosterId != personId)+PlanningHomeReservations(state,env,colony.Id,facilityId,null,personId);
            if (residents >= facility.CertifiedHomes) throw new InvalidDataException("Certified homes are already reserved.");
            var seat = env.People.Seats.SingleOrDefault(x => x.FacilityId == facilityId && x.PartId == partId);
            if (seat == null || !seat.Current || seat.ContextKey != env.ContextKey || seat.VesselId != facility.VesselId || !seat.HousingCertified)
                throw new InvalidDataException("Actual housing part and seat capacity need a current runtime witness.");
            int reserved = colony.Residents.Count(x => x.HomeFacilityId == facilityId && x.HomePartId == partId && x.RosterId != personId && !seat.Occupants.Contains(x.RosterId))+PlanningHomeReservations(state,env,colony.Id,facilityId,partId,personId);
            if (seat.Occupants.Count(x => x != personId) + reserved >= seat.Capacity)
                throw new InvalidDataException("Physical home seats are full or reserved by other residents.");
            return seat;
        }

        static void SupportForAdmission(ColonyRecord colony, int additional, ColonyEnvironment env)
        {
            RequireSupportOwner(colony, env, true);
            if (!colony.SupportCommissionedUt.HasValue) throw new InvalidDataException("Resident support must be commissioned before immigration.");
            if(!env.People.PresenceComplete||!env.People.PresentByColony.TryGetValue(colony.Id,out var present))throw new InvalidDataException("Admission requires a complete current physical site population.");
            long people = colony.Residents.Where(x=>x.Status!="missing").Select(x=>x.RosterId).Concat(colony.VisitorRosterIds).Concat(present).Distinct(StringComparer.Ordinal).Count()+additional;
            long needed = checked((long)decimal.Ceiling((decimal)people * colony.SupportMicroUnitsPerPersonDay * (decimal)colony.Charter.ReserveDays));
            var supplies = colony.Stock.SingleOrDefault(x => x.Resource == "Supplies");
            if (colony.SupportStatus.StartsWith("Shortage", StringComparison.Ordinal) || supplies == null || supplies.Amount - supplies.Reserved < needed)
                throw new InvalidDataException("Funded accessible resident-support reserves do not cover the charter reserve days; immigration is paused.");
        }

        static ColonyPassengerRoute Route(ColonyState state, ColonyRecord colony, ColonyCommand command, ColonyEnvironment env)
        {
            var route = env.People.Routes.SingleOrDefault(x => x.Id == Field(command, "RouteId"));
            if (route == null || !route.Qualified || string.IsNullOrWhiteSpace(route.Evidence) || route.Body != colony.Site.Body || route.Hash != Field(command, "RouteHash"))
                throw new InvalidDataException("Passenger route or reviewed terms are unavailable or unqualified.");
            if (route.DevelopmentOnly && !env.DevelopmentMode) throw new InvalidDataException("Development transport is unavailable in normal career operation.");
            ColonyStateCodec.Funds(route.Fare); ColonyStateCodec.Funds(route.RecruitmentFee); ColonyStateCodec.Time(route.TravelSeconds);
            if (route.TravelSeconds <= 0 || route.ConcurrentSeats < 1 || route.ConcurrentSeats > 256) throw new InvalidDataException("Passenger route has invalid lead time or finite seat capacity.");
            if (state.PeopleOperations.Count(x => x.RouteId == route.Id && x.State != "complete" && x.State != "cancelled") >= route.ConcurrentSeats)
                throw new InvalidDataException("Paid passenger transport has no unreserved seat capacity.");
            return route;
        }

        static string RecruitResident(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId); var person = Person(env, Field(command, "RosterId"));
            if(IsRosterReservedForPlanning(state,person.RosterId)&&!IsOwnPlanningResidentCommand(state,command,person.RosterId))throw new InvalidDataException("This Kerbal is reserved for an approved named startup operation.");
            ProtectMissionCrew(person, command);
            if (person.Status != "Available" || person.Type != "Crew" && person.Type != "Applicant") throw new InvalidDataException("Recruitment requires an available named crew member or applicant.");
            if (state.Colonies.Any(x => x.Residents.Any(r => r.RosterId == person.RosterId)) || state.PeopleOperations.Any(x => x.RosterId == person.RosterId && x.State != "complete" && x.State != "cancelled"))
                throw new InvalidDataException("This roster identity is already a resident or reserved for transport.");
            int populationReservations=PlanningPopulationReservations(state,colony,person.RosterId);
            if (colony.Residents.Count+populationReservations >= colony.Charter.ResidentLimit || colony.Residents.Count+populationReservations >= colony.Charter.PopulationTarget)
                throw new InvalidDataException("Charter resident limit or approved population target is reached.");
            uint part = checked((uint)Integer(command, "HomePartId"));
            Home(state, colony, env, Field(command, "HomeFacilityId"), part, person.RosterId); SupportForAdmission(colony, 1, env);
            var route = Route(state, colony, command, env);
            long fare = checked(route.Fare + (person.Type == "Applicant" ? route.RecruitmentFee : 0));
            if (Integer(command, "QuotedFunds") != fare) throw new InvalidDataException("Reviewed passenger fare changed.");
            CheckFunds(state, colony, fare, env);
            colony.Residents.Add(new ColonyResident { Id = command.OperationId, RosterId = person.RosterId, Name = person.Name, Trait = person.Trait,
                HomeFacilityId = Field(command, "HomeFacilityId"), HomePartId = part, ArrivalOperationId = command.OperationId, Status = "arriving", LastReason = "Awaiting funded passenger transport." });
            var op = new ColonyPeopleOperation { Id = command.OperationId, ColonyId = colony.Id, RosterId = person.RosterId, HomeFacilityId = Field(command, "HomeFacilityId"), HomePartId = part,
                RouteId = route.Id, RouteHash = route.Hash, Funds = fare, TravelSeconds = route.TravelSeconds, ExpectedRosterType = person.Type };
            AddPeopleOperation(state, op); state.Effects.Add(FundsEffect(command, op.Id, -fare, "passengerFare"));
            Log(state, env.Ut, colony.Id, op.Id, "passengerReserved", "Named roster identity, certified home and paid finite route seat reserved."); return op.Id;
        }

        static string AssignResident(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId); var person = Person(env, Field(command, "RosterId")); ProtectMissionCrew(person, command);
            if(IsRosterReservedForPlanning(state,person.RosterId)&&!IsOwnPlanningResidentCommand(state,command,person.RosterId))throw new InvalidDataException("This Kerbal is reserved for an approved named startup operation.");
            if (person.Type != "Crew" || person.Status != "Assigned" || !env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id, out var present) || !present.Contains(person.RosterId))
                throw new InvalidDataException("Assignment requires ordinary crew physically witnessed at this colony.");
            var resident = colony.Residents.SingleOrDefault(x => x.RosterId == person.RosterId);
            if (state.Colonies.Any(x => x.Id != colony.Id && x.Residents.Any(r => r.RosterId == person.RosterId))) throw new InvalidDataException("Roster identity is assigned to another colony.");
            if(resident==null)
            {
                int populationReservations=PlanningPopulationReservations(state,colony,person.RosterId);
                if(colony.Residents.Count+populationReservations>=colony.Charter.ResidentLimit||colony.Residents.Count+populationReservations>=colony.Charter.PopulationTarget)throw new InvalidDataException("Approved resident target or ceiling is already occupied/reserved.");
                // A present visitor is already counted in the support union;
                // designation changes status, not physical population.
                SupportForAdmission(colony,0,env);
            }
            if (resident != null && resident.Status != "resident" && resident.Status != "missing") throw new InvalidDataException("Transporting people cannot be reassigned.");
            string homeId = Field(command, "HomeFacilityId"); uint homePart = checked((uint)Integer(command, "HomePartId"));
            Home(state, colony, env, homeId, homePart, person.RosterId);
            string job = Field(command, "JobFacilityId", ""); uint workPart = checked((uint)Integer(command, "WorkPartId", 0)); bool atWork = false;
            if (job.Length != 0)
            {
                var facility = colony.Facilities.SingleOrDefault(x => x.Id == job) ?? throw new InvalidDataException("Job facility is not registered.");
                if (facility.State != "operational" && facility.State != "commissioning" || !facility.PartIds.Contains(workPart)) throw new InvalidDataException("Job work part is not physically registered for commissioning or operation.");
                var workSeat=env.People.Seats.SingleOrDefault(s=>s.FacilityId==facility.Id && s.PartId==workPart && s.VesselId==facility.VesselId && s.ContextKey==env.ContextKey && s.Current && s.WorkSupported);
                if(workSeat==null || !workSeat.Occupants.Contains(person.RosterId))throw new InvalidDataException("Job requires a current actual occupied converter or workshop seat; an ordinary cabin is not a workplace.");
                if (facility.RequiredTrait.Length != 0 && facility.RequiredTrait != person.Trait) throw new InvalidDataException("Kerbal skill does not meet the job requirement.");
                if (colony.Residents.Count(x => x.RosterId != person.RosterId && x.JobFacilityId == job) >= facility.RequiredWorkers) throw new InvalidDataException("Job seats are already assigned.");
                atWork = person.VesselId == facility.VesselId && person.PartId == workPart;
                if (!atWork) throw new InvalidDataException("A job requires the qualified Kerbal to occupy its actual work part; a label cannot grant a module bonus.");
            }
            if (resident == null) { resident = new ColonyResident { Id = command.OperationId, RosterId = person.RosterId, Name = person.Name }; colony.Residents.Add(resident); }
            resident.Trait = person.Trait; resident.HomeFacilityId = homeId; resident.HomePartId = homePart; resident.JobFacilityId = job; resident.WorkPartId = workPart;
            resident.PhysicallyAtWork = atWork; resident.Status = "resident"; resident.LastReason = "Explicit assignment verified against actual roster and work part.";
            foreach (var c in state.Colonies) c.VisitorRosterIds.Remove(person.RosterId);
            Log(state, env.Ut, colony.Id, command.OperationId, "residentAssignment", resident.LastReason); return resident.Id;
        }

        static string DepartResident(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId); var resident = colony.Residents.SingleOrDefault(x => x.Id == command.TargetId) ?? throw new InvalidDataException("Resident is unavailable.");
            var person = Person(env, resident.RosterId); ProtectMissionCrew(person, command);
            if(IsRosterReservedForPlanning(state,person.RosterId))throw new InvalidDataException("This Kerbal is reserved for an approved startup worker shift.");
            if (resident.Status != "resident" || person.Status != "Assigned" || state.PeopleOperations.Any(x => x.RosterId == resident.RosterId && x.State != "complete" && x.State != "cancelled")) throw new InvalidDataException("Resident is not available for another departure.");
            var route = Route(state, colony, command, env); if (Integer(command, "QuotedFunds") != route.Fare) throw new InvalidDataException("Reviewed departure fare changed.");
            CheckFunds(state, colony, route.Fare, env);
            var op = new ColonyPeopleOperation { Id = command.OperationId, ColonyId = colony.Id, RosterId = resident.RosterId, Kind = "departure", HomeFacilityId = resident.HomeFacilityId, HomePartId = resident.HomePartId,
                RouteId = route.Id, RouteHash = route.Hash, Funds = route.Fare, TravelSeconds = route.TravelSeconds };
            AddPeopleOperation(state, op); resident.Status = "departing"; resident.PhysicallyAtWork = false; resident.LastReason = "Paid departure reserved; actual Kerbal retained until verified release.";
            state.Effects.Add(FundsEffect(command, op.Id, -route.Fare, "passengerFare")); return op.Id;
        }

        static void AddPeopleOperation(ColonyState state, ColonyPeopleOperation op)
        {
            while (state.PeopleOperations.Count >= 256)
            {
                // Approved planning claims retain their exact child lineage for
                // save/load validation. Capacity must hold rather than erase it.
                int terminal = state.PeopleOperations.FindIndex(x => (x.State == "complete" || x.State == "cancelled") && !state.Plans.Any(p=>p.Workers.Any(w=>w.OperationId==x.Id)||p.Residents.Any(r=>r.OperationId==x.Id)));
                if (terminal < 0) throw new InvalidDataException("Passenger operation capacity reached; complete or reconcile existing transport.");
                state.PeopleOperations.RemoveAt(terminal);
            }
            state.PeopleOperations.Add(op);
        }

        static string CancelPassenger(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var op = state.PeopleOperations.SingleOrDefault(x => x.Id == command.TargetId && x.ColonyId == command.ColonyId) ?? throw new InvalidDataException("Passenger reservation is unavailable.");
            if(op.Kind=="workerTransfer")throw new InvalidDataException("Use the worker-transfer cancellation action for an unstarted cabin shift.");
            if (op.State != "reserved" || op.FundsPaid) throw new InvalidDataException("Funded or started transport cannot be cancelled by an unverified refund; retain the actual Kerbal and reconcile its operation.");
            var colony = Colony(state, op.ColonyId); var resident = colony.Residents.Single(x => x.RosterId == op.RosterId);
            if (op.Kind == "arrival") colony.Residents.Remove(resident);
            else { resident.Status = "resident"; resident.LastReason = "Unpaid departure cancelled; actual crew record and home retained."; }
            foreach (var effect in state.Effects.Where(x => x.TargetId == op.Id && x.State == "prepared")) effect.State = "cancelled";
            op.State = "cancelled"; op.Reason = "Unpaid reservation released; no roster or funds mutation was performed.";
            Log(state, env.Ut, colony.Id, command.OperationId, "passengerCancelled", op.Reason); return op.Id;
        }

        static bool CompletePeopleFundsEffect(ColonyState state, ColonyEffect effect, double ut)
        {
            if (effect.Kind != "passengerFare") return false;
            var op = state.PeopleOperations.Single(x => x.Id == effect.TargetId);
            if (op.State != "reserved" || op.FundsPaid || effect.FundsDelta != -op.Funds) throw new InvalidDataException("Passenger fare is not reserved exactly once.");
            op.FundsPaid = true; op.DepartUt = ut; op.ArrivalUt = ut + op.TravelSeconds; op.State = "inTransit"; op.Reason = "Paid modeled transport in progress; arrival requires actual roster and home-seat readback.";
            Colony(state, op.ColonyId).SpentFunds = checked(Colony(state, op.ColonyId).SpentFunds + op.Funds); return true;
        }

        static double NextPeopleEvent(ColonyState state, double target) => state.PeopleOperations.Where(x => x.State == "inTransit").Aggregate(target, (next, op) => Math.Min(next, Math.Max(state.SimulatedUt, op.ArrivalUt)));

        static void AdvancePeople(ColonyState state, ColonyEnvironment env, double ut)
        {
            foreach (var op in state.PeopleOperations.Where(x => x.State == "inTransit" && x.ArrivalUt <= ut))
            {
                op.State = "awaitingArrival"; op.Reason = op.Kind == "arrival" ? "Passenger lead time complete; waiting for a certified loaded home and real vacant seat." : "Departure lead time complete; waiting for verified physical crew release.";
                state.Effects.Add(new ColonyEffect { Id = Guid.NewGuid().ToString("D"), OperationId = op.Id, ColonyId = op.ColonyId, TargetId = op.Id, Kind = op.Kind == "arrival" ? "peopleArrival" : "peopleDeparture" });
                Log(state, ut, op.ColonyId, op.Id, "passengerReady", op.Reason); state.Revision++;
            }
            // Missing is a fresh negative roster witness, never the absence of an
            // unloaded physical observation. Unknown/stale preserves identities.
            foreach (var colony in state.Colonies)
            {
                foreach (var resident in colony.Residents.Where(x => x.Status == "resident" || x.Status == "missing"))
                {
                    var person = env.People.Roster.SingleOrDefault(x => x.RosterId == resident.RosterId && x.Current && x.ContextKey == env.ContextKey);
                    if (person == null) { resident.PhysicallyAtWork = false; resident.LastReason = "Current roster witness unavailable; identity and home retained."; continue; }
                    bool missing = person.Status == "Missing" || person.Status == "Dead" || person.Status == "Absent";
                    resident.Trait = person.Trait;
                    resident.Status = missing ? "missing" : "resident";
                    var job = colony.Facilities.SingleOrDefault(x => x.Id == resident.JobFacilityId);
                    resident.PhysicallyAtWork = !missing && job != null && person.Type=="Crew" && person.Status == "Assigned" && person.VesselId == job.VesselId && person.PartId == resident.WorkPartId && (job.RequiredTrait.Length == 0 || person.Trait == job.RequiredTrait) &&
                        env.People.Seats.Any(s=>s.Current && s.WorkSupported && s.ContextKey==env.ContextKey && s.FacilityId==job.Id && s.VesselId==job.VesselId && s.PartId==resident.WorkPartId && s.Occupants.Contains(person.RosterId));
                    resident.LastReason = missing ? "Roster is missing/dead/absent; no automatic replacement or death is performed." : resident.JobFacilityId.Length == 0 ? "Resident; no job assigned." : resident.PhysicallyAtWork ? "Qualified Kerbal occupies the required work part." : "Job bonus unavailable: Kerbal is not witnessed at the required work part.";
                }
                if (env.People.PresenceComplete && env.People.PresentByColony.TryGetValue(colony.Id, out var present))
                    colony.VisitorRosterIds = present.Distinct(StringComparer.Ordinal).Where(id => !state.Colonies.Any(c => c.Residents.Any(r => r.RosterId == id))).ToList();
            }
        }

        public static ColonyState PreparePeopleEffect(ColonyState prior, string effectId, ColonyEnvironment env, string beforeWitness)
        {
            ValidateEnvironment(prior, env); var state = ColonyStateCodec.Copy(prior); var effect = state.Effects.Single(x => x.Id == effectId);
            var op = state.PeopleOperations.Single(x => x.Id == effect.TargetId); var colony = Colony(state, op.ColonyId);
            if (effect.State != "prepared" || op.State != "awaitingArrival" || !op.FundsPaid || env.Ut < op.ArrivalUt || string.IsNullOrWhiteSpace(beforeWitness)) throw new InvalidDataException("Passenger effect is not ready for a witnessed physical transition.");
            var person = Person(env, op.RosterId);
            if (op.Kind == "arrival")
            {
                if (person.Status != "Available" || person.Type != op.ExpectedRosterType) throw new InvalidDataException("Reserved roster identity changed before arrival.");
                var seat = Home(state, colony, env, op.HomeFacilityId, op.HomePartId, op.RosterId);
                if (!seat.CrewMutationSupported || !seat.UtilitiesQualified) throw new InvalidDataException("Arrival requires a qualified crew-mapping provider, certified utilities and housing; passenger remains queued.");
                SupportForAdmission(colony, 0, env);
            }
            else
            {
                if (person.Status != "Assigned" || !env.People.PresenceComplete || !env.People.PresentByColony.TryGetValue(colony.Id, out var present) || !present.Contains(op.RosterId)) throw new InvalidDataException("Departing resident is not freshly witnessed at this site.");
                var seat = env.People.Seats.SingleOrDefault(x => x.VesselId == person.VesselId && x.PartId == person.PartId);
                if (seat == null || !seat.Current || !seat.CrewMutationSupported) throw new InvalidDataException("Departure requires a qualified source part/proto crew-mapping provider.");
            }
            effect.State = "applying"; effect.Provider = "KSP.RosterAndPartCrew"; effect.BeforeWitness = beforeWitness; op.State = "applying"; op.Provider = effect.Provider; op.BeforeWitness = beforeWitness;
            state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }

        public static ColonyState CompletePeopleEffect(ColonyState prior, string effectId, ColonyEnvironment after, string witness)
        {
            ValidateEnvironment(prior, after); var state = ColonyStateCodec.Copy(prior); var effect = state.Effects.Single(x => x.Id == effectId);
            if (effect.State == "applied") return prior;
            var op = state.PeopleOperations.Single(x => x.Id == effect.TargetId); var colony = Colony(state, op.ColonyId);
            if (effect.State != "applying" || op.State != "applying" || string.IsNullOrWhiteSpace(witness)) throw new InvalidDataException("Passenger transition lacks a same-context before/after witness.");
            var person = Person(after, op.RosterId);
            var resident = colony.Residents.Single(x => x.RosterId == op.RosterId);
            if (op.Kind == "arrival")
            {
                var home = colony.Facilities.Single(x => x.Id == op.HomeFacilityId);
                var seat = after.People.Seats.SingleOrDefault(x => x.FacilityId == home.Id && x.PartId == op.HomePartId && x.Current && x.ContextKey == after.ContextKey);
                if (person.Type != "Crew" || person.Status != "Assigned" || person.VesselId != home.VesselId || person.PartId != op.HomePartId || seat == null || seat.Occupants.Count(x => x == person.RosterId) != 1)
                    throw new InvalidDataException("Actual roster/home seat readback does not prove exactly one arrival.");
                resident.Status = "resident"; resident.LastReason = "Paid passenger arrived in the certified physical home; roster readback verified.";
                foreach (var c in state.Colonies) c.VisitorRosterIds.Remove(person.RosterId);
            }
            else
            {
                if (person.Type != "Crew" || person.Status != "Available" || person.VesselId.Length != 0 || after.People.Seats.Any(x => x.Occupants.Contains(person.RosterId))) throw new InvalidDataException("Departure readback has not released exactly one actual crew record.");
                colony.Residents.Remove(resident); foreach (var c in state.Colonies) c.VisitorRosterIds.Remove(person.RosterId);
            }
            op.State = "complete"; op.AfterWitness = witness; op.Reason = "Verified actual crew readback; operation will not repeat.";
            effect.State = "applied"; effect.AfterWitness = witness; effect.Reason = op.Reason;
            Log(state, after.Ut, colony.Id, op.Id, op.Kind == "arrival" ? "residentArrival" : "residentDeparture", op.Reason); state.Revision++; ColonyStateCodec.Serialize(state); return state;
        }
    }
}
