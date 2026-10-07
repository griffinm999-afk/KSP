using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Expanse.WorldBridge;

internal static class Program
{
    static int passed;
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll", path = Path.Combine(Environment.GetEnvironmentVariable("EXPANSE_PACKAGE_TEST_MANAGED_DIR") ?? @"C:\Kerbal Space Program\KSP_x64_Data\Managed", name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Run(args); Console.WriteLine("PASS package interoperability checks=" + passed); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void Run(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates";
        var paths = Directory.GetFiles(root, "*.manifest.json"); Check(paths.Length == 11, "Expected ten preserved candidates plus the separate Ranger bank");
        foreach (var path in paths)
        {
            var bytes = File.ReadAllBytes(path); var template = ColonyTemplateCatalog.ReadManifest(bytes);
            Check(!template.RuntimeCertified && template.CertificationId.EndsWith("-candidate"), "Static read promoted a candidate certificate");
            Check(ColonyPlacementRequest.Hash(File.ReadAllBytes(Path.Combine(root, template.CraftRelativePath))) == template.CraftSha256, "Craft bytes disagree with read manifest");
        }
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        byte[] housing = File.ReadAllBytes(Path.Combine(root, "housing-kpbs-v1.manifest.json"));
        Check(ColonyTemplateCatalog.ReadManifest(housing).StartupContents.Sum(c => c.Amount) == 1585000000L, "Culture changed exact paid startup quantities");
        string text = Encoding.UTF8.GetString(housing);
        Reject(text.Replace("6605000000", "6605000001"), "Altered BOM escaped complete manifest integrity check");
        Reject(text.Insert(text.IndexOf('{') + 1, "\"Hash\":\"duplicate\","), "Duplicate decoded manifest key accepted");
        Reject(text.Insert(text.IndexOf('{') + 1, "\"\\u0048ash\":\"duplicate\","), "Escaped duplicate manifest key accepted");
        Reject(Rehash(text.Replace("\"BuildFunds\": 5211", "\"BuildFunds\": -1")), "Hash-valid negative build funds accepted");
        Reject(Rehash(text.Replace("1585000000", "1585000001")), "Hash-valid double accounting in embedded startup accepted");
        Reject(Rehash(text.Replace("\"RuntimeCertified\": false", "\"RuntimeCertified\": true")), "Candidate could self-certify through a flag");
        Reject(text + " false", "Trailing manifest data accepted");
        CheckSurveyGeometry(ColonyTemplateCatalog.ReadManifest(housing));
        ColonyPlacementDeploymentSafetyTests.Run(Check);
        ColonyPlacementGroundPositioningTests.Run(Check);
        ColonyPlacementParallaxSurfaceTests.Run(Check);
        CheckSourceCacheProvenance();
        if (args.Length > 1) CheckNativeConfigHashes(root, args[1]);
        if (args.Length > 2) WriteConsumedConfigurations(args[1], args[2]);
    }
    static void CheckSourceCacheProvenance()
    {
        string original=new string('a',64), second=new string('b',64);
        Func<string, System.Collections.Generic.HashSet<string>> read = extra => ColonyTemplateCatalog.ReadSourcePartCacheHashes(ColonyCatalogJson.Parse(Encoding.UTF8.GetBytes("{\"PartConfigurationHash\":\""+original+"\""+extra+"}")));
        Check(read("").SetEquals(new[]{original}), "Old single-source catalog incompatible");
        var allowed=read(",\"AdditionalSourcePartCacheHashes\":[\""+second+"\"]");
        Check(allowed.SetEquals(new[]{original,second}), "Exact second source is not recognized");
        Check(!allowed.Contains(new string('c',64)), "Unlisted source accepted");
        Action<string> reject = extra => {try {read(extra);}catch(InvalidDataException){passed++;return;}throw new Exception("Malformed provenance accepted: "+extra);};
        reject(",\"AdditionalSourcePartCacheHashes\":null");
        reject(",\"AdditionalSourcePartCacheHashes\":\""+second+"\"");
        reject(",\"AdditionalSourcePartCacheHashes\":{}");
        reject(",\"AdditionalSourcePartCacheHashes\":[\"invalid\"]");
        reject(",\"AdditionalSourcePartCacheHashes\":[123]");
        reject(",\"AdditionalSourcePartCacheHashes\":[\""+second+"\",\""+second+"\"]");
        reject(",\"AdditionalSourcePartCacheHashes\":[\""+original+"\"]");
        reject(",\"AdditionalSourcePartCacheHashes\":[\""+original.ToUpperInvariant()+"\"]");
        var many=Enumerable.Range(0,129).Select(i=>i.ToString("x64",CultureInfo.InvariantCulture)).ToArray();
        reject(",\"AdditionalSourcePartCacheHashes\":["+string.Join(",",many.Select(x=>"\""+x+"\""))+"]");
        Check(read(",\"AdditionalSourcePartCacheHashes\":["+string.Join(",",many.Take(128).Select(x=>"\""+x+"\""))+"]").Count==129, "Supported128 additional sources rejected");
    }
    static void WriteConsumedConfigurations(string source, string output)
    {
        var sourceNode = ConfigNode.Load(source); var result = new ConfigNode();
        var compiler = sourceNode.GetNode("KISConfig"); Check(compiler != null, "Audit requires hash-bound original KIS compiler configuration");
        var method = typeof(ColonyTemplateCatalog).GetMethod("CompilerConfiguration", BindingFlags.Static | BindingFlags.NonPublic);
        var validate = typeof(ColonyTemplateCatalog).GetMethod("ValidateCompiledConfiguration", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var config in sourceNode.GetNodes("PART"))
        {
            var row = result.AddNode("AVAILABLE_PART"); row.AddValue("requestedName", config.GetValue("name"));
            var invocation = new object[] { config, compiler, null }; var consumed = (ConfigNode)method.Invoke(null, invocation); var info = (AvailablePart)invocation[2];
            // Sanitization only changes AvailablePart.cost, using the in-game
            // resource library. This detached audit compares retained nodes;
            // production still runs the exact stock sanitizer in KSP.
            row.AddValue("loadedName", info.name); row.AddValue("techRequired", info.TechRequired); row.AddValue("cost", info.cost.ToString("R", CultureInfo.InvariantCulture));
            row.AddValue("rawEntryCost", AvailablePart._GetEntryCost(info)); row.AddNode(consumed);
            validate.Invoke(null, new object[] { consumed, CloneRaw(consumed), info.name });
            var changed = CloneRaw(consumed); changed.AddValue("mass", "12345"); RejectCompiled(validate, consumed, changed, "Altered physical mass accepted");
            changed = CloneRaw(consumed); var module = changed.GetNodes("MODULE").FirstOrDefault();
            if (module != null) { module.AddValue("unexpectedFreeOutput", "12345"); RejectCompiled(validate, consumed, changed, "Altered module terms accepted"); }
            changed = CloneRaw(consumed); var resource = changed.GetNodes("RESOURCE").FirstOrDefault();
            if (resource != null) { resource.SetValue("maxAmount", "123456789", true); RejectCompiled(validate, consumed, changed, "Altered resource capacity accepted"); }
        }
        Check(result.Save(output), "Could not write detached compiler normalization audit");
    }
    static ConfigNode CloneRaw(ConfigNode node)
    {
        var copy = node.CreateCopy(); RestoreRaw(node, copy); return copy;
    }
    static void RestoreRaw(ConfigNode source, ConfigNode copy)
    {
        for (int i = 0; i < source.values.Count; i++) copy.values[i].value = source.values[i].value;
        for (int i = 0; i < source.nodes.Count; i++) RestoreRaw(source.nodes[i], copy.nodes[i]);
    }
    static void RejectCompiled(MethodInfo validate, ConfigNode expected, ConfigNode actual, string message)
    {
        try { validate.Invoke(null, new object[] { expected, actual, "test" }); }
        catch (TargetInvocationException e) { if (e.InnerException is InvalidDataException) { passed++; return; } throw; }
        throw new Exception(message);
    }
    static void CheckNativeConfigHashes(string root, string configsPath)
    {
        var configs = ConfigNode.Load(configsPath).GetNodes("PART");
        var method = typeof(ColonyTemplateCatalog).GetMethod("ConfigTerms", BindingFlags.NonPublic | BindingFlags.Static);
        var catalog = ColonyCatalogJson.Parse(File.ReadAllBytes(Path.Combine(root, "colony-template-catalog.json")));
        foreach (var config in configs)
        {
            string expected = catalog.Get("Templates").Array.SelectMany(m => m.Get("RawParts").Array).First(p => p.Get("PartName").String() == config.GetValue("name")).Get("PartConfigSha256").String();
            string native = (string)method.Invoke(null, new object[] { config, 0 });
            Check(ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(native)) == expected, "Native ConfigNode parsing changed selected installed part terms: " + config.GetValue("name"));
            var marker = config.AddNode("MODULE"); marker.AddValue("name", "ColonyPlacementMarker");
            string withMarker = (string)method.Invoke(null, new object[] { config, 0 });
            Check(ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(withMarker)) == expected, "Inert owned marker changed package qualification");
            config.SetValue("cost", "123456789", true);
            string changed = (string)method.Invoke(null, new object[] { config, 0 });
            Check(ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(changed)) != expected, "Non-owned installed config mutation was ignored");
        }
    }
    static void CheckSurveyGeometry(Expanse.Domain.Colonies.ColonyTemplate housing)
    {
        var overlap = typeof(ColonySiteSurvey).GetMethod("RectanglesOverlap", BindingFlags.Static | BindingFlags.NonPublic);
        Func<double, double, double, double, double, double, double, bool> rectangles = (x, z, angle, ax, az, bx, bz) => (bool)overlap.Invoke(null, new object[] { x, z, angle, ax, az, bx, bz });
        Check(!rectangles(0, 18, 0, 5, 9, 5, 9), "Access reservations at 18 m center spacing should only touch");
        Check(rectangles(0, 17.9, 0, 5, 9, 5, 9), "Closer housing reservations must overlap");
        Check(rectangles(13, 0, 90, 5, 9, 5, 9) && !rectangles(14, 0, 90, 5, 9, 5, 9), "Rotated plot projection failed");
        Check(rectangles(16, 8, 45, 10, 1, 1, 10) && !rectangles(16, -8, 45, 10, 1, 1, 10), "Rotated thin envelopes used the wrong signed axes");
        var offset = typeof(ColonySiteSurvey).GetMethod("OffsetPlot", BindingFlags.Static | BindingFlags.NonPublic);
        Func<double, double, double, double, double, double, Expanse.Domain.Colonies.ColonyPlot> point = (radius, lat, lon, heading, x, z) =>
            (Expanse.Domain.Colonies.ColonyPlot)offset.Invoke(null, new object[] { housing, radius, lat, lon, heading, x, z });
        var small = point(60000, 0, 0, 0, 0, 18); var large = point(600000, 0, 0, 0, 0, 18);
        Check(Math.Abs(small.Latitude * Math.PI / 180 * 60000 - 18) < 0.000001 && Math.Abs(large.Latitude * Math.PI / 180 * 600000 - 18) < 0.000001, "Body radius did not control metre offsets");
        var wrap = point(60000, 0, 179.9999, 0, 18, 0);
        Check(wrap.Longitude < -179.98 && wrap.Longitude >= -180 && Math.Abs(wrap.Latitude) < 0.000001, "Longitude wrap/east offset failed");
        var eastStreet = point(60000, 0, 0, 90, 0, 18);
        Check(Math.Abs(eastStreet.Longitude * Math.PI / 180 * 60000 - 18) < 0.000001 && Math.Abs(eastStreet.Latitude) < 0.000001, "Heading did not rotate street metre offsets");
        Check(small.WidthMeters == 10 && small.LengthMeters == 18 && string.IsNullOrEmpty(small.SurveyHash), "Geometric preview falsely claimed a loaded survey or omitted access bounds");
        bool rejected = false;
        try { point(60000, 89.899, 0, 0, 0, 100); } catch (TargetInvocationException e) { rejected = e.InnerException is InvalidOperationException; }
        Check(rejected, "Crossing supported polar boundary was not explicit");
    }
    static string Rehash(string text)
    {
        var node = ColonyCatalogJson.Parse(Encoding.UTF8.GetBytes(text)); string hash = ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(node.Canonical(true)));
        return Regex.Replace(text, "\"Hash\":\\s*\"[a-f0-9]{64}\"", "\"Hash\": \"" + hash + "\"");
    }
    static void Reject(string text, string message)
    { try { ColonyTemplateCatalog.ReadManifest(Encoding.UTF8.GetBytes(text)); } catch (InvalidDataException) { passed++; return; } throw new Exception(message); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
}
