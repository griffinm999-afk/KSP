namespace Expanse.Clock.Core;

public sealed record ColonyLifeSupportTelemetry(string Status,string Reason,double ObservedUt,LifeSupportSupplyStream? Supply,LifeSupportEcStream? CrewElectricity);
public sealed record LifeSupportSupplyStream(double SampleUt,double ProcessedEndUt,double IntervalGameSeconds,long CaptureSequence,int CrewCount,double TimeFactor,
    double RecyclerMultiplier,double GrossSuppliesPerSecond,double GrossMulchPerSecond,double ConfiguredSuppliesPerSecond,double ConfiguredMulchPerSecond,double SuppliesConsumed,double MulchProduced);
public sealed record LifeSupportEcStream(double SampleUt,double ProcessedEndUt,double IntervalGameSeconds,long CaptureSequence,int CrewCount,double TimeFactor,double ConfiguredEcPerSecond,double ElectricityConsumed);
public sealed record PowerAverageTelemetry(long WindowId,double WindowStartUt,double WindowEndUt,double WindowGameSeconds,double CoveredGameSeconds,double ReportRealSeconds,double AgeRealSeconds,
    double GenerationEc,double ConsumptionEc,double GenerationEcPerSecond,double ConsumptionEcPerSecond,string Status,string Basis,string Reason);

public static class ColonyLifeSupportTelemetryProtocol
{
    public static void Validate(ColonyLifeSupportTelemetry? value,PowerAverageTelemetry? average,string basis,double? now,int crew)
    {
        if(value is not null)
        {
            if(value.Status is not ("partial" or "unavailable")||!Text(value.Reason)||!Number(value.ObservedUt,1e15)||now is null||value.ObservedUt>now+1||value.Status=="unavailable"&&(value.Supply is not null||value.CrewElectricity is not null)||value.Status=="partial"&&value.Supply is null&&value.CrewElectricity is null)Fail();
            if(value.Supply is {} s)
            {
                Stream(s.SampleUt,s.ProcessedEndUt,s.IntervalGameSeconds,s.CaptureSequence,s.CrewCount,s.TimeFactor,value.ObservedUt,basis,crew);
                if(!Number(s.RecyclerMultiplier,1)||!Number(s.GrossSuppliesPerSecond,1e12)||!Number(s.GrossMulchPerSecond,1e12)||!Number(s.ConfiguredSuppliesPerSecond,1e12)||!Number(s.ConfiguredMulchPerSecond,1e12)||!Near(s.ConfiguredSuppliesPerSecond,s.GrossSuppliesPerSecond*s.RecyclerMultiplier)||!Near(s.ConfiguredMulchPerSecond,s.GrossMulchPerSecond*s.RecyclerMultiplier))Fail();
                Accepted(s.SuppliesConsumed,s.ConfiguredSuppliesPerSecond,s.IntervalGameSeconds);Accepted(s.MulchProduced,s.ConfiguredMulchPerSecond,s.IntervalGameSeconds);
            }
            if(value.CrewElectricity is {} e){Stream(e.SampleUt,e.ProcessedEndUt,e.IntervalGameSeconds,e.CaptureSequence,e.CrewCount,e.TimeFactor,value.ObservedUt,basis,crew);if(!Number(e.ConfiguredEcPerSecond,1e12))Fail();Accepted(e.ElectricityConsumed,e.ConfiguredEcPerSecond,e.IntervalGameSeconds);}
        }
        if(average is {} a)
        {
            if(a.WindowId<=0||a.WindowId>9007199254740991||!Number(a.WindowStartUt,1e15)||!Number(a.WindowEndUt,1e15)||now is null||a.WindowEndUt>now+1||!Number(a.WindowGameSeconds,1e15)||a.WindowGameSeconds<=0||!Near(a.WindowGameSeconds,a.WindowEndUt-a.WindowStartUt)||!Number(a.CoveredGameSeconds,a.WindowGameSeconds+1e-6)||a.CoveredGameSeconds<=0||!Number(a.ReportRealSeconds,1e12)||a.ReportRealSeconds<60||!Number(a.AgeRealSeconds,120)||!Number(a.GenerationEc,1e18)||!Number(a.ConsumptionEc,1e18)||!Number(a.GenerationEcPerSecond,1e12)||!Number(a.ConsumptionEcPerSecond,1e12)||!Near(a.GenerationEcPerSecond,a.GenerationEc/a.CoveredGameSeconds)||!Near(a.ConsumptionEcPerSecond,a.ConsumptionEc/a.CoveredGameSeconds)||a.Status is not ("observed" or "partial")||a.Basis is not ("fulfilled-part-requests" or "supported-native-callbacks")||a.Status=="observed"&&(a.Basis!="fulfilled-part-requests"||!Near(a.CoveredGameSeconds,a.WindowGameSeconds))||!Text(a.Reason))Fail();
        }
    }
    static void Stream(double sample,double end,double seconds,long sequence,int crew,double factor,double now,string basis,int vesselCrew)
    {if(basis!="loaded"||!Number(sample,now)||now-sample>10||!Number(seconds,21600)||seconds<1e-8||!Number(end,sample+1e-6)||end<seconds||sequence<=0||sequence>9007199254740991||crew<=0||crew>4096||crew!=vesselCrew||!Number(factor,seconds+1e-6))Fail();}
    static void Accepted(double amount,double rate,double seconds){if(!Number(amount,1e18)||amount>rate*seconds+Math.Max(1e-9,rate*seconds*1e-6))Fail();}
    static bool Near(double a,double b)=>double.IsFinite(a)&&double.IsFinite(b)&&Math.Abs(a-b)<=Math.Max(1e-9,Math.Abs(b)*1e-6);
    static bool Text(string? s)=>s is not null&&s.Length<=360;
    static bool Number(double n,double limit)=>double.IsFinite(n)&&n>=0&&n<=limit;
    static void Fail()=>throw new InvalidDataException("Invalid life support or power average telemetry.");
}
