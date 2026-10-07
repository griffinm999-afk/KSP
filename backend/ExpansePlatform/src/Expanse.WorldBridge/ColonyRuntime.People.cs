using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // The package/adoption certifier identifies actual housing parts. A
        // vessel-level certificate or CrewCapacity cannot turn work seats into homes.
        public Func<ColonyFacility, uint, bool> CertifiedHousingPart { get; set; }
        private void PopulatePeopleEnvironment(ColonyEnvironment env)
        {
            PopulatePeopleEnvironment(env, new ColonyEnvironmentConfigObservation());
        }
        private void PopulatePeopleEnvironment(ColonyEnvironment env, ColonyEnvironmentConfigObservation environmentConfigs)
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.CrewRoster == null || state == null) return;
            // A development route cannot leak into a default/live pipe invocation.
            env.DevelopmentMode = IsAuthorizedDevelopmentContext();
            var people = env.People;
            var roster = game.CrewRoster.Crew.Concat(game.CrewRoster.Applicants).Concat(game.CrewRoster.Tourist).Concat(game.CrewRoster.Unowned).Take(2049).ToArray();
            if (roster.Length > 2048) return;
            var byName = new Dictionary<string, ColonyRosterWitness>(StringComparer.Ordinal);
            foreach (var pcm in roster)
            {
                if (pcm == null || string.IsNullOrEmpty(pcm.name) || pcm.name.Length > 160 || byName.ContainsKey(pcm.name)) continue;
                var witness = new ColonyRosterWitness { RosterId = pcm.name, Name = pcm.displayName, Trait = pcm.trait ?? "", Type = pcm.type.ToString(), Status = pcm.rosterStatus.ToString(),
                    ContextKey = ContextKey, ObservedUt = env.Ut, Current = true, ProtectedMissionCrew = pcm.isHero || ProtectedNames.Contains(pcm.name) };
                byName.Add(pcm.name, witness); people.Roster.Add(witness);
            }
            foreach (var resident in state.Colonies.SelectMany(x => x.Residents))
                if (!byName.ContainsKey(resident.RosterId)) people.Roster.Add(new ColonyRosterWitness { RosterId = resident.RosterId, Name = resident.Name, Trait = resident.Trait, Type = "Crew", Status = "Absent", Current = true, ContextKey = ContextKey, ObservedUt = env.Ut });
            var vessels = FlightGlobals.Vessels;
            if (vessels == null || vessels.Count > 2048) return;
            var globalMemberships=new Dictionary<string,int>(StringComparer.Ordinal);
            var invalidIdentities=new HashSet<string>(StringComparer.Ordinal);
            bool globalComplete=true;int observedParts=0;
            foreach(var worldVessel in vessels.Where(v=>v!=null))
            {
                IEnumerable<List<ProtoCrewMember>> partCrew;
                if(worldVessel.loaded && worldVessel.parts!=null)partCrew=worldVessel.parts.Where(p=>p!=null).Select(p=>p.protoModuleCrew);
                else if(worldVessel.protoVessel!=null)
                {
                    foreach(var proto in worldVessel.protoVessel.protoPartSnapshots.Where(p=>p!=null))if(!proto.protoCrewNames.SequenceEqual(proto.protoModuleCrew.Where(c=>c!=null).Select(c=>c.name))){globalComplete=false;foreach(var name in proto.protoCrewNames)invalidIdentities.Add(name);}
                    partCrew=worldVessel.protoVessel.protoPartSnapshots.Where(p=>p!=null).Select(p=>p.protoModuleCrew);
                }
                else {if(worldVessel.GetVesselCrew().Count>0)globalComplete=false;continue;}
                foreach(var occupants in partCrew)
                {
                    if(++observedParts>16384){globalComplete=false;break;}
                    foreach(var crew in occupants.Where(c=>c!=null))
                    {
                        globalMemberships.TryGetValue(crew.name,out var count);globalMemberships[crew.name]=count+1;
                        if(!byName.ContainsKey(crew.name) || !ReferenceEquals(game.CrewRoster[crew.name],crew))invalidIdentities.Add(crew.name);
                    }
                }
                if(observedParts>16384)break;
            }
            foreach(var person in people.Roster)
            {
                globalMemberships.TryGetValue(person.RosterId,out var count);
                if(invalidIdentities.Contains(person.RosterId) || person.Status=="Assigned" && (!globalComplete || count!=1) || person.Status!="Assigned" && count>0)person.Current=false;
            }
            var memberships = new HashSet<string>(StringComparer.Ordinal);
            bool complete = globalComplete;
            foreach (var colony in state.Colonies) people.PresentByColony[colony.Id] = new List<string>();
            foreach (var vessel in vessels)
            {
                if (vessel == null || vessel.mainBody == null) continue;
                string vesselId = vessel.id.ToString("D");
                var facility = state.Colonies.SelectMany(x => x.Facilities).FirstOrDefault(x => x.VesselId == vesselId);
                var crew = vessel.GetVesselCrew();
                if (facility == null && (crew == null || crew.Count == 0)) continue;
                var site = new ColonySite { Body = vessel.mainBody.bodyName, Latitude = vessel.latitude, Longitude = vessel.longitude };
                var present = state.Colonies.Where(c => (vessel.Landed || vessel.Splashed) && c.Site.Body == site.Body && ColonyEngine.SurfaceDistance(c.Site, site, vessel.mainBody.Radius) <= c.Site.RadiusMeters).ToArray();
                if (present.Length > 1) { complete = false; continue; }
                if (facility == null && present.Length == 0) continue;
                if (crew != null && present.Length == 1)
                    foreach (var pcm in crew.Where(x => x != null)) people.PresentByColony[present[0].Id].Add(pcm.name);
                if (vessel.loaded && vessel.parts != null)
                {
                    foreach (var part in vessel.parts.Where(x => x != null && x.CrewCapacity > 0))
                    {
                        var names = part.protoModuleCrew.Where(x => x != null).Select(x => x.name).ToList();
                        complete &= AddSeatWitness(people, facility, vessel, part.persistentId, part.CrewCapacity, names, env);
                        WitnessCrewLocations(byName, memberships, names, vesselId, part.persistentId, ref complete);
                    }
                }
                else if (vessel.protoVessel != null)
                {
                    foreach (var part in vessel.protoVessel.protoPartSnapshots)
                    {
                        if (part == null || part.partInfo == null || part.partInfo.partPrefab == null) { complete = false; continue; }
                        int capacity = part.partInfo.partPrefab.CrewCapacity;
                        if(part.partInfo.name=="KKAOSS.Habitat.MK2.g")
                        {
                            var deployment=part.modules.SingleOrDefault(m=>m.moduleName=="PlanetaryModule");
                            double animation;bool initialized;
                            capacity=deployment!=null && double.TryParse(deployment.moduleValues.GetValue("animationTime"),NumberStyles.Float,CultureInfo.InvariantCulture,out animation) && animation>=.999 &&
                                bool.TryParse(deployment.moduleValues.GetValue("hasBeenInitialized"),out initialized) && initialized ? 4 : 0;
                        }
                        if (capacity <= 0) continue;
                        var names = part.protoModuleCrew.Where(x => x != null).Select(x => x.name).ToList();
                        if (!names.SequenceEqual(part.protoCrewNames)) { complete = false; continue; }
                        complete &= AddSeatWitness(people, facility, vessel, part.persistentId, capacity, names, env);
                        WitnessCrewLocations(byName, memberships, names, vesselId, part.persistentId, ref complete);
                    }
                }
                else complete = false;
            }
            foreach (var person in people.Roster.Where(p=>p.Status=="Assigned" && p.VesselId.Length==0))
            {
                // Crew on distant mission vessels have no site mapping and are
                // ineligible for colony assignment. Their roster status is real.
                if (state.Colonies.Any(c=>c.Residents.Any(r=>r.RosterId==person.RosterId))) person.Current=false;
            }
            people.PresenceComplete = complete;
            if (people.PresentByColony.Values.Any(names=>names.Count>ColonyLimits.Residents))
            {
                // Preserve the saved people ledger; a truncated observation cannot
                // authorize disappearance or pretend every visitor is accounted.
                people.PresenceComplete=false;
                foreach(var key in people.PresentByColony.Keys.ToArray()) people.PresentByColony[key]=people.PresentByColony[key].Take(ColonyLimits.Residents).ToList();
            }
            PopulateAdoptedHabitatWitnesses(env);
            foreach (var node in environmentConfigs.PassengerRoutes)
            {
                if (people.Routes.Count >= 128) break;
                try
                {
                    var fields=new Dictionary<string,string>(StringComparer.Ordinal);
                    foreach(var key in ColonyPassengerRoutePolicy.RequiredFields){if(node.GetValues(key).Length!=1)throw new InvalidDataException("Passenger route requires exactly one "+key+" field.");fields.Add(key,PassengerSetting(node,key));}
                    var route=ColonyPassengerRoutePolicy.Parse(fields);
                    if ((!route.DevelopmentOnly || env.DevelopmentMode) && !people.Routes.Any(x => x.Id == route.Id)) people.Routes.Add(route);
                }
                catch (Exception ex) { Debug.LogWarning("[ExpanseColony] Passenger route rejected: " + Bound(ex.Message, 240)); }
            }
        }

        private static readonly string[] ProtectedNames = { "Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman" };
        private int peopleEffectCursor;
        private static string PassengerSetting(ConfigNode node, string key) { var value = node.GetValue(key); if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Missing passenger route field " + key); return value; }

        private bool AddSeatWitness(ColonyPeopleEnvironment people, ColonyFacility facility, Vessel vessel, uint id, int capacity, List<string> names, ColonyEnvironment env)
        {
            if (people.Seats.Count>=4096 || capacity>512 || names.Count>512) return false;
            bool registered = facility != null && facility.PartIds.Contains(id);
            var q = registered ? facility.Qualification : null;
            bool housing = q != null && q.HousingCertified && facility.CertifiedHomes > 0 && facility.State == "operational" &&
                HasSealedHousingPart(facility,vessel,id,env) && (CertifiedHousingPart==null || CertifiedHousingPart(facility,id));
            var utility=registered ? env.Services.Utilities.SingleOrDefault(x=>x.FacilityId==facility.Id && x.ContextKey==ContextKey && Math.Abs(env.Ut-x.ObservedUt)<=10) : null;
            bool utilities = q != null && !string.IsNullOrWhiteSpace(q.EvidenceHash) && q.PowerReliable && q.HeatSafe && q.InputsAccessible &&
                ColonyUtilityQualification.PowerSupported(utility) && ColonyUtilityQualification.HeatSupported(utility) && utility.InputsAccessible && (vessel.loaded || utility.BackgroundProviderQualified && q.BackgroundSupported);
            // Qualification of proto membership is explicit. A seat count alone is
            // not evidence of background utility support or certified housing.
            bool mappingSupported = vessel.loaded || q != null && q.BackgroundSupported;
            var workModules=vessel.loaded && vessel.parts!=null ? vessel.parts.SingleOrDefault(p=>p!=null && p.persistentId==id)?.Modules.Cast<PartModule>() : vessel.protoVessel?.protoPartSnapshots.SingleOrDefault(p=>p!=null && p.persistentId==id)?.partInfo?.partPrefab?.Modules.Cast<PartModule>();
            bool workSupported=workModules!=null && workModules.Any(m=>IsSupportedWorkModule(m,null));
            string operatorEvidence;bool operatorCabin=TryPaidPowerOperatorCabin(facility,vessel,id,null,out operatorEvidence);
            workSupported|=operatorCabin;
            people.Seats.Add(new ColonySeatWitness { FacilityId = registered ? facility.Id : "", VesselId = vessel.id.ToString("D"), PartId = id, Capacity = capacity, Occupants = names,
                Current = names.Count <= capacity && names.Distinct(StringComparer.Ordinal).Count() == names.Count, LoadedUnpacked = vessel.loaded && !vessel.packed,
                HousingCertified = housing, UtilitiesQualified = utilities, WorkSupported=workSupported, CrewMutationSupported = mappingSupported, ContextKey = ContextKey,
                Evidence = (vessel.loaded ? vessel.packed ? "Current packed Part crew mapping" : "Current unpacked Part crew mapping" : "Current ProtoPart/ProtoVessel crew mappings; background qualification required")+(operatorCabin?"; "+operatorEvidence:""),
                Body=vessel.mainBody==null ? "" : vessel.mainBody.bodyName,Latitude=vessel.latitude,Longitude=vessel.longitude,
                SurfaceTransferSupported=vessel.loaded && vessel.LandedOrSplashed && TimeWarp.CurrentRate<=1 && vessel.parts!=null && vessel.parts.Any(p=>p!=null && p.persistentId==id && p.crewTransferAvailable) });
            return true;
        }

        private bool HasSealedHousingPart(ColonyFacility facility,Vessel vessel,uint id,ColonyEnvironment env)
        {
            if(facility.HomePartPersistentIds==null || !facility.HomePartPersistentIds.Contains(id) || facility.HomePartCertificationHash.Length==0 || facility.HomePartCertificationHash!=ColonyEngine.HomeMappingHash(facility))return false;
            if (facility.Qualification.Provider == ColonyEngine.AdoptedHabitatProvider && ColonyEngine.IsAdoptedHabitatIdentity(facility)) return HasAdoptedHousingPart(facility,vessel,id,env);
            var template=env.Templates.SingleOrDefault(t=>t.Id==facility.TemplateId && t.Hash==facility.TemplateHash && t.CraftSha256==facility.CraftSha256);
            var colony=state.Colonies.SingleOrDefault(c=>c.Facilities.Any(f=>f.Id==facility.Id));if(template==null || colony==null)return false;
            if(vessel.loaded && vessel.parts!=null)
            {
                var part=vessel.parts.SingleOrDefault(p=>p!=null && p.persistentId==id);if(part==null || !QualifyHomePart(vessel,part,template))return false;
                var marker=part.Modules.OfType<ColonyPlacementMarker>().Single();
                return template.HomeCraftPartIds.Contains(marker.craftPartId) && marker.operationId==facility.PlacementOperationId && marker.worldId==state.WorldId && marker.colonyId==colony.Id && marker.plotId==facility.PlotId && marker.requestFingerprint==facility.PlacementRequestFingerprint;
            }
            var snapshot=vessel.protoVessel==null ? null : vessel.protoVessel.protoPartSnapshots.SingleOrDefault(p=>p!=null && p.persistentId==id);
            if(snapshot==null || snapshot.partInfo==null || snapshot.partInfo.name!="KKAOSS.Habitat.MK2.g")return false;
            var markers=snapshot.modules.Where(m=>m.moduleName=="ColonyPlacementMarker").ToArray();if(markers.Length!=1)return false;var node=markers[0].moduleValues;
            uint craftId;return uint.TryParse(node.GetValue("craftPartId"),NumberStyles.Integer,CultureInfo.InvariantCulture,out craftId) && template.HomeCraftPartIds.Contains(craftId) &&
                node.GetValue("operationId")==facility.PlacementOperationId && node.GetValue("worldId")==state.WorldId && node.GetValue("colonyId")==colony.Id && node.GetValue("plotId")==facility.PlotId && node.GetValue("requestFingerprint")==facility.PlacementRequestFingerprint && node.GetValue("templateSha256")==facility.CraftSha256;
        }

        private static void WitnessCrewLocations(Dictionary<string, ColonyRosterWitness> roster, HashSet<string> memberships, List<string> names, string vesselId, uint partId, ref bool complete)
        {
            foreach (var name in names)
            {
                ColonyRosterWitness person;
                if (!roster.TryGetValue(name, out person)) { complete = false; continue; }
                if (!memberships.Add(name)) { person.Current = false; complete = false; continue; }
                person.VesselId = vesselId; person.PartId = partId;
                if (person.Status != "Assigned") { person.Current = false; complete = false; }
            }
        }

        private void ApplyOnePeopleEffect()
        {
            if (mutating || !Ready || state.Effects.Any(x => x.State == "applying" || x.State == "held")) return;
            if(ApplyOneWorkerTransferEffect())return;
            var readyEffects = state.Effects.Where(x => x.State == "prepared" && (x.Kind == "peopleArrival" || x.Kind == "peopleDeparture")).ToArray();
            if (readyEffects.Length == 0) return;
            // A packed/unqualified/full destination cannot starve another colony.
            if (peopleEffectCursor >= readyEffects.Length) peopleEffectCursor = 0;
            var effect = readyEffects[peopleEffectCursor++];
            var operation = state.PeopleOperations.Single(x => x.Id == effect.TargetId);
            var beforeEnv = GetEnvironment();
            var person = beforeEnv.People.Roster.SingleOrDefault(x => x.RosterId == operation.RosterId);
            string beforeWitness = PeopleWitness(beforeEnv, operation.RosterId);
            ColonyState applying;
            try { applying = ColonyEngine.PreparePeopleEffect(state, effect.Id, beforeEnv, beforeWitness); }
            catch (InvalidDataException ex)
            {
                // A missing/full/stale prerequisite is a queued blocker, not an
                // uncertain effect: no physical mutation has started.
                string reason = Bound(ex.Message, 512);
                if (operation.Reason != reason) { var waiting = GetStateCopy(); waiting.PeopleOperations.Single(x => x.Id == operation.Id).Reason = reason; Accept(waiting); }
                return;
            }
            var game = HighLogic.CurrentGame; string epoch = loadEpoch;
            Accept(applying); mutating = true;
            try
            {
                var pcm = game.CrewRoster[operation.RosterId];
                if (pcm == null || pcm.name != operation.RosterId) throw new InvalidOperationException("Reserved actual roster identity disappeared.");
                if (operation.Kind == "arrival")
                {
                    var home = state.Colonies.Single(x => x.Id == operation.ColonyId).Facilities.Single(x => x.Id == operation.HomeFacilityId);
                    var vessel = FlightGlobals.Vessels.Single(x => x.id.ToString("D") == home.VesselId);
                    if (pcm.type == ProtoCrewMember.KerbalType.Applicant) pcm.type = ProtoCrewMember.KerbalType.Crew;
                    if (vessel.loaded)
                    {
                        var part = vessel.parts.Single(x => x.persistentId == operation.HomePartId);
                        int seat = Enumerable.Range(0, part.CrewCapacity).First(i => !part.protoModuleCrew.Any(c => c.seatIdx == i));
                        if (!part.AddCrewmemberAt(pcm, seat)) throw new InvalidOperationException("KSP rejected the reserved physical home seat.");
                        vessel.CrewListSetDirty(); vessel.RebuildCrewList();
                        if (!vessel.packed) vessel.SpawnCrew();
                    }
                    else
                    {
                        var part = vessel.protoVessel.protoPartSnapshots.Single(x => x.persistentId == operation.HomePartId);
                        int capacity = part.partInfo.partPrefab.CrewCapacity;
                        int seat = Enumerable.Range(0, capacity).First(i => !part.protoModuleCrew.Any(c => c.seatIdx == i));
                        pcm.seatIdx = seat; pcm.seat = null; pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                        part.protoModuleCrew.Add(pcm); part.protoCrewNames.Add(pcm.name); vessel.protoVessel.AddCrew(pcm);
                        vessel.protoVessel.RebuildCrewCounts(); vessel.CrewListSetDirty(); vessel.RebuildCrewList();
                    }
                }
                else
                {
                    if (person == null) throw new InvalidOperationException("Departure source witness disappeared.");
                    var vessel = FlightGlobals.Vessels.Single(x => x.id.ToString("D") == person.VesselId);
                    if (vessel.loaded)
                    {
                        var part = vessel.parts.Single(x => x.persistentId == person.PartId);
                        part.RemoveCrewmember(pcm); vessel.CrewListSetDirty(); vessel.RebuildCrewList();
                        if (!vessel.packed) vessel.SpawnCrew();
                    }
                    else
                    {
                        var part = vessel.protoVessel.protoPartSnapshots.Single(x => x.persistentId == person.PartId);
                        part.RemoveCrew(pcm); if (!vessel.protoVessel.RemoveCrew(pcm)) throw new InvalidOperationException("ProtoVessel crew release failed.");
                        vessel.protoVessel.RebuildCrewCounts(); vessel.CrewListSetDirty(); vessel.RebuildCrewList();
                    }
                    pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available; pcm.seatIdx = -1; pcm.seat = null;
                }
                if (!ReferenceEquals(game, HighLogic.CurrentGame) || epoch != loadEpoch || state != applying) throw new InvalidOperationException("Selected save changed during a crew callback.");
                var after = GetEnvironment(); var complete = ColonyEngine.CompletePeopleEffect(applying, effect.Id, after, PeopleWitness(after, operation.RosterId));
                Accept(complete);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(game, HighLogic.CurrentGame) && epoch == loadEpoch && state == applying)
                    Accept(ColonyEngine.HoldEffect(state, effect.Id, Bound(ex.Message, 512)));
                else HoldReason = "Game context changed during crew mutation; reload/reconcile selected-save roster and part witnesses.";
                Debug.LogError("[ExpanseColony] Crew effect held: " + Bound(ex.Message, 512));
            }
            finally { mutating = false; }
        }

        private static string PeopleWitness(ColonyEnvironment env, string name)
        {
            var p = env.People.Roster.SingleOrDefault(x => x.RosterId == name);
            if (p == null) return "Roster absent; context=" + env.ContextKey;
            return "Roster=" + name + "; type=" + p.Type + "; status=" + p.Status + "; vessel=" + p.VesselId + "; part=" + p.PartId +
                "; memberships=" + env.People.Seats.Count(s => s.Occupants.Contains(name)) + "; context=" + env.ContextKey;
        }
    }
}
