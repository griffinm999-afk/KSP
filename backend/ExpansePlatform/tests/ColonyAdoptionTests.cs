using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

// Constructed domain observations; these tests do not qualify the KSP adapter.
public sealed class ColonyAdoptionTests
{
    static string Id() => Guid.NewGuid().ToString("D");
    static (ColonyState State, ColonyRecord Colony, ColonyFacility Candidate, ColonyEnvironment Env) Fixture(bool commissioned = false)
    {
        var state = ColonyEngine.Create(Id(), 100);
        var colony = new ColonyRecord { Id = Id(), Name = "Existing colony", FoundedUt = 50, SupportAccountedUt = 100,
            Site = new ColonySite { Body = "Duna", Latitude = 0, Longitude = 0, RadiusMeters = 400 },
            Charter = new ColonyCharter { PopulationTarget = 2, ResidentLimit = 8, VisitorLimit = 8 },
            SupportCommissionedUt = commissioned ? 100 : null };
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 100_000_000, ImportedAmount = 100_000_000, Capacity = 200_000_000 });
        state.Colonies.Add(colony);
        var candidate = new ColonyFacility { Id = Id(), VesselId = Id(), Name = "Actual MKS workshop", PartIds = [11, 22],
            Qualification = new ColonyQualification { Context = "loaded-packed", ObservedUt = 100 } };
        var env = new ColonyEnvironment { ContextKey = "selected-save/epoch", WorldId = state.WorldId, Ut = 100, AvailableFunds = 2_000_000 };
        env.AdoptableFacilities.Add(candidate); env.FacilitySites[candidate.Id] = new ColonySite { Body = "Duna", Latitude = 0, Longitude = .01 };
        env.BodyRadiiMeters["Duna"] = 320_000; env.People.PresenceComplete = true; env.People.PresentByColony[colony.Id] = [];
        return (state, colony, candidate, env);
    }
    static ColonyCommand Command(ColonyState state, ColonyRecord colony, ColonyFacility candidate, ColonyEnvironment env)
    {
        var quote = ColonyEngine.QuoteFacilityAdoption(state, colony.Id, candidate.Id, env);
        return new ColonyCommand { OperationId = Id(), Kind = "adoptFacility", ColonyId = colony.Id, TargetId = candidate.Id,
            ExpectedRevision = state.Revision, ContextKey = env.ContextKey, QuoteId = quote.Id,
            Fields = new() { ["AdoptionWitnessHash"] = quote.AdoptionWitnessHash } };
    }
    static void Crew(ColonyEnvironment env, ColonyRecord colony, ColonyFacility candidate, string name = "OrdinaryVisitor")
    {
        env.People.Roster.Add(new ColonyRosterWitness { RosterId = name, Name = name, Trait = "Engineer", Type = "Crew", Status = "Assigned", Current = true,
            ContextKey = env.ContextKey, VesselId = candidate.VesselId, PartId = 11, ObservedUt = env.Ut });
        env.People.Seats.Add(new ColonySeatWitness { VesselId = candidate.VesselId, PartId = 11, Current = true, ContextKey = env.ContextKey, Occupants = [name], Capacity = 2 });
        env.People.PresentByColony[colony.Id].Add(name);
    }
    [Fact]
    public void AdoptionRegistersOnlyPhysicalIdentityAndDetachesObservation()
    {
        var (state, colony, candidate, env) = Fixture(); Crew(env, colony, candidate);
        candidate.CertifiedHomes = 40; candidate.RequiredWorkers = 8; candidate.Qualification.HousingCertified = true;
        var before = ColonyStateCodec.Serialize(state); var result = ColonyEngine.Execute(state, Command(state, colony, candidate, env), env);
        Assert.Equal("accepted", result.Outcome); Assert.Equal(before, ColonyStateCodec.Serialize(state));
        var actual = Assert.Single(result.State.Colonies.Single().Facilities);
        Assert.Equal(candidate.PartIds, actual.PartIds); Assert.Equal("physical", actual.ProductionOwner); Assert.Equal("adopted", actual.State);
        Assert.Equal(0, actual.CertifiedHomes); Assert.False(actual.Qualification.HousingCertified); Assert.Equal(0, actual.RequiredWorkers);
        Assert.Empty(result.State.Effects); Assert.Empty(result.State.Colonies.Single().Residents); Assert.Empty(result.State.Colonies.Single().VisitorRosterIds);
        Assert.Equal(100_000_000, result.State.Colonies.Single().Stock.Single().Amount); Assert.Equal(100_000_000, result.State.Colonies.Single().Stock.Single().ImportedAmount);
        Assert.Equal(0, result.State.Colonies.Single().SpentFunds); Assert.Equal(2_000_000, env.AvailableFunds);
        candidate.PartIds[0] = 99; candidate.Qualification.Context = "bad"; Assert.Equal(11u, actual.PartIds[0]); Assert.Equal("loaded-packed", actual.Qualification.Context);
        Assert.Equal("Assigned", env.People.Roster.Single().Status); Assert.Equal("Crew", env.People.Roster.Single().Type);
    }
    [Theory]
    [InlineData("geo")]
    [InlineData("members")]
    [InlineData("name")]
    [InlineData("vessel")]
    [InlineData("context")]
    [InlineData("revision")]
    public void ReviewedIdentityAndContextChangesReject(string change)
    {
        var (state, colony, candidate, env) = Fixture(); var command = Command(state, colony, candidate, env);
        switch (change) { case "geo": env.FacilitySites[candidate.Id].Longitude += .00001; break; case "members": candidate.PartIds.Add(33); break;
            case "name": candidate.Name += " changed"; break; case "vessel": candidate.VesselId = Id(); break; case "context": env.ContextKey += "-new"; break; case "revision": state.Revision++; break; }
        Assert.Equal("rejected", ColonyEngine.Execute(state, command, env).Outcome); Assert.Empty(colony.Facilities);
    }
    [Theory]
    [InlineData("part")]
    [InlineData("vessel")]
    [InlineData("id")]
    public void AnyExistingColonyOwnershipRejects(string overlap)
    {
        var (state, colony, candidate, env) = Fixture();
        var other = new ColonyRecord { Id = Id(), Name = "Other colony" };
        other.Facilities.Add(new ColonyFacility { Id = overlap == "id" ? candidate.Id : Id(), VesselId = overlap == "vessel" ? candidate.VesselId : Id(), PartIds = overlap == "part" ? [22] : [44] });
        state.Colonies.Add(other); Assert.False(ColonyEngine.QuoteFacilityAdoption(state, colony.Id, candidate.Id, env).CanApprove);
    }
    [Fact]
    public void ExactOperationReplayCannotRepeatOrChangeItsTarget()
    {
        var (state, colony, candidate, env) = Fixture(); var command = Command(state, colony, candidate, env);
        var first = ColonyEngine.Execute(state, command, env); Assert.Equal("accepted", first.Outcome);
        Assert.Equal("duplicate", ColonyEngine.Execute(first.State, command, env).Outcome);
        command.TargetId = Id(); Assert.Equal("rejected", ColonyEngine.Execute(first.State, command, env).Outcome);
        Assert.Single(first.State.Colonies.Single().Facilities);
    }
    [Fact]
    public void NewCurrentVisitorWaitsForNormalSupportChronologyAndCreatesNoRetroactiveBurn()
    {
        var (state, colony, candidate, env) = Fixture(true); Crew(env, colony, candidate); env.Ut = 100 + ColonyLimits.KerbinDay;
        candidate.Qualification.ObservedUt = env.Ut;
        var rejected = ColonyEngine.Execute(state, Command(state, colony, candidate, env), env);
        Assert.Equal("rejected", rejected.Outcome); Assert.Contains("support reconciliation", rejected.Reason);
        Assert.Equal(100_000_000, colony.Stock.Single().Amount); Assert.Equal(100, colony.SupportAccountedUt);
        var reconciled = ColonyEngine.Advance(state, env);
        Assert.Equal(100_000_000, reconciled.Colonies.Single().Stock.Single().Amount); Assert.Equal("OrdinaryVisitor", Assert.Single(reconciled.Colonies.Single().VisitorRosterIds));
        var accepted = ColonyEngine.Execute(reconciled, Command(reconciled, reconciled.Colonies.Single(), candidate, env), env);
        Assert.Equal("accepted", accepted.Outcome); Assert.Equal(env.Ut, accepted.State.Colonies.Single().SupportAccountedUt);
        env.Ut += ColonyLimits.KerbinDay;
        Assert.Equal(99_000_000, ColonyEngine.Advance(accepted.State, env).Colonies.Single().Stock.Single().Amount);
    }
    [Theory]
    [InlineData("incomplete")]
    [InlineData("duplicate cabin")]
    [InlineData("other visitor")]
    [InlineData("limit")]
    [InlineData("stale")]
    [InlineData("part zero")]
    [InlineData("part duplicate")]
    [InlineData("outside")]
    [InlineData("body")]
    public void UnsafeCurrentProviderOrPopulationRejects(string failure)
    {
        var (state, colony, candidate, env) = Fixture(); Crew(env, colony, candidate);
        switch (failure) { case "incomplete": env.People.PresenceComplete = false; break;
            case "duplicate cabin": env.People.Seats.Add(env.People.Seats.Single()); break;
            case "other visitor": state.Colonies.Add(new ColonyRecord { Id = Id(), Name = "Other", VisitorRosterIds = ["OrdinaryVisitor"] }); break;
            case "limit": colony.Charter.VisitorLimit = 0; break; case "stale": candidate.Qualification.ObservedUt = 80; break;
            case "part zero": candidate.PartIds.Add(0); break; case "part duplicate": candidate.PartIds.Add(11); break;
            case "outside": env.FacilitySites[candidate.Id].Longitude = 2; break; case "body": env.FacilitySites[candidate.Id].Body = "Mun"; break; }
        Assert.False(ColonyEngine.QuoteFacilityAdoption(state, colony.Id, candidate.Id, env).CanApprove); Assert.Empty(colony.Facilities);
    }
    [Fact]
    public void WitnessIgnoresObservationTimeButBindsUniquePartSetAndSite()
    {
        var (_, _, candidate, env) = Fixture(); var site = env.FacilitySites[candidate.Id]; var hash = ColonyEngine.AdoptionWitnessHash(candidate, site);
        candidate.PartIds.Reverse(); candidate.Qualification.ObservedUt++; Assert.Equal(hash, ColonyEngine.AdoptionWitnessHash(candidate, site));
        site.Latitude += .000001; Assert.NotEqual(hash, ColonyEngine.AdoptionWitnessHash(candidate, site));
    }
    [Fact]
    public void ExtraAuthorityFieldsCannotAssignHomeOrCreditResource()
    {
        var (state, colony, candidate, env) = Fixture(); var command = Command(state, colony, candidate, env); command.Fields["CertifiedHomes"] = "2";
        Assert.Equal("rejected", ColonyEngine.Execute(state, command, env).Outcome);
    }
    [Fact]
    public void RecordedCoordinateBitsHavePinnedTransientWitness()
    {
        var (_, _, candidate, env) = Fixture();
        candidate.Id = "11111111-1111-1111-1111-111111111111"; candidate.VesselId = "22222222-2222-2222-2222-222222222222";
        candidate.Name = "Recorded coordinates";
        var site = new ColonySite { Body = "Minmus", Biome = "Greater Flats",
            Latitude = BitConverter.Int64BitsToDouble(4607167183711359775), Longitude = BitConverter.Int64BitsToDouble(4607161497937331200) };
        // Framework R text uses ...507506; modern R uses ...50751 for these same bits.
        Assert.Equal("e4443ffdcadfff67b31881af21c331e62d474def7ea9a4923da15baa73eba371", ColonyEngine.AdoptionWitnessHash(candidate, site));
        site.Longitude = BitConverter.Int64BitsToDouble(4607161497937331201);
        Assert.NotEqual("e4443ffdcadfff67b31881af21c331e62d474def7ea9a4923da15baa73eba371", ColonyEngine.AdoptionWitnessHash(candidate, site));
    }
    [Fact]
    public void SignedZeroDoesNotInvalidateAdoptionReview()
    {
        var (state, colony, candidate, env) = Fixture(); var command = Command(state, colony, candidate, env);
        colony.Site.Latitude = BitConverter.Int64BitsToDouble(long.MinValue);
        env.FacilitySites[candidate.Id].Latitude = colony.Site.Latitude;
        Assert.Equal("accepted", ColonyEngine.Execute(state, command, env).Outcome);
    }
    [Theory]
    [InlineData("Purpose")][InlineData("PopulationTarget")][InlineData("ResidentLimit")][InlineData("VisitorLimit")]
    [InlineData("FoundingBudget")][InlineData("CashFloor")][InlineData("SpendingLimit")][InlineData("ReserveDays")]
    [InlineData("GrowthPolicy")][InlineData("Sandbox")]
    public void EveryReviewedCharterTermStillRejectsAChangedApproval(string term)
    {
        var (state, colony, candidate, env) = Fixture();
        colony.Charter.ReserveDays = 6.123456789012345;
        var command = Command(state, colony, candidate, env);
        switch (term)
        {
            case "Purpose": colony.Charter.Purpose += " changed"; break;
            case "PopulationTarget": colony.Charter.PopulationTarget++; break;
            case "ResidentLimit": colony.Charter.ResidentLimit++; break;
            case "VisitorLimit": colony.Charter.VisitorLimit++; break;
            case "FoundingBudget": colony.Charter.FoundingBudget++; break;
            case "CashFloor": colony.Charter.CashFloor++; break;
            case "SpendingLimit": colony.Charter.SpendingLimit++; break;
            case "ReserveDays": colony.Charter.ReserveDays = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(colony.Charter.ReserveDays) + 1); break;
            case "GrowthPolicy": colony.Charter.GrowthPolicy = "automatic"; break;
            case "Sandbox": colony.Charter.Sandbox = true; break;
        }
        var before = ColonyStateCodec.Serialize(state);
        Assert.Equal("rejected", ColonyEngine.Execute(state, command, env).Outcome);
        Assert.Equal(before, ColonyStateCodec.Serialize(state)); Assert.Empty(colony.Facilities);
    }
    [Theory]
    [InlineData("candidate latitude")][InlineData("candidate longitude")][InlineData("candidate radius")]
    [InlineData("colony latitude")][InlineData("colony longitude")][InlineData("colony radius")]
    public void ExactSiteBitsStillRejectEvenOneUlpOfChange(string term)
    {
        var (state, colony, candidate, env) = Fixture(); var site = env.FacilitySites[candidate.Id];
        colony.Site.Latitude = site.Latitude = BitConverter.Int64BitsToDouble(4607167183711359775);
        colony.Site.Longitude = site.Longitude = BitConverter.Int64BitsToDouble(4607161497937331200);
        var command = Command(state, colony, candidate, env);
        switch (term)
        {
            case "candidate latitude": site.Latitude = Math.BitIncrement(site.Latitude); break;
            case "candidate longitude": site.Longitude = Math.BitIncrement(site.Longitude); break;
            case "candidate radius": site.RadiusMeters = Math.BitIncrement(site.RadiusMeters); break;
            case "colony latitude": colony.Site.Latitude = Math.BitIncrement(colony.Site.Latitude); break;
            case "colony longitude": colony.Site.Longitude = Math.BitIncrement(colony.Site.Longitude); break;
            case "colony radius": colony.Site.RadiusMeters = Math.BitIncrement(colony.Site.RadiusMeters); break;
        }
        Assert.Equal("rejected", ColonyEngine.Execute(state, command, env).Outcome);
    }
}
