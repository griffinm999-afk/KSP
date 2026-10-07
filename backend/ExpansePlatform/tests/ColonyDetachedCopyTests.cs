using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyDetachedCopyTests
{
    static readonly Type Copier = typeof(ColonyState).Assembly.GetType("Expanse.Domain.Colonies.ColonyStateDetachedCopy")!;
    static readonly Type Budget = Copier.GetNestedType("CopyBudget", BindingFlags.NonPublic)!;
    static readonly Type Json = typeof(ColonyState).Assembly.GetType("Expanse.Domain.Colonies.ColonyJson")!;
    static object? Call(MethodInfo method, object? owner, params object?[] args)
    {
        try { return method.Invoke(owner, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    static byte[] Wire(ColonyState value) => (byte[])Call(Json.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(ColonyState)), null, value, ColonyLimits.MaxBytes)!;
    static (ColonyState State, long Bytes) RawCopy(ColonyState value)
    {
        var budget = Activator.CreateInstance(Budget, true)!;
        var method = Copier.GetMethod("Clone", BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(ColonyState), Budget, typeof(int)], null)!;
        var copy = (ColonyState)Call(method, null, value, budget, 0)!;
        return (copy, (long)Budget.GetProperty("Bytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(budget)!);
    }
    static PropertyInfo[] Members(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
    static object? Populated(Type type, int variant, int depth = 0)
    {
        Assert.True(depth < 64);
        if (type == typeof(string)) return "sentinel \"\\\n\t—é🚀中 " + depth + ":" + variant;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return -2147483647;
        if (type == typeof(uint)) return uint.MaxValue;
        if (type == typeof(long)) return long.MinValue;
        if (type == typeof(decimal)) return 123456789012345.1234567890123m;
        if (type == typeof(double)) return variant == 0 ? -1.2345678901234567e-120 : -0.0;
        if (type == typeof(double?)) return variant == 0 ? null : 1.2345678901234567e120;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(Populated(type.GenericTypeArguments[0], variant, depth + 1));
            return list;
        }
        var result = Activator.CreateInstance(type)!;
        foreach (var p in Members(type)) p.SetValue(result, Populated(p.PropertyType, variant, depth + 1));
        return result;
    }
    static void AssertDetached(object? before, object? after)
    {
        if (before == null) { Assert.Null(after); return; }
        Assert.NotNull(after);
        var type = before.GetType();
        Assert.Equal(type, after!.GetType());
        if (type.IsValueType || before is string) { Assert.Equal(before, after); return; }
        Assert.NotSame(before, after);
        if (before is IList left)
        {
            var right = (IList)after; Assert.Equal(left.Count, right.Count);
            for (int i = 0; i < left.Count; i++) AssertDetached(left[i], right[i]);
        }
        else foreach (var p in Members(type)) AssertDetached(p.GetValue(before), p.GetValue(after));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryPersistedPropertyMatchesIndependentWireAndAllMutableObjectsAreDetached(int variant)
    {
        // Deliberately fill EVERY reflected persisted property, including newly
        // added ones. This probes the copier independently of domain fixtures.
        var original = (ColonyState)Populated(typeof(ColonyState), variant)!;
        var result = RawCopy(original);
        var before = Wire(original); var after = Wire(result.State);
        Assert.Equal(before, after);
        using var parsed = JsonDocument.Parse(after);
        Assert.Equal(original.Revision, parsed.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(before.LongLength, result.Bytes);
        AssertDetached(original, result.State);
    }
    [Fact]
    public void ExactFourMiBWireBoundaryIsPreserved()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 0);
        state.CompactedJournalHash = "";
        int overhead = Wire(state).Length;
        state.CompactedJournalHash = new string('x', ColonyLimits.MaxBytes - overhead);
        Assert.Equal(ColonyLimits.MaxBytes, Wire(state).Length);
        Assert.Equal(ColonyLimits.MaxBytes, RawCopy(state).Bytes);
        state.CompactedJournalHash += "x";
        Assert.Throws<InvalidDataException>(() => Wire(state));
        Assert.Throws<InvalidDataException>(() => RawCopy(state));
    }
    [Theory]
    [InlineData(0xd800, false)]
    [InlineData(0xdc00, false)]
    [InlineData(0xd800, true)]
    public void MalformedUtf16IsRejectedLikeWire(int codePoint, bool suffix)
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 0);
        state.CompactedJournalHash = ((char)codePoint).ToString() + (suffix ? "x" : "");
        Assert.Throws<InvalidDataException>(() => Wire(state));
        Assert.Throws<InvalidDataException>(() => RawCopy(state));
    }
    [Fact]
    public void GraphNodeBoundCannotBeBypassedByFastCopy()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 0);
        state.Colonies.Add(new ColonyRecord { VisitorRosterIds = Enumerable.Repeat("", 250001).ToList() });
        Assert.Throws<InvalidDataException>(() => Wire(state));
        Assert.Throws<InvalidDataException>(() => RawCopy(state));
    }
    [Fact]
    public void PublicCopyRetainsValidationAndSaveRoundTripSemantics()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 123.45);
        Assert.Equal(ColonyStateCodec.Serialize(ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(state))), ColonyStateCodec.Serialize(ColonyStateCodec.Copy(state)));
        state.Revision = -1;
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Copy(state));
    }
}
