using System.ComponentModel;
using System.Globalization;
using System.Windows;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public partial class ColonyManagementView
{
    private readonly Dictionary<string,HashSet<string>> foundingResidentSelections=new(StringComparer.Ordinal);
    private readonly Dictionary<string,ColonyManagementAdoptionSelection> foundingResidentChoices=new(StringComparer.Ordinal);
    private HashSet<string> FoundingSelections(string kind)
    {
        string key=snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0"+kind;
        if(!foundingResidentSelections.TryGetValue(key,out var values))foundingResidentSelections[key]=values=new(StringComparer.Ordinal);
        return values;
    }
    private void RefreshFoundingResidents()
    {
        bool visible=PageKey=="founding"&&CurrentWorkflow?.Key=="startup"&&snapshot.ColonyId is not null&&snapshot.FoundingExistingCandidates is not null;
        FoundingResidentsPanel.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        if(!visible)return;
        ColonyManagementAdoptionSelection[] Choices(string kind,ColonyManagementAdoptionCandidate[] candidates)
        {
            var selected=FoundingSelections(kind);
            return candidates.Concat(selected.Where(id=>!candidates.Any(c=>c.Id==id)).Select(id=>new ColonyManagementAdoptionCandidate(id,"Selected named Kerbal is unavailable","Keep this selection for diagnostics; review will hold until reconciled."))).Select(c=>
            {
                string key=snapshot.ContextKey+"\0"+snapshot.ColonyId+"\0"+kind+"\0"+c.Id;
                if(!foundingResidentChoices.TryGetValue(key,out var choice))
                {
                    choice=new(c,selected.Contains(c.Id));foundingResidentChoices[key]=choice;
                    var captured=choice;choice.PropertyChanged+=(_,e)=>
                    {
                        if(e.PropertyName!=nameof(ColonyManagementAdoptionSelection.Selected)||applying)return;
                        if(captured.Selected)selected.Add(captured.Id);else selected.Remove(captured.Id);
                        DraftChanged(this,new PropertyChangedEventArgs("FoundingNamedSelections"));
                    };
                }
                choice.Refresh(c);return choice;
            }).ToArray();
        }
        FoundingExistingChoices.ItemsSource=Choices("existing",snapshot.FoundingExistingCandidates??[]);
        FoundingRecruitChoices.ItemsSource=Choices("recruit",snapshot.FoundingRecruitCandidates??[]);
    }

    private void UseFacilitySite_Click(object sender,RoutedEventArgs e)
    {
        if(snapshot.ColonyId is null && sender is System.Windows.Controls.Button {Tag:string id} &&
            (snapshot.AdoptionCandidates ?? []).Any(c=>c.Id==id))FacilitySiteSelectionRequested?.Invoke(id);
    }
    public void SetFoundingSite(ColonySite? site,string context)
    {
        if(snapshot.ColonyId is not null || snapshot.ContextKey!=context)return;
        if(site is null){SetActionFeedback("This facility has no current observed site. Refresh and choose a current facility.");return;}
        try {ColonyStateCodec.Site(site);}
        catch(Exception ex) when(ex is System.IO.InvalidDataException or ArgumentException or OverflowException)
        {SetActionFeedback("This facility's observed site is invalid: "+ex.Message);return;}
        draft.Body=site.Body;draft.Biome=site.Biome;
        draft.Latitude=site.Latitude.ToString("R",CultureInfo.InvariantCulture);
        draft.Longitude=site.Longitude.ToString("R",CultureInfo.InvariantCulture);
        SetActionFeedback("Observed site copied. Check the coordinates and facilities, then request a fresh charter review.",false);
    }
    private ColonyFoundingIntent? BuildFoundingIntent(string kind,IReadOnlyDictionary<string,string> values)
    {
        if(kind is not ("reviewFoundingPlan" or "approveFoundingPlan" or "surveyFoundingPlan"))return null;
        string Value(string key,string fallback)=>values.TryGetValue(key,out var v)&&v.Length>0?v:fallback;
        bool Flag(string key)=>bool.TryParse(Value(key,"true"),out var v)?v:throw new InvalidOperationException("Choose whether "+key+" is enabled.");
        long Units(string key)
        {
            if(!decimal.TryParse(Value(key,"0"),NumberStyles.Number,CultureInfo.InvariantCulture,out var v)||v<0||v*ColonyLimits.Units!=decimal.Truncate(v*ColonyLimits.Units)||v*ColonyLimits.Units>ColonyLimits.MaxQuantity)throw new InvalidOperationException("Enter a bounded nonnegative resource amount with at most six decimal places.");
            return(long)(v*ColonyLimits.Units);
        }
        double Number(string key,string fallback)=>double.TryParse(Value(key,fallback),NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v)?v:throw new InvalidOperationException("Enter a finite number for "+key+".");
        if(!int.TryParse(Value("FoundingArrivalCount","0"),out var count))throw new InvalidOperationException("New arrivals requires a whole count.");
        var result=new ColonyFoundingIntent {FillPopulationTarget=Flag("FoundingFillTarget"),NewArrivalCount=count,Production=ProductionIntent(values),
            ExistingResidentRosterIds=FoundingSelections("existing").Order(StringComparer.Ordinal).ToList(),RecruitRosterIds=FoundingSelections("recruit").Order(StringComparer.Ordinal).ToList(),PassengerRouteId=Value("FoundingRouteId",""),
            Startup=new ColonyStartupIntent {SuppliesReorderEnabled=Flag("StartupReorder"),SuppliesReorderPointMicroUnits=Units("StartupReorderPoint"),SuppliesTargetMicroUnits=Units("StartupTarget"),ReorderCadenceSeconds=Number("StartupReorderCadence","21600"),
                ServiceEnabled=Flag("StartupService"),ServiceTargetFillFraction=Number("StartupServiceFill","95")/100,ServiceCadenceSeconds=Number("StartupServiceCadence","21600"),
                MachineryReserveMicroUnits=Units("StartupMachinery"),MaterialKitsReserveMicroUnits=Units("StartupMaterialKits"),EnrichedUraniumReserveMicroUnits=Units("StartupUranium"),LocalProcurementEnabled=Flag("StartupLocal")}};
        ColonyStateCodec.ValidateFoundingIntent(result);return result;
    }
}
