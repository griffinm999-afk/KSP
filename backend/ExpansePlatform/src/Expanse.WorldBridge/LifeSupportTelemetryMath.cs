using System;

namespace Expanse.WorldBridge
{
    // A recipe must have been returned by this exact owner's native getter in
    // this dynamic scope. Resource names alone never establish ownership.
    public sealed class LifeSupportTelemetryScope
    {
        public sealed class Frame
        {
            public object Owner; public string Context; public bool Eligible;
            internal object Supply, Electricity; internal Frame Previous;
        }
        Frame current;
        public Frame Enter(object owner,string context,bool eligible)
        {var f=new Frame{Owner=owner,Context=context,Eligible=eligible,Previous=current};current=f;return f;}
        public void Exit(Frame frame){if(ReferenceEquals(current,frame))current=frame.Previous;}
        public void Reset(){current=null;}
        public Frame Current=>current;
        public void Bind(object owner,object recipe,bool electricity)
        {if(current==null||!current.Eligible||!ReferenceEquals(current.Owner,owner))return;if(electricity)current.Electricity=recipe;else current.Supply=recipe;}
        public bool Consume(object owner,string context,object recipe,out bool electricity)
        {
            electricity=false;var f=current;
            if(f==null||!f.Eligible||f.Context!=context||!ReferenceEquals(f.Owner,owner)||recipe==null)return false;
            if(ReferenceEquals(f.Supply,recipe)){f.Supply=null;return true;}
            if(ReferenceEquals(f.Electricity,recipe)){f.Electricity=null;electricity=true;return true;}return false;
        }
        public static bool Interval(double observed,double end,double seconds)
            =>ProductionTelemetryLedger.Finite(observed)&&observed>=0&&ProductionTelemetryLedger.Finite(end)&&end>=seconds&&end<=observed+1e-6&&ProductionTelemetryLedger.Finite(seconds)&&seconds>=1e-8&&seconds<=21600;
    }
}
