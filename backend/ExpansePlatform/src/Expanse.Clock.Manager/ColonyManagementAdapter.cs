using System.Globalization;
using Expanse.Clock.Core;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

public static class ColonyManagementAdapter
{
    public static ColonyCommand Command(ColonyManagementRequest request)
    {
        var fields = new Dictionary<string, string>(request.Fields, StringComparer.Ordinal);
        if (fields.TryGetValue("GrowthPolicy", out var growth)) fields["GrowthPolicy"] = growth switch
        {
            "Approval required" => "approval", "Automatic within charter" => "automatic", "Paused" => "disabled", _ => growth
        };
        var target=request.TargetId ?? "";
        if(request.Kind=="surveyPlot")target=fields.TryGetValue("SurveyPlotId",out var plot) && Guid.TryParse(plot,out _) ? plot : request.OperationId;
        if(request.Kind=="serviceFacility" && fields.TryGetValue("ServiceTank",out var tank))
        {
            var components=tank.Split('/');if(components.Length==3){target=components[0];fields["PartId"]=components[1];fields["DestinationResource"]=components[2];}
        }
        if(request.Kind=="configureServicePolicy" && fields.TryGetValue("TargetFillPercent",out var fill) && decimal.TryParse(fill,NumberStyles.Number,CultureInfo.InvariantCulture,out var percent))fields["TargetFillFraction"]=(percent/100).ToString(CultureInfo.InvariantCulture);
        return new ColonyCommand
        {
            OperationId = request.OperationId, ContextKey = request.ContextKey, ExpectedRevision = request.ExpectedRevision,
            Kind = request.Kind, ColonyId = request.ColonyId ?? "", TargetId = target, QuoteId = request.QuoteId ?? "",
            FoundingIntent=request.FoundingIntent,
            Fields = fields
        };
    }

    // Only authoritative registered vessel IDs join physical observations. Body or
    // biome coincidence never joins facilities, residents or physical inventory.
    public static ColonyManagementPresentation Present(ColonyState state, string contextKey, string? selectedColonyId,
        string status, string reason, ColonyManagementAction[]? capabilities = null,
        ColonyTemplate[]? templates = null, ColonySnapshot? observations = null, long? observedFunds = null, bool observationsCurrent = false,
        IReadOnlyDictionary<string,double>? bodyRadiiMeters=null, ColonyPeopleEnvironment? peopleEnvironment=null,ColonyServicesEnvironment? servicesEnvironment=null,ColonyEconomyPolicy[]? economyPolicies=null,ColonyWolfEnvironment? wolfEnvironment=null)
    {
        var colony = selectedColonyId == "__new" ? null : state.Colonies.FirstOrDefault(c => c.Id == selectedColonyId);
        var choices = state.Colonies.Select(c => new ColonyManagementChoice(c.Id, c.Name + " · " + c.Site.Body + "/" + c.Site.Biome))
            .Append(new ColonyManagementChoice("__new", "Establish a new colony…")).ToArray();
        var sections = new List<ColonyManagementSection>();
        var actions = capabilities ?? [];
        peopleEnvironment ??= new ColonyPeopleEnvironment();
        servicesEnvironment ??= new ColonyServicesEnvironment();
        wolfEnvironment ??= new ColonyWolfEnvironment();
        var currentTemplates = templates ?? [];
        string PackageName(string id)=>currentTemplates.FirstOrDefault(t=>t.Id==id)?.Name ?? "Package unavailable";
        string SupplierName(ColonySupplier supplier)=>((economyPolicies ?? []).FirstOrDefault(p=>p.Suppliers.Any(s=>s.Id==supplier.Id))?.Provider ?? "Resource supplier")+" · "+supplier.Resource+(supplier.DestinationBody.Length>0 ? " · "+supplier.DestinationBody : "")+" · "+Funds(supplier.FundsPerUnit)+" / unit";
        var facilities = colony?.Facilities.ToArray() ?? [];
        var observed = observations?.Vessels.Where(v => facilities.Any(f => f.VesselId == v.VesselId)).ToArray() ?? [];
        var orders = state.Construction.Where(o => o.ColonyId == colony?.Id).ToArray();
        var shipments = state.Shipments.Where(s => s.ColonyId == colony?.Id).ToArray();
        var effects = state.Effects.Where(e => e.ColonyId == colony?.Id).ToArray();
        var journal = state.Journal.Where(j => j.ColonyId == colony?.Id).OrderByDescending(j => j.Sequence).ToArray();
        string context = "Save authority · UT " + Number(state.SimulatedUt);
        string Basis(ColonyVessel v) => (observationsCurrent && observations?.Status == "observed" ? "Current " : "Last reported ") + v.ObservationBasis;
        string selectedReason = colony is null ? "Select a registered colony or review a new founding plan." : DisplayReason(reason);
        string Details(ColonyFacility f) => "Vessel " + f.VesselId + "; template " + f.TemplateId + "; plot " + f.PlotId + "; production owner " + f.ProductionOwner + ". " + f.LastReason;
        string ContextName(string value) => value switch { "loaded-packed" => "Loaded, physics inactive", "loaded-unpacked" => "Loaded, physics active", "unloaded-proto" => "Unloaded, saved parts", _ => value };
        void Add(string key, string title, string summary, IEnumerable<ColonyManagementRow> rows, string[] kinds,
            string empty, ColonyManagementField[]? fields = null,ColonyManagementMetric[]? metrics=null,ColonyManagementPlotGeometry[]? map=null)
        {
            ColonyManagementColumn[]? columns=key switch
            {
                "people"=>[new("Person","Name",1.6),new("Status","State",1),new("Skill","Quantity",.9),new("Home","Cells[Home]",1.2),new("Job / seat","Cells[Job]",1.3)],
                "trade"=>[new("Shipment / supplier","Name",1.7),new("Phase / blocker","State",1.2),new("Cargo","Cells[Amount]",1.1,true),new("Funds","Cells[Cost]",1,true),new("Arrival UT","Cells[Arrival]",1,true)],
                "finance"=>[new("Ledger event / policy","Name",1.7),new("State / date","State",1.2),new("Funds","Quantity",1.2,true),new("Settlement","Cells[Operation]",1.3)],
                _=>null
            };
            sections.Add(new(key,title,summary,rows.ToArray(),actions.Where(a=>kinds.Contains(a.Kind,StringComparer.Ordinal)).Select(a=>
                a.Kind=="foundColony" && colony is not null ? a with {Available=false,Reason="Choose Establish a new colony before registering another identity."} :
                a.Kind=="updateCharter" && colony is null ? a with {Available=false,Reason="Select an existing registered colony to update its charter."} : a).ToArray(),empty,fields,columns,metrics,map));
        }
        var overview = new List<ColonyManagementRow>();
        if (colony is not null)
        {
            overview.Add(new("support", "Residents and visitors", colony.SupportStatus,
                colony.Residents.Count + " residents · " + colony.VisitorRosterIds.Count + " visitors", context,
                colony.SupportPolicy + ". Enforcement commissioned at UT " + (colony.SupportCommissionedUt.HasValue ? Number(colony.SupportCommissionedUt.Value) : "not commissioned") + ". Existing visitors remain part of support demand."));
            overview.Add(new("homes", "Housing capacity", "Verified homes", facilities.Sum(f=>f.CertifiedHomes) + " homes",context,
                "Counts homes that passed housing checks. Facility seats and industrial work places are shown separately."));
            overview.Add(new("cash", "Career funds", observedFunds.HasValue ? "Observed funds account" : "Unavailable", observedFunds.HasValue ? Funds(observedFunds.Value) : "Unknown", observedFunds.HasValue ? "KSP account observation" : "Unknown",
                "Charter cash floor " + Funds(colony.Charter.CashFloor) + "; total colony spending " + Funds(colony.SpentFunds) + ". Pending effects and reserved orders are listed on Finance & policy. Revenue and cash runway are unknown without qualified forecasts."));
            overview.AddRange(colony.Proposals.Select(p=>new ColonyManagementRow(p.Id,"Expansion: "+PackageName(p.TemplateId),p.State,Number(p.DownsideCashDays)+" downside days",context,p.Reason+" Decision: "+p.DecisionReason+(p.State=="deferred" ? "; deferred until game UT "+Number(p.DeferredUntilUt) : "")+"; plot "+p.PlotId+(p.PlanId.Length>0 ? "; exact saved plan "+p.PlanId : ""))));
            overview.AddRange(orders.Where(o=>o.State != "operational" && o.State != "cancelled").Select(o=>new ColonyManagementRow(o.Id,"Construction: "+PackageName(o.TemplateId),o.State,Funds(o.Funds),context,o.Reason)));
        }
        // These save-wide holds also block other colonies. Keep their exact evidence
        // in the existing records/details area, even without a selected record.
        var unresolved=state.Effects.Where(e=>e.State is "held" or "applying").ToArray();
        overview.AddRange(unresolved.Select(e=>new ColonyManagementRow("colony-effect:"+e.Id,"Pending colony change",e.State,"",context,
            e.Reason+"; operation "+e.OperationId+"; effect "+e.Id+"; colony "+e.ColonyId)));
        if((unresolved.FirstOrDefault(e=>e.State=="held") ?? unresolved.FirstOrDefault(e=>e.State=="applying")) is {} hold)
        {
            var matching=state.Construction.Where(o=>hold.Kind=="constructionPlacement" && hold.Id.Length>0 && hold.OperationId.Length>0 &&
                o.Id==hold.TargetId && o.ColonyId==hold.ColonyId && o.Placement.EffectId==hold.Id && o.Placement.OperationId==hold.OperationId).ToArray();
            bool selectedBuilding=matching.Length==1 && matching[0].ColonyId==colony?.Id;
            string subject=selectedBuilding ? PackageName(matching[0].TemplateId)+" placement" : "Building placement";
            selectedReason=hold.State=="held" && hold.Kind=="constructionPlacement" && hold.Reason==PlacementMembershipHold
                ? "Needs attention: "+subject+" could not be verified. "+(selectedBuilding ? "See Site & construction for details." : "See Details for the full error.")
                : (hold.State=="held" ? "Needs attention: " : "Waiting for a colony change to finish: ")+hold.Reason;
        }
        else if(orders.FirstOrDefault(o=>o.State=="held") is {} heldOrder)
            selectedReason="Needs attention: "+PackageName(heldOrder.TemplateId)+". "+DisplayReason(heldOrder.Reason);
        else if(colony is not null && (reason.Length==0 || reason==ConnectedReason))
        {
            var startup=state.Plans.FirstOrDefault(p=>p.ColonyId==colony.Id && p.Quote.Kind=="founding" && p.State!="cancelled");
            if(startup is not null)
            {
                int operational=startup.Buildings.Count(b=>b.OrderId.Length>0 && orders.Count(o=>o.Id==b.OrderId)==1 && orders.Any(o=>o.Id==b.OrderId && o.State=="operational"));
                selectedReason=startup.State=="complete" ? "Startup plan complete." :
                    "Building your colony: "+operational+" of "+startup.Buildings.Count+" buildings ready. See current plans and reviews for details.";
            }
        }
        ColonyManagementMetric[]? metrics=null;
        if(colony is not null)
        {
            int presentPeople=colony.Residents.Count(r=>r.Status!="arriving" && r.Status!="missing")+colony.VisitorRosterIds.Count;
            var supply=colony.Stock.FirstOrDefault(s=>s.Resource=="Supplies");
            string supportDays=presentPeople==0 ? "No present demand" : supply is null ? "Reserve unavailable" :
                colony.SupportMicroUnitsPerPersonDay<=0 ? "Policy rate unknown" :
                Number((double)Math.Max(0,(supply.Amount-supply.Reserved-colony.SupportRemainder)/(decimal)(presentPeople*colony.SupportMicroUnitsPerPersonDay)))+" Kerbin days";
            long committed=ColonyEngine.CommittedFunds(state,colony.Id);
            int homes=facilities.Where(f=>f.State=="operational" && f.Qualification.HousingCertified).Sum(f=>f.CertifiedHomes);
            var pending=orders.Where(o=>o.State!="operational" && o.State!="cancelled").ToArray();
            metrics=[new("PEOPLE & HOMES",colony.Residents.Count+" residents · "+colony.VisitorRosterIds.Count+" visitors",homes+" commissioned certified homes; population ceiling "+colony.Charter.ResidentLimit,"Saved roster links and housing certification","people"),
                new("SUPPORT RESERVE",supportDays,colony.SupportStatus,"Policy estimate · saved colony stock only","inventory"),
                new("FUNDS & COMMITMENTS",observedFunds.HasValue ? observedFunds.Value.ToString("N0")+" funds" : "Career funds unknown",committed.ToString("N0")+" committed; floor "+colony.Charter.CashFloor.ToString("N0"),"Observed account + saved obligations","finance"),
                new("CONSTRUCTION",pending.Length+" open orders",pending.FirstOrDefault()?.Reason ?? "No active construction; expansion proposals require funded approval.","Saved work / placement stages","construction")];
        }
        var activePlans=state.Plans.Where(p=>p.ColonyId==colony?.Id).Select(p=>new ColonyManagementRow("colony-plan:"+p.Id,p.Quote.Kind=="founding" ? "Complete startup plan" : "Housing expansion",p.State,Funds(p.RemainingFunds)+" future commitments",context,p.Reason+" Total approved scope "+Funds(p.Quote.TotalFunds)+"; buildings "+p.Buildings.Count+"; imports "+p.Imports.Count+". Paid/dispatched work is retained when future commitments are cancelled."));
        Add("overview","Overview",selectedReason,overview.Concat(activePlans),["reviewGrowthPlan","approveGrowthPlan","reviewGrowthProposal","approveGrowthProposal","deferGrowthProposal","rejectGrowthProposal","reconsiderGrowthProposal","surveyGrowthPlan","cancelColonyPlan"],"No colony is selected. Founding & charter contains the new-colony workflow.",
            fields:[new("DecisionReason","Decision reason","User reviewed expansion timing.","Saved with this exact review. Reject closes it permanently; a later cadence may generate an independent review."),new("DelaySeconds","Deferral (game seconds)","21600","60 seconds to 30 Kerbin days. Suppresses all automatic replacement reviews until the server sets this duration's deadline.")],metrics:metrics);
        var charterRows = new List<ColonyManagementRow>();
        if (colony is not null)
        {
            var c = colony.Charter;
            charterRows.Add(new("charter",colony.Name,colony.Status,Funds(c.FoundingBudget)+" spending cap",context,
                "Purpose "+c.Purpose+"; target "+c.PopulationTarget+"; resident ceiling "+c.ResidentLimit+"; visitor allowance "+c.VisitorLimit+"; cash floor "+Funds(c.CashFloor)+"; spending limit "+Funds(c.SpendingLimit)+"; reserves "+Number(c.ReserveDays)+" Kerbin days; growth "+c.GrowthPolicy+"; sandbox "+c.Sandbox+". Site "+colony.Site.Body+"/"+colony.Site.Biome+" "+Number(colony.Site.Latitude)+", "+Number(colony.Site.Longitude)+". Stable colony ID "+colony.Id));
            charterRows.AddRange(facilities.Select(f=>new ColonyManagementRow(f.Id,f.Name,f.State,f.CertifiedHomes+" certified homes",context,Details(f))));
            var wolfSite=wolfEnvironment?.Sites.FirstOrDefault(s=>s.ColonyId==colony.Id && s.Depot.Body==colony.Site.Body && s.Depot.Biome==colony.Site.Biome);
            if(wolfSite?.Depot.Exists==true)
            {
                charterRows.Add(new("colony-wolf:depot","WOLF depot "+colony.Site.Body+" / "+colony.Site.Biome,"Shared biome ledger",wolfSite.Depot.Streams.Count+" resource streams",wolfEnvironment?.Provider ?? "WOLF",
                    "Current native depot observation. Its capacity is shared by biome and is not colony-owned physical stock. "+wolfSite.Reason));
                charterRows.AddRange(wolfSite.Depot.Streams.Select(r=>new ColonyManagementRow("colony-wolf:"+r.Resource,"WOLF "+r.Resource,"Shared biome capacity",(r.Incoming-r.Outgoing)+" available",wolfEnvironment?.Provider ?? "WOLF",
                    "Incoming "+r.Incoming+"; outgoing "+r.Outgoing+". This is native WOLF capacity, not physical inventory.")));
            }
            charterRows.Add(new("logistics", "Paid staging contract",colony.Logistics.State,Funds(colony.Logistics.Funds),colony.Logistics.Provider,colony.Logistics.Reason+"; delivery UT "+Number(colony.Logistics.ActivationUt)+"; contractor capacity "+colony.Logistics.ContractorWorkers+". Empty storage capacity creates no free inventory or local crew."));
        }
        charterRows.AddRange(currentTemplates.Select(t=>new ColonyManagementRow(t.Id,t.Name,t.RuntimeCertified ? "Runtime certified" : "Certification pending",Funds(t.BuildFunds),"Package v"+t.Version,
            "Hash "+t.Hash+"; materials "+Materials(t.Materials)+"; embedded contents "+Materials(t.EmbeddedContents)+"; labor "+Number(t.LaborSeconds)+" s; footprint "+Number(t.WidthMeters)+" × "+Number(t.LengthMeters)+" m; tech "+string.Join(", ",t.RequiredTech)+". "+t.CertificationEvidence)));
        string foundingNext=colony is null
            ? "Name the settlement and choose its site. Review the charter, then add facilities, resources and residents through separate tasks when ready."
            : state.Plans.Any(p=>p.ColonyId==colony.Id && p.Quote.Kind=="founding" && p.State!="cancelled")
            ? "Startup plan accepted. Follow its current progress and holds in Overview; Site & construction shows physical placement."
            : "The charter is registered. Adopt hardware, obtain storage and supplies, qualify homes, and recruit residents as separate tasks. Construction planning is optional and requires certified packages.";
        Add("founding","Founding & charter",foundingNext,activePlans.Concat(charterRows),
            ["surveySite","quoteFounding","reviewFounding","foundColony","reviewCharter","updateCharter","reviewAdoption","adoptFacility","reviewAdoptedHabitat","qualifyAdoptedHabitat","reviewFoundingPlan","approveFoundingPlan","surveyFoundingPlan","cancelColonyPlan","reviewLogistics","activateLogistics"],"No records yet. An empty charter adds no hardware or stock.",
            [new("PolicyId","Paid staging / freight contract","","Approves finite empty stores; supplies and construction inputs require separate paid imports.",(economyPolicies ?? []).Where(p=>p.Body==colony?.Site.Body).Select(p=>new ColonyManagementChoice(p.Id,p.Provider+" · "+Funds(p.SetupFunds)+" · "+Number(p.SetupTravelSeconds)+" s")).ToArray())]);
        var constructionRows = orders.Select(o=>new ColonyManagementRow(o.Id,"Build "+PackageName(o.TemplateId),o.State,Number(o.WorkCompleted)+" / "+Number(o.WorkRequired)+" labor s",context,
            o.Reason+". Funds "+Funds(o.Funds)+" ("+(o.FundsPaid ? "paid" : "awaiting debit")+"); materials "+Materials(o.Materials)+" ("+(o.MaterialsConsumed ? "consumed" : "reserved")+"); dependencies "+string.Join(", ",o.Dependencies)+"; plot "+o.PlotId+"; facility "+o.FacilityId+". Timer completion is not commissioning."));
        var plots = colony?.Plots.Select((p,i)=>new ColonyManagementRow(p.Id,"Plot "+(i+1),p.OccupiedBy.Length>0 ? "Occupied" : p.ReservedBy.Length>0 ? "Reserved" : "Surveyed",Number(p.WidthMeters)+" × "+Number(p.LengthMeters)+" m",p.SurveyHash.Length>0 ? "Survey witness" : "Unverified",
            "Coordinates "+Number(p.Latitude)+", "+Number(p.Longitude)+"; heading "+Number(p.Heading)+"°; reservation "+p.ReservedBy+"; occupied by "+p.OccupiedBy+"; survey hash "+p.SurveyHash)) ?? [];
        ColonyManagementPlotGeometry[]? map=null;
        if(colony is not null && bodyRadiiMeters is not null && bodyRadiiMeters.TryGetValue(colony.Site.Body,out var radius) && radius>0)
        {
            map=colony.Plots.Select((p,i)=>new ColonyManagementPlotGeometry(p.Id,"Plot "+(i+1),
                radius*NormalizeLongitude(p.Longitude-colony.Site.Longitude)*Math.PI/180*Math.Cos(colony.Site.Latitude*Math.PI/180),radius*(p.Latitude-colony.Site.Latitude)*Math.PI/180,
                p.WidthMeters,p.LengthMeters,p.Heading,p.OccupiedBy.Length>0 ? "Occupied" : p.ReservedBy.Length>0 ? "Reserved" : "Surveyed",p.Id+"; latitude "+p.Latitude+"; longitude "+p.Longitude+"; survey "+p.SurveyHash)).ToArray();
        }
        Add("construction","Site & construction","Surveyed plots, funded dependencies, labor, placement and commissioning.",plots.Concat(constructionRows),
            ["surveyPlot","quoteConstruction","reviewConstruction","approveConstruction","cancelConstruction","retryConstructionPlacement","commissionFacility"],"No surveyed plots or construction orders.",
            [new("TemplateId","Building purpose / package","","Complete inputs, storage and utilities are reviewed with the package.",currentTemplates.Select(t=>new ColonyManagementChoice(t.Id,t.Name+" · v"+t.Version)).ToArray()),new("PlotId","Construction plot","","Placement rechecks actual clearance and scene context.",colony?.Plots.Select((p,i)=>new ColonyManagementChoice(p.Id,"Plot "+(i+1)+" · "+Number(p.WidthMeters)+" × "+Number(p.LengthMeters)+" m")).ToArray()),
             new("SurveyPlotId","Survey destination","__new","Existing uncommitted plots can be surveyed again.",new[]{new ColonyManagementChoice("__new","New building plot")}.Concat(colony?.Plots.Where(p=>p.OccupiedBy.Length==0 && p.ReservedBy.Length==0).Select((p,i)=>new ColonyManagementChoice(p.Id,"Resurvey plot "+(i+1))) ?? []).ToArray()),new("Latitude","Survey latitude",colony?.Site.Latitude.ToString("R",CultureInfo.InvariantCulture) ?? "","Authority checks loaded terrain and the colony boundary."),new("Longitude","Survey longitude",colony?.Site.Longitude.ToString("R",CultureInfo.InvariantCulture) ?? "","Authority checks actual footprints and clearance."),new("Heading","Building heading (degrees)","0","The final placement rechecks this footprint.")],map:map);
        var people = colony?.Residents.Select(r=>new ColonyManagementRow(r.Id,r.Name,r.Status,r.Trait,context,
            "Roster ID "+r.RosterId+"; certified home "+r.HomeFacilityId+" / part "+r.HomePartId+"; job "+r.JobFacilityId+"; physical work part "+r.WorkPartId+"; physically at work "+r.PhysicallyAtWork+". "+r.LastReason+" A job label alone grants no physical module bonus.",
            new Dictionary<string,string>{{"Home",facilities.FirstOrDefault(f=>f.Id==r.HomeFacilityId)?.Name ?? "Unassigned / unavailable"},{"Job",(facilities.FirstOrDefault(f=>f.Id==r.JobFacilityId)?.Name ?? "Unassigned")+" · "+(r.PhysicallyAtWork ? "qualified seat" : "seat unverified")}})) ?? [];
        var visitors = colony?.VisitorRosterIds.Select(id=>new ColonyManagementRow(id,peopleEnvironment.Roster.FirstOrDefault(p=>p.RosterId==id)?.Name ?? "Visitor name unavailable","Visiting astronaut",peopleEnvironment.Roster.FirstOrDefault(p=>p.RosterId==id)?.Trait ?? "Skill unknown",context,"This person is not automatically designated a resident. Physical seat requires current roster readback.",new Dictionary<string,string>{{"Home","Visitor accommodation unknown"},{"Job","Mission crew / unassigned"}})) ?? [];
        var passengerRows=state.PeopleOperations.Where(p=>p.ColonyId==colony?.Id).Select(p=>new ColonyManagementRow("passenger:"+p.Id,(p.Kind=="workerTransfer" ? "Worker shift" : p.Kind=="arrival" ? "Arrival" : "Departure")+" · "+(peopleEnvironment.Roster.FirstOrDefault(r=>r.RosterId==p.RosterId)?.Name ?? colony?.Residents.FirstOrDefault(r=>r.RosterId==p.RosterId)?.Name ?? "Crew record unavailable"),p.State,Funds(p.Funds),"Save-owned transport",
            p.Reason+"; route "+p.RouteId+"; fare "+Funds(p.Funds)+"; paid "+p.FundsPaid+"; arrival UT "+Number(p.ArrivalUt)+"; before "+p.BeforeWitness+"; after "+p.AfterWitness,
            new Dictionary<string,string>{{"Home",facilities.FirstOrDefault(f=>f.Id==p.HomeFacilityId)?.Name ?? "Unavailable home"},{"Job","Passenger operation; actual crew retained"}}));
        Add("people","People & jobs","Named residents, certified homes, job seats, and paid passenger trips. Crew stays protected during queued or uncertain trips.",people.Concat(visitors).Concat(passengerRows),
            ["reviewAdoptedHabitat","qualifyAdoptedHabitat","reviewWorkerTransfer","transferColonyWorker","cancelWorkerTransfer","reviewSupport","commissionSupport","recruitResident","reviewRecruitment","reviewDeparture","departResident","cancelPassenger","assignResidentHome","assignResidentJob","requestDeparture","assignResident"],"No residents or visitors are registered.",
            [new("RosterId","Named KSP roster Kerbal","","Recruit an available Crew/Applicant; assignment requires actual site presence.",peopleEnvironment?.Roster.Where(p=>p.Current && p.ContextKey==contextKey).Select(p=>new ColonyManagementChoice(p.RosterId,p.Name+" · "+p.Trait+" · "+p.Type+" / "+p.Status)).ToArray()),
             new("HomeFacilityId","Certified home","","Home capacity is rechecked before assignment.",facilities.Where(f=>f.CertifiedHomes>0 && f.Qualification.HousingCertified).Select(f=>new ColonyManagementChoice(f.Id,f.Name)).ToArray()),
             new("HomePartId","Home cabin / seats","","Choose the cabin belonging to the selected certified home.",peopleEnvironment?.Seats.Where(s=>s.HousingCertified && facilities.Any(f=>f.Id==s.FacilityId)).Select(s=>new ColonyManagementChoice(s.PartId.ToString(CultureInfo.InvariantCulture),(facilities.FirstOrDefault(f=>f.Id==s.FacilityId)?.Name ?? "Home")+" · cabin "+(peopleEnvironment.Seats.Where(p=>p.FacilityId==s.FacilityId && p.HousingCertified).OrderBy(p=>p.PartId).ToList().IndexOf(s)+1)+" · "+s.Occupants.Count+" / "+s.Capacity+" occupied")).ToArray()),
             new("JobFacilityId","Optional job facility","","Physical worker eligibility is independently qualified.",new[]{new ColonyManagementChoice("","No job assignment")}.Concat(facilities.Where(f=>(f.State=="operational" || f.State=="commissioning") && f.RequiredWorkers>0).Select(f=>new ColonyManagementChoice(f.Id,f.Name))).ToArray()),
             new("WorkPartId","Occupied workplace","0","A job requires its actual occupied workplace and qualified skill.",new[]{new ColonyManagementChoice("0","No workplace assignment")}.Concat(peopleEnvironment?.Seats.Where(s=>s.Current && s.WorkSupported && facilities.Any(f=>f.Id==s.FacilityId && f.RequiredWorkers>0)).Select((s,i)=>new ColonyManagementChoice(s.PartId.ToString(CultureInfo.InvariantCulture),(facilities.FirstOrDefault(f=>f.Id==s.FacilityId)?.Name ?? "Facility")+" · workplace "+(i+1)+" · "+s.Occupants.Count+" / "+s.Capacity+" occupants")) ?? []).ToArray()),
             new("RouteId","Paid modeled passenger route","","Finite seat capacity, lead time, recruitment fee and fare are rechecked.",peopleEnvironment?.Routes.Where(r=>colony is not null && r.Body==colony.Site.Body).Select(r=>new ColonyManagementChoice(r.Id,r.Name+" · "+Funds(r.Fare)+" fare · "+Number(r.TravelSeconds)+" s"+(r.DevelopmentOnly ? " · DEVELOPMENT" : ""))).ToArray()),
             new("ExplicitMissionCrewAssignment","Explicitly reassign protected astronaut?","false","Jeb, Bill, Bob and Val remain mission crew unless you explicitly select Yes.",[new("false","No — preserve mission assignment"),new("true","Yes — explicitly reassign this astronaut")])]);
        var production = facilities.Select(f=>new ColonyManagementRow(f.Id,f.Name,(f.ProductionOwner=="physical" ? "Facility production" : f.ProductionOwner+" production"),"Model lists "+f.RequiredWorkers+" workers",ContextName(f.Qualification.Context),
            Details(f)+" Required trait "+f.RequiredTrait+"; staffing qualified "+f.Qualification.StaffingQualified+"; inputs accessible "+f.Qualification.InputsAccessible+"; background supported "+f.Qualification.BackgroundSupported+"; evidence "+f.Qualification.EvidenceHash));
        var converterRows = observed.SelectMany(v=>v.Converters.Select((c,i)=>new ColonyManagementRow(v.VesselId+":converter:"+i,v.Name+" · "+c.Recipe,c.Running == true ? "Enabled switch" : c.Running == false ? "Disabled switch" : "Unknown switch","Throughput unknown",Basis(v),
            "Inputs "+string.Join(", ",c.Inputs)+"; outputs "+string.Join(", ",c.Outputs)+"; part "+c.PartName+". Activation is not measured production. Observation status "+observations!.Status)));
        Add("production","Production","One resource producer authority per facility. Existing MKS/WOLF/BRP ownership is preserved.",production.Concat(converterRows),[],"No registered production facilities or observations.");
        var power = facilities.Select(f=>new ColonyManagementRow(f.Id,f.Name,f.Qualification.EvidenceHash.Length == 0 ? "Qualification unknown" : f.Qualification.PowerReliable ? "Qualified power" : "Power not qualified","Heat "+(f.Qualification.HeatSafe ? "qualified" : "not qualified"),f.Qualification.Context,
            Details(f)+" Power qualification does not provide a measured EC rate, nighttime margin, storage autonomy or distribution reach. Evidence "+f.Qualification.EvidenceHash+"; observed UT "+Number(f.Qualification.ObservedUt)));
        var ec = observed.Select(v=>new ColonyManagementRow(v.VesselId+":ec",v.Name,v.Power?.Status ?? "Flow unknown",
            string.Join("; ",v.Tanks.Where(t=>t.Resource=="ElectricCharge").Select(t=>Number(t.Amount)+" / "+Number(t.Capacity)+" EC")),Basis(v),
            "Tracked generation "+NullableNumber(v.Power?.GenerationEcPerSecond)+" EC/s; consumption "+NullableNumber(v.Power?.ConsumptionEcPerSecond)+" EC/s; nominal estimate generation "+NullableNumber(v.PowerEstimate?.GenerationEcPerSecond)+" EC/s. "+v.Power?.Reason+" Summed batteries do not prove a connected grid. See existing Power drill-down for interval coverage."));
        var freshPower=(servicesEnvironment?.Utilities.Where(u=>facilities.Any(f=>f.Id==u.FacilityId)) ?? []).Select(u=>new ColonyManagementRow("current-power:"+u.FacilityId,facilities.First(f=>f.Id==u.FacilityId).Name,
            ColonyUtilityQualification.PowerSupported(u) ? "Continuous supply qualified" : "Supply qualification held",NullableNumber(u.ElectricCharge)+" / "+NullableNumber(u.ConnectedCapacity)+" EC",u.Context,
            u.Reason+" Recognized continuous source rating "+NullableNumber(u.NominalGenerationEcPerSecond)+" EC/s; "+(u.FullDemandAccounted ? "complete current" : "partial recognized")+" demand bound "+NullableNumber(u.NominalDemandEcPerSecond)+" EC/s. Storage change "+NullableNumber(u.NetStorageEcPerSecond)+" EC/s over "+NullableNumber(u.WindowSeconds)+" s is a separate observation. Part temperature "+NullableNumber(u.MaximumPartTemperature)+" K; part margin "+NullableNumber(u.MinimumTemperatureMargin)+" K; core temperature "+NullableNumber(u.MaximumCoreTemperature)+" K; core margin "+NullableNumber(u.MinimumCoreShutdownMargin)+" K. Thermal qualification "+(ColonyUtilityQualification.HeatSupported(u) ? "supported" : "held")+"; background "+(u.BackgroundProviderQualified ? "supported" : "unqualified")+". "+u.Evidence));
        Add("power","Power","Continuous supply, demand bounds, storage measurements and thermal margins remain separately labeled.",freshPower.Concat(power).Concat(ec),[],"Power evidence is unavailable; WOLF Power is not physical ElectricCharge.");
        var stock = colony?.Stock.Select(s=>new ColonyManagementRow("stock:"+s.Resource,s.Resource,"Colony-owned reserve",Units(s.Amount)+" / "+Units(s.Capacity),context,
            "Reserved "+Units(s.Reserved)+"; incoming reservation "+Units(s.IncomingReserved)+"; support floor "+Units(s.SupportFloor)+"; imported provenance "+Units(s.ImportedAmount)+". Reservations and incoming commitments are not additional inventory.")) ?? [];
        var physical = observed.SelectMany(v=>v.Tanks.Select((t,i)=>new ColonyManagementRow(v.VesselId+":tank:"+i,v.Name+" · "+t.Resource,"Part tank observed",Number(t.Amount)+" / "+Number(t.Capacity),Basis(v),
            "Role "+t.Role+"; warehouse present "+Flag(t.WarehousePresent)+"; local warehouse "+Flag(t.LocalWarehouseOn)+"; resource flow "+Flag(t.FlowEnabled)+". Observation is not a transfer entitlement; accessible stock needs a qualified provider.")));
        var wolf = observations?.Wolf?.Depots.Where(d=>colony is not null && d.Body==colony.Site.Body && d.Biome==colony.Site.Biome).SelectMany(d=>d.Resources.Select(r=>new ColonyManagementRow("wolf:"+r.Name,"WOLF · "+r.Name,"Biome capacity ledger",r.Available+" available",observations.Wolf.Status,
            "Incoming "+r.Incoming+"; outgoing "+r.Outgoing+"; established "+d.Established+". Capacity is shared at biome scope, not physical tanks or exclusively owned colony stock."))) ?? [];
        var currentWolf=(wolfEnvironment?.Sites.Where(s=>s.ColonyId==colony?.Id) ?? []).SelectMany(s=>s.Depot.Streams.Select(r=>new ColonyManagementRow("current-wolf:"+r.Resource,"WOLF · "+r.Resource,"Current biome allocation",(r.Incoming-r.Outgoing)+" available",wolfEnvironment?.Provider ?? "WOLF","Incoming "+r.Incoming+"; outgoing "+r.Outgoing+". Shared WOLF capacity is distinct from physical tanks. "+s.Reason)));
        var wolfOrders=state.WolfOrders.Where(o=>o.ColonyId==colony?.Id).Select(o=>new ColonyManagementRow("wolf-order:"+o.Id,"WOLF setup · "+o.Quote.Resource,o.State,Funds(o.Quote.Funds),"Paid module package",o.Reason+"; labor "+Number(o.WorkCompleted)+" / "+Number(o.Quote.LaborSeconds)+" s; "+o.Quote.BalancePolicy+"; before "+o.BeforeWitness+"; after "+o.AfterWitness));
        Add("inventory","Inventory & WOLF","Colony stock, physical tanks, reservations and WOLF allocations have separate authorities.",stock.Concat(physical).Concat(currentWolf).Concat(wolfOrders).Concat(wolf),["reviewWolfSupply","approveWolfSupply","cancelWolfSupply","reviewWolfReplan","replanPaidWolfSupply"],"No colony-owned stock or registered physical observations.",
            [new("Resource","WOLF capacity purpose","Power","Installed recipes determine real point dependencies.",wolfEnvironment?.Recipes.SelectMany(r=>r.Outputs).Select(r=>r.Resource).Distinct().Order().Select(r=>new ColonyManagementChoice(r,r)).ToArray()),new("DesiredAvailable","Desired unallocated WOLF points","5","1–1000 points; quote includes current depot allocation and paid dependency modules.")]);
        var trade = shipments.Select(s=>new ColonyManagementRow(s.Id,s.Kind+" · "+s.Resource,s.State,Units(s.Amount)+" · "+Funds(s.Funds),context,
            s.Reason+"; supplier "+s.SupplierId+"; departure UT "+Number(s.DepartUt)+"; arrival UT "+Number(s.ArrivalUt)+"; travel "+Number(s.TravelSeconds)+" s; freight "+Units(s.MassMicroTonnes)+" t / "+Number(s.VolumeMilliLiters/1000d)+" L. Cargo is distinct from available inventory.",new Dictionary<string,string>{{"Amount",Units(s.Amount)+" "+s.Resource},{"Cost",s.Funds.ToString("N0")},{"Arrival",s.State=="reserved" ? "Not dispatched" : Number(s.ArrivalUt)}}));
        var suppliers = state.Suppliers.Select(s=>new ColonyManagementRow("supplier:"+s.Id,SupplierName(s),"Supplier quote inputs",Units(s.Available)+" stock",context,
            "Reserved "+Units(s.Reserved)+"; price "+Funds(s.FundsPerUnit)+" / unit; freight "+Funds(s.FreightFunds)+"; lead time "+Number(s.TravelSeconds)+" s; concurrent capacity "+s.ConcurrentCapacity+"; mass capacity "+Units(s.MassCapacityMicroTonnes)+" t; volume capacity "+Number(s.VolumeCapacityMilliLiters/1000d)+" L.",new Dictionary<string,string>{{"Amount",Units(s.Available)+" stock"},{"Cost",s.FundsPerUnit.ToString("N0")+" / unit"},{"Arrival",Number(s.TravelSeconds)+" s lead time"}}));
        var reorders=state.ReorderPolicies.Where(p=>p.ColonyId==colony?.Id).Select(p=>new ColonyManagementRow("reorder:"+p.Resource,"Reserve policy · "+p.Resource,p.Enabled ? "Automatic paid imports" : "Disabled",Units(p.ReorderPoint)+" → "+Units(p.TargetAmount),context,p.Reason+"; next review UT "+Number(p.NextReviewUt)+"; cadence "+Number(p.CadenceSeconds)+" seconds. Shared cash floor, actual storage and finite supplier/freight limits are rechecked.",new Dictionary<string,string>{{"Amount",Units(p.ReorderPoint)+" → "+Units(p.TargetAmount)},{"Cost","Paid current quote"},{"Arrival",Number(p.NextReviewUt)+" next review"}}));
        Add("trade","Trade","Choose paid staging, import supplier and reserve policy. Cargo becomes stock only after verified delivery.",trade.Concat(suppliers).Concat(reorders),["reviewLogistics","activateLogistics","quoteTrade","reviewTrade","approveTrade","cancelTrade","configureReorderPolicy"],
            effects.FirstOrDefault(e=>e.State != "applied")?.Reason ?? "No transit shipments or suppliers. No automatic order readiness has been reported; inspect existing Delivery setup for fuel and Ore recovery blockers.",
            [new("PolicyId","Staging and freight service","","Purchase empty receiving stores before importing resource contents.",(economyPolicies ?? []).Where(p=>p.Body==colony?.Site.Body).Select(p=>new ColonyManagementChoice(p.Id,p.Provider+" · "+Funds(p.SetupFunds))).ToArray()),new("SupplierId","Resource supplier","","Stock, concurrent freight and lead time are rechecked.",state.Suppliers.Select(s=>new ColonyManagementChoice(s.Id,SupplierName(s)+" · "+Units(Math.Max(0,s.Available-s.Reserved))+" available")).ToArray()),new("Amount","Import amount (resource units)","","The authority validates receiving capacity and funds."),
             new("Resource","Automatic reserve resource","","Paid colony stock only; WOLF points remain a separate capacity ledger.",((colony?.Stock.Select(s=>s.Resource) ?? []).Concat(state.Suppliers.Select(s=>s.Resource))).Distinct().Order().Select(r=>new ColonyManagementChoice(r,r)).ToArray()),new("Enabled","Automatic reserve imports","false","Purchases only when the saved policy and actual guards permit it.",[new("false","Disabled"),new("true","Enabled")]),new("ReorderPoint","Reorder below (resource units)","0","Unreserved stock below this threshold triggers review."),new("TargetAmount","Refill target (resource units)","0","Includes outstanding deliveries and finite actual receiving capacity."),new("CadenceSeconds","Reserve review cadence (game seconds)","21600","At least one Kerbin day between policy reviews.")]);
        var maintenance = facilities.Select(f=>new ColonyManagementRow(f.Id,f.Name,f.LastReason,f.RequiredTrait.Length==0 ? "Worker requirement unknown" : f.RequiredTrait,f.Qualification.Context,
            Details(f)+" Inputs accessible "+f.Qualification.InputsAccessible+"; staffing qualified "+f.Qualification.StaffingQualified+"; background support "+f.Qualification.BackgroundSupported+". Service requires its own supported provider; module presence alone is insufficient."));
        var serviceTargets=servicesEnvironment?.Targets.Where(t=>t.ColonyId==colony?.Id).ToArray() ?? [];
        var tankRows=serviceTargets.Select(t=>new ColonyManagementRow("tank:"+t.FacilityId+"/"+t.PartId+"/"+t.DestinationResource,t.PartName+" · "+t.DestinationResource,t.Current && t.CanApply && t.QualifiedWorker ? "Service available" : "Held",Units(t.Amount)+" / "+Units(t.Capacity),t.Provider,"Part "+t.PartId+"; source "+t.SourceResource+"; "+t.Reason+" "+t.WorkerWitness));
        var operationRows=state.ServiceOperations.Where(o=>o.ColonyId==colony?.Id).Select(o=>new ColonyManagementRow("service-operation:"+o.Id,"Tank service · "+o.DestinationResource,o.State,Units(o.Amount),context,o.Reason+"; source "+o.SourceResource+"; before "+o.BeforeWitness+"; after "+o.AfterWitness));
        var utilityRows=(servicesEnvironment?.Utilities.Where(u=>facilities.Any(f=>f.Id==u.FacilityId)) ?? []).Select(u=>new ColonyManagementRow("utility:"+u.FacilityId,"Utilities · "+facilities.First(f=>f.Id==u.FacilityId).Name,ColonyUtilityQualification.PowerSupported(u) ? "Continuous power qualified" : "Power held",NullableNumber(u.ElectricCharge)+" EC",u.Context,u.Evidence+" "+u.Reason+" Recognized continuous generation "+NullableNumber(u.NominalGenerationEcPerSecond)+"; "+(u.FullDemandAccounted ? "complete current" : "partial recognized")+" demand bound "+NullableNumber(u.NominalDemandEcPerSecond)+" EC/s. Storage delta is not measured consumption."));
        var serviceLog = journal.Where(j=>j.Kind.IndexOf("service",StringComparison.OrdinalIgnoreCase)>=0).Select(j=>new ColonyManagementRow("journal:"+j.Sequence,j.Kind,"Recorded at UT "+Number(j.Ut),j.Resource.Length==0 ? "—" : Units(j.ResourceDelta)+" "+j.Resource,context,j.Detail+"; operation "+j.OperationId));
        Add("maintenance","Maintenance","Costed reserve-to-installed-tank service, actual Engineer workshop placement and physical utility evidence.",tankRows.Concat(operationRows).Concat(utilityRows).Concat(maintenance).Concat(serviceLog),["reviewService","serviceFacility","cancelService","configureServicePolicy"],"No registered service tanks or facilities.",
            [new("ServiceTank","Installed service tank","","One actual part tank; physical worker/provider eligibility is rechecked.",serviceTargets.Select((t,i)=>new ColonyManagementChoice(t.FacilityId+"/"+t.PartId+"/"+t.DestinationResource,(facilities.FirstOrDefault(f=>f.Id==t.FacilityId)?.Name ?? "Facility")+" · "+t.PartName+" · "+t.DestinationResource+" · tank "+(i+1))).ToArray()),
             new("Amount","Service amount (resource units)","","Debits paid colony stock once; protects support floors and existing reservations."),
             new("AutomaticEnabled","Recurring costed service","false","Default disabled; each delivery needs actual stock and physical qualified workers.",[new("false","Disabled"),new("true","Enabled")]),new("CadenceSeconds","Minimum cadence (game seconds)","21600","At least one Kerbin day; at most four new service operations per current tick."),new("TargetFillPercent","Refill tanks to (%)","95","1–100%; stops at actual tank capacity and accessible paid reserves.")]);
        var finance = journal.Select(j=>new ColonyManagementRow("journal:"+j.Sequence,j.Kind,"UT "+Number(j.Ut),Funds(j.FundsDelta),context,j.Detail+"; operation "+j.OperationId+"; resource delta "+Units(j.ResourceDelta)+" "+j.Resource,new Dictionary<string,string>{{"Operation",j.OperationId.Length==0 ? "Scheduled policy event" : "Recorded operation"}}));
        var commitments = effects.Select(e=>new ColonyManagementRow(e.Id,e.Kind,e.State,Funds(e.FundsDelta),context,e.Reason+"; provider "+e.Provider+"; before witness "+e.BeforeWitness+"; after witness "+e.AfterWitness+"; operation "+e.OperationId,new Dictionary<string,string>{{"Operation",e.State=="applied" ? "Settled" : "Awaiting settlement"}}));
        Add("finance","Finance & policy","Ledger entries and prepared effects distinguish spent funds, commitments and settlement. Unavailable forecasts remain unknown.",charterRows.Take(1).Concat(commitments).Concat(finance),["reviewCharter","updateCharter"],"No colony finance or policy records are selected.");
        IReadOnlyDictionary<string,string>? defaults = colony is null ? new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["Purpose"]="Settlement", ["Population"]="0", ["Budget"]="1000000",
            ["CashFloor"]="100000", ["SpendingLimit"]="1000000", ["ResidentLimit"]="24",
            ["VisitorLimit"]="24", ["ReserveDays"]="6", ["GrowthPolicy"]="Approval required"
        } : new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["Name"] = colony.Name, ["Body"] = colony.Site.Body, ["Biome"] = colony.Site.Biome,
            ["Latitude"] = colony.Site.Latitude.ToString("R",CultureInfo.InvariantCulture), ["Longitude"] = colony.Site.Longitude.ToString("R",CultureInfo.InvariantCulture),
            ["Purpose"] = colony.Charter.Purpose, ["Population"] = colony.Charter.PopulationTarget.ToString(CultureInfo.InvariantCulture),
            ["Budget"] = colony.Charter.FoundingBudget.ToString(CultureInfo.InvariantCulture), ["CashFloor"] = colony.Charter.CashFloor.ToString(CultureInfo.InvariantCulture),
            ["SpendingLimit"] = colony.Charter.SpendingLimit.ToString(CultureInfo.InvariantCulture), ["ResidentLimit"] = colony.Charter.ResidentLimit.ToString(CultureInfo.InvariantCulture),
            ["VisitorLimit"] = colony.Charter.VisitorLimit.ToString(CultureInfo.InvariantCulture), ["ReserveDays"] = colony.Charter.ReserveDays.ToString("R",CultureInfo.InvariantCulture),
            ["GrowthPolicy"] = colony.Charter.GrowthPolicy
        };
        return new(contextKey,status,reason,colony?.Id,state.Revision,choices,sections.ToArray(),DraftDefaults:defaults);
    }
    private static string Units(long value) => (value / (decimal)ColonyLimits.Units).ToString("N6",CultureInfo.CurrentCulture).TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray());
    internal const string ConnectedReason="Selected-save colony authority; effects persist at the next KSP save boundary.";
    internal const string PlacementMembershipHold="Placed part/flight membership is missing, duplicated, merged or changed";
    internal static string DisplayReason(string reason)
    {
        if(reason==ConnectedReason)return "Changes are saved when you save in KSP.";
        const string prefix="Reconcile external effect ";
        if(reason==PlacementMembershipHold || reason.StartsWith(prefix,StringComparison.Ordinal) &&
            reason.Length==prefix.Length+36+2+PlacementMembershipHold.Length &&
            Guid.TryParseExact(reason.Substring(prefix.Length,36),"D",out _) &&
            reason.Substring(prefix.Length+36)==": "+PlacementMembershipHold)
            return "A placed building’s parts could not be verified. See Details for the full error.";
        return reason;
    }
    private static string Number(double value) => value.ToString("N2",CultureInfo.CurrentCulture);
    private static string NullableNumber(double? value) => value.HasValue ? Number(value.Value) : "unknown";
    private static string Funds(long value) => value.ToString("N0",CultureInfo.CurrentCulture)+" funds";
    private static string Flag(bool? value) => value.HasValue ? value.Value ? "on" : "off" : "unknown";
    private static string Materials(IEnumerable<MaterialRequirement> rows) => string.Join(", ",rows.Select(r=>Units(r.Amount)+" "+r.Resource));
    private static double NormalizeLongitude(double value)=>(value+540)%360-180;
}
