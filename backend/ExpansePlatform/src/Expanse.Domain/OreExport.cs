using System;
using System.IO;

namespace Expanse.Domain
{
    public static class OreExportPolicy
    {
        public const string KerbinBuyerId = "kerbin-recovery";
        public const string VirtualDestinationKind = "virtualKerbinRecovery";
        public const string PhysicalDestinationKind = "physicalDepot";
        public const long BatchMicroUnits = 1_000_000_000;
        public const long FundsPerUnit = 500;
        public const long FundsPerRecovery = 500_000;
        public const long DefaultBatchMicroUnits = BatchMicroUnits;
        public const long DefaultFundsPerUnit = 100;
        public const long MaxOreUnits = 1_000_000;
        public static bool IsValidBatch(long amount) => amount > 0 && amount <= MaxOreUnits * 1_000_000 && amount % 1_000_000 == 0;
        public static long RecoveryFunds(ResourceAmount[] rows, long rate)
        {
            if (!IsExactManifest(rows) || (rate != DefaultFundsPerUnit && rate != FundsPerUnit) || (rate == FundsPerUnit && rows[0].AmountMicroUnits != BatchMicroUnits)) throw new ArgumentException("Invalid immutable Ore recovery terms.");
            return checked(rows[0].AmountMicroUnits / 1_000_000 * rate);
        }
        public const double DefaultTravelSeconds = 64_800;
        public static bool IsExactManifest(ResourceAmount[] rows) => rows != null && rows.Length == 1 && rows[0] != null && rows[0].ResourceName == "Ore" && IsValidBatch(rows[0].AmountMicroUnits);
        public static bool IsVirtual(RouteVersionRecord route) => route.DestinationKind == VirtualDestinationKind;
        public static bool ValidBuyer(RouteVersionRecord route) => IsVirtual(route) && route.DestinationDepotId == KerbinBuyerId && route.DestinationMembershipRevision == 0 && route.DestinationMembershipHash == "" && IsExactManifest(route.Resources) && (route.FundsPerUnit == DefaultFundsPerUnit || (route.FundsPerUnit == FundsPerUnit && route.Resources[0].AmountMicroUnits == BatchMicroUnits));
        public static bool Finite(double value) => value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        public static void ValidateResult(EconomicEffectResult result)
        {
            if (result == null || (result.Status != "applied" && result.Status != "uncertain") || !Finite(result.BeforeFunds) || result.IntendedDeltaFunds <= 0 || (double)result.IntendedDeltaFunds >= 9223372036854775808d || (long)(double)result.IntendedDeltaFunds != result.IntendedDeltaFunds || !Finite(result.IntendedAfterFunds) || result.IntendedAfterFunds != result.BeforeFunds + result.IntendedDeltaFunds || result.IntendedAfterFunds - result.BeforeFunds != result.IntendedDeltaFunds || result.IntendedAfterFunds <= result.BeforeFunds || !Finite(result.ObservedAfterFunds) || result.Reason == null || result.Reason.Length > 256 || (result.Status == "applied" && (!result.ObservedAfterKnown || result.ObservedAfterFunds != result.IntendedAfterFunds)) || (result.Status == "uncertain" && String.IsNullOrWhiteSpace(result.Reason))) throw new InvalidDataException("Economic result lacks exact bounded funds evidence.");
        }
    }
    public sealed class EconomicRecoveryIntent { public string ShipmentId { get; set; } = ""; public long FundsDelta { get; set; } }
    public sealed class FundsSuccessWitness
    {
        public double BeforeFunds { get; set; }
        public long IntendedDeltaFunds { get; set; }
        public double IntendedAfterFunds { get; set; }
        public double ObservedAfterFunds { get; set; }
    }
    public sealed class EconomicEffectResult
    {
        public string Status { get; set; } = "";
        public double BeforeFunds { get; set; }
        public long IntendedDeltaFunds { get; set; }
        public double IntendedAfterFunds { get; set; }
        public bool ObservedAfterKnown { get; set; }
        public double ObservedAfterFunds { get; set; }
        public string Reason { get; set; } = "";
    }
    public sealed class EconomicFaultRecord
    {
        public string FaultId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public long CommandSequence { get; set; }
        public string ShipmentId { get; set; } = "";
        public EconomicEffectResult Result { get; set; } = new EconomicEffectResult();
    }
}
