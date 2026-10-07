using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyPeopleInvariantTests
{
    [Fact]
    public void RecruitmentReservesOneActualIdentityHomeAndFinitePaidSeat()
    {
        var (state, env, colony, home) = Fixture();
        var first = Recruit(state, env, colony, home, "Ada Kerman"); Assert.Equal("accepted", first.Outcome);
        Assert.Single(first.State.Residents()); Assert.Single(first.State.PeopleOperations);
        Assert.Equal("Available", env.People.Roster[0].Status); // pure domain does not mutate actual crew
        Assert.Equal(3000, ColonyEngine.CommittedFunds(first.State, colony.Id));
        var duplicate = ColonyEngine.Execute(first.State, Command(first.State, env, colony, home, "Ada Kerman", first.ResultId), env);
        // Retry uses the original revision, not the latest revision.
        var retry = Command(state, env, colony, home, "Ada Kerman", first.ResultId);
        Assert.Equal("duplicate", ColonyEngine.Execute(first.State, retry, env).Outcome);
        Assert.Equal("rejected", duplicate.Outcome);
        var another = Recruit(first.State, env, colony, home, "Grace Kerman");
        Assert.Equal("rejected", another.Outcome); Assert.Contains("seat capacity", another.Reason);
        Assert.Single(first.State.Residents());
    }

    [Fact]
    public void MissingSupportFullPhysicalSeatsAndProtectedMissionCrewCannotBeAdmitted()
    {
        var (state, env, colony, home) = Fixture();
        colony.Stock[0].Amount = 0;
        Assert.Contains("reserves", Recruit(state, env, colony, home, "Ada Kerman").Reason);
        colony.Stock[0].Amount = 100 * ColonyLimits.Units;
        env.People.Seats[0].Occupants.AddRange(new[] { "Visitor 1", "Visitor 2" });
        Assert.Contains("seats", Recruit(state, env, colony, home, "Ada Kerman").Reason);
        env.People.Seats[0].Occupants.Clear(); env.People.Roster[0].ProtectedMissionCrew = true;
        Assert.Contains("explicit", Recruit(state, env, colony, home, "Ada Kerman").Reason);
        Assert.Empty(state.Residents()); Assert.Empty(state.PeopleOperations); Assert.Empty(state.Effects);
    }

    [Fact]
    public void ArrivalConsumesOnlyOldPopulationIntervalAndRequiresPhysicalReadbackExactlyOnce()
    {
        var (state, env, colony, home) = Fixture();
        var accepted = Recruit(state, env, colony, home, "Ada Kerman").State;
        var fare = accepted.Effects.Single();
        var applying = ColonyEngine.MarkEffectApplying(accepted, fare.Id, "KSP.Funding", "before=1000000");
        var paid = ColonyEngine.CompleteFundsEffect(applying, fare.Id, 1_000_000, 997_000, 0, "after=997000");
        env.Ut = 100; var ready = ColonyEngine.Advance(paid, env);
        Assert.Equal("awaitingArrival", ready.PeopleOperations[0].State);
        Assert.Equal(100 * ColonyLimits.Units, ready.Colonies[0].Stock[0].Amount); // arriving not yet present
        var effect = ready.Effects.Single(x => x.Kind == "peopleArrival");
        var crewApplying = ColonyEngine.PreparePeopleEffect(ready, effect.Id, env, "actual Applicant Available; memberships=0");
        Assert.Throws<InvalidDataException>(() => ColonyEngine.CompletePeopleEffect(crewApplying, effect.Id, env, "unproven"));
        env.People.Roster[0].Type = "Crew"; env.People.Roster[0].Status = "Assigned"; env.People.Roster[0].VesselId = home.VesselId; env.People.Roster[0].PartId = 42;
        env.People.Seats[0].Occupants.Add("Ada Kerman"); env.People.PresentByColony[colony.Id].Add("Ada Kerman");
        var complete = ColonyEngine.CompletePeopleEffect(crewApplying, effect.Id, env, "actual Crew Assigned; memberships=1; part=42");
        Assert.Equal("resident", complete.Colonies[0].Residents[0].Status);
        Assert.Equal("complete", complete.PeopleOperations[0].State);
        Assert.Same(complete, ColonyEngine.CompletePeopleEffect(complete, effect.Id, env, "retry"));
        env.Ut = 100 + ColonyLimits.KerbinDay;
        var day = ColonyEngine.Advance(complete, env);
        Assert.Equal(99 * ColonyLimits.Units, day.Colonies[0].Stock[0].Amount);
        Assert.Empty(day.Colonies[0].VisitorRosterIds); Assert.Equal(3000, day.Colonies[0].SpentFunds);
    }

    [Fact]
    public void JobLabelCannotGrantBonusAndVisitorsAreNotDuplicatedByExplicitResidentAssignment()
    {
        var (state, env, colony, home) = Fixture();
        env.People.Roster[0].Type = "Crew"; env.People.Roster[0].Status = "Assigned"; env.People.Roster[0].VesselId = home.VesselId; env.People.Roster[0].PartId = 42;
        env.People.PresentByColony[colony.Id].Add("Ada Kerman"); env.People.Seats[0].Occupants.Add("Ada Kerman"); colony.VisitorRosterIds.Add("Ada Kerman");
        home.RequiredWorkers = 1; home.RequiredTrait = "Engineer";
        env.People.Seats[0].WorkSupported=true;
        var request = Command(state, env, colony, home, "Ada Kerman"); request.Kind = "assignResident";
        request.Fields["JobFacilityId"] = home.Id; request.Fields["WorkPartId"] = "43";
        Assert.Equal("rejected", ColonyEngine.Execute(state, request, env).Outcome);
        request.Fields["WorkPartId"] = "42";
        var assigned = ColonyEngine.Execute(state, request, env); Assert.Equal("accepted", assigned.Outcome);
        Assert.True(assigned.State.Colonies[0].Residents[0].PhysicallyAtWork); Assert.Empty(assigned.State.Colonies[0].VisitorRosterIds);
        env.People.Roster[0].PartId = 0; env.Ut = 1;
        var moved = ColonyEngine.Advance(assigned.State, env); Assert.False(moved.Colonies[0].Residents[0].PhysicallyAtWork);
        Assert.Equal("resident", moved.Colonies[0].Residents[0].Status);
        env.People.Roster[0].Status = "Missing"; env.Ut = 2;
        Assert.Equal("missing", ColonyEngine.Advance(moved, env).Colonies[0].Residents[0].Status);
        Assert.Equal("Missing", env.People.Roster[0].Status); // pure scheduler never changes actual roster witnesses
    }

    [Fact]
    public void CommissioningJobUsesRealOccupiedWorkplaceWithoutPromotingFacilityOrHome()
    {
        var (state,env,colony,home)=Fixture();
        var workshop=new ColonyFacility {Id=Guid.NewGuid().ToString("D"),VesselId=Guid.NewGuid().ToString("D"),Name="Anchored commissioning workshop",State="commissioning",PartIds=new(){43},RequiredWorkers=1,RequiredTrait="Engineer"};
        colony.Facilities.Add(workshop);
        var person=env.People.Roster[0];person.Type="Crew";person.Status="Assigned";person.VesselId=workshop.VesselId;person.PartId=43;
        env.People.PresentByColony[colony.Id].Add(person.RosterId);
        var seat=new ColonySeatWitness {FacilityId=workshop.Id,VesselId=workshop.VesselId,PartId=43,Capacity=1,Current=true,ContextKey=env.ContextKey,Occupants=new(){person.RosterId},WorkSupported=true};env.People.Seats.Add(seat);
        var request=Command(state,env,colony,home,person.RosterId);request.Kind="assignResident";request.Fields["JobFacilityId"]=workshop.Id;request.Fields["WorkPartId"]="43";
        var accepted=ColonyEngine.Execute(state,request,env);Assert.Equal("accepted",accepted.Outcome);
        Assert.True(accepted.State.Colonies[0].Residents[0].PhysicallyAtWork);Assert.Equal("commissioning",accepted.State.Colonies[0].Facilities[1].State);
        Assert.False(accepted.State.Colonies[0].Facilities[1].Qualification.PowerReliable);Assert.Equal(0,accepted.State.Colonies[0].Facilities[1].CertifiedHomes);
        seat.WorkSupported=false;Assert.Equal("rejected",ColonyEngine.Execute(state,request,env).Outcome);
        seat.WorkSupported=true;seat.ContextKey+="/stale";Assert.Equal("rejected",ColonyEngine.Execute(state,request,env).Outcome);
        seat.ContextKey=env.ContextKey;request.Fields["HomeFacilityId"]=workshop.Id;request.Fields["HomePartId"]="43";Assert.Equal("rejected",ColonyEngine.Execute(state,request,env).Outcome);
    }

    [Fact]
    public void PackedAndBackgroundAdmissionUseExplicitProviderQualificationAndPreserveWaitingPeople()
    {
        var (state, env, colony, home) = Fixture();
        var accepted = Recruit(state, env, colony, home, "Ada Kerman").State; var fare = accepted.Effects.Single();
        var paid = ColonyEngine.CompleteFundsEffect(ColonyEngine.MarkEffectApplying(accepted, fare.Id, "funds", "before"), fare.Id, 10000, 7000, 0, "after");
        env.Ut = 100; var ready = ColonyEngine.Advance(paid, env); var arrival = ready.Effects.Single(x => x.Kind == "peopleArrival");
        env.People.Seats[0].LoadedUnpacked = false; env.People.Seats[0].CrewMutationSupported = false;
        Assert.Throws<InvalidDataException>(() => ColonyEngine.PreparePeopleEffect(ready, arrival.Id, env, "before"));
        Assert.Single(ready.Residents()); Assert.Equal("Available", env.People.Roster[0].Status);
        env.People.Seats[0].CrewMutationSupported = true;
        Assert.Equal("applying", ColonyEngine.PreparePeopleEffect(ready, arrival.Id, env, "qualified packed/proto provider before").PeopleOperations[0].State);
    }

    [Fact]
    public void UnpaidCancellationPreservesApplicantAndPaidDepartureNeedsVerifiedActualRelease()
    {
        var (state, env, colony, home) = Fixture();
        var reservation = Recruit(state, env, colony, home, "Ada Kerman").State;
        var cancel = new ColonyCommand { OperationId=Guid.NewGuid().ToString("D"),ContextKey=env.ContextKey,ExpectedRevision=reservation.Revision,Kind="cancelPassenger",ColonyId=colony.Id,TargetId=reservation.PeopleOperations[0].Id };
        var cancelled=ColonyEngine.Execute(reservation,cancel,env); Assert.Equal("accepted",cancelled.Outcome); Assert.Empty(cancelled.State.Residents());
        Assert.Equal("Applicant",env.People.Roster[0].Type);Assert.Equal("Available",env.People.Roster[0].Status);Assert.Equal(0,ColonyEngine.CommittedFunds(cancelled.State,colony.Id));
        env.People.Roster[0].Type="Crew";env.People.Roster[0].Status="Assigned";env.People.Roster[0].VesselId=home.VesselId;env.People.Roster[0].PartId=42;
        env.People.Seats[0].Occupants.Add("Ada Kerman");env.People.PresentByColony[colony.Id].Add("Ada Kerman");
        var assign=Command(cancelled.State,env,colony,home,"Ada Kerman");assign.Kind="assignResident";
        var assigned=ColonyEngine.Execute(cancelled.State,assign,env).State;
        var departure=Command(assigned,env,colony,home,"Ada Kerman");departure.Kind="departResident";departure.TargetId=assigned.Colonies[0].Residents[0].Id;departure.Fields["QuotedFunds"]="1000";
        var reserved=ColonyEngine.Execute(assigned,departure,env);Assert.Equal("accepted",reserved.Outcome);
        var fare=reserved.State.Effects.Single(x=>x.State=="prepared");
        var paid=ColonyEngine.CompleteFundsEffect(ColonyEngine.MarkEffectApplying(reserved.State,fare.Id,"KSP.Funding","before"),fare.Id,10000,9000,0,"after");
        env.Ut=100;var ready=ColonyEngine.Advance(paid,env);Assert.Single(ready.Residents()); // modeled lead time never deletes crew
        var effect=ready.Effects.Single(x=>x.Kind=="peopleDeparture");var applying=ColonyEngine.PreparePeopleEffect(ready,effect.Id,env,"actual Assigned source seat");
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompletePeopleEffect(applying,effect.Id,env,"still seated"));
        env.People.Roster[0].Status="Available";env.People.Roster[0].VesselId="";env.People.Roster[0].PartId=0;env.People.Seats[0].Occupants.Clear();env.People.PresentByColony[colony.Id].Clear();
        var complete=ColonyEngine.CompletePeopleEffect(applying,effect.Id,env,"actual Available; memberships=0");Assert.Empty(complete.Residents());Assert.Equal("Crew",env.People.Roster[0].Type);Assert.Equal("Available",env.People.Roster[0].Status);
    }

    [Fact]
    public void SchemaOneWithoutPeopleFieldsMigratesWithoutDiscardingExistingPeople()
    {
        var (state, _, colony, home) = Fixture();
        colony.Residents.Add(new ColonyResident {Id=Guid.NewGuid().ToString("D"),RosterId="Existing Kerman",Name="Existing Kerman",HomeFacilityId=home.Id,HomePartId=42});
        string json = System.Text.Encoding.UTF8.GetString(ColonyStateCodec.Serialize(state));
        var node=System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node.AsObject().Remove("PeopleOperations");
        var person=node["Colonies"]![0]!["Residents"]![0]!.AsObject();
        person.Remove("HomePartId");person.Remove("ArrivalOperationId");person.Remove("LastReason");
        json=node.ToJsonString();
        var restored = ColonyStateCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Empty(restored.PeopleOperations); Assert.Equal(colony.Id, restored.Colonies.Single().Id);
        var existing=Assert.Single(restored.Residents());Assert.Equal("Existing Kerman",existing.RosterId);Assert.Equal(home.Id,existing.HomeFacilityId);Assert.Equal(0u,existing.HomePartId);Assert.Equal("",existing.LastReason);
    }

    static ColonyResult Recruit(ColonyState state, ColonyEnvironment env, ColonyRecord colony, ColonyFacility home, string name) => ColonyEngine.Execute(state, Command(state, env, colony, home, name), env);
    static ColonyCommand Command(ColonyState state, ColonyEnvironment env, ColonyRecord colony, ColonyFacility home, string name, string? id = null) => new()
    {
        OperationId = id ?? Guid.NewGuid().ToString("D"), ContextKey = env.ContextKey, ExpectedRevision = state.Revision, ColonyId = colony.Id, Kind = "recruitResident",
        Fields = new() { ["RosterId"] = name, ["HomeFacilityId"] = home.Id, ["HomePartId"] = "42", ["RouteId"] = "paid-modeled-test", ["RouteHash"] = new string('a',64), ["QuotedFunds"] = "3000" }
    };
    static (ColonyState state, ColonyEnvironment env, ColonyRecord colony, ColonyFacility home) Fixture()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 0);
        var home = new ColonyFacility { Id = Guid.NewGuid().ToString("D"), VesselId = Guid.NewGuid().ToString("D"), Name = "Certified home", State = "operational", CertifiedHomes = 2, PartIds = new() { 42 }, Qualification = new() { HousingCertified = true } };
        var colony = new ColonyRecord { Id = Guid.NewGuid().ToString("D"), Name = "People invariant fixture", Site = new() { Body = "Minmus" }, SupportCommissionedUt = 0,
            Stock = new() { new() { Resource = "Supplies", Amount = 100 * ColonyLimits.Units, Capacity = 1000 * ColonyLimits.Units } }, Facilities = new() { home } };
        state.Colonies.Add(colony);
        var env = new ColonyEnvironment { WorldId = state.WorldId, ContextKey = state.WorldId + "/" + Guid.NewGuid().ToString("D"), AvailableFunds = 1_000_000,
            Support = new() { Ready = true, PolicyId = "test-support", PolicyHash = "test-policy", MicroUnitsPerPersonDay = ColonyLimits.Units } };
        foreach (string name in new[] { "Ada Kerman", "Grace Kerman" }) env.People.Roster.Add(new() { RosterId = name, Name = name, Trait = "Engineer", Status = "Available", Type = "Applicant", Current = true, ContextKey = env.ContextKey });
        env.People.Seats.Add(new() { FacilityId = home.Id, VesselId = home.VesselId, PartId = 42, Capacity = 2, Current = true, ContextKey = env.ContextKey, HousingCertified = true, UtilitiesQualified = true, LoadedUnpacked = true, CrewMutationSupported = true });
        env.People.Routes.Add(new() { Id = "paid-modeled-test", Body = "Minmus", Hash = new string('a',64), Fare = 1000, RecruitmentFee = 2000, TravelSeconds = 100, ConcurrentSeats = 1, Qualified = true, Evidence = "Pure invariant fixture; not a runtime certificate" });
        env.People.PresenceComplete = true; env.People.PresentByColony[colony.Id] = new();
        return (state, env, colony, home);
    }
}

internal static class ColonyPeopleTestHelpers
{
    public static IEnumerable<ColonyResident> Residents(this ColonyState state) => state.Colonies.SelectMany(x => x.Residents);
}
