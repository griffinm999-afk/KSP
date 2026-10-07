using System.Text;

namespace Expanse.Clock.Core;

public sealed record ResourceRow(string Name, string? DisplayName, double Amount, double MaxAmount);

public sealed record StockSnapshot(Guid SessionId, Guid LoadEpoch, Guid WorldId, Guid DepotId,
    int MembershipRevision, long Revision, double StartedUt, double CompletedUt, double AgeSeconds, ResourceRow[] Resources);

public sealed record DepotPayload(int SchemaVersion, string RegistryState, string? Reason, Guid? WorldId,
    Guid? DepotId, int? MembershipRevision, string? Label, uint? AnchorPartId, int? MemberCount,
    string? CurrentVesselName, string ObservationState, string? ObservationReason, StockSnapshot? Snapshot);

/// <summary>Bounded all-depot view. Null collection means the publisher predates this optional surface.</summary>
public sealed record DepotSummaryView(string DepotId, string Label, long MembershipRevision, string MembershipHash,
    string Status, string? Reason, double? StockAgeSeconds, int? MemberCount, ResourceRow[] Resources);

public sealed record DepotView(string Status, string? Reason, Guid? WorldId, Guid? DepotId, string? Label,
    string? CurrentVesselName, int? MemberCount, long? SnapshotRevision, double? StockAgeSeconds, ResourceRow[] Resources);

internal static class DepotRules
{
    static bool HasId(Guid? value) => value.HasValue && value.Value != Guid.Empty;
    static bool Fits(string? value, int maxBytes = 1024) => value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes;

    public static void Validate(DepotPayload depot, ClockSample sample)
    {
        ValidateMetadata(depot, sample);
        if (depot.Snapshot is not null) ValidateSnapshot(depot.Snapshot, sample, depot);
    }

    public static void ValidateMetadata(DepotPayload depot, ClockSample sample)
    {
        if (depot.SchemaVersion != 1) throw new InvalidDataException("Unsupported depot schema version.");
        if (!Fits(depot.Reason) || !Fits(depot.ObservationReason) || !Fits(depot.Label) || !Fits(depot.CurrentVesselName)) throw new InvalidDataException("Depot text field exceeds 1024 UTF-8 bytes.");
        if (depot.RegistryState is not ("noWorld" or "loading" or "none" or "registered" or "unavailable")) throw new InvalidDataException("Unknown depot registry state.");
        if (depot.ObservationState is not ("none" or "collecting" or "complete" or "unavailable")) throw new InvalidDataException("Unknown depot observation state.");

        if (depot.RegistryState is "noWorld" or "loading")
        {
            if (sample.ActiveWorld) throw new InvalidDataException("No-world/loading depot state conflicts with an active clock world.");
            if (depot.WorldId is not null || depot.DepotId is not null || depot.MembershipRevision is not null || depot.Label is not null || depot.AnchorPartId is not null || depot.MemberCount is not null || depot.CurrentVesselName is not null || depot.Snapshot is not null)
                throw new InvalidDataException("No-world/loading depot state must not carry world identity or stock.");
            if (depot.ObservationState != "none") throw new InvalidDataException("No-world/loading depot state must have no observation.");
            return;
        }

        if (depot.RegistryState == "none")
        {
            if (!sample.ActiveWorld || !HasId(depot.WorldId) || depot.DepotId is not null || depot.MembershipRevision is not null || depot.Label is not null || depot.AnchorPartId is not null || depot.MemberCount is not null || depot.CurrentVesselName is not null || depot.Snapshot is not null || depot.ObservationState != "none")
                throw new InvalidDataException("Empty depot registry has invalid identity or stock fields.");
            return;
        }

        if (depot.RegistryState == "unavailable")
        {
            if (!sample.ActiveWorld || depot.DepotId is not null || depot.MembershipRevision is not null || depot.Label is not null || depot.AnchorPartId is not null || depot.MemberCount is not null || depot.CurrentVesselName is not null || depot.Snapshot is not null || depot.ObservationState != "unavailable")
                throw new InvalidDataException("Unavailable registry must not carry depot identity or stock.");
            return;
        }

        if (depot.RegistryState == "registered")
        {
            if (!sample.ActiveWorld || !HasId(depot.WorldId) || !HasId(depot.DepotId) || depot.MembershipRevision is not > 0 || string.IsNullOrWhiteSpace(depot.Label) || depot.AnchorPartId is not > 0 || depot.MemberCount is < 1 or > 4096)
                throw new InvalidDataException("Registered depot is missing required identity or membership fields.");
            if (depot.ObservationState == "complete" && depot.Snapshot is null) throw new InvalidDataException("Complete depot observation requires a stock snapshot.");
            if (depot.ObservationState == "none" && depot.Snapshot is not null) throw new InvalidDataException("Unobserved depot cannot include a stock snapshot.");
            return;
        }
    }

    public static void ValidateSnapshot(StockSnapshot snapshot, ClockSample sample, DepotPayload depot)
    {
        if (depot.RegistryState != "registered" || depot.ObservationState == "none") throw new InvalidDataException("Snapshot is inconsistent with depot registry state.");
        if (snapshot.SessionId == Guid.Empty || snapshot.LoadEpoch == Guid.Empty || snapshot.WorldId == Guid.Empty || snapshot.DepotId == Guid.Empty || snapshot.Revision <= 0 || snapshot.MembershipRevision <= 0)
            throw new InvalidDataException("Snapshot identity or revision is invalid.");
        if (snapshot.SessionId != sample.SessionId || snapshot.LoadEpoch != sample.LoadEpoch || snapshot.WorldId != depot.WorldId || snapshot.DepotId != depot.DepotId || snapshot.MembershipRevision != depot.MembershipRevision)
            throw new InvalidDataException("Snapshot does not match the current session, epoch, world, depot and membership revision.");
        if (!double.IsFinite(snapshot.StartedUt) || !double.IsFinite(snapshot.CompletedUt) || !double.IsFinite(snapshot.AgeSeconds) || snapshot.AgeSeconds < 0)
            throw new InvalidDataException("Snapshot observation time or age is invalid.");
        if (snapshot.Resources is null || snapshot.Resources.Length is < 1 or > 128) throw new InvalidDataException("Snapshot resource count is outside 1..128.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot.Resources)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.Name) || !Fits(row.Name, 128) || !Fits(row.DisplayName, 128)) throw new InvalidDataException("Resource identity or label is invalid.");
            if (!names.Add(row.Name)) throw new InvalidDataException("Snapshot contains duplicate resource names.");
            if (!double.IsFinite(row.Amount) || !double.IsFinite(row.MaxAmount) || row.Amount < 0 || row.MaxAmount < 0) throw new InvalidDataException("Resource quantities must be finite and nonnegative.");
            var tolerance = Math.Max(1d, row.MaxAmount) * 1e-9;
            if (row.Amount > row.MaxAmount + tolerance) throw new InvalidDataException("Resource amount exceeds capacity.");
        }
    }

    public static DepotPayload Unavailable(ClockSample sample, string reason)
    {
        if (!sample.ActiveWorld) return new(1, "noWorld", null, null, null, null, null, null, null, null, "none", null, null);
        return new(1, "unavailable", reason, null, null, null, null, null, null, null, "unavailable", reason, null);
    }

    public static void Validate(DepotView view)
    {
        if (view.Status is not ("waitingForKsp" or "updateBridge" or "noWorld" or "loading" or "noDepot" or "unavailable" or "live" or "lastObserved")) throw new InvalidDataException("Invalid depot view status.");
        if (!Fits(view.Reason) || !Fits(view.Label) || !Fits(view.CurrentVesselName)) throw new InvalidDataException("Depot view text exceeds 1024 UTF-8 bytes.");
        if (view.WorldId == Guid.Empty || view.DepotId == Guid.Empty || view.MemberCount is < 1 or > 4096 || view.SnapshotRevision is <= 0 || view.StockAgeSeconds is double age && (!double.IsFinite(age) || age < 0)) throw new InvalidDataException("Depot view identity, count or age is invalid.");
        if (view.Resources is null || view.Resources.Length > 128) throw new InvalidDataException("Depot view resource count exceeds 128.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in view.Resources)
            if (row is null || string.IsNullOrWhiteSpace(row.Name) || !Fits(row.Name, 128) || !Fits(row.DisplayName, 128) || !names.Add(row.Name) || !double.IsFinite(row.Amount) || !double.IsFinite(row.MaxAmount) || row.Amount < 0 || row.MaxAmount < 0 || row.Amount > row.MaxAmount + Math.Max(1d, row.MaxAmount) * 1e-9)
                throw new InvalidDataException("Depot view contains an invalid resource row.");
    }
}

internal static class DepotSummaryRules
{
    static bool Fits(string? value, int maxBytes = 1024) => value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes;
    static bool Hash(string value) => value is not null && value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    public static void Validate(DepotSummaryView[]? depots, bool activeWorld)
    {
        if (depots is null) return;
        if (!activeWorld || depots.Length > 8) throw new InvalidDataException("All-depot view is invalid outside an active world or exceeds eight rows.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var depot in depots)
        {
            if (depot is null || string.IsNullOrWhiteSpace(depot.DepotId) || !Fits(depot.DepotId, 128) || !ids.Add(depot.DepotId) || string.IsNullOrWhiteSpace(depot.Label) || !Fits(depot.Label) || depot.MembershipRevision <= 0 || !Hash(depot.MembershipHash)) throw new InvalidDataException("All-depot identity or membership fingerprint is invalid.");
            if (depot.Status is not ("live" or "lastObserved" or "unavailable" or "loading") || !Fits(depot.Reason) || depot.StockAgeSeconds is double age && (!double.IsFinite(age) || age < 0) || depot.MemberCount is < 1 or > 4096 || depot.Resources is null || depot.Resources.Length > 128) throw new InvalidDataException("All-depot status, age, membership count or resource bound is invalid.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in depot.Resources)
                if (row is null || string.IsNullOrWhiteSpace(row.Name) || !Fits(row.Name, 128) || !Fits(row.DisplayName, 128) || !names.Add(row.Name) || !double.IsFinite(row.Amount) || !double.IsFinite(row.MaxAmount) || row.Amount < 0 || row.MaxAmount < 0 || row.Amount > row.MaxAmount + Math.Max(1, row.MaxAmount) * 1e-9) throw new InvalidDataException("All-depot resource row is invalid.");
        }
    }
}
