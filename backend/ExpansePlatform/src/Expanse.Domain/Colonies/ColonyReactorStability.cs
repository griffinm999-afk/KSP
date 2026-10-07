using System;

namespace Expanse.Domain.Colonies
{
    // Detached observation accumulator, never saved as a substitute for native
    // simulation. Every rising interval is bounded, so an average that conceals
    // a recent heat-up cannot qualify a package.
    public sealed class ColonyReactorStabilityWindow
    {
        string seal="";
        double start=-1,last=-1,core,loop;
        public double Seconds { get; private set; }
        public double MaximumCoreRise { get; private set; }
        public double MaximumLoopRise { get; private set; }
        public bool Observe(string identity,double ut,double actualCore,double actualLoop,bool fullPowerAtOneX)
        {
            if(!fullPowerAtOneX || !Finite(ut) || !Finite(actualCore) || !Finite(actualLoop) || ut<0 || actualCore<=0 || actualLoop<=0)
            {Reset();return false;}
            if(identity!=seal || last<0 || ut<last || ut-last>10 || ut-start>3600)
            {Begin(identity,ut,actualCore,actualLoop);return false;}
            if(ut==last)return Seconds>=30;
            double dt=ut-last,coreRise=Math.Max(0,(actualCore-core)/dt),loopRise=Math.Max(0,(actualLoop-loop)/dt);
            if(coreRise>.01 || loopRise>.01){Begin(identity,ut,actualCore,actualLoop);return false;}
            MaximumCoreRise=Math.Max(MaximumCoreRise,coreRise);MaximumLoopRise=Math.Max(MaximumLoopRise,loopRise);
            last=ut;core=actualCore;loop=actualLoop;Seconds=ut-start;return Seconds>=30;
        }
        public void Reset(){seal="";start=last=-1;Seconds=MaximumCoreRise=MaximumLoopRise=0;}
        void Begin(string identity,double ut,double actualCore,double actualLoop){Reset();seal=identity;start=last=ut;core=actualCore;loop=actualLoop;}
        static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    }
}
