using System;
using System.Globalization;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static int[] UtilityNativeConfigurationMap(AvailablePart info)
        {
            if (info?.partPrefab == null || info.partConfig == null) throw new InvalidOperationException("Native utility part configuration is absent.");
            var native = info.partPrefab.Modules.Cast<PartModule>().ToArray(); var configured = info.partConfig.GetNodes("MODULE");
            if (native.Length > 128 || configured.Length > 128 || native.Any(m => m == null)) throw new InvalidOperationException("Native utility module inventory exceeds its bound.");
            var trips = native.Where(m => m.moduleName == "ModuleTripLogger").ToArray();
            bool stock = trips.Length == 1 && trips[0].GetType() == typeof(ModuleTripLogger) && info.partPrefab.vesselType > VesselType.Unknown;
            if (stock)
            {
                // Installed KISPodInventoryLoader follows PartLoader. Only
                // its exact per-seat pod modules may follow the implicit row.
                var pods = native.Skip(Array.IndexOf(native, trips[0]) + 1).ToArray();
                stock = pods.Length <= info.partPrefab.CrewCapacity;
                for (int i = 0; stock && i < pods.Length; i++)
                    stock = UtilityModuleIdentity(pods[i]) == "KIS.ModuleKISInventory|KIS|1.29.8039.40483" &&
                        Convert.ToString(UtilityRead(pods[i], "invType"), CultureInfo.InvariantCulture) == "Pod" &&
                        UtilityNumber(pods[i], "podSeat", double.NaN) == info.partPrefab.CrewCapacity - pods.Length + i;
            }
            // Part.AddModule(string) refuses exact stock ModuleCargoPart when
            // the earlier configured ModuleInventoryPart is already present.
            var inventories = native.Where(m => m.moduleName == "ModuleInventoryPart").ToArray();
            bool suppressedCargo = inventories.Length == 1 && inventories[0].GetType() == typeof(ModuleInventoryPart) &&
                !native.Any(m => m.moduleName == "ModuleCargoPart");
            int[] map;
            if (!ColonyUtilityNativeModuleConfiguration.TryMap(native.Select(m => m.moduleName).ToArray(), configured.Select(n => n.GetValue("name")).ToArray(), stock, suppressedCargo, out map))
                throw new InvalidOperationException("Native/configured module order changed for " + info.name + " (native=" + native.Length + ", configured=" + configured.Length + "); only exact stock TripLogger, Inventory/Cargo exclusion and native KIS pod suffix are supported.");
            for (int i = 0; i < map.Length; i++)
                if (map[i] < 0)
                    for (int p = i + 1; p < native.Length; p++)
                    {
                        var node = configured[map[p]]; int seat;
                        if (node.GetValues("invType").Length != 1 || node.GetValue("invType") != "Pod" || node.GetValues("podSeat").Length != 1 ||
                            !int.TryParse(node.GetValue("podSeat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out seat) ||
                            UtilityNumber(native[p], "podSeat", double.NaN) != seat)
                            throw new InvalidOperationException("Native KIS pod seat/configuration order changed after stock TripLogger.");
                    }
            return map;
        }

        static ConfigNode UtilityNativeConfiguration(AvailablePart info, int nativeIndex)
        {
            var map = UtilityNativeConfigurationMap(info);
            if (nativeIndex < 0 || nativeIndex >= map.Length || map[nativeIndex] < 0) throw new InvalidOperationException("Stock-added utility module has no configured resource terms.");
            return info.partConfig.GetNodes("MODULE")[map[nativeIndex]];
        }
    }
}
