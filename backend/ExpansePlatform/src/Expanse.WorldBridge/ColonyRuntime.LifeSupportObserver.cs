using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static Harmony lifeSupportHarmony;static Type lifeSupportOwnerType;static string lifeSupportHookFailure;
        internal static Type LifeSupportOwnerType=>lifeSupportOwnerType;
        internal static string LifeSupportHookFailure=>lifeSupportHookFailure;
        internal static void EnsureLifeSupportObserver()
        {
            if(lifeSupportHarmony!=null||lifeSupportHookFailure!=null)return;
            var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="USILifeSupport");if(assembly==null)return;
            try
            {
                // The reviewed field/recipe semantics apply only to this installed DLL.
                using(var sha=SHA256.Create())using(var file=File.OpenRead(assembly.Location))
                    if(string.Concat(sha.ComputeHash(file).Select(b=>b.ToString("x2")))!="2aa072962e237d6a31f5f8defc93ad4ac5d03bfa51b0cadb7ddd00b84ffb427d")throw new InvalidOperationException("USI DLL differs from the reviewed consumption boundary.");
                lifeSupportOwnerType=assembly.GetType("LifeSupport.ModuleLifeSupportSystem",true);
                if(lifeSupportOwnerType.BaseType!=typeof(VesselModule))throw new InvalidOperationException("USI owner type changed.");
                var flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly;
                var update=lifeSupportOwnerType.GetMethod("FixedUpdate",flags,null,Type.EmptyTypes,null);
                var supply=lifeSupportOwnerType.GetProperty("SupplyRecipe",flags)?.GetGetMethod(true);var ec=lifeSupportOwnerType.GetProperty("ECRecipe",flags)?.GetGetMethod(true);
                if(update==null||update.ReturnType!=typeof(void)||supply?.ReturnType!=typeof(ConversionRecipe)||ec?.ReturnType!=typeof(ConversionRecipe))throw new MissingMethodException("Reviewed USI callback or recipe getters changed.");
                EnsureProductionBrokerHook();if(ProductionBrokerHookFailure.Length>0)throw new InvalidOperationException("Native broker observer is unavailable.");
                lifeSupportHarmony=new Harmony("Expanse.WorldBridge.PassiveUSILifeSupport");
                lifeSupportHarmony.Patch(update,prefix:LifeSupportPatch(nameof(LifeSupportPrefix)),finalizer:LifeSupportPatch(nameof(LifeSupportFinalizer)));
                lifeSupportHarmony.Patch(supply,postfix:LifeSupportPatch(nameof(LifeSupportSupplyPostfix)));
                lifeSupportHarmony.Patch(ec,postfix:LifeSupportPatch(nameof(LifeSupportEcPostfix)));
            }
            catch(Exception ex){lifeSupportHookFailure="USI consumption observer unavailable ("+ex.GetType().Name+").";StopLifeSupportObserver();}
        }
        static HarmonyMethod LifeSupportPatch(string name)=>new HarmonyMethod(typeof(ColonyRuntime).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        internal static void StopLifeSupportObserver(){try{lifeSupportHarmony?.UnpatchAll("Expanse.WorldBridge.PassiveUSILifeSupport");}catch{}lifeSupportHarmony=null;lifeSupportOwnerType=null;}
        static void LifeSupportPrefix(object __instance,out LifeSupportTelemetryScope.Frame __state){__state=WorldBridgeAddon.BeginLifeSupportScope(__instance);}
        static Exception LifeSupportFinalizer(Exception __exception,LifeSupportTelemetryScope.Frame __state){WorldBridgeAddon.EndLifeSupportScope(__state);return __exception;}
        static void LifeSupportSupplyPostfix(object __instance,ConversionRecipe __result){WorldBridgeAddon.BindLifeSupportRecipe(__instance,__result,false);}
        static void LifeSupportEcPostfix(object __instance,ConversionRecipe __result){WorldBridgeAddon.BindLifeSupportRecipe(__instance,__result,true);}
    }
}
