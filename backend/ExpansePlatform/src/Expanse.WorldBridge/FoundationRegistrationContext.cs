using System;
using System.Reflection;

namespace Expanse.WorldBridge
{
    // Registration records part identities only. An Expanse Foundations anchor
    // intentionally keeps a landed vessel packed, so it is safe to enumerate
    // its loaded parts for registration even though inventory writes stay held.
    // Resolve the optional plugin at runtime to keep the bridge usable without it.
    internal static class FoundationRegistrationContext
    {
        private static MethodInfo isHeld;

        public static bool CanEnumerate(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded || vessel.parts == null || !HighLogic.LoadedSceneIsFlight)
                return false;
            return !vessel.packed || IsAnchored(vessel);
        }

        public static bool IsAnchored(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded || !vessel.packed ||
                (vessel.situation != Vessel.Situations.LANDED && vessel.situation != Vessel.Situations.SPLASHED))
                return false;
            try
            {
                if (isHeld == null)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name != "ExpanseFoundations") continue;
                        Type hold = assembly.GetType("Expanse.Foundations.Hold", false);
                        isHeld = hold == null ? null : hold.GetMethod("IsHeld", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vessel) }, null);
                        break;
                    }
                }
                return isHeld != null && isHeld.Invoke(null, new object[] { vessel }) is bool held && held;
            }
            catch (Exception) { return false; }
        }
    }
}
