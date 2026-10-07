using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Expanse.Clock.Core;

public sealed record ClockSample(int ProtocolVersion, string MessageType, long Sequence, Guid SessionId,
    Guid LoadEpoch, string InstallNamespace, string? SaveFolder, string? SaveTitle, double? UtSeconds,
    bool ActiveWorld, string Scene, bool? Paused, string? FormattedDate, double? WarpRate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DepotPayload? Depot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DepotSummaryView[]? Depots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorldId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonySnapshot? Colony = null);

public sealed record ClockView(int ProtocolVersion, string MessageType, string Status, ClockSample? Sample, double? AgeSeconds, bool PublisherConnected,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DepotView? DepotView = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DepotSummaryView[]? Depots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorldId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ColonySnapshot? Colony = null);

public static partial class ClockProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 256 * 1024;

    public static byte[] Encode<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (json.Length is < 1 or > MaxFrameBytes) throw new InvalidDataException("Frame size is outside the allowed range.");
        var frame = new byte[json.Length + 4]; BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length); json.CopyTo(frame.AsSpan(4)); return frame;
    }
    public static byte[] EncodeView(ClockView view)
    {
        try { return Encode(view); }
        catch (InvalidDataException) when (view.Colony is not null)
        {
            var reduced=FitProductionView(view);
            try {return Encode(view with {Colony=reduced});}catch(InvalidDataException) { }
            reduced = FitProductionView(view with { Colony = view.Colony with {
                Vessels = view.Colony.Vessels.Select(v => v with { CrewRoster = null, CrewRosterComplete = false }).ToArray() } });
            try { return Encode(view with { Colony = reduced }); }
            catch (InvalidDataException) { }
            try
            {
                return Encode(view with { Colony = reduced with {
                    VesselCensus = view.Colony.VesselCensus is { } existingCensus
                        ? new ColonyVesselCensus("truncated", Array.Empty<string>(), existingCensus.ObservationSequence,
                            "Census omitted to fit the view frame.") : null,
                    Vessels = reduced.Vessels.Select(v => v with { CrewRoster = null, CrewRosterComplete = false }).ToArray() } });
            }
            catch (InvalidDataException) { }
            // Colony observation is optional. Preserve the clock, depot and route
            // context if a large colony pushes a view over the pipe frame limit.
            var truncated = new ColonySnapshot("truncated",
                "Colony observation exceeded the view message limit.", view.Colony.ObservedUt,
                Array.Empty<ColonyVessel>(), VesselCensus: view.Colony.VesselCensus is { } census
                    ? new ColonyVesselCensus("truncated", Array.Empty<string>(), census.ObservationSequence,
                        "Colony view frame limit.") : null);
            try { return Encode(view with { Colony = truncated }); }
            catch (InvalidDataException) { return Encode(view with { Colony = truncated with { VesselCensus = null } }); }
        }
    }

    public static async ValueTask WriteFrameAsync<T>(Stream stream, T value, CancellationToken ct)
    { await stream.WriteAsync(Encode(value), ct); await stream.FlushAsync(ct); }

    public static async ValueTask<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await ReadExactlyAsync(stream, header, ct);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (n is < 1 or > MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[(int)n]; await ReadExactlyAsync(stream, bytes, ct); return bytes;
    }

    public static async ValueTask<T?> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    { var bytes = await ReadFrameAsync(stream, ct); return JsonSerializer.Deserialize<T>(bytes, JsonOptions); }

    private static async ValueTask ReadExactlyAsync(Stream s, Memory<byte> b, CancellationToken ct)
    { var i = 0; while (i < b.Length) { var n = await s.ReadAsync(b[i..], ct); if (n == 0) throw new EndOfStreamException("Truncated frame."); i += n; } }

    public static void Validate(ClockSample s)
    {
        static bool Fits(string? x) => x is null || Encoding.UTF8.GetByteCount(x) <= 1024;
        if (s.ProtocolVersion != Version || s.MessageType != "clockSample") throw new InvalidDataException("Unsupported message version or type.");
        if (s.Sequence <= 0 || s.SessionId == Guid.Empty || s.LoadEpoch == Guid.Empty) throw new InvalidDataException("Invalid sequence or identifier.");
        if (string.IsNullOrWhiteSpace(s.InstallNamespace) || !Path.IsPathFullyQualified(s.InstallNamespace) || string.IsNullOrWhiteSpace(s.Scene)) throw new InvalidDataException("Invalid namespace or scene.");
        if (!Fits(s.InstallNamespace) || !Fits(s.SaveFolder) || !Fits(s.SaveTitle) || !Fits(s.Scene) || !Fits(s.FormattedDate)) throw new InvalidDataException("Text field exceeds 1024 UTF-8 bytes.");
        if ((s.WorldId is null) != (s.RunId is null)) throw new InvalidDataException("World and run context identifiers must be published together.");
        if (s.WorldId is not null && (string.IsNullOrWhiteSpace(s.WorldId) || Encoding.UTF8.GetByteCount(s.WorldId) > 128) || s.RunId is not null && (string.IsNullOrWhiteSpace(s.RunId) || Encoding.UTF8.GetByteCount(s.RunId) > 128)) throw new InvalidDataException("World or run identity is invalid.");
        if (s.UtSeconds is double ut && !double.IsFinite(ut) || s.WarpRate is double warp && (!double.IsFinite(warp) || warp <= 0)) throw new InvalidDataException("Invalid numeric field.");
        if (s.ActiveWorld && (s.UtSeconds is null || s.SaveFolder is null) || !s.ActiveWorld && (s.UtSeconds is not null || s.SaveFolder is not null || s.SaveTitle is not null || s.FormattedDate is not null || s.WarpRate is not null)) throw new InvalidDataException("Invalid active-world field combination.");
        if (s.Depot is not null) DepotRules.Validate(s.Depot, s);
        DepotSummaryRules.Validate(s.Depots, s.ActiveWorld);
        ValidateColony(s.Colony, s.ActiveWorld, s.UtSeconds);
    }

    public static void Validate(ClockView view)
    {
        if (view.ProtocolVersion != Version || view.MessageType != "clockView") throw new InvalidDataException("Unsupported view response version or type.");
        if (view.Status is not ("waitingForKsp" or "noWorld" or "live" or "paused" or "stale")) throw new InvalidDataException("Invalid clock view status.");
        if (view.AgeSeconds is double age && (!double.IsFinite(age) || age < 0)) throw new InvalidDataException("Invalid view age.");
        if (view.Sample is not null) Validate(view.Sample);
        if (view.DepotView is not null) DepotRules.Validate(view.DepotView);
        DepotSummaryRules.Validate(view.Depots, view.Sample?.ActiveWorld == true);
        ValidateColony(view.Colony, view.Sample?.ActiveWorld == true, view.Sample?.UtSeconds);
        if ((view.WorldId is null) != (view.RunId is null) || view.Sample is not null && (view.WorldId != view.Sample.WorldId || view.RunId != view.Sample.RunId)) throw new InvalidDataException("View world/run context disagrees with its sample.");
        if (view.WorldId is not null && (string.IsNullOrWhiteSpace(view.WorldId) || Encoding.UTF8.GetByteCount(view.WorldId) > 128) || view.RunId is not null && (string.IsNullOrWhiteSpace(view.RunId) || Encoding.UTF8.GetByteCount(view.RunId) > 128)) throw new InvalidDataException("View world or run identity is invalid.");
    }
    private static void ValidateColony(ColonySnapshot? colony, bool activeWorld, double? currentUt)
    {
        if (colony is null) return;
        static bool Fits(string? value, int bytes) => value is null || Encoding.UTF8.GetByteCount(value) <= bytes;
        if (!activeWorld || colony.Status is not ("observed" or "unavailable" or "truncated") ||
            colony.Vessels is null || colony.Vessels.Length > 24 ||
            !Fits(colony.Reason, 300) ||
            colony.Status == "unavailable" && colony.Vessels.Length != 0 ||
            colony.ObservedUt is double ut && (!double.IsFinite(ut) || ut < 0 || currentUt is double now && ut > now + 1))
            throw new InvalidDataException("Invalid colony snapshot context.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int rosterPeople = 0, rosterBytes = 0;
        int vesselIndex = -1;
        foreach (var vessel in colony.Vessels)
        {
            vesselIndex++;
            try
            {
            if (vessel is null || string.IsNullOrWhiteSpace(vessel.VesselId) || string.IsNullOrWhiteSpace(vessel.Name) ||
                string.IsNullOrWhiteSpace(vessel.Body) || string.IsNullOrWhiteSpace(vessel.Biome) ||
                !Fits(vessel.VesselId, 80) || !Fits(vessel.Name, 200) || !Fits(vessel.Body, 100) || !Fits(vessel.Biome, 100) ||
                !identities.Add(vessel.VesselId) ||
                vessel.ObservationBasis is not ("loaded" or "snapshot") ||
                !double.IsFinite(vessel.Latitude) || !double.IsFinite(vessel.Longitude) ||
                vessel.Latitude is < -90 or > 90 || vessel.Longitude is < -180 or > 180 ||
                vessel.Crew < 0 || vessel.Tanks is null || vessel.Tanks.Length > 24 ||
                vessel.Converters is null || vessel.Converters.Length > 12)
                throw new InvalidDataException("Invalid colony vessel.");
            if (vessel.PhysicalCrewCapacity is int capacity && (capacity < vessel.Crew || capacity > 16384))
                throw new InvalidDataException("Invalid physical crew capacity.");
            if (vessel.CrewRosterComplete)
            {
                if (vessel.CrewRoster is null || vessel.CrewRoster.Length != vessel.Crew || vessel.CrewRoster.Length > 64)
                    throw new InvalidDataException("Invalid complete crew roster.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var person in vessel.CrewRoster)
                {
                    if (person is null || string.IsNullOrWhiteSpace(person.Name) || string.IsNullOrWhiteSpace(person.Profession) ||
                        !Fits(person.Name, 160) || !Fits(person.Profession, 64) || !names.Add(person.Name))
                        throw new InvalidDataException("Invalid crew member.");
                    rosterBytes += Encoding.UTF8.GetByteCount(person.Name) + Encoding.UTF8.GetByteCount(person.Profession) + 48;
                }
                rosterPeople += vessel.CrewRoster.Length;
                if (rosterPeople > 96 || rosterBytes > 8192) throw new InvalidDataException("Crew roster exceeds observation budget.");
            }
            else if (vessel.CrewRoster is { Length: > 0 })
                throw new InvalidDataException("Incomplete crew roster must omit names.");
            foreach (var tank in vessel.Tanks)
                if (tank is null || string.IsNullOrWhiteSpace(tank.Resource) || !Fits(tank.Resource, 80) ||
                    tank.Role is not ("installed" or "storage" or "other") || !double.IsFinite(tank.Amount) ||
                    !double.IsFinite(tank.Capacity) || tank.Amount < 0 || tank.Capacity < tank.Amount)
                    throw new InvalidDataException("Invalid colony tank.");
            ColonyProductionTelemetryProtocol.Validate(vessel.Production,vessel.ObservationBasis,colony.ObservedUt);
            ColonyLifeSupportTelemetryProtocol.Validate(vessel.LifeSupport,vessel.PowerAverage,vessel.ObservationBasis,colony.ObservedUt,vessel.Crew);
            foreach (var converter in vessel.Converters)
                if (converter is null || string.IsNullOrWhiteSpace(converter.PartName) || string.IsNullOrWhiteSpace(converter.Recipe) ||
                    !Fits(converter.PartName, 200) || !Fits(converter.Recipe, 200) ||
                    converter.Inputs is null || converter.Outputs is null || converter.Inputs.Length > 16 || converter.Outputs.Length > 16 ||
                    converter.Inputs.Any(x => string.IsNullOrWhiteSpace(x) || !Fits(x, 80)) ||
                    converter.Outputs.Any(x => string.IsNullOrWhiteSpace(x) || !Fits(x, 80)))
                    throw new InvalidDataException("Invalid colony converter.");
            if (vessel.Power is { } power)
            {
                bool hasRates = power.GenerationEcPerSecond.HasValue &&
                    power.ConsumptionEcPerSecond.HasValue && power.NetEcPerSecond.HasValue;
                double generation = power.GenerationEcPerSecond.GetValueOrDefault();
                double consumption = power.ConsumptionEcPerSecond.GetValueOrDefault();
                double net = power.NetEcPerSecond.GetValueOrDefault();
                if (power.Status is not ("partial" or "unavailable") || !Fits(power.Reason, 300) ||
                    power.SampleUt is double sampleUt && (!double.IsFinite(sampleUt) || sampleUt < 0 ||
                        colony.ObservedUt is double observedUt && sampleUt > observedUt + 1) ||
                    power.Status == "partial" &&
                        (!hasRates || power.SampleUt is null || power.WindowSeconds is not double seconds ||
                         !double.IsFinite(seconds) || seconds <= 0 || seconds > 30 ||
                         !double.IsFinite(generation) || !double.IsFinite(consumption) || !double.IsFinite(net) ||
                         generation < 0 || consumption < 0 || generation > 1e12 || consumption > 1e12 ||
                         Math.Abs(generation - consumption - net) > 0.0001 * Math.Max(1, generation + consumption)) ||
                    power.Status == "unavailable" &&
                        (power.WindowSeconds is not null || power.GenerationEcPerSecond is not null ||
                         power.ConsumptionEcPerSecond is not null || power.NetEcPerSecond is not null))
                    throw new InvalidDataException("Invalid colony power rate.");
            }
            if (vessel.PowerEstimate is { } estimate &&
                (vessel.ObservationBasis != "loaded" || estimate.Status != "nominal" ||
                 !Fits(estimate.Reason, 300) || estimate.ModuleCount is < 1 or > 160 ||
                 !double.IsFinite(estimate.GenerationEcPerSecond) || !double.IsFinite(estimate.ConsumptionEcPerSecond) ||
                 estimate.GenerationEcPerSecond < 0 || estimate.ConsumptionEcPerSecond < 0 ||
                 estimate.GenerationEcPerSecond > 1e9 || estimate.ConsumptionEcPerSecond > 1e9 ||
                 estimate.GenerationEcPerSecond + estimate.ConsumptionEcPerSecond <= 0))
                throw new InvalidDataException("Invalid colony power estimate.");
            }
            catch (InvalidDataException ex)
            {
                ex.Data["ValidationPath"] = "$.colony.vessels[" + vesselIndex + "]";
                throw;
            }
        }
        if (colony.VesselCensus is { } census)
        {
            if (census.Status is not ("complete" or "unavailable" or "truncated") ||
                census.VesselIds is null || census.VesselIds.Length > 512 || !Fits(census.Reason, 300) ||
                census.ObservationSequence is null or < 0 or > 9007199254740991L)
                throw new InvalidDataException("Invalid vessel census.");
            if (census.Status == "complete")
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (colony.Status != "observed" || census.VesselIds.Any(id =>
                    !Guid.TryParseExact(id, "D", out _) || !ids.Add(id)) ||
                    identities.Any(id => !ids.Contains(id)))
                    throw new InvalidDataException("Complete vessel census disagrees with the colony observation.");
            }
            else if (census.VesselIds.Length != 0)
                throw new InvalidDataException("An incomplete vessel census cannot assert identities.");
        }
        if (colony.Wolf is { } wolf)
        {
            if (wolf.Status is not ("observed" or "unavailable" or "truncated") ||
                !Fits(wolf.Reason, 300) || wolf.Depots is null || wolf.Depots.Length > 32 ||
                wolf.AllowedResources is null || wolf.AllowedResources.Length > 80 ||
                wolf.AllowedResources.Any(x => string.IsNullOrWhiteSpace(x) || !Fits(x, 80)) ||
                wolf.Status == "unavailable" && wolf.Depots.Length != 0 ||
                wolf.ObservedUt is double wolfUt && (!double.IsFinite(wolfUt) || wolfUt < 0 ||
                    currentUt is double currentWolfUt && wolfUt > currentWolfUt + 1))
                throw new InvalidDataException("Invalid WOLF observation.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var depot in wolf.Depots)
            {
                if (depot is null || string.IsNullOrWhiteSpace(depot.Body) || string.IsNullOrWhiteSpace(depot.Biome) ||
                    !Fits(depot.Body, 100) || !Fits(depot.Biome, 100) ||
                    !keys.Add(depot.Body + "\0" + depot.Biome) || depot.Resources is null || depot.Resources.Length > 80)
                    throw new InvalidDataException("Invalid WOLF depot.");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var resource in depot.Resources)
                    if (resource is null || string.IsNullOrWhiteSpace(resource.Name) || !Fits(resource.Name, 80) ||
                        !names.Add(resource.Name) || resource.Incoming < 0 || resource.Outgoing < 0 ||
                        resource.Available != resource.Incoming - resource.Outgoing)
                        throw new InvalidDataException("Invalid WOLF resource stream.");
            }
        }
    }

    public static ClockSample DecodeClockSample(ReadOnlyMemory<byte> utf8Json)
        => DecodeClockSample(utf8Json, null);

    public static ClockSample DecodeClockSample(ReadOnlyMemory<byte> utf8Json, Action<string>? colonyDiagnostic)
    {
        using var document = JsonDocument.Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Clock sample must be a JSON object.");
        DepotPayload? depot = null; bool depotPresent = false; JsonElement depotElement = default;
        ColonySnapshot? colony = null; bool colonyPresent = false; JsonElement colonyElement = default;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("depot")) { depotPresent = true; depotElement = property.Value; }
            if (property.NameEquals("colony")) { colonyPresent = true; colonyElement = property.Value; }
        }
        using var baseStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(baseStream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) if (!property.NameEquals("depot") && !property.NameEquals("colony")) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        var sample = JsonSerializer.Deserialize<ClockSample>(baseStream.ToArray(), JsonOptions) ?? throw new JsonException("Empty clock sample.");
        if (depotPresent && depotElement.ValueKind != JsonValueKind.Null)
        {
            try
            {
                depot = JsonSerializer.Deserialize<DepotPayload>(depotElement.GetRawText(), JsonOptions);
                if (depot is null) throw new InvalidDataException("Depot payload was empty.");
                DepotRules.ValidateMetadata(depot, sample);
                if (depot.Snapshot is not null)
                {
                    try { DepotRules.ValidateSnapshot(depot.Snapshot, sample, depot); }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
                    {
                        if (depot.RegistryState == "registered") depot = depot with { ObservationState = "unavailable", ObservationReason = "Stock snapshot rejected by Host validation.", Snapshot = null };
                        else throw;
                    }
                }
                DepotRules.Validate(depot, sample);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException)
            {
                depot = DepotRules.Unavailable(sample, "Depot payload rejected by Host validation.");
            }
        }
        else depot = null;
        if (colonyPresent && colonyElement.ValueKind != JsonValueKind.Null)
        {
            try
            {
                colony = JsonSerializer.Deserialize<ColonySnapshot>(colonyElement.GetRawText(), JsonOptions);
                if (colony is null) throw new InvalidDataException("Empty colony observation.");
                colony = NormalizeLegacyRecipeLabels(colony);
                ValidateColony(colony, sample.ActiveWorld, sample.UtSeconds);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or OverflowException)
            {
                ReportColonyDiagnostic(colonyDiagnostic, ex);
                if (colony?.Wolf is not null)
                {
                    try
                    {
                        var physical = colony with { Wolf = null };
                        ValidateColony(physical, sample.ActiveWorld, sample.UtSeconds);
                        colony = physical with { Wolf = new WolfSnapshot("unavailable",
                            "WOLF ledger observation failed validation.", sample.UtSeconds,
                            Array.Empty<WolfDepot>(), Array.Empty<string>()) };
                    }
                    catch (Exception physicalError) when (physicalError is InvalidDataException or JsonException or ArgumentException or OverflowException)
                    {
                        ReportColonyDiagnostic(colonyDiagnostic, physicalError, "physical ");
                        colony = sample.ActiveWorld
                            ? new ColonySnapshot("unavailable", "Colony observation failed validation; clock and deliveries remain available.", sample.UtSeconds, Array.Empty<ColonyVessel>())
                            : null;
                    }
                }
                else colony = sample.ActiveWorld
                    ? new ColonySnapshot("unavailable", "Colony observation failed validation; clock and deliveries remain available.", sample.UtSeconds, Array.Empty<ColonyVessel>())
                    : null;
            }
        }
        sample = sample with { Depot = depot, Colony = colony };
        Validate(sample);
        return sample;
    }

    public static async ValueTask<ClockSample> ReadClockSampleAsync(Stream stream, CancellationToken ct)
        => DecodeClockSample(await ReadFrameAsync(stream, ct));

    static void ReportColonyDiagnostic(Action<string>? callback, Exception error, string prefix = "")
    {
        if (callback is null) return;
        try { callback(prefix + ColonyValidationDiagnostic.Describe(error)); }
        catch { /* Diagnostics cannot disconnect the publisher or suppress the clock fallback. */ }
    }

    // Older publishers use null-coalescing for ConverterName, which preserves an empty
    // native display caption. The recipe hash and all physical telemetry remain authoritative.
    static ColonySnapshot NormalizeLegacyRecipeLabels(ColonySnapshot colony)
    {
        if (colony.Vessels?.Any(v => v?.Production?.Modules?.Any(m => m?.Recipe == "") == true) != true) return colony;
        return colony with { Vessels = colony.Vessels.Select(v => v is null || v.Production?.Modules is null ? v! :
            v with { Production = v.Production with { Modules = v.Production.Modules.Select(m =>
                m is not null && m.Recipe == "" ? m with { Recipe = "Unnamed native recipe" } : m!).ToArray() } }).ToArray() };
    }

    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
    static string UserSuffix
    {
        get
        {
            var userName = Environment.UserName;
            if (string.IsNullOrWhiteSpace(userName)) throw new InvalidOperationException("Cannot create the Expanse clock pipe name because Environment.UserName is empty.");
            return userName;
        }
    }
    public static string PublisherPipeName => $"ExpanseFoundations.Clock.Publisher.v1.{UserSuffix}";
    public static string ViewPipeName => $"ExpanseFoundations.Clock.View.v1.{UserSuffix}";
}

public static class EffectsProtocol
{
    public const int MaxFrameBytes = 65536;
    static string User
    {
        get { var user = Environment.UserName; if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("Cannot create Expanse effects pipe name because Environment.UserName is empty."); return user; }
    }
    public static string DefaultPipeName => $"ExpanseFoundations.Effects.v1.{User}";
    public static string DefaultCommandPipeName => $"ExpanseFoundations.Commands.v1.{User}";
    public static string CreateDevPublisherPipeName(string token)
    {
        ValidateToken(token); return $"ExpanseFoundations.Clock.Publisher.dev.{User}.{token}";
    }
    public static string CreateDevViewPipeName(string token)
    {
        ValidateToken(token); return $"ExpanseFoundations.Clock.View.dev.{User}.{token}";
    }
    public static string CreateDevPipeName(string token)
    {
        ValidateToken(token);
        return $"ExpanseFoundations.Effects.dev.{User}.{token}";
    }
    public static string CreateDevCommandPipeName(string token)
    {
        ValidateToken(token);
        return $"ExpanseFoundations.Commands.dev.{User}.{token}";
    }
    static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64 || token.Any(c => !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')))
            throw new ArgumentException("Dev pipe token must contain only ASCII letters, digits, '-' or '_' and be 1-64 characters.", nameof(token));
    }
}

public sealed class ClockState
{
    readonly object gate = new(); ClockSample? sample; long receivedAt; bool connected; long connectionGeneration, sampleGeneration, liveContextGeneration; string? liveEpoch; double? previousUt;
    readonly Queue<Guid> retiredSessions = new(); readonly HashSet<Guid> retiredSessionSet = new();
    DepotPayload? currentDepot; StockSnapshot? retainedStock; string? stockContext; long stockFirstAt; double stockFirstAge; long stockLatestAt; double stockLatestAge;
    public bool PublisherConnected { get { lock (gate) return connected; } }
    public ClockSample? CurrentSample { get { lock (gate) return sample; } }
    public void SetPublisherConnected(bool value) { lock (gate) { if (value && !connected) connectionGeneration++; connected = value; } }
    public bool MatchesFreshGameContext(Guid sessionId, Guid loadEpoch, string installNamespace, string saveFolder)
        => TryGetFreshGameContext(sessionId, loadEpoch, installNamespace, saveFolder, out _);
    public bool MatchesFreshGameContext(Guid sessionId, Guid loadEpoch, string installNamespace, string saveFolder, string worldId, string runId)
        => TryGetFreshGameContext(sessionId, loadEpoch, installNamespace, saveFolder, worldId, runId, out _);
    public bool TryGetFreshGameContext(Guid sessionId, Guid loadEpoch, string installNamespace, string saveFolder, out long contextGeneration)
    {
        lock (gate)
        {
            contextGeneration = liveContextGeneration;
            if (!connected || sample is null || sampleGeneration != connectionGeneration || !sample.ActiveWorld || sample.SessionId != sessionId || sample.LoadEpoch != loadEpoch || sample.InstallNamespace != installNamespace || sample.SaveFolder != saveFolder) return false;
            return (Environment.TickCount64 - receivedAt) < 3000;
        }
    }
    public bool TryGetFreshGameContext(Guid sessionId, Guid loadEpoch, string installNamespace, string saveFolder, string worldId, string runId, out long contextGeneration)
    {
        lock (gate)
        {
            contextGeneration = liveContextGeneration;
            if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(runId) || !connected || sample is null || sampleGeneration != connectionGeneration || !sample.ActiveWorld || sample.SessionId != sessionId || sample.LoadEpoch != loadEpoch || sample.InstallNamespace != installNamespace || sample.SaveFolder != saveFolder || sample.WorldId != worldId || sample.RunId != runId) return false;
            return (Environment.TickCount64 - receivedAt) < 3000;
        }
    }
    public void Accept(ClockSample s)
    {
        ClockProtocol.Validate(s); lock (gate)
        {
            if (sample is not null && s.SessionId != sample.SessionId && retiredSessionSet.Contains(s.SessionId)) throw new InvalidDataException("Sample belongs to a retired bridge session.");
            if (sample is not null && sample.SessionId == s.SessionId && s.Sequence <= sample.Sequence) throw new InvalidDataException("Regressing sample sequence.");
            if (sample is not null && sample.SessionId != s.SessionId)
            {
                retiredSessions.Enqueue(sample.SessionId); retiredSessionSet.Add(sample.SessionId);
                while (retiredSessions.Count > 64) retiredSessionSet.Remove(retiredSessions.Dequeue());
            }
            var transition = sample is not null && (sample.SessionId != s.SessionId || liveEpoch != s.LoadEpoch.ToString("D") || s.ActiveWorld && (sample.WorldId != s.WorldId || sample.RunId != s.RunId || previousUt is double u && s.UtSeconds < u));
            if (transition) { liveContextGeneration++; liveEpoch = s.LoadEpoch.ToString("D"); previousUt = null; }
            if (sample is null) liveEpoch = s.LoadEpoch.ToString("D");
            if (!s.ActiveWorld) { liveEpoch = null; previousUt = null; }
            else previousUt = s.UtSeconds;

            if (transition || !s.ActiveWorld || s.Depot is null || s.Depot.RegistryState != "registered") ClearStock();
            currentDepot = s.Depot;
            if (s.Depot is { RegistryState: "registered" } depot)
            {
                var context = $"{s.SessionId:D}/{s.LoadEpoch:D}/{depot.WorldId:D}/{depot.DepotId:D}/{depot.MembershipRevision}";
                if (!string.Equals(stockContext, context, StringComparison.Ordinal)) { ClearStock(); stockContext = context; }
                if (depot.ObservationState == "none") ClearStock();
                else if (depot.Snapshot is { } snapshot)
                {
                    var now = Environment.TickCount64;
                    if (retainedStock is null || snapshot.Revision > retainedStock.Revision)
                    {
                        retainedStock = snapshot; stockFirstAt = stockLatestAt = now; stockFirstAge = stockLatestAge = snapshot.AgeSeconds;
                    }
                    else if (snapshot.Revision == retainedStock.Revision)
                    {
                        stockLatestAge = Math.Max(stockLatestAge, snapshot.AgeSeconds); stockLatestAt = now;
                    }
                    else currentDepot = depot with { ObservationState = "unavailable", ObservationReason = "A regressing stock revision was ignored.", Snapshot = null };
                }
            }
            sample = s; receivedAt = Environment.TickCount64; sampleGeneration = connectionGeneration;
        }
    }
    public ClockView Snapshot()
    {
        lock (gate)
        {
            if (sample is null) return new(1, "clockView", "waitingForKsp", null, null, connected, BuildDepotView("waitingForKsp", null), null, null, null);
            var age = Math.Max(0, (Environment.TickCount64 - receivedAt) / 1000d);
            var status = age >= 3 || !connected ? "stale" : !sample.ActiveWorld ? "noWorld" : sample.Paused == true ? "paused" : "live";
            var summaries = sample.Depots?.Select(d => d with { StockAgeSeconds = d.StockAgeSeconds is double stockAge ? stockAge + age : null }).ToArray();
            return new(1, "clockView", status, sample with { Depot = null, Colony = null }, age, connected, BuildDepotView(status, age), summaries, sample.WorldId, sample.RunId,
                sample.ActiveWorld ? sample.Colony : null);
        }
    }

    DepotView BuildDepotView(string clockStatus, double? clockAge)
    {
        if (sample is null) return new("waitingForKsp", null, null, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        if (!sample.ActiveWorld) return new("noWorld", null, null, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        if (currentDepot is null) return new("updateBridge", "Update bridge for depot view.", null, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        var depot = currentDepot;
        if (depot.RegistryState == "loading") return new("loading", depot.Reason, depot.WorldId, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        if (depot.RegistryState == "none") return new("noDepot", depot.Reason, depot.WorldId, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        if (depot.RegistryState == "unavailable") return new("unavailable", depot.Reason, depot.WorldId, null, null, null, null, null, null, Array.Empty<ResourceRow>());
        if (depot.RegistryState != "registered") return new("unavailable", "Depot registry state is invalid.", null, null, null, null, null, null, null, Array.Empty<ResourceRow>());

        var stockAge = retainedStock is null ? (double?)null : EffectiveStockAge();
        var hasSameSnapshot = retainedStock is not null && stockContext is not null &&
            retainedStock.SessionId == sample.SessionId && retainedStock.LoadEpoch == sample.LoadEpoch &&
            retainedStock.WorldId == depot.WorldId && retainedStock.DepotId == depot.DepotId &&
            retainedStock.MembershipRevision == depot.MembershipRevision;
        if (!hasSameSnapshot)
            return new("unavailable", depot.ObservationReason ?? "No completed stock snapshot is available yet.", depot.WorldId, depot.DepotId, depot.Label, depot.CurrentVesselName, depot.MemberCount, null, null, Array.Empty<ResourceRow>());

        var isLive = (clockStatus is "live" or "paused") && connected && depot.ObservationState == "complete" && stockAge < 6d;
        var stockStatus = isLive ? "live" : "lastObserved";
        var reason = isLive ? null : depot.ObservationReason ?? (clockStatus == "stale" || !connected ? "Publisher disconnected; showing last observed stock." : stockAge >= 6d ? "Stock observation is older than six seconds." : "Visit this depot to refresh stock.");
        return new(stockStatus, reason, depot.WorldId, depot.DepotId, depot.Label, depot.CurrentVesselName, depot.MemberCount,
            retainedStock!.Revision, stockAge, retainedStock.Resources);
    }

    double EffectiveStockAge()
    {
        var now = Environment.TickCount64;
        var first = stockFirstAge + Math.Max(0, now - stockFirstAt) / 1000d;
        var latest = stockLatestAge + Math.Max(0, now - stockLatestAt) / 1000d;
        return Math.Max(first, latest);
    }

    void ClearStock()
    { retainedStock = null; stockContext = null; stockFirstAge = stockLatestAge = 0; stockFirstAt = stockLatestAt = 0; }
}

public sealed class ClockViewClient
{
    readonly string pipeName; public ClockViewClient(string? pipeName = null) => this.pipeName = pipeName ?? ClockProtocol.ViewPipeName;
    public async Task<ClockView> GetSnapshotAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token); await ClockProtocol.WriteFrameAsync(pipe, new { protocolVersion = 1, messageType = "getSnapshot" }, cts.Token);
        var response = await ClockProtocol.ReadJsonAsync<ClockView>(pipe, cts.Token) ?? throw new InvalidDataException("Empty view response.");
        ClockProtocol.Validate(response); return response;
    }
}
