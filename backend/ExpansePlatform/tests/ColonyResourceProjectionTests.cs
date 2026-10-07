using Expanse.Clock.Core;
using Xunit;

public sealed class ColonyResourceProjectionTests
{
    [Fact]
    public void IncludesUnstoredRecipeOutputsAndKeepsFacilityDetail()
    {
        var converter = new ColonyConverter("Agriculture Support", "Fertilizer", true,
            ["Gypsum", "Substrate"], ["Fertilizer"]);
        var vessel = new ColonyVessel("v1", "Ag Support", "Minmus", "Greater Flats", 0, 0,
            "loaded", 2, [new ColonyTank("Gypsum", 5, 10, true, true, true)], [converter]);

        var resources = ColonyResourceProjection.ForSite([vessel]);

        Assert.Equal(["Fertilizer", "Gypsum", "Substrate"], resources.Select(r => r.Resource));
        var fertilizer = Assert.Single(resources.Where(r => r.Resource == "Fertilizer"));
        Assert.Null(fertilizer.Amount);
        Assert.Equal(1, fertilizer.ProducerCount);
        Assert.Equal("Ag Support", Assert.Single(fertilizer.Facilities).Name);
        var gypsum = Assert.Single(resources.Where(r => r.Resource == "Gypsum"));
        Assert.Equal(5, gypsum.Amount);
        Assert.Equal(10, gypsum.Capacity);
        Assert.Equal(1, gypsum.ConsumerCount);
    }

    [Fact]
    public void AggregatesCaseInsensitiveStockWithoutInventingUnloadedConverters()
    {
        ColonyVessel Vessel(string id, string resource, double amount) =>
            new(id, id, "Minmus", "Greater Flats", 0, 0, "snapshot", 0,
                [new ColonyTank(resource, amount, 10, null, null, null)], []);

        var row = Assert.Single(ColonyResourceProjection.ForSite([
            Vessel("one", "Water", 2), Vessel("two", "water", 3)]));

        Assert.Equal(5, row.Amount);
        Assert.Equal(20, row.Capacity);
        Assert.Equal(2, row.Facilities.Length);
        Assert.Equal(0, row.ProducerCount);
    }
}
