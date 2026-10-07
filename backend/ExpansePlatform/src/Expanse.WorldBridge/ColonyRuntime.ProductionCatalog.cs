using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using USITools;
namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        ColonyProductionRecipe cachedProductionRecipe;string productionCatalogKey="",productionCatalogReason="";
        void PopulateProductionEnvironment(ColonyEnvironment env)
        {
            PopulateProductionRegistry(env);
            if(PartLoader.LoadedPartsList==null || GameDatabase.Instance==null){env.Production.Reason="Installed native part/configuration database is not ready.";return;}
            var a=env.Templates.SingleOrDefault(t=>t.Id=="cultivation-duna-v1");var h=env.Templates.SingleOrDefault(t=>t.Id=="cultivation-feeds-v1");
            string key=loadEpoch+"|"+(a?.Hash??"")+"|"+(h?.Hash??"");
            if(key!=productionCatalogKey)
            {
                productionCatalogKey=key;cachedProductionRecipe=null;
                try{cachedProductionRecipe=ReadProductionCatalog(a,h);productionCatalogReason="Installed hash-bound cultivation/feed packages; nominal estimates and actual native output remain distinct.";}
                catch(Exception ex){productionCatalogReason=Bound(ex.Message,512);}
            }
            env.Production.Reason=productionCatalogReason;
            if(cachedProductionRecipe!=null)env.Production.Recipes.Add(cachedProductionRecipe);
            if(state==null)return;
            PopulateProductionFuelTargets(env);
            PopulateProductionObservations(env);
            ColonyStateCodec.ValidateProductionEnvironment(env.Production);
        }
        static ColonyProductionRecipe ReadProductionCatalog(ColonyTemplate agriculture,ColonyTemplate hoppers)
        {
            if(agriculture==null||hoppers==null)throw new InvalidDataException("Install the separately reviewed cultivation-duna-v1 and cultivation-feeds-v1 source packages; original package identities remain unchanged.");
            var info=PartLoader.getPartInfoByName("Duna.Agriculture");var hopperInfo=PartLoader.getPartInfoByName("WOLF.HarvestingHopper375");
            if(info?.partConfig==null||info.partPrefab==null||hopperInfo?.partConfig==null||hopperInfo.partPrefab==null)throw new InvalidDataException("Installed agriculture/harvesting part is unavailable.");
            var options=info.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="USI_ConverterSwapOption").ToArray();
            var feedOptions=hopperInfo.partConfig.GetNodes("MODULE").Where(n=>n.GetValue("name")=="WOLF_HopperSwapOption").ToArray();
            if(options.Length>64||feedOptions.Length>64)throw new InvalidDataException("Native loadout catalog exceeds its bound.");
            int index=Array.FindIndex(options,n=>n.GetValue("ConverterName")=="Cultivate(S)");
            if(index<0||index!=1)throw new InvalidDataException("Installed Cultivate(S) native option changed.");
            var node=options[index];var recipe=new ColonyProductionRecipe {Id="cultivate-substrate-v1",Name="Substrate cultivation · Supplies with real raw-feed hoppers",
                TemplateId=agriculture.Id,TemplateHash=agriculture.Hash,HopperTemplateId=hoppers.Id,HopperTemplateHash=hoppers.Hash,
                CraftPartId=100,PartName=info.name,OptionIndex=index,OptionHash=ProductionOptionHash(node),NativeExperienceEffect=node.GetValue("ExperienceEffect"),
                Inputs=ProductionConfigRates(node,"INPUT_RESOURCE"),Outputs=ProductionConfigRates(node,"OUTPUT_RESOURCE"),
                RequiredInputs=node.GetNodes("REQUIRED_RESOURCE").Select(n=>new MaterialRequirement {Resource=Required(n,"ResourceName"),Amount=checked((long)decimal.Ceiling((decimal)Value(n,"Ratio")*ColonyLimits.Units))}).ToList()};
            foreach(var pair in new[]{Tuple.Create(115u,"Substrate"),Tuple.Create(120u,"Water")})
            {
                int option=Array.FindIndex(feedOptions,n=>ProductionPointInputs(n).Count==1&&ProductionPointInputs(n)[0].Resource==pair.Item2);
                if(option<0)throw new InvalidDataException("Installed raw-feed hopper option is unavailable for "+pair.Item2+".");
                var feed=feedOptions[option];var points=ProductionPointInputs(feed);var output=ProductionConfigRates(feed,"OUTPUT_RESOURCE").SingleOrDefault(r=>r.Resource==pair.Item2);
                if(output==null||points[0].Points!=5)throw new InvalidDataException("Native hopper physical output/point demand changed.");
                recipe.Feeds.Add(new ColonyProductionFeed {CraftPartId=pair.Item1,PartName=hopperInfo.name,Resource=pair.Item2,OptionIndex=option,
                    OptionHash=ProductionOptionHash(feed),WolfPoints=points[0].Points,NominalUnitsPerSecond=output.UnitsPerSecond});
            }
            var ac=ProductionSourceCraft(agriculture);var hc=ProductionSourceCraft(hoppers);
            recipe.Preconfigured=ProductionCraftLoadout(ac,100,"USI_SwappableBay")==recipe.OptionIndex&&recipe.Feeds.All(f=>ProductionCraftLoadout(hc,f.CraftPartId,"WOLF_HopperBay")==f.OptionIndex);
            if(!recipe.Preconfigured)throw new InvalidDataException("Fresh production package does not serialize the reviewed exact native loadouts.");
            if(!agriculture.StartupContents.Any(c=>c.CraftPartId==104&&c.ResourceName=="Plutonium-238"&&c.Amount==20*ColonyLimits.Units))throw new InvalidDataException("Cultivation package must bill its full installed 20-unit fuel requirement; a nominal generator rate cannot replace fuel.");
            recipe.ConfigurationHash=ColonyStateCodec.ProductionRecipeHash(recipe);ColonyStateCodec.ValidateProductionRecipe(recipe);return recipe;
        }
        static string ProductionOptionHash(ConfigNode node)=>ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(node.ToString()));
        static List<ColonyProductionRate> ProductionConfigRates(ConfigNode node,string kind)
        {
            var rows=node.GetNodes(kind);if(rows.Length>16)throw new InvalidDataException("Native recipe resource vector exceeds 16.");
            return rows.Select(n=>new ColonyProductionRate {Resource=Required(n,"ResourceName"),UnitsPerSecond=Value(n,"Ratio")}).OrderBy(r=>r.Resource,StringComparer.Ordinal).ToList();
        }
        static List<ColonyWolfIngredient> ProductionPointInputs(ConfigNode node)
        {
            var values=(node.GetValue("InputResources")??"").Split(',');if(values.Length==0||values.Length>32||values.Length%2!=0)throw new InvalidDataException("Native hopper abstract input vector is invalid.");
            var rows=new List<ColonyWolfIngredient>();for(int i=0;i<values.Length;i+=2)rows.Add(new ColonyWolfIngredient{Resource=values[i].Trim(),Points=int.Parse(values[i+1],CultureInfo.InvariantCulture)});
            ColonyStateCodec.WolfIngredients(rows);return rows;
        }
        static ConfigNode ProductionSourceCraft(ColonyTemplate template)
        {
            string root=Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath,"GameData","ExpanseWorldBridge","Templates"));
            string path=Path.GetFullPath(Path.Combine(root,template.CraftRelativePath));
            if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)||new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Bounded reviewed production craft is unavailable.");
            byte[] bytes=File.ReadAllBytes(path);if(ColonyPlacementRequest.Hash(bytes)!=template.CraftSha256)throw new InvalidDataException("Production source craft changed after catalog review.");
            return ConfigNode.Load(path)??throw new InvalidDataException("Native craft node could not be read.");
        }
        static int ProductionCraftLoadout(ConfigNode craft,uint id,string module)
        {
            var parts=craft.GetNodes("PART").Where(p=>(p.GetValue("part")??"").EndsWith("_"+id.ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal)).ToArray();
            if(parts.Length!=1)throw new InvalidDataException("Production craft mapping is absent or duplicated.");
            var bay=parts[0].GetNodes("MODULE").SingleOrDefault(n=>n.GetValue("name")==module);if(bay==null)throw new InvalidDataException("Production native bay is absent.");
            return int.Parse(bay.GetValue("currentLoadout"),CultureInfo.InvariantCulture);
        }
    }
}
