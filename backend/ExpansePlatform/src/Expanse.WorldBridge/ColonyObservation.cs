using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Expanse.WorldBridge
{
    public sealed partial class WorldBridgeAddon
    {
        private readonly VesselCensusTracker vesselCensusTracker = new VesselCensusTracker();

        private ColonyCapture ObserveColony(double? ut, string censusEpochContext, string censusScene, bool censusIdentityReady)
        {
            ColonySnapshot snapshot = new ColonySnapshot { Status = "observed", ObservedUt = ut };
            long attempt = NextColonyAttempt(censusEpochContext);
            snapshot.Wolf = ObserveWolf(ut);
            PowerWindow powerWindow = ClosePowerWindow(ut);
            if (FlightGlobals.Vessels == null)
            {
                snapshot.Status = "unavailable"; snapshot.Reason = "KSP vessel list is unavailable.";
                return new ColonyCapture(snapshot, censusEpochContext, censusScene, attempt, null, null);
            }
            int partsInspected = 0, vesselsInspected = 0;
            // Prioritize Minmus, while still making the inspector reusable for later settlements.
            foreach (Vessel vessel in ColonyCandidates())
            {
                if (++vesselsInspected > 128 || partsInspected > 1024)
                { snapshot.Status = "truncated"; snapshot.Reason = "The vessel scan reached its safety limit."; break; }
                if (vessel.mainBody == null || !IsSurfaceSettled(vessel.situation, vessel.Landed, vessel.Splashed)) continue;
                ColonyVessel row = new ColonyVessel
                {
                    VesselId = vessel.id.ToString("D"), Name = Bound(vessel.vesselName, 100), Body = Bound(vessel.mainBody.bodyName, 64),
                    Biome = Bound(ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude), 64),
                    Latitude = vessel.latitude, Longitude = vessel.longitude,
                    ObservationBasis = vessel.loaded ? "loaded" : "snapshot",
                    Crew = vessel.GetCrewCount(), CapturedFlow = CapturePowerFlow(vessel, powerWindow)
                };
                if (String.IsNullOrWhiteSpace(row.Name)) row.Name = "Unnamed vessel";
                if (String.IsNullOrWhiteSpace(row.Biome)) row.Biome = "Unknown biome";
                List<ColonyTank> tanks = row.Tanks;
                bool relevant = false;
                if (vessel.loaded && vessel.parts != null)
                {
                    if (vessel.parts.Count > 160) { MarkTruncated(snapshot, "A settlement vessel exceeded the 160-part inspection limit."); continue; }
                    partsInspected += vessel.parts.Count;
                    foreach (Part part in vessel.parts)
                    {
                        if (part == null) continue;
                        bool warehouse = false, local = false, hasConverter = false;
                        foreach (PartModule module in part.Modules)
                        {
                            if (module == null) continue;
                            string moduleName = module.moduleName ?? "";
                            if (IsColonyModule(moduleName)) relevant = true;
                            if (moduleName == "USI_ModuleResourceWarehouse")
                            {
                                warehouse = true;
                                FieldInfo flag = module.GetType().GetField("localTransferEnabled", BindingFlags.Public | BindingFlags.Instance);
                                local = flag != null && flag.GetValue(module) is bool enabled && enabled;
                            }
                            BaseConverter converter = module as BaseConverter;
                            if (converter != null) hasConverter = true;
                            if (converter != null && (moduleName == "USI_Converter" || moduleName == "USI_Harvester" || moduleName.IndexOf("WOLF", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                if (row.Converters.Count >= 12) MarkTruncated(snapshot, "A settlement vessel has more than 12 converters.");
                                else row.Converters.Add(new ColonyConverter
                                {
                                    PartName = Bound(part.partInfo == null ? part.name : part.partInfo.title, 100),
                                    Recipe = Bound(converter.ConverterName, 100), Running = converter.IsActivated,
                                    Inputs = converter.inputList == null ? new string[0] : CaptureConverterNames(converter.inputList, snapshot),
                                    Outputs = converter is ModuleResourceHarvester harvester ? new[]{Bound(harvester.ResourceName,50)} : converter.outputList == null ? new string[0] : CaptureConverterNames(converter.outputList, snapshot)
                                });
                            }
                        }
                        foreach (PartResource resource in part.Resources)
                            if (resource != null) CaptureTank(snapshot, tanks, resource.resourceName, resource.amount, resource.maxAmount,
                                warehouse, warehouse ? (bool?)local : false, resource.flowState,
                                resource.resourceName == "Machinery" && hasConverter ? "installed" : warehouse ? "storage" : "other");
                    }
                }
                else if (vessel.protoVessel != null && vessel.protoVessel.protoPartSnapshots != null)
                {
                    if (vessel.protoVessel.protoPartSnapshots.Count > 160) { MarkTruncated(snapshot, "A settlement vessel exceeded the 160-part snapshot limit."); continue; }
                    partsInspected += vessel.protoVessel.protoPartSnapshots.Count;
                    foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                    {
                        if (part == null) continue;
                        bool warehouse = false, hasConverter = false;
                        if (part.modules != null)
                            foreach (ProtoPartModuleSnapshot module in part.modules)
                            {
                                if (module == null) continue;
                                if (IsColonyModule(module.moduleName)) relevant = true;
                                if (module.moduleName == "USI_ModuleResourceWarehouse") warehouse = true;
                                if (module.moduleName == "USI_Converter" || module.moduleName == "USI_Harvester") hasConverter = true;
                            }
                        foreach (ProtoPartResourceSnapshot resource in part.resources)
                            if (resource != null) CaptureTank(snapshot, tanks, resource.resourceName, resource.amount, resource.maxAmount,
                                warehouse, null, null,
                                resource.resourceName == "Machinery" && hasConverter ? "installed" : warehouse ? "storage" : "other");
                    }
                }
                if (!relevant) continue;
                if (ut.HasValue)
                {
                    row.Production = ObserveProductionTelemetry(vessel,ut.Value);
                    row.LifeSupport = ObserveLifeSupportTelemetry(vessel,ut.Value);
                    if(vessel.loaded&&EnsureAverageMode(vessel))row.PowerAverage = powerAverage.Observe(vessel.id.ToString("D"),RealSeconds);
                    else powerAverage.Invalidate(vessel.id.ToString("D"));
                }
                ObserveCrew(vessel, row);
                row.CapturedPower = CapturePowerEstimate(vessel);
                snapshot.Vessels.Add(row);
                if (snapshot.Vessels.Count >= 24) { snapshot.Status = "truncated"; snapshot.Reason = "Showing the first 24 settlement vessels."; break; }
            }
            SetPowerEligibleVessels(snapshot);
            var game = HighLogic.CurrentGame;
            captureParts.Add(partsInspected);
            captureVessels.Add(vesselsInspected);
            return new ColonyCapture(snapshot, censusEpochContext, censusScene, attempt,
                !censusIdentityReady || snapshot.Status != "observed" ? null : CaptureVesselIds(FlightGlobals.Vessels),
                !censusIdentityReady || snapshot.Status != "observed" || game == null || game.flightState == null ? null : CaptureProtoIds(game.flightState.protoVessels));
        }

        private static void ObserveCrew(Vessel vessel, ColonyVessel row)
        {
            int seats = 0;
            bool capacityComplete = true, rosterComplete = true;
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            if (vessel.loaded && vessel.parts != null)
            {
                foreach (Part part in vessel.parts)
                {
                    if (part == null || part.vessel != vessel) { capacityComplete = false; rosterComplete = false; continue; }
                    if (part.CrewCapacity < 0 || part.CrewCapacity > 256 || seats > 16384 - part.CrewCapacity) capacityComplete = false;
                    else seats += part.CrewCapacity;
                    AddCrew(part.protoModuleCrew, row, names, ref rosterComplete);
                }
            }
            else if (vessel.protoVessel != null && vessel.protoVessel.protoPartSnapshots != null)
            {
                foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                {
                    if (part == null) { capacityComplete = false; rosterComplete = false; continue; }
                    int capacity;
                    if (!TryProtoCapacity(part, out capacity) || capacity < 0 || capacity > 256 || seats > 16384 - capacity) capacityComplete = false;
                    else seats += capacity;
                    if (part.protoModuleCrew == null || part.protoCrewNames == null ||
                        !part.protoCrewNames.SequenceEqual(part.protoModuleCrew.Where(p => p != null).Select(p => p.name))) rosterComplete = false;
                    AddCrew(part.protoModuleCrew, row, names, ref rosterComplete);
                }
            }
            else { capacityComplete = false; rosterComplete = false; }
            var vesselCrew = vessel.GetVesselCrew();
            if (vesselCrew == null || row.CrewRoster.Count != row.Crew || vesselCrew.Count != row.Crew ||
                vesselCrew.Any(p => p == null || !names.Contains(p.name))) rosterComplete = false;
            if (!rosterComplete) row.CrewRoster.Clear();
            row.CrewRosterComplete = rosterComplete;
            row.PhysicalCrewCapacity = capacityComplete && seats >= row.Crew ? (int?)seats : null;
        }

        private static void AddCrew(IEnumerable<ProtoCrewMember> occupants, ColonyVessel row,
            HashSet<string> names, ref bool complete)
        {
            if (occupants == null) { complete = false; return; }
            foreach (ProtoCrewMember person in occupants)
            {
                if (person == null || String.IsNullOrWhiteSpace(person.name) || person.name.Length > 160 ||
                    !names.Add(person.name) || row.CrewRoster.Count >= 64) { complete = false; continue; }
                string profession = person.type.ToString() == "Tourist" ? "Tourist" : person.trait;
                if (String.IsNullOrWhiteSpace(profession) || profession.Length > 64) { complete = false; continue; }
                row.CrewRoster.Add(new ColonyCrewMember { Name = person.name, Profession = profession });
            }
        }

        private static bool TryProtoCapacity(ProtoPartSnapshot part, out int capacity)
        {
            capacity = 0;
            if (part.partInfo == null || part.partInfo.partPrefab == null) return false;
            var deployment = part.modules == null ? null : part.modules.FirstOrDefault(m => m != null && m.moduleName == "PlanetaryModule");
            return ColonySeatCapacity.TryProto(part.partInfo.name, part.partInfo.partPrefab.CrewCapacity,
                deployment == null || deployment.moduleValues == null ? null : deployment.moduleValues.GetValue("animationTime"),
                deployment == null || deployment.moduleValues == null ? null : deployment.moduleValues.GetValue("hasBeenInitialized"), out capacity);
        }

        internal static bool IsSurfaceSettled(Vessel.Situations situation, bool landed, bool splashed)
        {
            return (situation == Vessel.Situations.LANDED || situation == Vessel.Situations.SPLASHED) || landed || splashed;
        }

        private static bool IsColonyModule(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            return name.IndexOf("USI_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("WOLF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MKS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MarkTruncated(ColonySnapshot snapshot, string reason)
        {
            snapshot.Status = "truncated";
            snapshot.Reason = reason;
        }

        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static string Bound(string value, int max) { return value == null ? "" : value.Length <= max ? value : value.Substring(0, max); }

        private static string ColonyJson(ColonySnapshot s, string wolfJson = null, bool includeRoster = true, bool omitProduction = false)
        {
            StringBuilder b = new StringBuilder(2048);
            b.Append("{\"status\":").Append(Q(s.Status)).Append(",\"reason\":").Append(Q(s.Reason))
             .Append(",\"observedUt\":").Append(N(s.ObservedUt)).Append(",\"vessels\":[");
            for (int i = 0; i < s.Vessels.Count; i++)
            {
                if (i > 0) b.Append(',');
                ColonyVessel v = s.Vessels[i];
                b.Append("{\"vesselId\":").Append(Q(v.VesselId)).Append(",\"name\":").Append(Q(v.Name))
                 .Append(",\"body\":").Append(Q(v.Body)).Append(",\"biome\":").Append(Q(v.Biome))
                 .Append(",\"latitude\":").Append(N(v.Latitude)).Append(",\"longitude\":").Append(N(v.Longitude))
                 .Append(",\"observationBasis\":").Append(Q(v.ObservationBasis)).Append(",\"crew\":").Append(v.Crew.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"crewRosterComplete\":").Append(B(includeRoster && v.CrewRosterComplete))
                 .Append(",\"physicalCrewCapacity\":").Append(v.PhysicalCrewCapacity.HasValue ? v.PhysicalCrewCapacity.Value.ToString(CultureInfo.InvariantCulture) : "null")
                 .Append(",\"crewRoster\":[");
                if (includeRoster && v.CrewRosterComplete)
                    for (int j = 0; j < v.CrewRoster.Count; j++)
                    {
                        if (j > 0) b.Append(',');
                        b.Append("{\"name\":").Append(Q(v.CrewRoster[j].Name)).Append(",\"profession\":").Append(Q(v.CrewRoster[j].Profession)).Append('}');
                    }
                b.Append("],\"tanks\":[");
                for (int j = 0; j < v.Tanks.Count; j++)
                {
                    if (j > 0) b.Append(',');
                    ColonyTank t = v.Tanks[j];
                    b.Append("{\"resource\":").Append(Q(t.Resource)).Append(",\"amount\":").Append(N(t.Amount))
                     .Append(",\"capacity\":").Append(N(t.Capacity)).Append(",\"role\":").Append(Q(t.Role)).Append(",\"warehousePresent\":").Append(B(t.WarehousePresent))
                     .Append(",\"localWarehouseOn\":").Append(B(t.LocalWarehouseOn)).Append(",\"flowEnabled\":").Append(B(t.FlowEnabled)).Append('}');
                }
                b.Append("],\"power\":").Append(PowerJson(v.Power))
                 .Append(",\"powerEstimate\":").Append(PowerEstimateJson(v.PowerEstimate))
                 .Append(",\"converters\":[");
                for (int j = 0; j < v.Converters.Count; j++)
                {
                    if (j > 0) b.Append(',');
                    ColonyConverter c = v.Converters[j];
                    b.Append("{\"partName\":").Append(Q(c.PartName)).Append(",\"recipe\":").Append(Q(c.Recipe))
                     .Append(",\"running\":").Append(B(c.Running)).Append(",\"inputs\":").Append(StringArray(c.Inputs))
                     .Append(",\"outputs\":").Append(StringArray(c.Outputs)).Append('}');
                }
                b.Append("],\"production\":").Append(ProductionTelemetryJson(omitProduction&&v.Production!=null?OmitAllProduction(v.Production,s):v.Production))
                    .Append(",\"lifeSupport\":").Append(LifeSupportJson(v.LifeSupport)).Append(",\"powerAverage\":").Append(PowerAverageJson(v.PowerAverage)).Append('}');
            }
            b.Append(']');
            if (s.VesselCensus != null)
            {
                b.Append(",\"vesselCensus\":{\"status\":").Append(Q(s.VesselCensus.Status))
                 .Append(",\"reason\":").Append(Q(s.VesselCensus.Reason))
                 .Append(",\"observationSequence\":").Append(s.VesselCensus.ObservationSequence.ToString(CultureInfo.InvariantCulture))
                 .Append(",\"vesselIds\":").Append(StringArray(s.VesselCensus.VesselIds)).Append('}');
            }
            if (wolfJson != null) b.Append(",\"wolf\":").Append(wolfJson);
            return b.Append('}').ToString();
        }
        private static string B(bool? value) { return !value.HasValue ? "null" : value.Value ? "true" : "false"; }
        private static string B(bool value) { return value ? "true" : "false"; }
        private static string PowerJson(ColonyPowerRate rate)
        {
            if (rate == null) return "null";
            return "{\"status\":" + Q(rate.Status) + ",\"reason\":" + Q(rate.Reason) +
                ",\"sampleUt\":" + N(rate.SampleUt) + ",\"windowSeconds\":" + N(rate.WindowSeconds) +
                ",\"generationEcPerSecond\":" + N(rate.GenerationEcPerSecond) +
                ",\"consumptionEcPerSecond\":" + N(rate.ConsumptionEcPerSecond) +
                ",\"netEcPerSecond\":" + N(rate.NetEcPerSecond) + "}";
        }
        private static string PowerEstimateJson(ColonyPowerEstimate estimate)
        {
            if (estimate == null) return "null";
            return "{\"status\":" + Q(estimate.Status) + ",\"reason\":" + Q(estimate.Reason) +
                ",\"generationEcPerSecond\":" + N(estimate.GenerationEcPerSecond) +
                ",\"consumptionEcPerSecond\":" + N(estimate.ConsumptionEcPerSecond) +
                ",\"moduleCount\":" + estimate.ModuleCount.ToString(CultureInfo.InvariantCulture) + "}";
        }
        private static string StringArray(string[] values)
        {
            StringBuilder b = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++) { if (i > 0) b.Append(','); b.Append(Q(values[i])); }
            return b.Append(']').ToString();
        }
    }
}
