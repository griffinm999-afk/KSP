using System;
using System.Globalization;

namespace Expanse.WorldBridge
{
    internal static class ColonySeatCapacity
    {
        internal static bool TryProto(string partName, int prefabCapacity, string animationTime,
            string initializedText, out int capacity)
        {
            capacity = prefabCapacity;
            if (prefabCapacity < 0 || prefabCapacity > 256) return false;
            if (partName != "KKAOSS.Habitat.MK2.g") return true;
            double animation; bool initialized;
            if (!double.TryParse(animationTime, NumberStyles.Float, CultureInfo.InvariantCulture, out animation) ||
                double.IsNaN(animation) || double.IsInfinity(animation) || animation < 0 || animation > 1 ||
                !bool.TryParse(initializedText, out initialized)) return false;
            capacity = initialized && animation >= .999 ? 4 : 0;
            return true;
        }
    }
}
