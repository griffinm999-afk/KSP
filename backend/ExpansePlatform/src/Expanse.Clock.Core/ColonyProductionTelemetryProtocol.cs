namespace Expanse.Clock.Core;

// Optional v1 addition. Actual means returned physical broker quantities;
// modeled background rates and callback-prepared potential are separate vectors.
public sealed record ColonyProductionTelemetry(string Status,string Reason,double ObservedUt,ColonyProductionModuleTelemetry[] Modules,string InventoryStatus="partial",
    int BudgetOmittedModuleCount=0,long? BudgetSelectionSequence=null);
public sealed record ColonyProductionModuleTelemetry(uint PartId,uint? ModuleId,int ModuleIndex,string ModuleType,string PartName,
    int? BayIndex,int? SelectedLoadout,string Recipe,string RecipeHash,bool? Enabled,bool? Activated,string Basis,
    ColonyProductionVector Configured,ColonyProductionPotential? Prepared,ColonyProductionAchieved? Achieved,
    ColonyProductionBackgroundRate? Background,string? NativeStatus,string Reason,ColonyProductionHopper? Hopper,ColonyProductionHarvester? Harvester=null);
public sealed record ColonyProductionHarvester(string Resource,double Efficiency,double HarvestThreshold,int HarvesterType);
public sealed record ColonyProductionRateTelemetry(string Resource,double UnitsPerSecond,string FlowMode,bool DumpExcess);
public sealed record ColonyProductionRequirementTelemetry(string Resource,double Amount);
public sealed record ColonyProductionVector(ColonyProductionRateTelemetry[] Inputs,ColonyProductionRateTelemetry[] Outputs,ColonyProductionRequirementTelemetry[] Requirements);
public sealed record ColonyProductionPotential(double SampleUt,double EfficiencyMultiplier,double RequirementMultiplier,ColonyProductionVector Rates);
public sealed record ColonyProductionAchieved(double SampleUt,double IntervalGameSeconds,double TimeFactor,ColonyProductionRateTelemetry[] Inputs,ColonyProductionRateTelemetry[] Outputs,long CaptureSequence=0);
public sealed record ColonyProductionBackgroundRate(double SampleUt,string ConstraintState,ColonyProductionRateTelemetry[] Inputs,ColonyProductionRateTelemetry[] Outputs);
public sealed record ColonyProductionHopper(string HopperId,bool? Connected,string Body,string Biome,ColonyProductionWolfPoints[] AllocationPoints);
public sealed record ColonyProductionWolfPoints(string Resource,int Points);

public static class ColonyProductionTelemetryProtocol
{
    public static void Validate(ColonyProductionTelemetry? production,string vesselBasis,double? observedUt)
    {
        if(production is null)return;
        if(production.BudgetOmittedModuleCount is <0 or >32 || production.BudgetOmittedModuleCount+(production.Modules?.Length??0)>32 ||
            production.BudgetOmittedModuleCount>0&&(production.Status!="truncated"||production.InventoryStatus!="partial"||production.BudgetSelectionSequence is null or <0 or >9007199254740991L)||
            production.BudgetOmittedModuleCount==0&&production.BudgetSelectionSequence is not null)Fail();
        if(production.InventoryStatus is not ("complete-supported" or "partial")||production.Status!="partial"&&production.InventoryStatus=="complete-supported")Fail();
        if(production.InventoryStatus=="complete-supported"&&(vesselBasis!="loaded"||production.Modules is null||production.Modules.Any(r=>r is null||r.ModuleType is not ("ModuleResourceConverter" or "USITools.USI_Converter" or "WOLF.WOLF_HopperModule" or "ModuleResourceHarvester" or "USITools.USI_Harvester"))))Fail();
        if(production.Status is not ("partial" or "unavailable" or "truncated")||!Text(production.Reason,360)||!Ut(production.ObservedUt,observedUt)||production.Modules is null||production.Modules.Length>32||production.Status=="unavailable"&&production.Modules.Length!=0) Fail();
        var keys=new HashSet<string>(StringComparer.Ordinal);
        int moduleIndex=-1;
        foreach(var row in production.Modules)
        {
            moduleIndex++;
            try
            {
            ValidateIdentity(row,keys);
            Vector(row.Configured);
            if(row.Achieved is {} capture&&capture.CaptureSequence<=0)Fail();
            if(row.Harvester is {} harvester){if(!Text(harvester.Resource,80,true)||!Number(harvester.Efficiency,1e12)||!Number(harvester.HarvestThreshold,1)||harvester.HarvesterType is <0 or >3||row.Configured.Outputs.Length!=0)Fail();}
            if(row.Prepared is {} potential){if(row.Basis!="loaded-broker"||vesselBasis!="loaded"||!Ut(potential.SampleUt,production.ObservedUt)||!Number(potential.EfficiencyMultiplier,10000)||!Number(potential.RequirementMultiplier,1))Fail();Vector(potential.Rates);}
            if(row.Achieved is {} actual){if(row.Basis!="loaded-broker"||vesselBasis!="loaded"||row.Prepared is null||row.Activated!=true||!Ut(actual.SampleUt,production.ObservedUt)||production.ObservedUt-actual.SampleUt>10||!double.IsFinite(actual.IntervalGameSeconds)||actual.IntervalGameSeconds<=0||actual.IntervalGameSeconds>21600||!Number(actual.TimeFactor,1e12)||actual.SampleUt!=row.Prepared.SampleUt||actual.TimeFactor>actual.IntervalGameSeconds*row.Prepared.EfficiencyMultiplier*row.Prepared.RequirementMultiplier+1e-6)Fail();Rates(actual.Inputs);Rates(actual.Outputs);WithinPotential(actual.Inputs,row.Prepared.Rates.Inputs);WithinPotential(actual.Outputs,row.Prepared.Rates.Outputs);}
            if(row.Background is {} background){if(row.Basis!="background-model"||vesselBasis!="snapshot"||row.Achieved is not null||row.Prepared is not null||!Ut(background.SampleUt,production.ObservedUt)||production.ObservedUt-background.SampleUt>10||!Text(background.ConstraintState,128,true))Fail();Rates(background.Inputs);Rates(background.Outputs);}
            if(row.Hopper is {} hopper){if(!Text(hopper.HopperId,80)||!Text(hopper.Body,100)||!Text(hopper.Biome,100)||hopper.AllocationPoints is null||hopper.AllocationPoints.Length>16)Fail();var resources=new HashSet<string>(StringComparer.Ordinal);foreach(var point in hopper.AllocationPoints)if(point is null||!Text(point.Resource,80,true)||!resources.Add(point.Resource)||point.Points<0)Fail();}
            }
            catch(InvalidDataException ex){ex.Data["ProductionModuleIndex"]=moduleIndex;throw;}
        }
    }
    static void Vector(ColonyProductionVector? vector){if(vector is null)Fail();Rates(vector!.Inputs);Rates(vector.Outputs);if(vector.Requirements is null||vector.Requirements.Length>16)Fail();foreach(var requirement in vector.Requirements)if(requirement is null||!Text(requirement.Resource,80,true)||!double.IsFinite(requirement.Amount)||Math.Abs(requirement.Amount)>1e12)Fail();}
    static void Rates(ColonyProductionRateTelemetry[]? rates){if(rates is null||rates.Length>16)Fail();foreach(var rate in rates!)if(rate is null||!Text(rate.Resource,80,true)||!Text(rate.FlowMode,80)||!Number(rate.UnitsPerSecond,1e12))Fail();}
    static void WithinPotential(ColonyProductionRateTelemetry[] actual,ColonyProductionRateTelemetry[] potential)
    {foreach(var group in actual.GroupBy(r=>r.Resource,StringComparer.Ordinal)){var expected=potential.Where(r=>r.Resource==group.Key).ToArray();double bound=expected.Sum(r=>r.UnitsPerSecond);if(expected.Length==0||group.Sum(r=>r.UnitsPerSecond)>bound+Math.Max(1e-9,bound*1e-6))Fail();}}
    static bool Text(string? text,int limit,bool required=false)=>text is null?!required:text.Length<=limit&&(!required||!string.IsNullOrWhiteSpace(text));
    static bool Number(double number,double limit)=>double.IsFinite(number)&&number>=0&&number<=limit;
    static bool Ut(double ut,double? now)=>Number(ut,1e15)&&(!now.HasValue||ut<=now+1);
    static void ValidateIdentity(ColonyProductionModuleTelemetry? row,HashSet<string> keys)
    {
        var failures=new List<string>();
        if(row is null){failures.Add("module constraint=non-null actualType=null");}
        else
        {
            if(row.PartId==0)failures.Add("partId constraint=positive actualValid=false");
            if(row.ModuleIndex is <0 or >159)failures.Add("moduleIndex constraint=0..159 actualValid=false");
            if(row.BayIndex is <0 or >63)failures.Add("bayIndex constraint=null-or-0..63 actualValid=false");
            if(row.SelectedLoadout is <0 or >63)failures.Add("selectedLoadout constraint=null-or-0..63 actualValid=false");
            if(!keys.Add(row.PartId+":"+row.ModuleIndex))failures.Add("identity constraint=unique-part-and-module actualValid=false");
            void CheckText(string? value,string field,int max,bool required=false)
            {
                if(!Text(value,max,required))failures.Add(field+" constraint="+(required?"required-and-":"")+"maxLength"+max+" actualType="+(value is null?"null":"string")+" actualLength="+(value?.Length??-1));
            }
            CheckText(row.ModuleType,"moduleType",128,true);CheckText(row.PartName,"partName",100,true);
            CheckText(row.Recipe,"recipe",100,true);CheckText(row.RecipeHash,"recipeHash",64,true);
            if(row.RecipeHash is not null&&row.RecipeHash.Length!=64)failures.Add("recipeHash constraint=exactLength64 actualType=string actualLength="+row.RecipeHash.Length);
            if(row.Basis is not ("loaded-broker" or "background-model" or "proto-config"))failures.Add("basis constraint=supported-enum actualValid=false");
            CheckText(row.NativeStatus,"nativeStatus",256);CheckText(row.Reason,"reason",360);
        }
        if(failures.Count!=0){var error=new InvalidDataException("Invalid production telemetry.");error.Data["IdentityFailures"]=string.Join("; ",failures);throw error;}
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    static void Fail([System.Runtime.CompilerServices.CallerLineNumber] int check=0)=>throw new InvalidDataException("Invalid production telemetry (check " + check + ").");
}
