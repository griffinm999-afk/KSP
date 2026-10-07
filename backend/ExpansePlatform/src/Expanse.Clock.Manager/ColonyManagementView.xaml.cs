using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Text.RegularExpressions;

namespace Expanse.Clock.Manager;

public partial class ColonyManagementView : UserControl
{
    private static readonly (string Key, string Name)[] Pages =
    [ ("overview", "Overview"), ("founding", "Founding & charter"), ("construction", "Site & construction"),
      ("people", "People & jobs"), ("production", "Production"), ("power", "Power"),
      ("inventory", "Inventory & WOLF"), ("trade", "Trade"), ("maintenance", "Maintenance"),
      ("finance", "Finance & policy") ];
    private ColonyManagementPresentation snapshot = ColonyManagementPresentation.Disconnected("Waiting to connect to this KSP save.");
    private readonly Dictionary<string, ColonyManagementDraft> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> selections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColonyManagementRow> adoptionSelections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColonyManagementRequest> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string,bool> actionExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColonyManagementInput> inputs = new(StringComparer.Ordinal);
    private bool applying, inFlight, quoteInvalidated, renderingRows;
    private string? submittedContext;
    private ColonyManagementDraft draft = new();
    private ColonyManagementColumn[] displayedColumns=[];
    private ColonyManagementInput[] displayedInputs=[];
    public event Action<string?>? ColonySelectionRequested;
    public event Action<string>? FacilitySiteSelectionRequested;
    public Func<ColonyManagementRequest, Task<ColonyManagementResponse>>? SubmitAsync { get; set; }
    public ColonyManagementDraft Draft => draft;

    public ColonyManagementView()
    {
        InitializeComponent();
        Navigation.ItemsSource = Pages.Select(p => p.Name).ToArray();
        Navigation.SelectedIndex = 0;
        SetDraft(draft);
        ApplySnapshot(snapshot);
    }

    // Only immutable observations change on refresh. Form models, page selection
    // and selected row identities remain local to the selected context/colony.
    public void ApplySnapshot(ColonyManagementPresentation value)
    {
        Dispatcher.VerifyAccess();
        bool contextChanged = snapshot.ContextKey != value.ContextKey || snapshot.ColonyId != value.ColonyId;
        bool quoteChanged = snapshot.QuoteId != value.QuoteId || snapshot.QuoteKind!=value.QuoteKind;
        bool reviewRevisionChanged=value.QuoteKind is "adoptFacility" or "qualifyAdoptedHabitat" or "updateCharter" && value.QuoteId is not null && !quoteChanged && snapshot.Revision!=value.Revision;
        snapshot = value;
        applying = true;
        try
        {
            if (contextChanged)
            {
                var key = value.ContextKey + "\0" + value.ColonyId;
                if (!drafts.TryGetValue(key, out var saved)) drafts.Add(key, saved = new ColonyManagementDraft(value.DraftDefaults));
                SetDraft(saved);
                quoteInvalidated = false;
                SetActionFeedback("");
            }
            if (quoteChanged) quoteInvalidated = false;
            if(reviewRevisionChanged)quoteInvalidated=true;
            RefreshSiteChoices();
            ConnectionText.Text = value.Status=="Save authority connected" ? "Connected to this KSP save" : value.Status;
            ConnectionReasonText.Text = ColonyManagementAdapter.DisplayReason(value.Reason);
            ColonyPicker.ItemsSource = value.Colonies;
            ColonyPicker.SelectedValue = value.ColonyId ?? "__new";
            ColonyPicker.IsEnabled = value.Colonies.Length > 0 && !inFlight;
            RenderPage();
        }
        finally { applying = false; }
    }

    private void SetDraft(ColonyManagementDraft value)
    {
        FoundingSitePicker.SelectedIndex=-1;
        draft.PropertyChanged -= DraftChanged;
        draft = value;
        draft.PropertyChanged += DraftChanged;
        DraftPanel.DataContext = draft;
    }

    private void DraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if(sender is ColonyManagementInput && e.PropertyName!=nameof(ColonyManagementInput.Value))return;
        if (snapshot.QuoteId is not null)
        {
            quoteInvalidated = true;
            SetActionFeedback("Draft changed. Request a fresh quote before approval.");
            RenderActions();
        }
        RefreshAdvancedFields();
    }
    private void RefreshSiteChoices()
    {
        var candidates=snapshot.AdoptionCandidates ?? [];
        string? site=FoundingSitePicker.SelectedValue as string;
        FoundingSitePicker.ItemsSource=candidates;
        FoundingSitePicker.SelectedValue=candidates.Any(c=>c.Id==site) ? site : null;
    }

    private string PageKey => Pages[Math.Clamp(Navigation.SelectedIndex, 0, Pages.Length - 1)].Key;
    private string PageSelectionKey => snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0"+PageKey;
    private ColonyManagementSection? Section => snapshot.Sections.FirstOrDefault(s => s.Key == PageKey);
    private void SetActionFeedback(string message,bool isError=true)
    {
        ActionFeedbackText.Text=message;
        TaskFeedbackText.Text=message;
        TaskFeedbackText.Foreground=isError ? new SolidColorBrush(Color.FromRgb(138,41,32)) : new SolidColorBrush(Color.FromRgb(28,91,92));
        TaskFeedbackText.Visibility=string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RenderTaskNotice(ColonyManagementSection? section)
    {
        string message="";
        if(PageKey=="founding" && snapshot.ColonyId is not null)
        {
            var assets=(section?.Rows ?? []).Where(r=>r.Quantity.EndsWith(" certified homes",StringComparison.Ordinal)).ToArray();
            int homes=assets.Sum(r=>int.TryParse(r.Quantity.Split(' ')[0],out int count) ? count : 0);
            bool homeAction=snapshot.Sections.SelectMany(s=>s.Actions).Any(a=>a.Kind=="reviewAdoptedHabitat");
            if(CurrentWorkflow?.Key=="existing")
                message=$"{assets.Length} existing facilities registered; {homes} qualified homes. "+
                    (homes==0 && !homeAction ? "No supported habitat qualification action is available in the current observation. Existing equipment remains registered; adoption does not certify homes or startup building roles." :
                     "Select a facility below to inspect its recorded state and evidence.");
            else if(CurrentWorkflow?.Key=="adoption" && (snapshot.AdoptionCandidates?.Length ?? 0)==0)
                message=$"{assets.Length} facilities are already registered. No new observed adoption candidate is available. Do not adopt a registered facility again.";
            else if(CurrentWorkflow?.Key=="startup")
                message="This task reviews new paid building packages. Existing registered equipment is preserved, but adoption alone does not certify storage, power, workshop or lamp roles. Check the exact bill before approval.";
        }
        if(message.Length==0 && CurrentWorkflow is {Actions.Length:>0} workflow)
        {
            var selected=(RowsGrid?.SelectedItem as ColonyManagementRow)?.Id;
            var blocker=section?.Actions.FirstOrDefault(a=>workflow.Actions.Contains(a.Kind) && a.Kind.StartsWith("review",StringComparison.Ordinal) &&
                !a.Available && (a.TargetId is null || a.TargetId==selected));
            if(blocker is not null)message=blocker.Label+": "+blocker.Reason;
        }
        TaskNoticeText.Text=message;
        TaskNoticePanel.Visibility=message.Length==0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RenderPage()
    {
        if (RowsGrid is null) return;
        var section = Section;
        SectionTitle.Text = section?.Title ?? Pages[Math.Clamp(Navigation.SelectedIndex, 0, Pages.Length - 1)].Name;
        SectionSummary.Text = section?.Summary ?? ColonyManagementAdapter.DisplayReason(snapshot.Reason);
        RefreshWorkflows(section);
        if(CurrentWorkflow?.Key=="existing")
            SectionSummary.Text="Registered physical facilities and matching shared WOLF biome capacity. Inspect each record, then add another landed facility or choose a separate resource or resident task.";
        RenderTaskNotice(section);
        RowsExpander.Header=PageKey=="overview" ? "Current plans and reviews" : "Catalog and current records";
        RefreshFormHeight();
        RefreshFoundingResidents();
        OverviewMetrics.ItemsSource=section?.Metrics ?? [];
        OverviewMetrics.Visibility=section?.Metrics is {Length:>0} ? Visibility.Visible : Visibility.Collapsed;
        var columns=section?.Columns ?? [new("Item / facility","Name",2),new("State / blocker","State",1.4),new("Quantity / cost","Quantity",1.2,true),new("Evidence","Provenance",1.2)];
        if(!displayedColumns.SequenceEqual(columns))
        {
            RowsGrid.Columns.Clear();displayedColumns=columns;
            foreach(var column in columns)
            {
                var style=new Style(typeof(TextBlock),(Style)FindResource("GridText"));
                if(column.Numeric)style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,TextAlignment.Right));
                RowsGrid.Columns.Add(new DataGridTextColumn {Header=column.Header,Binding=new Binding(column.Property),Width=new DataGridLength(column.Width,DataGridLengthUnitType.Star),ElementStyle=style});
            }
        }
        DraftPanel.Visibility = PageKey=="finance" && !HasWorkflowDefinitions || CurrentWorkflow?.Charter==true ? Visibility.Visible : Visibility.Collapsed;
        DraftIntroText.Text = PageKey == "finance" ? "Edit the charter limits below. Review the saved policy and pending commitments before applying changes." :
            "Name the settlement and choose its site. An empty charter starts with no residents, buildings, supplies or new funds. Add each through separate tasks when ready.";
        CharterAdoptionPanel.Visibility=snapshot.ColonyId is null ? Visibility.Visible : Visibility.Collapsed;
        bool registeredIdentity=snapshot.ColonyId is not null;
        foreach(var input in new[]{CharterNameInput,CharterBodyInput,CharterBiomeInput,CharterLatitudeInput,CharterLongitudeInput})
        {
            input.IsReadOnly=registeredIdentity;
            input.ToolTip=registeredIdentity ? "Registered identity and site are fixed for this charter update." : null;
        }
        if(registeredIdentity)DraftIntroText.Text="Review changes to the editable charter terms. The registered colony name and site are read-only; adopt existing hardware with its separate task.";
        if(registeredIdentity && PageKey=="founding" && CurrentWorkflow?.Charter==true)
            SectionSummary.Text="Edit the existing charter's limits and policy. Its name and site stay fixed; review changes before applying.";
        var fields = (section?.Fields ?? []).Where(field=>!HasWorkflowDefinitions && CurrentWorkflow is null || CurrentWorkflow?.Fields.Contains(field.Key)==true).Select(field =>
        {
            string key = snapshot.ContextKey + "\0" + snapshot.ColonyId + "\0" + PageKey + "\0" + field.Key;
            if (!inputs.TryGetValue(key, out var input))
            {
                inputs.Add(key, input = new ColonyManagementInput(field));
                input.PropertyChanged += DraftChanged;
            }
            input.RefreshChoices(field.Choices);
            return input;
        }).ToArray();
        if(!displayedInputs.SequenceEqual(fields)) displayedInputs=fields;
        RefreshAdvancedFields();
        OperationFields.Visibility = fields.Length == 0 || section?.Actions.Length is null or 0 ? Visibility.Collapsed : Visibility.Visible;
        OperationFieldsPanel.Visibility=OperationFields.Visibility;
        var rows = section?.Rows ?? [];
        if(CurrentWorkflow?.Key=="existing")
        {
            rows=rows.Where(r=>r.Quantity.EndsWith(" certified homes",StringComparison.Ordinal) || r.Id.StartsWith("colony-wolf:",StringComparison.Ordinal)).ToArray();
            EmptyText.Text="No physical facilities or WOLF depot records are linked to this colony yet. Add an observed landed facility through its separate task.";
        }
        if(CurrentWorkflow?.Key=="habitat")
        {
            rows=rows.Where(r=>section!.Actions.Any(a=>a.Kind=="qualifyAdoptedHabitat" && a.TargetId==r.Id)).ToArray();
            SectionSummary.Text="Select an adopted deployed KPBS Habitat MK2, review its actual four-seat cabins and utilities, then qualify its homes. Supplies and resident support remain a separate funded step.";
        }
        if(CurrentWorkflow?.Key=="adoption")
        {
            var candidates=snapshot.AdoptionCandidates ?? [];
            rows=rows.Where(r=>candidates.Any(c=>c.Id==r.Id)).ToArray();
            SectionSummary.Text="Select a named current candidate, review its exact membership and site, then approve adoption into this colony.";
            if(adoptionSelections.TryGetValue(PageSelectionKey,out var previous) && !rows.Any(r=>r.Id==previous.Id))
                rows=rows.Append(previous with {State="Selected candidate is unavailable",Quantity="Review held",Detail="This exact selected facility is absent from current inspection. Refresh and reconcile it before adoption. "+previous.Detail}).ToArray();
        }
        renderingRows=true;
        try
        {
            RowsGrid.ItemsSource = rows;
            RowsGrid.SelectedItem = selections.TryGetValue(PageSelectionKey, out var selectedId) ? rows.FirstOrDefault(r => r.Id == selectedId) : null;
        }
        finally {renderingRows=false;}
        if(CurrentWorkflow?.Key!="existing")EmptyText.Text = section?.EmptyReason ?? "No authoritative data is available for this page.";
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        QuotePanel.Header = CurrentWorkflow?.Key=="adoption" ? "Facility adoption review" : PageKey == "founding" ? CurrentWorkflow?.Key=="startup" ? "Step 2: Startup bill and approval" : snapshot.ColonyId is null ? "Step 1: Charter review and registration" : "Charter change review" : "Reviewed quote";
        QuotePanel.Visibility = snapshot.QuoteReview is not null ? Visibility.Visible : Visibility.Collapsed;
        QuoteReviewText.Text = ReadableText(snapshot.QuoteReview ?? "");
        SiteMapPanel.Visibility=section?.Plots is {Length:>0} ? Visibility.Visible : Visibility.Collapsed;
        DrawSiteMap();
        RenderDetail();
        RenderActions();
    }

    private void RenderDetail()
    {
        var diagnostics=new List<string>{"Status: "+snapshot.Status,"Reason: "+snapshot.Reason};
        diagnostics.AddRange((snapshot.Sections.FirstOrDefault(s=>s.Key=="overview")?.Rows ?? [])
            .Where(r=>r.Id.StartsWith("colony-effect:",StringComparison.Ordinal)).Select(r=>r.State+": "+r.Detail));
        diagnostics.AddRange(snapshot.Sections.SelectMany(s=>s.Actions).Where(a=>!a.Available && a.Reason.Length>0)
            .Select(a=>a.Reason).Distinct(StringComparer.Ordinal));
        if (RowsGrid.SelectedItem is ColonyManagementRow row)
        {
            DetailText.Text = ReadableText(row.Name + "  ·  " + row.Provenance + Environment.NewLine + row.State + " · " + row.Quantity + Environment.NewLine + row.Detail);
            diagnostics.Add("Selected record: "+row.Id+Environment.NewLine+row.Detail);
        }
        else DetailText.Text = "Choose an item for details.";
        if(snapshot.QuoteReview is not null)diagnostics.Add(snapshot.QuoteReview);
        DiagnosticsText.Text=string.Join(Environment.NewLine,diagnostics);
        DiagnosticsPanel.Visibility=Visibility.Visible;
    }
    // Stable operation/catalog references remain available for diagnosis, while
    // the ordinary review contains resource quantities, costs and actual reasons.
    private static string ReadableText(string text)=>Regex.Replace(text,@"\b(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{32,64})\b","[technical reference]");

    private void RenderActions()
    {
        if (Actions is null) return;
        var target = (RowsGrid.SelectedItem as ColonyManagementRow)?.Id;
        if (target?.StartsWith("passenger:",StringComparison.Ordinal)==true) target=target.Substring("passenger:".Length);
        if (target?.StartsWith("service-operation:",StringComparison.Ordinal)==true) target=target.Substring("service-operation:".Length);
        if(target?.StartsWith("wolf-order:",StringComparison.Ordinal)==true)target=target.Substring("wolf-order:".Length);
        if(target?.StartsWith("colony-plan:",StringComparison.Ordinal)==true)target=target.Substring("colony-plan:".Length);
        if(target?.StartsWith("physical-transfer:",StringComparison.Ordinal)==true)target=target.Substring("physical-transfer:".Length);
        bool hasPending = pending.ContainsKey(snapshot.ContextKey);
        RetryButton.Visibility = hasPending ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = hasPending && !inFlight && SubmitAsync is not null;
        var shown=(Section?.Actions ?? []).Where(a => WorkflowShowsAction(a) && (a.TargetId is null || a.TargetId == target)).ToArray();
        if(CurrentWorkflow?.Key=="habitat" && shown.Length==0)
            shown=[new("reviewAdoptedHabitat","Review habitat homes",false,"Choose an adopted habitat from the current list.",target)];
        if(CurrentWorkflow?.Key=="adoption" && shown.Length==0)
            shown=[new("reviewAdoption","Review facility adoption",false,target is null ? "Choose a named current adoption candidate." : "The selected candidate has no current adoption capability; refresh and reconcile it.",target)];
        if(CurrentWorkflow?.Key=="existing")shown=[];
        var presented=shown
            .Select(a => a with
            {
                Available = a.Available && AdoptionSelectionCurrent(a) && !inFlight && !hasPending && SubmitAsync is not null && snapshot.ContextKey.Length > 0 && (!NeedsReviewedQuote(a.Kind) || !quoteInvalidated && snapshot.QuoteId is not null && snapshot.QuoteKind==a.Kind && (a.TargetId is null || a.TargetId==snapshot.QuoteTargetId)),
                Reason = inFlight ? "Waiting for the current operation to reconcile." :
                    hasPending ? "Reconcile the pending operation before submitting another." :
                    SubmitAsync is null ? "The colony command connection is unavailable." :
                    snapshot.ContextKey.Length == 0 ? "A selected-save context is required." :
                    !a.Available ? a.Reason :
                    !AdoptionSelectionCurrent(a) ? "The exact selected facility is unavailable; refresh and review a current candidate." :
                    NeedsReviewedQuote(a.Kind) && snapshot.QuoteKind!=a.Kind ? "Review this task before committing." :
                    NeedsReviewedQuote(a.Kind) && a.TargetId is not null && a.TargetId!=snapshot.QuoteTargetId ? "Review this selected record before committing." :
                    snapshot.QuoteId is null && NeedsReviewedQuote(a.Kind) ? "Review a current plan before committing." :
                    quoteInvalidated && NeedsReviewedQuote(a.Kind) ? "Draft changed; request a fresh quote." : a.Reason
            }).ToArray();
        Actions.ItemsSource=presented.Select(a=>
        {
            string key=snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0"+PageKey+"\0"+CurrentWorkflow?.Key+"\0"+a.Kind+"\0"+a.TargetId;
            return new ColonyManagementActionCard(a,actionExpansion.TryGetValue(key,out bool expanded) && expanded,value=>actionExpansion[key]=value);
        }).ToArray();
    }
    private bool AdoptionSelectionCurrent(ColonyManagementAction action)=>action.Kind is "reviewAdoptedHabitat" or "qualifyAdoptedHabitat" ? snapshot.ColonyId is not null && action.TargetId is not null && (RowsGrid.SelectedItem as ColonyManagementRow)?.Id==action.TargetId :
        action.Kind is not ("reviewAdoption" or "adoptFacility") ||
        snapshot.ColonyId is not null && action.TargetId is not null && (RowsGrid.SelectedItem as ColonyManagementRow)?.Id==action.TargetId &&
        (snapshot.AdoptionCandidates ?? []).Any(c=>c.Id==action.TargetId);

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if(ActionFeedbackText is not null)SetActionFeedback("");
        RenderPage();
    }
    private void Metric_Click(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:ColonyManagementMetric metric}) Navigation.SelectedIndex=Array.FindIndex(Pages,p=>p.Key==metric.PageKey);
    }
    private void SiteMap_SizeChanged(object sender,SizeChangedEventArgs e)=>DrawSiteMap();
    private void DrawSiteMap()
    {
        if(SiteMapCanvas is null || SiteMapCanvas.ActualWidth<60)return;
        SiteMapCanvas.Children.Clear();
        var plots=Section?.Plots ?? [];
        if(plots.Length==0)return;
        double extent=Math.Max(20,plots.Max(p=>Math.Max(Math.Abs(p.EastMeters),Math.Abs(p.NorthMeters)) + Math.Sqrt(p.WidthMeters*p.WidthMeters+p.LengthMeters*p.LengthMeters)/2));
        double width=SiteMapCanvas.ActualWidth,height=SiteMapCanvas.Height;
        double scale=Math.Min((width-70)/(extent*2),(height-45)/(extent*2));
        double cx=width/2,cy=(height-20)/2;
        var north=new TextBlock {Text="N ↑",Foreground=Brushes.DimGray,FontSize=10};Canvas.SetLeft(north,8);Canvas.SetTop(north,7);SiteMapCanvas.Children.Add(north);
        var legend=new TextBlock {Text="Relative survey · span "+(extent*2).ToString("N0")+" m",Foreground=Brushes.DimGray,FontSize=10};Canvas.SetLeft(legend,8);Canvas.SetTop(legend,height-19);SiteMapCanvas.Children.Add(legend);
        var origin=new Ellipse {Width=5,Height=5,Fill=Brushes.Teal,ToolTip="Charter site coordinates"};Canvas.SetLeft(origin,cx-2.5);Canvas.SetTop(origin,cy-2.5);SiteMapCanvas.Children.Add(origin);
        foreach(var plot in plots)
        {
            double x=cx+plot.EastMeters*scale,y=cy-plot.NorthMeters*scale;
            var shape=new Rectangle {Width=Math.Max(5,plot.WidthMeters*scale),Height=Math.Max(5,plot.LengthMeters*scale),Stroke=Brushes.SlateGray,StrokeThickness=1,
                Fill=plot.State=="Occupied" ? new SolidColorBrush(Color.FromRgb(153,178,186)) : plot.State=="Reserved" ? new SolidColorBrush(Color.FromRgb(220,199,153)) : new SolidColorBrush(Color.FromRgb(223,236,228)),
                ToolTip=plot.Label+" · "+plot.State+Environment.NewLine+plot.Detail,RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(plot.Heading)};
            Canvas.SetLeft(shape,x-shape.Width/2);Canvas.SetTop(shape,y-shape.Height/2);SiteMapCanvas.Children.Add(shape);
            var label=new TextBlock {Text=plot.Label,MaxWidth=100,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=9,Foreground=Brushes.DarkSlateGray,ToolTip=plot.Detail};
            Canvas.SetLeft(label,x+shape.Width/2+3);Canvas.SetTop(label,y-6);SiteMapCanvas.Children.Add(label);
        }
    }
    private void ColonyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!applying && !inFlight) ColonySelectionRequested?.Invoke(ColonyPicker.SelectedValue as string);
    }
    private void RowsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!applying && !renderingRows && RowsGrid.SelectedItem is ColonyManagementRow row)
        {
            selections[PageSelectionKey] = row.Id;
            if(CurrentWorkflow?.Key=="adoption")adoptionSelections[PageSelectionKey]=row;
        }
        RenderDetail(); RenderActions();
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColonyManagementAction action } || inFlight || pending.ContainsKey(snapshot.ContextKey) || SubmitAsync is null || !action.Available || !AdoptionSelectionCurrent(action) || snapshot.ContextKey.Length == 0) return;
        if (NeedsReviewedQuote(action.Kind) && (quoteInvalidated || snapshot.QuoteId is null || snapshot.QuoteKind!=action.Kind || action.TargetId is not null && action.TargetId!=snapshot.QuoteTargetId)) return;
        var values = new Dictionary<string, string>(draft.Snapshot(), StringComparer.Ordinal);
        foreach (ColonyManagementInput input in displayedInputs) values[input.Key] = input.Value;
        if(action.Kind is "reviewFounding" or "foundColony")values.Remove("AdoptFacilityIds");
        if(action.Kind is "reviewAdoption" or "adoptFacility" or "reviewAdoptedHabitat" or "qualifyAdoptedHabitat")values.Clear();
        if(action.Kind is "reviewCharter" or "updateCharter")values=ColonyManagementCharter.EditableFields(values);
        Expanse.Domain.Colonies.ColonyFoundingIntent? intent;
        try{intent=BuildFoundingIntent(action.Kind,values);}
        catch(Exception ex){SetActionFeedback(ex.Message);return;}
        var request = new ColonyManagementRequest(Guid.NewGuid().ToString("D"), snapshot.ContextKey, snapshot.ColonyId,
            snapshot.Revision, action.Kind, action.TargetId, quoteInvalidated ? null : snapshot.QuoteId, values,intent);
        pending.Add(request.ContextKey, request);
        await SendAsync(request);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (!inFlight && pending.TryGetValue(snapshot.ContextKey, out var request)) await SendAsync(request);
    }
    private async Task SendAsync(ColonyManagementRequest request)
    {
        if (SubmitAsync is null) return;
        inFlight = true;
        submittedContext = request.ContextKey;
        ColonyPicker.IsEnabled = false;
        RenderActions();
        try
        {
            var result = await SubmitAsync(request);
            if (result.Terminal) pending.Remove(request.ContextKey);
            if (submittedContext == snapshot.ContextKey) SetActionFeedback(result.Message,result.IsError);
        }
        catch (Exception ex)
        {
            if (submittedContext == snapshot.ContextKey) SetActionFeedback("Operation outcome is unavailable. Reconcile operation " + request.OperationId + " before retrying. " + ex.Message);
        }
        finally { inFlight = false; ColonyPicker.IsEnabled = snapshot.Colonies.Length > 0; RenderActions(); }
    }
}
