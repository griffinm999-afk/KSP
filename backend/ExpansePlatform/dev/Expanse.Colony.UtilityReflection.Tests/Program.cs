using System.Reflection;
using System.Reflection.Emit;
using Expanse.WorldBridge;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
object Read(object instance, string name) => ColonyUtilityReflection.Read(instance, name);
Type Error(Action action)
{
    try { action(); return null; }
    catch (Exception ex) { return ex.GetType(); }
}
object OldRead(object instance, string name)
{
    if (instance == null) return null;
    var type = instance as Type ?? instance.GetType();
    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    var field = type.GetField(name, flags);
    if (field != null) return field.GetValue(instance is Type ? null : instance);
    var property = type.GetProperty(name, flags);
    return property == null ? null : property.GetValue(instance is Type ? null : instance, null);
}

var one = new Mutable { Amount = 20, Cooling = false };
var two = new Mutable { Amount = 2, Cooling = true };
Check((double)Read(one, "Amount") == 20 && (double)Read(two, "Amount") == 2, "same CLR metadata, separate current instance values");
one.Amount = 19.5;
Check((double)Read(one, "Amount") == 19.5, "fuel changes remain visible after metadata warmup");
Check(!(bool)Read(one, "Cooling"), "initial getter");
one.Cooling = true;
Check((bool)Read(one, "Cooling"), "cooling change remains visible");
var beforeCalls = one.GetterCalls;
Read(one, "Cooling"); Read(one, "Cooling");
Check(one.GetterCalls == beforeCalls + 2, "getter executes for each observation");
Check((int)Read(typeof(Mutable), "Instructions") == 100, "static configuration read");
Mutable.Instructions = 250;
Check((int)Read(typeof(Mutable), "Instructions") == 250 && (int)Read(one, "Instructions") == 250, "static mutation observed through both call forms");
Check(Read(one, "Unknown") == null && Read(two, "Unknown") == null && Read(null, null) == null, "missing members and null instance preserve behavior");
Check((int)Read(new HiddenField(), "Choice") == 7, "field before inherited property");
Check((string)Read(new Inherited(), "Name") == "parent", "inherited public property");
Check(Read(one, "PrivateAmount") == null, "private members remain unavailable");
Check((double)Read(one, "Sometimes") == 19.5, "getter initially succeeds");
one.Fail = true;
Check(Error(() => Read(one, "Sometimes")) == typeof(TargetInvocationException), "getter exception is fresh after successful read");
one.Fail = false;
Check((double)Read(one, "Sometimes") == 19.5, "a previous getter exception is not retained");
foreach (var request in new (object Value, string Name)[] {
    (typeof(Mutable), "Amount"), (one, null), (new Indexer(), "Item"), (new Ambiguous(), "Item") })
    Check(Error(() => Read(request.Value, request.Name)) == Error(() => OldRead(request.Value, request.Name)), "lookup/invocation failure semantics: " + (request.Name ?? "null"));

// Force the bounded missing-member cache to churn; neither values nor exact
// inherited/static lookup behavior may change when metadata is evicted.
for (var i = 0; i < 10000; i++)
    if (Read(one, "Absent_" + i) != null) throw new Exception("Missing lookup became present.");
one.Amount = 0.25;
Check((double)Read(one, "Amount") == 0.25 && (int)Read(typeof(Mutable), "Instructions") == 250, "bounded-cache eviction preserves fresh observations");
Parallel.For(0, 10000, i => {
    var instance = new Mutable { Amount = i };
    if ((double)Read(instance, "Amount") != i) throw new Exception("Cross-instance data was retained.");
});
Check(true, "concurrent metadata reuse never shares instance state");

Type MakeType(string assemblyName)
{
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
    var builder = assembly.DefineDynamicModule("module").DefineType("Coincident.Module", TypeAttributes.Public);
    builder.DefineField("Amount", typeof(int), FieldAttributes.Public);
    return builder.CreateType();
}
var typeA = MakeType("ProviderA"); var typeB = MakeType("ProviderB");
var a = Activator.CreateInstance(typeA); var b = Activator.CreateInstance(typeB);
typeA.GetField("Amount").SetValue(a, 1); typeB.GetField("Amount").SetValue(b, 9);
Check((int)Read(a, "Amount") == 1 && (int)Read(b, "Amount") == 9 && ColonyUtilityReflection.Identity(typeA) != ColonyUtilityReflection.Identity(typeB), "coincident type names in separate assemblies remain distinct");
var assemblyIdentity = typeof(Mutable).Assembly.GetName();
var expectedIdentity = typeof(Mutable).FullName + "|" + assemblyIdentity.Name + "|" + assemblyIdentity.Version;
Check(ColonyUtilityReflection.Identity(typeof(Mutable)) == expectedIdentity && ColonyUtilityReflection.Identity(typeof(Mutable)) == expectedIdentity, "cached immutable identity matches native metadata");
Console.WriteLine($"{checks} checks passed. No KSP performance or native qualification is claimed.");

public class Mutable
{
    public double Amount;
    public bool Fail;
    public int GetterCalls;
    private double PrivateAmount => 99;
    private bool cooling;
    public bool Cooling { get { GetterCalls++; return cooling; } set { cooling = value; } }
    public double Sometimes => Fail ? throw new InvalidOperationException("changed") : Amount;
    public static int Instructions = 100;
}
public class Parent { public string Name => "parent"; public int Choice => 5; }
public class Inherited : Parent { }
public class HiddenField : Parent { public new int Choice = 7; }
public class Indexer { public int this[int i] => i; }
public class Ambiguous { public int this[int i] => i; public int this[string s] => s.Length; }
