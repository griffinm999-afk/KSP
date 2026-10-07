using System;
using System.Linq;
using BackgroundResourceProcessing.Converter;
using BrpConverter=BackgroundResourceProcessing.Core.ResourceConverter;

namespace Expanse.BrpColony
{
    // Inherit the installed adapter's OnLoad/configuration and fallback. A
    // single patched registry entry owns either this behavior or the original.
    public sealed class ExpanseColonyProportionalAdapter : BackgroundResourceConverter
    {
        public override ModuleBehaviour GetBehaviour(BaseConverter module)
        {
            if(ReferenceEquals(module,null)||ReferenceEquals(module.part,null))return base.GetBehaviour(module);
            if(module.GetType()!=typeof(USITools.USI_Converter)||
                module.part.partInfo==null||(module.part.partInfo.name!="Ranger.PowerPack"&&module.part.partInfo.name!="Duna.Agriculture"))return base.GetBehaviour(module);
            bool owned=module.part.Modules.Cast<PartModule>().Any(m=>m.GetType().FullName=="Expanse.WorldBridge.ColonyPlacementMarker"&&
                !string.IsNullOrEmpty(Convert.ToString(m.GetType().GetField("worldId")?.GetValue(m)))&&
                !string.IsNullOrEmpty(Convert.ToString(m.GetType().GetField("operationId")?.GetValue(m))));
            if(!owned)return base.GetBehaviour(module);
            if(!module.IsActivated)return null;
            ConfigNode record=null;string reason="";
            try{if(!UsePreparedRecipe.Evaluate(module))throw new InvalidOperationException("Owned proportional capture requires the reviewed prepared-recipe configuration.");ColonyGate.Provenance();record=ColonyGate.Capture(module);ColonyRecipe.Read(record);}
            catch(Exception ex)
            {
                reason=(ex.InnerException??ex).Message;
                UnityEngine.Debug.LogWarning("[ExpanseColonyProportional] capture held flight="+module.part.flightID+" module="+module.GetPersistentId()+" loaded="+(module.vessel!=null&&module.vessel.loaded)+": "+reason);
            }
            return new ModuleBehaviour(new ExpanseColonyProportionalBehaviour(record,reason));
        }
        public override void OnRestore(BaseConverter module,BrpConverter converter)
        {
            // Installed native lastUpdateTime suppression follows inventory
            // restore even for a held behavior; no second catch-up is allowed.
            base.OnRestore(module,converter);
        }
    }
}
