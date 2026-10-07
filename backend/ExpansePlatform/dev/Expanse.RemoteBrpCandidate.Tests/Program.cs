using Expanse.Recovery.KspFixture;
using BackgroundResourceProcessing;

var cases = new (string Name, Action Body)[]
{
    ("one unit conservation and dirty notification", Transfer),
    ("duplicate selected member rejected", Duplicate),
    ("duplicate identities across vessel objects rejected", CrossVesselDuplicate),
    ("stale stock rejected", Stale),
    ("full destination rejected", Full),
    ("nonfinite amount rejected", Nonfinite),
    ("locked resource rejected", Locked),
    ("missing provider inventory rejected", Missing),
    ("unloaded identity mismatch rejected", Identity),
    ("apply fault restores both provider rows", Rollback),
    ("rollback fault is reported as uncertain", RollbackFault)
};
foreach (var test in cases)
{
    test.Body();
    Console.WriteLine("PASS " + test.Name);
}

static (RemoteBrpTransactionCandidate.Selection, RemoteBrpTransactionCandidate.Selection,
    BackgroundResourceProcessor, BackgroundResourceProcessor, FakeResource, FakeResource) Pair()
{
    var a = new FakeResource { resourceName = "LiquidFuel", amount = 5, maxAmount = 10 };
    var b = new FakeResource { resourceName = "LiquidFuel", amount = 1, maxAmount = 10 };
    a.resourceValues.SetValue("amount", "5", false);
    b.resourceValues.SetValue("amount", "1", false);
    var pa = new FakePart { persistentId = 11, flightID = 101, resources = [a] };
    var pb = new FakePart { persistentId = 22, flightID = 202, resources = [b] };
    var ia = new FakeInventory { FlightId = 101, ResourceName = "LiquidFuel", Amount = 5, OriginalAmount = 5, MaxAmount = 10, Snapshot = a };
    var ib = new FakeInventory { FlightId = 202, ResourceName = "LiquidFuel", Amount = 1, OriginalAmount = 1, MaxAmount = 10, Snapshot = b };
    var va = new FakeVessel { protoVessel = new FakeProto { protoPartSnapshots = [pa] } };
    var vb = new FakeVessel { protoVessel = new FakeProto { protoPartSnapshots = [pb] } };
    var ba = new BackgroundResourceProcessor { Inventories = [ia] };
    var bb = new BackgroundResourceProcessor { Inventories = [ib] };
    va.vesselModules.Add(ba); vb.vesselModules.Add(bb);
    return (new() { Vessel = va, PersistentId = 11, FlightId = 101, ResourceName = "LiquidFuel", ObservedAmount = 5, ObservedCapacity = 10 },
        new() { Vessel = vb, PersistentId = 22, FlightId = 202, ResourceName = "LiquidFuel", ObservedAmount = 1, ObservedCapacity = 10 }, ba, bb, a, b);
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Transfer()
{
    var (a, b, pa, pb, sa, sb) = Pair();
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(r.Applied && !r.Faulted, r.Reason);
    Check(sa.amount == 4 && sb.amount == 2, "Proto quantities");
    Check(pa.Inventories[0].Amount == 4 && pa.Inventories[0].OriginalAmount == 4 &&
          pb.Inventories[0].Amount == 2 && pb.Inventories[0].OriginalAmount == 2, "Provider quantities");
    Check(pa.Dirty && pb.Dirty && sa.WrittenAmount == 4 && sb.WrittenAmount == 2, "Provider notifications");
}
static void Duplicate()
{
    var (a, b, pa, _, sa, _) = Pair(); b.Vessel = a.Vessel; b.PersistentId = a.PersistentId;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && !pa.Dirty, "Duplicate rejection");
}
static void CrossVesselDuplicate()
{
    var (a, b, _, _, sa, sb) = Pair(); b.PersistentId = a.PersistentId; b.FlightId = a.FlightId;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1, "Cross-vessel duplicate rejection");
}
static void Stale()
{
    var (a, b, pa, pb, sa, sb) = Pair(); a.ObservedAmount = 4;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1 && !pa.Dirty && !pb.Dirty, "Stale rejection");
}
static void Full()
{
    var (a, b, _, pb, sa, sb) = Pair(); sb.amount = 10; sb.resourceValues.SetValue("amount", "10", false);
    pb.Inventories[0].Amount = 10; pb.Inventories[0].OriginalAmount = 10; b.ObservedAmount = 10;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 10, "Full destination rejection");
}
static void Nonfinite()
{
    var (a, b, _, _, sa, sb) = Pair(); sa.amount = double.NaN;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && double.IsNaN(sa.amount) && sb.amount == 1, "Nonfinite rejection");
}
static void Locked()
{
    var (a, b, _, _, sa, sb) = Pair(); sb.flowState = false;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1, "Lock rejection");
}
static void Missing()
{
    var (a, b, _, pb, sa, sb) = Pair(); pb.Inventories.Clear();
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1, "Missing inventory rejection");
}
static void Identity()
{
    var (a, b, _, _, sa, sb) = Pair(); b.FlightId = 999;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1, "Identity rejection");
}
static void Rollback()
{
    var (a, b, pa, pb, sa, sb) = Pair(); sb.ThrowOnce = true;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && !r.Faulted && sa.amount == 5 && sb.amount == 1, "Proto rollback");
    Check(pa.Inventories[0].Amount == 5 && pa.Inventories[0].OriginalAmount == 5 &&
          pb.Inventories[0].Amount == 1 && pb.Inventories[0].OriginalAmount == 1, "Provider rollback");
    Check(sa.resourceValues.GetValues("amount")[0] == "5" && sb.resourceValues.GetValues("amount")[0] == "1", "ConfigNode rollback");
}
static void RollbackFault()
{
    var (a, b, _, _, _, sb) = Pair(); sb.ThrowAlways = true;
    var r = RemoteBrpTransactionCandidate.Transfer(a, b, 1);
    Check(!r.Applied && r.Faulted && r.Reason.Contains("Rollback unconfirmed"), "Uncertain rollback fault");
}

internal sealed class FakeVessel
{
    public bool loaded;
    public FakeProto protoVessel = new();
    public List<object> vesselModules = [];
}
internal sealed class FakeProto { public List<FakePart> protoPartSnapshots = []; }
internal sealed class FakePart
{
    public uint persistentId, flightID;
    public List<FakeResource> resources = [];
}
internal sealed class FakeResource
{
    public string resourceName = "";
    public bool flowState = true;
    public double amount, maxAmount, WrittenAmount;
    public bool ThrowOnce, ThrowAlways;
    public FakeNode resourceValues = new();
    public FakeResource() { resourceValues.SetValue("amount", "0", false); resourceValues.SetValue("maxAmount", "10", false); }
    public void UpdateConfigNodeAmounts()
    {
        if (ThrowAlways) throw new InvalidOperationException("Injected persistent write failure");
        if (ThrowOnce) { ThrowOnce = false; throw new InvalidOperationException("Injected write failure"); }
        WrittenAmount = amount;
        resourceValues.SetValue("amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        resourceValues.SetValue("maxAmount", maxAmount.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
    }
}
internal sealed class FakeNode
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public string[] GetValues(string name) => values.TryGetValue(name, out var value) ? [value] : [];
    public bool SetValue(string name, string value, bool create) { values[name] = value; return true; }
}
internal sealed class FakeInventory
{
    public uint FlightId;
    public uint? ModuleId;
    public string ResourceName = "";
    public double Amount, OriginalAmount, MaxAmount;
    public FakeResource Snapshot = null!;
}
namespace BackgroundResourceProcessing
{
    internal sealed class BackgroundResourceProcessor
    {
        public List<FakeInventory> Inventories = [];
        public bool Dirty;
        public void UpdateBackgroundState() { }
        public void MarkDirty() { Dirty = true; }
    }
}
