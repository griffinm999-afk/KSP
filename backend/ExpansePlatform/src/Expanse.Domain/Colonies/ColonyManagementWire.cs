using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class ColonyManagementCapability
    {
        public string Kind { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Available { get; set; }
        public string Reason { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string ColonyId { get; set; } = "";
    }
    public sealed class ColonyManagementSnapshot
    {
        public string ContextKey { get; set; } = "";
        public string Status { get; set; } = "unavailable";
        public string Reason { get; set; } = "";
        public ColonyState? State { get; set; }
        public long? AvailableFunds { get; set; }
        public List<ColonyTemplate> Templates { get; set; } = new List<ColonyTemplate>();
        public List<ColonyFacility> AdoptableFacilities { get; set; } = new List<ColonyFacility>();
        public Dictionary<string,ColonySite> FacilitySites { get; set; } = new Dictionary<string,ColonySite>();
        public Dictionary<string,double> BodyRadiiMeters { get; set; } = new Dictionary<string,double>();
        public List<ColonyManagementCapability> Capabilities { get; set; } = new List<ColonyManagementCapability>();
        public List<ColonyConstructionRecoveryWitness> ConstructionRecovery { get; set; } = new List<ColonyConstructionRecoveryWitness>();
        public ColonyPeopleEnvironment People { get; set; } = new ColonyPeopleEnvironment();
        public ColonyServicesEnvironment Services { get; set; } = new ColonyServicesEnvironment();
        public List<ColonyEconomyPolicy> EconomyPolicies {get;set;}=new List<ColonyEconomyPolicy>();
        public ColonyWolfEnvironment Wolf {get;set;}=new ColonyWolfEnvironment();
        public List<string> UnlockedTech {get;set;}=new List<string>();
        public bool DevelopmentMode {get;set;}
        public double ObservedUt {get;set;}
        public ColonySupportEnvironment Support {get;set;}=new ColonySupportEnvironment();
        public ColonyPlanningEnvironment Planning {get;set;}=new ColonyPlanningEnvironment();
        public ColonyProductionEnvironment Production {get;set;}=new ColonyProductionEnvironment();
    }
    public sealed class ColonyManagementWireRequest
    {
        public int Version { get; set; } = 1;
        public string RequestId { get; set; } = "";
        public string Kind { get; set; } = "snapshot";
        public ColonyCommand? Command { get; set; }
        public SettlementReadRequest? Settlements { get; set; }
    }
    public sealed class ColonyManagementWireResponse
    {
        public int Version { get; set; } = 1;
        public string RequestId { get; set; } = "";
        public string Outcome { get; set; } = "unavailable";
        public string Reason { get; set; } = "";
        public ColonyManagementSnapshot? Snapshot { get; set; }
        public ColonyResult? Result { get; set; }
        public SettlementPage? Settlements { get; set; }
    }
    public static class ColonyManagementWire
    {
        public const int MaxFrameBytes = 8 * 1024 * 1024; // 4 MiB saved state plus bounded catalog/qualifications
        public const int MaxQueuedRequests = 32;
        public static string PipeForInstallation(string installationRoot)
        {
            if (string.IsNullOrWhiteSpace(installationRoot) || !Path.IsPathRooted(installationRoot)) throw new ArgumentException("An absolute KSP installation path is required.");
            string canonical = Path.GetFullPath(installationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (var sha = SHA256.Create()) return "ExpanseFoundations.Colonies." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
        }
        public static void ValidatePipeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0) throw new ArgumentException("Invalid local colony pipe name.");
        }
        public static byte[] Encode(ColonyManagementWireRequest request) { Validate(request); return Serialize(request); }
        public static byte[] Encode(ColonyManagementWireResponse response) { Validate(response); return Serialize(response); }
        public static ColonyManagementWireRequest DecodeRequest(byte[] bytes) { var value = Deserialize<ColonyManagementWireRequest>(bytes); Validate(value); return value; }
        public static ColonyManagementWireResponse DecodeResponse(byte[] bytes) { var value = Deserialize<ColonyManagementWireResponse>(bytes); Validate(value); return value; }
        static byte[] Serialize<T>(T value)
        {
            return ColonyJson.Serialize(value, MaxFrameBytes);
        }
        static T Deserialize<T>(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxFrameBytes) throw new InvalidDataException("Invalid colony management frame length.");
            return ColonyJson.Deserialize<T>(bytes, MaxFrameBytes);
        }
        public static void Validate(ColonyManagementWireRequest request)
        {
            if (request == null || request.Version != 1 || !Guid.TryParse(request.RequestId, out _)) throw new InvalidDataException("Invalid colony management request envelope.");
            if (request.Kind != "settlements" && request.Settlements != null) throw new InvalidDataException("Unexpected settlement page request.");
            if (request.Kind == "snapshot") { if (request.Command != null) throw new InvalidDataException("Snapshot request contains a command."); }
            else if (request.Kind == "settlements") { if(request.Command!=null || request.Settlements==null)throw new InvalidDataException("Settlement reads require a page request and no command."); SettlementJournal.Validate(request.Settlements); }
            else if (request.Kind == "submit")
            {
                if (request.Command == null || !Guid.TryParse(request.Command.OperationId, out _)) throw new InvalidDataException("Mutation requires a GUID operation ID.");
                ColonyStateCodec.CommandHash(request.Command);
            }
            else throw new InvalidDataException("Unknown colony management request kind.");
        }
        public static void Validate(ColonyManagementWireResponse response)
        {
            if (response == null || response.Version != 1 || !Guid.TryParse(response.RequestId, out _)) throw new InvalidDataException("Invalid colony management response envelope.");
            Text(response.Outcome, 64); Text(response.Reason, 4096);
            if(response.Settlements!=null)SettlementJournal.Validate(response.Settlements);
            if (response.Result != null) { Text(response.Result.Outcome,64); Text(response.Result.Reason,4096); Text(response.Result.ResultId,128); if (response.Result.State != null) ColonyStateCodec.Validate(response.Result.State); else if (response.Result.Outcome == "accepted" || response.Result.Outcome == "duplicate") throw new InvalidDataException("Accepted colony result lacks authoritative state."); }
            if (response.Snapshot != null)
            {
                var s = response.Snapshot; Text(s.ContextKey,256); Text(s.Status,64); Text(s.Reason,4096);
                ColonyStateCodec.Time(s.ObservedUt);
                if(s.Planning==null)s.Planning=new ColonyPlanningEnvironment();
                ColonyStateCodec.ValidatePlanningEnvironment(s.Planning);
                if(s.Production==null)s.Production=new ColonyProductionEnvironment();
                ColonyStateCodec.ValidateProductionEnvironment(s.Production);
                if(s.Support==null)s.Support=new ColonySupportEnvironment();Text(s.Support.PolicyId,128);Text(s.Support.PolicyHash,128);Text(s.Support.Reason,4096);ColonyStateCodec.Quantity(s.Support.MicroUnitsPerPersonDay);
                if (s.State != null) ColonyStateCodec.Validate(s.State);
                if (s.AvailableFunds.HasValue) ColonyStateCodec.Funds(s.AvailableFunds.Value);
                if (s.Templates == null || s.Templates.Count > 256 || s.AdoptableFacilities == null || s.AdoptableFacilities.Count > ColonyLimits.Facilities || s.Capabilities == null || s.Capabilities.Count > 1024) throw new InvalidDataException("Colony capability/catalog limits exceeded.");
                if (s.FacilitySites == null || s.FacilitySites.Count>ColonyLimits.Facilities) throw new InvalidDataException("Colony survey limit exceeded.");
                foreach (var pair in s.FacilitySites) { ColonyStateCodec.Id(pair.Key); ColonyStateCodec.Site(pair.Value); }
                if (s.BodyRadiiMeters==null || s.BodyRadiiMeters.Count>256)throw new InvalidDataException("Body catalog limit exceeded.");
                foreach(var pair in s.BodyRadiiMeters) {Text(pair.Key,128);if(double.IsNaN(pair.Value) || double.IsInfinity(pair.Value) || pair.Value<=0)throw new InvalidDataException("Invalid surveyed body radius.");}
                foreach (var capability in s.Capabilities) { if (capability == null) throw new InvalidDataException("Missing colony capability."); Text(capability.Kind,64); Text(capability.Label,160); Text(capability.Reason,4096); Text(capability.TargetId,128); if (capability.ColonyId != null) Text(capability.ColonyId,64); }
                if (s.People == null) s.People = new ColonyPeopleEnvironment(); // additive older snapshot has no people provider
                ValidatePeopleCatalog(s.People);
                if(s.Services==null)s.Services=new ColonyServicesEnvironment();
                ValidateServicesCatalog(s.Services);
                if(s.EconomyPolicies==null || s.EconomyPolicies.Count>16 || s.UnlockedTech==null || s.UnlockedTech.Count>512)throw new InvalidDataException("Economy/technology catalog limits exceeded.");
                foreach(var policy in s.EconomyPolicies)ColonyStateCodec.ValidateEconomyPolicy(policy);
                foreach(var tech in s.UnlockedTech)Text(tech,128);
                if(s.Wolf==null)s.Wolf=new ColonyWolfEnvironment();
                Text(s.Wolf.Provider,128);Text(s.Wolf.Reason,4096);
                if(s.Wolf.Recipes==null || s.Wolf.Recipes.Count>256 || s.Wolf.Sites==null || s.Wolf.Sites.Count>ColonyLimits.Colonies)throw new InvalidDataException("WOLF catalog limits exceeded.");
                if(s.Wolf.Ready)ColonyStateCodec.ValidateWolfRecipe(s.Wolf.DepotConstruction,true);
                foreach(var recipe in s.Wolf.Recipes)ColonyStateCodec.ValidateWolfRecipe(recipe);
                foreach(var site in s.Wolf.Sites){Text(site.ColonyId,64);Text(site.ContextKey,256);Text(site.SurveyConfigurationHash,128);Text(site.Reason,4096);ColonyStateCodec.Time(site.ObservedUt);ColonyStateCodec.ValidateWolfDepot(site.Depot);ColonyStateCodec.WolfIngredients(site.SurveyVeins);}
                foreach (var template in s.Templates)
                {
                    if (template==null) throw new InvalidDataException("Missing package.");
                    Text(template.Id,128);Text(template.Name,160);Text(template.Hash,128);Text(template.CraftRelativePath,512);Text(template.CertificationEvidence,4096);
                    ColonyStateCodec.Funds(template.BuildFunds);ColonyStateCodec.Materials(template.Materials);ColonyStateCodec.Materials(template.EmbeddedContents);
                    if (template.RequiredTech==null || template.RequiredTech.Count>256 || template.RequiredPartNames==null || template.RequiredPartNames.Count>512) throw new InvalidDataException("Package field limit exceeded.");
                    foreach (var tech in template.RequiredTech) Text(tech,128);foreach(var part in template.RequiredPartNames)Text(part,128);
                }
                foreach (var facility in s.AdoptableFacilities)
                {
                    if (facility==null || facility.Qualification==null || facility.PartIds==null || facility.PartIds.Count>512) throw new InvalidDataException("Invalid adoption record.");
                    ColonyStateCodec.Id(facility.Id);ColonyStateCodec.Id(facility.VesselId);Text(facility.Name,160);Text(facility.TemplateId,128);Text(facility.TemplateHash,128);Text(facility.PlotId,64);Text(facility.State,64);Text(facility.ProductionOwner,64);Text(facility.RequiredTrait,128);Text(facility.LastReason,512);
                    Text(facility.Qualification.Provider,128);Text(facility.Qualification.Context,64);Text(facility.Qualification.EvidenceHash,128);ColonyStateCodec.Time(facility.Qualification.ObservedUt);
                }
            }
        }
        static void ValidateServicesCatalog(ColonyServicesEnvironment services)
        {
            if(services.Targets==null || services.Targets.Count>1024 || services.Utilities==null || services.Utilities.Count>ColonyLimits.Facilities)throw new InvalidDataException("Service observation limits exceeded.");
            foreach(var t in services.Targets)
            {
                if(t==null)throw new InvalidDataException("Missing service target.");
                Text(t.ColonyId,64);Text(t.FacilityId,64);Text(t.DepotId,128);Text(t.PartName,160);Text(t.SourceResource,128);Text(t.DestinationResource,128);Text(t.QuoteHash,128);Text(t.ContextKey,256);Text(t.Provider,128);Text(t.WorkerWitness,1024);Text(t.Reason,4096);
                ColonyStateCodec.Quantity(t.Amount);ColonyStateCodec.Quantity(t.Capacity);ColonyStateCodec.Time(t.ObservedUt);
                if(t.Amount>t.Capacity || t.PartId==0)throw new InvalidDataException("Invalid installed service tank.");
            }
            foreach(var u in services.Utilities)
            {
                if(u==null)throw new InvalidDataException("Missing utility observation.");
                Text(u.FacilityId,64);Text(u.VesselId,64);Text(u.ContextKey,256);Text(u.Context,64);Text(u.DistributionWitness,4096);Text(u.Evidence,4096);Text(u.Reason,4096);ColonyStateCodec.Time(u.ObservedUt);
                foreach(var number in new[]{u.ElectricCharge,u.ConnectedCapacity,u.NetStorageEcPerSecond,u.WindowSeconds,u.NominalGenerationEcPerSecond,u.NominalDemandEcPerSecond,u.MaximumPartTemperature,u.MinimumTemperatureMargin,u.MaximumCoreTemperature,u.MinimumCoreShutdownMargin,u.ContinuousFuelEnduranceSeconds})
                    if(number.HasValue && (double.IsNaN(number.Value) || double.IsInfinity(number.Value)))throw new InvalidDataException("Nonfinite utility observation.");
            }
        }
        static void ValidatePeopleCatalog(ColonyPeopleEnvironment people)
        {
            if (people.Roster == null || people.Roster.Count > 2048 || people.Seats == null || people.Seats.Count > 4096 || people.Routes == null || people.Routes.Count > 128 || people.PresentByColony == null || people.PresentByColony.Count > ColonyLimits.Colonies) throw new InvalidDataException("People observation limits exceeded.");
            foreach (var p in people.Roster) { if (p == null) throw new InvalidDataException("Missing roster witness."); Text(p.RosterId,160); Text(p.Name,160); Text(p.Trait,128); Text(p.Type,64); Text(p.Status,64); Text(p.VesselId,64); Text(p.ContextKey,256); ColonyStateCodec.Time(p.ObservedUt); }
            foreach (var s in people.Seats) { if (s == null || s.Capacity < 0 || s.Capacity > 512 || s.Occupants == null || s.Occupants.Count > 512) throw new InvalidDataException("Invalid seat observation."); Text(s.FacilityId,64); Text(s.VesselId,64); Text(s.ContextKey,256); Text(s.Evidence,512);Text(s.Body,128);ColonyStateCodec.Range(s.Latitude,-90,90);ColonyStateCodec.Range(s.Longitude,-180,180); foreach (string name in s.Occupants) Text(name,160); }
            foreach (var r in people.Routes) { if (r == null) throw new InvalidDataException("Missing passenger route."); Text(r.Id,128); Text(r.Name,160); Text(r.Body,128); Text(r.Hash,64); Text(r.Evidence,512); ColonyStateCodec.Funds(r.Fare); ColonyStateCodec.Funds(r.RecruitmentFee); ColonyStateCodec.Range(r.TravelSeconds,1,1e9); ColonyStateCodec.Range(r.ConcurrentSeats,1,256); }
            foreach (var pair in people.PresentByColony) { ColonyStateCodec.Id(pair.Key); if (pair.Value == null || pair.Value.Count > ColonyLimits.Residents) throw new InvalidDataException("Site population witness limit exceeded."); foreach (string name in pair.Value) Text(name,160); }
        }
        static void Text(string value, int max) { if (value == null || value.Length > max || value.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid colony wire text."); }
    }
}
