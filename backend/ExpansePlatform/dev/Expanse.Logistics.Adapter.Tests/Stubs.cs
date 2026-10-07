using Expanse.Domain;
using System.Globalization;

// Minimal KSP/BRP boundary double. Tests compile the production adapters unchanged;
// this is deliberately not an alternative inventory implementation.
public enum VesselType { BASE, Ship, EVA }
public enum GameScenes { FLIGHT, SPACECENTER, TRACKSTATION }
public static class HighLogic { public static bool LoadedSceneIsFlight = true, LoadedSceneIsGame = true; public static object CurrentGame = new object(); public static GameScenes LoadedScene = GameScenes.FLIGHT; }
public static class FlightGlobals { public static List<Vessel> Vessels = new(); public static Vessel ActiveVessel; }
public sealed class Vessel { public Guid id = Guid.NewGuid(); public bool loaded = true, packed; public VesselType vesselType = VesselType.BASE; public List<Part> parts = new(); public List<object> vesselModules = new(); public ProtoVessel protoVessel = new(); }
public sealed class ProtoVessel { public List<ProtoPartSnapshot> protoPartSnapshots = new(); }
public sealed class Part { public uint persistentId, flightID; public List<PartResource> Resources = new(); }
public sealed class PartResource { public string resourceName; public double amount, maxAmount; public bool flowState = true; }
public sealed class ProtoPartSnapshot { public uint persistentId, flightID; public List<ProtoPartResourceSnapshot> resources = new(); }
public sealed class ProtoPartResourceSnapshot
{
 public string resourceName; public double amount, maxAmount; public bool flowState = true; public ConfigNode resourceValues = new();public Action OnUpdate;
 public void UpdateConfigNodeAmounts() { resourceValues.SetValue("amount", amount.ToString("R",CultureInfo.InvariantCulture),true); resourceValues.SetValue("maxAmount",maxAmount.ToString("R",CultureInfo.InvariantCulture),true);OnUpdate?.Invoke(); }
}
public sealed class ConfigNode
{
 public Dictionary<string,List<string>> Values = new(); public bool FailWrites;public Action OnWrite,OnRead;
 public string[] GetValues(string key) {OnRead?.Invoke();return Values.TryGetValue(key,out var value)?value.ToArray():[];}
 public bool SetValue(string key,string value,bool createIfNotFound) { if(FailWrites)return false; if(!Values.ContainsKey(key)&&!createIfNotFound)return false; Values[key]=[value];OnWrite?.Invoke();return true; }
}
namespace UnityEngine { public static class Debug { public static void Log(string text) { } } }
namespace BackgroundResourceProcessing
{
 public sealed class ResourceInventory { public uint FlightId; public uint? ModuleId; public string ResourceName; public double Amount,OriginalAmount,MaxAmount; public ProtoPartResourceSnapshot Snapshot; }
 public sealed class BackgroundResourceProcessor { public List<ResourceInventory> Inventories = new(); public Action OnDirty, OnCatchup; public bool Dirty; public void MarkDirty() { Dirty=true;OnDirty?.Invoke(); } public void UpdateBackgroundState() { OnCatchup?.Invoke(); } }
}
namespace Expanse.WorldBridge
{
 internal static class WorldBridgeAddon { internal static bool RemotePhysicalCandidateEnabled => RemoteBrpInventoryGateway.SupportedProviderAvailable; internal static bool RemotePhysicalDiagnosticsEnabled => false; }
 internal sealed class DepotRegistration { internal string DepotId,OwnerKind="legacy",OwnerColonyId="",OwnerFacilityId="",ApprovedSpecHash="";internal int MembershipRevision;internal uint Anchor;internal List<uint> MemberIds = new(); }
 internal sealed class DepotRegistryModule
 {
  internal static DepotRegistryModule Instance;internal bool IsReady = true,IsCorrupt;internal string WorldId = "test-world";internal List<DepotRegistration> Registrations = new();internal int Invalidations;internal long MutationRevision;internal int LegacySnapshotCalls;
  internal DepotRegistrySnapshot CreateEffectRegistrySnapshot() { LegacySnapshotCalls++;return Snapshot(Registrations.Where(r=>r.OwnerKind=="legacy")); }
  internal DepotRegistrySnapshot CreateSelectedEffectRegistrySnapshot(string depotId) {var selected=Registrations.Where(r=>r.DepotId==depotId).ToArray();return IsReady&&!IsCorrupt&&selected.Length==1&&selected[0].MemberIds.Count<=64?Snapshot(selected):null;}
  DepotRegistrySnapshot Snapshot(IEnumerable<DepotRegistration> entries) { var depots=entries.Select(x=>new DepotRecord {DepotId=x.DepotId,MembershipRevision=x.MembershipRevision,MembershipHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WorldId+"|"+x.Anchor+"|"+string.Join(",",x.MemberIds.OrderBy(m=>m))+"|"+x.OwnerKind+"|"+x.OwnerColonyId+"|"+x.OwnerFacilityId+"|"+x.ApprovedSpecHash))).ToLowerInvariant(),Active=true}).ToArray();return new DepotRegistrySnapshot {RegistryVersion="test",RegistryHash=OperationIdentity.ComputeDepotRegistryHash(depots),Depots=depots}; }
  internal void InvalidateEffectInventoryObservations() { Invalidations++; }
 }
}
