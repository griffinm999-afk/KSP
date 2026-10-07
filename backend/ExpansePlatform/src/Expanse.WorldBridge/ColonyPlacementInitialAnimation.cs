using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using USITools;

namespace Expanse.WorldBridge
{
    internal static class ColonyPlacementInitialAnimation
    {
        private sealed class State
        {
            internal AnimationState Native;
            internal float Time, Speed, Weight;
            internal int Layer;
            internal bool Enabled;
            internal State(AnimationState native)
            { Native = native; Time = native.time; Speed = native.speed; Weight = native.weight; Layer = native.layer; Enabled = native.enabled; }
            internal void Restore()
            { Native.time = Time; Native.speed = Speed; Native.weight = Weight; Native.layer = Layer; Native.enabled = Enabled; }
        }
        private sealed class Plan
        {
            internal Animation Animation;
            internal AnimationState Primary;
            internal State[] States;
            internal bool Deployed;
            internal int Layer;
        }

        internal static void Prepare(ShipConstruct ship, ConfigNode template)
        {
            if (ship == null || ship.parts == null || ship.parts.Count == 0 || ship.parts.Count > 256 || template == null || FlightGlobals.Vessels == null)
                throw new InvalidOperationException("Initial animation geometry lacks an unregistered construct witness");
            if (ship.parts.Any(p => p == null || p.started || p.vessel != null || FlightGlobals.Vessels.Any(v => v != null && v.parts != null && v.parts.Contains(p))))
                throw new InvalidOperationException("Initial animation geometry cannot alter a started or registered native part");
            var nodes = template.GetNodes("PART").ToDictionary(n => uint.Parse(n.GetValue("part").Substring(n.GetValue("part").LastIndexOf('_') + 1), CultureInfo.InvariantCulture));
            var plans = new List<Plan>();
            foreach (var part in ship.parts)
            {
                string name = part.partInfo == null ? "" : part.partInfo.name;
                if (name != "Duna.Pioneer" && name != "Duna.PDU" && name != "Duna.Agriculture") continue;
                var modules = part.Modules.Cast<PartModule>().Where(m => m.moduleName == "USIAnimation").ToArray();
                ConfigNode node;
                if (modules.Length != 1 || modules[0].GetType() != typeof(USIAnimation) || !nodes.TryGetValue(part.craftID, out node))
                    throw new InvalidOperationException("Duna initial deployment geometry lacks its exact native animation identity");
                var declarations = node.GetNodes("MODULE").Where(n => n.GetValue("name") == "USIAnimation").ToArray();
                bool deployed;
                if (declarations.Length != 1 || !bool.TryParse(declarations[0].GetValue("isDeployed"), out deployed))
                    throw new InvalidOperationException("Duna initial deployment geometry lacks explicit saved deployment intent");
                var module = (USIAnimation)modules[0];
                if (module.isDeployed != deployed || module.deployAnimationName != "Deploy" || module.PrimaryLayer != 2 ||
                    !string.IsNullOrEmpty(module.secondaryAnimationName) || module.inflatable || module.shedOnInflate ||
                    !ColonyPlacementRequest.Finite(module.inflatedMultiplier) || module.inflatedMultiplier > 0 || !string.IsNullOrEmpty(module.ResourceCosts))
                    throw new InvalidOperationException("Duna initial deployment geometry differs from the reviewed resource-free primary animation");
                var animations = part.FindModelAnimators(module.deployAnimationName);
                if (animations == null || animations.Length != 1 || animations[0] == null)
                    throw new InvalidOperationException("Duna initial deployment geometry has missing or ambiguous native animators");
                var animation = animations[0]; var primary = animation[module.deployAnimationName];
                var states = animation.Cast<AnimationState>().Select(s => new State(s)).ToArray();
                if (primary == null || !ColonyPlacementRequest.Finite(primary.length) || primary.length <= 0 || states.Length == 0 || states.Length > 16 ||
                    states.Any(s => !ColonyPlacementRequest.Finite(s.Time) || !ColonyPlacementRequest.Finite(s.Speed) || !ColonyPlacementRequest.Finite(s.Weight)))
                    throw new InvalidOperationException("Duna initial deployment geometry has unavailable native clip states");
                plans.Add(new Plan { Animation = animation, Primary = primary, States = states, Deployed = deployed, Layer = module.PrimaryLayer });
            }
            foreach (var plan in plans)
            {
                // OnLoad queues USI's fast Play, but synchronous LoadShip has not
                // advanced a Unity animation frame. Measure its saved terminal
                // pose before choosing root height. Only this unregistered model
                // is sampled; flight Start still owns deployment and all modules.
                try
                {
                    foreach (var state in plan.States) state.Native.enabled = false;
                    plan.Primary.enabled = true; plan.Primary.weight = 1; plan.Primary.layer = plan.Layer;
                    plan.Primary.speed = 0; plan.Primary.normalizedTime = plan.Deployed ? 1 : 0;
                    plan.Animation.Sample();
                }
                finally { foreach (var state in plan.States) state.Restore(); }
            }
        }
    }
}
