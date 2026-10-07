using System;

namespace Expanse.WorldBridge
{
    // Created for one synchronous utility observation/preflight, including its
    // distribution peers. No setting or provider authority survives the call.
    internal sealed class ColonyUtilityInstructionObservation
    {
        readonly Func<double?> readBound;
        bool attempted;
        double? instructions;
        internal ColonyUtilityInstructionObservation(Func<double?> readBound){this.readBound=readBound;}
        internal bool TryDemand(double perInstruction,double perByte,double disk,double fixedDeltaTime,out double demand)
        {
            demand=0;
            if(!attempted)
            {
                attempted=true;
                try{instructions=readBound();}catch{instructions=null;}
            }
            if(!instructions.HasValue||!Finite(instructions.Value)||instructions.Value<=0||instructions.Value>100000||
                !Finite(perInstruction)||perInstruction<0||!Finite(perByte)||perByte<0||!Finite(disk)||disk<0||!Finite(fixedDeltaTime)||fixedDeltaTime<=0)return false;
            demand=Math.Max(disk,50000)*perByte+Math.Max(instructions.Value,1)*perInstruction/fixedDeltaTime;
            return Finite(demand)&&demand>=0;
        }
        static bool Finite(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n);
    }
}
