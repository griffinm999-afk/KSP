using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Expanse.Clock.Core;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class ClockTests
{
    static readonly Guid WorldId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    static readonly Guid DepotId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    static ClockSample Sample(long sequence = 1, double ut = 42, Guid? epoch = null, bool active = true) =>
        new(1, "clockSample", sequence, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), epoch ?? Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            @"C:\KSP", active ? "Sandbox" : null, active ? "A \"save\"" : null, active ? ut : null, active, "Flight", false, active ? "Year 1, Day 1" : null, null);

    static DepotPayload Registered(ClockSample sample, long revision = 1, double stockAge = 0, ResourceRow[]? resources = null, int membershipRevision = 1) =>
        new(1, "registered", null, WorldId, DepotId, membershipRevision, "Alpha Depot", 100u, 2, "Test Vessel", "complete", null,
            new StockSnapshot(sample.SessionId, sample.LoadEpoch, WorldId, DepotId, membershipRevision, revision,
                sample.UtSeconds ?? 0, sample.UtSeconds ?? 0, stockAge,
                resources ?? [new ResourceRow("LiquidFuel", "Liquid Fuel", 0, 100)]));
    static string HostDllPath()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "Expanse.Clock.Host.dll");
        if (File.Exists(copied)) return copied;
        var configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/Expanse.Clock.Host/bin", configuration, "net8.0/Expanse.Clock.Host.dll"));
    }

    [Fact] public void FramingUsesLittleEndianAndRoundTripsJson()
    {
        var frame = ClockProtocol.Encode(Sample()); Assert.Equal((uint)(frame.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(frame));
        var sample = System.Text.Json.JsonSerializer.Deserialize<ClockSample>(frame.AsSpan(4), ClockProtocol.JsonOptions)!;
        Assert.Equal("A \"save\"", sample.SaveTitle); ClockProtocol.Validate(sample);
        var view = new ClockView(1, "clockView", "live", sample, 0.25, true);
        var viewJson = System.Text.Json.JsonSerializer.Serialize(view, ClockProtocol.JsonOptions);
        Assert.Contains("\"protocolVersion\":1", viewJson); Assert.Contains("\"messageType\":\"clockView\"", viewJson);
        ClockProtocol.Validate(System.Text.Json.JsonSerializer.Deserialize<ClockView>(viewJson, ClockProtocol.JsonOptions)!);
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(view with { ProtocolVersion = 2 }));
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(view with { MessageType = "clockSample" }));
    }

    [Fact] public void RejectsInvalidLengthsAndInvalidSamples()
    {
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Encode(new string('x', ClockProtocol.MaxFrameBytes)));
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(Sample() with { UtSeconds = double.NaN }));
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(Sample() with { ProtocolVersion = 2 }));
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(Sample() with { ActiveWorld = false }));
    }

    [Fact] public void OptionalDepotFieldsPreserveOldBridgeAndHostCompatibility()
    {
        var sampleJson = System.Text.Json.JsonSerializer.Serialize(Sample(), ClockProtocol.JsonOptions);
        Assert.DoesNotContain("\"depot\"", sampleJson);
        var decoded = ClockProtocol.DecodeClockSample(System.Text.Encoding.UTF8.GetBytes(sampleJson));
        Assert.Null(decoded.Depot);
        var state = new ClockState(); state.SetPublisherConnected(true); state.Accept(decoded);
        Assert.Equal("updateBridge", state.Snapshot().DepotView!.Status);

        var oldHostJson = "{\"protocolVersion\":1,\"messageType\":\"clockView\",\"status\":\"waitingForKsp\",\"sample\":null,\"ageSeconds\":null,\"publisherConnected\":false}";
        var oldHostView = System.Text.Json.JsonSerializer.Deserialize<ClockView>(oldHostJson, ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(oldHostView); Assert.Null(oldHostView.DepotView);
    }

    [Fact] public async Task OptionalAllDepotRowsRoundTripThroughViewAndKeepMonotonicAge()
    {
        var first = new DepotSummaryView("stable-alpha", "Alpha", 3, new string('a', 64), "live", null, 0, 2, [new ResourceRow("Fuel", "Liquid Fuel", 4, 8)]);
        var sample = Sample() with { WorldId = "world-a", RunId = "run-a", Depots = [first] };
        ClockProtocol.Validate(sample);
        var state = new ClockState(); state.SetPublisherConnected(true); state.Accept(sample);
        await Task.Delay(40);
        var view = state.Snapshot();
        Assert.Equal("world-a", view.WorldId); Assert.Equal("run-a", view.RunId);
        var row = Assert.Single(view.Depots!); Assert.Equal("stable-alpha", row.DepotId); Assert.True(row.StockAgeSeconds >= 0.03);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<ClockView>(System.Text.Json.JsonSerializer.Serialize(view, ClockProtocol.JsonOptions), ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(decoded); Assert.Equal("Alpha", Assert.Single(decoded.Depots!).Label);
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(sample with { Depots = [first, first] }));
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(sample with { ActiveWorld = false, SaveFolder = null, SaveTitle = null, UtSeconds = null, FormattedDate = null, WarpRate = null, Depots = [first] }));
    }

    [Fact] public void InvalidLargeDepotSnapshotIsReducedWithoutRejectingClockSample()
    {
        var sample = Sample();
        var tooManyRows = Enumerable.Range(0, 129).Select(i => new ResourceRow("res" + i, "Resource " + i, 1, 2)).ToArray();
        var message = sample with { Depot = Registered(sample, resources: tooManyRows) };
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, ClockProtocol.JsonOptions);
        Assert.True(json.Length < ClockProtocol.MaxFrameBytes);
        var decoded = ClockProtocol.DecodeClockSample(json);
        Assert.True(decoded.ActiveWorld); Assert.Equal("registered", decoded.Depot!.RegistryState);
        Assert.Equal("unavailable", decoded.Depot.ObservationState); Assert.Null(decoded.Depot.Snapshot);
        var state = new ClockState(); state.SetPublisherConnected(true); state.Accept(decoded);
        Assert.Equal("live", state.Snapshot().Status); Assert.Equal("unavailable", state.Snapshot().DepotView!.Status);

        var invalidRowsSample = sample with { Depot = Registered(sample, resources: [new ResourceRow("Fuel", "Fuel", 2, 1)]) };
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Validate(invalidRowsSample));
    }

    [Fact] public void ColonyObservationSurvivesClockAndRejectsMalformedRowsIndependently()
    {
        var sample = Sample() with
        {
            Colony = new ColonySnapshot("observed", null, 42,
            [new ColonyVessel("vessel-1", "Ag module", "Minmus", "Greater Flats", 3, 2.9, "loaded", 4,
                [new ColonyTank("Gypsum", 3, 20, true, true, true)],
                [new ColonyConverter("Tundra Ag Support", "Fertilizer(G)", true, ["Gypsum", "ElectricCharge"], ["Fertilizer"])])])
        };
        var decoded = ClockProtocol.DecodeClockSample(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sample, ClockProtocol.JsonOptions));
        var state = new ClockState(); state.SetPublisherConnected(true); state.Accept(decoded);
        Assert.Equal("live", state.Snapshot().Status);
        Assert.Equal(3, Assert.Single(Assert.Single(state.Snapshot().Colony!.Vessels).Tanks).Amount);

        var malformed = sample with { Sequence = 2, Colony = sample.Colony with
            { Vessels = [sample.Colony.Vessels[0] with { Tanks = [new ColonyTank("Gypsum", 21, 20, true, true, true)] }] } };
        var badDecoded = ClockProtocol.DecodeClockSample(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(malformed, ClockProtocol.JsonOptions));
        state.Accept(badDecoded);
        Assert.Equal("live", state.Snapshot().Status);
        Assert.Equal("unavailable", state.Snapshot().Colony!.Status);
        Assert.Empty(state.Snapshot().Colony!.Vessels);
    }

    [Fact] public void OversizedColonyViewKeepsClockAndDepotProjection()
    {
        var vesselRows = Enumerable.Range(0, 24).Select(i => new ColonyVessel("vessel-" + i,
            "Settlement " + i, "Minmus", "Greater Flats", 3, 2.9, "snapshot", 0,
            Enumerable.Range(0, 24).Select(j => new ColonyTank("VeryLongResourceName" + new string('R', 45) + j,
                1, 10, null, null, null)).ToArray(),
            Enumerable.Range(0,12).Select(j=>new ColonyConverter("Converter "+j,"Configured recipe",true,
                Enumerable.Range(0,16).Select(k=>"Input-"+k+new string('I',60)).ToArray(),
                Enumerable.Range(0,16).Select(k=>"Output-"+k+new string('O',60)).ToArray())).ToArray())).ToArray();
        var colony = new ColonySnapshot("observed", null, 42, vesselRows);
        var depot = new DepotSummaryView("depot-a", "Fuel station", 1, new string('a', 64),
            "live", null, 0, 1, [new ResourceRow("LiquidFuel", "Liquid Fuel", 10, 20)]);
        var sample = Sample() with { Depots = [depot] };
        var view = new ClockView(1, "clockView", "live", sample, 0, true, null, [depot], null, null, colony);
        ClockProtocol.Validate(view);
        Assert.Throws<InvalidDataException>(() => ClockProtocol.Encode(view));
        var frame = ClockProtocol.EncodeView(view);
        var reduced = System.Text.Json.JsonSerializer.Deserialize<ClockView>(frame.AsSpan(4), ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(reduced);
        Assert.Equal("truncated", reduced.Colony!.Status);
        Assert.Equal("Fuel station", Assert.Single(reduced.Depots!).Label);
        Assert.Equal("live", reduced.Status);
    }

    [Fact] public void ColonyProjectionClearsOnNoWorldAndDoesNotCrossLoadEpoch()
    {
        var vessel = new ColonyVessel("minmus-a", "Farm", "Minmus", "Greater Flats", 3, 2.9,
            "loaded", 2, [new ColonyTank("Water", 3, 10, true, true, true)], []);
        var first = Sample() with { Colony = new ColonySnapshot("observed", null, 42, [vessel]) };
        var state = new ClockState(); state.SetPublisherConnected(true); state.Accept(first);
        Assert.Single(state.Snapshot().Colony!.Vessels);

        var menu = Sample(2, active:false);
        state.Accept(menu);
        Assert.Equal("noWorld", state.Snapshot().Status);
        Assert.Null(state.Snapshot().Colony);

        state.Accept(Sample(3, epoch:Guid.NewGuid()));
        Assert.Equal("live", state.Snapshot().Status);
        Assert.Null(state.Snapshot().Colony);
    }

    [Fact] public async Task DepotFreshnessUsesMonotonicObservationAgeAndContextFencing()
    {
        var state = new ClockState(); state.SetPublisherConnected(true);
        var first = Sample() with { Depot = Registered(Sample()) };
        state.Accept(first);
        Assert.Equal("live", state.Snapshot().DepotView!.Status);
        await Task.Delay(100);
        var repeatedZeroAge = first with { Sequence = 2, Depot = Registered(first, stockAge: 0) };
        state.Accept(repeatedZeroAge);
        Assert.True(state.Snapshot().DepotView!.StockAgeSeconds >= 0.08, "Repeated ageSeconds=0 must not reset the same stock revision's age.");

        var nextEpoch = Guid.NewGuid();
        var changedContext = Sample(3, 42, nextEpoch) with
        { Depot = new DepotPayload(1, "registered", null, WorldId, DepotId, 1, "Alpha Depot", 100, 2, "Renamed Vessel", "collecting", null, null) };
        state.Accept(changedContext);
        var afterEpoch = state.Snapshot().DepotView!;
        Assert.Equal("unavailable", afterEpoch.Status); Assert.Empty(afterEpoch.Resources); Assert.Null(afterEpoch.SnapshotRevision);

        var knownZero = changedContext with { Sequence = 4, Depot = Registered(changedContext) };
        state.Accept(knownZero);
        Assert.Equal("live", state.Snapshot().DepotView!.Status); Assert.Equal(0, state.Snapshot().DepotView!.Resources[0].Amount);
        state.SetPublisherConnected(false);
        Assert.Equal("lastObserved", state.Snapshot().DepotView!.Status);
    }

    [Fact] public async Task ReaderRejectsTruncatedAndOversizedFramesAndMalformedUtf8()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await ClockProtocol.ReadFrameAsync(new MemoryStream([1, 0]), default));
        var oversizedHeader=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader,(uint)ClockProtocol.MaxFrameBytes+1);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await ClockProtocol.ReadFrameAsync(new MemoryStream(oversizedHeader), default));
        var badJson = new byte[] { 0xff }; var frame = new byte[5]; BinaryPrimitives.WriteUInt32LittleEndian(frame, 1); badJson.CopyTo(frame, 4);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await ClockProtocol.ReadJsonAsync<ClockSample>(new MemoryStream(frame), default));
    }

    [Fact] public void StateHandlesSequenceEpochBackwardTimeAndMenu()
    {
        var state = new ClockState(); state.SetPublisherConnected(true);
        state.Accept(Sample(1, 100)); Assert.Equal("live", state.Snapshot().Status);
        Assert.Throws<InvalidDataException>(() => state.Accept(Sample(1, 101)));
        state.Accept(Sample(2, 20)); Assert.Equal(20, state.Snapshot().Sample!.UtSeconds);
        state.Accept(Sample(3, 20, Guid.NewGuid())); Assert.Equal("live", state.Snapshot().Status);
        state.Accept(Sample(4, active: false)); Assert.Equal("noWorld", state.Snapshot().Status);
        state.SetPublisherConnected(false); Assert.Equal("stale", state.Snapshot().Status);
        Assert.Equal("waitingForKsp", new ClockState().Snapshot().Status);
    }

    [Fact] public void StateRejectsSamplesFromRetiredBridgeSessions()
    {
        var state = new ClockState(); var first = Sample(); state.Accept(first);
        var nextSession = Sample(1, 70) with { SessionId = Guid.NewGuid() }; state.Accept(nextSession);
        Assert.Throws<InvalidDataException>(() => state.Accept(first with { Sequence = 2, UtSeconds = 99 }));
        Assert.Equal(70, state.Snapshot().Sample!.UtSeconds);
    }

    [Fact] public void SameSaveFolderOnDifferentInstallNamespacesRemainsDistinct()
    {
        var state = new ClockState(); state.SetPublisherConnected(true);
        state.Accept(Sample()); state.Accept(Sample(2, 50) with { InstallNamespace = @"D:\KSP" });
        Assert.Equal("Sandbox", state.Snapshot().Sample!.SaveFolder);
        Assert.Equal(@"D:\KSP", state.Snapshot().Sample!.InstallNamespace);
    }

    [Fact] public void EffectsRequireSampleFromCurrentPublisherConnectionGeneration()
    {
        var state = new ClockState(); state.SetPublisherConnected(true); var first = Sample(); state.Accept(first);
        Assert.True(state.MatchesFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!));
        state.SetPublisherConnected(false); state.SetPublisherConnected(true);
        Assert.False(state.MatchesFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!));
        Assert.Equal("live", state.Snapshot().Status); // read-only clock state remains available until ordinary freshness expires
        var next = first with { Sequence = 2 }; state.Accept(next);
        Assert.True(state.MatchesFreshGameContext(next.SessionId, next.LoadEpoch, next.InstallNamespace, next.SaveFolder!));
    }

    [Fact] public void ExactEffectFreshnessFencesWorldRunAndAdvancesGenerationAtSameUt()
    {
        var state = new ClockState(); state.SetPublisherConnected(true);
        var first = Sample() with { WorldId = "world-a", RunId = "run-a" }; state.Accept(first);
        Assert.True(state.TryGetFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!, "world-a", "run-a", out var generationA));
        Assert.False(state.MatchesFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!, "world-b", "run-a"));
        state.Accept(first with { Sequence = 2, WorldId = "world-b", RunId = "run-b" });
        Assert.False(state.MatchesFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!, "world-a", "run-a"));
        Assert.True(state.TryGetFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!, "world-b", "run-b", out var generationB));
        Assert.True(generationB > generationA);
        state.Accept(first with { Sequence = 3, ActiveWorld = false, SaveFolder = null, SaveTitle = null, UtSeconds = null, FormattedDate = null, WarpRate = null, WorldId = null, RunId = null });
        Assert.False(state.MatchesFreshGameContext(first.SessionId, first.LoadEpoch, first.InstallNamespace, first.SaveFolder!, "world-b", "run-b"));
    }

    [Fact] public async Task SyntheticPublisherFlowsThroughRealHostToTypedViewClient()
    {
        var suffix = Guid.NewGuid().ToString("N"); var pubName = "Expanse.Clock.Test.Publisher." + suffix; var viewName = "Expanse.Clock.Test.View." + suffix;
        var hostDll = HostDllPath();
        Assert.True(File.Exists(hostDll), $"Host build missing: {hostDll}");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "ExpanseClockSynthetic-" + suffix);
        Process StartHost() => Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { hostDll, "--publisher-pipe", pubName, "--view-pipe", viewName,
            "--effects-pipe", "Expanse.Clock.Test.Effects." + suffix, "--command-pipe", "Expanse.Clock.Test.Commands." + suffix, "--data-dir", dataDirectory }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var proc = StartHost();
        try
        {
            await using var publisher = new NamedPipeClientStream(".", pubName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var connectLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await publisher.ConnectAsync(connectLimit.Token);
            await ClockProtocol.WriteFrameAsync(publisher, Sample(1, 9_999_999_999), connectLimit.Token);
            var client = new ClockViewClient(viewName); ClockView? view = null;
            var readyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < readyUntil)
            { try { view = await client.GetSnapshotAsync(TimeSpan.FromSeconds(1)); if (view.Status == "live" && view.Sample?.Sequence == 1) break; } catch (IOException) { } await Task.Delay(50); }
            Assert.NotNull(view); Assert.Equal("live", view!.Status); Assert.Equal(9_999_999_999, view.Sample!.UtSeconds); Assert.True(view.PublisherConnected);

            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetSnapshotAsync())); Assert.All(concurrent, x => Assert.Equal(9_999_999_999, x.Sample!.UtSeconds));
            await ClockProtocol.WriteFrameAsync(publisher, Sample(2, 123456789012), connectLimit.Token);
            await ClockProtocol.WriteFrameAsync(publisher, Sample(3, 765432109876), connectLimit.Token);
            var latestUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            do
            {
                view = await client.GetSnapshotAsync();
                if (view.Sample?.Sequence == 3) break;
                await Task.Delay(50);
            }
            while (DateTime.UtcNow < latestUntil);
            Assert.Equal(3, view.Sample!.Sequence);
            Assert.Equal(765432109876, view.Sample.UtSeconds); // latest processed sample replaces intermediate history

            await using (var second = new NamedPipeClientStream(".", pubName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using var secondLimit = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await second.ConnectAsync(secondLimit.Token);
                var rejected = false;
                try { await second.WriteAsync(ClockProtocol.Encode(Sample(4, 999))); var one = new byte[1]; rejected = await second.ReadAsync(one, secondLimit.Token) == 0; }
                catch (IOException) { rejected = true; }
                Assert.True(rejected, "A concurrent second publisher should be closed.");
            }
            await publisher.DisposeAsync();
            await Task.Delay(100); // allow the host to release the prior transport generation
            await using (var reconnect = new NamedPipeClientStream(".", pubName, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                using var reconnectLimit = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await reconnect.ConnectAsync(reconnectLimit.Token);
                await ClockProtocol.WriteFrameAsync(reconnect, Sample(4, 777777777777), reconnectLimit.Token);
                var reconnectUntil = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                do
                {
                    view = await client.GetSnapshotAsync();
                    if (view.Sample?.Sequence == 4) break;
                    await Task.Delay(50);
                }
                while (DateTime.UtcNow < reconnectUntil);
                Assert.Equal(4, view.Sample!.Sequence);
                Assert.Equal(777777777777, view.Sample.UtSeconds);
            }
            await Task.Delay(3200); view = await client.GetSnapshotAsync(); Assert.Equal("stale", view.Status); Assert.Equal(777777777777, view.Sample!.UtSeconds);
        }
        finally { try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { } await proc.WaitForExitAsync(); proc.Dispose(); }

        // Restart starts with empty in-memory state and requires a fresh publisher sample.
        proc = StartHost();
        try
        {
            var client = new ClockViewClient(viewName); ClockView? view = null;
            for (var attempt = 0; attempt < 15; attempt++)
            { try { view = await client.GetSnapshotAsync(TimeSpan.FromSeconds(1)); break; } catch (IOException) { await Task.Delay(100); } }
            Assert.NotNull(view); Assert.Equal("waitingForKsp", view!.Status); Assert.Null(view.Sample);
        }
        finally { try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { } await proc.WaitForExitAsync(); proc.Dispose(); }
    }

    [Fact] public async Task StalledViewClientsAreBoundedAndEndpointRecoversAfterSixteenSlots()
    {
        var suffix = Guid.NewGuid().ToString("N"); var viewName = "Expanse.Clock.Test.View.Bounded." + suffix;
        var hostDll = HostDllPath();
        Assert.True(File.Exists(hostDll), $"Host build missing: {hostDll}");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "ExpanseClockBounded-" + suffix);
        var proc = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { hostDll,
            "--publisher-pipe", "Expanse.Clock.Test.Publisher.Bounded." + suffix, "--view-pipe", viewName,
            "--effects-pipe", "Expanse.Clock.Test.Effects." + suffix, "--command-pipe", "Expanse.Clock.Test.Commands." + suffix,
            "--data-dir", dataDirectory }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stalled = Enumerable.Range(0, 16).Select(_ => new NamedPipeClientStream(".", viewName, PipeDirection.InOut, PipeOptions.Asynchronous)).ToArray();
        var seventeenth = new NamedPipeClientStream(".", viewName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connectLimit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await Task.WhenAll(stalled.Select(pipe => pipe.ConnectAsync(connectLimit.Token)));
            using var overflowLimit = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var overflowConnect = seventeenth.ConnectAsync(overflowLimit.Token);
            await Task.Delay(2300); // each accepted but incomplete request is bounded by the Host's two-second read deadline
            await overflowConnect;
            Assert.True(seventeenth.IsConnected, "Listener should admit the next client after timed-out handlers release their reserved slots.");

            foreach (var pipe in stalled) await pipe.DisposeAsync();
            await seventeenth.DisposeAsync();
            var snapshot = await new ClockViewClient(viewName).GetSnapshotAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("waitingForKsp", snapshot.Status);
        }
        finally
        {
            foreach (var pipe in stalled) await pipe.DisposeAsync();
            await seventeenth.DisposeAsync();
            try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(); proc.Dispose();
        }
    }

    [Fact] public async Task TypedRecoveryCommandPipeIsReachableAndCounterIsDisabledByDefault()
    {
        var token = Guid.NewGuid().ToString("N"); var publisher = EffectsProtocol.CreateDevPublisherPipeName(token); var view = EffectsProtocol.CreateDevViewPipeName(token);
        var effect = EffectsProtocol.CreateDevPipeName(token); var command = EffectsProtocol.CreateDevCommandPipeName(token);
        var dataDirectory = Path.Combine(Path.GetTempPath(), "ExpanseCommandPipeTest-" + token); Directory.CreateDirectory(dataDirectory);
        var hostDll = HostDllPath();
        var proc = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { hostDll, "--publisher-pipe", publisher, "--view-pipe", view, "--effects-pipe", effect, "--command-pipe", command, "--data-dir", dataDirectory }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        try
        {
            var client = new RecoveryCommandClient(command); SubmitCommandResult? result = null; var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    result = await client.SubmitCommandAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = "disabled-probe", WorldId = Guid.NewGuid().ToString("D"), RunId = "none", CommandKind = "counterIncrement", CounterDelta = 1 }, TimeSpan.FromSeconds(1));
                    break;
                }
                catch (IOException) { await Task.Delay(50); }
            }
            Assert.NotNull(result); Assert.Equal("rejected", result!.Status); Assert.Contains("disabled", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { }
            await proc.WaitForExitAsync(); proc.Dispose();
            Directory.Delete(dataDirectory, true);
        }
    }
}
