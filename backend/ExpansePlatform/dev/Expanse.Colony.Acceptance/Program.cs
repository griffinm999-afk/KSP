using System.Globalization;
using System.Text.Json;
using Expanse.Colony.Acceptance;
using Expanse.Domain.Colonies;

// Offline by construction: this executable has no IPC/game/control dependency.
// A review writes one exact command for the separately guarded RuntimeDriver.
var json=new JsonSerializerOptions {WriteIndented=true};
try
{
    if(args.Length==1&&args[0]=="--self-check"){AcceptanceSelfChecks.Run();return 0;}
    var o=new Dictionary<string,string>(StringComparer.Ordinal);
    for(int i=0;i<args.Length;i+=2)
    {if(!args[i].StartsWith("--",StringComparison.Ordinal)||i+1==args.Length||!o.TryAdd(args[i],args[i+1]))throw new ArgumentException("Use --mode inspect|review|assert --snapshot file --colony id --output file [--action ...] [--baseline file].");}
    string Get(string k,string? fallback=null)=>o.TryGetValue("--"+k,out var v)?v:fallback??throw new ArgumentException("Missing --"+k);
    long Micro(string k) {decimal n=decimal.Parse(Get(k),CultureInfo.InvariantCulture);decimal scaled=n*ColonyLimits.Units;if(n<=0||scaled!=decimal.Truncate(scaled)||scaled>long.MaxValue)throw new ArgumentException("--"+k+" needs positive ordinary units with at most six decimals.");return(long)scaled;}
    uint Part()=>uint.Parse(Get("part"),CultureInfo.InvariantCulture);
    ColonyFoundingIntent? Intent(bool required=false)
    {
        if(!o.TryGetValue("--intent",out string? path)){if(required)throw new ArgumentException("Missing --intent: review exact named inhabitants, startup policies and production choice.");return null;}
        return AcceptanceInput.ReadIntent(path);
    }
    ColonyManagementSnapshot Read(string path)
    =>AcceptanceInput.ReadSnapshot(path);
    var snapshot=Read(Get("snapshot"));var state=snapshot.State??throw new InvalidDataException("Recorded snapshot lacks state.");
    string id=Get("colony");var colony=state.Colonies.Single(c=>c.Id==id);var env=AcceptanceChecks.Environment(snapshot);
    string mode=Get("mode"),output=Path.GetFullPath(Get("output"));object report;
    if(File.Exists(output))throw new InvalidDataException("Evidence files are immutable; choose a new --output path.");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    if(mode=="inspect")
    {
        var intent=Intent();var founding=ColonyEngine.QuoteFoundingPlan(state,id,env,intent);var growth=ColonyEngine.QuoteGrowthPlan(state,id,env);
        var ordinary=snapshot.People.Roster.Where(p=>p.Current&&p.Type=="Crew"&&!p.ProtectedMissionCrew&&colony.VisitorRosterIds.Contains(p.RosterId)).ToArray();
        report=new {Source=Path.GetFullPath(Get("snapshot")),snapshot.ContextKey,state.WorldId,state.Revision,snapshot.ObservedUt,snapshot.Status,snapshot.Reason,
            Colony=new{colony.Id,colony.Name,colony.Site,colony.Charter,colony.Status,colony.SupportStatus,colony.Logistics.State,People=colony.Residents.Count+colony.VisitorRosterIds.Count,OrdinaryVisitors=ordinary.Length,OrdinaryEngineers=ordinary.Count(p=>p.Trait=="Engineer"),
                Facilities=colony.Facilities.Select(f=>new{f.Id,f.Name,f.State,f.TemplateId,f.CertifiedHomes,f.RequiredWorkers,f.RequiredTrait,f.PartIds,f.LastReason}),colony.Stock},
            RequestedFoundingIntent=intent,Founding=founding,CompleteStartupGaps=AcceptanceChecks.CompleteStartupGaps(founding,colony),Growth=growth,Forecast=ColonyEngine.Forecast(state,id,env),Production=AcceptanceChecks.ProductionReadiness(snapshot,id),
            Physical=snapshot.Planning.LocalStocks.Where(s=>s.ColonyId==id),Services=snapshot.Services.Targets.Where(s=>s.ColonyId==id),
            Workplaces=snapshot.People.Seats.Where(s=>s.WorkSupported&&colony.Facilities.Any(f=>f.Id==s.FacilityId)).Select(s=>new{s.FacilityId,s.VesselId,s.PartId,s.Capacity,Occupied=s.Occupants.Count,s.SurfaceTransferSupported,s.CrewMutationSupported,s.Current})};
    }
    else if(mode=="review")
    {
        if(!snapshot.DevelopmentMode)throw new InvalidDataException("Acceptance commands require a recorded authorized development snapshot.");
        var command=new ColonyCommand {OperationId=Guid.NewGuid().ToString("D"),ContextKey=snapshot.ContextKey,ExpectedRevision=state.Revision,ColonyId=id};object? quote=null;ColonyFoundingIntent? requestedIntent=null;
        string action=Get("action");
        switch(action)
        {
            case "founding":case "growth":
                ColonyFoundingIntent? intent=null;
                if(action=="founding")
                {
                    intent=Intent(true);requestedIntent=intent;
                }
                var plan=action=="founding"?ColonyEngine.QuoteFoundingPlan(state,id,env,intent):ColonyEngine.QuoteGrowthPlan(state,id,env);
                quote=plan;command.Kind=action=="founding"?"approveFoundingPlan":"approveGrowthPlan";command.QuoteId=plan.Id;break;
            case "surveyFounding":case "surveyGrowth":
                command.Kind=action=="surveyFounding"?"surveyFoundingPlan":"surveyGrowthPlan";
                if(action=="surveyFounding"){requestedIntent=Intent();command.FoundingIntent=requestedIntent;}
                // Runtime alone supplies real surveyed plots. A pure domain dry
                // run cannot pretend that native terrain evidence already exists.
                break;
            case "worker":
                var worker=ColonyEngine.QuoteWorkerTransfer(state,id,Get("roster"),Get("facility"),Part(),env);
                quote=worker;command.Kind="transferColonyWorker";command.TargetId=worker.RosterId;command.QuoteId=worker.Id;
                command.Fields=new(){["JobFacilityId"]=worker.JobFacilityId,["WorkPartId"]=worker.WorkPartId.ToString(CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"};break;
            case "recruit":
                var route=snapshot.People.Routes.Single(r=>r.Id==Get("route"));var person=snapshot.People.Roster.Single(r=>r.RosterId==Get("roster"));
                command.Kind="recruitResident";command.Fields=new(){["RosterId"]=person.RosterId,["HomeFacilityId"]=Get("facility"),["HomePartId"]=Part().ToString(CultureInfo.InvariantCulture),["RouteId"]=route.Id,["RouteHash"]=route.Hash,["QuotedFunds"]=checked(route.Fare+(person.Type=="Applicant"?route.RecruitmentFee:0)).ToString(CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"};quote=route;break;
            case "assign":
                command.Kind="assignResident";command.Fields=new(){["RosterId"]=Get("roster"),["HomeFacilityId"]=Get("facility"),["HomePartId"]=Part().ToString(CultureInfo.InvariantCulture),["ExplicitMissionCrewAssignment"]="false"};break;
            case "support":
                command.Kind="commissionSupport";command.Fields=new(){["PolicyId"]=env.Support.PolicyId,["PolicyHash"]=env.Support.PolicyHash,["QuotedReserveMicroUnits"]=ColonyEngine.SupportReserveQuote(colony,env).ToString(CultureInfo.InvariantCulture)};break;
            case "reorder":
                command.Kind="configureReorderPolicy";command.Fields=new(){["Resource"]=Get("resource"),["Enabled"]=Get("enabled","true"),["ReorderPointMicroUnits"]=Micro("point").ToString(CultureInfo.InvariantCulture),["TargetMicroUnits"]=Micro("target").ToString(CultureInfo.InvariantCulture),["CadenceSeconds"]=Get("cadence","21600")};break;
            case "physicalPolicy":command.Kind="configurePhysicalProcurement";command.Fields=new(){["Enabled"]=Get("enabled","true")};break;
            case "physical":
                var transfer=ColonyEngine.QuotePhysicalTransfer(state,id,Get("local-stock"),Get("direction"),Micro("amount"),env);
                command.Kind="transferColonyStock";command.QuoteId=transfer.Id;command.TargetId=transfer.LocalStockId;
                command.Fields=new(){["Direction"]=transfer.Direction,["AmountMicroUnits"]=transfer.Amount.ToString(CultureInfo.InvariantCulture)};quote=transfer;break;
            case "service":
                var target=snapshot.Services.Targets.Single(t=>t.ColonyId==id&&t.FacilityId==Get("facility")&&t.PartId==Part()&&t.DestinationResource==Get("resource"));
                command.Kind="serviceFacility";command.TargetId=target.FacilityId;command.Fields=new(){["PartId"]=target.PartId.ToString(CultureInfo.InvariantCulture),["DestinationResource"]=target.DestinationResource,["ServiceQuoteHash"]=target.QuoteHash,["AmountMicroUnits"]=Micro("amount").ToString(CultureInfo.InvariantCulture)};quote=target;break;
            default:throw new ArgumentException("Unknown reviewed action.");
        }
        if(quote is ColonyPlanningQuote reviewed && action=="founding")command.FoundingIntent=reviewed.FoundingIntent;
        bool nativeSurvey=action is "surveyFounding" or "surveyGrowth";
        var dry=nativeSurvey?null:ColonyEngine.Execute(state,command,env);
        bool capability=snapshot.Capabilities.Any(c=>c.Kind==command.Kind&&c.Available&&(c.ColonyId.Length==0||c.ColonyId==id)&&(c.TargetId.Length==0||c.TargetId==command.TargetId));
        bool completeStartup=bool.Parse(Get("require-complete-startup","false"));
        if(completeStartup&&action!="founding")throw new ArgumentException("--require-complete-startup applies only to the founding review.");
        var completeGaps=quote is ColonyPlanningQuote q&&action=="founding"?AcceptanceChecks.CompleteStartupGaps(q,colony):new List<string>();
        bool canSubmit=capability&&(nativeSurvey||dry?.Outcome=="accepted")&&(!completeStartup||completeGaps.Count==0);
        string commandPath=output+".command.json";
        if(canSubmit){if(File.Exists(commandPath))throw new InvalidDataException("Command file already exists.");File.WriteAllText(commandPath,JsonSerializer.Serialize(command,json));}
        report=new{Action=action,CanSubmit=canSubmit,RequireCompleteStartup=completeStartup,CompleteStartupGaps=completeGaps,DryRunOutcome=dry?.Outcome??"native survey prerequisite only",Reason=completeStartup&&completeGaps.Count>0?"The exact review does not yet cover the full startup acceptance scope: "+string.Join(" ",completeGaps):!capability?"Current recorded runtime does not declare this exact action available; obtain a fresh ready snapshot and resolve the runtime hold.":dry?.Reason??"Runtime must obtain current terrain evidence; no fake surveyed plots are inserted.",RequestedFoundingIntent=requestedIntent,Quote=quote,Production=AcceptanceChecks.ProductionReadiness(snapshot,id),Command=command,CommandFile=canSubmit?commandPath:null,
            Reminder="This is a review, not submission or proof. Use the exact immutable command file with verified isolated RuntimeDriver; ambiguous replies require retry of this same operation ID."};
    }
    else if(mode=="assert")
    {
        var before=Read(Get("baseline"));string action=Get("action");
        switch(action)
        {case "preserved":AcceptanceChecks.Preserved(before,snapshot,id);break;case "founding":AcceptanceChecks.Founding(before,snapshot,id);break;case "arrivals":AcceptanceChecks.Arrivals(before,snapshot,id);break;case "support3days":AcceptanceChecks.SupportDays(before,snapshot,id);break;case "physical":AcceptanceChecks.Physical(before,snapshot,id,Get("operation"));break;case "growth":AcceptanceChecks.Growth(before,snapshot,id);break;case "production":AcceptanceChecks.Production(before,snapshot,id);break;case "productionOperations":AcceptanceChecks.ProductionOperations(before,snapshot,id);break;default:throw new ArgumentException("Unknown acceptance assertion.");}
        report=new{Action=action,Passed=true,Before=Path.GetFullPath(Get("baseline")),After=Path.GetFullPath(Get("snapshot")),ColonyId=id,WorldId=state.WorldId,Scope="Recorded management witnesses; native save/cold-load, scene and provider-context proof must be archived separately."};
    }
    else throw new ArgumentException("Unknown --mode.");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllText(output,JsonSerializer.Serialize(report,json));Console.WriteLine(output);return 0;
}
catch(Exception ex){Console.Error.WriteLine("Acceptance preparation/check failed: "+ex.Message);return 2;}
