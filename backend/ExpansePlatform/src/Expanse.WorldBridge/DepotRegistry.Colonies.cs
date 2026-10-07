using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using Expanse.Domain;

namespace Expanse.WorldBridge
{
    public sealed partial class DepotRegistryModule
    {
        public const int MaxColonyDepots=128;
        private long registryMutationRevision;
        public long MutationRevision=>registryMutationRevision;
        public int ColonyDepotCount=>registrations.Count(r=>r.OwnerKind=="colony");
        internal IEnumerable<DepotRegistration> LegacyRegistrations=>registrations.Where(r=>r.OwnerKind=="legacy");
        static void ReadRegistrationOwner(ConfigNode node,DepotRegistration record,int schema)
        {
            if(schema<3)return;
            foreach(string key in new[]{"ownerKind","ownerColonyId","ownerFacilityId","approvedSpecHash"})
                if(node.GetValues(key).Length>1)throw new FormatException("Duplicate endpoint ownership authority: "+key);
            record.OwnerKind=node.GetValue("ownerKind")??"legacy";
            record.OwnerColonyId=node.GetValue("ownerColonyId")??"";record.OwnerFacilityId=node.GetValue("ownerFacilityId")??"";record.ApprovedSpecHash=node.GetValue("approvedSpecHash")??"";
            Guid colony,facility;
            if(record.OwnerKind=="legacy")
            {if(record.OwnerColonyId.Length>0||record.OwnerFacilityId.Length>0||record.ApprovedSpecHash.Length>0)throw new FormatException("Legacy endpoint carries unsupported colony authority.");}
            else if(record.OwnerKind!="colony"||!Guid.TryParseExact(record.OwnerColonyId,"D",out colony)||colony==Guid.Empty||!Guid.TryParseExact(record.OwnerFacilityId,"D",out facility)||facility==Guid.Empty||!HashText(record.ApprovedSpecHash))throw new FormatException("Invalid paid-colony endpoint ownership.");
        }
        static void RequireSingleAuthority(ConfigNode node,params string[] keys)
        {foreach(string key in keys)if(node.GetValues(key).Length!=1)throw new FormatException("Missing or duplicate endpoint registry authority: "+key);}
        static void WriteRegistrationOwner(ConfigNode node,DepotRegistration record)
        {
            if(record.OwnerKind!="colony")return;
            node.AddValue("ownerKind",record.OwnerKind);node.AddValue("ownerColonyId",record.OwnerColonyId);node.AddValue("ownerFacilityId",record.OwnerFacilityId);node.AddValue("approvedSpecHash",record.ApprovedSpecHash);
        }
        static bool HashText(string text)=>text!=null&&text.Length==64&&text.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
        string RegistrationMembershipHash(DepotRegistration record)
        {
            string original=ComputeMembershipHash(worldId,record.OwnerKind=="legacy"?2:3,record.DepotId,record.MembershipRevision,record.Anchor,record.MemberIds.OrderBy(x=>x).ToArray());
            if(record.OwnerKind=="legacy")return original;
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(original+"|"+record.OwnerKind+"|"+record.OwnerColonyId+"|"+record.OwnerFacilityId+"|"+record.ApprovedSpecHash))).Replace("-","").ToLowerInvariant();
        }
        // Selected colony/provider context only. Legacy mirror/normal pipe keep
        // CreateEffectRegistrySnapshot and their original eight-endpoint bytes.
        internal DepotRegistrySnapshot CreateSelectedEffectRegistrySnapshot(string selectedDepotId)
        {
            if(!ready||corrupt||string.IsNullOrWhiteSpace(worldId))return null;
            var matches=registrations.Where(entry=>entry.DepotId==selectedDepotId).Take(2).ToArray();if(matches.Length!=1)return null;
            var r=matches[0];if(r.MemberIds.Count<1||r.MemberIds.Count>MaxNewDepotMembers)return null;
            var snapshot=new DepotRegistrySnapshot {RegistryVersion="selected-depot-v1",Depots=new[]{new DepotRecord {DepotId=r.DepotId,MembershipRevision=r.MembershipRevision,MembershipHash=RegistrationMembershipHash(r),Active=true}}};
            snapshot.RegistryHash=OperationIdentity.ComputeDepotRegistryHash(snapshot.Depots);return snapshot;
        }
        internal string RegistrationAuthorityWitness()
        {
            string rows=string.Join("\n",registrations.OrderBy(r=>r.DepotId,StringComparer.Ordinal).Select(r=>r.DepotId+":"+RegistrationMembershipHash(r)));
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(worldId+"|"+registryMutationRevision.ToString(CultureInfo.InvariantCulture)+"|"+rows))).Replace("-","").ToLowerInvariant();
        }
        // The caller precommits its exact durable attempt and supplies a fresh
        // same-game/epoch/paid-marker/spec authority fence. This API records
        // identities only; it does not change focus, funds, stock or crew.
        internal bool TryRegisterColonyEndpoint(Vessel vessel,string expectedWorld,long expectedRevision,string id,string colonyId,string facilityId,string specHash,string displayLabel,uint selectedAnchor,IEnumerable<uint> selectedMembers,Func<bool> authority,out string reason)
        {
            reason="Paid-colony registration authority or selected registry changed.";
            Guid parsedId,parsedColony,parsedFacility;
            if(!ready||corrupt||Instance!=this||worldId!=expectedWorld||registryMutationRevision!=expectedRevision||authority==null||!authority()||
                !Guid.TryParseExact(id,"D",out parsedId)||parsedId==Guid.Empty||!Guid.TryParseExact(colonyId,"D",out parsedColony)||parsedColony==Guid.Empty||!Guid.TryParseExact(facilityId,"D",out parsedFacility)||parsedFacility==Guid.Empty||!HashText(specHash)||selectedMembers==null)return false;
            var members=selectedMembers.Take(MaxNewDepotMembers+1).OrderBy(m=>m).ToArray();
            if(members.Length<1||members.Length>MaxNewDepotMembers||members.Any(m=>m==0)||members.Distinct().Count()!=members.Length||!members.Contains(selectedAnchor)){reason="Invalid bounded selected member/anchor mapping.";return false;}
            var existing=registrations.Where(r=>r.DepotId==id).Take(2).ToArray();
            if(existing.Length>0)
            {
                var r=existing[0];bool exact=existing.Length==1&&r.OwnerKind=="colony"&&r.OwnerColonyId==colonyId&&r.OwnerFacilityId==facilityId&&r.ApprovedSpecHash==specHash&&r.Anchor==selectedAnchor&&r.MemberIds.OrderBy(m=>m).SequenceEqual(members);
                reason=exact?"Exact previously created endpoint readback; no registration repeated.":"Deterministic endpoint ID conflicts with another authority or membership.";return exact;
            }
            if(ColonyDepotCount>=MaxColonyDepots||registrations.Sum(r=>r.MemberIds.Count)+members.Length>MaxMembers){reason="Paid-colony endpoint/member capacity is exhausted.";return false;}
            if(registrations.Any(r=>r.MemberIds.Intersect(members).Any())){reason="Selected paid members overlap an existing legacy or colony endpoint.";return false;}
            if(!FoundationRegistrationContext.CanEnumerate(vessel)||vessel.vesselType==VesselType.EVA||vessel.parts.Count>MaxVesselParts){reason="Actual paid vessel must be loaded, unpacked or genuinely Foundation anchored.";return false;}
            var map=vessel.parts.Where(p=>p!=null).GroupBy(p=>p.persistentId).ToDictionary(g=>g.Key,g=>g.ToArray());
            if(members.Any(m=>!map.ContainsKey(m)||map[m].Length!=1||map[m][0].Resources==null||map[m][0].Resources.Count==0)){reason="Actual selected resource-bearing part mapping is incomplete or ambiguous.";return false;}
            if(!GloballyUniqueSelectedMembers(vessel,members,out reason))return false;
            if(!authority()||!ready||corrupt||Instance!=this||worldId!=expectedWorld||registryMutationRevision!=expectedRevision)return false;
            var created=new DepotRegistration {DepotId=id,Label=Bound("Colony inventory · "+(displayLabel??"Paid facility"),256),Anchor=selectedAnchor,MembershipRevision=1,OwnerKind="colony",OwnerColonyId=colonyId,OwnerFacilityId=facilityId,ApprovedSpecHash=specHash};created.MemberIds.AddRange(members);registrations.Add(created);
            registryMutationRevision++;cachedRegistrySnapshot=null;ClearObservation("Paid-colony endpoint registered");reason="Exact paid-created resource members registered; inventory ownership unchanged.";return true;
        }
        static bool GloballyUniqueSelectedMembers(Vessel selected,uint[] members,out string reason)
        {
            reason="Selected paid part identity is ambiguous or exceeds the bounded current world scan.";
            if(FlightGlobals.Vessels==null)return false;
            var vessels=FlightGlobals.Vessels.Where(v=>v!=null).Take(513).ToArray();if(vessels.Length>512||vessels.Count(v=>ReferenceEquals(v,selected))!=1)return false;
            var counts=members.ToDictionary(id=>id,id=>0);int scanned=0;
            foreach(var vessel in vessels)
            {
                if(!vessel.loaded&&(vessel.protoVessel==null||vessel.protoVessel.protoPartSnapshots==null))return false;
                IEnumerable<uint> ids=vessel.loaded&&vessel.parts!=null?vessel.parts.Where(p=>p!=null).Select(p=>p.persistentId):vessel.protoVessel==null?Enumerable.Empty<uint>():vessel.protoVessel.protoPartSnapshots.Where(p=>p!=null).Select(p=>p.persistentId);
                foreach(uint id in ids){if(++scanned>32768)return false;if(counts.ContainsKey(id)&&++counts[id]>1)return false;}
            }
            if(counts.Values.Any(count=>count!=1))return false;reason=null;return true;
        }
    }
}
