namespace Expanse.Clock.Core;

// A site inventory view over observed physical tanks and converter recipes.
// Recipe references describe configuration, not measured production rates.
public sealed record ColonyResourceFacility(string VesselId, string Name, string ObservationBasis,
    double? Amount, double? Capacity, ColonyTank[] Tanks,
    ColonyConverter[] Producers, ColonyConverter[] Consumers);

public sealed record ColonyResourceSummary(string Resource, double? Amount, double? Capacity,
    ColonyResourceFacility[] Facilities)
{
    public int ProducerCount => Facilities.Sum(f => f.Producers.Length);
    public int ConsumerCount => Facilities.Sum(f => f.Consumers.Length);
}

public static class ColonyResourceProjection
{
    public static ColonyResourceSummary[] ForSite(IEnumerable<ColonyVessel>? vessels)
    {
        var rows = (vessels ?? Array.Empty<ColonyVessel>()).ToArray();
        var resources = rows.SelectMany(v => (v.Tanks ?? Array.Empty<ColonyTank>()).Select(t => t.Resource)
            .Concat((v.Converters ?? Array.Empty<ColonyConverter>()).SelectMany(c =>
                (c.Inputs ?? Array.Empty<string>()).Concat(c.Outputs ?? Array.Empty<string>()))))
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase);

        return resources.Select(resource =>
        {
            var facilities = rows.Select(v =>
            {
                var tanks = (v.Tanks ?? Array.Empty<ColonyTank>())
                    .Where(t => string.Equals(t.Resource, resource, StringComparison.OrdinalIgnoreCase))
                    .Where(t => double.IsFinite(t.Amount) && double.IsFinite(t.Capacity) && t.Amount >= 0 && t.Capacity >= t.Amount)
                    .ToArray();
                var converters = v.Converters ?? Array.Empty<ColonyConverter>();
                var producers = converters.Where(c => (c.Outputs ?? Array.Empty<string>())
                    .Contains(resource, StringComparer.OrdinalIgnoreCase)).ToArray();
                var consumers = converters.Where(c => (c.Inputs ?? Array.Empty<string>())
                    .Contains(resource, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (tanks.Length == 0 && producers.Length == 0 && consumers.Length == 0) return null;
                return new ColonyResourceFacility(v.VesselId, v.Name, v.ObservationBasis,
                    tanks.Length == 0 ? null : tanks.Sum(t => t.Amount),
                    tanks.Length == 0 ? null : tanks.Sum(t => t.Capacity), tanks, producers, consumers);
            }).Where(f => f is not null).Cast<ColonyResourceFacility>().ToArray();
            var stocked = facilities.Where(f => f.Amount.HasValue).ToArray();
            return new ColonyResourceSummary(resource,
                stocked.Length == 0 ? null : stocked.Sum(f => f.Amount!.Value),
                stocked.Length == 0 ? null : stocked.Sum(f => f.Capacity!.Value), facilities);
        }).ToArray();
    }
}
