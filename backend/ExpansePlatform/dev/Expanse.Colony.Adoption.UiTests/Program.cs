using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using Expanse.Clock.Manager;
using Expanse.Domain.Colonies;

Exception? failure=null;
var thread=new Thread(()=>{try{Run();}catch(Exception ex){failure=ex;Application.Current?.Shutdown();}});
thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
Console.WriteLine("PASS: Disconnected synthetic WPF adoption workflow and exact pure-domain command checks; no game, Host or pipe connection.");

static void Run()
{
    int checks=0;
    void Check(bool value,string message){if(!value)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
    string Id()=>Guid.NewGuid().ToString("D");
    var state=ColonyEngine.Create(Id(),100);
    var colony=new ColonyRecord {Id=Id(),Name="SYNTHETIC Greater Flats",FoundedUt=100,SupportAccountedUt=100,Site=new(){Body="Minmus",Biome="Greater Flats"}};
    state.Colonies.Add(colony);
    var first=new ColonyFacility {Id=Id(),VesselId=Id(),Name="SYNTHETIC workshop",PartIds=[1001],Qualification=new(){Context="loaded-unpacked",ObservedUt=100}};
    var second=new ColonyFacility {Id=Id(),VesselId=Id(),Name="SYNTHETIC receiving store",PartIds=[1002],Qualification=new(){Context="loaded-unpacked",ObservedUt=100}};
    var snapshot=new ColonyManagementSnapshot {State=state,ContextKey="synthetic/adoption/epoch-1",Status="SYNTHETIC",ObservedUt=100,
        AdoptableFacilities=[first,second],FacilitySites=new(){[first.Id]=new(){Body="Minmus",Biome="Greater Flats"},[second.Id]=new(){Body="Minmus",Biome="Greater Flats",Longitude=.01}},
        BodyRadiiMeters=new(){["Minmus"]=60000},People=new(){PresenceComplete=true,PresentByColony=new(){[colony.Id]=[]}},
        Capabilities=[new(){Kind="updateCharter",Available=true},new(){Kind="adoptFacility",ColonyId=colony.Id,TargetId=first.Id,Available=true,Reason="SYNTHETIC current candidate"},new(){Kind="adoptFacility",ColonyId=colony.Id,TargetId=second.Id,Available=true,Reason="SYNTHETIC current candidate"}]};
    ColonyManagementPresentation Present()
    {
        var actions=snapshot.Capabilities.Select(c=>new ColonyManagementAction(c.Kind,c.Kind,c.Available,c.Reason,c.TargetId.Length==0 ? null : c.TargetId)).ToArray();
        return ColonyManagementAdoption.Present(ColonyManagementAdapter.Present(snapshot.State!,snapshot.ContextKey,colony.Id,snapshot.Status,"Synthetic fixture only",ColonyManagementAdoption.ReviewedActions(actions)),snapshot);
    }
    var app=new FixtureApplication();
    using(var resource=typeof(FixtureApplication).Assembly.GetManifestResourceStream("FixtureResources.xaml")!)
    {
        var document=XDocument.Load(resource);XNamespace wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary=document.Root!.Element(wpf+"Application.Resources")!;dictionary.Name=wpf+"ResourceDictionary";
        dictionary.SetAttributeValue(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources=(ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }
    var view=new ColonyManagementView();
    var window=new Window {Content=view,Width=960,Height=540,Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,WindowStyle=WindowStyle.None};
    window.Show();
    var nav=(ListBox)view.FindName("Navigation");var grid=(DataGrid)view.FindName("RowsGrid");var picker=(ComboBox)view.FindName("WorkflowPicker");var actions=(ItemsControl)view.FindName("Actions");
    void Layout(){window.UpdateLayout();view.Measure(new Size(window.Width,window.Height));view.Arrange(new Rect(0,0,window.Width,window.Height));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);}
    ColonyManagementAction Action(string kind)=>actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind==kind);
    void Click(string kind){Layout();Descendants(actions).OfType<Button>().Single(b=>b.Tag is ColonyManagementAction a && a.Kind==kind).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();}
    var requests=new List<ColonyManagementRequest>();ColonyAdoptionQuote? review=null;ColonyCommand? accepted=null;
    view.SubmitAsync=request=>
    {
        requests.Add(request);
        if(request.Kind=="reviewAdoption")
        {
            review=ColonyManagementAdoption.Review(snapshot,request);
            view.ApplySnapshot(Present() with {QuoteId=review.Id,QuoteKind="adoptFacility",QuoteTargetId=request.TargetId,QuoteReview=review.Name+" → "+colony.Name});
        }
        else
        {
            var fields=new Dictionary<string,string>{{"AdoptionWitnessHash",review!.AdoptionWitnessHash}};
            var reviewed=request with {Fields=fields};
            Check(ColonyManagementAdoption.MatchesReview(snapshot,reviewed),"Commit matches exact current reviewed candidate");
            accepted=ColonyManagementAdapter.Command(reviewed);
            var result=ColonyEngine.Execute(snapshot.State!,accepted,Environment(snapshot));
            Check(result.Outcome=="accepted","Reachable approval executes guarded domain adoption: "+result.Reason);
            snapshot.State=result.State;view.ApplySnapshot(Present());
        }
        return Task.FromResult(new ColonyManagementResponse(true,"Synthetic only"));
    };
    view.ApplySnapshot(Present());nav.SelectedIndex=1;picker.SelectedValue="adoption";Layout();
    Check(Equals(picker.SelectedValue,"adoption") && grid.Items.Count==2 && grid.SelectedItem is null && !Action("reviewAdoption").Available,"Named adoption task reachable without incidental target selection");
    Check(((FrameworkElement)view.FindName("DraftPanel")).Visibility==Visibility.Collapsed && ((ItemsControl)view.FindName("OperationFields")).Items.Count==0,"Adoption exposes no editable charter or broad identity field");
    grid.SelectedItem=grid.Items.Cast<ColonyManagementRow>().Single(r=>r.Id==second.Id);Layout();
    Check(Action("reviewAdoption").Available && !Action("adoptFacility").Available,"Selected target can be reviewed; adoption requires review");
    Click("reviewAdoption");
    Check(review is not null,"Domain review succeeds: "+((TextBlock)view.FindName("ActionFeedbackText")).Text);
    Check(requests.Count==1 && requests[0].TargetId==second.Id && requests[0].ColonyId==colony.Id && requests[0].ContextKey==snapshot.ContextKey && requests[0].ExpectedRevision==state.Revision && requests[0].Fields.Count==0,"Review request retains exact selected target, colony, context and revision");
    Check(snapshot.State!.Colonies.Single().Facilities.Count==0 && snapshot.State.Revision==state.Revision,"Review does not mutate authority");
    var quoted=Present() with {QuoteId=review!.Id,QuoteKind="adoptFacility",QuoteTargetId=second.Id,QuoteReview=review.Name};
    view.Draft.Purpose="Retained local charter edit";view.ApplySnapshot(quoted with {QuoteId="edited-draft-re-review"});Layout();
    view.ApplySnapshot(quoted);Layout();
    var before=requests.Count;view.ApplySnapshot(quoted);Layout();
    Check(requests.Count==before && (grid.SelectedItem as ColonyManagementRow)?.Id==second.Id && Equals(picker.SelectedValue,"adoption") && view.Draft.Purpose=="Retained local charter edit","Polling retains exact target, task and edits without requests");
    grid.SelectedItem=grid.Items.Cast<ColonyManagementRow>().Single(r=>r.Id==first.Id);Layout();
    Check(!Action("adoptFacility").Available,"Another candidate cannot consume selected candidate review");
    grid.SelectedItem=grid.Items.Cast<ColonyManagementRow>().Single(r=>r.Id==second.Id);Layout();
    Check(Action("adoptFacility").Available,"Exact reviewed target enables approval");
    view.ApplySnapshot(quoted with {Revision=quoted.Revision+1});Layout();
    Check(!Action("adoptFacility").Available,"Unchanged quote holds when colony revision changes");
    view.ApplySnapshot(Present());Click("reviewAdoption");
    quoted=Present() with {QuoteId=review!.Id,QuoteKind="adoptFacility",QuoteTargetId=second.Id,QuoteReview=review.Name};
    var exactRequest=new ColonyManagementRequest(Id(),snapshot.ContextKey,colony.Id,state.Revision,"adoptFacility",second.Id,review.Id,new Dictionary<string,string>{{"AdoptionWitnessHash",review.AdoptionWitnessHash}});
    second.PartIds=[1002,1003];
    Check(!ColonyManagementAdoption.MatchesReview(snapshot,exactRequest),"Same revision member drift invalidates reviewed witness");
    second.PartIds=[1002];
    second.Qualification.ObservedUt=89;
    bool staleHeld=false;try{ColonyManagementAdoption.Review(snapshot,exactRequest);}catch(InvalidOperationException){staleHeld=true;}
    Check(staleHeld && !ColonyManagementAdoption.MatchesReview(snapshot,exactRequest),"Stale candidate observation holds domain review and commitment");second.Qualification.ObservedUt=100;
    snapshot.AdoptableFacilities=[first];view.ApplySnapshot(ColonyManagementAdoption.Present(quoted,snapshot));Layout();
    Check(grid.Items.Cast<ColonyManagementRow>().Any(r=>r.Id==second.Id && r.State.Contains("unavailable")) && (grid.SelectedItem as ColonyManagementRow)?.Id==second.Id && !Action("adoptFacility").Available,"Missing selected candidate remains visible and held without replacement");
    snapshot.AdoptableFacilities=[first,second];view.ApplySnapshot(Present());Layout();Click("reviewAdoption");
    var approveButton=Descendants(actions).OfType<Button>().Single(b=>b.Tag is ColonyManagementAction a && a.Kind=="adoptFacility");
    var bounds=approveButton.TransformToAncestor(view).TransformBounds(new Rect(approveButton.RenderSize));
    Check(grid.ActualHeight>=40 && bounds.Top>=0 && bounds.Bottom<=view.ActualHeight && approveButton.ActualHeight>=25,"Compact layout retains visible approval and usable candidate table");
    Click("adoptFacility");
    Check(snapshot.State!.Colonies.Single().Facilities.Select(f=>f.Id).SequenceEqual(new[]{second.Id}) && snapshot.State.Colonies.Single().Stock.Count==0,"Approval adopts only selected hardware without stock grant");
    Check(!actions.Items.Cast<ColonyManagementAction>().Any(a=>a.Kind=="adoptFacility" && a.Available),"Accepted target cannot be adopted repeatedly from stale candidate capabilities");
    var duplicate=ColonyEngine.Execute(snapshot.State,accepted!,Environment(snapshot));
    Check(duplicate.Outcome=="duplicate" && duplicate.State.Colonies.Single().Facilities.Count==1,"Exact accepted operation replay does not repeat adoption");
    view.ApplySnapshot(Present() with {ContextKey="synthetic/other-load",QuoteId=null});Layout();
    Check(grid.SelectedItem is null && requests.Count==before+3,"New load context does not inherit a selected facility or submit a request");
    // A lost reply must reconcile the original operation after polling observes
    // adoption. The retained request crosses no selected-load boundary.
    view.ApplySnapshot(Present());nav.SelectedIndex=1;picker.SelectedValue="adoption";Layout();
    grid.SelectedItem=grid.Items.Cast<ColonyManagementRow>().Single(r=>r.Id==first.Id);Layout();
    var operations=new ColonyManagementAdoptionOperations();ColonyCommand? originalCommand=null;ColonyManagementRequest? originalRequest=null;int attempts=0;
    view.SubmitAsync=request=>
    {
        if(request.Kind=="reviewAdoption")
        {
            review=ColonyManagementAdoption.Review(snapshot,request);view.ApplySnapshot(Present() with {QuoteId=review.Id,QuoteKind="adoptFacility",QuoteTargetId=first.Id});
            return Task.FromResult(new ColonyManagementResponse(true,"Synthetic review only"));
        }
        attempts++;var command=operations.Prepare(snapshot,request,new Dictionary<string,string>{{"AdoptionWitnessHash",review!.AdoptionWitnessHash}});
        if(attempts==1){originalCommand=command;originalRequest=request;}
        else Check(ColonyStateCodec.CommandHash(command)==ColonyStateCodec.CommandHash(originalCommand!) && command.OperationId==originalCommand!.OperationId,"Retry retains exact submitted command payload and operation ID");
        var result=ColonyEngine.Execute(snapshot.State!,command,Environment(snapshot));snapshot.State=result.State;view.ApplySnapshot(Present());
        Check(result.Outcome==(attempts==1 ? "accepted" : "duplicate"),"Unknown reply reconciliation reaches exact authority receipt");
        if(attempts>1)operations.Complete(request.OperationId);
        return Task.FromResult(new ColonyManagementResponse(attempts>1,"Synthetic first reply deliberately unknown"));
    };
    Click("reviewAdoption");Click("adoptFacility");Layout();
    var retry=(Button)view.FindName("RetryButton");
    Check(retry.Visibility==Visibility.Visible && retry.IsEnabled && snapshot.State!.Colonies.Single().Facilities.Count==2 && !actions.Items.Cast<ColonyManagementAction>().Any(a=>a.Available),"Unknown reply holds new actions after polling observes adoption and revision drift");
    bool changedRequestHeld=false;try{operations.Prepare(snapshot,originalRequest! with {TargetId=second.Id},new Dictionary<string,string>());}catch(InvalidOperationException){changedRequestHeld=true;}
    Check(changedRequestHeld,"A retained operation ID cannot switch to another adoption target");
    var compacted=ColonyStateCodec.Copy(snapshot.State!);compacted.Receipts.Clear();
    var expiredCommand=operations.Prepare(snapshot,originalRequest!,new Dictionary<string,string>());
    var compactedEnvironment=Environment(snapshot);var expired=ColonyEngine.Execute(compacted,expiredCommand,compactedEnvironment);
    Check(expired.Outcome=="rejected" && expiredCommand.ExpectedRevision==originalCommand!.ExpectedRevision && expired.State.Colonies.Single().Facilities.Count==2,"Missing receipt preserves stale revision rejection without replacement adoption");
    string originalContext=snapshot.ContextKey;snapshot.ContextKey="synthetic/changed-load";view.ApplySnapshot(Present());Layout();
    Check(retry.Visibility==Visibility.Collapsed && attempts==1,"Changed selected context disables pending retry without transport");
    bool contextHeld=false;try{operations.Prepare(snapshot,originalRequest!,new Dictionary<string,string>());}catch(InvalidOperationException){contextHeld=true;}
    Check(contextHeld,"Prepared reconciliation cannot cross selected save/load context");
    snapshot.ContextKey=originalContext;view.ApplySnapshot(Present());Layout();retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
    Check(attempts==2 && snapshot.State!.Colonies.Single().Facilities.Count==2 && retry.Visibility==Visibility.Collapsed,"Exact retry settles duplicate receipt without a second adoption");
    var newView=ColonyManagementAdapter.Present(state,"synthetic/new-charter",null,"SYNTHETIC","Fixture only",[new("reviewFounding","Review charter",true,"")]) with {AdoptionCandidates=[new(first.Id,first.Name,"Synthetic actual site")]};
    view.ApplySnapshot(newView);nav.SelectedIndex=1;Layout();
    var choices=(ItemsControl)view.FindName("AdoptionChoices");choices.Items.Cast<ColonyManagementAdoptionSelection>().Single().Selected=true;view.Draft.Name="Edited new charter";view.ApplySnapshot(newView);Layout();
    Check(((FrameworkElement)view.FindName("CharterAdoptionPanel")).Visibility==Visibility.Visible && view.Draft.AdoptFacilityIds==first.Id && view.Draft.Name=="Edited new charter","Initial named charter adoption and local edits survive polling");
    Check(ColonyManagementAdoption.ReviewedActions([]).Length==0,"No runtime capability manufactures an available adoption action");
    // Existing charter controls may only edit terms the real domain command
    // applies. The registered identity/site remain visible and read-only.
    snapshot.ContextKey=originalContext;
    ColonyManagementPresentation CharterPresentation()
    {
        var value=Present();return value with {Sections=value.Sections.Select(s=>s.Key is "founding" or "finance" ? s with {Actions=s.Actions.Append(new("reviewCharter","Review charter update",true,"Synthetic current charter capability")).ToArray()} : s).ToArray()};
    }
    var charterRequests=new List<ColonyManagementRequest>();ColonyManagementCharterReview? charterReview=null;
    view.SubmitAsync=request=>
    {
        charterRequests.Add(request);
        if(request.Kind=="reviewCharter")
        {
            charterReview=ColonyManagementCharter.Review(snapshot,request);view.ApplySnapshot(CharterPresentation() with {QuoteId=charterReview.Id,QuoteKind="updateCharter",QuoteReview=charterReview.Description});
        }
        else
        {
            var command=ColonyManagementAdapter.Command(request);
            Check(ColonyManagementCharter.MatchesEditableFields(command.Fields,charterReview!.Fields),"Charter approval carries exactly reviewed editable terms");
            command.Fields=new Dictionary<string,string>(charterReview.Fields,StringComparer.Ordinal);
            var result=ColonyEngine.Execute(snapshot.State!,command,Environment(snapshot));Check(result.Outcome=="accepted","Guarded existing charter update executes");snapshot.State=result.State;view.ApplySnapshot(CharterPresentation());
        }
        return Task.FromResult(new ColonyManagementResponse(true,"Synthetic charter only"));
    };
    view.ApplySnapshot(CharterPresentation());nav.SelectedIndex=1;picker.SelectedValue="charter";Layout();
    Check(new[]{"CharterNameInput","CharterBodyInput","CharterBiomeInput","CharterLatitudeInput","CharterLongitudeInput"}.All(name=>((TextBox)view.FindName(name)).IsReadOnly),"Registered identity and site fields are genuinely read-only");
    var purposeInput=Descendants((DependencyObject)view.FindName("DraftPanel")).OfType<TextBox>().Single(t=>System.Windows.Data.BindingOperations.GetBindingExpression(t,TextBox.TextProperty)?.ParentBinding.Path.Path=="Purpose");
    Check(!purposeInput.IsReadOnly && !Action("updateCharter").Available && Action("reviewCharter").Available,"Existing charter terms editable with explicit review required");
    view.Draft.Purpose="Reviewed source-audit purpose";view.Draft.GrowthPolicy="Paused";var charterRevision=snapshot.State!.Revision;Click("reviewCharter");
    Check(charterReview is not null && snapshot.State.Revision==charterRevision && snapshot.State.Colonies.Single().Charter.Purpose!="Reviewed source-audit purpose","Charter review is pure and does not mutate selected authority");
    Check(charterRequests[0].Fields.Count==9 && !charterRequests[0].Fields.ContainsKey("Name") && !charterRequests[0].Fields.ContainsKey("AdoptFacilityIds"),"Charter review omits inert identity/site and adoption edits");
    Check(Action("updateCharter").Available,"Fresh existing-charter review enables its update");
    view.Draft.CashFloor="123456";Layout();Check(!Action("updateCharter").Available,"Changed charter term invalidates reviewed update");
    Click("reviewCharter");nav.SelectedIndex=9;Layout();Check(Action("updateCharter").Available && Action("reviewCharter").Available,"Finance uses the same exact reviewed charter payload");
    string registeredName=snapshot.State.Colonies.Single().Name;string registeredBody=snapshot.State.Colonies.Single().Site.Body;Click("updateCharter");
    Check(snapshot.State.Colonies.Single().Charter.Purpose=="Reviewed source-audit purpose" && snapshot.State.Colonies.Single().Charter.CashFloor==123456 && snapshot.State.Colonies.Single().Charter.GrowthPolicy=="disabled" && snapshot.State.Colonies.Single().Name==registeredName && snapshot.State.Colonies.Single().Site.Body==registeredBody,"Charter applies edited terms without silently promising a rename or site move");
    // Controller retention must survive a changed global UI quote and revision,
    // while a changed save context or operation payload must never be sent.
    var charterOperations=new ColonyManagementCharterOperations();
    ColonyCommand? pendingCharter=null;int charterAttempts=0;
    view.SubmitAsync=request=>
    {
        if(request.Kind=="reviewCharter")
        {
            charterReview=ColonyManagementCharter.Review(snapshot,request);view.ApplySnapshot(CharterPresentation() with {QuoteId=charterReview.Id,QuoteKind="updateCharter",QuoteReview=charterReview.Description});
            return Task.FromResult(new ColonyManagementResponse(true,"Synthetic review only"));
        }
        charterAttempts++;var command=charterOperations.Prepare(snapshot,request,charterAttempts==1 ? charterReview!.Fields : new Dictionary<string,string>());
        if(charterAttempts==1)
        {
            Check(charterOperations.ContainsExact(request),"Charter controller retains the exact original submitted request");
            bool changedRejected=false;
            try {charterOperations.Prepare(snapshot,request with {Fields=new Dictionary<string,string>(request.Fields){["Purpose"]="Changed pending payload"}},charterReview!.Fields);}
            catch(InvalidOperationException){changedRejected=true;}
            Check(changedRejected,"Charter controller refuses operation ID reuse with changed payload");
            string originalContext=snapshot.ContextKey;snapshot.ContextKey="synthetic/different-save";bool contextRejected=false;
            try {charterOperations.Prepare(snapshot,request,null);}
            catch(InvalidOperationException){contextRejected=true;}
            snapshot.ContextKey=originalContext;
            Check(contextRejected,"Charter controller holds uncertain operations across another selected save");
        }
        if(charterAttempts==1)pendingCharter=command;else Check(command.OperationId==pendingCharter!.OperationId && ColonyStateCodec.CommandHash(command)==ColonyStateCodec.CommandHash(pendingCharter),"Charter retry preserves exact reviewed fields, revision and operation ID");
        var result=ColonyEngine.Execute(snapshot.State!,command,Environment(snapshot));Check(result.Outcome==(charterAttempts==1 ? "accepted" : "duplicate"),"Unknown charter reply reconciles exact receipt without replacement update");
        snapshot.State=result.State;view.ApplySnapshot(CharterPresentation());return Task.FromResult(new ColonyManagementResponse(charterAttempts>1,"Synthetic unknown first reply"));
    };
    view.Draft.Purpose="Exact pending charter update";Click("reviewCharter");Click("updateCharter");Layout();
    Check(retry.Visibility==Visibility.Visible && retry.IsEnabled && !Action("updateCharter").Available,"Unknown charter reply holds new mutations and exposes exact retry");
    view.Draft.Purpose="Unreviewed edit while old operation is pending";
    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();Check(charterAttempts==2 && retry.Visibility==Visibility.Collapsed,"Exact charter retry settles once after revision advances");
    Check(snapshot.State!.Colonies.Single().Charter.Purpose=="Exact pending charter update" && view.Draft.Purpose=="Unreviewed edit while old operation is pending","Pending charter retry preserves old reviewed authority and newer local draft independently");
    var development=ColonyStateCodec.Copy(snapshot.State);development.Colonies.Single().Charter.Sandbox=true;
    var developmentSnapshot=new ColonyManagementSnapshot {State=development,ContextKey=snapshot.ContextKey,ObservedUt=snapshot.ObservedUt,DevelopmentMode=true,Capabilities=snapshot.Capabilities};
    var preserved=ColonyManagementCharter.Review(developmentSnapshot,new(Id(),snapshot.ContextKey,colony.Id,development.Revision,"reviewCharter",null,null,view.Draft.Snapshot()));
    Check(preserved.Fields["Sandbox"]=="true","Hidden existing certification override is preserved by charter review");
    int matrix=0;view.ApplySnapshot(CharterPresentation());
    foreach(var size in new[]{new Size(860,680),new Size(1180,930),new Size(960,540)})
    {
        window.Width=size.Width;window.Height=size.Height;
        foreach(int page in Enumerable.Range(0,10))
        {
            nav.SelectedIndex=page;Layout();var table=grid.TransformToAncestor(view).TransformBounds(new Rect(grid.RenderSize));var footer=((FrameworkElement)view.FindName("ActionArea")).TransformToAncestor(view).TransformBounds(new Rect(((FrameworkElement)view.FindName("ActionArea")).RenderSize));
            if(grid.ActualHeight<40 || table.Bottom>footer.Top+.5)throw new Exception("Source-audit table/action overlap at "+size+" page "+page);
            matrix++;
        }
    }
    Check(matrix==30,"Ten Manager areas retain separate usable table/actions at three logical fixture sizes; OS DPI acceptance remains unverified");
    view.ApplySnapshot(newView);nav.SelectedIndex=1;Layout();Check(!((TextBox)view.FindName("CharterNameInput")).IsReadOnly && !((TextBox)view.FindName("CharterBodyInput")).IsReadOnly,"New founding identity/site remain editable after existing charter workflow");
    Console.WriteLine(checks+" targeted checks passed.");window.Close();app.Shutdown();
}

static ColonyEnvironment Environment(ColonyManagementSnapshot s)=>new(){WorldId=s.State!.WorldId,ContextKey=s.ContextKey,Ut=s.ObservedUt,AdoptableFacilities=s.AdoptableFacilities,FacilitySites=s.FacilitySites,BodyRadiiMeters=s.BodyRadiiMeters,People=s.People,Support=s.Support};
static IEnumerable<DependencyObject> Descendants(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}

// Load production resources without ever creating the operational MainWindow.
sealed class FixtureApplication : Application
{
    protected override void OnStartup(StartupEventArgs e) { }
}
