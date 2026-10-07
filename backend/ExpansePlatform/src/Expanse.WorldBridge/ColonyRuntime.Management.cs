using System;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private ColonyManagementEndpoint managementEndpoint;
        private bool managementEndpointFailed;
        partial void PumpManagementEndpoint()
        {
            if (managementEndpointFailed) return;
            if (managementEndpoint == null)
            {
                try
                {
                    string pipe=ColonyManagementWire.PipeForInstallation(Path.GetFullPath(KSPUtil.ApplicationRootPath));
                    const string prefix="-expanseColonyPipe=";
                    var overrides=Environment.GetCommandLineArgs().Where(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (overrides.Length>1) throw new ArgumentException("Multiple colony pipe overrides are not allowed.");
                    if (overrides.Length==1) { pipe=overrides[0].Substring(prefix.Length); ColonyManagementWire.ValidatePipeName(pipe); }
                    managementEndpoint=new ColonyManagementEndpoint(pipe,CaptureManagement,
                        command=>Current==this ? Submit(command) : new ColonyResult {State=GetStateCopy(),Outcome="rejected",Reason="This colony runtime was superseded by a new scene/load context."}, CaptureSettlements);
                    managementEndpoint.Start();
                    managementEndpoint.Diagnostic=message=>Debug.LogWarning("[ExpanseColony] "+message);
                    Debug.Log("[ExpanseColony] Local management endpoint workers started: "+pipe);
                }
                catch (Exception ex) { managementEndpointFailed=true; Debug.LogError("[ExpanseColony] Management endpoint unavailable: "+ex.Message); return; }
            }
            managementEndpoint.Pump(4);
            ColonyMaintenanceWindow.Capture=CaptureMaintenance;
            ColonyMaintenanceWindow.SubmitService=SubmitMaintenanceService;
        }
        partial void StopManagementEndpoint()
        {
            if (managementEndpoint!=null) { managementEndpoint.Dispose(); managementEndpoint=null; }
            if(ColonyMaintenanceWindow.Capture==(Func<ColonyMaintenanceSnapshot>)CaptureMaintenance)ColonyMaintenanceWindow.Capture=null;
            if(ColonyMaintenanceWindow.SubmitService==(Func<ColonyMaintenanceServiceRequest,ColonyMaintenanceServiceResult>)SubmitMaintenanceService)ColonyMaintenanceWindow.SubmitService=null;
        }
        private ColonyManagementSnapshot CaptureManagement()
        {
            var result=new ColonyManagementSnapshot { ContextKey=ContextKey,Status=Ready ? "Save authority connected" : "Held",Reason=HoldReason ?? "Selected-save colony authority; effects persist at the next KSP save boundary.",State=GetStateCopy() };
            if (!Ready || Current!=this) {result.Status="Held";result.Reason=HoldReason ?? "Waiting for the current selected-save runtime.";return result;}
            var env=GetEnvironment();
            result.ObservedUt=env.Ut;
            result.AvailableFunds=HighLogic.CurrentGame!=null && HighLogic.CurrentGame.Mode==Game.Modes.CAREER && Funding.Instance!=null ? (long?)env.AvailableFunds : null;
            result.Templates=env.Templates;
            result.ConstructionRecovery=env.ConstructionRecovery.Values.ToList();
            // Includes registered native observations for explicit adopted-home review; adoption itself filters already-owned candidates.
            result.AdoptableFacilities=env.AdoptableFacilities.ToList();
            result.FacilitySites=env.FacilitySites;
            result.BodyRadiiMeters=env.BodyRadiiMeters;
            result.People=env.People;
            result.Services=env.Services;
            result.Support=env.Support;
            result.Planning=env.Planning;
            result.Production=env.Production;
            result.EconomyPolicies=env.EconomyPolicies;result.Wolf=env.Wolf;result.UnlockedTech=env.UnlockedTech.ToList();result.DevelopmentMode=env.DevelopmentMode;
            result.Capabilities.Add(new ColonyManagementCapability { Kind="foundColony",Label="Register founding charter",Available=true,Reason="Registers identity and charter; commissioning and certified building construction remain separate operations." });
            result.Capabilities.Add(new ColonyManagementCapability { Kind="updateCharter",Label="Apply charter limits",Available=true,Reason="Rechecks existing people and financial obligations before applying." });
            foreach (var colony in result.State.Colonies)
            {
                bool planning=env.Planning.Roles.Count>0;
                AddAdoptionCapabilities(result,env,colony);
                AddGrowthProposalCapabilities(result,env,colony);
                result.Capabilities.Add(new ColonyManagementCapability {Kind="approveFoundingPlan",ColonyId=colony.Id,Label="Approve reviewed startup plan",Available=planning && !colony.SupportCommissionedUt.HasValue,Reason="Reviews complete package roles, exact plots, paid imports and support reserve before any commitment."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="approveGrowthPlan",ColonyId=colony.Id,Label="Approve reviewed expansion",Available=planning && colony.SupportCommissionedUt.HasValue && !ColonyEngine.HasGrowthDecision(result.State,colony.Id,env),Reason=ColonyEngine.HasGrowthDecision(result.State,colony.Id,env) ? "Select the exact expansion review and use its approve, defer, reject or reconsider controls." : "Requires actual housing pressure, qualified open jobs and a funded delayed-import downside."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="surveyFoundingPlan",ColonyId=colony.Id,Label="Survey startup street",Available=planning && HighLogic.LoadedSceneIsFlight && !colony.SupportCommissionedUt.HasValue,Reason="Surveys actual bounded terrain footprints for the planned packages; no client-invented clear plots."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="surveyGrowthPlan",ColonyId=colony.Id,Label="Survey expansion plot",Available=planning && HighLogic.LoadedSceneIsFlight && colony.SupportCommissionedUt.HasValue,Reason="Rechecks actual proposed expansion footprint and obstacles."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="configureReorderPolicy",ColonyId=colony.Id,Label="Apply reserve reorder policy",Available=true,Reason="Orders paid finite imports only within actual receiving capacity, supplier availability and shared cash floor."});
                var localStocks=env.Planning.LocalStocks.Where(s=>s.ColonyId==colony.Id).ToArray();
                result.Capabilities.Add(new ColonyManagementCapability {Kind="transferColonyStock",ColonyId=colony.Id,Label="Approve reviewed physical transfer",Available=localStocks.Any(s=>s.Current && s.CanApply),Reason=localStocks.FirstOrDefault(s=>!s.CanApply)?.Reason ?? "Requires actual registered warehouse, resource flow, range, exact tank provider and conserved before/after amounts."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="configurePhysicalProcurement",ColonyId=colony.Id,Label="Apply local procurement policy",Available=true,Reason="Reviews local shortage sourcing before imports; native input fill requires explicit enablement and stays below 50% actual capacity. Service resources retain qualified-worker controls."});
                foreach(var transfer in result.State.PhysicalTransfers.Where(p=>p.ColonyId==colony.Id && p.State=="reserved" && !p.OwnedSourceDebited))result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelPhysicalTransfer",ColonyId=colony.Id,TargetId=transfer.Id,Label="Cancel unstarted physical transfer",Available=true,Reason="Releases only unstarted owned stock/capacity reservations; no speculative refund after physical mutation."});
                foreach(var plan in result.State.Plans.Where(p=>p.ColonyId==colony.Id && p.State!="cancelled" && p.State!="complete"))result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelColonyPlan",ColonyId=colony.Id,TargetId=plan.Id,Label="Cancel future plan commitments",Available=true,Reason="Releases unstarted future claims; dispatched imports and paid construction remain their real independent obligations."});
                var targets=env.Services.Targets.Where(t=>t.ColonyId==colony.Id).ToArray();
                bool logistics=colony.Logistics.State=="none" && colony.Stock.Count==0 && env.EconomyPolicies.Any(p=>p.Body==colony.Site.Body);
                result.Capabilities.Add(new ColonyManagementCapability {Kind="activateLogistics",ColonyId=colony.Id,Label="Approve paid staging contract",Available=logistics,Reason=logistics ? "Pays for finite empty staging capacity; inventory is purchased separately after actual modeled delivery time." : colony.Logistics.Reason});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="surveyPlot",ColonyId=colony.Id,Label="Survey building plot",Available=HighLogic.LoadedSceneIsFlight && env.Templates.Count>0,Reason="Authority surveys actual loaded terrain, footprint, obstacles and registered boundary for the selected package."});
                bool build=env.Templates.Any(t=>(t.RuntimeCertified || env.DevelopmentMode && colony.Charter.Sandbox) && colony.Plots.Any(p=>p.SurveyHash.Length>0 && p.ReservedBy.Length==0 && p.OccupiedBy.Length==0 && p.TemplateId==t.Id && p.TemplateHash==t.Hash));
                result.Capabilities.Add(new ColonyManagementCapability {Kind="approveConstruction",ColonyId=colony.Id,Label="Approve reviewed building",Available=build,Reason=build ? "Reserves current paid materials and funds; actual placement and commissioning remain separate witnessed stages." : "Survey a clear plot for an unlocked certified package; isolated sandbox packages require exact development authorization."});
                foreach(var order in result.State.Construction.Where(o=>o.ColonyId==colony.Id && o.State!="cancelled" && o.State!="operational" && o.State!="placing" && o.State!="commissioning" && o.FacilityId.Length==0 && o.Placement.OperationId.Length==0 && !o.Placement.AssemblyAttempted))result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelConstruction",ColonyId=colony.Id,TargetId=order.Id,Label="Cancel unplaced construction",Available=true,Reason="Releases unconsumed materials and only verified unspent escrow; physical placement cannot be removed through this action."});
                foreach(var order in result.State.Construction.Where(o=>o.ColonyId==colony.Id && o.State=="held" && o.Placement.OperationId.Length>0))
                {
                    string retryReason; bool retry=ColonyEngine.CanRetryConstructionPlacement(result.State,order.Id,env,out retryReason);
                    result.Capabilities.Add(new ColonyManagementCapability {Kind="retryConstructionPlacement",ColonyId=colony.Id,TargetId=order.Id,Label="Retry original unattempted placement",Available=retry,Reason=Bound(retryReason,512)});
                }
                bool import=result.State.Suppliers.Any(s=>(s.DestinationBody.Length==0 || s.DestinationBody==colony.Site.Body) && s.Available>s.Reserved && colony.Stock.Any(t=>t.Resource==s.Resource && t.Capacity>t.Amount+t.IncomingReserved));
                result.Capabilities.Add(new ColonyManagementCapability {Kind="approveTrade",ColonyId=colony.Id,Label="Approve reviewed import",Available=import,Reason=import ? "Rechecks finite supplier stock, paid freight capacity, destination storage, cash floor and existing plan claims." : "Complete paid receiving stores and select a stocked supplier serving this body."});
                foreach(var shipment in result.State.Shipments.Where(s=>s.ColonyId==colony.Id && s.State=="reserved"))result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelTrade",ColonyId=colony.Id,TargetId=shipment.Id,Label="Cancel unstarted import",Available=true,Reason="Only reserved undispatched cargo can be cancelled; approved plan claims must be released first."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="approveWolfSupply",ColonyId=colony.Id,Label="Approve costed WOLF setup",Available=env.Wolf.Ready,Reason=env.Wolf.Reason});
                foreach(var wolf in result.State.WolfOrders.Where(o=>o.ColonyId==colony.Id))
                {
                    if(wolf.State=="reserved" && !wolf.FundsPaid)result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelWolfSupply",ColonyId=colony.Id,TargetId=wolf.Id,Label="Cancel unpaid WOLF setup",Available=true,Reason="Cancels only an unstarted paid-package reservation; no WOLF capacity is granted."});
                    if(ColonyEngine.CanReplanPaidWolfSupply(result.State,wolf.Id))result.Capabilities.Add(new ColonyManagementCapability {Kind="replanPaidWolfSupply",ColonyId=colony.Id,TargetId=wolf.Id,Label="Apply reviewed paid-package replan",Available=env.Wolf.Ready,Reason="Preserves the same paid module package; fresh depot before/after allocation is reviewed with zero additional purchase."});
                }
                result.Capabilities.Add(new ColonyManagementCapability {Kind="serviceFacility",ColonyId=colony.Id,Label="Reserve reviewed tank service",Available=targets.Any(t=>t.Current && t.CanApply && t.QualifiedWorker),Reason=targets.FirstOrDefault(t=>!t.CanApply)?.Reason ?? "Rechecks paid stock, actual qualified workshop occupant, destination capacity and physical provider."});
                result.Capabilities.Add(new ColonyManagementCapability {Kind="configureServicePolicy",ColonyId=colony.Id,Label="Apply recurring service policy",Available=true,Reason="Default disabled; recurring attempts require real owned stock, qualified physical crew and exact tank provider."});
                foreach(var service in result.State.ServiceOperations.Where(o=>o.ColonyId==colony.Id && o.State=="reserved" && !o.SourceDebited))
                    result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelService",ColonyId=colony.Id,TargetId=service.Id,Label="Cancel unstarted tank service",Available=true,Reason="Releases only an unstarted owned-stock reservation; no speculative refund after physical apply."});
                bool home=env.People.Seats.Any(s=>s.Current && s.HousingCertified && colony.Facilities.Any(f=>f.Id==s.FacilityId));
                bool support=false;string supportReason=env.Support.Reason;
                if(!colony.SupportCommissionedUt.HasValue)
                {
                    try
                    {
                        long reserve=ColonyEngine.SupportReserveQuote(colony,env);
                        int homes=env.People.Seats.Where(s=>s.Current && s.HousingCertified && s.UtilitiesQualified && s.ContextKey==ContextKey && colony.Facilities.Any(f=>f.Id==s.FacilityId)).Sum(s=>s.Capacity);
                        var supplies=colony.Stock.SingleOrDefault(s=>s.Resource=="Supplies");support=homes>=colony.Charter.PopulationTarget && supplies!=null && supplies.Amount-supplies.Reserved>=reserve;
                        supportReason=homes<colony.Charter.PopulationTarget ? "There are not enough ready home spaces for the resident target." : supplies==null || supplies.Amount-supplies.Reserved<reserve ? "Buy enough available Supplies for everyone and the reserve target." : "Available Supplies, certified homes, utilities, and the life-support system are checked before support starts.";
                    }
                    catch(Exception ex){supportReason=Bound(ex.Message,512);}
                }
                else supportReason="Resident support is already active; its start time and spent Supplies cannot be reset.";
                result.Capabilities.Add(new ColonyManagementCapability {Kind="commissionSupport",ColonyId=colony.Id,Label="Start resident support",Available=support,Reason=supportReason});
                bool route=env.People.Routes.Any(r=>r.Body==colony.Site.Body && r.Qualified && (!r.DevelopmentOnly || env.DevelopmentMode));
                string blockers=!home ? "No operational certified physical home parts; qualify the housing package and its actual seat mapping first. Work seats do not become homes." : !colony.SupportCommissionedUt.HasValue ? "Commission funded resident support before immigration." : !route ? "No qualified configured paid passenger route serves this body." : "Rechecks actual roster identity, physical home seats, support reserves, current route quote and shared funds.";
                result.Capabilities.Add(new ColonyManagementCapability { Kind="recruitResident",ColonyId=colony.Id,Label="Recruit resident",Available=home && route && colony.SupportCommissionedUt.HasValue,Reason=blockers });
                bool residentAdmission=colony.SupportCommissionedUt.HasValue || colony.Residents.Any(r=>r.Status!="missing");
                result.Capabilities.Add(new ColonyManagementCapability { Kind="assignResident",ColonyId=colony.Id,Label="Assign home or job",Available=home && env.People.PresenceComplete && residentAdmission,Reason=!residentAdmission ? "Commission resident support before designating new residents." : home ? "New residents require commissioned support and space within the population target. Existing residents can be reassigned; actual certified homes and qualified work parts remain required. Protected astronauts need explicit reassignment." : blockers });
                result.Capabilities.Add(new ColonyManagementCapability {Kind="transferColonyWorker",ColonyId=colony.Id,Label="Approve reviewed worker shift",Available=env.People.PresenceComplete && env.People.Seats.Any(s=>s.Current && s.SurfaceTransferSupported && s.WorkSupported && s.Occupants.Count<s.Capacity && colony.Facilities.Any(f=>f.Id==s.FacilityId && (f.State=="commissioning" || f.State=="operational"))),Reason="Moves current ordinary nearby colony crew into an actual workplace; preserves visitor/resident status and homes. Both loaded landed cabins must allow transfer, within 200 metres."});
                foreach(var shift in result.State.PeopleOperations.Where(p=>p.ColonyId==colony.Id && p.Kind=="workerTransfer" && p.State=="reserved"))result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelWorkerTransfer",ColonyId=colony.Id,TargetId=shift.Id,Label="Cancel unstarted worker shift",Available=true,Reason="Only releases an unstarted physical transfer; actual crew and residency remain unchanged."});
                foreach (var resident in colony.Residents)
                    result.Capabilities.Add(new ColonyManagementCapability { Kind="departResident",ColonyId=colony.Id,TargetId=resident.Id,Label="Approve paid departure",Available=route && resident.Status=="resident",Reason=route ? "Reserves finite paid transport; preserves actual Kerbal until source release is verified." : "No qualified configured paid passenger route serves this body." });
                foreach (var passenger in result.State.PeopleOperations.Where(p=>p.ColonyId==colony.Id && p.Kind!="workerTransfer" && p.State=="reserved" && !p.FundsPaid))
                    result.Capabilities.Add(new ColonyManagementCapability {Kind="cancelPassenger",ColonyId=colony.Id,TargetId=passenger.Id,Label="Cancel unpaid passenger reservation",Available=true,Reason="Releases only the unpaid local reservation. Actual named Kerbal remains in the KSP roster; no funds are refunded."});
            }
            ColonyManagementCapabilityHolds.Apply(result,env);
            return result;
        }
        private ColonyMaintenanceSnapshot CaptureMaintenance()
        {
            var value=GetStateCopy();
            var result=new ColonyMaintenanceSnapshot { ContextKey=ContextKey,Revision=value==null ? 0 : value.Revision,Status=Ready ? "Save authority connected" : "Held",Reason=HoldReason ?? "Registered facilities only; service eligibility requires a qualified provider." };
            if (value==null) return result;
            var active=FlightGlobals.ActiveVessel;
            var colony=value.Colonies.FirstOrDefault(c=>active!=null && c.Facilities.Any(f=>f.VesselId==active.id.ToString("D")));
            if (colony==null) { result.Reason="Active vessel is not an adopted member of a registered colony. Select a member vessel for maintenance inspection."; return result; }
            var facilities=colony.Facilities;
            var env=GetEnvironment();
            result.Sections["Facilities"]=facilities.Select(f=>new ColonyMaintenanceRow { Id=f.Id,Name=f.Name,State=f.State,Quantity=f.CertifiedHomes+" certified homes",Basis=f.Qualification.Context,Detail="Vessel "+f.VesselId+"; owner "+f.ProductionOwner+"; "+f.LastReason,ServiceReason="No qualified service provider is connected." }).ToArray();
            result.Sections["Machinery"]=colony.Stock.Where(s=>s.Resource=="Machinery").Select(s=>new ColonyMaintenanceRow { Id="stock:"+s.Resource,Name=s.Resource,State="Colony-owned reserve",Quantity=(s.Amount/(decimal)ColonyLimits.Units).ToString("0.######"),Basis="Save authority",Detail="Reserved "+(s.Reserved/(decimal)ColonyLimits.Units)+"; support floor "+(s.SupportFloor/(decimal)ColonyLimits.Units)+". Installed physical machinery is distinct.",ServiceReason="A real debit/credit service adapter is required." }).ToArray();
            var serviceRows=env.Services.Targets.Where(t=>t.ColonyId==colony.Id).Select(t=>MaintenanceTankRow(t,colony)).ToArray();
            result.Sections["Machinery"]=serviceRows.Where(r=>r.State=="Machinery").Concat(result.Sections["Machinery"]).ToArray();
            result.Sections["Staffing"]=colony.Residents.Select(r=>new ColonyMaintenanceRow { Id=r.Id,Name=r.Name,State=r.Trait,Quantity=r.PhysicallyAtWork ? "Physical work qualified" : "No physical work witness",Basis="Save authority",Detail="Roster "+r.RosterId+"; home "+r.HomeFacilityId+"; job "+r.JobFacilityId+"; part "+r.WorkPartId+". A job title alone grants no module bonus.",ServiceReason="Service staffing is rechecked by the provider." }).ToArray();
            result.Sections["Power"]=facilities.Select(f=>new ColonyMaintenanceRow { Id=f.Id,Name=f.Name,State=f.Qualification.EvidenceHash.Length==0 ? "Qualification unknown" : f.Qualification.PowerReliable ? "Qualified power" : "Unqualified power",Quantity="Live EC rate unknown",Basis=f.Qualification.Context,Detail="Heat qualification "+f.Qualification.HeatSafe+"; evidence "+f.Qualification.EvidenceHash+". Batteries do not prove a connected grid.",ServiceReason="Power/service provider is unavailable." }).ToArray();
            result.Sections["Power"]=env.Services.Utilities.Where(u=>facilities.Any(f=>f.Id==u.FacilityId)).Select(u=>new ColonyMaintenanceRow {Id="utility:"+u.FacilityId,Name=facilities.First(f=>f.Id==u.FacilityId).Name,State=ColonyUtilityQualification.PowerSupported(u) ? "Continuous source qualified" : "Power held",Quantity=(u.ElectricCharge.HasValue ? u.ElectricCharge.Value.ToString("0.##")+" EC" : "EC unavailable"),Basis=u.Context,Detail=u.Evidence+" "+u.Reason+" Demand is a conservative module bound; no complete measured EC throughput is claimed.",ServiceReason="Utilities are qualified from actual same-owner hardware; no arbitrary module service action."}).Concat(serviceRows.Where(r=>r.State=="EnrichedUranium")).ToArray();
            result.Sections["Inputs"]=colony.Stock.Select(s=>new ColonyMaintenanceRow { Id="stock:"+s.Resource,Name=s.Resource,State="Colony-owned reserve",Quantity=(s.Amount/(decimal)ColonyLimits.Units).ToString("0.######")+" / "+(s.Capacity/(decimal)ColonyLimits.Units).ToString("0.######"),Basis="Save authority",Detail="Reserved "+(s.Reserved/(decimal)ColonyLimits.Units)+"; incoming reservation "+(s.IncomingReserved/(decimal)ColonyLimits.Units)+"; support floor "+(s.SupportFloor/(decimal)ColonyLimits.Units)+". Reservations are not extra stock.",ServiceReason="Inputs require a qualified service path." }).ToArray();
            result.Sections["Inputs"]=serviceRows.Where(r=>r.State=="ReplacementParts").Concat(result.Sections["Inputs"]).ToArray();
            result.Sections["Service log"]=value.Journal.Where(j=>j.ColonyId==colony.Id && j.Kind.IndexOf("service",StringComparison.OrdinalIgnoreCase)>=0).OrderByDescending(j=>j.Sequence).Select(j=>new ColonyMaintenanceRow { Id="journal:"+j.Sequence,Name=j.Kind,State="UT "+j.Ut.ToString("0.##"),Quantity=j.Resource, Basis="Save journal",Detail=j.Detail+"; operation "+j.OperationId,ServiceReason="Recorded outcome; no service action." }).ToArray();
            return result;
        }
        private ColonyMaintenanceRow MaintenanceTankRow(ColonyServiceTarget target,ColonyRecord colony)
        {
            var stock=colony.Stock.SingleOrDefault(s=>s.Resource==target.SourceResource);
            long amount=Math.Max(0,Math.Min(target.Capacity-target.Amount,stock==null ? 0 : stock.Amount-stock.Reserved-stock.SupportFloor));
            return new ColonyMaintenanceRow {Id="tank:"+target.PartId+":"+target.DestinationResource,ColonyId=colony.Id,FacilityId=target.FacilityId,Name=target.PartName,State=target.DestinationResource,Quantity=(target.Amount/(decimal)ColonyLimits.Units).ToString("0.######")+" / "+(target.Capacity/(decimal)ColonyLimits.Units).ToString("0.######"),Basis=target.Provider,
                Detail=target.Reason+" "+target.WorkerWitness+" Service debits "+(amount/(decimal)ColonyLimits.Units)+" "+target.SourceResource+" owned reserves into this exact installed tank. Support floor/reservations protected.",ServiceReason=amount<=0 ? "No accessible paid service inputs or free installed tank capacity." : target.Reason,
                CanService=Ready && Current==this && target.Current && target.CanApply && target.QualifiedWorker && amount>0,
                ServiceFields=new System.Collections.Generic.Dictionary<string,string> {{"PartId",target.PartId.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"DestinationResource",target.DestinationResource},{"ServiceQuoteHash",target.QuoteHash},{"AmountMicroUnits",amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}};
        }
        private ColonyMaintenanceServiceResult SubmitMaintenanceService(ColonyMaintenanceServiceRequest request)
        {
            if(Current!=this)return new ColonyMaintenanceServiceResult {Terminal=true,Message="This runtime was superseded; refresh the selected save."};
            var result=Submit(new ColonyCommand {OperationId=request.OperationId,ContextKey=request.ContextKey,ExpectedRevision=request.ExpectedRevision,ColonyId=request.ColonyId,TargetId=request.FacilityId,Kind="serviceFacility",Fields=new System.Collections.Generic.Dictionary<string,string>(request.Fields,StringComparer.Ordinal)});
            return new ColonyMaintenanceServiceResult {Terminal=result.Outcome=="accepted" || result.Outcome=="rejected" || result.Outcome=="duplicate",Message=result.Outcome+": "+result.Reason};
        }
    }
}
