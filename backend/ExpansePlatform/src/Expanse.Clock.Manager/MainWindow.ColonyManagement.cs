using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

public partial class MainWindow
{
    private ColonyManagementClient? colonyManagementClient;
    private readonly DispatcherTimer colonyManagementTimer = new() { Interval=TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource colonyManagementStop = new();
    private bool colonyManagementPolling;
    private string? managedColonyId;
    private ColonyManagementSnapshot? managementSnapshot;
    private bool managementSnapshotCurrent;
    private string? managementQuoteContext,managementQuoteId,managementQuoteReview,managementQuoteKind;
    private string? managementQuoteColonyId;
    private string? managementQuoteTargetId;
    private long managementQuoteRevision;
    private Dictionary<string,string>? managementQuoteFields;
    private readonly ColonyManagementAdoptionOperations managementAdoptions=new();
    private readonly ColonyManagementCharterOperations managementCharters=new();

    private void InitializeColonyManagement(string? colonyPipe, bool isolatedClockPipes)
    {
        // Historical development callers that pass only isolated clock pipes must
        // never accidentally attach a new operational colony client to live KSP.
        if (colonyPipe is null && isolatedClockPipes)
        {
            ColonyManagementControl.ApplySnapshot(ColonyManagementPresentation.Disconnected("Development clock pipes selected; pass --colony-pipe for a verified isolated colony endpoint."));
            return;
        }
        colonyManagementClient = new ColonyManagementClient(colonyPipe ?? ColonyManagementWire.PipeForInstallation(@"C:\Kerbal Space Program"));
        ColonyManagementControl.SubmitAsync = SubmitColonyManagementAsync;
        ColonyManagementControl.ColonySelectionRequested += id => { managedColonyId=id; RenderColonyManagement(); };
        ColonyManagementControl.FacilitySiteSelectionRequested += id =>
        {
            var current=managementSnapshot;
            if(current?.State is null || managedColonyId is not (null or "__new"))return;
            var candidate=current.AdoptableFacilities.FirstOrDefault(f=>f.Id==id &&
                !current.State.Colonies.Any(c=>c.Facilities.Any(member=>member.Id==f.Id || member.VesselId==f.VesselId)));
            ColonyManagementControl.SetFoundingSite(candidate is not null && current.FacilitySites.TryGetValue(id,out var site) ? site : null,current.ContextKey);
        };
        colonyManagementTimer.Tick += async (_,_)=>await PollColonyManagementAsync();
        Loaded += async (_,_)=> { await PollColonyManagementAsync(); colonyManagementTimer.Start(); };
        Closed += (_,_)=> { colonyManagementTimer.Stop(); colonyManagementStop.Cancel(); };
    }
    private async Task PollColonyManagementAsync()
    {
        if (colonyManagementPolling || colonyManagementClient is null || colonyManagementStop.IsCancellationRequested) return;
        colonyManagementPolling=true;
        try
        {
            var response = await colonyManagementClient.SnapshotAsync(colonyManagementStop.Token);
            if (response.Snapshot is null) throw new InvalidOperationException(response.Reason);
            if (managementSnapshot?.ContextKey != response.Snapshot.ContextKey) managedColonyId = response.Snapshot.State?.Colonies.FirstOrDefault()?.Id;
            managementSnapshot = response.Snapshot;
            managementSnapshotCurrent = true;
            RenderColonyManagement();
        }
        catch (Exception) when (!colonyManagementStop.IsCancellationRequested)
        {
            managementSnapshotCurrent = false;
            ColonyManagementControl.ApplySnapshot(ColonyManagementPresentation.Disconnected("KSP colony authority is unavailable. Load the selected save in the verified installation; existing clock and delivery views retain their separate connection."));
        }
        finally { colonyManagementPolling=false; }
    }
    private void RenderColonyManagement()
    {
        var value=managementSnapshot;
        if (value?.State is null)
        {
            ColonyManagementControl.ApplySnapshot(ColonyManagementPresentation.Disconnected(value?.Reason ?? "No save-owned colony state is available.")); return;
        }
        var capabilities=value.Capabilities.Where(c=>string.IsNullOrEmpty(c.ColonyId) || c.ColonyId==managedColonyId).Select(c=>new ColonyManagementAction(c.Kind,c.Label,c.Available,c.Reason,c.TargetId.Length==0 ? null : c.TargetId)).ToArray();
        var reviewedCapabilities=new List<ColonyManagementAction>(ColonyManagementAdoption.ReviewedActions(capabilities));
        if(managedColonyId is not (null or "__new"))foreach(var action in capabilities.Where(c=>c.Kind=="updateCharter"))
            reviewedCapabilities.Add(new("reviewCharter","Review charter update",action.Available,action.Reason));
        if(capabilities.Any(c=>c.Kind=="transferColonyStock"))reviewedCapabilities.Add(new("reviewPhysicalTransfer","Review physical stock transfer",true,"Checks exact tank, owned reserve, access and conserved before/after amounts."));
        if(capabilities.Any(c=>c.Kind=="approveFoundingPlan"))reviewedCapabilities.Add(new("reviewFoundingPlan","Review step 2: startup bill",true,"Itemizes packages, adopted roles, paid reserve imports, plots and the downside forecast."));
        if(capabilities.Any(c=>c.Kind=="approveGrowthPlan"))reviewedCapabilities.Add(new("reviewGrowthPlan","Review housing expansion",true,"Checks actual housing pressure, open jobs, support and shared cash before expansion."));
        foreach(var proposal in capabilities.Where(c=>c.Kind=="approveGrowthProposal"))reviewedCapabilities.Add(new("reviewGrowthProposal","Review this expansion bill",true,"Recalculates the exact selected review's itemized funded plan before approval.",proposal.TargetId));
        if (managedColonyId is null or "__new" && capabilities.Any(c=>c.Kind=="foundColony")) reviewedCapabilities.Add(new("reviewFounding","Review step 1: charter and site",true,"Reviews the editable charter and adoption scope without committing funds."));
        if (capabilities.Any(c=>c.Kind=="approveConstruction")) reviewedCapabilities.Add(new("reviewConstruction","Review construction quote",true,"Uses the selected snapshot package. Commitment rechecks the current catalog and resources."));
        if (capabilities.Any(c=>c.Kind=="approveTrade")) reviewedCapabilities.Add(new("reviewTrade","Review import quote",true,"Uses saved supplier terms. Commitment rechecks cash, supplier stock and freight."));
        if(capabilities.Any(c=>c.Kind=="transferColonyWorker"))reviewedCapabilities.Add(new("reviewWorkerTransfer","Review worker cabin transfer",true,"Reviews actual nearby source and vacant workplace; visitor/resident status and certified home remain unchanged."));
        if (capabilities.Any(c=>c.Kind=="recruitResident")) reviewedCapabilities.Add(new("reviewRecruitment","Review passenger recruitment",true,"Reviews named actual roster identity and configured route fare; no crew is created by review."));
        foreach(var departure in capabilities.Where(c=>c.Kind=="departResident")) reviewedCapabilities.Add(new("reviewDeparture","Review departure fare",true,"Reviews current finite passenger route terms; the actual Kerbal remains protected.",departure.TargetId));
        if(capabilities.Any(c=>c.Kind=="serviceFacility"))reviewedCapabilities.Add(new("reviewService","Review installed tank service",true,"Shows the real owned-stock debit, destination and actual qualified worker witness."));
        if(capabilities.Any(c=>c.Kind=="activateLogistics"))reviewedCapabilities.Add(new("reviewLogistics","Review paid staging contract",true,"Reviews setup cost, finite empty capacity, contractor terms and paid freight supply."));
        if(capabilities.Any(c=>c.Kind=="commissionSupport"))reviewedCapabilities.Add(new("reviewSupport","Review resident support ownership",true,"Computes current population/charter reserve and verifies one installed life-support owner."));
        if(capabilities.Any(c=>c.Kind=="approveWolfSupply"))reviewedCapabilities.Add(new("reviewWolfSupply","Review costed WOLF supply",true,"Computes actual installed module dependencies against the current depot witness."));
        foreach(var replan in capabilities.Where(c=>c.Kind=="replanPaidWolfSupply"))reviewedCapabilities.Add(new("reviewWolfReplan","Review same paid WOLF package",true,"Reuses the paid modules against a current depot snapshot; no new funds or fabrication are granted.",replan.TargetId));
        var view=ColonyManagementAdapter.Present(value.State,value.ContextKey,managedColonyId,value.Status,value.Reason,
            reviewedCapabilities.ToArray(),value.Templates.ToArray(),_lastColony,value.AvailableFunds,
            observationsCurrent:false,bodyRadiiMeters:value.BodyRadiiMeters,peopleEnvironment:value.People,servicesEnvironment:value.Services,economyPolicies:value.EconomyPolicies.ToArray(),wolfEnvironment:value.Wolf);
        view=PresentPhysicalStock(view,value,reviewedCapabilities);
        view=PresentFoundingResidents(view,value);
        view=PresentProduction(view,value);
        view=ColonyManagementAdoption.Present(view,value);
        if (managementQuoteContext==value.ContextKey && managementQuoteRevision==value.State.Revision && managementQuoteColonyId==(managedColonyId=="__new" ? null : managedColonyId))
        {
            bool currentAdoption=managementQuoteKind is not ("adoptFacility" or "qualifyAdoptedHabitat") || managementQuoteFields is not null &&
                ColonyManagementAdoption.MatchesReview(value,new("",value.ContextKey,view.ColonyId,value.State.Revision,managementQuoteKind!,managementQuoteTargetId,managementQuoteId,managementQuoteFields));
            if(currentAdoption)view=view with {QuoteId=managementQuoteId,QuoteReview=managementQuoteReview,QuoteKind=managementQuoteKind,QuoteTargetId=managementQuoteTargetId};
            if(managementPlanningQuote is not null)view=PresentPlanningReview(view,managementPlanningQuote,managementQuoteTargetId);
        }
        ColonyManagementControl.ApplySnapshot(view);
    }
    private async Task<ColonyManagementResponse> SubmitColonyManagementAsync(ColonyManagementRequest request)
    {
        if (colonyManagementClient is null) return new(false,"Colony command connection unavailable.");
        if (request.Kind is "reviewCharter" or "reviewAdoption" or "reviewAdoptedHabitat" or "reviewGrowthProposal" or "reviewWorkerTransfer" or "reviewPhysicalTransfer" or "reviewFounding" or "reviewFoundingPlan" or "reviewGrowthPlan" or "reviewConstruction" or "reviewTrade" or "reviewRecruitment" or "reviewDeparture" or "reviewService" or "reviewLogistics" or "reviewWolfSupply" or "reviewWolfReplan" or "reviewSupport") return ReviewColonyPlan(request);
        var command=ColonyManagementAdapter.Command(request);
        if(request.Kind=="retryConstructionPlacement")
        {
            var order=managementSnapshot?.State?.Construction.SingleOrDefault(o=>o.Id==request.TargetId && o.ColonyId==request.ColonyId);
            if(order is null || managementSnapshot!.ContextKey!=request.ContextKey)return new(true,"The original paid placement is unavailable in this context; refresh before retrying.");
            command.Fields.Clear();
            command.Fields["PlacementOperationId"]=order.Placement.OperationId;
            command.Fields["RequestFingerprint"]=order.Placement.RequestFingerprint;
            command.Fields["PayloadHash"]=order.Placement.PayloadHash;
        }
        bool reconcileAdoption=request.Kind is "adoptFacility" or "qualifyAdoptedHabitat" && managementAdoptions.ContainsExact(request);
        bool reconcileCharter=request.Kind=="updateCharter" && managementCharters.ContainsExact(request);
        if (!reconcileAdoption && !reconcileCharter && request.Kind is "updateCharter" or "adoptFacility" or "qualifyAdoptedHabitat" or "approveGrowthProposal" or "transferColonyWorker" or "transferColonyStock" or "foundColony" or "approveFoundingPlan" or "approveGrowthPlan" or "approveConstruction" or "approveTrade" or "recruitResident" or "departResident" or "serviceFacility" or "activateLogistics" or "approveWolfSupply" or "replanPaidWolfSupply" or "commissionSupport")
        {
            if (request.QuoteId!=managementQuoteId || managementQuoteFields is null || managementQuoteContext!=request.ContextKey || managementQuoteRevision!=request.ExpectedRevision || managementQuoteKind!=request.Kind || managementQuoteColonyId!=request.ColonyId || managementQuoteTargetId!=request.TargetId)
                return new(true,"The reviewed quote changed. Refresh and review the plan before commitment.");
            if(request.Kind=="updateCharter" && !ColonyManagementCharter.MatchesEditableFields(command.Fields,managementQuoteFields))return new(true,"The charter draft changed. Review its current terms again.");
            foreach (var pair in managementQuoteFields) command.Fields[pair.Key]=pair.Value;
        }
        if(request.Kind is "adoptFacility" or "qualifyAdoptedHabitat")
        {
            if(managementSnapshot is null)return new(false,"The adoption authority snapshot is unavailable; reconcile the same pending operation when connected.");
            try {command=managementAdoptions.Prepare(managementSnapshot,request,managementQuoteFields ?? new Dictionary<string,string>());}
            catch(InvalidOperationException ex){return new(!reconcileAdoption,ex.Message);}
        }
        if(request.Kind=="updateCharter")
        {
            if(managementSnapshot is null)return new(false,"The charter authority snapshot is unavailable; reconnect to reconcile the pending operation.");
            try {command=managementCharters.Prepare(managementSnapshot,request,managementQuoteFields);}
            catch(InvalidOperationException ex){return new(!reconcileCharter,ex.Message);}
        }
        if(request.Kind is "approveGrowthProposal" or "approveFoundingPlan" or "approveGrowthPlan" or "surveyFoundingPlan" or "surveyGrowthPlan")command.Fields.Clear();
        if(request.Kind=="approveFoundingPlan")command.FoundingIntent=managementPlanningQuote?.FoundingIntent;
        if(request.Kind=="transferColonyWorker")
        {
            command.TargetId=command.Fields["RosterId"];var workplace=command.Fields["JobFacilityId"];var part=command.Fields["WorkPartId"];var consent=command.Fields.TryGetValue("ExplicitMissionCrewAssignment",out var explicitConsent) ? explicitConsent : "false";
            command.Fields.Clear();command.Fields["JobFacilityId"]=workplace;command.Fields["WorkPartId"]=part;command.Fields["ExplicitMissionCrewAssignment"]=consent;
        }
        if(request.Kind=="transferColonyStock")
        {
            command.TargetId=command.Fields["LocalStockId"];var direction=command.Fields["Direction"];var amount=command.Fields["AmountMicroUnits"];
            command.Fields.Clear();command.Fields["Direction"]=direction;command.Fields["AmountMicroUnits"]=amount;
        }
        if(request.Kind=="configurePhysicalProcurement")
        {
            if(!command.Fields.TryGetValue("PhysicalPolicyEnabled",out var enabled) || enabled is not ("true" or "false"))return new(true,"Choose whether automatic local procurement is enabled.");
            command.Fields.Clear();command.Fields["Enabled"]=enabled;
        }
        if(request.Kind=="configureReorderPolicy")
        {
            foreach(var key in new[]{"ReorderPoint","TargetAmount"})
            {
                if(!command.Fields.TryGetValue(key,out var raw) || !TryResourceAmount(raw,out var amount))return new(true,"Enter bounded reserve amounts with at most six decimal places.");
                command.Fields[key=="ReorderPoint" ? "ReorderPointMicroUnits" : "TargetMicroUnits"]=amount.ToString(CultureInfo.InvariantCulture);
            }
        }
        if(request.Kind=="surveyPlot")
        {
            var package=managementSnapshot?.Templates.SingleOrDefault(t=>command.Fields.TryGetValue("TemplateId",out var id) && t.Id==id);
            if(package is null)return new(true,"Select a current installed package before surveying.");command.Fields["TemplateHash"]=package.Hash;
        }
        var response=await colonyManagementClient.SubmitAsync(command,colonyManagementStop.Token);
        bool terminal=response.Outcome is "accepted" or "duplicate" or "rejected" or "notStarted";
        if(command.Kind is "adoptFacility" or "qualifyAdoptedHabitat" && terminal)managementAdoptions.Complete(request.OperationId);
        if(command.Kind=="updateCharter" && terminal)managementCharters.Complete(request.OperationId);
        if (response.Result is { State: { } state } && managementSnapshot is not null && managementSnapshot.ContextKey==request.ContextKey)
        {
            managementSnapshot.State=state;
            bool charterRegistered=command.Kind=="foundColony" && response.Outcome is "accepted" or "duplicate";
            if (charterRegistered) managedColonyId=response.Result.ResultId;
            RenderColonyManagement();
            if(charterRegistered && managedColonyId is not null && state.Colonies.Any(c=>c.Id==managedColonyId))
                ColonyManagementControl.ShowExistingBase();
            if(command.Kind=="approveFoundingPlan" && managedColonyId==command.ColonyId && response.Outcome is "accepted" or "duplicate")
                ColonyManagementControl.ShowOverview();
        }
        await PollColonyManagementAsync();
        string message=response.Outcome is "accepted" or "duplicate" ? command.Kind switch
        {
            "foundColony"=>"Charter registered. Review the existing facilities and their qualification before choosing any separate purchase or resident task.",
            "qualifyAdoptedHabitat"=>"Existing habitat homes qualified. Purchase owned Supplies and commission resident support before admitting residents.",
            "approveFoundingPlan"=>"Startup plan approved. Overview now shows current progress and any holds. Save in KSP to persist.",
            _=>response.Reason.Length==0 ? response.Outcome+" · "+response.Result?.ResultId : response.Reason
        } : response.Reason.Length==0 ? response.Outcome+" · "+response.Result?.ResultId : response.Reason;
        return new(terminal,message,response.Outcome is not ("accepted" or "duplicate"));
    }
    private ColonyManagementResponse ReviewColonyPlan(ColonyManagementRequest request)
    {
        try
        {
            var snapshot=managementSnapshot ?? throw new InvalidOperationException("No current colony authority snapshot.");
            if (snapshot.State is null || snapshot.ContextKey!=request.ContextKey || snapshot.State.Revision!=request.ExpectedRevision) throw new InvalidOperationException("The save changed; refresh before review.");
            var command=ColonyManagementAdapter.Command(request);
            var fields=command.Fields;
            string Field(string key) => fields.TryGetValue(key,out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new InvalidOperationException("Enter "+key+" before review.");
            long Integer(string key) => long.TryParse(Field(key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var number) ? number : throw new InvalidOperationException(key+" requires a whole number.");
            double Number(string key) => double.TryParse(Field(key),NumberStyles.Float,CultureInfo.InvariantCulture,out var number) && double.IsFinite(number) ? number : throw new InvalidOperationException(key+" requires a finite number.");
            string review,kind;string? backendQuoteId=null;
            ColonyPlanningQuote? reviewedPlanning=null;
            if(request.Kind=="reviewCharter")
            {
                var charterReview=ColonyManagementCharter.Review(snapshot,request);
                backendQuoteId=charterReview.Id;kind="updateCharter";review=charterReview.Description;
                fields.Clear();foreach(var pair in charterReview.Fields)fields[pair.Key]=pair.Value;
            }
            else if(request.Kind=="reviewAdoptedHabitat")
            {
                var quote=ColonyManagementAdoption.Review(snapshot,request);
                backendQuoteId=quote.Id;kind="qualifyAdoptedHabitat";fields.Clear();fields["HabitatWitnessHash"]=quote.AdoptionWitnessHash;
                review=quote.Name+" · "+quote.PartIds.Count+" actual dedicated four-seat cabins ("+(quote.PartIds.Count*4)+" home seats). "+quote.Reason+" Current native membership, deployment, location, utilities and installed support ownership are rechecked on approval.";
            }
            else if(request.Kind=="reviewAdoption")
            {
                var quote=ColonyManagementAdoption.Review(snapshot,request);
                backendQuoteId=quote.Id;kind="adoptFacility";fields.Clear();fields["AdoptionWitnessHash"]=quote.AdoptionWitnessHash;
                var colony=snapshot.State.Colonies.Single(c=>c.Id==quote.ColonyId);
                review="Adopt "+quote.Name+" into "+colony.Name+". Site "+quote.Site.Body+" / "+quote.Site.Biome+" at "+quote.Site.Latitude.ToString("0.00000")+", "+quote.Site.Longitude.ToString("0.00000")+"; "+quote.PartIds.Count+" exact observed member parts. "+quote.PresentPeople.Count+" present people; "+quote.NewVisitorCount+" visitors awaiting normal population reconciliation; "+quote.VisitorCount+" total observed/recorded visitors. "+quote.Reason+" This reviewed operation registers this exact existing physical hardware. It grants no owned stock, certified home, worker bonus, resident conversion or construction. Vessel "+quote.VesselId+"; facility "+quote.FacilityId+". Fresh runtime inspection rechecks identity, members, site, ownership and people before acceptance.";
            }
            else if(request.Kind=="reviewWorkerTransfer")
            {
                var quote=ColonyEngine.QuoteWorkerTransfer(snapshot.State,request.ColonyId ?? "",Field("RosterId"),Field("JobFacilityId"),checked((uint)Integer("WorkPartId")),PlanningEnvironment(snapshot),fields.TryGetValue("ExplicitMissionCrewAssignment",out var consent) && consent=="true");
                if(!quote.CanApprove)throw new InvalidOperationException(quote.Reason);backendQuoteId=quote.Id;kind="transferColonyWorker";
                var person=snapshot.People.Roster.Single(p=>p.RosterId==quote.RosterId);var facility=snapshot.State.Colonies.Single(c=>c.Id==request.ColonyId).Facilities.Single(f=>f.Id==quote.JobFacilityId);
                review=person.Name+" · "+person.Trait+" → "+facility.Name+" workplace · "+quote.DistanceMeters.ToString("N1")+" metres. Source occupants "+quote.SourceOccupants.Count+" → "+(quote.SourceOccupants.Count-1)+"; destination "+quote.DestinationOccupants.Count+" → "+(quote.DestinationOccupants.Count+1)+". "+quote.Reason+" Actual loaded crew mappings are read back on both sides. Unknown partial transfers remain held.";
            }
            else if(request.Kind=="reviewPhysicalTransfer")
            {
                var quote=ReviewPhysicalTransfer(snapshot,request,fields);backendQuoteId=quote.Id;kind="transferColonyStock";
                var tank=snapshot.Planning.LocalStocks.Single(s=>s.Id==quote.LocalStockId);
                string Units(long amount)=>(amount/(decimal)ColonyLimits.Units).ToString("0.######");
                review=tank.PartName+" · "+quote.Resource+" · "+(quote.Direction=="toColony" ? "physical tank → colony reserves" : "colony reserves → physical tank")+". Exact amount "+Units(quote.Amount)+" resource units. Physical tank "+Units(quote.PhysicalBefore)+" → "+Units(quote.PhysicalAfter)+"; owned reserve "+Units(quote.OwnedBefore)+" → "+Units(quote.OwnedAfter)+". "+quote.Reason+" Provider "+quote.Provider+". "+quote.Provenance+". Commitment rechecks current exact tank and conserves the same amount; uncertain physical effects retain a durable hold.";
            }
            else if(request.Kind=="reviewGrowthProposal")
            {
                reviewedPlanning=ColonyEngine.QuoteGrowthProposal(snapshot.State,request.ColonyId ?? "",request.TargetId ?? "",PlanningEnvironment(snapshot));
                backendQuoteId=reviewedPlanning.Id;review=PlanningReview(reviewedPlanning);kind="approveGrowthProposal";fields.Clear();
            }
            else if(request.Kind is "reviewFoundingPlan" or "reviewGrowthPlan")
            {
                var env=PlanningEnvironment(snapshot);
                reviewedPlanning=request.Kind=="reviewFoundingPlan" ? ColonyEngine.QuoteFoundingPlan(snapshot.State,request.ColonyId ?? "",env,request.FoundingIntent) : ColonyEngine.QuoteGrowthPlan(snapshot.State,request.ColonyId ?? "",env);
                backendQuoteId=reviewedPlanning.Id;review=PlanningReview(reviewedPlanning);kind=request.Kind=="reviewFoundingPlan" ? "approveFoundingPlan" : "approveGrowthPlan";fields.Clear();
            }
            else if (request.Kind=="reviewFounding")
            {
                string name=Field("Name"); if(name.Length>160)throw new InvalidOperationException("Colony name is too long.");
                var site=new ColonySite {Body=Field("Body"),Biome=fields.TryGetValue("Biome",out var biome) ? biome : "",Latitude=Number("Latitude"),Longitude=Number("Longitude")};
                var charter=new ColonyCharter {Purpose=Field("Purpose"),PopulationTarget=checked((int)Integer("Population")),ResidentLimit=checked((int)Integer("ResidentLimit")),VisitorLimit=checked((int)Integer("VisitorLimit")),FoundingBudget=Integer("Budget"),CashFloor=Integer("CashFloor"),SpendingLimit=Integer("SpendingLimit"),ReserveDays=Number("ReserveDays"),GrowthPolicy=Field("GrowthPolicy")};
                ColonyStateCodec.Site(site);ColonyStateCodec.Charter(charter);
                var adoption=fields.TryGetValue("AdoptFacilityIds",out var ids) ? ids.Split(new[]{'\r','\n',','},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Distinct(StringComparer.Ordinal).ToArray() : [];
                if (adoption.Any(id=>!snapshot.AdoptableFacilities.Any(f=>f.Id==id))) throw new InvalidOperationException("An adopted facility ID is absent from current provider inspection.");
                fields["AdoptFacilityIds"]=string.Join(",",adoption);
                review=name+" · "+site.Body+"/"+site.Biome+" at "+site.Latitude+", "+site.Longitude+". "+adoption.Length+" assets selected for adoption. Purpose "+charter.Purpose+"; target "+charter.PopulationTarget+" residents; ceiling "+charter.ResidentLimit+"; visitor allowance "+charter.VisitorLimit+". Budget "+charter.FoundingBudget.ToString("N0")+" funds; cash floor "+charter.CashFloor.ToString("N0")+"; spending limit "+charter.SpendingLimit.ToString("N0")+"; reserve "+charter.ReserveDays+" Kerbin days; growth "+charter.GrowthPolicy+". This operation registers the charter and selected assets. It does not claim funded startup reserves, commissioned housing, resident transport or finished construction.";
                kind="foundColony";
            }
            else if (request.Kind=="reviewConstruction")
            {
                var template=snapshot.Templates.SingleOrDefault(t=>t.Id==Field("TemplateId")) ?? throw new InvalidOperationException("Package is absent from the current catalog.");
                var colony=snapshot.State.Colonies.SingleOrDefault(c=>c.Id==request.ColonyId) ?? throw new InvalidOperationException("Select a registered colony.");
                var plot=colony.Plots.SingleOrDefault(p=>p.Id==Field("PlotId")) ?? throw new InvalidOperationException("Plot is absent from the saved survey.");
                fields["TemplateHash"]=template.Hash;
                review=template.Name+" v"+template.Version+"; funds "+template.BuildFunds.ToString("N0")+"; labor "+template.LaborSeconds.ToString("N0")+" s; plot "+plot.Id+"; footprint "+template.WidthMeters+" × "+template.LengthMeters+" m. Materials: "+string.Join(", ",template.Materials.Select(m=>(m.Amount/(decimal)ColonyLimits.Units)+" "+m.Resource))+". Embedded contents: "+string.Join(", ",template.EmbeddedContents.Select(m=>(m.Amount/(decimal)ColonyLimits.Units)+" "+m.Resource))+". Certification: "+template.CertificationEvidence+". The authority rechecks tech, materials, plots, funds and charter obligations at commitment.";
                kind="approveConstruction";
            }
            else if (request.Kind is "reviewRecruitment" or "reviewDeparture")
            {
                var colony=snapshot.State.Colonies.SingleOrDefault(c=>c.Id==request.ColonyId) ?? throw new InvalidOperationException("Select a registered colony.");
                var route=snapshot.People.Routes.SingleOrDefault(r=>r.Id==Field("RouteId") && r.Body==colony.Site.Body) ?? throw new InvalidOperationException("Configured passenger route is absent from current provider inspection.");
                if(!route.Qualified)throw new InvalidOperationException("Passenger route has not passed runtime qualification: "+route.Evidence);
                string personName;long funds;
                if(request.Kind=="reviewRecruitment")
                {
                    var person=snapshot.People.Roster.SingleOrDefault(p=>p.RosterId==Field("RosterId") && p.Current && p.ContextKey==snapshot.ContextKey) ?? throw new InvalidOperationException("Named roster identity has no current witness.");
                    if(person.Status!="Available" || person.Type is not ("Crew" or "Applicant"))throw new InvalidOperationException("Recruitment requires an available ordinary Crew or named Applicant.");
                    var home=colony.Facilities.SingleOrDefault(f=>f.Id==Field("HomeFacilityId") && f.State=="operational" && f.Qualification.HousingCertified) ?? throw new InvalidOperationException("Select an operational certified home.");
                    uint part=checked((uint)Integer("HomePartId"));
                    var seat=snapshot.People.Seats.SingleOrDefault(s=>s.FacilityId==home.Id && s.PartId==part && s.Current) ?? throw new InvalidOperationException("Home part has no current seat witness.");
                    funds=checked(route.Fare+(person.Type=="Applicant" ? route.RecruitmentFee : 0));personName=person.Name;kind="recruitResident";
                    review=personName+" · "+person.Trait+" · actual "+person.Type+" roster record. Home "+home.Name+" / part "+part+"; "+seat.Occupants.Count+" / "+seat.Capacity+" physical seats occupied. Recruitment fee "+(person.Type=="Applicant" ? route.RecruitmentFee : 0).ToString("N0")+"; passenger fare "+route.Fare.ToString("N0")+"; total "+funds.ToString("N0")+" funds.";
                }
                else
                {
                    var resident=colony.Residents.SingleOrDefault(r=>r.Id==request.TargetId) ?? throw new InvalidOperationException("Select the actual resident whose departure is being reviewed.");
                    personName=resident.Name;funds=route.Fare;kind="departResident";
                    review=personName+" · actual roster "+resident.RosterId+"; paid departure fare "+funds.ToString("N0")+" funds. The actual Kerbal and home reservation remain protected until the verified source crew release.";
                }
                fields["RouteHash"]=route.Hash;fields["QuotedFunds"]=funds.ToString(CultureInfo.InvariantCulture);
                review+=" Route "+route.Name+"; lead time "+route.TravelSeconds.ToString("N0")+" game seconds; "+route.ConcurrentSeats+" concurrent paid seats. "+(route.DevelopmentOnly ? "DEVELOPMENT modeled transport service. " : "Modeled transport service. ")+route.Evidence+". Authority rechecks current cash, certified homes, physical seat capacity, roster identity and support reserves before commitment/arrival. No Kerbal is spawned by review.";
            }
            else if(request.Kind=="reviewSupport")
            {
                var colony=snapshot.State.Colonies.SingleOrDefault(c=>c.Id==request.ColonyId) ?? throw new InvalidOperationException("Select a registered colony.");
                var env=new ColonyEnvironment {WorldId=snapshot.State.WorldId,ContextKey=snapshot.ContextKey,Ut=Math.Max(snapshot.State.SimulatedUt,snapshot.ObservedUt),Support=snapshot.Support,People=snapshot.People};
                long reserve=ColonyEngine.SupportReserveQuote(colony,env);
                fields["PolicyId"]=snapshot.Support.PolicyId;fields["PolicyHash"]=snapshot.Support.PolicyHash;fields["QuotedReserveMicroUnits"]=reserve.ToString(CultureInfo.InvariantCulture);
                int seats=snapshot.People.Seats.Where(s=>s.Current && s.HousingCertified && s.UtilitiesQualified && colony.Facilities.Any(f=>f.Id==s.FacilityId)).Sum(s=>s.Capacity);
                review=snapshot.Support.PolicyId+" · "+snapshot.Support.MicroUnitsPerPersonDay/(decimal)ColonyLimits.Units+" Supplies per person per Kerbin day; charter reserve "+colony.Charter.ReserveDays+" days; required owned reserve "+reserve/(decimal)ColonyLimits.Units+" Supplies. Current qualified home seats "+seats+"; founding target "+colony.Charter.PopulationTarget+". "+snapshot.Support.Reason+". Commissioning starts support accounting now; prior time creates no debt. External life-support ownership, complete site population, paid stock, reservations and actual utility/home witnesses are rechecked; no Supplies are consumed twice.";kind="commissionSupport";
            }
            else if(request.Kind=="reviewLogistics")
            {
                var colony=snapshot.State.Colonies.SingleOrDefault(c=>c.Id==request.ColonyId) ?? throw new InvalidOperationException("Select a registered colony.");
                var policy=snapshot.EconomyPolicies.SingleOrDefault(p=>p.Id==Field("PolicyId") && p.Body==colony.Site.Body) ?? throw new InvalidOperationException("Configured staging contract is unavailable for this body.");
                fields["PolicyHash"]=policy.Hash;fields["QuotedFunds"]=policy.SetupFunds.ToString(CultureInfo.InvariantCulture);
                review=policy.Provider+" · setup "+policy.SetupFunds.ToString("N0")+" funds; delivery "+policy.SetupTravelSeconds.ToString("N0")+" game seconds; "+policy.ContractorWorkers+" contractor slots. Empty stores: "+string.Join(", ",policy.Stores.Select(s=>s.Capacity/(decimal)ColonyLimits.Units+" "+s.Resource))+". Finite suppliers: "+string.Join(", ",policy.Suppliers.Select(s=>s.Resource+" "+s.Available/(decimal)ColonyLimits.Units+" stock / "+s.ConcurrentCapacity+" shared freight slots"))+". "+policy.Reason+". No resource contents, WOLF capacity or local Kerbals are created by approving staging.";kind="activateLogistics";
            }
            else if(request.Kind is "reviewWolfSupply" or "reviewWolfReplan" or "reviewSupport")
            {
                var env=new ColonyEnvironment {WorldId=snapshot.State.WorldId,ContextKey=snapshot.ContextKey,Ut=Math.Max(snapshot.State.SimulatedUt,snapshot.ObservedUt),AvailableFunds=snapshot.AvailableFunds ?? 0,Wolf=snapshot.Wolf,UnlockedTech=snapshot.UnlockedTech,DevelopmentMode=snapshot.DevelopmentMode};
                if(request.Kind=="reviewWolfSupply")
                {
                    var quote=ColonyEngine.QuoteWolfSupply(snapshot.State,request.ColonyId ?? "",Field("Resource"),checked((int)Integer("DesiredAvailable")),env);
                    backendQuoteId=quote.Id;kind="approveWolfSupply";
                    review=quote.DesiredAvailable+" available WOLF "+quote.Resource+" points; "+quote.Funds.ToString("N0")+" funds; "+quote.LaborSeconds.ToString("N0")+" game seconds supplier lead time. Establish depot "+quote.EstablishDepot+"; survey "+quote.SurveyDepot+". Modules: "+string.Join("; ",quote.Modules.Select(m=>m.Count+" × "+m.Recipe.PartName+"; inputs "+string.Join(",",m.Recipe.Inputs.Select(i=>i.Points+" "+i.Resource))+"; outputs "+string.Join(",",m.Recipe.Outputs.Select(i=>i.Points+" "+i.Resource))))+". "+quote.BalancePolicy;
                }
                else
                {
                    var quote=ColonyEngine.QuotePaidWolfReplan(snapshot.State,request.TargetId ?? "",env);backendQuoteId=quote.Id;kind="replanPaidWolfSupply";
                    review=quote.Reason+" Unused purchased depot retained "+quote.UnusedPurchasedDepot+". Before "+ColonyStateCodec.WolfDepotHash(quote.Before)+"; after "+ColonyStateCodec.WolfDepotHash(quote.After)+". Zero additional funds; allocation is revalidated against the actual current full depot.";
                }
            }
            else if(request.Kind=="reviewService")
            {
                var tankId=Field("ServiceTank");
                var tank=snapshot.Services.Targets.SingleOrDefault(t=>t.ColonyId==request.ColonyId && t.FacilityId+"/"+t.PartId+"/"+t.DestinationResource==tankId) ?? throw new InvalidOperationException("Installed tank is absent from the current service provider.");
                if(!tank.Current || !tank.CanApply || !tank.QualifiedWorker)throw new InvalidOperationException(tank.Reason);
                if(!decimal.TryParse(Field("Amount"),NumberStyles.Number,CultureInfo.InvariantCulture,out var amount) || amount<=0 || amount*ColonyLimits.Units!=decimal.Truncate(amount*ColonyLimits.Units))throw new InvalidOperationException("Enter a positive service quantity with at most six decimal places.");
                long micro=checked((long)(amount*ColonyLimits.Units));if(micro>tank.Capacity-tank.Amount)throw new InvalidOperationException("Requested amount exceeds actual installed free capacity.");
                fields["AmountMicroUnits"]=micro.ToString(CultureInfo.InvariantCulture);fields["ServiceQuoteHash"]=tank.QuoteHash;
                review=amount+" "+tank.SourceResource+" owned stock → installed "+tank.DestinationResource+" tank; part "+tank.PartId+"; facility "+tank.FacilityId+". Current tank "+tank.Amount/(decimal)ColonyLimits.Units+" / "+tank.Capacity/(decimal)ColonyLimits.Units+". "+tank.WorkerWitness+" Provider "+tank.Provider+". Authority protects support floors/reservations and verifies exact physical credit; an uncertain attempt retains held stock without blind refund.";kind="serviceFacility";
            }
            else
            {
                var supplier=snapshot.State.Suppliers.SingleOrDefault(s=>s.Id==Field("SupplierId")) ?? throw new InvalidOperationException("Supplier is absent from the current saved quote inputs.");
                if (!decimal.TryParse(Field("Amount"),NumberStyles.Number,CultureInfo.InvariantCulture,out var amount) || amount<=0 || amount*ColonyLimits.Units!=decimal.Truncate(amount*ColonyLimits.Units)) throw new InvalidOperationException("Enter a positive quantity with at most six decimal places.");
                long micro=checked((long)(amount*ColonyLimits.Units));ColonyStateCodec.Quantity(micro);
                long cost=checked(ColonyEngine.ScaledProduct(micro,supplier.FundsPerUnit)+supplier.FreightFunds);
                fields["AmountMicroUnits"]=micro.ToString(CultureInfo.InvariantCulture);fields["QuotedFunds"]=cost.ToString(CultureInfo.InvariantCulture);
                review=amount+" "+supplier.Resource+" from "+supplier.Id+"; purchase and freight "+cost.ToString("N0")+" funds; travel "+supplier.TravelSeconds.ToString("N0")+" game seconds. Supplier stock, receiving space, mass/volume, concurrent freight, shared funds and reserve policy are rechecked before approval.";
                kind="approveTrade";
            }
            command.Kind=kind;
            managementPlanningQuote=reviewedPlanning;
            managementQuoteId=backendQuoteId ?? ColonyStateCodec.CommandHash(command);managementQuoteContext=request.ContextKey;managementQuoteColonyId=request.ColonyId;managementQuoteTargetId=request.TargetId;managementQuoteRevision=request.ExpectedRevision;managementQuoteFields=new Dictionary<string,string>(fields,StringComparer.Ordinal);managementQuoteKind=kind;managementQuoteReview=review;
            RenderColonyManagement();
            return new(true,"Plan reviewed. Inspect the itemized quote before approval.",false);
        }
        catch(Exception ex) when(ex is InvalidOperationException or InvalidDataException or OverflowException or ArgumentException)
        { return new(true,ex.Message); }
    }
}
