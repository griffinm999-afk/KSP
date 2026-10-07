using System;
using Expanse.WorldBridge;

internal static class ColonyPlacementDeploymentSafetyTests
{
    internal static void Run(Action<bool, string> check)
    {
        // Exercise the real native-event boundary with every gate combination,
        // rather than letting a fake module's state stand in for the event.
        for (int mask = 0; mask < 64; mask++)
        {
            bool loaded = (mask & 1) != 0, landed = (mask & 2) != 0,
                splashed = (mask & 4) != 0, packed = (mask & 8) != 0,
                hold = (mask & 16) != 0, easing = (mask & 32) != 0;
            int events = 0; bool rejected = false;
            try { ColonyPlacementDeploymentSafety.Invoke(loaded, landed, splashed, packed, hold, easing, 0, .08, () => events++); }
            catch (InvalidOperationException) { rejected = true; }
            bool eligible = mask == 3;
            check(eligible ? !rejected && events == 1 : rejected && events == 0,
                "Native deployment crossed loaded/landed/unpacked/easing/physics gate mask=" + mask);
        }
        foreach (double speed in new[] { -.001, .080001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            int events = 0; bool rejected = false;
            try { ColonyPlacementDeploymentSafety.Invoke(true, true, false, false, false, false, speed, .08, () => events++); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && events == 0, "Native deployment crossed invalid or excessive live-speed gate");
        }
        foreach (double limit in new[] { 0d, -.08, double.NaN, double.PositiveInfinity })
        {
            int events = 0; bool rejected = false;
            try { ColonyPlacementDeploymentSafety.Invoke(true, true, false, false, false, false, 0, limit, () => events++); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && events == 0, "Invalid immutable settlement limit allowed native deployment");
        }
        int allowed = 0;
        ColonyPlacementDeploymentSafety.Invoke(true, true, false, false, false, false, .08, .08, () => allowed++);
        check(allowed == 1, "Exactly eligible live native deployment event did not execute once");
    }
}
