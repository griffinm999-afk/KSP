using System;
using System.IO;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyReactorContinuationProof
    {
        public string WorldId { get; set; } = "";
        public string VesselId { get; set; } = "";
        public string HardwareHash { get; set; } = "";
        public string ProviderHash { get; set; } = "";
        public string RecipeHash { get; set; } = "";
        public double ObservedUt { get; set; }
        public double ExpiresUt { get; set; }
        public double FullThrottleSeconds { get; set; }
        public double GenerationEcPerSecond { get; set; }
        public double FullLoadHeatKw { get; set; }
        public double NominalCoolingKw { get; set; }
        public double ObservedRejectionKw { get; set; }
        public double MinimumShutdownMarginK { get; set; }
        public double FuelWasteEnduranceSeconds { get; set; }
        public double MaximumCoreRiseKelvinPerSecond { get; set; }
        public double MaximumLoopRiseKelvinPerSecond { get; set; }
        public double MinimumLoopOperatingMarginK { get; set; }
        public string Model { get; set; } = "LoadedStableFullPower30s.BoundedUnchangedThermalContinuation.v2";
    }
    public static class ColonyReactorContinuation
    {
        public static bool ShouldRevokeProof(string currentContext,string observedContext,bool observedLoaded,bool invalidated,double ut,double observedUt,bool latestKnownFailure=false)=>
            observedLoaded && invalidated && !string.IsNullOrEmpty(currentContext) && currentContext==observedContext && Finite(ut) && Finite(observedUt) && observedUt>=0 && ut>=observedUt && (latestKnownFailure || ut-observedUt<=10);
        public static bool CanContinue(ColonyReactorContinuationProof? proof,string worldId,string vesselId,string hardwareHash,string providerHash,string recipeHash,
            double ut,double actualFuelWasteEnduranceSeconds,double requiredSeconds,out string reason)
        {
            reason="No saved loaded full-power reactor proof; load and qualify the active native package.";
            if(proof==null)return false;
            try{ColonyStateCodec.ValidateReactorContinuation(proof);}
            catch(InvalidDataException ex){reason=ex.Message;return false;}
            if(proof.WorldId!=worldId||proof.VesselId!=vesselId||proof.HardwareHash!=hardwareHash||proof.ProviderHash!=providerHash||proof.RecipeHash!=recipeHash)
            {reason="Reactor continuation identity, real hardware/settings or installed provider/recipe changed; loaded requalification required.";return false;}
            if(!Finite(ut)||ut<proof.ObservedUt||ut>=proof.ExpiresUt)
            {reason="Saved reactor continuation is from a later save or has expired; loaded requalification required.";return false;}
            if(!Finite(actualFuelWasteEnduranceSeconds)||!Finite(requiredSeconds)||requiredSeconds<=0||actualFuelWasteEnduranceSeconds<requiredSeconds)
            {reason="Actual native fuel and waste room do not cover the current required reserve period.";return false;}
            // Subtraction avoids overflowing UT + reserve, and also rejects a
            // proof that expires a moment after the start of a long commitment.
            if(requiredSeconds>proof.ExpiresUt-ut)
            {reason="Remaining authorized reactor continuation horizon does not cover the current reserve period; loaded requalification required.";return false;}
            reason="Conditional thermal continuation from prior actual stable full-power observation of unchanged hardware, valid for another "+(proof.ExpiresUt-ut).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"s; BRP owns current fuel/waste/EC. Offline loop temperatures are not measured or simulated.";return true;
        }
        static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    }
    public static partial class ColonyStateCodec
    {
        public static void ValidateReactorContinuation(ColonyReactorContinuationProof p)
        {
            if(p==null)throw new InvalidDataException("Missing reactor continuation proof.");
            Id(p.WorldId);Id(p.VesselId);Text(p.HardwareHash,64,true);Text(p.ProviderHash,64,true);Text(p.RecipeHash,64,true);
            if(p.HardwareHash.Length!=64||p.ProviderHash.Length!=64||p.RecipeHash.Length!=64)throw new InvalidDataException("Reactor proof lacks exact configuration/recipe hashes.");
            Time(p.ObservedUt);Time(p.ExpiresUt);Range(p.FullThrottleSeconds,30,3600);Range(p.GenerationEcPerSecond,.000001,1e9);
            Range(p.FullLoadHeatKw,.000001,1e12);Range(p.NominalCoolingKw,.000001,1e12);Range(p.ObservedRejectionKw,.000001,1e12);
            Range(p.MinimumShutdownMarginK,100,1e9);Range(p.FuelWasteEnduranceSeconds,1,1e12);
            Range(p.MaximumCoreRiseKelvinPerSecond,0,.01);Range(p.MaximumLoopRiseKelvinPerSecond,0,.01);
            Range(p.MinimumLoopOperatingMarginK,10,1e9);
            double duration=p.ExpiresUt-p.ObservedUt;
            double coreTrendHorizon=p.MaximumCoreRiseKelvinPerSecond>0 ? (p.MinimumShutdownMarginK-100)/p.MaximumCoreRiseKelvinPerSecond : double.MaxValue;
            double loopTrendHorizon=p.MaximumLoopRiseKelvinPerSecond>0 ? p.MinimumLoopOperatingMarginK/p.MaximumLoopRiseKelvinPerSecond : double.MaxValue;
            if(p.Model!="LoadedStableFullPower30s.BoundedUnchangedThermalContinuation.v2"||p.ExpiresUt<=p.ObservedUt||p.ExpiresUt-p.ObservedUt>365*ColonyLimits.KerbinDay||
                duration>p.FuelWasteEnduranceSeconds||duration>coreTrendHorizon||duration>loopTrendHorizon||p.NominalCoolingKw<p.FullLoadHeatKw*1.1||p.ObservedRejectionKw<p.FullLoadHeatKw)
                throw new InvalidDataException("Reactor continuation lacks bounded full-load thermal/headroom/endurance evidence.");
        }
    }
}
