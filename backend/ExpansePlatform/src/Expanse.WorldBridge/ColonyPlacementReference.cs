using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Expanse.WorldBridge
{
    // Foundation holds are deliberately packed and report HoldPhysics. Read a
    // current binding and measure every part now; never repair or move a reference.
    internal static class ColonyPlacementReference
    {
        internal static string Stability(Vessel vessel, double maximumSpeed)
        {
            if (vessel == null || !vessel.loaded || vessel.isEVA || !vessel.Landed || vessel.Splashed || vessel.easingInToSurface ||
                !Finite(vessel.srfSpeed) || !Finite(maximumSpeed) || vessel.srfSpeed < 0 || vessel.srfSpeed > maximumSpeed)
                return "Use a stable landed reference vessel near the colony";
            if (!vessel.HoldPhysics) return null;
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "ExpanseFoundations").ToArray();
                if (assemblies.Length != 1) return Wait("matching Foundations assembly is unavailable or ambiguous");
                return Verify(vessel, assemblies[0].GetType("Expanse.Foundations.Hold", true), assemblies[0].GetType("Expanse.Foundations.FoundationRegistry", true));
            }
            catch (Exception e)
            {
                string detail = e.GetBaseException().Message;
                if (detail.Length > 200) detail = detail.Substring(0, 200);
                return Wait("installed Foundations witness API is unavailable or incompatible: " + detail);
            }
        }

        // Kept separately so the exact read-only adapter can be exercised offline.
        internal static string Verify(Vessel vessel, Type hold, Type registryType)
        {
            if (vessel == null || !vessel.loaded || !vessel.packed || vessel.isEVA || !vessel.Landed || vessel.Splashed || vessel.mainBody == null)
                return Wait("reference must be a loaded, packed, landed Foundation base");
            var registry = Field(registryType, "Instance", null);
            var scenario = registry as ScenarioModule;
            var game = HighLogic.CurrentGame;
            if (registry == null || !(bool)Field(registry, "Ready") || !(bool)Field(hold, "HooksReady", null) || scenario == null || game == null ||
                game.scenarios == null || !game.scenarios.Any(s => ReferenceEquals(s, scenario.snapshot) && ReferenceEquals(s.moduleRef, scenario)))
                return Wait("wait for this selected save's ready Foundations registry and KSP hooks");
            var bindings = (IDictionary)Field(hold, "Bindings", null);
            var anchors = (IDictionary)Field(registry, "Anchors");
            if (!bindings.Contains(vessel)) return Wait("wait for the live Foundation binding");
            object binding = bindings[vessel], anchor = Field(binding, "Anchor");
            string id = anchor == null ? null : Field(anchor, "Id") as string;
            Guid parsed;
            if (!ReferenceEquals(Field(binding, "Vessel"), vessel) || !Guid.TryParse(id, out parsed) || !anchors.Contains(id) || !ReferenceEquals(anchors[id], anchor))
                return Wait("Foundation binding does not belong to the current registry and vessel");
            if (!string.Equals(Field(binding, "Problem") as string, "", StringComparison.Ordinal) || !(bool)Field(binding, "AdaptersReady"))
                return Wait("Foundation binding is suspended or its adapters are not ready; inspect Foundations");
            double radius = (double)Field(anchor, "BodyRadius");
            if (!string.Equals(Field(anchor, "Body") as string, vessel.mainBody.bodyName, StringComparison.Ordinal) || !Finite(radius) || radius <= 0 ||
                !Finite(vessel.mainBody.Radius) || Math.Abs(radius - vessel.mainBody.Radius) > 0.01)
                return Wait("Foundation body or radius does not match this flight");
            var marker = Method(hold, "Marker", typeof(Vessel)).Invoke(null, new object[] { vessel });
            if (marker == null || !string.Equals(Field(marker, "foundationId") as string, id, StringComparison.Ordinal))
                return Wait("Foundation vessel marker does not match the live anchor");
            var parts = vessel.parts;
            if (parts == null || parts.Count == 0 || parts.Any(p => p == null || p.vessel != vessel || p.State == PartStates.DEAD || !p.started || p.transform == null) ||
                vessel.rootPart == null || !parts.Contains(vessel.rootPart)) return Wait("Foundation live part membership is incomplete");
            var ids = new HashSet<uint>(parts.Select(p => p.persistentId));
            if (ids.Contains(0) || ids.Count != parts.Count) return Wait("Foundation part identities are zero or duplicated");
            uint referenceId = (uint)Field(anchor, "ReferenceId");
            var reference = parts.SingleOrDefault(p => p.persistentId == referenceId);
            if (reference == null || !ReferenceEquals(Field(binding, "ReferencePart"), reference)) return Wait("Foundation reference part does not match the anchor");
            var members = ((IEnumerable)Field(anchor, "Members")).Cast<object>().ToArray();
            var map = (IDictionary)Field(binding, "Members");
            var order = ((IEnumerable)Field(binding, "Order")).Cast<object>().ToArray();
            if (members.Length != parts.Count || map.Count != parts.Count || order.Length != parts.Count || order.Distinct().Count() != parts.Count ||
                order.Any(p => !parts.Any(part => ReferenceEquals(part, p)))) return Wait("Foundation member map or order is incomplete");
            var memberIds = new HashSet<uint>();
            foreach (var member in members)
            {
                uint memberId = (uint)Field(member, "Id");
                if (memberId == 0 || !memberIds.Add(memberId) || !ids.Contains(memberId)) return Wait("Foundation saved member identities do not match the live parts");
                var part = parts.Single(p => p.persistentId == memberId);
                if (!map.Contains(part) || !ReferenceEquals(map[part], member) || (uint)Field(member, "FlightId") != part.flightID)
                    return Wait("Foundation member fields or part mapping do not match");
            }
            foreach (DictionaryEntry other in anchors)
                if (!ReferenceEquals(other.Value, anchor) && ids.Contains((uint)Field(other.Value, "ReferenceId")))
                    return Wait("Reference contains another Foundation anchor");
            object surface = Field(anchor, "Surface");
            ValidatePose(surface);
            var bodyWorld = Method(hold, "BodyWorld", typeof(CelestialBody)).Invoke(null, new object[] { vessel.mainBody });
            ValidatePose(bodyWorld);
            object frame = Multiply(bodyWorld, surface);
            foreach (var part in parts)
            {
                object local = Field(map[part], "Local"); ValidatePose(local);
                object expected = Multiply(frame, local);
                object actual = Method(hold, "World", typeof(Part)).Invoke(null, new object[] { part });
                ValidatePose(expected); ValidatePose(actual);
                object actualPosition = Field(actual, "Position"), expectedPosition = Field(expected, "Position");
                var difference = RequiredMethod(actualPosition.GetType(), "op_Subtraction", BindingFlags.Public | BindingFlags.Static, actualPosition.GetType(), actualPosition.GetType()).Invoke(null, new[] { actualPosition, expectedPosition });
                var length = difference.GetType().GetProperty("Length");
                if (length == null) throw new InvalidOperationException("Missing Foundations API " + difference.GetType().FullName + ".Length");
                double metres = (double)length.GetValue(difference, null);
                object actualRotation = Field(actual, "Rotation"), expectedRotation = Field(expected, "Rotation");
                double degrees = (double)RequiredMethod(actualRotation.GetType(), "AngleDegrees", BindingFlags.Public | BindingFlags.Instance, actualRotation.GetType()).Invoke(actualRotation, new[] { expectedRotation });
                if (!Finite(metres) || !Finite(degrees) || metres > 0.01 || degrees > 0.01)
                    return Wait("fresh live part " + part.persistentId + " pose error " + metres.ToString("G6", CultureInfo.InvariantCulture) + " m / " + degrees.ToString("G6", CultureInfo.InvariantCulture) + " degree exceeds 0.01 m / 0.01 degree; wait or inspect Foundations");
            }
            return null;
        }

        private static string Wait(string detail) { return "Awaiting a verified anchored reference: " + detail; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static object Field(object value, string name) { if (value == null) throw new InvalidOperationException("Missing witness"); return Field(value.GetType(), name, value); }
        private static object Field(Type type, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.Public | (value == null ? BindingFlags.Static : BindingFlags.Instance));
            if (field == null) throw new InvalidOperationException("Missing Foundations API " + type.FullName + "." + name);
            return field.GetValue(value);
        }
        private static MethodInfo RequiredMethod(Type type, string name, BindingFlags flags, params Type[] arguments)
        {
            var method = type.GetMethod(name, flags, null, arguments, null);
            if (method == null) throw new InvalidOperationException("Missing Foundations API " + type.FullName + "." + name);
            return method;
        }
        private static MethodInfo Method(Type type, string name, Type argument) { return RequiredMethod(type, name, BindingFlags.Public | BindingFlags.Static, argument); }
        private static object Multiply(object a, object b) { return RequiredMethod(a.GetType(), "op_Multiply", BindingFlags.Public | BindingFlags.Static, a.GetType(), a.GetType()).Invoke(null, new[] { a, b }); }
        private static void ValidatePose(object pose)
        {
            object position = Field(pose, "Position"), rotation = Field(pose, "Rotation");
            foreach (string coordinate in new[] { "X", "Y", "Z" }) if (!Finite((double)Field(position, coordinate)) || !Finite((double)Field(rotation, coordinate))) throw new InvalidOperationException("Nonfinite pose");
            double x = (double)Field(rotation, "X"), y = (double)Field(rotation, "Y"), z = (double)Field(rotation, "Z"), w = (double)Field(rotation, "W");
            if (!Finite(w) || Math.Abs(x * x + y * y + z * z + w * w - 1) > 1e-5) throw new InvalidOperationException("Invalid pose orientation");
        }
    }
}
