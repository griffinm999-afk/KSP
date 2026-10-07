using System;
namespace Expanse.Domain.Colonies
{
    // Ratings are conservative conditional native-output bounds, never fuel
    // consumption, produced resources, or a claim of measured throughput.
    public static class ColonyFixedGeneratorBounds
    {
        public static bool InitialFuelWitness(double reviewedAmount,double actualFuel,double actualCapacity)
        {
            // A partially fueled paid package is legitimate. This check never
            // fills the tank or changes the reviewed startup allocation.
            return Positive(reviewedAmount)&&reviewedAmount<=20&&actualCapacity==20&&Positive(actualFuel)&&actualFuel<=actualCapacity&&
                actualFuel<=reviewedAmount+Math.Max(1e-6,reviewedAmount*1e-8);
        }
        public static bool TryFuelBound(double fuel,double capacity,double requiredFuel,double fuelPerSecond,double outputPerSecond,
            double minimumNativeMultiplier,double maximumNativeMultiplier,double horizonSeconds,
            out double minimumOutput,out double enduranceSeconds,out string reason)
        {
            minimumOutput=0;enduranceSeconds=0;reason="Fixed native generator terms are invalid or unbounded.";
            if(!Positive(capacity)||capacity>1e9||!Finite(fuel)||fuel<=0||fuel>capacity||!Positive(requiredFuel)||requiredFuel>capacity||
                !Positive(fuelPerSecond)||fuelPerSecond>1e9||!Positive(outputPerSecond)||outputPerSecond>1e9||
                !Positive(minimumNativeMultiplier)||minimumNativeMultiplier>10000||!Positive(maximumNativeMultiplier)||maximumNativeMultiplier>10000||
                maximumNativeMultiplier<minimumNativeMultiplier||!Finite(horizonSeconds)||horizonSeconds<0||horizonSeconds>365*21600)return false;
            double fullBurn=fuelPerSecond*maximumNativeMultiplier;
            double burn=fullBurn*horizonSeconds;
            if(!Positive(fullBurn)||!Finite(burn))return false;
            enduranceSeconds=Math.Floor(fuel/fullBurn);
            // Subtract a small conservative floating-point guard. A horizon
            // reaching zero fuel must not qualify due to roundoff.
            double after=fuel-burn-Math.Max(1e-12,Math.Abs(fuel)*1e-12);
            if(!Positive(after)||enduranceSeconds<horizonSeconds)
            {reason="Local fuel does not cover the complete native reserve horizon.";return false;}
            minimumOutput=outputPerSecond*minimumNativeMultiplier*Math.Min(1,after/requiredFuel);
            if(!Positive(minimumOutput)||minimumOutput>1e9){minimumOutput=0;return false;}
            reason="Conservative end-of-horizon output includes actual local required-fuel throttling; native module retains resource ownership.";return true;
        }
        public static double RecipeMultiplier(bool nativePrecalculated,double nativePassedMultiplier)
        {
            if(!Finite(nativePassedMultiplier)||nativePassedMultiplier<0||nativePassedMultiplier>10000)throw new ArgumentOutOfRangeException(nameof(nativePassedMultiplier));
            return nativePrecalculated?1:nativePassedMultiplier;
        }
        static bool Positive(double value)=>Finite(value)&&value>0;
        static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
