using System.Text.Json;
using System.Text.RegularExpressions;

namespace Expanse.Clock.Tests;

public sealed class ColonyPlanningCatalogCoverageTests
{
    static string Platform()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir!=null){if(Directory.Exists(Path.Combine(dir.FullName,"package","GameData","ExpanseWorldBridge")))return dir.FullName;dir=dir.Parent;}
        throw new DirectoryNotFoundException("Real checked-in colony package required for policy/BOM coverage.");
    }
    [Fact] public void EveryActualTemplateBillAndStartupResourceHasFiniteConfiguredSupplyAndReceivingCapacity()
    {
        string package=Path.Combine(Platform(),"package","GameData","ExpanseWorldBridge");
        string text=File.ReadAllText(Path.Combine(package,"ColonyEconomy.cfg"));
        var line=Regex.Matches(text,@"(?m)^\s*resources\s*=\s*([^\r\n]+)$");Assert.Single(line.Cast<Match>());
        var covered=line[0].Groups[1].Value.Split(',').Select(s=>s.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal("kerbin-minmus-supply-v2",Regex.Match(text,@"(?m)^\s*id\s*=\s*(kerbin-minmus-supply[^\r\n]+)$").Groups[1].Value.Trim());
        var files=Directory.GetFiles(Path.Combine(package,"Templates"),"*.manifest.json");
        var expected=new[]{"agriculture-duna-v1","cultivation-duna-v1","cultivation-feeds-v1","fertilizer-tundra-v1","housing-kpbs-v1","lamp-stock-v1","power-duna-v1","power-ranger-bank-v1","service-kpbs-v1","storage-kpbs-v1","wolf-hoppers-v1"};
        Assert.Equal(expected,files.Select(file=>Path.GetFileName(file).Replace(".manifest.json","",StringComparison.Ordinal)).OrderBy(id=>id,StringComparer.Ordinal).ToArray());
        foreach(string file in files)
        {
            using var doc=JsonDocument.Parse(File.ReadAllBytes(file));string id=doc.RootElement.GetProperty("Id").GetString()!;
            Assert.Equal(id+".manifest.json",Path.GetFileName(file));
            foreach(string field in new[]{"Materials","EmbeddedContents"})foreach(var material in doc.RootElement.GetProperty(field).EnumerateArray())
                Assert.True(covered.Contains(material.GetProperty("Resource").GetString()!),id+" lacks configured paid stock/capacity for "+material.GetProperty("Resource").GetString());
        }
        Assert.Contains("Plutonium-238",covered);
        Assert.True(double.Parse(Regex.Match(text,@"(?m)^\s*supplierStockUnits\s*=\s*([\d.]+)").Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>0);
        Assert.True(double.Parse(Regex.Match(text,@"(?m)^\s*stagingCapacityUnits\s*=\s*([\d.]+)").Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)>0);
    }
}
