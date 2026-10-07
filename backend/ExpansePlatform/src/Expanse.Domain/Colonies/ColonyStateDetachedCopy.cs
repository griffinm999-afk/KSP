using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Expanse.Domain.Colonies
{
    // Copy mutable state without serializing it into a temporary JSON tree. The
    // exact wire-size/node/depth checks remain in force, including UTF-8 escapes.
    internal static partial class ColonyStateDetachedCopy
    {
        static ColonyStateDetachedCopy()
        {
            var types = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            Gather(typeof(ColonyState), types);
            var actual = types.Values.SelectMany(t => Members(t).Select(p => t.Name + "." + p.Name + ":" + TypeName(p.PropertyType))).ToArray();
            if (!actual.SequenceEqual(ExpectedShape)) throw new InvalidDataException("Persisted colony records changed; regenerate the detached state copy before running this build.");
        }
        static PropertyInfo[] Members(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        static string TypeName(Type t) => t.IsGenericType ? t.GetGenericTypeDefinition() == typeof(List<>) ? "List<" + TypeName(t.GenericTypeArguments[0]) + ">" : t == typeof(double?) ? "double?" : throw new InvalidDataException("Unsupported persisted generic type.") : t.Name;
        static void Gather(Type t, SortedDictionary<string, Type> types)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { Gather(t.GenericTypeArguments[0], types); return; }
            if (t == typeof(string) || t.IsValueType) return;
            if (t.Namespace != typeof(ColonyState).Namespace || !t.IsSealed || t.GetConstructor(Type.EmptyTypes) == null) throw new InvalidDataException("Unsupported persisted copy type.");
            if (types.ContainsKey(t.Name)) return;
            types.Add(t.Name, t);
            foreach (var p in Members(t)) Gather(p.PropertyType, types);
        }
        internal static ColonyState Copy(ColonyState state) => Clone(state, new CopyBudget(), 0);
        static List<T> CloneList<T>(List<T> value, Func<T, CopyBudget, int, T> clone, CopyBudget budget, int depth)
        {
            budget.Node(depth);
            if (value == null) { budget.Add(4); return null!; }
            budget.Add(2L + Math.Max(0, value.Count - 1));
            var result = new List<T>(value.Count);
            foreach (var item in value) result.Add(clone(item, budget, depth + 1));
            return result;
        }
        static string Clone(string value, CopyBudget budget, int depth) { budget.Node(depth); budget.Quoted(value); return value; }
        static bool Clone(bool value, CopyBudget budget, int depth) { budget.Node(depth); budget.Add(value ? 4 : 5); return value; }
        static int Clone(int value, CopyBudget budget, int depth) { budget.Node(depth); budget.Number(value); return value; }
        static uint Clone(uint value, CopyBudget budget, int depth) { budget.Node(depth); budget.Number(value); return value; }
        static long Clone(long value, CopyBudget budget, int depth) { budget.Node(depth); budget.Number(value); return value; }
        static decimal Clone(decimal value, CopyBudget budget, int depth) { budget.Node(depth); budget.Number(value); return value; }
        static double Clone(double value, CopyBudget budget, int depth)
        {
            budget.Node(depth);
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("Non-finite JSON number.");
            budget.Add(value.ToString("R", CultureInfo.InvariantCulture).Length); return value;
        }
        static double? Clone(double? value, CopyBudget budget, int depth)
        {
            if (value.HasValue) return Clone(value.Value, budget, depth);
            budget.Node(depth); budget.Add(4); return null;
        }
        internal sealed class CopyBudget
        {
            int nodes;
            internal long Bytes { get; private set; }
            internal void Node(int depth) { if (depth > 64 || ++nodes > 250000) throw new InvalidDataException("Colony JSON graph bound exceeded."); }
            internal void Add(long count) { Bytes += count; if (Bytes > ColonyLimits.MaxBytes) throw new InvalidDataException("Colony JSON exceeds its payload bound."); }
            internal void Number(IFormattable value) => Add(value.ToString(null, CultureInfo.InvariantCulture).Length);
            internal void Quoted(string value)
            {
                if (value == null) { Add(4); return; }
                Add(2);
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == '"' || c == '\\') Add(2);
                    else if (c < 32) Add(6);
                    else if (char.IsSurrogate(c))
                    {
                        if (!char.IsHighSurrogate(c) || i + 1 >= value.Length || !char.IsLowSurrogate(value[++i])) throw new InvalidDataException("Invalid Unicode surrogate.");
                        Add(4);
                    }
                    else Add(c < 128 ? 1 : c < 2048 ? 2 : 3);
                }
            }
        }
    }
}
