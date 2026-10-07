using System.Windows.Controls;

namespace Expanse.Clock.Manager;

public partial class ColonyManagementView
{
    private sealed record ManagementWorkflow(string Key,string Name,string[] Actions,string[] Fields,bool Charter=false);
    private readonly Dictionary<string,string> workflowSelections=new(StringComparer.Ordinal);
    private ManagementWorkflow[] displayedWorkflows=[];
    private bool applyingWorkflow;
    private bool showAdvancedPlanning;
    private ManagementWorkflow? CurrentWorkflow=>WorkflowPicker?.SelectedItem as ManagementWorkflow;
    private bool HasWorkflowDefinitions=>Workflows(PageKey,snapshot.ColonyId is not null).Length>0;
    private static ManagementWorkflow[] Workflows(string page,bool registered=false)=>page switch
    {
        "overview"=>[
            new("summary","Colony status",[],[]),
            new("growth-proposal","Manage an expansion review",["reviewGrowthProposal","approveGrowthProposal","deferGrowthProposal","rejectGrowthProposal","reconsiderGrowthProposal","surveyGrowthPlan"],["DecisionReason","DelaySeconds"]),
            new("growth-manual","Review a new housing expansion",["reviewGrowthPlan","approveGrowthPlan","surveyGrowthPlan"],[]),
            new("plan-cancel","Cancel future plan commitments",["cancelColonyPlan"],[])],
        "founding"=>[
            new("existing","Existing base status",[],[]),
            new("charter",registered ? "Edit existing charter" : "Step 1: Register charter",["reviewFounding","foundColony","reviewCharter","updateCharter"],[],true),
            new("adoption","Add an existing landed facility",["reviewAdoption","adoptFacility"],[]),
            new("habitat","Qualify existing habitat homes",["reviewAdoptedHabitat","qualifyAdoptedHabitat"],[]),
            new("staging","Purchase receiving stores",["reviewLogistics","activateLogistics"],["PolicyId"]),
            new("startup","Plan new startup construction (advanced)",["reviewFoundingPlan","approveFoundingPlan","surveyFoundingPlan","cancelColonyPlan"],["FoundingFillTarget","FoundingArrivalCount","FoundingRouteId","StartupReorder","StartupReorderPoint","StartupTarget","StartupReorderCadence","StartupService","StartupServiceFill","StartupServiceCadence","StartupMachinery","StartupMaterialKits","StartupUranium","StartupLocal","ProductionMode","ProductionRecipe","ProductionCount","ProductionHorizonDays","ProductionRegister","ProductionIntake","ProductionRefill","ProductionFertilizerInitial","ProductionFertilizerPoint","ProductionFertilizerTarget","ProductionFertilizerEnabled","ProductionMachineryInitial","ProductionMachineryPoint","ProductionMachineryTarget","ProductionMachineryEnabled","ProductionFuelInitial","ProductionFuelPoint","ProductionFuelTarget","ProductionFuelEnabled","ProductionOperatingDays"])],
        "construction"=>[
            new("build","Review a building package",["reviewConstruction","approveConstruction","cancelConstruction"],["TemplateId","PlotId"]),
            new("placement-retry","Retry original unattempted placement",["retryConstructionPlacement"],[]),
            new("survey","Survey or resurvey terrain",["surveyPlot"],["TemplateId","SurveyPlotId","Latitude","Longitude","Heading"])],
        "people"=>[
            new("habitat","Qualify existing habitat homes",["reviewAdoptedHabitat","qualifyAdoptedHabitat"],[]),
            new("support","Start resident support",["reviewSupport","commissionSupport"],[]),
            new("recruit","Recruit a resident",["reviewRecruitment","recruitResident","cancelPassenger"],["RosterId","HomeFacilityId","HomePartId","RouteId"]),
            new("assign","Assign a present crew member",["assignResident"],["RosterId","HomeFacilityId","HomePartId","JobFacilityId","WorkPartId","ExplicitMissionCrewAssignment"]),
            new("worker-shift","Move an existing colony worker to a workplace",["reviewWorkerTransfer","transferColonyWorker","cancelWorkerTransfer"],["RosterId","JobFacilityId","WorkPartId","ExplicitMissionCrewAssignment"]),
            new("depart","Arrange a paid departure",["reviewDeparture","departResident","cancelPassenger"],["RouteId"])],
        "inventory"=>[
            new("transfer","Transfer physical tanks and colony reserves",["reviewPhysicalTransfer","transferColonyStock","cancelPhysicalTransfer"],["LocalStockId","Direction","TransferAmount"]),
            new("wolf","Build or review WOLF supply capacity",["reviewWolfSupply","approveWolfSupply","cancelWolfSupply","reviewWolfReplan","replanPaidWolfSupply"],["Resource","DesiredAvailable"]),
            new("local-policy","Configure local reserve procurement",["configurePhysicalProcurement"],["PhysicalPolicyEnabled"])],
        "trade"=>[
            new("import","Review a resource import",["reviewTrade","approveTrade","cancelTrade"],["SupplierId","Amount"]),
            new("reserve","Set automatic reserve replenishment",["configureReorderPolicy"],["Resource","Enabled","ReorderPoint","TargetAmount","CadenceSeconds"]),
            new("staging","Purchase receiving stores",["reviewLogistics","activateLogistics"],["PolicyId"])],
        "maintenance"=>[
            new("service","Deliver owned reserves to an installed tank",["reviewService","serviceFacility","cancelService"],["ServiceTank","Amount"]),
            new("service-policy","Configure recurring tank service",["configureServicePolicy"],["AutomaticEnabled","CadenceSeconds","TargetFillPercent"])],
        _=>[]
    };
    private string WorkflowSelectionKey=>snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0"+PageKey;
    private void RefreshWorkflows(ColonyManagementSection? section)
    {
        if(WorkflowPicker is null)return;
        var choices=Workflows(PageKey,snapshot.ColonyId is not null).Where(w=>
            (showAdvancedPlanning || w.Key is not ("startup" or "growth-manual" or "growth-proposal" or "build" or "survey" or "wolf")) &&
            (w.Key=="summary" || w.Key=="existing" && snapshot.ColonyId is not null ||
             section?.Actions.Any(a=>w.Actions.Contains(a.Kind))==true || w.Key=="adoption" && snapshot.ColonyId is not null)).ToArray();
        applyingWorkflow=true;
        try
        {
            // Preserve control identity during polling, so keyboard focus and
            // open selectors do not bounce on each native snapshot.
            if(!displayedWorkflows.Select(w=>(w.Key,w.Name)).SequenceEqual(choices.Select(w=>(w.Key,w.Name))))
            {
                displayedWorkflows=choices;WorkflowPicker.ItemsSource=choices;
            }
            string? key=workflowSelections.TryGetValue(WorkflowSelectionKey,out var saved) ? saved : null;
            if(key is null && PageKey=="founding" && snapshot.ColonyId is null)key="charter";
            WorkflowPicker.SelectedValue=choices.Any(w=>w.Key==key) ? key : choices.FirstOrDefault()?.Key;
            WorkflowPickerPanel.Visibility=choices.Length>1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            AdvancedPlanningToggle.IsChecked=showAdvancedPlanning;
        }
        finally {applyingWorkflow=false;}
    }
    private void Workflow_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(applyingWorkflow || CurrentWorkflow is null)return;
        SetActionFeedback("");
        workflowSelections[WorkflowSelectionKey]=CurrentWorkflow.Key;RenderPage();
    }
    private void AdvancedPlanning_Changed(object sender,System.Windows.RoutedEventArgs e)
    {
        if(applyingWorkflow || AdvancedPlanningToggle is null)return;
        showAdvancedPlanning=AdvancedPlanningToggle.IsChecked==true;
        RenderPage();
    }
    private bool WorkflowShowsAction(ColonyManagementAction action)=>
        (!HasWorkflowDefinitions && CurrentWorkflow is null || CurrentWorkflow?.Actions.Contains(action.Kind)==true ||
            PageKey=="overview" && CurrentWorkflow?.Key=="summary" && action.TargetId is not null &&
            action.TargetId==(RowsGrid.SelectedItem as ColonyManagementRow)?.Id &&
            action.Kind is "reviewGrowthProposal" or "approveGrowthProposal") &&
        !(snapshot.ColonyId is not null && action.Kind is "foundColony" or "reviewFounding") &&
        !(snapshot.ColonyId is null && action.Kind is "updateCharter" or "reviewCharter");

    private static bool BasicStartupField(string key)=>key is "FoundingFillTarget" or "FoundingArrivalCount" or "FoundingRouteId" or "ProductionMode";
    private void RefreshAdvancedFields()
    {
        if(OperationFields is null || AdvancedOperationFields is null || StartupAdvanced is null || CharterAdvanced is null)return;
        bool startup=PageKey=="founding" && CurrentWorkflow?.Key=="startup";
        var basic=startup ? displayedInputs.Where(i=>BasicStartupField(i.Key)).ToArray() : displayedInputs;
        if(!basic.SequenceEqual(OperationFields.ItemsSource?.Cast<ColonyManagementInput>() ?? []))OperationFields.ItemsSource=basic;
        bool local=displayedInputs.FirstOrDefault(i=>i.Key=="ProductionMode")?.Value is not (null or "importOnly");
        var advanced=startup ? displayedInputs.Where(i=>!BasicStartupField(i.Key) && (!i.Key.StartsWith("Production",StringComparison.Ordinal) || local)).ToArray() : [];
        if(!advanced.SequenceEqual(AdvancedOperationFields.ItemsSource?.Cast<ColonyManagementInput>() ?? []))AdvancedOperationFields.ItemsSource=advanced;
        StartupAdvanced.Visibility=startup ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        int changed=displayedInputs.Count(i=>!BasicStartupField(i.Key) && (Section?.Fields ?? []).Any(f=>f.Key==i.Key && f.Value!=i.Value));
        StartupAdvanced.Header=changed>0 ? $"Advanced startup settings — {changed} changed" : "Advanced startup settings — defaults";
        var values=draft.Snapshot();
        bool charterChanged=new[]{"Population","Budget","CashFloor","SpendingLimit","ResidentLimit","VisitorLimit","ReserveDays","GrowthPolicy"}
            .Any(k=>values.TryGetValue(k,out var v) && snapshot.DraftDefaults is not null && snapshot.DraftDefaults.TryGetValue(k,out var original) && original!=v);
        CharterAdvanced.Header=charterChanged ? "Advanced charter policy — changed" : snapshot.ColonyId is null ? "Advanced charter policy — defaults" : "Advanced charter policy — current saved terms";
        CharterPolicySummary.Text=$"Resident target {draft.Population}; founding spending cap {draft.Budget} funds (no funds granted); protected cash {draft.CashFloor}; spending limit {draft.SpendingLimit}; population ceiling {draft.ResidentLimit}; visitor allowance {draft.VisitorLimit}; reserve {draft.ReserveDays} Kerbin days; expansion {draft.GrowthPolicy}. All terms appear in the charter review.";
    }
    public void ShowOverview()
    {
        workflowSelections[snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0overview"]="summary";
        Navigation.SelectedIndex=0;RenderPage();
    }
    public void ShowStartupPlanning()
    {
        if(snapshot.ColonyId is null)return;
        showAdvancedPlanning=true;
        workflowSelections[snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0founding"]="startup";
        Navigation.SelectedIndex=Array.FindIndex(Pages,p=>p.Key=="founding");
        RenderPage();
    }
    public void ShowExistingBase()
    {
        if(snapshot.ColonyId is null)return;
        showAdvancedPlanning=false;
        workflowSelections[snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0founding"]="existing";
        Navigation.SelectedIndex=Array.FindIndex(Pages,p=>p.Key=="founding");
        RenderPage();
    }
    private static bool NeedsReviewedQuote(string kind)=>kind is "updateCharter" or "adoptFacility" or "qualifyAdoptedHabitat" or "transferColonyWorker" or "foundColony" or "approveFoundingPlan" or "approveGrowthPlan" or "approveGrowthProposal" or "approveConstruction" or "approveTrade" or "recruitResident" or "departResident" or "serviceFacility" or "activateLogistics" or "approveWolfSupply" or "replanPaidWolfSupply" or "commissionSupport" or "transferColonyStock";
}
