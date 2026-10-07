using System;
using System.Collections.Generic;

namespace Expanse.WorldBridge
{
    // Passive observer state only. On-rails is announced before packed changes;
    // reject captures until the announced mode is visible. Events invalidate even
    // same-UT roundtrips; polling additionally catches unannounced mode changes.
    public sealed class ProductionTelemetryMode
    {
        long epoch;
        bool initialized, packed, pending, targetPacked;
        public void Transition(bool target)
        { epoch++; pending=true; targetPacked=target; }
        public bool TryCapture(bool currentPacked,out long capturedEpoch)
        {
            capturedEpoch=epoch;
            if(pending&&currentPacked!=targetPacked)return false;
            pending=false;
            if(initialized&&packed!=currentPacked)epoch++;
            initialized=true; packed=currentPacked; capturedEpoch=epoch; return true;
        }
        public bool Matches(long capturedEpoch,bool capturedPacked,bool currentPacked)
        { return TryCapture(currentPacked,out long currentEpoch)&&capturedEpoch==currentEpoch&&capturedPacked==currentPacked; }
    }

    // Pure accounting for passive native broker observations. Nested calls always
    // get a frame, including unobserved calls, so they cannot credit their parent.
    public sealed class ProductionTelemetryLedger
    {
        public sealed class Frame
        {
            public object Broker, Part; public string Context, RecipeKey;
            public double Ut, Seconds; public bool Valid, Finished;public long CaptureSequence;
            internal Frame Previous;
            public readonly Dictionary<string,double> Inputs=new Dictionary<string,double>(StringComparer.Ordinal);
            public readonly Dictionary<string,double> Outputs=new Dictionary<string,double>(StringComparer.Ordinal);
        }
        Frame current; string context; double lastUt=double.NaN;long captures;
        public void Reset(string nextContext) { context=nextContext;current=null;lastUt=double.NaN; }
        public Frame Enter(string sampleContext,object broker,object part,string recipeKey,double ut,double seconds,bool eligible)
        {
            if(context!=sampleContext || Finite(lastUt)&&ut<lastUt) Reset(sampleContext);
            lastUt=ut;
            var frame=new Frame {Broker=broker,Part=part,Context=sampleContext,RecipeKey=recipeKey,Ut=ut,Seconds=seconds,
                Valid=eligible&&broker!=null&&part!=null&&Finite(ut)&&ut>=0&&Finite(seconds)&&seconds>0&&seconds<=21600,Previous=current};
            current=frame;return frame;
        }
        public void Record(object broker,object part,string resource,double requested,double returned,bool output)
        {
            var frame=current;if(frame==null||!frame.Valid)return;
            if(!ReferenceEquals(frame.Broker,broker)||!ReferenceEquals(frame.Part,part)) {frame.Valid=false;return;}
            double actual=output?-returned:returned;
            if(string.IsNullOrWhiteSpace(resource)||resource.Length>80||!Finite(requested)||requested<0||!Finite(actual)||actual<0||actual>requested+Math.Max(1e-9,requested*1e-6)) {frame.Valid=false;return;}
            var rows=output?frame.Outputs:frame.Inputs;
            if(rows.Count>=16&&!rows.ContainsKey(resource)){frame.Valid=false;return;}
            double sum=(rows.TryGetValue(resource,out double before)?before:0)+actual;
            if(!Finite(sum)||sum/frame.Seconds>1e12){frame.Valid=false;return;}rows[resource]=sum;
        }
        public bool Complete(Frame frame,bool returned,double timeFactor)
        {
            if(frame==null||frame.Finished)return false;
            frame.Finished=true;
            bool valid=ReferenceEquals(frame,current)&&frame.Context==context&&frame.Valid&&returned&&Finite(timeFactor)&&timeFactor>=0;
            frame.Valid=valid;
            if(valid)frame.CaptureSequence=++captures;
            if(ReferenceEquals(frame,current))current=frame.Previous!=null&&frame.Previous.Context==context?frame.Previous:null;
            return valid;
        }
        public static bool Fresh(Frame frame,string sampleContext,string recipeKey,double now,bool active)
            =>frame!=null&&frame.Valid&&frame.Finished&&active&&frame.Context==sampleContext&&frame.RecipeKey==recipeKey&&Finite(now)&&now>=frame.Ut&&now-frame.Ut<=10;
        public static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
