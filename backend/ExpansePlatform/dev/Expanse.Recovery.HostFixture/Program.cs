using System.Diagnostics;
using System.Text.Json;
using Expanse.Clock.Core;
using Expanse.Domain;

if (args.Length < 1 || args[0] is not ("submit" or "duplicate-1000" or "observe" or "route-upsert-only" or "rules-disable" or "physical-3b-submit" or "physical-3b-rules-submit" or "remote-delivery-submit" or "production-copy-send"))
{
    Console.Error.WriteLine("Usage: Expanse.Recovery.HostFixture <submit|duplicate-1000> --command-pipe NAME --world-id GUID --run-id ID --request-id ID [--delta N]\n       Expanse.Recovery.HostFixture observe --view-pipe NAME --duration-seconds N --interval-ms N --output-file PATH\n       Expanse.Recovery.HostFixture route-upsert-only --command-pipe DEV_NAME --world-id ID --run-id ID --source-depot-id ID --destination-depot-id ID --route-id ID --resource NAME --amount-micro-units N --duration-seconds N [--timeout-seconds N]\n       Expanse.Recovery.HostFixture rules-disable --command-pipe DEV_NAME --world-id ID --run-id ID --repeat-rule-id ID --keep-stock-rule-id ID [--timeout-seconds N]\n       Expanse.Recovery.HostFixture physical-3b-submit --command-pipe DEV_NAME --world-id ID --run-id ID --source-depot-id ID --destination-depot-id ID --resource NAME --duration-seconds N [--timeout-seconds N]\n       Expanse.Recovery.HostFixture physical-3b-rules-submit --command-pipe DEV_NAME --world-id ID --run-id ID --source-depot-id ID --destination-depot-id ID --route-id ID --repeat-rule-id ID --repeat-next-due-ut N --repeat-interval-seconds N --keep-stock-rule-id ID --keep-stock-resource NAME --keep-stock-low-micro-units N --keep-stock-target-micro-units N --keep-stock-batch-micro-units N [--timeout-seconds N]\n       Expanse.Recovery.HostFixture production-copy-send --command-pipe DEV_NAME --world-id ID --run-id ID --source-depot-id ID --destination-depot-id ID --copy-token GUID_N [--timeout-seconds N]");
    return 2;
}

var mode = args[0]; var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { Console.Error.WriteLine("Invalid argument: " + args[i]); return 2; }
    values[args[i]] = args[++i];
}
if (mode == "observe")
{
    if (!values.TryGetValue("--view-pipe", out var viewPipe) || !values.TryGetValue("--output-file", out var outputFile) || !int.TryParse(values.GetValueOrDefault("--duration-seconds"), out var durationSeconds) || durationSeconds is < 1 or > 600 || !int.TryParse(values.GetValueOrDefault("--interval-ms", "1000"), out var intervalMs) || intervalMs is < 100 or > 10000)
    { Console.Error.WriteLine("observe requires --view-pipe, --output-file, duration 1..600 seconds, and interval 100..10000 ms."); return 2; }
    try
    {
        var path = Path.GetFullPath(outputFile); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
        var observerClient = new ClockViewClient(viewPipe); var deadline = Stopwatch.GetTimestamp() + (long)(durationSeconds * (double)Stopwatch.Frequency);
        do
        {
            var at = DateTimeOffset.UtcNow;
            try
            {
                var view = await observerClient.GetSnapshotAsync(TimeSpan.FromSeconds(3)); var sample = view.Sample;
                await output.WriteLineAsync(JsonSerializer.Serialize(new { atUtc = at, status = view.Status, publisherConnected = view.PublisherConnected, ageSeconds = view.AgeSeconds, sequence = sample?.Sequence, sessionId = sample?.SessionId, loadEpoch = sample?.LoadEpoch, installNamespace = sample?.InstallNamespace, saveFolder = sample?.SaveFolder, utSeconds = sample?.UtSeconds }, ClockProtocol.JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or OperationCanceledException)
            { await output.WriteLineAsync(JsonSerializer.Serialize(new { atUtc = at, status = "unavailable", error = ex.Message }, ClockProtocol.JsonOptions)); }
            await output.FlushAsync(); var remaining = deadline - Stopwatch.GetTimestamp(); if (remaining > 0) await Task.Delay(Math.Min(intervalMs, (int)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)));
        } while (Stopwatch.GetTimestamp() < deadline);
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (mode == "route-upsert-only")
{
    if (!values.TryGetValue("--command-pipe", out var routePipe) || !values.TryGetValue("--world-id", out var routeWorld) || !values.TryGetValue("--run-id", out var routeRun) ||
        !values.TryGetValue("--source-depot-id", out var routeSourceId) || !values.TryGetValue("--destination-depot-id", out var routeDestinationId) || !values.TryGetValue("--route-id", out var routeId) || !values.TryGetValue("--resource", out var routeResource) ||
        !long.TryParse(values.GetValueOrDefault("--amount-micro-units"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amountMicroUnits) ||
        !double.TryParse(values.GetValueOrDefault("--duration-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var routeDuration) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "90"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var routeTimeout) || routeTimeout is < 5 or > 180)
    { Console.Error.WriteLine("route-upsert-only requires pipe, context, endpoints, route ID, resource, positive amount/duration, and timeout 5..180 seconds."); return 2; }
    if (!IsCurrentUserDevCommandPipe(routePipe) || routeWorld.Length is < 1 or > 128 || routeRun.Length is < 1 or > 128 || routeSourceId.Length is < 1 or > 128 || routeDestinationId.Length is < 1 or > 128 || routeSourceId == routeDestinationId || routeId.Length is < 1 or > 128 || routeResource.Length is < 1 or > 64 || routeResource.Any(char.IsControl) || amountMicroUnits <= 0 || !double.IsFinite(routeDuration) || routeDuration <= 0 || routeDuration > 1e15)
    { Console.Error.WriteLine("Route fixture values are invalid or outside bounds, or command pipe is not a current-user dev endpoint."); return 2; }
    var routeClient = new RecoveryCommandClient(routePipe); var routeRequestId = "route-only-" + Guid.NewGuid().ToString("N");
    try
    {
        var projection = await routeClient.GetAcceptedStateAsync(routeWorld, routeRun, TimeSpan.FromSeconds(3));
        if (projection.Status != "available" || projection.AcceptedCapsule is null) return RouteResult("unavailable", null, projection.Reason);
        var state = AcceptedStateCodec.ReadCapsule(projection.AcceptedCapsule);
        var source = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == routeSourceId); var destination = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == routeDestinationId);
        if (state.SchemaVersion != 2 || source is null || destination is null) return RouteResult("rejected", null, "Both route endpoints must exist in the exact active accepted depot mirror.");
        var route = new RouteVersionRecord { RouteId = routeId, Version = state.RouteVersions.Where(x => x.RouteId == routeId).Select(x => x.Version).DefaultIfEmpty(0).Max() + 1, SourceDepotId = source.DepotId, SourceMembershipRevision = source.MembershipRevision, SourceMembershipHash = source.MembershipHash, DestinationDepotId = destination.DepotId, DestinationMembershipRevision = destination.MembershipRevision, DestinationMembershipHash = destination.MembershipHash, TravelDurationSeconds = routeDuration, Provenance = "dev route-upsert-only fixture", Resources = [new ResourceAmount { ResourceName = routeResource, AmountMicroUnits = amountMicroUnits }] };
        var command = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = routeRequestId, WorldId = routeWorld, RunId = routeRun, CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
        var settled = await SubmitWithBoundedPreparedRetry(routeClient, command, routeTimeout);
        if (settled.Status == "rejected") return RouteResult("rejected", settled, settled.Reason);
        settled = await WaitForAccepted(routeClient, routeRequestId, routeWorld, routeRun, routeTimeout);
        if (settled.Status != "accepted" || settled.AcceptedCapsule is null) return RouteResult(settled.Status, settled, settled.Reason);
        var saved = AcceptedStateCodec.ReadCapsule(settled.AcceptedCapsule).RouteVersions.SingleOrDefault(x => x.RouteId == routeId && x.Version == route.Version);
        if (saved is null || saved.SourceMembershipHash != source.MembershipHash || saved.DestinationMembershipHash != destination.MembershipHash || saved.Resources.Length != 1 || saved.Resources[0].ResourceName != routeResource || saved.Resources[0].AmountMicroUnits != amountMicroUnits)
            return RouteResult("verificationFailed", settled, "Verified capsule does not contain the exact immutable route version.");
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "routeUpsertOnlyResult", status = "accepted", worldId = routeWorld, runId = routeRun, routeId, routeVersion = route.Version, routeRequestId, operationId = settled.OperationId, sourceDepotId = routeSourceId, destinationDepotId = routeDestinationId, resourceName = routeResource, amountMicroUnits, durationSeconds = routeDuration, acceptedRevision = settled.AcceptedRevision, acceptedSequence = settled.AcceptedSequence, stateHash = settled.StateHash }, ClockProtocol.JsonOptions));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    { return RouteResult("error", null, Bound(ex.Message)); }

    int RouteResult(string status, SubmitCommandResult? result, string? reason)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "routeUpsertOnlyResult", status, worldId = routeWorld, runId = routeRun, routeId, routeRequestId, operationId = result?.OperationId, acceptedRevision = result?.AcceptedRevision, acceptedSequence = result?.AcceptedSequence, reason = reason is null ? null : Bound(reason) }, ClockProtocol.JsonOptions));
        return 1;
    }
}

if (mode == "production-copy-send")
{
    string[] required = ["--command-pipe", "--world-id", "--run-id", "--source-depot-id", "--destination-depot-id", "--copy-token"];
    if (required.Any(k => !values.ContainsKey(k)) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "180"), out var timeoutSeconds) || timeoutSeconds is < 30 or > 300)
    { Console.Error.WriteLine("production-copy-send requires exact full-clone token, dev command pipe, world/run, depot IDs and timeout 30..300 seconds."); return 2; }
    const string cloneRoot = @"C:\Users\griff\Documents\KSP-RMM-Dev-Full";
    const string routeId = "MM Route 1";
    const long routeVersion = 1;
    var pipe = values["--command-pipe"]; var world = values["--world-id"]; var run = values["--run-id"];
    var sourceId = values["--source-depot-id"]; var destinationId = values["--destination-depot-id"];
    var token = values["--copy-token"];
    if (!IsCurrentUserDevCommandPipe(pipe) || world.Length is < 1 or > 128 || run.Length is < 1 or > 128 ||
        sourceId.Length is < 1 or > 128 || destinationId.Length is < 1 or > 128 || sourceId == destinationId ||
        !Guid.TryParseExact(token, "N", out var parsedToken) || parsedToken == Guid.Empty)
    { Console.Error.WriteLine("Exact copied-route fixture identity or dev pipe is invalid."); return 2; }
    var folder = "ExpanseProductionCopyDelivery-" + parsedToken.ToString("N");
    var copied = Path.GetFullPath(Path.Combine(cloneRoot, "saves", folder, "persistent.sfs"));
    var manifestPath = Path.GetFullPath(Path.Combine(cloneRoot, "saves", folder, "copy-manifest.json"));
    var sendRequest = "production-copy-send-" + Guid.NewGuid().ToString("N");
    try
    {
        if (!copied.StartsWith(cloneRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(copied) || !File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 4096)
            throw new InvalidDataException("Exact full-clone copied save/manifest is missing.");
        for (string? cursor = copied; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Full-clone copied save traverses a reparse point.");
            if (Path.GetDirectoryName(cursor) == cursor) break;
        }
        using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            var m = document.RootElement;
            var expected = m.GetProperty("Sha256").GetString();
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(copied)));
            if (m.GetProperty("Token").GetString() != token || m.GetProperty("Folder").GetString() != folder ||
                !String.Equals(Path.GetFullPath(m.GetProperty("Target").GetString()!), copied, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Exact full-clone copied save hash/manifest differs.");
        }
        var copyClient = new RecoveryCommandClient(pipe);
        var before = await copyClient.GetAcceptedStateAsync(world, run, TimeSpan.FromSeconds(3));
        if (before.Status != "available" || before.AcceptedCapsule is null)
            throw new InvalidDataException("Copied accepted capsule unavailable: " + before.Reason);
        var state = AcceptedStateCodec.ReadCapsule(before.AcceptedCapsule);
        var route = state.RouteVersions.SingleOrDefault(x => !x.LegacyOpaque && x.RouteId == routeId && x.Version == routeVersion);
        var source = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == sourceId);
        var destination = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == destinationId);
        if (state.WorldId != world || state.WritesBlocked || state.ActiveShipments.Length != 0 || route is null ||
            source is null || destination is null || route.SourceDepotId != sourceId || route.DestinationDepotId != destinationId ||
            route.SourceMembershipRevision != source.MembershipRevision || route.SourceMembershipHash != source.MembershipHash ||
            route.DestinationMembershipRevision != destination.MembershipRevision || route.DestinationMembershipHash != destination.MembershipHash ||
            route.TravelDurationSeconds != 302400 || route.Resources.Length != 3 ||
            !route.Resources.Any(x => x.ResourceName == "LiquidFuel" && x.AmountMicroUnits == 1_000_000_000) ||
            !route.Resources.Any(x => x.ResourceName == "MonoPropellant" && x.AmountMicroUnits == 500_000_000) ||
            !route.Resources.Any(x => x.ResourceName == "Oxidizer" && x.AmountMicroUnits == 1_400_000_000))
            throw new InvalidDataException("Host does not expose the exact existing MM Route 1 v1 and selected depots.");
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * (double)Stopwatch.Frequency);
        DeliveryReadinessResult readiness;
        do
        {
            readiness = await copyClient.GetDeliveryReadinessAsync(world, run, routeId, routeVersion, TimeSpan.FromSeconds(3));
            if (readiness.Status == "ready") break;
            await Task.Delay(1000);
        } while (Stopwatch.GetTimestamp() < deadline);
        if (readiness.Status != "ready" || readiness.AcceptedSequence != state.AcceptedSequence)
            throw new InvalidDataException("Exact copied-route physical readiness stayed held: " + readiness.Reason);
        var sendCommand = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = sendRequest,
            WorldId = world, RunId = run, CommandKind = "sendOnce",
            Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = routeId, RouteVersionNumber = routeVersion } };
        var submitted = await copyClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
        if (submitted.Status == "rejected") throw new InvalidDataException("Existing-route Send once rejected: " + submitted.Reason);
        var settled = await WaitForAccepted(copyClient, sendRequest, world, run, timeoutSeconds);
        if (settled.Status != "accepted" || string.IsNullOrWhiteSpace(settled.OperationId))
            throw new InvalidDataException("Existing-route Send once did not settle: " + settled.Status + " " + settled.Reason);
        var accepted = await copyClient.GetAcceptedStateAsync(world, run, TimeSpan.FromSeconds(3));
        if (accepted.Status != "available" || accepted.AcceptedCapsule is null) throw new InvalidDataException("Accepted dispatch capsule unavailable.");
        var dispatched = AcceptedStateCodec.ReadCapsule(accepted.AcceptedCapsule);
        var shipment = dispatched.ActiveShipments.SingleOrDefault(x => x.RouteId == routeId && x.RouteVersion == routeVersion);
        if (dispatched.ActiveShipments.Length != 1 || shipment is null || shipment.RemainingResources.Length != 3 ||
            !shipment.RemainingResources.Any(x => x.ResourceName == "LiquidFuel" && x.AmountMicroUnits == 1_000_000_000) ||
            !shipment.RemainingResources.Any(x => x.ResourceName == "MonoPropellant" && x.AmountMicroUnits == 500_000_000) ||
            !shipment.RemainingResources.Any(x => x.ResourceName == "Oxidizer" && x.AmountMicroUnits == 1_400_000_000) ||
            dispatched.Receipts.Count(x => x.OperationId == settled.OperationId && x.OperationKind == "dispatch" && x.Outcome == "accepted") != 1)
            throw new InvalidDataException("Accepted copied-route dispatch lacks exactly one complete shipment and receipt.");
        var acceptedHash = AcceptedStateCodec.ComputeHash(dispatched);
        var duplicate = await copyClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
        var afterDuplicate = await copyClient.GetAcceptedStateAsync(world, run, TimeSpan.FromSeconds(3));
        if (duplicate.Status != "accepted" || duplicate.OperationId != settled.OperationId ||
            afterDuplicate.Status != "available" || afterDuplicate.AcceptedCapsule is null)
            throw new InvalidDataException("Exact duplicate Send once did not resolve to accepted operation.");
        var duplicateState = AcceptedStateCodec.ReadCapsule(afterDuplicate.AcceptedCapsule);
        if (duplicateState.AcceptedSequence != dispatched.AcceptedSequence ||
            AcceptedStateCodec.ComputeHash(duplicateState) != acceptedHash || duplicateState.ActiveShipments.Length != 1)
            throw new InvalidDataException("Duplicate Send once changed copied-route capsule or shipment.");
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "productionCopySendResult", status = "accepted", worldId = world,
            runId = run, copyToken = token, routeId, routeVersion, sourceDepotId = sourceId, destinationDepotId = destinationId,
            manifest = "LiquidFuel=1000,MonoPropellant=500,Oxidizer=1400", sendRequestId = sendRequest,
            operationId = settled.OperationId, shipmentId = shipment.ShipmentId, acceptedSequence = dispatched.AcceptedSequence,
            capsuleHash = acceptedHash, duplicateOperationId = duplicate.OperationId, duplicateSequence = duplicateState.AcceptedSequence }, ClockProtocol.JsonOptions));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "productionCopySendResult", status = "error", worldId = world,
            runId = run, copyToken = token, routeId, routeVersion, sendRequestId = sendRequest, reason = Bound(ex.Message) }, ClockProtocol.JsonOptions));
        return 1;
    }
}

if (mode == "remote-delivery-submit")
{
    string[] required = ["--command-pipe", "--world-id", "--run-id", "--source-depot-id", "--destination-depot-id", "--duration-seconds"];
    if (required.Any(k => !values.ContainsKey(k)) ||
        !double.TryParse(values["--duration-seconds"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var remoteDuration) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "120"), out var remoteTimeout) || remoteTimeout is < 10 or > 180)
    { Console.Error.WriteLine("remote-delivery-submit requires a dev command pipe, world/run, two depot IDs, duration and timeout 10..180 seconds."); return 2; }
    var remotePipe = values["--command-pipe"]; var remoteWorld = values["--world-id"]; var remoteRun = values["--run-id"];
    var remoteSource = values["--source-depot-id"]; var remoteDestination = values["--destination-depot-id"];
    if (!IsCurrentUserDevCommandPipe(remotePipe) || remoteWorld.Length is < 1 or > 128 || remoteRun.Length is < 1 or > 128 ||
        remoteSource.Length is < 1 or > 128 || remoteDestination.Length is < 1 or > 128 || remoteSource == remoteDestination ||
        !double.IsFinite(remoteDuration) || remoteDuration < 60 || remoteDuration > 600)
    { Console.Error.WriteLine("Remote fixture identity, dev pipe, or 60..600 second route duration is invalid."); return 2; }
    var remoteClient = new RecoveryCommandClient(remotePipe);
    var routeId = "remote-route-" + Guid.NewGuid().ToString("N");
    var routeRequest = "remote-route-request-" + Guid.NewGuid().ToString("N");
    var sendRequest = "remote-send-request-" + Guid.NewGuid().ToString("N");
    try
    {
        var before = await remoteClient.GetAcceptedStateAsync(remoteWorld, remoteRun, TimeSpan.FromSeconds(3));
        if (before.Status != "available" || before.AcceptedCapsule is null) throw new InvalidDataException("Remote accepted capsule unavailable: " + before.Reason);
        var state = AcceptedStateCodec.ReadCapsule(before.AcceptedCapsule);
        var source = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == remoteSource);
        var destination = state.Depots.SingleOrDefault(x => x.Active && x.DepotId == remoteDestination);
        if (state.SchemaVersion != 2 || source is null || destination is null) throw new InvalidDataException("Exact two remote endpoints are not accepted.");
        var route = new RouteVersionRecord
        {
            RouteId = routeId, Version = 1, SourceDepotId = source.DepotId, SourceMembershipRevision = source.MembershipRevision,
            SourceMembershipHash = source.MembershipHash, DestinationDepotId = destination.DepotId,
            DestinationMembershipRevision = destination.MembershipRevision, DestinationMembershipHash = destination.MembershipHash,
            TravelDurationSeconds = remoteDuration, Provenance = "isolated dev two-vessel remote BRP fixture",
            Resources = [new ResourceAmount { ResourceName = "LiquidFuel", AmountMicroUnits = 1_000_000 },
                new ResourceAmount { ResourceName = "MonoPropellant", AmountMicroUnits = 1_000_000 },
                new ResourceAmount { ResourceName = "Oxidizer", AmountMicroUnits = 1_000_000 }]
        };
        var routeCommand = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = routeRequest,
            WorldId = remoteWorld, RunId = remoteRun, CommandKind = "routeUpsert",
            Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } };
        var submitted = await remoteClient.SubmitCommandAsync(routeCommand, TimeSpan.FromSeconds(3));
        if (submitted.Status == "rejected") throw new InvalidDataException("Route rejected: " + submitted.Reason);
        var routeStatus = await WaitForAccepted(remoteClient, routeRequest, remoteWorld, remoteRun, remoteTimeout);
        if (routeStatus.Status != "accepted") throw new InvalidDataException("Route not accepted: " + routeStatus.Status + " " + routeStatus.Reason);
        var acceptedProjection = await remoteClient.GetAcceptedStateAsync(remoteWorld, remoteRun, TimeSpan.FromSeconds(3));
        if (acceptedProjection.Status != "available" || acceptedProjection.AcceptedCapsule is null) throw new InvalidDataException("Accepted route capsule unavailable.");
        var acceptedRoute = AcceptedStateCodec.ReadCapsule(acceptedProjection.AcceptedCapsule).RouteVersions.SingleOrDefault(x => x.RouteId == routeId && x.Version == 1);
        if (acceptedRoute is null || acceptedRoute.SourceMembershipHash != source.MembershipHash || acceptedRoute.DestinationMembershipHash != destination.MembershipHash ||
            acceptedRoute.Resources.Length != 3) throw new InvalidDataException("Exact three-resource route did not persist.");
        var sendCommand = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = sendRequest,
            WorldId = remoteWorld, RunId = remoteRun, CommandKind = "sendOnce",
            Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = routeId, RouteVersionNumber = 1 } };
        var deadline = Stopwatch.GetTimestamp() + (long)(remoteTimeout * (double)Stopwatch.Frequency);
        SubmitCommandResult send;
        do
        {
            send = await remoteClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
            if (send.Status != "rejected" || send.OperationId is not null || send.Reason != "Fresh source stock or the complete rollback/persistence write capability is unavailable.") break;
            await Task.Delay(1000);
        } while (Stopwatch.GetTimestamp() < deadline);
        if (send.Status == "rejected") throw new InvalidDataException("Send once rejected: " + send.Reason);
        var settled = await WaitForAccepted(remoteClient, sendRequest, remoteWorld, remoteRun, remoteTimeout);
        if (settled.Status != "accepted" || string.IsNullOrEmpty(settled.OperationId))
            throw new InvalidDataException("Send once did not settle before the duplicate check: " + settled.Status);
        var beforeDuplicate = await remoteClient.GetAcceptedStateAsync(remoteWorld, remoteRun, TimeSpan.FromSeconds(3));
        if (beforeDuplicate.Status != "available" || beforeDuplicate.AcceptedCapsule is null)
            throw new InvalidDataException("Accepted dispatch capsule unavailable before duplicate check.");
        var dispatchedState = AcceptedStateCodec.ReadCapsule(beforeDuplicate.AcceptedCapsule);
        var dispatchShipment = dispatchedState.ActiveShipments.SingleOrDefault(x => x.RouteId == routeId && x.RouteVersion == 1);
        if (dispatchShipment is null || dispatchedState.Receipts.Count(x => x.OperationId == settled.OperationId && x.OperationKind == "dispatch" && x.Outcome == "accepted") != 1)
            throw new InvalidDataException("Accepted dispatch does not have exactly one shipment and receipt.");
        var beforeDuplicateHash = AcceptedStateCodec.ComputeHash(dispatchedState);
        var duplicate = await remoteClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
        if (duplicate.Status != "accepted" || duplicate.OperationId != settled.OperationId)
            throw new InvalidDataException("Duplicate sendOnce was not resolved to the original accepted operation: " + duplicate.Status);
        var afterDuplicate = await remoteClient.GetAcceptedStateAsync(remoteWorld, remoteRun, TimeSpan.FromSeconds(3));
        if (afterDuplicate.Status != "available" || afterDuplicate.AcceptedCapsule is null)
            throw new InvalidDataException("Accepted dispatch capsule unavailable after duplicate check.");
        var afterDuplicateState = AcceptedStateCodec.ReadCapsule(afterDuplicate.AcceptedCapsule);
        if (afterDuplicateState.AcceptedSequence != dispatchedState.AcceptedSequence ||
            AcceptedStateCodec.ComputeHash(afterDuplicateState) != beforeDuplicateHash ||
            afterDuplicateState.ActiveShipments.Count(x => x.ShipmentId == dispatchShipment.ShipmentId) != 1 ||
            afterDuplicateState.Receipts.Count(x => x.OperationId == settled.OperationId && x.OperationKind == "dispatch" && x.Outcome == "accepted") != 1)
            throw new InvalidDataException("Duplicate sendOnce changed accepted sequence, capsule, shipment, or dispatch receipt.");
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "remoteDeliverySubmitResult", status = settled.Status,
            worldId = remoteWorld, runId = remoteRun, sourceDepotId = remoteSource, destinationDepotId = remoteDestination,
            manifest = "LiquidFuel=1,MonoPropellant=1,Oxidizer=1", routeId, routeVersion = 1,
            routeRequestId = routeRequest, routeOperationId = routeStatus.OperationId, sendRequestId = sendRequest,
            sendOperationId = settled.OperationId, acceptedRevision = settled.AcceptedRevision,
            acceptedSequence = settled.AcceptedSequence, stateHash = settled.StateHash,
            duplicateStatus = duplicate.Status, duplicateOperationId = duplicate.OperationId,
            duplicateSequence = afterDuplicateState.AcceptedSequence, duplicateCapsuleHash = beforeDuplicateHash,
            duplicateShipmentId = dispatchShipment.ShipmentId, reason = settled.Reason }, ClockProtocol.JsonOptions));
        return settled.Status == "accepted" ? 0 : 1;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "remoteDeliverySubmitResult", status = "error", worldId = remoteWorld,
            runId = remoteRun, routeId, routeRequestId = routeRequest, sendRequestId = sendRequest, reason = Bound(ex.Message) }, ClockProtocol.JsonOptions));
        return 1;
    }
}
if (mode == "physical-3b-submit")
{
    if (!values.TryGetValue("--command-pipe", out var devCommandPipe) || !values.TryGetValue("--world-id", out var physicalWorldId) || !values.TryGetValue("--run-id", out var physicalRunId) ||
        !values.TryGetValue("--source-depot-id", out var sourceDepotId) || !values.TryGetValue("--destination-depot-id", out var destinationDepotId) || !values.TryGetValue("--resource", out var resourceName) ||
        !double.TryParse(values.GetValueOrDefault("--duration-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var durationSeconds) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "90"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var timeoutSeconds) ||
        timeoutSeconds is < 5 or > 180)
    {
        Console.Error.WriteLine("physical-3b-submit requires a command pipe, world/run, two depot IDs, resource, positive duration, and bounded timeout (5..180 seconds)."); return 2;
    }
    if (physicalWorldId.Length is < 1 or > 128 || physicalRunId.Length is < 1 or > 128 || sourceDepotId.Length is < 1 or > 128 || destinationDepotId.Length is < 1 or > 128 || sourceDepotId == destinationDepotId ||
        resourceName.Length is < 1 or > 64 || resourceName.Any(char.IsControl) || !double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 1e15)
    { Console.Error.WriteLine("The supplied context, depot, resource or duration values are invalid or exceed protocol bounds."); return 2; }
    if (!IsCurrentUserDevCommandPipe(devCommandPipe))
    { Console.Error.WriteLine("Refusing non-dev or other-user command pipe. Expected this user's ExpanseFoundations.Commands.dev.<user>.<token> endpoint."); return 2; }

    var routeId = "route-" + Guid.NewGuid().ToString("N");
    var routeRequestId = "route-upsert-" + Guid.NewGuid().ToString("N");
    var sendRequestId = "send-once-" + Guid.NewGuid().ToString("N");
    var commandClient = new RecoveryCommandClient(devCommandPipe);
    try
    {
        var before = await commandClient.GetAcceptedStateAsync(physicalWorldId, physicalRunId, TimeSpan.FromSeconds(3));
        if (before.Status != "available" || before.AcceptedCapsule is null)
            return PhysicalResult("unavailable", routeRequestId, sendRequestId, null, null, before.Reason ?? "Accepted state is not available for this world/run.");
        var state = AcceptedStateCodec.ReadCapsule(before.AcceptedCapsule);
        var source = state.Depots.SingleOrDefault(d => d.Active && d.DepotId == sourceDepotId);
        var destination = state.Depots.SingleOrDefault(d => d.Active && d.DepotId == destinationDepotId);
        if (state.SchemaVersion != 2 || source is null || destination is null)
            return PhysicalResult("rejected", routeRequestId, sendRequestId, null, null, "Both requested endpoints must be active in the verified schema-2 accepted depot mirror.");

        var routeVersion = state.RouteVersions.Where(r => r.RouteId == routeId).Select(r => r.Version).DefaultIfEmpty(0).Max() + 1;
        var route = new RouteVersionRecord
        {
            RouteId = routeId,
            Version = routeVersion,
            SourceDepotId = source.DepotId,
            SourceMembershipRevision = source.MembershipRevision,
            SourceMembershipHash = source.MembershipHash,
            DestinationDepotId = destination.DepotId,
            DestinationMembershipRevision = destination.MembershipRevision,
            DestinationMembershipHash = destination.MembershipHash,
            TravelDurationSeconds = durationSeconds,
            Provenance = "dev physical-3b one-shot fixture",
            Resources = new[] { new ResourceAmount { ResourceName = resourceName, AmountMicroUnits = 1_000_000 } }
        };
        var routeCommand = new SubmitCommand
        {
            ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = routeRequestId,
            WorldId = physicalWorldId, RunId = physicalRunId, CommandKind = "routeUpsert",
            Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route }
        };
        var routeSubmit = await commandClient.SubmitCommandAsync(routeCommand, TimeSpan.FromSeconds(3));
        if (routeSubmit.Status == "rejected") return PhysicalResult("routeRejected", routeRequestId, sendRequestId, null, null, routeSubmit.Reason);
        var routeStatus = await WaitForAccepted(commandClient, routeRequestId, physicalWorldId, physicalRunId, timeoutSeconds);
        if (routeStatus.Status != "accepted") return PhysicalResult("route" + Capitalize(routeStatus.Status), routeRequestId, sendRequestId, routeStatus, null, routeStatus.Reason);

        // Re-read the verified projection after the route receipt so the send references the accepted route version.
        var afterRoute = await commandClient.GetAcceptedStateAsync(physicalWorldId, physicalRunId, TimeSpan.FromSeconds(3));
        if (afterRoute.Status != "available" || afterRoute.AcceptedCapsule is null)
            return PhysicalResult("routeAcceptedStateUnavailable", routeRequestId, sendRequestId, routeStatus, null, afterRoute.Reason);
        var acceptedAfterRoute = AcceptedStateCodec.ReadCapsule(afterRoute.AcceptedCapsule);
        var savedRoute = acceptedAfterRoute.RouteVersions.SingleOrDefault(r => r.RouteId == routeId && r.Version == routeVersion);
        if (savedRoute is null || savedRoute.SourceMembershipHash != source.MembershipHash || savedRoute.DestinationMembershipHash != destination.MembershipHash)
            return PhysicalResult("routeVerificationFailed", routeRequestId, sendRequestId, routeStatus, null, "The verified accepted capsule does not contain the exact submitted route version.");

        var sendCommand = new SubmitCommand
        {
            ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = sendRequestId,
            WorldId = physicalWorldId, RunId = physicalRunId, CommandKind = "sendOnce",
            Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = routeId, RouteVersionNumber = routeVersion }
        };
        var sendDeadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * (double)Stopwatch.Frequency);
        var sendTransientRetries = 0;
        var sendSubmit = await commandClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
        while (sendSubmit.Status == "rejected" && sendSubmit.OperationId is null &&
               sendSubmit.Reason == "Fresh source stock or the complete rollback/persistence write capability is unavailable." &&
               Stopwatch.GetTimestamp() < sendDeadline)
        {
            sendTransientRetries++;
            await Task.Delay(1000);
            sendSubmit = await commandClient.SubmitCommandAsync(sendCommand, TimeSpan.FromSeconds(3));
        }
        if (sendSubmit.Status == "rejected") return PhysicalResult("sendRejected", routeRequestId, sendRequestId, routeStatus, null, sendSubmit.Reason, routeId, routeVersion);
        var sendStatus = await WaitForAccepted(commandClient, sendRequestId, physicalWorldId, physicalRunId, timeoutSeconds);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            messageType = "physical3bSubmitResult", status = sendStatus.Status == "accepted" ? "accepted" : sendStatus.Status,
            worldId = physicalWorldId, runId = physicalRunId, sourceDepotId, destinationDepotId, resourceName,
            amountMicroUnits = 1_000_000, durationSeconds, routeId, routeVersion,
            routeRequestId, routeOperationId = routeStatus.OperationId, routeStatus = routeStatus.Status,
            sendRequestId, sendOperationId = sendStatus.OperationId, sendStatus = sendStatus.Status, sendTransientRetries,
            acceptedRevision = sendStatus.AcceptedRevision, acceptedSequence = sendStatus.AcceptedSequence,
            stateHash = sendStatus.StateHash, outcome = sendStatus.AcceptedOutcome, reason = sendStatus.Reason
        }, ClockProtocol.JsonOptions));
        return sendStatus.Status == "accepted" ? 0 : 1;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "physical3bSubmitResult", status = "error", worldId = physicalWorldId, runId = physicalRunId, routeId, routeRequestId, sendRequestId, error = Bound(ex.Message) }, ClockProtocol.JsonOptions));
        return 1;
    }
}
if (mode == "physical-3b-rules-submit")
{
    string[] required = ["--command-pipe", "--world-id", "--run-id", "--source-depot-id", "--destination-depot-id", "--route-id", "--repeat-rule-id", "--repeat-next-due-ut", "--repeat-interval-seconds", "--keep-stock-rule-id", "--keep-stock-resource", "--keep-stock-low-micro-units", "--keep-stock-target-micro-units", "--keep-stock-batch-micro-units"];
    if (required.Any(k => !values.ContainsKey(k)) ||
        !double.TryParse(values.GetValueOrDefault("--repeat-next-due-ut"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var repeatNextUt) ||
        !double.TryParse(values.GetValueOrDefault("--repeat-interval-seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var repeatInterval) ||
        !long.TryParse(values.GetValueOrDefault("--keep-stock-low-micro-units"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var low) ||
        !long.TryParse(values.GetValueOrDefault("--keep-stock-target-micro-units"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var target) ||
        !long.TryParse(values.GetValueOrDefault("--keep-stock-batch-micro-units"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var batch) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "90"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rulesTimeout) || rulesTimeout is < 5 or > 180)
    { Console.Error.WriteLine("physical-3b-rules-submit requires all route/rule arguments and timeout 5..180 seconds."); return 2; }

    var rulesPipe = values["--command-pipe"]; var rulesWorld = values["--world-id"]; var rulesRun = values["--run-id"];
    var rulesSource = values["--source-depot-id"]; var rulesDestination = values["--destination-depot-id"]; var rulesRouteId = values["--route-id"];
    var repeatRuleId = values["--repeat-rule-id"]; var keepRuleId = values["--keep-stock-rule-id"]; var keepResource = values["--keep-stock-resource"];
    if (!IsCurrentUserDevCommandPipe(rulesPipe) || rulesWorld.Length is < 1 or > 128 || rulesRun.Length is < 1 or > 128 || rulesSource.Length is < 1 or > 128 || rulesDestination.Length is < 1 or > 128 || rulesSource == rulesDestination || rulesRouteId.Length is < 1 or > 128 || repeatRuleId.Length is < 1 or > 128 || keepRuleId.Length is < 1 or > 128 || repeatRuleId == keepRuleId || keepResource.Length is < 1 or > 64 || keepResource.Any(char.IsControl) || !double.IsFinite(repeatNextUt) || repeatNextUt < 0 || !double.IsFinite(repeatInterval) || repeatInterval <= 0 || repeatInterval > 1e15 || low < 0 || target <= low || batch <= 0)
    { Console.Error.WriteLine("The rule context, IDs, thresholds or dev pipe are invalid or outside bounds."); return 2; }

    var rulesClient = new RecoveryCommandClient(rulesPipe);
    var repeatRequestId = "repeat-rule-" + Guid.NewGuid().ToString("N"); var keepRequestId = "keep-stock-rule-" + Guid.NewGuid().ToString("N");
    try
    {
        var initialProjection = await rulesClient.GetAcceptedStateAsync(rulesWorld, rulesRun, TimeSpan.FromSeconds(3));
        if (initialProjection.Status != "available" || initialProjection.AcceptedCapsule is null) return RulesResult("unavailable", null, null, initialProjection.Reason ?? "Accepted state unavailable.");
        var initial = AcceptedStateCodec.ReadCapsule(initialProjection.AcceptedCapsule);
        var sourceDepot = initial.Depots.SingleOrDefault(d => d.Active && d.DepotId == rulesSource);
        var destinationDepot = initial.Depots.SingleOrDefault(d => d.Active && d.DepotId == rulesDestination);
        var route = initial.RouteVersions.Where(r => !r.LegacyOpaque && r.RouteId == rulesRouteId).OrderByDescending(r => r.Version).FirstOrDefault();
        if (initial.SchemaVersion != 2 || sourceDepot is null || destinationDepot is null || route is null || route.SourceDepotId != rulesSource || route.DestinationDepotId != rulesDestination ||
            route.SourceMembershipRevision != sourceDepot.MembershipRevision || route.SourceMembershipHash != sourceDepot.MembershipHash || route.DestinationMembershipRevision != destinationDepot.MembershipRevision || route.DestinationMembershipHash != destinationDepot.MembershipHash ||
            route.Resources.Length != 1 || route.Resources[0].ResourceName != keepResource || route.Resources[0].AmountMicroUnits != batch)
            return RulesResult("rejected", null, null, "The exact active route/endpoints must match and its single resource amount must equal keep-stock batch size.");

        var keepRule = new DeliveryRuleRecord { RuleId = keepRuleId, Kind = "keepStock", RouteId = route.RouteId, RouteVersion = route.Version, Enabled = true, ResourceName = keepResource, LowTriggerMicroUnits = low, TargetMicroUnits = target, BatchSizeMicroUnits = batch };
        var keepStatus = await SubmitRuleAndVerify(rulesClient, rulesWorld, rulesRun, keepRequestId, keepRule, rulesTimeout);
        if (keepStatus.Status != "accepted") return RulesResult("keepStock" + Capitalize(keepStatus.Status), null, keepStatus, keepStatus.Reason ?? "Keep-stock rule was not accepted.");

        var repeatRule = new DeliveryRuleRecord { RuleId = repeatRuleId, Kind = "repeat", RouteId = route.RouteId, RouteVersion = route.Version, Enabled = true, NextDueUt = repeatNextUt, IntervalSeconds = repeatInterval };
        var repeatStatus = await SubmitRuleAndVerify(rulesClient, rulesWorld, rulesRun, repeatRequestId, repeatRule, rulesTimeout);
        if (repeatStatus.Status != "accepted") return RulesResult("repeat" + Capitalize(repeatStatus.Status), repeatStatus, keepStatus, repeatStatus.Reason ?? "Repeat rule was not accepted.");

        var finalProjection = await rulesClient.GetAcceptedStateAsync(rulesWorld, rulesRun, TimeSpan.FromSeconds(3));
        if (finalProjection.Status != "available" || finalProjection.AcceptedCapsule is null) return RulesResult("acceptedProjectionUnavailable", repeatStatus, keepStatus, finalProjection.Reason);
        var finalState = AcceptedStateCodec.ReadCapsule(finalProjection.AcceptedCapsule);
        var savedRepeat = finalState.DeliveryRules.SingleOrDefault(x => x.RuleId == repeatRuleId);
        var savedKeep = finalState.DeliveryRules.SingleOrDefault(x => x.RuleId == keepRuleId);
        if (savedRepeat is null || savedKeep is null || savedRepeat.Kind != "repeat" || savedKeep.Kind != "keepStock" || savedKeep.ResourceName != keepResource || savedKeep.LowTriggerMicroUnits != low || savedKeep.TargetMicroUnits != target || savedKeep.BatchSizeMicroUnits != batch)
            return RulesResult("verificationFailed", repeatStatus, keepStatus, "Verified accepted capsule does not contain both exact submitted rule records.");
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "physical3bRulesResult", status = "accepted", worldId = rulesWorld, runId = rulesRun, routeId = rulesRouteId, routeVersion = route.Version, sourceDepotId = rulesSource, destinationDepotId = rulesDestination, repeatRuleId, repeatRequestId, repeatOperationId = repeatStatus.OperationId, repeatNextDueUt = savedRepeat.NextDueUt, repeatIntervalSeconds = savedRepeat.IntervalSeconds, keepStockRuleId = keepRuleId, keepStockRequestId = keepRequestId, keepStockOperationId = keepStatus.OperationId, resourceName = savedKeep.ResourceName, lowTriggerMicroUnits = low, targetMicroUnits = target, batchSizeMicroUnits = batch, acceptedRevision = finalState.Revision, acceptedSequence = finalState.AcceptedSequence, stateHash = AcceptedStateCodec.ComputeHash(finalState) }, ClockProtocol.JsonOptions));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    { return RulesResult("error", null, null, Bound(ex.Message)); }

    int RulesResult(string status, SubmitCommandResult? repeat, SubmitCommandResult? keep, string? reason)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "physical3bRulesResult", status, worldId = rulesWorld, runId = rulesRun, routeId = rulesRouteId, repeatRequestId, repeatOperationId = repeat?.OperationId, repeatStatus = repeat?.Status, keepStockRequestId = keepRequestId, keepStockOperationId = keep?.OperationId, keepStockStatus = keep?.Status, reason = reason is null ? null : Bound(reason) }, ClockProtocol.JsonOptions));
        return 1;
    }
}

if (mode == "rules-disable")
{
    if (!values.TryGetValue("--command-pipe", out var disablePipe) || !values.TryGetValue("--world-id", out var disableWorld) || !values.TryGetValue("--run-id", out var disableRun) ||
        !values.TryGetValue("--repeat-rule-id", out var disableRepeatId) || !values.TryGetValue("--keep-stock-rule-id", out var disableKeepId) ||
        !int.TryParse(values.GetValueOrDefault("--timeout-seconds", "90"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var disableTimeout) || disableTimeout is < 5 or > 180)
    { Console.Error.WriteLine("rules-disable requires dev pipe, exact world/run, both rule IDs, and timeout 5..180 seconds."); return 2; }
    if (!IsCurrentUserDevCommandPipe(disablePipe) || disableWorld.Length is < 1 or > 128 || disableRun.Length is < 1 or > 128 || disableRepeatId.Length is < 1 or > 128 || disableKeepId.Length is < 1 or > 128 || disableRepeatId == disableKeepId)
    { Console.Error.WriteLine("Rule IDs/context are invalid or command pipe is not a current-user dev endpoint."); return 2; }

    var disableClient = new RecoveryCommandClient(disablePipe);
    try
    {
        // Stop the time-triggered rule first; keep-stock is still protected by the accepted inbound-cargo projection while the second command settles.
        var repeatResult = await DisableRule(disableRepeatId, "repeat");
        if (repeatResult.Status is not ("accepted" or "alreadyDisabled")) return DisableResult("repeat" + Capitalize(repeatResult.Status), repeatResult, null, repeatResult.Reason);
        var keepResult = await DisableRule(disableKeepId, "keepStock");
        if (keepResult.Status is not ("accepted" or "alreadyDisabled")) return DisableResult("keepStock" + Capitalize(keepResult.Status), repeatResult, keepResult, keepResult.Reason);
        var finalProjection = await disableClient.GetAcceptedStateAsync(disableWorld, disableRun, TimeSpan.FromSeconds(3));
        if (finalProjection.Status != "available" || finalProjection.AcceptedCapsule is null) return DisableResult("projectionUnavailable", repeatResult, keepResult, finalProjection.Reason);
        var finalState = AcceptedStateCodec.ReadCapsule(finalProjection.AcceptedCapsule);
        var repeat = finalState.DeliveryRules.SingleOrDefault(x => x.RuleId == disableRepeatId); var keep = finalState.DeliveryRules.SingleOrDefault(x => x.RuleId == disableKeepId);
        if (repeat is null || keep is null || repeat.Enabled || keep.Enabled || repeat.Kind != "repeat" || keep.Kind != "keepStock") return DisableResult("verificationFailed", repeatResult, keepResult, "Final accepted capsule does not show both exact rules disabled.");
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "rulesDisableResult", status = "accepted", worldId = disableWorld, runId = disableRun, repeatRuleId = disableRepeatId, repeatOperationId = repeatResult.OperationId, repeatStatus = repeatResult.Status, keepStockRuleId = disableKeepId, keepStockOperationId = keepResult.OperationId, keepStockStatus = keepResult.Status, acceptedRevision = finalState.Revision, acceptedSequence = finalState.AcceptedSequence, stateHash = AcceptedStateCodec.ComputeHash(finalState) }, ClockProtocol.JsonOptions));
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
    { return DisableResult("error", null, null, Bound(ex.Message)); }

    async Task<SubmitCommandResult> DisableRule(string ruleId, string expectedKind)
    {
        var requestId = StableFixtureRequestId("m5-disable", disableWorld, disableRun, ruleId);
        var projection = await disableClient.GetAcceptedStateAsync(disableWorld, disableRun, TimeSpan.FromSeconds(3));
        if (projection.Status != "available" || projection.AcceptedCapsule is null) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = requestId, Status = "unavailable", Reason = projection.Reason };
        var state = AcceptedStateCodec.ReadCapsule(projection.AcceptedCapsule);
        var current = state.DeliveryRules.SingleOrDefault(x => x.RuleId == ruleId && !x.LegacyOpaque);
        if (current is null || current.Kind != expectedKind) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = requestId, Status = "rejected", Reason = "The exact accepted rule is missing or has the wrong type." };
        if (!current.Enabled) return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = requestId, Status = "alreadyDisabled", AcceptedRevision = state.Revision, AcceptedSequence = state.AcceptedSequence, StateHash = AcceptedStateCodec.ComputeHash(state) };
        var disabled = new DeliveryRuleRecord { RuleId = current.RuleId, Revision = state.Revision + 1, Kind = current.Kind, RouteId = current.RouteId, RouteVersion = current.RouteVersion, Enabled = false, NextDueUt = current.NextDueUt, IntervalSeconds = current.IntervalSeconds, WaitingRequest = false, WaitingScheduledUt = current.WaitingScheduledUt, WaitingCoalescedSlots = 0, ResourceName = current.ResourceName, LowTriggerMicroUnits = current.LowTriggerMicroUnits, TargetMicroUnits = current.TargetMicroUnits, BatchSizeMicroUnits = current.BatchSizeMicroUnits };
        var command = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = requestId, WorldId = disableWorld, RunId = disableRun, CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = disabled } };
        var submitted = await SubmitWithBoundedPreparedRetry(disableClient, command, disableTimeout);
        if (submitted.Status == "rejected") return submitted;
        var settled = await WaitForAccepted(disableClient, requestId, disableWorld, disableRun, disableTimeout);
        if (settled.Status != "accepted" || settled.AcceptedCapsule is null) return settled;
        var acceptedState = AcceptedStateCodec.ReadCapsule(settled.AcceptedCapsule);
        var acceptedRule = acceptedState.DeliveryRules.SingleOrDefault(x => x.RuleId == ruleId);
        var receipt = acceptedState.Receipts.SingleOrDefault(x => x.OperationId == settled.OperationId && x.OperationKind == "ruleUpsert" && x.Outcome == "accepted");
        if (acceptedRule is null || acceptedRule.Kind != expectedKind || acceptedRule.Enabled || receipt is null || receipt.PayloadHash != OperationIdentity.RulePayloadHash(disabled))
            return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = requestId, Status = "rejected", Reason = "Accepted receipt/capsule does not verify the disabled rule edit." };
        return settled;
    }

    int DisableResult(string status, SubmitCommandResult? repeat, SubmitCommandResult? keep, string? reason)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { messageType = "rulesDisableResult", status, worldId = disableWorld, runId = disableRun, repeatRuleId = disableRepeatId, repeatOperationId = repeat?.OperationId, repeatStatus = repeat?.Status, keepStockRuleId = disableKeepId, keepStockOperationId = keep?.OperationId, keepStockStatus = keep?.Status, reason = reason is null ? null : Bound(reason) }, ClockProtocol.JsonOptions));
        return 1;
    }
}

if (!values.TryGetValue("--command-pipe", out var pipeName) || !values.TryGetValue("--world-id", out var worldId) || !values.TryGetValue("--run-id", out var runId) || !values.TryGetValue("--request-id", out var requestId))
{ Console.Error.WriteLine("Missing required command-pipe or context/request argument."); return 2; }
if (!long.TryParse(values.GetValueOrDefault("--delta", "1"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var delta) || delta <= 0)
{ Console.Error.WriteLine("--delta must be a positive integer."); return 2; }

var request = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = requestId, WorldId = worldId, RunId = runId, CommandKind = "counterIncrement", CounterDelta = delta };
var client = new RecoveryCommandClient(pipeName);
try
{
    if (mode == "submit")
    {
        var result = await client.SubmitCommandAsync(request, TimeSpan.FromSeconds(3));
        Console.WriteLine(JsonSerializer.Serialize(result, ClockProtocol.JsonOptions));
        return result.Status == "rejected" ? 1 : 0;
    }

    string? operationId = null; string? finalStatus = null; var pendingResponses = 0; var acceptedResponses = 0;
    for (var i = 0; i < 1000; i++)
    {
        var result = await client.SubmitCommandAsync(request, TimeSpan.FromSeconds(3));
        if (result.Status == "rejected") { Console.Error.WriteLine(JsonSerializer.Serialize(result, ClockProtocol.JsonOptions)); return 1; }
        if (operationId is null) operationId = result.OperationId;
        else if (operationId != result.OperationId) { Console.Error.WriteLine("Idempotency operation ID changed during duplicate submissions."); return 1; }
        if (result.Status == "pending") pendingResponses++;
        else if (result.Status == "accepted") acceptedResponses++;
        else { Console.Error.WriteLine("Host returned an unknown command state."); return 1; }
        finalStatus = result.Status;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { messageType = "duplicateProbeResult", requests = 1000, uniqueOperationIds = 1, operationId, finalStatus, pendingResponses, acceptedResponses, counterDelta = delta }, ClockProtocol.JsonOptions));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, ClockProtocol.JsonOptions));
    return 1;
}

static bool IsCurrentUserDevCommandPipe(string pipeName)
{
    var user = Environment.UserName;
    if (string.IsNullOrWhiteSpace(user)) return false;
    var prefix = $"ExpanseFoundations.Commands.dev.{user}.";
    if (!pipeName.StartsWith(prefix, StringComparison.Ordinal)) return false;
    var token = pipeName[prefix.Length..];
    if (token.Length is < 1 or > 64 || token.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))) return false;
    return string.Equals(EffectsProtocol.CreateDevCommandPipeName(token), pipeName, StringComparison.Ordinal);
}

static async Task<SubmitCommandResult> WaitForAccepted(RecoveryCommandClient client, string requestId, string worldId, string runId, int timeoutSeconds)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * (double)Stopwatch.Frequency);
    while (true)
    {
        var status = await client.GetCommandStatusAsync(requestId, worldId, runId, TimeSpan.FromSeconds(3));
        if (status.Status != "pending") return status;
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return status;
        await Task.Delay((int)Math.Min(500, Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency))));
    }
}

static async Task<SubmitCommandResult> SubmitWithBoundedPreparedRetry(RecoveryCommandClient client, SubmitCommand request, int timeoutSeconds)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * (double)Stopwatch.Frequency);
    while (true)
    {
        var result = await client.SubmitCommandAsync(request, TimeSpan.FromSeconds(3));
        if (result.Status != "rejected" || result.Reason != "Another command is already prepared for this world.") return result;
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining <= 0) return result;
        await Task.Delay((int)Math.Min(500, Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency))));
    }
}

static async Task<SubmitCommandResult> SubmitRuleAndVerify(RecoveryCommandClient client, string worldId, string runId, string requestId, DeliveryRuleRecord rule, int timeoutSeconds)
{
    var request = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = requestId, WorldId = worldId, RunId = runId, CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = rule } };
    var submitted = await SubmitWithBoundedPreparedRetry(client, request, timeoutSeconds);
    if (submitted.Status == "rejected") return submitted;
    var settled = await WaitForAccepted(client, requestId, worldId, runId, timeoutSeconds);
    if (settled.Status != "accepted" || settled.AcceptedCapsule is null) return settled;
    var state = AcceptedStateCodec.ReadCapsule(settled.AcceptedCapsule);
    var receipt = state.Receipts.SingleOrDefault(x => x.OperationId == settled.OperationId && x.WorldId == worldId);
    var savedRule = state.DeliveryRules.SingleOrDefault(x => x.RuleId == rule.RuleId);
    if (receipt is null || receipt.Outcome != "accepted" || receipt.OperationKind != "ruleUpsert" || receipt.PayloadHash != OperationIdentity.RulePayloadHash(rule) || savedRule is null || savedRule.Kind != rule.Kind || savedRule.RouteId != rule.RouteId || savedRule.RouteVersion != rule.RouteVersion || !savedRule.Enabled ||
        (rule.Kind == "repeat" && (savedRule.NextDueUt < rule.NextDueUt || savedRule.IntervalSeconds != rule.IntervalSeconds)) ||
        (rule.Kind == "keepStock" && (savedRule.ResourceName != rule.ResourceName || savedRule.LowTriggerMicroUnits != rule.LowTriggerMicroUnits || savedRule.TargetMicroUnits != rule.TargetMicroUnits || savedRule.BatchSizeMicroUnits != rule.BatchSizeMicroUnits)))
        return new SubmitCommandResult { ProtocolVersion = 1, MessageType = "commandStatus", ClientRequestId = requestId, Status = "rejected", Reason = "Accepted receipt/capsule does not match the exact submitted rule." };
    return settled;
}

static int PhysicalResult(string status, string routeRequestId, string sendRequestId, SubmitCommandResult? route, SubmitCommandResult? send, string? reason, string? routeId = null, long? routeVersion = null)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        messageType = "physical3bSubmitResult", status, routeRequestId, routeOperationId = route?.OperationId, routeStatus = route?.Status,
        sendRequestId, sendOperationId = send?.OperationId, sendStatus = send?.Status, routeId, routeVersion,
        acceptedRevision = send?.AcceptedRevision ?? route?.AcceptedRevision, acceptedSequence = send?.AcceptedSequence ?? route?.AcceptedSequence,
        stateHash = send?.StateHash ?? route?.StateHash, reason = Bound(reason ?? "No accepted result within the bounded wait.")
    }, ClockProtocol.JsonOptions));
    return 1;
}

static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
static string Bound(string value) => value.Length <= 256 ? value : value[..256];
static string StableFixtureRequestId(string prefix, params string[] parts)
{
    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    foreach (var part in parts)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(part);
        var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }
    return prefix + "-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}
