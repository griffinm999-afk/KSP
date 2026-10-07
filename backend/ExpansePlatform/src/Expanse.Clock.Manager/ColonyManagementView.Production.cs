using System.Globalization;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Manager;
public partial class ColonyManagementView
{
    private static ColonyProductionIntent ProductionIntent(IReadOnlyDictionary<string,string> values)
    {
        string Value(string key,string fallback)=>values.TryGetValue(key,out var value)&&value.Length>0?value:fallback;
        if(!int.TryParse(Value("ProductionCount","1"),NumberStyles.None,CultureInfo.InvariantCulture,out int count)||
            !double.TryParse(Value("ProductionHorizonDays","6"),NumberStyles.Float,CultureInfo.InvariantCulture,out double days))throw new InvalidOperationException("Enter a whole package count and finite comparison horizon.");
        var intent=new ColonyProductionIntent{Mode=Value("ProductionMode","importOnly"),RecipeId=Value("ProductionRecipe",""),PackageCount=count,ReviewHorizonSeconds=days*ColonyLimits.KerbinDay};
        bool Flag(string key)=>bool.TryParse(Value(key,"true"),out bool flag)?flag:throw new InvalidOperationException("Choose an enabled or declined production operation.");
        long Units(string key,string fallback)
        {if(!decimal.TryParse(Value(key,fallback),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out decimal units)||units<0||decimal.Truncate(units*ColonyLimits.Units)!=units*ColonyLimits.Units||units*ColonyLimits.Units>ColonyLimits.MaxQuantity)throw new InvalidOperationException("Enter a nonnegative "+key+" amount with at most six decimal places.");return (long)(units*ColonyLimits.Units);}
        if(intent.Mode!="importOnly")
        {
            bool register=Flag("ProductionRegister");var operations=new ColonyProductionOperationsIntent{RegisterCreatedEndpoints=register,AutomaticIntake=register&&Flag("ProductionIntake"),AutomaticInputRefill=register&&Flag("ProductionRefill")};
            if(register)
            {
                if(!double.TryParse(Value("ProductionOperatingDays","1"),NumberStyles.Float,CultureInfo.InvariantCulture,out double cadence))throw new InvalidOperationException("Enter a finite production purchase review cadence.");
                foreach(var item in new[]{("Fertilizer","Fertilizer","100","100","200",.5),("Machinery","Machinery","100","100","100",.95),("Plutonium-238","Fuel","1","1","2",1d)})
                    operations.Resources.Add(new(){Resource=item.Item1,InitialReserve=Units("Production"+item.Item2+"Initial",item.Item3),ReorderPoint=Units("Production"+item.Item2+"Point",item.Item4),TargetAmount=Units("Production"+item.Item2+"Target",item.Item5),
                        ReorderEnabled=operations.AutomaticInputRefill&&Flag("Production"+item.Item2+"Enabled"),CadenceSeconds=cadence*ColonyLimits.KerbinDay,NativeTargetFraction=item.Item6});
            }
            intent.Operations=operations;
        }
        ColonyStateCodec.ValidateProductionIntent(intent);return intent;
    }
}
