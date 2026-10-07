using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

public sealed record ColonyManagementCharterReview(string Id,string Description,IReadOnlyDictionary<string,string> Fields);
public static class ColonyManagementCharter
{
    private static readonly string[] editable=["Purpose","Population","Budget","CashFloor","SpendingLimit","ResidentLimit","VisitorLimit","ReserveDays","GrowthPolicy"];
    public static Dictionary<string,string> EditableFields(IReadOnlyDictionary<string,string> fields)=>
        fields.Where(p=>editable.Contains(p.Key,StringComparer.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
    public static bool MatchesEditableFields(IReadOnlyDictionary<string,string> fields,IReadOnlyDictionary<string,string> reviewed)
    {
        var actual=EditableFields(fields);var expected=EditableFields(reviewed);
        return actual.Count==expected.Count && actual.All(p=>expected.TryGetValue(p.Key,out var value) && value==p.Value);
    }
    public static ColonyManagementCharterReview Review(ColonyManagementSnapshot snapshot,ColonyManagementRequest request)
    {
        if(snapshot.State is null || snapshot.ContextKey!=request.ContextKey || snapshot.State.Revision!=request.ExpectedRevision)
            throw new InvalidOperationException("The selected save or charter revision changed; refresh before review.");
        var colony=snapshot.State.Colonies.SingleOrDefault(c=>c.Id==request.ColonyId) ?? throw new InvalidOperationException("Select the registered colony whose charter is being reviewed.");
        var capability=snapshot.Capabilities.FirstOrDefault(c=>c.Kind=="updateCharter" && (c.ColonyId.Length==0 || c.ColonyId==colony.Id));
        if(capability is null || !capability.Available)throw new InvalidOperationException(capability?.Reason ?? "Charter update is unavailable in this runtime.");
        var fields=EditableFields(request.Fields);
        if(editable.Any(key=>!fields.TryGetValue(key,out var value) || string.IsNullOrWhiteSpace(value)))throw new InvalidOperationException("Complete every editable charter term before review.");
        fields["Sandbox"]=colony.Charter.Sandbox ? "true" : "false";
        var command=ColonyManagementAdapter.Command(request with {Kind="updateCharter",Fields=fields,QuoteId=null});
        var result=ColonyEngine.Execute(snapshot.State,command,new(){WorldId=snapshot.State.WorldId,ContextKey=snapshot.ContextKey,Ut=Math.Max(snapshot.State.SimulatedUt,snapshot.ObservedUt),AvailableFunds=snapshot.AvailableFunds ?? 0,DevelopmentMode=snapshot.DevelopmentMode});
        if(result.Outcome!="accepted")throw new InvalidOperationException(result.Reason);
        var charter=result.State.Colonies.Single(c=>c.Id==colony.Id).Charter;
        return new(ColonyStateCodec.CommandHash(command),"Update the charter for "+colony.Name+" at its registered "+colony.Site.Body+" / "+colony.Site.Biome+" site. Purpose "+charter.Purpose+"; population target "+charter.PopulationTarget+"; resident ceiling "+charter.ResidentLimit+"; visitor allowance "+charter.VisitorLimit+". Founding budget "+charter.FoundingBudget.ToString("N0")+" funds; protected cash floor "+charter.CashFloor.ToString("N0")+"; spending limit "+charter.SpendingLimit.ToString("N0")+"; support reserve "+charter.ReserveDays+" Kerbin days; expansion policy "+charter.GrowthPolicy+". Existing people and obligations were checked. This changes the reviewed charter terms; the registered identity, site, existing facilities and certification setting are preserved.",new Dictionary<string,string>(command.Fields,StringComparer.Ordinal));
    }
}

// A different save may be reviewed while the first save has an uncertain reply.
// Preserve the first submitted payload independently of the current UI quote.
public sealed class ColonyManagementCharterOperations
{
    private readonly Dictionary<string,(ColonyManagementRequest Original,Dictionary<string,string> Fields)> pending=new(StringComparer.Ordinal);
    public bool ContainsExact(ColonyManagementRequest request)=>pending.TryGetValue(request.OperationId,out var prior) && Same(prior.Original,request);
    public ColonyCommand Prepare(ColonyManagementSnapshot snapshot,ColonyManagementRequest request,IReadOnlyDictionary<string,string>? reviewedFields)
    {
        if(snapshot.ContextKey!=request.ContextKey)throw new InvalidOperationException("Reconnect the original save/load context before reconciling this charter operation.");
        if(!pending.TryGetValue(request.OperationId,out var prior))
        {
            if(request.Kind!="updateCharter" || request.FoundingIntent is not null || snapshot.State?.Revision!=request.ExpectedRevision || reviewedFields is null ||
                !ColonyManagementCharter.MatchesEditableFields(ColonyManagementAdapter.Command(request).Fields,reviewedFields))throw new InvalidOperationException("Review the current charter terms before applying this update.");
            prior=(request with {Fields=new Dictionary<string,string>(request.Fields,StringComparer.Ordinal)},new Dictionary<string,string>(reviewedFields,StringComparer.Ordinal));
            pending.Add(request.OperationId,prior);
        }
        else if(!Same(prior.Original,request))throw new InvalidOperationException("The pending charter operation has different terms. Reconcile its exact original request.");
        return ColonyManagementAdapter.Command(prior.Original with {Fields=new Dictionary<string,string>(prior.Fields,StringComparer.Ordinal)});
    }
    public void Complete(string operationId)=>pending.Remove(operationId);
    private static bool Same(ColonyManagementRequest a,ColonyManagementRequest b)=>
        a.OperationId==b.OperationId && a.ContextKey==b.ContextKey && a.ColonyId==b.ColonyId && a.ExpectedRevision==b.ExpectedRevision &&
        a.Kind==b.Kind && a.TargetId==b.TargetId && a.QuoteId==b.QuoteId && a.FoundingIntent is null && b.FoundingIntent is null &&
        a.Fields.Count==b.Fields.Count && a.Fields.All(p=>b.Fields.TryGetValue(p.Key,out var value) && value==p.Value);
}
