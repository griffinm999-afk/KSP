using System;
using System.Collections.Generic;

namespace Expanse.BrpColony
{
    public static class ProportionalMath
    {
        public const double RelativeDrift = 1e-4;
        public const double StopFactor = 1e-9;
        public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        public static double Factor(IReadOnlyList<double> amounts, IReadOnlyList<double> requirements)
        {
            if(amounts==null||requirements==null||amounts.Count!=requirements.Count||amounts.Count==0||amounts.Count>16)throw new ArgumentException("Bounded local requirements unavailable.");
            double f=1;
            for(int i=0;i<amounts.Count;i++)
            {
                double a=amounts[i],r=requirements[i];
                if(!Finite(a)||a<0||!Finite(r)||r==0)throw new ArgumentException("Invalid local requirement.");
                f=Math.Min(f,r>0?a/r:1-a/Math.Abs(r));
            }
            return f<=StopFactor?0:Math.Max(0,Math.Min(1,f));
        }
        public static double Next(double now,IReadOnlyList<double> amounts,IReadOnlyList<double> requirements,IReadOnlyList<double> rates,double stopThreshold=StopFactor)
        {
            double f=Factor(amounts,requirements);
            if(!Finite(now)||now<0||rates==null||rates.Count!=amounts.Count||!Finite(stopThreshold)||stopThreshold<StopFactor)throw new ArgumentException("Invalid current solver time/rates.");
            double dt=double.PositiveInfinity;
            var values=new double[amounts.Count];var slopes=new double[amounts.Count];
            for(int i=0;i<amounts.Count;i++)
            {
                if(!Finite(rates[i]))throw new ArgumentException("Invalid requirement inventory rate.");
                double r=requirements[i];values[i]=r>0?amounts[i]/r:1-amounts[i]/Math.Abs(r);slopes[i]=rates[i]/r;
                if(slopes[i]==0)continue;
                // Crossing clamp/stop/minimum boundaries uses strictly positive
                // times; at a boundary the drift limit gives the first interval.
                foreach(double boundary in new[]{0d,stopThreshold,1d})
                {double t=(boundary-values[i])/slopes[i];if(t>0)dt=Math.Min(dt,t);}
                dt=Math.Min(dt,RelativeDrift*Math.Max(f,StopFactor)/Math.Abs(slopes[i]));
                if(rates[i]<0)dt=Math.Min(dt,amounts[i]/-rates[i]);
            }
            for(int i=0;i<values.Length;i++)for(int j=i+1;j<values.Length;j++)
            {double slope=slopes[i]-slopes[j];if(slope==0)continue;double t=(values[j]-values[i])/slope;if(t>0)dt=Math.Min(dt,t);}
            if(double.IsPositiveInfinity(dt))return dt;
            if(!Finite(dt)||dt<=0)throw new InvalidOperationException("Requirement interval cannot progress.");
            return Future(now,dt);
        }
        // The vectors remain scaled to the amounts observed by GetResources,
        // even when an unrelated changepoint recomputes rates. Account for
        // movement already spent in that vector epoch before scheduling more.
        public static double NextInEpoch(double now,IReadOnlyList<double> amounts,IReadOnlyList<double> requirements,IReadOnlyList<double> rates,IReadOnlyList<double> epochAmounts,double stopThreshold=StopFactor)
        {
            if(epochAmounts==null||epochAmounts.Count!=amounts.Count)throw new ArgumentException("Missing proportional vector epoch.");
            double next=Next(now,amounts,requirements,rates,stopThreshold);
            double budget=RelativeDrift*Math.Max(Factor(epochAmounts,requirements),StopFactor);
            for(int i=0;i<amounts.Count;i++)
            {
                double remaining=budget-Math.Abs(amounts[i]-epochAmounts[i])/Math.Abs(requirements[i]);
                if(remaining<=0)return now; // one native refresh installs a new epoch
                double speed=Math.Abs(rates[i]/requirements[i]);
                if(speed>0)next=Math.Min(next,Future(now,remaining/speed));
            }
            return next;
        }
        static double Future(double now,double dt)
        {
            if(double.IsPositiveInfinity(dt))return dt;
            if(!Finite(dt)||dt<=0)throw new InvalidOperationException("Requirement interval cannot progress.");
            double next=now+dt;
            // Addition may round upward. Round back down so the represented
            // interval never exceeds the proved rate/factor budget.
            if(next-now>dt)next=BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(next)-1);
            if(next<=now)throw new InvalidOperationException("UT precision cannot represent the bounded requirement interval.");
            if(!Finite(next))throw new InvalidOperationException("Requirement interval overflows UT.");
            return next;
        }
        // For an isolated consumed positive requirement A'=-k*A, holding each
        // interval's start factor is explicit Euler. x=k*h<=RelativeDrift.
        // 0 <= -log(1-x)-x <= x*x/(2*(1-x)); sum x=k*T.
        // Thus (exact-stock - Euler-stock)/initial-stock is bounded by this
        // expression, independent of interval count and save/reload cuts.
        public static double CumulativeStockError(double k,double time)
        {
            if(!Finite(k)||k<0||!Finite(time)||time<0)throw new ArgumentException("Invalid decay horizon.");
            return 1-Math.Exp(-k*time*RelativeDrift/(2*(1-RelativeDrift)));
        }
    }
}
