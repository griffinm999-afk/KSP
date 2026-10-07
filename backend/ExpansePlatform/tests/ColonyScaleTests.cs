using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Expanse.Domain.Colonies;
using Xunit.Abstractions;

namespace Expanse.Clock.Tests;

/// <summary>
/// Bounded model-scale tests. All entities are constructed unit fixtures, not KSP runtime evidence.
/// </summary>
public sealed class ColonyScaleTests(ITestOutputHelper output)
{
    const string World = "11f85341-03db-48f1-9e9b-c9e51e994150";
    const string Context = "scale-fixture";

    static string Id(int value) => $"00000000-0000-0000-0000-{value:D12}";

    static ColonyEnvironment Environment(ColonyState state, double ut) => new()
    {
        ContextKey = Context,
        WorldId = state.WorldId,
        Ut = ut,
        AvailableFunds = 10_000_000
    };

    [Fact]
    public void JournalCompactionChainsRemovedEntryAndPreservesRollingTail()
    {
        var state = ColonyEngine.Create(World, 0);
        for (var i = 1; i <= ColonyLimits.Journal; i++)
        {
            state.Journal.Add(new ColonyJournalEntry
            {
                Sequence = i,
                Ut = i,
                ColonyId = Id(10),
                OperationId = "",
                Kind = "fixture",
                Detail = $"entry-{i}",
                FundsDelta = 0,
                Resource = "",
                ResourceDelta = 0
            });
        }
        state.NextSequence = ColonyLimits.Journal + 1;
        var evicted = state.Journal[0];
        var command = new ColonyCommand
        {
            OperationId = Id(1), ContextKey = Context, ExpectedRevision = state.Revision,
            Kind = "foundColony"
        };
        command.Fields.Add("Name", "Compaction fixture");
        command.Fields.Add("Body", "Minmus");
        command.Fields.Add("Biome", "Greater Flats");
        command.Fields.Add("Latitude", "3");
        command.Fields.Add("Longitude", "3");
        command.Fields.Add("RadiusMeters", "400");
        var env = Environment(state, 0);
        env.BodyRadiiMeters.Add("Minmus", 60_000);

        var result = ColonyEngine.Execute(state, command, env);

        Assert.Equal("accepted", result.Outcome);
        Assert.Equal(1, result.State.CompactedJournalCount);
        Assert.Equal(ExpectedCheckpointHash("", evicted), result.State.CompactedJournalHash);
        Assert.Equal(ColonyLimits.Journal, result.State.Journal.Count);
        Assert.Equal(2, result.State.Journal[0].Sequence);
        Assert.Equal("founded", result.State.Journal[^1].Kind);
        Assert.Equal(ColonyLimits.Journal + 1, result.State.Journal[^1].Sequence);
        Assert.Equal(ColonyLimits.Journal + 3, result.State.NextSequence);
    }

    [Fact]
    public void HundredFacilityTwoHundredResidentStateConservesFractionalSupportAndReportsPayloadCost()
    {
        var catchUpState = BuildScaleState();
        var encodeTimer = Stopwatch.StartNew();
        var payload = ColonyStateCodec.Serialize(catchUpState);
        encodeTimer.Stop();

        var decodeTimer = Stopwatch.StartNew();
        var decoded = ColonyStateCodec.Deserialize(payload);
        decodeTimer.Stop();

        var catchUpTimer = Stopwatch.StartNew();
        var catchUp = ColonyEngine.Advance(decoded, Environment(decoded, 10_000));
        catchUpTimer.Stop();

        var steppedState = BuildScaleState();
        var steppedTimer = Stopwatch.StartNew();
        for (var step = 1; step <= 100; step++)
            steppedState = ColonyEngine.Advance(steppedState, Environment(steppedState, step * 100));
        steppedTimer.Stop();

        var catchUpColony = Assert.Single(catchUp.Colonies);
        var steppedColony = Assert.Single(steppedState.Colonies);
        var catchUpSupplies = Assert.Single(catchUpColony.Stock);
        var steppedSupplies = Assert.Single(steppedColony.Stock);

        Assert.Equal(100, catchUpColony.Facilities.Count);
        Assert.Equal(200, catchUpColony.Residents.Count);
        Assert.Equal(100, decoded.Colonies.Single().Facilities.Count);
        Assert.Equal(200, decoded.Colonies.Single().Residents.Count);
        Assert.True(payload.Length < ColonyLimits.MaxBytes);
        Assert.Equal(ColonyStateCodec.Hash(payload), ColonyStateCodec.Hash(ColonyStateCodec.Serialize(decoded)));
        Assert.Equal(10_000, catchUp.SimulatedUt);
        Assert.Equal(catchUp.SimulatedUt, steppedState.SimulatedUt);
        Assert.Equal(catchUpSupplies.Amount, steppedSupplies.Amount);
        Assert.Equal(catchUpColony.SupportConsumedMicroUnits, steppedColony.SupportConsumedMicroUnits);
        Assert.Equal(catchUpColony.SupportRemainder, steppedColony.SupportRemainder);

        const long expectedConsumed = 92_592_685;
        Assert.Equal(expectedConsumed, catchUpColony.SupportConsumedMicroUnits);
        Assert.Equal(0.185185185185185185m, decimal.Round(catchUpColony.SupportRemainder, 18));
        Assert.Equal(1_000_000_000 - expectedConsumed, catchUpSupplies.Amount);

        output.WriteLine(
            "Unit-fixture performance only; not a game-frame claim. " +
            $"facilities={catchUpColony.Facilities.Count}; residents={catchUpColony.Residents.Count}; " +
            $"serializedBytes={payload.Length}; encodeMs={encodeTimer.Elapsed.TotalMilliseconds:F3}; " +
            $"decodeMs={decodeTimer.Elapsed.TotalMilliseconds:F3}; " +
            $"singleCatchUpMs={catchUpTimer.Elapsed.TotalMilliseconds:F3}; " +
            $"100StepAdvanceMs={steppedTimer.Elapsed.TotalMilliseconds:F3}; " +
            $"supportConsumedMicroUnits={catchUpColony.SupportConsumedMicroUnits}; " +
            $"supportRemainder={catchUpColony.SupportRemainder.ToString(CultureInfo.InvariantCulture)}");
    }

    static ColonyState BuildScaleState()
    {
        var colony = new ColonyRecord
        {
            Id = Id(2),
            Name = "Scale fixture",
            Site = new ColonySite { Body = "Minmus", Biome = "Greater Flats", Latitude = 3, Longitude = 3, RadiusMeters = 400 },
            Charter = new ColonyCharter { PopulationTarget = 200, ResidentLimit = 200, VisitorLimit = 24 },
            SupportCommissionedUt = 0,
            SupportAccountedUt = 0,
            SupportMicroUnitsPerPersonDay = 1_000_001,
            SupportConsumedMicroUnits = 0,
            SupportRemainder = 0
        };
        colony.Stock.Add(new ColonyStock { Resource = "Supplies", Amount = 1_000_000_000, Capacity = 1_000_000_000 });

        for (var i = 0; i < 100; i++)
        {
            colony.Facilities.Add(new ColonyFacility
            {
                Id = Id(100 + i),
                VesselId = Id(1_000 + i),
                Name = $"Habitat {i:D3}",
                State = "operational",
                CertifiedHomes = 2,
                Qualification = new ColonyQualification
                {
                    Provider = "unit-fixture",
                    Context = "test",
                    EvidenceHash = $"fixture-{i:D3}",
                    HousingCertified = true
                }
            });
        }

        for (var i = 0; i < 200; i++)
        {
            colony.Residents.Add(new ColonyResident
            {
                Id = Id(2_000 + i),
                RosterId = $"fixture-kerbal-{i:D3}",
                Name = $"Fixture resident {i:D3}",
                Trait = i % 2 == 0 ? "Engineer" : "Scientist",
                HomeFacilityId = Id(100 + i / 2),
                Status = "resident"
            });
        }

        return new ColonyState
        {
            WorldId = World,
            SimulatedUt = 0,
            TargetUt = 0,
            Colonies = [colony]
        };
    }

    static string ExpectedCheckpointHash(string priorHash, ColonyJournalEntry entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(priorHash);
            writer.Write(entry.Sequence);
            writer.Write(entry.Ut);
            writer.Write(entry.ColonyId);
            writer.Write(entry.OperationId);
            writer.Write(entry.Kind);
            writer.Write(entry.Detail);
            writer.Write(entry.FundsDelta);
            writer.Write(entry.Resource);
            writer.Write(entry.ResourceDelta);
        }
        return ColonyStateCodec.Hash(stream.ToArray());
    }
}
