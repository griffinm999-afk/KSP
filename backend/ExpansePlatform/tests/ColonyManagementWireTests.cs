using System.Text;
using Expanse.Domain.Colonies;

public sealed class ColonyManagementWireTests
{
    [Fact]
    public void Mutation_requires_an_operation_id_and_rejects_snapshot_command_smuggling()
    {
        var request=new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=new ColonyCommand {OperationId="invalid"}};
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(request));
        request.Kind="snapshot";request.Command.OperationId=Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(request));
    }
    [Fact]
    public void Epoch_revision_payload_and_operation_identity_survive_wire_roundtrip()
    {
        var command=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey="world/branch/epoch",ExpectedRevision=42,Kind="foundColony",Fields=new(){{"Name","A long ordinary colony name"},{"GrowthPolicy","approval"}}};
        var request=new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=command};
        var decoded=ColonyManagementWire.DecodeRequest(ColonyManagementWire.Encode(request));
        Assert.Equal(command.OperationId,decoded.Command!.OperationId);Assert.Equal(command.ContextKey,decoded.Command.ContextKey);
        Assert.Equal(command.ExpectedRevision,decoded.Command.ExpectedRevision);Assert.Equal(ColonyStateCodec.CommandHash(command),ColonyStateCodec.CommandHash(decoded.Command));
    }
    [Fact]
    public void Frame_cap_and_unsupported_version_are_rejected()
    {
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.DecodeRequest(new byte[ColonyManagementWire.MaxFrameBytes+1]));
        var bytes=Encoding.UTF8.GetBytes("{\"Version\":2,\"RequestId\":\""+Guid.NewGuid()+"\",\"Kind\":\"snapshot\"}");
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.DecodeRequest(bytes));
    }
    [Fact]
    public void Unavailable_runtime_can_report_an_honest_empty_snapshot()
    {
        var response=new ColonyManagementWireResponse {RequestId=Guid.NewGuid().ToString("D"),Outcome="snapshot",Snapshot=new() {Status="held",Reason="Save checksum failed; original state preserved."}};
        var decoded=ColonyManagementWire.DecodeResponse(ColonyManagementWire.Encode(response));
        Assert.Null(decoded.Snapshot!.State);Assert.Empty(decoded.Snapshot.Capabilities);Assert.Null(decoded.Snapshot.AvailableFunds);
        Assert.Contains("preserved",decoded.Snapshot.Reason);
    }
    [Fact]
    public void Accepted_results_cannot_omit_authoritative_state_but_unavailable_rejections_can()
    {
        var response=new ColonyManagementWireResponse {RequestId=Guid.NewGuid().ToString("D"),Outcome="accepted",Result=new() {Outcome="accepted",State=null!}};
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(response));
        response.Outcome="rejected";response.Result.Outcome="rejected";response.Result.Reason="No initialized selected save.";
        Assert.Null(ColonyManagementWire.DecodeResponse(ColonyManagementWire.Encode(response)).Result!.State);
    }
    [Fact]
    public void People_provider_context_and_finite_route_terms_roundtrip_and_catalog_is_bounded()
    {
        var response=new ColonyManagementWireResponse {RequestId=Guid.NewGuid().ToString("D"),Outcome="snapshot",Snapshot=new()};
        response.Snapshot.People.Roster.Add(new() {RosterId="Named Applicant",Name="Named Applicant",Type="Applicant",Status="Available",Current=true,ContextKey="world/epoch"});
        response.Snapshot.People.Routes.Add(new() {Id="test-paid",Name="Explicit development modeled service",Body="Minmus",Fare=1000,RecruitmentFee=2000,ConcurrentSeats=2,TravelSeconds=64800,DevelopmentOnly=true,Evidence="Fixture; qualification pending"});
        var people=ColonyManagementWire.DecodeResponse(ColonyManagementWire.Encode(response)).Snapshot!.People;
        Assert.Equal("world/epoch",people.Roster.Single().ContextKey);Assert.Equal(2,people.Routes.Single().ConcurrentSeats);Assert.False(people.Routes.Single().Qualified);Assert.True(people.Routes.Single().DevelopmentOnly);
        response.Snapshot.People.Roster=Enumerable.Range(0,2049).Select(i=>new ColonyRosterWitness {RosterId="too-many-"+i}).ToList();
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(response));
    }
    [Fact]
    public void Local_default_is_scoped_to_canonical_installation_not_user_global()
    {
        Assert.Equal(ColonyManagementWire.PipeForInstallation(@"C:\Kerbal Space Program"),ColonyManagementWire.PipeForInstallation(@"c:\kerbal space program\"));
        Assert.NotEqual(ColonyManagementWire.PipeForInstallation(@"C:\Kerbal Space Program"),ColonyManagementWire.PipeForInstallation(@"C:\Users\griff\Documents\Codex\KSP-Colony-Demo"));
        Assert.Throws<ArgumentException>(()=>ColonyManagementWire.ValidatePipeName(@"remote\pipe"));
    }
}
