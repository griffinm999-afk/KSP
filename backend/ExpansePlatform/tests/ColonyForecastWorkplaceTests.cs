using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public class ColonyForecastWorkplaceTests
{
    static (ColonyState State, ColonyEnvironment Env, string Colony, ColonyFacility Work, ColonySeatWitness Seat) Fixture()
    {
        var (state, env, id) = ColonyPlanningTests.GrowthFixture();
        var work = state.Colonies.Single().Facilities.Single(f => f.RequiredWorkers > 0);
        work.PartIds = new() { 888 };
        env.People.Seats.RemoveAll(s => s.FacilityId == work.Id);
        var seat = new ColonySeatWitness { FacilityId = work.Id, VesselId = work.VesselId, PartId = 888,
            Capacity = 4, Current = true, WorkSupported = true, UtilitiesQualified = true, ContextKey = env.ContextKey };
        env.People.Seats.Add(seat);
        return (state, env, id, work, seat);
    }

    static void Occupy(ColonyEnvironment env, ColonySeatWitness seat, string name, string trait = "Engineer")
    {
        seat.Occupants.Add(name);
        env.People.Roster.Add(new() { RosterId = name, Name = name, Trait = trait, Type = "Crew", Status = "Assigned",
            VesselId = seat.VesselId, PartId = seat.PartId, Current = true, ContextKey = env.ContextKey, ObservedUt = env.Ut });
    }

    [Fact] public void RealVisitorWorkersFillJobsWithoutBecomingResidents()
    {
        var (state, env, id, work, seat) = Fixture();
        for (int i = 0; i < 4; i++) Occupy(env, seat, "Visitor " + i);
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        Assert.Equal(2, state.Colonies.Single().Residents.Count);
        Assert.DoesNotContain(state.Colonies.Single().Residents, r => r.JobFacilityId == work.Id);
    }

    [Fact] public void RemoteJobLabelsDoNotSupplyActualWorkplaceLabor()
    {
        var (state, env, id, work, seat) = Fixture();
        foreach (var resident in state.Colonies.Single().Residents) resident.JobFacilityId = work.Id;
        Assert.Equal(4, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        Occupy(env, seat, "Actual visitor");
        Assert.Equal(3, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
    }

    [Fact] public void UnverifiedOccupancyDoesNotBecomeDemandForMorePeople()
    {
        var (state, env, id, _, seat) = Fixture();
        seat.Occupants.Add("Unresolved occupant");
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        seat.Occupants.Clear(); Occupy(env, seat, "Stale visitor"); env.People.Roster.Last().ObservedUt -= 11;
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        env.People.Roster.Last().ObservedUt = env.Ut; seat.WorkSupported = false;
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
    }

    [Fact] public void FullCabinDoesNotPromisePlacesForIncomingWorkers()
    {
        var (state, env, id, _, seat) = Fixture();
        for (int i = 0; i < 4; i++) Occupy(env, seat, "Pilot " + i, "Pilot");
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        seat.Occupants.RemoveAt(3); env.People.Roster.RemoveAt(env.People.Roster.Count - 1);
        Assert.Equal(1, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
        env.People.PresenceComplete = false;
        Assert.Equal(0, ColonyEngine.Forecast(state, id, env).OpenQualifiedJobs);
    }
}
