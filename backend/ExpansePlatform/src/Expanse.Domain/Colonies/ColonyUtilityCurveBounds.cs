using System;
using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyUtilityCurvePoint
    {
        public double Time { get; set; }
        public double Value { get; set; }
        public double InTangent { get; set; }
        public double OutTangent { get; set; }
    }
    public static class ColonyUtilityCurveBounds
    {
        // Exact extrema of unweighted cubic Hermite segments, including
        // overshoot between keys. Endpoints bound the clamped extrapolation.
        public static bool TryMaximum(IReadOnlyList<ColonyUtilityCurvePoint> points,out double maximum)
        {
            maximum=double.NaN;if(points==null || points.Count==0 || points.Count>64)return false;
            double result=double.MinValue;
            for(int i=0;i<points.Count;i++)
            {
                var p=points[i];if(p==null || !Finite(p.Time) || !Finite(p.Value) || !Finite(p.InTangent) || !Finite(p.OutTangent))return false;
                result=Math.Max(result,p.Value);if(i==0)continue;var left=points[i-1];double span=p.Time-left.Time;if(span<=0)return false;
                double a=2*left.Value-2*p.Value+span*(left.OutTangent+p.InTangent),b=-3*left.Value+3*p.Value-span*(2*left.OutTangent+p.InTangent),c=span*left.OutTangent,d=left.Value;
                if(!Finite(a) || !Finite(b) || !Finite(c))return false;
                Action<double> consider=t=>{if(t>0 && t<1)result=Math.Max(result,((a*t+b)*t+c)*t+d);};
                if(Math.Abs(a)<1e-15){if(Math.Abs(b)>1e-15)consider(-c/(2*b));}
                else
                {
                    double discriminant=4*b*b-12*a*c;if(!Finite(discriminant))return false;
                    if(discriminant>=0){double root=Math.Sqrt(discriminant);consider((-2*b+root)/(6*a));consider((-2*b-root)/(6*a));}
                }
            }
            if(!Finite(result))return false;maximum=result;return true;
        }
        static bool Finite(double value)=>!double.IsNaN(value) && !double.IsInfinity(value);
    }
}
