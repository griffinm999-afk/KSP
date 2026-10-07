using System.Globalization;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public partial class MainWindow
{
    private static ColonyManagementPresentation PresentFoundingResidents(ColonyManagementPresentation view,ColonyManagementSnapshot snapshot)
    {
        var colony=snapshot.State?.Colonies.SingleOrDefault(c=>c.Id==view.ColonyId);
        if(colony is null||colony.SupportCommissionedUt.HasValue)return view;
        var env=PlanningEnvironment(snapshot);
        var workers=ColonyEngine.QuoteFoundingPlan(snapshot.State!,colony.Id,env).BootstrapWorkers.Select(w=>w.RosterId).ToHashSet(StringComparer.Ordinal);
        bool Free(ColonyRosterWitness p)=>p.Current&&p.ContextKey==snapshot.ContextKey&&!p.ProtectedMissionCrew&&!workers.Contains(p.RosterId)&&
            !snapshot.State!.Colonies.Any(c=>c.Residents.Any(r=>r.RosterId==p.RosterId))&&!ColonyEngine.IsRosterReservedForPlanning(snapshot.State,p.RosterId)&&
            !snapshot.State.PeopleOperations.Any(o=>o.RosterId==p.RosterId&&o.State!="complete"&&o.State!="cancelled");
        var existing=snapshot.People.Roster.Where(p=>Free(p)&&p.Type=="Crew"&&p.Status=="Assigned"&&colony.VisitorRosterIds.Contains(p.RosterId))
            .OrderBy(p=>p.RosterId,StringComparer.Ordinal).Select(p=>new ColonyManagementAdoptionCandidate(p.RosterId,p.Name+" · "+p.Trait,
                (colony.Facilities.FirstOrDefault(f=>f.VesselId==p.VesselId&&f.PartIds.Contains(p.PartId))?.Name ?? "Current colony cabin")+" · existing visitor; designation preserves actual crew cabin.")).ToArray();
        var recruits=snapshot.People.Roster.Where(p=>Free(p)&&p.Status=="Available"&&(p.Type=="Crew"||p.Type=="Applicant"))
            .OrderBy(p=>p.Type=="Crew"?0:1).ThenBy(p=>p.RosterId,StringComparer.Ordinal).Select(p=>new ColonyManagementAdoptionCandidate(p.RosterId,p.Name+" · "+p.Trait,p.Type+" · available named roster record; exact fare and real home reviewed before commitment.")).ToArray();
        ColonyManagementChoice[] YesNo=[new("true","Enabled — include in reviewed scope"),new("false","Disabled — no automatic spending")];
        var routes=snapshot.People.Routes.Where(r=>r.Body==colony.Site.Body&&r.Qualified&&(!r.DevelopmentOnly||snapshot.DevelopmentMode)).Select(r=>new ColonyManagementChoice(r.Id,r.Name+" · "+r.Fare.ToString("N0")+" fare · "+r.TravelSeconds.ToString("N0")+" game seconds")).ToArray();
        ColonyManagementField[] fields=[
            new("FoundingFillTarget","Resident selection","true","Fill the charter target from ordinary existing visitors first; approved bootstrap workers and protected mission crew are excluded.",[new("true","Fill target using preferred names, then eligible ordinary crew"),new("false","Use only my selected named residents")]),
            new("FoundingArrivalCount","New paid arrivals","0","Explicit new arrival count. Existing visitors cover the remainder; all final names and fares are reviewed."),
            new("FoundingRouteId","Passenger service","","Review finite paid seats and travel time. Existing-person designation has no fare.",new[]{new ColonyManagementChoice("","Choose the lowest cost qualified route in review")}.Concat(routes).ToArray()),
            new("StartupReorder","Automatic Supplies replenishment","true","Orders finite paid stock within receiving capacity, shared funds and cash floor.",YesNo),
            new("StartupReorderPoint","Supplies reorder point (units)","0","Zero derives the reviewed resident/visitor reserve plus finite supplier transit at the reviewed delay factor."),
            new("StartupTarget","Supplies target (units)","0","Zero derives the reserve and delayed supplier transit, plus one review cadence of consumption."),
            new("StartupReorderCadence","Supply review cadence (game seconds)","21600","Reviewed automatic purchases remain capacity and budget constrained."),
            new("StartupService","Recurring installed-tank service","true","Requires real paid buffers, actual qualified Engineer and exact physical provider.",YesNo),
            new("StartupServiceFill","Service target fill (%)","95","No quantity is created from a tank capacity reading."),
            new("StartupServiceCadence","Service cadence (game seconds)","21600","At least one Kerbin day; qualifying each service stays mandatory."),
            new("StartupMachinery","Optional Machinery buffer (units)","100","Explicit modeled starter buffer; set zero to decline. Current supplier cost and receiving capacity appear in the bill."),
            new("StartupMaterialKits","Optional MaterialKits operating buffer (units)","100","Additional paid operating reserve; construction bill remains separate. Set zero to decline."),
            new("StartupUranium","Optional EnrichedUranium buffer (units)","10","Explicit paid fuel reserve, not proof of a reactor provider. Set zero to decline."),
            new("StartupLocal","Use qualified local procurement","true","Exact accessible physical stock is conserved; unqualified native inputs remain held.",YesNo)];
        return view with {FoundingExistingCandidates=existing,FoundingRecruitCandidates=recruits,Sections=view.Sections.Select(s=>s.Key=="founding"?s with {Fields=(s.Fields??[]).Concat(fields).Concat(ProductionFields(snapshot)).ToArray()}:s).ToArray()};
    }
}
