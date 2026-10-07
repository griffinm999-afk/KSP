namespace Expanse.WorldBridge
{
    internal sealed class ColonyLifeSupportTelemetry
    {public string Status,Reason;public double ObservedUt;public LifeSupportSupplyStream Supply;public LifeSupportEcStream CrewElectricity;}
    internal sealed class LifeSupportSupplyStream
    {
        public double SampleUt,ProcessedEndUt,IntervalGameSeconds,TimeFactor,RecyclerMultiplier,GrossSuppliesPerSecond,GrossMulchPerSecond,ConfiguredSuppliesPerSecond,ConfiguredMulchPerSecond,SuppliesConsumed,MulchProduced;
        public long CaptureSequence;public int CrewCount;
    }
    internal sealed class LifeSupportEcStream
    {public double SampleUt,ProcessedEndUt,IntervalGameSeconds,TimeFactor,ConfiguredEcPerSecond,ElectricityConsumed;public long CaptureSequence;public int CrewCount;}
}
