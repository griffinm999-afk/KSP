using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Expanse.WorldBridge
{
    // No BRP types occur in this assembly's metadata. All quantities and real
    // activation flags survive. Only derived recipe/rate state is reset.
    internal static class ColonyBrpColdRecipeGuard
    {
        internal sealed class LoadState {internal bool Targeted;internal string Failure;}
        sealed class Fence {internal string Reason;}
        static readonly ConditionalWeakTable<object,Fence> Fences=new ConditionalWeakTable<object,Fence>();
        internal static string Failure(object engine)=>engine!=null&&Fences.TryGetValue(engine,out Fence row)?row.Reason:"";
        static MethodInfo Method(object engine,string name,int parameters)=>engine.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==parameters);
        static object Field(object owner,string name)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(owner);
        internal static ConfigNode PrepareCopy(ConfigNode original,LoadState state)
        {
            if(original==null)return original;
            var rows=original.GetNodes("CONVERTER");
            bool owned=rows.Any(c=>c.GetNode("BEHAVIOUR")?.GetValue("name")=="ExpanseColonyProportionalBehaviour");
            if(!owned)return original;
            state.Targeted=true;
            // A recognized but malformed contract is still held. Never route
            // its cached vector into the generic adapter.
            var copy=original.CreateCopy();
            if(rows.Length>512||copy.GetNodes("INVENTORY").Length>4096)state.Failure="Owned BRP cold-load bounds exceeded.";
            double ut;
            if(!double.TryParse(copy.GetValue("lastUpdate"),NumberStyles.Float,CultureInfo.InvariantCulture,out ut)||double.IsNaN(ut)||double.IsInfinity(ut)||ut<0)
            {state.Failure="Owned BRP cold-load UT is invalid.";ut=0;}
            string time=ut.ToString("R",CultureInfo.InvariantCulture);
            foreach(var converter in copy.GetNodes("CONVERTER").Where(c=>c.GetNode("BEHAVIOUR")?.GetValue("name")=="ExpanseColonyProportionalBehaviour"))
            {
                converter.RemoveNodes("INPUT_RESOURCE");converter.RemoveNodes("OUTPUT_RESOURCE");converter.RemoveNodes("REQUIRED_RESOURCE");
                converter.SetValue("rate","0",true);converter.SetValue("nextChangepoint",time,true);
            }
            copy.SetValue("nextChangepoint",time,true);return copy;
        }
        internal static void Recompute(object engine,LoadState state,Action<object> recompute=null)
        {
            if(state==null||!state.Targeted)return;
            if(state.Failure!=null)throw new InvalidOperationException(state.Failure);
            if(recompute!=null){recompute(engine);return;}
            // ComputeRates catches solver exceptions in the installed build.
            // Surface the real solver result first, then verify application,
            // so a swallowed solver failure cannot revive serialized rates.
            object solution=Method(engine,"ComputeRateSolution",0).Invoke(engine,null);
            Method(engine,"ComputeRates",0).Invoke(engine,null);
            var expectedInventories=(double[])Field(solution,"inventoryRates");var expectedConverters=(double[])Field(solution,"converterRates");
            // StableList enumeration skips empty slots. Compare by its actual
            // index instead of treating its compact enumeration as an index.
            VerifyRates(Field(engine,"inventories"),expectedInventories);VerifyRates(Field(engine,"converters"),expectedConverters);
            Fences.Remove(engine);
        }
        static void VerifyRates(object list,double[] expected)
        {
            int count=(int)list.GetType().GetProperty("Count").GetValue(list,null);var item=list.GetType().GetProperty("Item");
            if(count!=expected.Length)throw new InvalidOperationException("Owned BRP cold recomputation shape differs.");
            for(int i=0;i<count;i++)
            {
                object row=item.GetValue(list,new object[]{i});if(row==null)continue;
                double rate=Convert.ToDouble(Field(row,"Rate"),CultureInfo.InvariantCulture);
                if(double.IsNaN(rate)||double.IsInfinity(rate)||rate!=expected[i])throw new InvalidOperationException("Owned BRP cold recomputation failed to install exact native rates.");
            }
        }
        internal static string FenceFailure(object engine,LoadState state,Exception error)
        {
            if(state==null||!state.Targeted)return "";
            string reason="Owned BRP cold-load processor held: "+(error?.InnerException??error)?.Message;
            // Native ClearRates preserves inventories; infinity prevents every
            // stale cached rate on this failed processor from advancing time.
            Method(engine,"ClearRates",0).Invoke(engine,null);
            engine.GetType().GetField("nextChangepoint").SetValue(engine,double.PositiveInfinity);
            foreach(object converter in (IEnumerable)Field(engine,"converters"))
                converter.GetType().GetField("NextChangepoint").SetValue(converter,double.PositiveInfinity);
            Fences.Remove(engine);Fences.Add(engine,new Fence{Reason=reason});return reason;
        }
    }
}
