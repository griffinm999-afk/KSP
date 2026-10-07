using System;
using System.Linq;
using System.Reflection;
using Expanse.WorldBridge;
using HarmonyLib;
public static class Program
{
    public static int Main()
    {
        var owner=typeof(LifeSupport.ModuleLifeSupportSystem);var runtime=typeof(ColonyRuntime);int count=0;
        runtime.GetMethod("EnsureLifeSupportObserver",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        var error=runtime.GetProperty("LifeSupportHookFailure",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
        if(error!=null){Console.WriteLine(error);return 1;}
        foreach(var target in new[]{owner.GetMethod("FixedUpdate",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic),owner.GetProperty("SupplyRecipe",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetGetMethod(true),owner.GetProperty("ECRecipe",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetGetMethod(true)})
        {var patches=Harmony.GetPatchInfo(target);if(patches==null||!patches.Owners.Contains("Expanse.WorldBridge.PassiveUSILifeSupport"))throw new Exception("Passive USI hook missing");count++;}
        var process=typeof(ResourceConverter).GetMethod("ProcessRecipe");if(!Harmony.GetPatchInfo(process).Owners.Contains("Expanse.WorldBridge.ColonyPhysicalProduction"))throw new Exception("Shared broker boundary missing");count++;
        runtime.GetMethod("StopLifeSupportObserver",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);runtime.GetMethod("StopProductionObservers",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        if(Harmony.GetPatchInfo(process)!=null&&Harmony.GetPatchInfo(process).Owners.Contains("Expanse.WorldBridge.ColonyPhysicalProduction"))throw new Exception("Hook cleanup failed");count++;
        Console.WriteLine("Detached .NET Framework passive hook registration checks passed: "+count+". No original flight or resource method invoked.");return 0;
    }
}
