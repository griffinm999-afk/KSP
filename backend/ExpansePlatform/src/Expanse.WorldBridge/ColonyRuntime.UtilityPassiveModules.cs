using System;
using System.Collections.Generic;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static string UtilityModuleIdentity(PartModule module)
        {
            return ColonyUtilityReflection.Identity(module.GetType());
        }
        // Installed configuration/geometry callbacks do not themselves supply or
        // consume continuous EC. Converter recipes remain accounted separately.
        // Exact type + assembly + version avoids trusting a coincident moduleName.
        static readonly HashSet<string> ReviewedPassiveUtilityTypes=new HashSet<string>(new[]{
            "USITools.ModuleWeightDistributableCargo|USITools|1.0.0.0",
            "USITools.ModuleWeightDistributor|USITools|1.0.0.0",
            "USITools.USI_ModuleRecycleBin|USITools|1.0.0.0",
            "USITools.USI_SwapController|USITools|1.0.0.0",
            "USITools.USI_SwappableBay|USITools|1.0.0.0",
            "USITools.USI_ConverterSwapOption|USITools|1.0.0.0",
            "Firespitter.customization.FSfuelSwitch|Firespitter|7.3.7660.26532",
            "Firespitter.customization.FStextureSwitch2|Firespitter|7.3.7660.26532",
            "HabUtils.ModuleAdjustableLeg|HabUtils|1.0.0.0",
            "HabUtils.ModuleLevelingBase|HabUtils|1.0.0.0",
            "KolonyTools.ModuleResourceSurveyor|KolonyTools|1.0.0.0",
            "ConnectedLivingSpace.ModuleDockingHatch|ConnectedLivingSpace|2.0.2.0",
            "WOLF.Modules.WOLF_HopperBay|USI_WOLF|1.0.0.0",
            "WOLF.WOLF_HopperSwapOption|USI_WOLF|1.0.0.0",
            "ModuleOverheatDisplay|Assembly-CSharp|0.0.0.0"
        },StringComparer.Ordinal);
        static bool IsReviewedPassiveUtilityModule(PartModule module,Vessel vessel)
        {
            string identity=UtilityModuleIdentity(module);
            if(ReviewedPassiveUtilityTypes.Contains(identity))return true;
            // The actual CryoTank is a continuous EC consumer only when cooling
            // is enabled. Saved proto mode, not prefab mode, governs background.
            if(identity!="SimpleBoiloff.ModuleCryoTank|SimpleBoiloff|0.2.1.0")return false;
            if(vessel.loaded)return !UtilityBool(module,"CoolingEnabled");
            var parts=vessel.protoVessel==null ? null : vessel.protoVessel.protoPartSnapshots.Where(p=>p.partInfo!=null && ReferenceEquals(p.partInfo.partPrefab,module.part)).ToArray();
            if(parts==null || parts.Length==0)return false;
            return parts.All(p=>p.modules.Where(m=>m.moduleName=="ModuleCryoTank").Any() && p.modules.Where(m=>m.moduleName=="ModuleCryoTank").All(m=>bool.TryParse(m.moduleValues.GetValue("CoolingEnabled"),out bool cooling) && !cooling));
        }
    }
}
