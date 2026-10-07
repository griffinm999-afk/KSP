using Expanse.Clock.Core;
using Expanse.Clock.Host;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class RemoteIntentTests
{
    [Fact]
    public void MixedSpecializedTanksAllocateOnlyPresentResources()
    {
        var members = new[]
        {
            Member(11, Stock("LiquidFuel", 5, 10)),
            Member(12, Stock("Oxidizer", 7, 10)),
            Member(13, Stock("MonoPropellant", 3, 10))
        };
        var intent = Build(members, [Amount("LiquidFuel", 2), Amount("Oxidizer", 4), Amount("MonoPropellant", 1)], out var hold);
        Assert.NotNull(intent);
        Assert.Equal(string.Empty, hold);
        Assert.Equal(3, intent.Deltas.Length);
        Assert.Equal(new uint[] { 11, 12, 13 }, intent.Deltas.Select(x => x.MemberPersistentId).Order().ToArray());
        Assert.Equal(-7_000_000, intent.Deltas.Sum(x => x.DeltaMicroUnits));
    }

    [Fact]
    public void MissingOrInsufficientResourceDoesNotCreatePartialIntent()
    {
        var members = new[] { Member(11, Stock("LiquidFuel", 5, 10)), Member(12, Stock("Oxidizer", 1, 10)) };
        Assert.Null(Build(members, [Amount("LiquidFuel", 2), Amount("MonoPropellant", 1)], out _));
        Assert.Null(Build(members, [Amount("Oxidizer", 2)], out _));
    }

    [Fact]
    public void InvalidPresentRowFailsEvenWhenItsResourceIsNotRequested()
    {
        var bad = Stock("Oxidizer", 11, 10);
        Assert.Null(Build([Member(11, Stock("LiquidFuel", 5, 10), bad)], [Amount("LiquidFuel", 1)], out _));
    }

    [Fact]
    public void SeventeenPhysicalRowsHoldWithSpecificReason()
    {
        var members = Enumerable.Range(1, 17).Select(x => Member((uint)x, Stock("LiquidFuel", 1, 2))).ToArray();
        Assert.Null(Build(members, [Amount("LiquidFuel", 17)], out var hold));
        Assert.Contains("17 tank/resource changes", hold);
        Assert.Contains("16-row physical-effect safety limit", hold);
    }

    private static PhysicalEffectIntent? Build(MemberStockObservation[] members, ResourceAmount[] amounts, out string hold)
    {
        var ids = members.Select(x => x.MemberPersistentId).Order().ToArray();
        var capability = new InventoryCapability { DepotId = "minmus", MembershipRevision = 1,
            MembershipHash = new string('a', 64), AnchorPersistentId = ids[0],
            MemberSetHash = OperationIdentity.ComputeMemberSetHash(ids[0], ids),
            ProviderId = "BackgroundResourceProcessing.Remote", ProviderVersion = "0.2.7", Scene = "SpaceCenter",
            ObservationAvailable = true, ReadSupported = true, WriteSupported = true,
            SynchronousRollbackSupported = true, PersistenceSyncSupported = true };
        var observation = new StockObservation { MemberStocks = members };
        return RecoveryCoordinator.BuildIntent("dispatchDebit", capability, observation, amounts, true, out hold);
    }

    private static MemberStockObservation Member(uint id, params StockAmount[] stocks) => new() { MemberPersistentId = id, Resources = stocks };
    private static StockAmount Stock(string name, long amount, long capacity) => new() { ResourceName = name, AmountMicroUnits = amount * 1_000_000, CapacityMicroUnits = capacity * 1_000_000, DebitAllowed = true };
    private static ResourceAmount Amount(string name, long amount) => new() { ResourceName = name, AmountMicroUnits = amount * 1_000_000 };
}
