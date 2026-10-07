using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed class PowerAverageTelemetry
    {
        public long WindowId; public double WindowStartUt,WindowEndUt,WindowGameSeconds,CoveredGameSeconds,ReportRealSeconds,AgeRealSeconds;
        public double GenerationEc,ConsumptionEc,GenerationEcPerSecond,ConsumptionEcPerSecond;
        public string Status,Basis,Reason;
    }
    // Real time determines reporting cadence; union of original game intervals
    // determines physical means. Parallel owners never multiply the denominator.
    public sealed class PowerAverageAccumulator
    {
        sealed class Totals
        {public double Generation,Consumption;public string Basis;public bool Partial;public long LastSequence;public readonly List<double[]> Intervals=new List<double[]>();}
        readonly Dictionary<string,Totals> totals=new Dictionary<string,Totals>();
        readonly Dictionary<string,PowerAverageTelemetry> completed=new Dictionary<string,PowerAverageTelemetry>();
        readonly HashSet<string> interrupted=new HashSet<string>();
        double started=double.NaN,finished=double.NaN,lastUt=double.NaN;long sequence;string context;bool windowPartial;
        public void Reset(string next){context=next;totals.Clear();completed.Clear();interrupted.Clear();started=finished=lastUt=double.NaN;windowPartial=false;}
        public void MarkPartial(){windowPartial=true;}
        public void Invalidate(string vessel){totals.Remove(vessel);completed.Remove(vessel);interrupted.Add(vessel);}
        public void Tick(string next,double real,double ut)
        {
            if(context!=next||!Finite(real)||!Finite(ut)||Finite(lastUt)&&ut<lastUt||Finite(started)&&real<started)Reset(next);
            if(!Finite(real)||!Finite(ut))return;lastUt=ut;
            if(!Finite(started)){started=real;return;}if(real-started<60)return;
            completed.Clear();sequence++;
            var all=totals.Values.SelectMany(t=>t.Intervals).ToArray();
            if(all.Length>0)
            {
                double begin=all.Min(x=>x[0]),end=all.Max(x=>x[1]),span=end-begin;
                foreach(var pair in totals)
                {
                    var t=pair.Value;double covered=t.Intervals.Sum(x=>x[1]-x[0]);if(covered<=0||t.Generation/covered>1e12||t.Consumption/covered>1e12)continue;
                    bool partial=windowPartial||t.Partial||covered<span-1e-6;
                    completed[pair.Key]=new PowerAverageTelemetry{WindowId=sequence,WindowStartUt=begin,WindowEndUt=end,WindowGameSeconds=span,CoveredGameSeconds=covered,
                        ReportRealSeconds=real-started,GenerationEc=t.Generation,ConsumptionEc=t.Consumption,GenerationEcPerSecond=t.Generation/covered,ConsumptionEcPerSecond=t.Consumption/covered,
                        Status=partial?"partial":"observed",Basis=t.Basis,Reason=partial?"Measured subtotal over covered intervals; missing intervals or resource owners remain unknown.":"Accepted fulfilled part requests over the covered reporting window; direct resource writes remain outside coverage."};
                }
            }
            finished=real;started=real;totals.Clear();interrupted.Clear();windowPartial=false;
        }
        public void Add(string vessel,double begin,double end,double generation,double consumption,string basis,bool partial,long captureSequence)
        {
            if(interrupted.Contains(vessel)||!Finite(begin)||begin<0||!Finite(end)||end<=begin||!Finite(generation)||generation<0||!Finite(consumption)||consumption<0||generation>1e18||consumption>1e18)return;
            if(!totals.TryGetValue(vessel,out var t)){if(totals.Count>=128)return;t=new Totals{Basis=basis};totals.Add(vessel,t);}
            if(captureSequence>0&&captureSequence<=t.LastSequence)return;
            if(captureSequence>0)t.LastSequence=captureSequence;
            if(t.Basis!=basis){Invalidate(vessel);return;}
            t.Generation+=generation;t.Consumption+=consumption;t.Partial|=partial;
            if(!Finite(t.Generation)||!Finite(t.Consumption)||t.Generation>1e18||t.Consumption>1e18){Invalidate(vessel);return;}
            t.Intervals.Add(new[]{begin,end});t.Intervals.Sort((a,b)=>a[0].CompareTo(b[0]));
            for(int i=1;i<t.Intervals.Count;){var a=t.Intervals[i-1];var b=t.Intervals[i];if(b[0]<=a[1]+1e-6){a[1]=Math.Max(a[1],b[1]);t.Intervals.RemoveAt(i);}else i++;}
            if(t.Intervals.Count>256)Invalidate(vessel);
        }
        public PowerAverageTelemetry Observe(string vessel,double real)
        {
            if(!completed.TryGetValue(vessel,out var t)||!Finite(real)||real<finished||real-finished>120)return null;
            return new PowerAverageTelemetry{WindowId=t.WindowId,WindowStartUt=t.WindowStartUt,WindowEndUt=t.WindowEndUt,WindowGameSeconds=t.WindowGameSeconds,CoveredGameSeconds=t.CoveredGameSeconds,
                ReportRealSeconds=t.ReportRealSeconds,AgeRealSeconds=real-finished,GenerationEc=t.GenerationEc,ConsumptionEc=t.ConsumptionEc,GenerationEcPerSecond=t.GenerationEcPerSecond,
                ConsumptionEcPerSecond=t.ConsumptionEcPerSecond,Status=t.Status,Basis=t.Basis,Reason=t.Reason};
        }
        static bool Finite(double n)=>ProductionTelemetryLedger.Finite(n);
    }
}
