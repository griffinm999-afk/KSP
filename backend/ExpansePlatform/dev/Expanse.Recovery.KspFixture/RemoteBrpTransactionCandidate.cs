using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Expanse.Recovery.KspFixture
{
    // Development candidate only. Only the one-shot dev fixture calls this class; the production
    // WorldBridge BRP capability remains held until save-cut and runtime proof.
    internal static class RemoteBrpTransactionCandidate
    {
        internal sealed class Selection
        {
            internal object Vessel;
            internal uint PersistentId;
            internal uint FlightId;
            internal string ResourceName;
            internal double ObservedAmount;
            internal double ObservedCapacity;
        }

        internal sealed class Result
        {
            internal bool Applied;
            internal bool Faulted;
            internal string Reason;
        }

        private sealed class Row
        {
            internal Selection Selection;
            internal object Processor, Inventory, Snapshot;
            internal double Amount, OriginalAmount, Capacity;
            internal object ResourceValues;
            internal string ConfigAmount, ConfigCapacity;
        }

        private const int MaxParts = 16384;
        private const int MaxResources = 4096;

        internal static Result Transfer(Selection source, Selection destination, double units)
        {
            try
            {
                if (source == null || destination == null || !Finite(units) || units <= 0 || units > 1 ||
                    source.Vessel == null || destination.Vessel == null ||
                    source.PersistentId == 0 || destination.PersistentId == 0 ||
                    source.FlightId == 0 || destination.FlightId == 0 ||
                    String.IsNullOrWhiteSpace(source.ResourceName) || source.ResourceName.Length > 64 ||
                    !String.Equals(source.ResourceName, destination.ResourceName, StringComparison.Ordinal) ||
                    (source.PersistentId == destination.PersistentId && source.FlightId == destination.FlightId) ||
                    (ReferenceEquals(source.Vessel, destination.Vessel) &&
                     (source.PersistentId == destination.PersistentId || source.FlightId == destination.FlightId)))
                    return Reject("Invalid or duplicate selected members, resource, or one-unit bound.");

                // These are BRP-owned catches up, performed before stock preflight.
                // A failed catch-up is never repaired by writing proto amounts.
                object sourceProcessor = FindProcessor(source.Vessel);
                object destinationProcessor = ReferenceEquals(source.Vessel, destination.Vessel)
                    ? sourceProcessor : FindProcessor(destination.Vessel);
                Invoke(sourceProcessor, "UpdateBackgroundState");
                if (!ReferenceEquals(sourceProcessor, destinationProcessor))
                    Invoke(destinationProcessor, "UpdateBackgroundState");

                Row debit = Resolve(source, sourceProcessor);
                Row credit = Resolve(destination, destinationProcessor);
                if (ReferenceEquals(debit.Inventory, credit.Inventory) || ReferenceEquals(debit.Snapshot, credit.Snapshot))
                    return Reject("Selected inventory occurs on both sides.");
                // Verify the KSP snapshot persistence API before changing a field;
                // if a version exposes only an extension method, fail closed.
                RequireMethod(debit.Snapshot, "UpdateConfigNodeAmounts");
                RequireMethod(credit.Snapshot, "UpdateConfigNodeAmounts");
                if (debit.Amount < units || credit.Capacity - credit.Amount < units)
                    return Reject("Fresh BRP stock or destination capacity is insufficient.");
                double sourceAfter = debit.Amount - units, destinationAfter = credit.Amount + units;
                if (!FiniteStock(sourceAfter, debit.Capacity) || !FiniteStock(destinationAfter, credit.Capacity))
                    return Reject("Computed stock is nonfinite or outside capacity.");

                // Resolve again immediately before the first write. There is no yield
                // or file/network work until verification or rollback finishes.
                AssertUnchanged(debit);
                AssertUnchanged(credit);
                try
                {
                    Write(debit, sourceAfter);
                    Write(credit, destinationAfter);
                    Invoke(sourceProcessor, "MarkDirty");
                    if (!ReferenceEquals(sourceProcessor, destinationProcessor)) Invoke(destinationProcessor, "MarkDirty");
                    AssertWritten(debit, sourceAfter);
                    AssertWritten(credit, destinationAfter);
                    return new Result { Applied = true, Reason = "Selected BRP inventory transaction applied in memory." };
                }
                catch (Exception applyError)
                {
                    Exception rollbackError = null;
                    TryRollback(() => RestoreRow(credit), ref rollbackError);
                    TryRollback(() => RestoreRow(debit), ref rollbackError);
                    TryRollback(() => Invoke(sourceProcessor, "MarkDirty"), ref rollbackError);
                    if (!ReferenceEquals(sourceProcessor, destinationProcessor))
                        TryRollback(() => Invoke(destinationProcessor, "MarkDirty"), ref rollbackError);
                    TryRollback(() => AssertRestored(debit), ref rollbackError);
                    TryRollback(() => AssertRestored(credit), ref rollbackError);
                    return rollbackError == null
                        ? Reject("Apply failed; selected BRP fields were restored: " + applyError.GetType().Name)
                        : new Result { Faulted = true, Reason = "Rollback unconfirmed after " + applyError.GetType().Name + ": " + rollbackError.GetType().Name };
                }
            }
            catch (Exception ex) { return Reject("Preflight failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static Row Resolve(Selection selection, object processor)
        {
            if ((bool)Get(selection.Vessel, "loaded")) throw new InvalidOperationException("Endpoint is loaded.");
            object proto = Get(selection.Vessel, "protoVessel");
            if (proto == null) throw new InvalidOperationException("Missing proto vessel.");
            object selectedPart = null;
            int parts = 0, persistentMatches = 0, flightMatches = 0;
            foreach (object part in Sequence(Get(proto, "protoPartSnapshots")))
            {
                if (++parts > MaxParts || part == null) throw new InvalidOperationException("Invalid part collection.");
                uint persistent = UInt(Get(part, "persistentId")), flight = UInt(Get(part, "flightID"));
                if (persistent == selection.PersistentId) { ++persistentMatches; selectedPart = part; }
                if (flight == selection.FlightId) ++flightMatches;
            }
            if (persistentMatches != 1 || flightMatches != 1 || UInt(Get(selectedPart, "flightID")) != selection.FlightId)
                throw new InvalidOperationException("Persistent ID and BRP flight ID do not map to one selected part.");

            object snapshot = null;
            int resourceMatches = 0, resources = 0;
            foreach (object candidate in Sequence(Get(selectedPart, "resources")))
            {
                if (++resources > MaxResources || candidate == null) throw new InvalidOperationException("Invalid part resources.");
                if (!String.Equals((string)Get(candidate, "resourceName"), selection.ResourceName, StringComparison.Ordinal)) continue;
                ++resourceMatches; snapshot = candidate;
            }
            if (resourceMatches != 1) throw new InvalidOperationException("Selected resource is missing or duplicated.");
            object flow = Get(snapshot, "flowState");
            if (!(flow is bool) || !(bool)flow) throw new InvalidOperationException("Selected resource flow is locked.");
            double amount = Double(Get(snapshot, "amount")), capacity = Double(Get(snapshot, "maxAmount"));
            if (!FiniteStock(amount, capacity) || amount != selection.ObservedAmount || capacity != selection.ObservedCapacity)
                throw new InvalidOperationException("Selected proto stock/capacity is stale or invalid.");

            object inventory = null;
            int matches = 0, scanned = 0;
            foreach (object candidate in Sequence(Get(processor, "Inventories")))
            {
                if (++scanned > MaxResources || candidate == null) throw new InvalidOperationException("Invalid BRP inventories.");
                if (Get(candidate, "ModuleId") != null || UInt(Get(candidate, "FlightId")) != selection.FlightId ||
                    !String.Equals((string)Get(candidate, "ResourceName"), selection.ResourceName, StringComparison.Ordinal)) continue;
                ++matches; inventory = candidate;
            }
            if (matches != 1 || !ReferenceEquals(Get(inventory, "Snapshot"), snapshot))
                throw new InvalidOperationException("BRP does not uniquely own the exact selected proto snapshot.");
            object resourceValues = Get(snapshot, "resourceValues");
            string configAmount = ReadSingleValue(resourceValues, "amount");
            string configCapacity = ReadSingleValue(resourceValues, "maxAmount");
            double parsedAmount, parsedCapacity;
            if (!System.Double.TryParse(configAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedAmount) ||
                !System.Double.TryParse(configCapacity, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedCapacity) ||
                parsedAmount != amount || parsedCapacity != capacity)
                throw new InvalidOperationException("Selected resource ConfigNode differs from proto stock/capacity.");
            double brpAmount = Double(Get(inventory, "Amount"));
            double original = Double(Get(inventory, "OriginalAmount"));
            double brpCapacity = Double(Get(inventory, "MaxAmount"));
            if (!FiniteStock(brpAmount, brpCapacity) || !FiniteStock(original, brpCapacity) ||
                brpAmount != amount || original != amount || brpCapacity != capacity)
                throw new InvalidOperationException("BRP amount/original, proto amount, or capacity disagree.");
            return new Row { Selection = selection, Processor = processor, Inventory = inventory,
                Snapshot = snapshot, Amount = amount, OriginalAmount = original, Capacity = capacity,
                ResourceValues = resourceValues, ConfigAmount = configAmount, ConfigCapacity = configCapacity };
        }

        private static void AssertUnchanged(Row row)
        {
            Row fresh = Resolve(row.Selection, row.Processor);
            if (!ReferenceEquals(fresh.Inventory, row.Inventory) || !ReferenceEquals(fresh.Snapshot, row.Snapshot) ||
                fresh.Amount != row.Amount || fresh.OriginalAmount != row.OriginalAmount || fresh.Capacity != row.Capacity)
                throw new InvalidOperationException("Selected BRP ownership or stock changed before write.");
        }

        private static void Write(Row row, double amount)
        {
            Set(row.Inventory, "Amount", amount);
            Set(row.Inventory, "OriginalAmount", amount);
            Set(row.Snapshot, "amount", amount);
            Invoke(row.Snapshot, "UpdateConfigNodeAmounts");
        }

        private static void AssertWritten(Row row, double amount)
        {
            if (Double(Get(row.Inventory, "Amount")) != amount || Double(Get(row.Inventory, "OriginalAmount")) != amount ||
                Double(Get(row.Snapshot, "amount")) != amount)
                throw new InvalidOperationException("Selected BRP write did not verify.");
        }

        private static void AssertRestored(Row row)
        {
            if (Double(Get(row.Inventory, "Amount")) != row.Amount ||
                Double(Get(row.Inventory, "OriginalAmount")) != row.OriginalAmount ||
                Double(Get(row.Snapshot, "amount")) != row.Amount ||
                ReadSingleValue(row.ResourceValues, "amount") != row.ConfigAmount ||
                ReadSingleValue(row.ResourceValues, "maxAmount") != row.ConfigCapacity)
                throw new InvalidOperationException("Selected BRP rollback did not verify.");
        }

        private static void RestoreConfig(Row row)
        {
            SetValue(row.ResourceValues, "amount", row.ConfigAmount);
            SetValue(row.ResourceValues, "maxAmount", row.ConfigCapacity);
        }

        private static void RestoreRow(Row row)
        {
            try { Write(row, row.Amount); }
            finally { RestoreConfig(row); }
        }

        private static void TryRollback(Action action, ref Exception firstError)
        {
            try { action(); }
            catch (Exception ex) { if (firstError == null) firstError = ex; }
        }

        private static string ReadSingleValue(object node, string name)
        {
            if (node == null) throw new InvalidOperationException("Missing selected resource ConfigNode.");
            MethodInfo method = node.GetType().GetMethod("GetValues", new[] { typeof(string) });
            if (method == null) throw new MissingMethodException(node.GetType().FullName, "GetValues");
            string[] values = method.Invoke(node, new object[] { name }) as string[];
            if (values == null || values.Length != 1 || values[0] == null)
                throw new InvalidOperationException("Selected resource ConfigNode amount/capacity is missing or duplicated.");
            return values[0];
        }

        private static void SetValue(object node, string name, string value)
        {
            MethodInfo method = node.GetType().GetMethod("SetValue", new[] { typeof(string), typeof(string), typeof(bool) });
            if (method == null) throw new MissingMethodException(node.GetType().FullName, "SetValue");
            if (!(bool)method.Invoke(node, new object[] { name, value, false }))
                throw new InvalidOperationException("Selected resource ConfigNode value could not be restored.");
        }

        private static object FindProcessor(object vessel)
        {
            if ((bool)Get(vessel, "loaded")) throw new InvalidOperationException("Endpoint is loaded.");
            object found = null; int count = 0;
            foreach (object module in Sequence(Get(vessel, "vesselModules")))
            {
                if (++count > MaxResources) throw new InvalidOperationException("Vessel modules exceed bound.");
                if (module == null || module.GetType().FullName != "BackgroundResourceProcessing.BackgroundResourceProcessor") continue;
                if (found != null) throw new InvalidOperationException("Duplicate BRP processors.");
                found = module;
            }
            if (found == null) throw new InvalidOperationException("Missing BRP processor.");
            return found;
        }

        private static IEnumerable Sequence(object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) throw new InvalidOperationException("Expected enumerable provider collection.");
            return enumerable;
        }
        private static bool Finite(double x) { return !System.Double.IsNaN(x) && !System.Double.IsInfinity(x); }
        private static bool FiniteStock(double amount, double capacity) { return Finite(amount) && Finite(capacity) && amount >= 0 && capacity >= 0 && amount <= capacity; }
        private static uint UInt(object value) { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        private static double Double(object value) { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        private static Result Reject(string reason) { return new Result { Reason = reason }; }
        private static object Get(object instance, string name)
        {
            if (instance == null) throw new InvalidOperationException("Null provider object for " + name);
            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return property.GetValue(instance, null);
            throw new MissingMemberException(type.FullName, name);
        }
        private static void Set(object instance, string name, double value)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(double)) throw new MissingFieldException(instance.GetType().FullName, name);
            field.SetValue(instance, value);
        }
        private static void Invoke(object instance, string name)
        {
            RequireMethod(instance, name).Invoke(instance, null);
        }
        private static MethodInfo RequireMethod(object instance, string name)
        {
            MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(instance.GetType().FullName, name);
            return method;
        }
    }
}
