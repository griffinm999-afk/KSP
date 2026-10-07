using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

// Presentation only. The selected-save authority supplies quantities, blockers and
// capabilities; the UI never infers inventory, homes, utility coverage or money.
public sealed record ColonyManagementRow(string Id, string Name, string State, string Quantity,
    string Provenance, string Detail,IReadOnlyDictionary<string,string>? Cells=null);
public sealed record ColonyManagementAction(string Kind, string Label, bool Available, string Reason,
    string? TargetId = null);
public sealed class ColonyManagementActionCard
{
    private readonly Action<bool> saveExpanded;
    private bool expanded;
    public ColonyManagementAction Action { get; }
    public bool IsExpanded { get=>expanded; set {if(expanded==value)return;expanded=value;saveExpanded(value);} }
    public Visibility DetailVisibility=>Action.Reason.Length==0 ? Visibility.Collapsed : Visibility.Visible;
    public ColonyManagementActionCard(ColonyManagementAction action,bool expanded,Action<bool> saveExpanded)
    {Action=action;this.expanded=expanded;this.saveExpanded=saveExpanded;}
}
public sealed record ColonyManagementSection(string Key, string Title, string Summary,
    ColonyManagementRow[] Rows, ColonyManagementAction[] Actions, string EmptyReason,
    ColonyManagementField[]? Fields = null, ColonyManagementColumn[]? Columns = null,
    ColonyManagementMetric[]? Metrics = null, ColonyManagementPlotGeometry[]? Plots = null);
public sealed record ColonyManagementColumn(string Header,string Property,double Width,bool Numeric=false);
public sealed record ColonyManagementMetric(string Label,string Value,string Detail,string Basis,string PageKey);
public sealed record ColonyManagementPlotGeometry(string Id,string Label,double EastMeters,double NorthMeters,
    double WidthMeters,double LengthMeters,double Heading,string State,string Detail);
public sealed record ColonyManagementField(string Key, string Label, string Value, string Help,ColonyManagementChoice[]? Choices=null);
public sealed class ColonyManagementInput : INotifyPropertyChanged
{
    private string value;
    public string Key { get; }
    public string Label { get; }
    public string Help { get; }
    public ColonyManagementChoice[] Choices {get;private set;}
    public bool HasChoices=>Choices.Length>0;
    public bool IsChoiceField {get;private set;}
    private string issue="";
    private bool currentChoiceAvailable=true;
    private ColonyManagementChoice[] currentOptions=[];
    public string Issue {get=>issue;private set {if(issue==value)return;issue=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Issue)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IssueVisibility)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(HasIssue)));}}
    public Visibility IssueVisibility=>Issue.Length==0 ? Visibility.Collapsed : Visibility.Visible;
    public bool HasIssue=>Issue.Length>0;
    public string Value { get => value; set { value ??= ""; if (this.value == value) return; this.value = value; currentChoiceAvailable=value.Length==0 || currentOptions.Any(c=>c.Id==value); UpdateIssue(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    public ColonyManagementInput(ColonyManagementField field) { Key = field.Key; Label = field.Label; Help = field.Help; value = field.Value; IsChoiceField=field.Choices is not null; Choices=field.Choices ?? []; currentOptions=Choices; currentChoiceAvailable=value.Length==0 || currentOptions.Any(c=>c.Id==value); UpdateIssue(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshChoices(ColonyManagementChoice[]? choices)
    {
        var next=choices ?? [];
        currentOptions=next;
        currentChoiceAvailable=value.Length==0 || next.Any(c=>c.Id==value);
        if(IsChoiceField && value.Length>0 && !next.Any(c=>c.Id==value))next=next.Append(new ColonyManagementChoice(value,"Previous selection is unavailable - choose a current option")).ToArray();
        if(!Choices.SequenceEqual(next))
        {
            Choices=next;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Choices)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(HasChoices)));
        }
        UpdateIssue();
    }
    private void UpdateIssue()
    {
        if(IsChoiceField)
        {
            Issue=!currentChoiceAvailable ? "Previous selection is unavailable. Choose a current option." :
                currentOptions.Length==0 ? "No current options were reported for this field. Refresh or check this task's prerequisites." : "";
            return;
        }
        Issue=Key is "Amount" or "TransferAmount" or "ReorderPoint" or "TargetAmount" or "DesiredAvailable" or "CadenceSeconds" or "TargetFillPercent" &&
            value.Length>0 && !decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out _) ? "Enter a number using digits and a decimal point." : "";
    }
}
public sealed record ColonyManagementChoice(string Id, string Name);
public sealed record ColonyManagementAdoptionCandidate(string Id,string Name,string Detail);
public sealed class ColonyManagementAdoptionSelection : INotifyPropertyChanged
{
    private bool selected;
    public string Id {get;}
    public string Name {get;private set;}
    public string Detail {get;private set;}
    public bool Selected {get=>selected;set {if(selected==value)return;selected=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Selected)));}}
    public event PropertyChangedEventHandler? PropertyChanged;
    public ColonyManagementAdoptionSelection(ColonyManagementAdoptionCandidate candidate,bool selected) {Id=candidate.Id;Name=candidate.Name;Detail=candidate.Detail;this.selected=selected;}
    public void Refresh(ColonyManagementAdoptionCandidate candidate)
    {
        if(candidate.Id!=Id)throw new ArgumentException("Selection identity changed.");
        if(Name!=candidate.Name){Name=candidate.Name;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Name)));}
        if(Detail!=candidate.Detail){Detail=candidate.Detail;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Detail)));}
    }
}
public sealed record ColonyManagementPresentation(string ContextKey, string Status, string Reason,
    string? ColonyId, long Revision, ColonyManagementChoice[] Colonies, ColonyManagementSection[] Sections,
    string? QuoteId = null, string? QuoteReview = null, IReadOnlyDictionary<string, string>? DraftDefaults = null,
    ColonyManagementAdoptionCandidate[]? AdoptionCandidates=null,string? QuoteKind=null,
    ColonyManagementAdoptionCandidate[]? FoundingExistingCandidates=null,ColonyManagementAdoptionCandidate[]? FoundingRecruitCandidates=null,string? QuoteTargetId=null)
{
    public static ColonyManagementPresentation Disconnected(string reason) => new("", "Disconnected", reason,
        null, 0, [], [], null, null);
}
public sealed record ColonyManagementRequest(string OperationId, string ContextKey, string? ColonyId,
    long ExpectedRevision, string Kind, string? TargetId, string? QuoteId,
    IReadOnlyDictionary<string, string> Fields,ColonyFoundingIntent? FoundingIntent=null);
public sealed record ColonyManagementResponse(bool Terminal, string Message,bool IsError=true);

public sealed class ColonyManagementDraft : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    public ColonyManagementDraft(IReadOnlyDictionary<string, string>? defaults = null)
    {
        if (defaults is not null) foreach (var pair in defaults) values.Add(pair.Key,pair.Value);
    }
    public string Name { get => Get(); set => Set(value); }
    public string Body { get => Get(); set => Set(value); }
    public string Biome { get => Get(); set => Set(value); }
    public string Latitude { get => Get(); set => Set(value); }
    public string Longitude { get => Get(); set => Set(value); }
    public string Purpose { get => Get(); set => Set(value); }
    public string Population { get => Get(); set => Set(value); }
    public string Budget { get => Get(); set => Set(value); }
    public string CashFloor { get => Get(); set => Set(value); }
    public string SpendingLimit { get => Get(); set => Set(value); }
    public string ResidentLimit { get => Get(); set => Set(value); }
    public string VisitorLimit { get => Get(); set => Set(value); }
    public string ReserveDays { get => Get(); set => Set(value); }
    public string GrowthPolicy { get => Get(); set => Set(value); }
    public string AdoptFacilityIds { get => Get(); set => Set(value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(values, StringComparer.Ordinal);
    private string Get([CallerMemberName] string key = "") => values.TryGetValue(key, out var value) ? value : "";
    private void Set(string value, [CallerMemberName] string key = "")
    {
        if (Get(key) == value) return;
        values[key] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(key));
    }
}
