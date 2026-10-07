using System;

namespace Expanse.WorldBridge
{
    // Detached rule; runtime supplies current physical observations, never catalog defaults.
    internal static class ColonyUtilityModuleBounds
    {
        internal const string AntennaIdentity="CommNetAntennasConsumptor.ModuleGeneratorAntenna|CommNetAntennasConsumptor|3.5.8.0";
        internal static bool TryAntennaDemand(string identity,bool loaded,bool stockTick,bool stockHandler,bool throttleControlled,string[] names,double[] rates,int outputs,out double demand)
        {
            demand=0;
            if(identity!=AntennaIdentity||!loaded||!stockTick||!stockHandler||throttleControlled||outputs!=0||names==null||rates==null||names.Length==0||names.Length>64||names.Length!=rates.Length)return false;
            for(int i=0;i<rates.Length;i++)
            {
                if(names[i]!="ElectricCharge"||double.IsNaN(rates[i])||double.IsInfinity(rates[i])||rates[i]<0||rates[i]>1e6){demand=0;return false;}
                demand+=rates[i];
            }
            if(demand>1e6){demand=0;return false;}return true;
        }
        internal static bool OwnerlessAnimationTerminal(bool deployed, bool playing, double normalizedTime)
            => !playing && !double.IsNaN(normalizedTime) && !double.IsInfinity(normalizedTime) && normalizedTime >= 0 && normalizedTime <= 1 &&
                (deployed ? normalizedTime >= .9999 : normalizedTime <= .0001);
        internal static bool DependentProof(bool loaded,bool identity,bool independentResources,bool samePart,bool relation,bool settled,bool ownersAccounted)
            =>loaded&&identity&&!independentResources&&samePart&&relation&&settled&&ownersAccounted;
    }
}
