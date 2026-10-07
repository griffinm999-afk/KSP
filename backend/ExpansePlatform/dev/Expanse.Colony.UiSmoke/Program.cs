using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Expanse.Clock.Manager;
using Expanse.Domain.Colonies;

if (args.Length != 1 && !(args.Length==3 && args[0]=="--snapshot")) throw new ArgumentException("Pass an isolated screenshot output directory, or --snapshot recorded-response.json output-directory.");
Exception? failure = null;
var thread = new Thread(() => { try { if(args.Length==1)Run(args[0]);else RunRecorded(args[1],args[2]); } catch (Exception ex) { failure = ex; Application.Current?.Shutdown(); } });
thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
Console.WriteLine(args.Length==1 ? "Colony WPF layout and draft/reconciliation checks passed. Synthetic fixture images; no game or Host connection." : "Ten native WPF pages rendered from recorded actual KSP snapshot. Historical observation only; commands disabled and no game/Host connection.");

static void RunRecorded(string source,string output)
{
 var response=ColonyManagementWire.DecodeResponse(File.ReadAllBytes(source));var snapshot=response.Snapshot ?? throw new Exception("Recorded file has no management snapshot.");var state=snapshot.State ?? throw new Exception("Recorded file has no state.");
 Directory.CreateDirectory(output);var app=new App();app.InitializeComponent();var view=new ColonyManagementView();
 var window=new Window {Content=view,Width=1180,Height=930,Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowActivated=false,WindowStyle=WindowStyle.None,Background=Brushes.White};window.Show();
 var colony=state.Colonies.FirstOrDefault();var capabilities=snapshot.Capabilities.Where(c=>c.ColonyId.Length==0 || c.ColonyId==colony?.Id).Select(c=>new ColonyManagementAction(c.Kind,c.Label,false,"Recorded snapshot only; commands are disabled.",c.TargetId.Length>0 ? c.TargetId : null)).ToArray();
 var presentation=ColonyManagementAdapter.Present(state,snapshot.ContextKey,colony?.Id,"Recorded KSP observation · commands disabled","HISTORICAL REAL DATA · "+Path.GetFileName(source)+" · No live game or Host connection; separate clock-flow overlay unavailable in this snapshot.",capabilities,snapshot.Templates.ToArray(),observedFunds:snapshot.AvailableFunds,bodyRadiiMeters:snapshot.BodyRadiiMeters,peopleEnvironment:snapshot.People,servicesEnvironment:snapshot.Services,economyPolicies:snapshot.EconomyPolicies.ToArray(),wolfEnvironment:snapshot.Wolf);
 presentation=ColonyPhysicalStockPresentation.Present(presentation,snapshot,capabilities);
 // Reuse the live pure helpers without constructing a connected MainWindow.
 foreach(var methodName in new[]{"PresentFoundingResidents","PresentProduction"})
 {
  var method=typeof(MainWindow).GetMethod(methodName,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static,null,new[]{typeof(ColonyManagementPresentation),typeof(ColonyManagementSnapshot)},null) ?? throw new Exception("Recorded renderer cannot find live presentation helper: "+methodName);
  presentation=(ColonyManagementPresentation)(method.Invoke(null,new object[]{presentation,snapshot}) ?? throw new Exception("Live presentation helper returned no recorded view: "+methodName));
 }
 presentation=ColonyManagementAdoption.Present(presentation,snapshot);
 view.ApplySnapshot(presentation);var nav=(ListBox)view.FindName("Navigation");var grid=(DataGrid)view.FindName("RowsGrid");
 foreach(var size in new[]{new Size(1180,930),new Size(960,540)})
 {
  window.Width=size.Width;window.Height=size.Height;
  for(int page=0;page<10;page++)
  {
   nav.SelectedIndex=page;window.UpdateLayout();view.Measure(size);view.Arrange(new Rect(0,0,size.Width,size.Height));view.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background);
   bool recordsExpanded=((Expander)view.FindName("RowsExpander")).IsExpanded;
   Console.WriteLine(FormattableString.Invariant($"RECORDED_LAYOUT {presentation.Sections[page].Key} {size.Width}x{size.Height} recordsExpanded={recordsExpanded} tableHeight={grid.ActualHeight:R} expandedTableUsable={!recordsExpanded || grid.ActualHeight>=40}"));
   if(Descendants(view).OfType<Button>().Any(b=>b.IsEnabled && b.Tag is ColonyManagementAction))throw new Exception("Recorded snapshot enabled a mutation.");
   var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"recorded-"+presentation.Sections[page].Key+"-"+size.Width+"x"+size.Height+".png"));encoder.Save(file);
  }
 }
 window.Close();app.Shutdown();
}

static void Run(string output)
{
 Directory.CreateDirectory(output);
 var app = new App(); app.InitializeComponent();
 var view = new ColonyManagementView();
 var window = new Window { Content = view, Width = 860, Height = 680, Left = -10000, Top = -10000,
  WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, WindowStyle = WindowStyle.None, Background = Brushes.White };
 window.Show();
 var nav = (ListBox)view.FindName("Navigation");
 var grid = (DataGrid)view.FindName("RowsGrid");
 var actions = (ItemsControl)view.FindName("Actions");
 var retry = (Button)view.FindName("RetryButton");
 var sections = new[] { "overview", "founding", "construction", "people", "production", "power", "inventory", "trade", "maintenance", "finance" }
  .Select((key,i) => new ColonyManagementSection(key, nav.Items[i].ToString()!, "SYNTHETIC UI FIXTURE · Tests display and interaction only.",
   Enumerable.Range(0,100).Select(n => new ColonyManagementRow("row-"+n,
    n == 0 ? "Long named settlement facility with a deployment envelope and reserve storage" : "Fixture item "+n,
    n%3 == 0 ? "Waiting: funded receiving capacity" : "Observed / review required", "1,234,567.89 / 2,000,000", n%2 == 0 ? "Estimated" : "Loaded snapshot",
    "Fixture detail: owner is physical tanks; reservations are commitments, not extra stock. No throughput, certified housing or connected utility grid is inferred. Prerequisite: verify service adapter, assigned Engineer, reserve availability and scene context." )).ToArray(),
   [new("quoteFounding", "Review plan", true, "Read-only fixture response"), new("serviceFacility", "Service facility", false, "Provider is not qualified in this fixture.", "row-0")], "No observed rows.",
   key == "people" ? [new("homeId", "Certified home ID", "", "Use a commissioned home with available capacity."),new("jobId", "Job ID", "", "Skills and physical seat are rechecked by the authority.")] : null )).ToArray();
 var state = new ColonyManagementPresentation("fixture-world/branch/epoch", "Fixture data", "SYNTHETIC · No operational connection.", "colony-1", 1,
  [new("colony-1", "Long named Minmus Greater Flats fixture colony")], sections, "fixture-quote", "Fixture quote: funding and materials itemized by backend; no funds are committed by this screenshot.");
 var ids = new List<string>();
 view.SubmitAsync = request => { ids.Add(request.OperationId); return Task.FromResult(new ColonyManagementResponse(ids.Count > 1, "Fixture result; no external effect.")); };
 view.ApplySnapshot(state);
 view.Draft.Name = "Unsaved founding draft";
 view.Draft.Budget = "54321";
 view.ApplySnapshot(state with { Revision = 2 });
 if (view.Draft.Name != "Unsaved founding draft" || view.Draft.Budget != "54321") throw new Exception("Refresh discarded draft edits.");
 void Layout() { window.UpdateLayout(); view.Measure(new Size(window.Width,window.Height)); view.Arrange(new Rect(0,0,window.Width,window.Height)); view.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.Background); view.UpdateLayout(); }
 void Capture(string name)
 {
  Layout(); var bitmap = new RenderTargetBitmap((int)window.Width,(int)window.Height,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
  var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output,name)); encoder.Save(stream);
 }
 foreach (var size in new[] { new Size(860,680),new Size(1180,930),new Size(960,540) })
 {
  window.Width = size.Width; window.Height = size.Height;
  for (int i=0;i<sections.Length;i++)
  {
   nav.SelectedIndex = i; Layout(); grid.SelectedIndex = 0; Layout();
   if (grid.ActualHeight < 40) throw new Exception("Management table has no usable viewport at "+size+" on "+sections[i].Key);
   Capture(sections[i].Key+"-"+size.Width+"x"+size.Height+".png");
  }
 }
 nav.SelectedIndex = 0; Layout();
 var button = Descendants(view).OfType<Button>().First(b => Equals(b.Content,"Review plan"));
 button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
 if (retry.Visibility != Visibility.Visible) throw new Exception("Uncertain response did not retain an operation.");
 if (Descendants(actions).OfType<Button>().Any(b=>b.IsEnabled)) throw new Exception("New actions remained enabled before reconciliation.");
 retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
 if (ids.Count != 2 || ids[0] != ids[1]) throw new Exception("Reconciliation generated a new operation ID.");
 if (retry.Visibility != Visibility.Collapsed) throw new Exception("Terminal readback did not clear pending operation.");
 view.ApplySnapshot(ColonyManagementPresentation.Disconnected("Fixture: KSP and Host unavailable."));
 Capture("disconnected.png");
 if (actions.Items.Count != 0 || grid.Items.Count != 0) throw new Exception("Disconnected page retained actions or authoritative rows.");
 view.ApplySnapshot(state with { Sections = sections.Select(s=>s with {Rows=[],EmptyReason="Automatic order waiting: cash reserve protected; no cargo is in transit."}).ToArray() });
 nav.SelectedIndex = 7; Capture("trade-empty-blocker.png");
 if (!((TextBlock)view.FindName("EmptyText")).Text.Contains("cash reserve")) throw new Exception("Empty trade page hid its waiting reason.");
 var model=ColonyEngine.Create(Guid.NewGuid().ToString("D"),100);
 var facilityId=Guid.NewGuid().ToString("D");var plotId=Guid.NewGuid().ToString("D");
 var colonyModel=new ColonyRecord {Id=Guid.NewGuid().ToString("D"),Name="SYNTHETIC adapter fixture",Site=new ColonySite {Body="Minmus",Biome="Greater Flats",Latitude=0,Longitude=0},SupportCommissionedUt=100,SupportAccountedUt=100,SupportMicroUnitsPerPersonDay=ColonyLimits.Units,SupportStatus="Supported"};
 colonyModel.Facilities.Add(new ColonyFacility {Id=facilityId,VesselId=Guid.NewGuid().ToString("D"),Name="A long named commissioned habitat with certified homes",State="operational",CertifiedHomes=8,PlotId=plotId,Qualification=new() {HousingCertified=true,Context="fixture-loaded",EvidenceHash="synthetic-certification"}});
 colonyModel.Residents.Add(new ColonyResident {Id=Guid.NewGuid().ToString("D"),RosterId="Synthetic ordinary resident",Name="Synthetic ordinary resident",HomeFacilityId=facilityId,Trait="Engineer"});
 colonyModel.Stock.Add(new ColonyStock {Resource="Supplies",Amount=400*ColonyLimits.Units,Capacity=500*ColonyLimits.Units});
 colonyModel.Plots.Add(new ColonyPlot {Id=plotId,Latitude=.002,Longitude=.002,WidthMeters=30,LengthMeters=20,Heading=45,SurveyHash="synthetic-survey",OccupiedBy=facilityId});
 model.Colonies.Add(colonyModel);
 model.Suppliers.Add(new ColonySupplier {Id="fixture-supplier",Resource="Supplies",Available=100*ColonyLimits.Units,FundsPerUnit=100,FreightFunds=1000,ConcurrentCapacity=2,TravelSeconds=64800});
 ColonyStateCodec.Validate(model);
 var services=new ColonyServicesEnvironment {Targets=[new(){ColonyId=colonyModel.Id,FacilityId=facilityId,PartId=42,PartName="SYNTHETIC installed tank and long facility display name",DestinationResource="Machinery",SourceResource="Machinery",Amount=10*ColonyLimits.Units,Capacity=100*ColonyLimits.Units,Provider="SYNTHETIC provider",Reason="MODEL RENDERING ONLY; no actual service entitlement."}]};
 var adapted=ColonyManagementAdapter.Present(model,"fixture-model/epoch",colonyModel.Id,"Synthetic adapter fixture","MODEL RENDERING ONLY · No KSP/Host/save connection.",capabilities:[new("reviewService","Review installed service",false,"Synthetic adapter fixture only."),new("configureServicePolicy","Apply service policy",false,"Synthetic adapter fixture only.")],observedFunds:1000000,bodyRadiiMeters:new Dictionary<string,double>{{"Minmus",60000}},servicesEnvironment:services);
 view.ApplySnapshot(adapted);
 var overview=adapted.Sections.Single(s=>s.Key=="overview");
 if(overview.Metrics?.Length!=4 || !overview.Metrics[0].Detail.Contains("8 commissioned") || !overview.Metrics[1].Value.StartsWith("400"))throw new Exception("Actual adapter lost certified housing or policy reserve calculation.");
 foreach(var size in new[]{new Size(860,680),new Size(1180,930),new Size(960,540)})
 {
  window.Width=size.Width;window.Height=size.Height;
  foreach(var page in Enumerable.Range(0,10))
  {
   nav.SelectedIndex=page;Layout();
   if(grid.ActualHeight<40)throw new Exception("Native adapter page lost table viewport at "+size+" page "+page);
   Capture("adapter-"+adapted.Sections[page].Key+"-"+size.Width+"x"+size.Height+".png");
  }
 }
 nav.SelectedIndex=2;((Expander)view.FindName("SiteMapPanel")).IsExpanded=true;Capture("adapter-survey-map.png");
 // Real task controls must preserve independent edits when users switch tasks
 // or observations refresh. A quote for a different task cannot enable payment.
 var workflowSection=adapted.Sections.Single(s=>s.Key=="people") with {
  Actions=[new("reviewSupport","Review support",true,""),new("commissionSupport","Commission support",true,""),new("reviewRecruitment","Review recruitment",true,""),new("recruitResident","Recruit resident",true,"")],
  Fields=[new("RosterId","Kerbal","",""),new("HomeFacilityId","Home","",""),new("RouteId","Route","","")]
 };
 var workflowSnapshot=adapted with {Sections=adapted.Sections.Select(s=>s.Key=="people" ? workflowSection : s).ToArray(),QuoteId="support-review",QuoteKind="commissionSupport",QuoteReview="Synthetic supported task quote; no native effect."};
 view.ApplySnapshot(workflowSnapshot);nav.SelectedIndex=3;Layout();
 var picker=(ComboBox)view.FindName("WorkflowPicker");var fields=(ItemsControl)view.FindName("OperationFields");
 if(picker.Items.Count!=2 || fields.Items.Count!=0)throw new Exception("Support task exposed recruitment fields.");
 picker.SelectedValue="recruit";Layout();
 var roster=fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="RosterId");roster.Value="Unsaved ordinary Kerbal";
 if(actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind=="recruitResident").Available)throw new Exception("Support quote enabled a different funded task.");
 picker.SelectedValue="support";Layout();picker.SelectedValue="recruit";Layout();view.ApplySnapshot(workflowSnapshot with {Revision=workflowSnapshot.Revision+1});Layout();
 if(!ReferenceEquals(roster,fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="RosterId")) || roster.Value!="Unsaved ordinary Kerbal" || !Equals(picker.SelectedValue,"recruit"))throw new Exception("Task switch or polling discarded an edit or control identity.");
 view.ApplySnapshot(workflowSnapshot with {QuoteId="recruit-review",QuoteKind="recruitResident"});Layout();
 if(!actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind=="recruitResident").Available)throw new Exception("Fresh matching reviewed task did not enable approval.");
 roster.Value="Changed after review";Layout();
 if(actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind=="recruitResident").Available)throw new Exception("Edited recruitment draft retained funded approval.");
 // Typed startup requests carry the edited resident choices and paid recurring
 // terms. Polling preserves the same named checkbox, including a stale choice.
 var foundingSection=adapted.Sections.Single(s=>s.Key=="founding") with {
  Actions=[new("reviewFoundingPlan","Review complete startup",true,"")],
  Fields=[new("FoundingFillTarget","Fill target","true",""),new("FoundingArrivalCount","Paid arrivals","0",""),new("StartupMachinery","Machinery buffer (units)","0",""),new("StartupMaterialKits","MaterialKits buffer (units)","0",""),new("StartupUranium","Uranium buffer (units)","0","")]
 };
 var foundingSnapshot=adapted with {Sections=adapted.Sections.Select(s=>s.Key=="founding" ? foundingSection : s).ToArray(),QuoteId=null,QuoteKind=null,
  FoundingExistingCandidates=[new("Ordinary Ada","Ordinary Ada · Engineer","Current actual cabin")],FoundingRecruitCandidates=[new("Applicant Grace","Applicant Grace · Scientist","Available named Applicant")]};
 view.ApplySnapshot(foundingSnapshot);nav.SelectedIndex=1;Layout();
 var existingChoices=(ItemsControl)view.FindName("FoundingExistingChoices");var recruitChoices=(ItemsControl)view.FindName("FoundingRecruitChoices");
 var ordinary=existingChoices.Items.Cast<ColonyManagementAdoptionSelection>().Single();ordinary.Selected=true;recruitChoices.Items.Cast<ColonyManagementAdoptionSelection>().Single().Selected=true;
 var arrivals=fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="FoundingArrivalCount");arrivals.Value="1";
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="StartupMachinery").Value="1.234567";
 view.ApplySnapshot(foundingSnapshot with {Revision=foundingSnapshot.Revision+1});Layout();
 if(!ReferenceEquals(ordinary,existingChoices.Items[0]) || !ordinary.Selected || !ReferenceEquals(arrivals,fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="FoundingArrivalCount")) || arrivals.Value!="1")throw new Exception("Polling discarded typed startup edits.");
 ColonyManagementRequest? startupRequest=null;view.SubmitAsync=request=>{startupRequest=request;return Task.FromResult(new ColonyManagementResponse(true,"Synthetic typed request only; no external effect."));};
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Review complete startup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(startupRequest?.FoundingIntent is not {NewArrivalCount:1,FillPopulationTarget:true} intent || !intent.ExistingResidentRosterIds.SequenceEqual(new[]{"Ordinary Ada"}) || !intent.RecruitRosterIds.SequenceEqual(new[]{"Applicant Grace"}) || intent.Startup?.MachineryReserveMicroUnits!=1234567)throw new Exception("Startup request lost named people or converted resource units incorrectly.");
 view.ApplySnapshot(foundingSnapshot with {FoundingExistingCandidates=[]});Layout();
 if(!ReferenceEquals(ordinary,existingChoices.Items[0]) || !ordinary.Selected || !ordinary.Name.Contains("unavailable"))throw new Exception("Unavailable selected person was silently discarded or still labeled current.");
 window.Width=960;window.Height=540;Capture("founding-resident-draft-960x540.png");if(grid.ActualHeight<40)throw new Exception("Typed startup form collapsed the compact table viewport.");
 // An itemized expansion review must retain its exact selectable saved row.
 // A quote for another proposal cannot enable this proposal's approval.
 string proposalId=Guid.NewGuid().ToString("D");
 var proposalRow=new ColonyManagementRow(proposalId,"SYNTHETIC expansion review","proposed","Paid scope needs review","UI fixture only","Reject closes this exact review; a later cadence may generate an independent review.");
 var growthSection=adapted.Sections.Single(s=>s.Key=="overview") with {Rows=[proposalRow],Actions=[new("reviewGrowthProposal","Review selected expansion",true,"",proposalId),new("approveGrowthProposal","Approve selected expansion",true,"",proposalId),new("deferGrowthProposal","Defer selected expansion",true,"",proposalId)],Fields=[new("DecisionReason","Decision reason","Wait for mission completion",""),new("DelaySeconds","Delay (game seconds)","21600","")]};
 var growthSnapshot=adapted with {Sections=adapted.Sections.Select(s=>s.Key=="overview" ? growthSection : s).ToArray(),QuoteId="SYNTHETIC itemized bill",QuoteKind="approveGrowthProposal",QuoteTargetId=proposalId,QuoteReview="Synthetic exact-item bill; no game connection."};
 var growthQuote=new ColonyPlanningQuote {Kind="growth",CanApprove=true,Rationale="Synthetic fixture render only",Buildings=[new(){Id="reviewed-habitat",Role="housing",Name="SYNTHETIC paid habitat",Funds=1000,Homes=6,LaborSeconds=120}]};
 var presenter=typeof(MainWindow).GetMethod("PresentPlanningReview",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
 growthSnapshot=(ColonyManagementPresentation)presenter.Invoke(null,new object?[]{growthSnapshot,growthQuote,proposalId})!;view.ApplySnapshot(growthSnapshot);nav.SelectedIndex=0;Layout();grid.SelectedItem=grid.Items.Cast<ColonyManagementRow>().Single(r=>r.Id==proposalId);Layout();
 if(!actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind=="approveGrowthProposal").Available)throw new Exception("Matching proposal bill lost its exact approval action or saved selectable row.");
 view.ApplySnapshot(growthSnapshot with {QuoteTargetId=Guid.NewGuid().ToString("D")});Layout();
 if(actions.Items.Cast<ColonyManagementAction>().Single(a=>a.Kind=="approveGrowthProposal").Available)throw new Exception("Another proposal's quote enabled this exact target.");
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="DecisionReason").Value="Wait for mission completion";fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="DelaySeconds").Value="21600";Layout();
 ColonyManagementRequest? deferRequest=null;view.SubmitAsync=request=>{deferRequest=request;return Task.FromResult(new ColonyManagementResponse(true,"Synthetic deferral only."));};
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Defer selected expansion")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(deferRequest?.TargetId!=proposalId || deferRequest.Kind!="deferGrowthProposal" || deferRequest.Fields["DelaySeconds"]!="21600" || deferRequest.Fields["DecisionReason"]!="Wait for mission completion")throw new Exception("Deferral lost its exact review, deadline duration or saved reason.");
 Capture("growth-proposal-review-960x540.png");if(grid.ActualHeight<80)throw new Exception("Expansion proposal form/metrics collapsed the compact table viewport.");
 var visibleDecision=Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Defer selected expansion"));
 var decisionBounds=visibleDecision.TransformToAncestor(view).TransformBounds(new Rect(visibleDecision.RenderSize));
 if(decisionBounds.Top<0 || decisionBounds.Bottom>view.ActualHeight || visibleDecision.ActualHeight<25)throw new Exception("Compact expansion decision button is clipped outside the management viewport.");
 // Exercise the production selector from the actual product field builder.
 // This fixture is deliberately disconnected and does not certify production.
 var productionFields=(ColonyManagementField[])typeof(MainWindow).GetMethod("ProductionFields",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.Invoke(null,new object[]{new ColonyManagementSnapshot {
  Production=new(){Reason="SYNTHETIC installed catalog fixture; no native qualification",Recipes=[new(){Id="fixture-cultivation",Name="SYNTHETIC Cultivate(S) package"}]}}})!;
 var productionSection=foundingSection with {Fields=foundingSection.Fields!.Concat(productionFields).ToArray()};
 var productionSnapshot=foundingSnapshot with {Sections=foundingSnapshot.Sections.Select(s=>s.Key=="founding"?productionSection:s).ToArray()};
 view.ApplySnapshot(productionSnapshot);nav.SelectedIndex=1;Layout();
 var productionMode=fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionMode");productionMode.Value="localInvestment";
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionCount").Value="2";
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionHorizonDays").Value="6.5";
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionFertilizerInitial").Value="1.234567";
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionFuelEnabled").Value="false";
 view.ApplySnapshot(productionSnapshot with {Revision=productionSnapshot.Revision+1});Layout();
 if(!ReferenceEquals(productionMode,fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionMode"))||productionMode.Value!="localInvestment")throw new Exception("Production polling discarded the edited selector.");
 ColonyManagementRequest? productionRequest=null;view.SubmitAsync=request=>{productionRequest=request;return Task.FromResult(new ColonyManagementResponse(true,"Synthetic production review only; no external effect."));};
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Review complete startup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(productionRequest?.FoundingIntent?.Production is not {Mode:"localInvestment",RecipeId:"fixture-cultivation",PackageCount:2} typedProduction || typedProduction.ReviewHorizonSeconds!=6.5*ColonyLimits.KerbinDay)throw new Exception("Production review lost typed package, count or game-time horizon.");
 if(typedProduction.Operations is not {RegisterCreatedEndpoints:true,AutomaticIntake:true,AutomaticInputRefill:true} operating || operating.Resources.Count!=3 || operating.Resources.Single(r=>r.Resource=="Fertilizer").InitialReserve!=1234567 || operating.Resources.Single(r=>r.Resource=="Plutonium-238").ReorderEnabled)throw new Exception("Production review lost exact edited operating quantities or explicit declined fuel purchases.");
 window.Width=960;window.Height=540;Capture("founding-production-draft-960x540.png");
 ((ScrollViewer)view.FindName("FormPanel")).ScrollToEnd();Capture("founding-production-selectors-960x540.png");
 var actualSelector=Descendants(fields).OfType<ComboBox>().Single(b=>ReferenceEquals(b.DataContext,productionMode));
 if(!actualSelector.IsVisible||!Equals(actualSelector.SelectedValue,"localInvestment"))throw new Exception("The typed production strategy is not a readable selector.");
 if(grid.ActualHeight<40)throw new Exception("Production draft collapsed the compact table viewport.");
 // Declining refill preserves explicitly purchased initial buffers but removes
 // recurring purchase authority. Declining registration removes both scopes.
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionRefill").Value="false";
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Review complete startup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(productionRequest?.FoundingIntent?.Production?.Operations is not {AutomaticInputRefill:false} declinedRefill || declinedRefill.Resources.Any(r=>r.ReorderEnabled) || declinedRefill.Resources.Single(r=>r.Resource=="Fertilizer").InitialReserve!=1234567)throw new Exception("Declining refill lost an initial buffer or retained recurring purchase authority.");
 fields.Items.Cast<ColonyManagementInput>().Single(f=>f.Key=="ProductionRegister").Value="false";
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Review complete startup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(productionRequest?.FoundingIntent?.Production?.Operations is not {RegisterCreatedEndpoints:false,AutomaticIntake:false,AutomaticInputRefill:false} declinedRegistry || declinedRegistry.Resources.Count!=0)throw new Exception("Declining inventory registration retained automatic operations or hidden buffers.");
 productionMode.Value="importOnly";
 Descendants(actions).OfType<Button>().Single(b=>Equals(b.Content,"Review complete startup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout();
 if(productionRequest?.FoundingIntent?.Production?.Operations is not null)throw new Exception("Import-only review retained local operating authority.");
 window.Close(); app.Shutdown();
}
static IEnumerable<DependencyObject> Descendants(DependencyObject root)
{
 for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var nested in Descendants(child))yield return nested; }
}
