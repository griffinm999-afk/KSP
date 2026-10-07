using System;
using System.Linq;

namespace Expanse.WorldBridge
{
    // Native/proto indices never change. Stock adds TripLogger without a
    // config node, and refuses Cargo when Inventory is already present.
    internal static class ColonyUtilityNativeModuleConfiguration
    {
        internal static bool TryMap(string[] native, string[] configured, bool exactStockTripLogger, out int[] map)
            => TryMap(native, configured, exactStockTripLogger, false, out map);

        internal static bool TryMap(string[] native, string[] configured, bool exactStockTripLogger, bool exactStockInventoryCargo, out int[] map)
        {
            map = null;
            if (native == null || configured == null || native.Length > 128 || configured.Length > 128 ||
                native.Any(string.IsNullOrEmpty) || configured.Any(string.IsNullOrEmpty)) return false;
            if (native.SequenceEqual(configured)) { map = Enumerable.Range(0, native.Length).ToArray(); return true; }
            int cargo = -1;
            if (exactStockInventoryCargo && native.Count(n => n == "ModuleInventoryPart") == 1 && !native.Contains("ModuleCargoPart") &&
                configured.Count(n => n == "ModuleInventoryPart") == 1 && configured.Count(n => n == "ModuleCargoPart") == 1 &&
                Array.IndexOf(configured, "ModuleInventoryPart") < Array.IndexOf(configured, "ModuleCargoPart"))
                cargo = Array.IndexOf(configured, "ModuleCargoPart");
            int trip = -1;
            if (exactStockTripLogger && !configured.Contains("ModuleTripLogger") && native.Count(n => n == "ModuleTripLogger") == 1)
            {
                trip = Array.IndexOf(native, "ModuleTripLogger");
                if (native.Skip(trip + 1).Any(n => n != "ModuleKISInventory")) return false;
            }
            if (native.Length != configured.Length + (trip >= 0 ? 1 : 0) - (cargo >= 0 ? 1 : 0)) return false;
            var mapped = new int[native.Length]; int next = 0;
            for (int i = 0; i < native.Length; i++)
            {
                if (i == trip) { mapped[i] = -1; continue; }
                if (next == cargo) next++;
                if (next >= configured.Length || native[i] != configured[next]) return false;
                mapped[i] = next++;
            }
            if (next == cargo) next++;
            if (next != configured.Length) return false;
            map = mapped; return true;
        }
    }
}
