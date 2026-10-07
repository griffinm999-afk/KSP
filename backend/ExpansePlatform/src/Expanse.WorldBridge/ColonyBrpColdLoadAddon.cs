using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace Expanse.WorldBridge
{
    [KSPAddon(KSPAddon.Startup.Instantly,true)]
    public sealed class ColonyBrpColdLoadAddon:MonoBehaviour
    {
        static Harmony harmony;
        static void Report(string reason){try{Debug.LogError("[ExpanseColony] "+reason);}catch{ /* The stored fence remains authoritative if logging is unavailable. */ }}
        internal static bool Installed {get;private set;}
        internal static string Failure {get;private set;}="Owned BRP cold-load guard has not initialized.";
        void Awake()
        {
            try
            {
                var rows=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="BackgroundResourceProcessing").Take(2).ToArray();
                if(rows.Length==0){Failure="Optional BRP is absent.";return;}
                if(rows.Length!=1||rows[0].GetName().Version!=new Version(0,2,7,0))throw new InvalidOperationException("BRP cold-load assembly identity differs.");
                using(var stream=File.OpenRead(rows[0].Location))using(var sha=SHA256.Create())
                    if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!="D514CFEF35388FECEAAFC79D235D8F800D7748FF273814F1E511B9E30ECC8E55")throw new InvalidOperationException("BRP cold-load assembly content differs.");
                var type=rows[0].GetType("BackgroundResourceProcessing.Core.ResourceProcessor",true);
                var target=type.GetMethod("Load",BindingFlags.Instance|BindingFlags.Public,null,new[]{typeof(ConfigNode),typeof(Vessel)},null);
                if(target==null||target.ReturnType!=typeof(void)||target.DeclaringType!=type||target.IsStatic||
                    type.GetMethod("ComputeRates",Type.EmptyTypes)?.ReturnType!=typeof(void)||type.GetMethod("ClearRates",Type.EmptyTypes)?.ReturnType!=typeof(void)||
                    type.GetField("nextChangepoint")?.FieldType!=typeof(double))throw new InvalidOperationException("BRP cold-load method/field shape differs.");
                harmony=new Harmony("Expanse.WorldBridge.ColonyProportionalColdLoad");
                harmony.Patch(target,new HarmonyMethod(typeof(ColonyBrpColdLoadAddon),nameof(Prefix)),new HarmonyMethod(typeof(ColonyBrpColdLoadAddon),nameof(Postfix)),null,new HarmonyMethod(typeof(ColonyBrpColdLoadAddon),nameof(Finalizer)));
                ColonyRuntime.RegisterReactorRestore(harmony,rows[0]);
                Installed=true;Failure="";
            }
            catch(Exception ex){Failure="Owned BRP cold-load guard unavailable: "+ex.Message;Report(Failure);}
        }
        static void Prefix(ref ConfigNode node,out ColonyBrpColdRecipeGuard.LoadState __state)
        {__state=new ColonyBrpColdRecipeGuard.LoadState();node=ColonyBrpColdRecipeGuard.PrepareCopy(node,__state);}
        static void Postfix(object __instance,ColonyBrpColdRecipeGuard.LoadState __state)
        {ColonyBrpColdRecipeGuard.Recompute(__instance,__state);}
        static Exception Finalizer(object __instance,ColonyBrpColdRecipeGuard.LoadState __state,Exception __exception)
        {
            if(__exception!=null&&__state!=null&&__state.Targeted)
            {
                try{Report(ColonyBrpColdRecipeGuard.FenceFailure(__instance,__state,__exception));return null;}
                catch(Exception fence){Report("BRP cold-load failure fence failed: "+fence);return __exception;}
            }
            return __exception;
        }
        internal static string ProcessorFailure(object processor)
        {
            if(!Installed)return Failure;
            var field=processor?.GetType().GetField("processor",BindingFlags.Instance|BindingFlags.NonPublic);
            return field==null?"Current BRP cold-load processor is absent.":ColonyBrpColdRecipeGuard.Failure(field.GetValue(processor));
        }
    }
}
