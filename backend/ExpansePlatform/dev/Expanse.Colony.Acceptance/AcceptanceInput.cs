using System.Text.Json;
using System.Text.Json.Serialization;
using Expanse.Domain.Colonies;

namespace Expanse.Colony.Acceptance;

// All reviews use the actual current DTO and wire validators. No catalog,
// permission, provider witness or default operating consent is filled here.
internal static class AcceptanceInput
{
    internal const int MaximumIntentBytes=32768;
    internal static ColonyFoundingIntent ReadIntent(string path)
    {
        path=Path.GetFullPath(path);
        if(new FileInfo(path).Length>MaximumIntentBytes)throw new InvalidDataException("Founding intent exceeds the bounded typed contract.");
        return ParseIntent(File.ReadAllBytes(path));
    }
    internal static ColonyFoundingIntent ParseIntent(byte[] bytes)
    {
        if(bytes.Length==0||bytes.Length>MaximumIntentBytes)throw new InvalidDataException("Founding intent exceeds the bounded typed contract.");
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});
        static void Unique(JsonElement element)
        {
            if(element.ValueKind==JsonValueKind.Object)
            {
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var field in element.EnumerateObject())
                {if(!names.Add(field.Name))throw new InvalidDataException("Duplicate founding intent field: "+field.Name);Unique(field.Value);}
            }
            else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Unique(child);
        }
        Unique(document.RootElement);
        var intent=JsonSerializer.Deserialize<ColonyFoundingIntent>(bytes,new JsonSerializerOptions{MaxDepth=16,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidDataException("Founding intent is missing.");
        ColonyStateCodec.ValidateFoundingIntent(intent);return intent;
    }
    internal static ColonyManagementSnapshot ReadSnapshot(string path)
    {
        path=Path.GetFullPath(path);
        if(new FileInfo(path).Length>ColonyManagementWire.MaxFrameBytes)throw new InvalidDataException("Recorded snapshot exceeds frame bound.");
        return ParseSnapshot(File.ReadAllBytes(path));
    }
    internal static ColonyManagementSnapshot ParseSnapshot(byte[] bytes)=>ColonyManagementWire.DecodeResponse(bytes).Snapshot??throw new InvalidDataException("Recorded response lacks a snapshot.");
}
