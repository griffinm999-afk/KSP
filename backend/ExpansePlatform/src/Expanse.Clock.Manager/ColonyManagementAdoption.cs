using System.IO;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

// The runtime publishes the exact target capabilities. Reviews use the same
// pure domain guards; only the separate command submission changes authority.
public static class ColonyManagementAdoption
{
    public static ColonyManagementAction[] ReviewedActions(ColonyManagementAction[] actions)=>actions
        .Concat(actions.Where(a=>a.Kind=="adoptFacility" && a.TargetId is not null).Select(a=>
            new ColonyManagementAction("reviewAdoption","Review facility adoption",a.Available,a.Reason,a.TargetId)))
        .Concat(actions.Where(a=>a.Kind=="qualifyAdoptedHabitat" && a.TargetId is not null).Select(a=>
            new ColonyManagementAction("reviewAdoptedHabitat","Review habitat homes",a.Available,a.Reason,a.TargetId))).ToArray();

    public static ColonyManagementPresentation Present(ColonyManagementPresentation view,ColonyManagementSnapshot snapshot)
    {
        var candidates=snapshot.AdoptableFacilities.Where(f=>snapshot.State is not null &&
            !snapshot.State.Colonies.Any(c=>c.Facilities.Any(member=>member.Id==f.Id || member.VesselId==f.VesselId))).ToArray();
        var rows=candidates.Select(f=>new ColonyManagementRow(f.Id,f.Name,"Current adoption candidate","Physical hardware",f.Qualification.Context,
            snapshot.FacilitySites.TryGetValue(f.Id,out var site)
                ? site.Body+" / "+site.Biome+" at "+site.Latitude.ToString("0.00000")+", "+site.Longitude.ToString("0.00000")+". "+f.PartIds.Count+" observed member parts. "+f.LastReason+". Review this exact facility before adopting it. Vessel "+f.VesselId+"; facility "+f.Id
                : "Site coordinates unavailable; adoption is held. Vessel "+f.VesselId+"; facility "+f.Id)).ToArray();
        var homes=snapshot.State?.Colonies.Where(c=>c.Id==view.ColonyId).SelectMany(c=>c.Facilities).Where(ColonyEngine.IsAdoptedHabitatIdentity)
            .Select(f=>new ColonyManagementRow(f.Id,f.Name,f.State,f.CertifiedHomes+" qualified home seats",f.Qualification.Context,
                snapshot.Capabilities.FirstOrDefault(c=>c.Kind=="qualifyAdoptedHabitat" && c.TargetId==f.Id)?.Reason ?? "Load the habitat to review its actual home parts.")).ToArray() ?? [];
        return view with
        {
            Sections=view.Sections.Select(s=>s.Key=="people" ? s with {Rows=s.Rows.Concat(homes).ToArray()} : s.Key!="founding" ? s : s with
            {
                Rows=s.Rows.Concat(rows).ToArray(),
                Actions=s.Actions.Select(a=>a.Kind is "adoptFacility" or "reviewAdoption" && (view.ColonyId is null || !candidates.Any(f=>f.Id==a.TargetId))
                    ? a with {Available=false,Reason="The exact candidate is unavailable or already belongs to a colony; refresh before adoption."} : a).ToArray()
            }).ToArray(),
            AdoptionCandidates=rows.Select(r=>new ColonyManagementAdoptionCandidate(r.Id,r.Name,r.Detail)).ToArray()
        };
    }

    public static ColonyAdoptionQuote Review(ColonyManagementSnapshot snapshot,ColonyManagementRequest request)
    {
        if(snapshot.State is null || snapshot.ContextKey!=request.ContextKey || snapshot.State.Revision!=request.ExpectedRevision)
            throw new InvalidOperationException("The save or colony revision changed; refresh before reviewing adoption.");
        bool habitat=request.Kind is "reviewAdoptedHabitat" or "qualifyAdoptedHabitat";
        var capability=snapshot.Capabilities.SingleOrDefault(c=>c.Kind==(habitat ? "qualifyAdoptedHabitat" : "adoptFacility") && c.ColonyId==request.ColonyId && c.TargetId==request.TargetId);
        if(capability is null || !capability.Available)
            throw new InvalidOperationException(capability?.Reason ?? "The exact selected facility has no current adoption capability.");
        var env=new ColonyEnvironment
        {
            WorldId=snapshot.State.WorldId,ContextKey=snapshot.ContextKey,Ut=Math.Max(snapshot.State.SimulatedUt,snapshot.ObservedUt),
            AvailableFunds=snapshot.AvailableFunds ?? 0,AdoptableFacilities=snapshot.AdoptableFacilities,
            FacilitySites=snapshot.FacilitySites,BodyRadiiMeters=snapshot.BodyRadiiMeters,People=snapshot.People,
            Support=snapshot.Support,Services=snapshot.Services,DevelopmentMode=snapshot.DevelopmentMode
        };
        var quote=habitat ? ColonyEngine.QuoteAdoptedHabitat(snapshot.State,request.ColonyId ?? "",request.TargetId ?? "",env) : ColonyEngine.QuoteFacilityAdoption(snapshot.State,request.ColonyId ?? "",request.TargetId ?? "",env);
        if(!quote.CanApprove)throw new InvalidOperationException(quote.Reason);
        return quote;
    }

    public static bool MatchesReview(ColonyManagementSnapshot snapshot,ColonyManagementRequest request)
    {
        try
        {
            var quote=Review(snapshot,request);
            return request.QuoteId==quote.Id && request.Fields.Count==1 &&
                request.Fields.TryGetValue(request.Kind=="qualifyAdoptedHabitat" ? "HabitatWitnessHash" : "AdoptionWitnessHash",out var hash) && hash==quote.AdoptionWitnessHash;
        }
        catch(Exception ex) when(ex is InvalidOperationException or InvalidDataException or ArgumentException or OverflowException)
        {return false;}
    }
}

// An uncertain reply retains the exact already-submitted command. Reconciliation
// reaches the authority's receipt lookup even after polling observes membership
// or revision changes; it never prepares a new adoption under that operation ID.
public sealed class ColonyManagementAdoptionOperations
{
    private readonly Dictionary<string,(ColonyManagementRequest Original,ColonyManagementRequest Reviewed)> prepared=new(StringComparer.Ordinal);
    public bool ContainsExact(ColonyManagementRequest request)=>prepared.TryGetValue(request.OperationId,out var value) && SameRequest(value.Original,request);
    public ColonyCommand Prepare(ColonyManagementSnapshot snapshot,ColonyManagementRequest request,IReadOnlyDictionary<string,string> reviewedFields)
    {
        if(snapshot.ContextKey!=request.ContextKey)throw new InvalidOperationException("The selected save/load context changed. Reconnect the original context before reconciling this adoption operation.");
        if(prepared.TryGetValue(request.OperationId,out var prior))
        {
            if(!SameRequest(prior.Original,request))throw new InvalidOperationException("Pending adoption operation was reused with a different request. Reconcile its exact original request.");
            return ColonyManagementAdapter.Command(prior.Reviewed);
        }
        var reviewed=request with {Fields=new Dictionary<string,string>(reviewedFields,StringComparer.Ordinal)};
        if(!ColonyManagementAdoption.MatchesReview(snapshot,reviewed))throw new InvalidOperationException("The selected adoption candidate or its current evidence changed. Refresh and review this exact facility again.");
        prepared.Add(request.OperationId,(request with {Fields=new Dictionary<string,string>(request.Fields,StringComparer.Ordinal)},reviewed));
        return ColonyManagementAdapter.Command(reviewed);
    }
    public void Complete(string operationId)=>prepared.Remove(operationId);
    private static bool SameRequest(ColonyManagementRequest a,ColonyManagementRequest b)=>
        a.OperationId==b.OperationId && a.ContextKey==b.ContextKey && a.ColonyId==b.ColonyId && a.ExpectedRevision==b.ExpectedRevision &&
        a.Kind==b.Kind && a.TargetId==b.TargetId && a.QuoteId==b.QuoteId && a.FoundingIntent is null && b.FoundingIntent is null &&
        a.Fields.Count==b.Fields.Count && a.Fields.All(pair=>b.Fields.TryGetValue(pair.Key,out var value) && value==pair.Value);
}
