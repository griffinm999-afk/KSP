using System;

namespace Expanse.Domain.Colonies
{
    // Certification consumes explicit provider evidence, never storage delta alone.
    public static class ColonyUtilityQualification
    {
        public static bool PowerSupported(ColonyUtilityReport report)
        {
            return report!=null && report.ActualConnectedPath && report.FullDemandAccounted && report.ContinuousSourceQualified &&
                report.DistributionReachQualified && Positive(report.ElectricCharge) && Positive(report.NominalGenerationEcPerSecond) &&
                report.NominalDemandEcPerSecond.HasValue && report.NominalDemandEcPerSecond.Value>=0 &&
                report.NominalGenerationEcPerSecond.GetValueOrDefault()>=report.NominalDemandEcPerSecond.Value*1.1;
        }
        public static bool HeatSupported(ColonyUtilityReport report) => report!=null && report.HeatRejectionQualified && Positive(report.MinimumTemperatureMargin) && (!report.MinimumCoreShutdownMargin.HasValue || Positive(report.MinimumCoreShutdownMargin));
        static bool Positive(double? value) => value.HasValue && value.Value>0 && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
    }
}
