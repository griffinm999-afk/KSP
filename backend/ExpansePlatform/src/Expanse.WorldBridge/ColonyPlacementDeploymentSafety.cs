using System;

namespace Expanse.WorldBridge
{
    // The event boundary repeats the live native gate immediately before the
    // external animation call. Settlement and future callers share this guard.
    internal static class ColonyPlacementDeploymentSafety
    {
        internal static string Eligibility(bool loaded, bool landed, bool splashed, bool packed, bool holdPhysics, bool easing, double speed, double maximumSpeed)
        {
            if (!loaded || !landed || splashed || packed || holdPhysics || easing ||
                !ColonyPlacementRequest.Finite(speed) || !ColonyPlacementRequest.Finite(maximumSpeed) || maximumSpeed <= 0 || speed < 0 || speed > maximumSpeed)
                return "Waiting for stock unpack/easing and stable landed physics before native deployment";
            return null;
        }

        internal static void Invoke(bool loaded, bool landed, bool splashed, bool packed, bool holdPhysics, bool easing, double speed, double maximumSpeed, Action nativeEvent)
        {
            string reason = Eligibility(loaded, landed, splashed, packed, holdPhysics, easing, speed, maximumSpeed);
            if (reason != null) throw new InvalidOperationException(reason);
            if (nativeEvent == null) throw new InvalidOperationException("Native deployment event is absent");
            nativeEvent();
        }
    }
}
