using System.Collections.Generic;
namespace Expanse.Domain.Colonies
{
    // Null Operations keeps prior paid intent without registration or recurring
    // purchasing authority. Every nonzero offer is explicitly reviewable.
    public sealed class ColonyProductionOperationsIntent
    {
        public bool RegisterCreatedEndpoints {get;set;}
        public bool AutomaticIntake {get;set;}
        public bool AutomaticInputRefill {get;set;}
        public List<ColonyProductionOperatingResource> Resources {get;set;}=new List<ColonyProductionOperatingResource>();
    }
    public sealed class ColonyProductionOperatingResource
    {
        public string Resource {get;set;}="";
        public long InitialReserve {get;set;}
        public bool ReorderEnabled {get;set;}
        public long ReorderPoint {get;set;}
        public long TargetAmount {get;set;}
        public double CadenceSeconds {get;set;}=21600;
        public double NativeTargetFraction {get;set;}=.5;
    }
    public sealed class ColonyProductionRegistryAuthority
    {
        public bool Ready {get;set;}
        public string WorldId {get;set;}="";
        public long Revision {get;set;}
        public string Witness {get;set;}="";
        public int ColonyEndpoints {get;set;}
        public int TotalMembers {get;set;}
        public int MaximumColonyEndpoints {get;set;}=128;
        public int MaximumMembers {get;set;}=4096;
        public string Reason {get;set;}="Scoped physical registry unavailable.";
    }
    public sealed class ColonyProductionEndpointSpec
    {
        public string Id {get;set;}="";
        public string BuildingId {get;set;}="";
        public string Role {get;set;}="";
        public uint AnchorCraftPartId {get;set;}
        public List<uint> MemberCraftPartIds {get;set;}=new List<uint>();
        public string Hash {get;set;}="";
    }
    public sealed class ColonyProductionInventoryQuote
    {
        public string RegistryWorldId {get;set;}="";
        public long RegistryRevision {get;set;}
        public string RegistryWitness {get;set;}="";
        public bool AutomaticIntake {get;set;}
        public bool AutomaticInputRefill {get;set;}
        public List<ColonyProductionEndpointSpec> Endpoints {get;set;}=new List<ColonyProductionEndpointSpec>();
        public List<ColonyProductionOperatingResource> Resources {get;set;}=new List<ColonyProductionOperatingResource>();
        public List<MaterialRequirement> ReserveMaterials {get;set;}=new List<MaterialRequirement>();
    }
    public sealed class ColonyProductionInventoryClaim
    {
        public bool ReservesReleased {get;set;}
        public bool PoliciesApplied {get;set;}
        public List<ColonyProductionEndpointReceipt> Endpoints {get;set;}=new List<ColonyProductionEndpointReceipt>();
    }
    public sealed class ColonyProductionEndpointReceipt
    {
        public string SpecId {get;set;}="";
        public string EffectId {get;set;}="";
        public string DepotId {get;set;}="";
        public string FacilityId {get;set;}="";
        public string VesselId {get;set;}="";
        public uint AnchorPartId {get;set;}
        public List<uint> MemberPartIds {get;set;}=new List<uint>();
        public string State {get;set;}="prepared";
        public long BeforeRegistryRevision {get;set;}
        public string BeforeWitness {get;set;}="";
        public string AfterWitness {get;set;}="";
        public string MembershipHash {get;set;}="";
        public string Reason {get;set;}="Awaiting exact paid-created members.";
    }
}
