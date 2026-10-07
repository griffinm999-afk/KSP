using System.Text.Json;
using System.Text.RegularExpressions;

namespace Expanse.Clock.Core;

internal static class ColonyValidationDiagnostic
{
    // Closed vocabulary: even malicious strings in JSON or exception messages cannot enter logs.
    static readonly HashSet<string> Checks = new(StringComparer.Ordinal)
    {
        "Empty colony observation.", "Invalid colony snapshot context.", "Invalid colony vessel.",
        "Invalid physical crew capacity.", "Invalid complete crew roster.", "Invalid crew member.",
        "Crew roster exceeds observation budget.", "Incomplete crew roster must omit names.",
        "Invalid colony tank.", "Invalid colony converter.", "Invalid colony power rate.",
        "Invalid colony power estimate.", "Invalid vessel census.",
        "Complete vessel census disagrees with the colony observation.",
        "An incomplete vessel census cannot assert identities.", "Invalid WOLF observation.",
        "Invalid WOLF depot.", "Invalid WOLF resource stream."
    };
    static readonly HashSet<string> Fields = typeof(ColonySnapshot).Assembly.GetTypes()
        .Where(t => t.Namespace == "Expanse.Clock.Core" && (t.Name.StartsWith("Colony", StringComparison.Ordinal) || t.Name.StartsWith("Wolf", StringComparison.Ordinal)))
        .SelectMany(t => t.GetProperties()).Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet(StringComparer.Ordinal);
    internal static string Describe(Exception ex)
    {
        var type = ex switch { JsonException => "JsonException", InvalidDataException => "InvalidDataException", OverflowException => "OverflowException", _ => "ArgumentException" };
        var check = Checks.Contains(ex.Message) || ex.Message == "Invalid production telemetry." || Regex.IsMatch(ex.Message, @"\AInvalid production telemetry \(check [0-9]{1,4}\)\.\z")
            ? ex.Message : "Schema conversion failed.";
        var rawPath = ex is JsonException json ? json.Path : ex.Data["ValidationPath"] as string;
        var path = "$.colony";
        if (rawPath is not null && rawPath.Length <= 200 && Regex.IsMatch(rawPath, @"\A\$(?:\.[a-zA-Z]+|\[[0-9]{1,5}\])*\z") &&
            Regex.Matches(rawPath, @"\.([a-zA-Z]+)").All(m => m.Groups[1].Value == "colony" || Fields.Contains(m.Groups[1].Value))) path = rawPath;
        if (ex.Data["ProductionModuleIndex"] is int moduleIndex && moduleIndex >= 0 && moduleIndex < 32)
            path += ".production.modules[" + moduleIndex + "]";
        var fields=ex.Data["IdentityFailures"] as string;
        if(fields is not null && fields.Length<=2000 && Regex.IsMatch(fields,@"\A[a-zA-Z0-9=.; -]+\z"))check+=" fields="+fields;
        return type + " path=" + path + " check=" + check;
    }
}
