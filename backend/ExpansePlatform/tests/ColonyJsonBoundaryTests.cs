using System.Text;
using System.Text.Json;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public sealed class ColonyJsonBoundaryTests
{
    static string Fresh() => Encoding.UTF8.GetString(ColonyStateCodec.Serialize(ColonyEngine.Create(Guid.NewGuid().ToString("D"), 1811001.478765358)));
    [Theory]
    [InlineData("\"Revision\":1,\"Revision\":2")]
    [InlineData("\"Revision\":1,\"Rev\\u0069sion\":2")]
    [InlineData("\"Revision\":01")]
    [InlineData("\"Revision\":1.2")]
    [InlineData("\"Revision\":\"1\"")]
    [InlineData("\"Revision\":null")]
    [InlineData("\"Revision\":{}")]
    [InlineData("\"Revision\":9223372036854775808")]
    public void InvalidTypedOrDuplicateTermsAreRejected(string term)
    {
        var json = Fresh().Replace("\"Revision\":0", term);
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    }
    [Fact]
    public void MetadataCannotInstantiateTypesAndIndependentParserReadsExactNumbers()
    {
        var state = ColonyEngine.Create(Guid.NewGuid().ToString("D"), 1811001.478765358);
        state.Revision = long.MaxValue - 1;
        var json = Encoding.UTF8.GetString(ColonyStateCodec.Serialize(state));
        using var independent = JsonDocument.Parse(json);
        Assert.Equal(state.Revision, independent.RootElement.GetProperty("Revision").GetInt64());
        Assert.Equal(state.SimulatedUt, independent.RootElement.GetProperty("SimulatedUt").GetDouble());
        var injected = "{\"$type\":\"System.IO.FileInfo, mscorlib\"," + json[1..];
        var decoded = ColonyStateCodec.Deserialize(Encoding.UTF8.GetBytes(injected));
        Assert.Equal(state.Revision, decoded.Revision);
        Assert.Equal(state.SimulatedUt, decoded.SimulatedUt);
    }
    [Fact]
    public void UnicodeDictionaryKeysAndEscapedTextRoundTrip()
    {
        var command = new ColonyCommand { OperationId = Guid.NewGuid().ToString("D"), ContextKey = "world/epoch", Kind = "foundColony", Fields = new() { ["Name"] = "Minmus 🚀 — \"Home\"\nLine two", ["\\"] = "é" } };
        var request = new ColonyManagementWireRequest { RequestId = Guid.NewGuid().ToString("D"), Kind = "submit", Command = command };
        var bytes = ColonyManagementWire.Encode(request);
        using var independent = JsonDocument.Parse(bytes);
        Assert.Equal(command.Fields["Name"], independent.RootElement.GetProperty("Command").GetProperty("Fields").GetProperty("Name").GetString());
        var read = ColonyManagementWire.DecodeRequest(bytes);
        Assert.Equal(command.Fields, read.Command!.Fields);
    }
    [Theory]
    [InlineData("{} {}")]
    [InlineData("{/*comment*/}")]
    [InlineData("{\"Unknown\":NaN}")]
    [InlineData("{\"Unknown\":\"\\uD800\"}")]
    public void NonJsonAndInvalidUnicodeAreRejected(string json) => Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
    [Fact]
    public void UnknownNestedFieldsCannotBypassDepthLimit()
    {
        var json = Fresh();
        var deep = "{\"Unknown\":" + new string('[', 70) + "0" + new string(']', 70) + "," + json[1..];
        Assert.Throws<InvalidDataException>(() => ColonyStateCodec.Deserialize(Encoding.UTF8.GetBytes(deep)));
    }
}
