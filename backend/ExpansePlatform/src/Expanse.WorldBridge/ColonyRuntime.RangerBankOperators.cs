using System;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using USITools;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        // This exact new paid workplace grants no EC, native bonus, crew, home,
        // refill, replacement or background authority. Actual Engineer ownership
        // remains the existing People reader. Fresh utilities qualify generation.
        static bool TryPaidRangerBankOperatorCabin(ColonyFacility facility,Vessel vessel,uint partId,ColonyTemplate template,out string evidence)
        {
            evidence="";
            try
            {
                var runtime=Current;
                if(runtime==null||runtime.state==null||!runtime.Ready||HighLogic.CurrentGame==null||!ReferenceEquals(runtime.selectedGame,HighLogic.CurrentGame)||
                    facility==null||facility.DevelopmentOnly||vessel==null||!vessel.loaded||vessel.packed||!vessel.LandedOrSplashed||partId==0||facility.State!="commissioning"&&facility.State!="operational")return false;
                if(template==null)template=runtime.templates.SingleOrDefault(t=>t.Id==facility.TemplateId&&t.Hash==facility.TemplateHash);
                if(template==null||template.Id!="power-ranger-bank-v1"||template.Version!=1||template.ExpectedPartCount!=19||template.Homes!=0||template.HomeCraftPartIds==null||template.HomeCraftPartIds.Count!=0||
                    template.Workers!=1||template.WorkerTrait!="Engineer"||facility.RequiredWorkers!=1||facility.RequiredTrait!="Engineer"||
                    facility.TemplateId!=template.Id||facility.TemplateHash!=template.Hash||template.Hash.Length!=64||facility.CraftSha256.Length!=64||
                    !string.Equals(facility.CraftSha256,template.CraftSha256,StringComparison.OrdinalIgnoreCase)||
                    facility.FoundationId.Length==0||facility.PlacementWitnessHash.Length!=64||facility.PlacementRequestFingerprint.Length==0||!facility.PartIds.Contains(partId)||
                    facility.VesselId!=vessel.id.ToString("D")||!RangerBankPaidStartup(template))return false;
                var colony=runtime.state.Colonies.SingleOrDefault(c=>c.Facilities.Any(f=>f.Id==facility.Id&&f.ConstructionOrderId==facility.ConstructionOrderId));
                var order=runtime.state.Construction.SingleOrDefault(o=>o.Id==facility.ConstructionOrderId&&o.ColonyId==colony?.Id&&o.FacilityId==facility.Id);
                if(colony==null||order==null||!order.FundsPaid||!order.MaterialsConsumed||order.State!="commissioning"&&order.State!="operational"||
                    order.Funds!=template.BuildFunds||order.FundsConsumed!=order.Funds||order.LaborFunds!=template.LaborFunds||order.LaborFunds<=0||
                    !Finite(order.WorkRequired)||order.WorkRequired<=0||order.WorkRequired!=template.LaborSeconds||order.WorkCompleted!=order.WorkRequired||
                    order.Placement.EscrowWitness!=ColonyEngine.ConstructionEscrowWitness(runtime.state,order.Id)||
                    order.TemplateId!=template.Id||order.TemplateHash!=template.Hash||order.PlotId!=facility.PlotId||order.Placement.Phase!="Anchored"||
                    order.Placement.OperationId!=facility.PlacementOperationId||order.Placement.RequestFingerprint!=facility.PlacementRequestFingerprint||
                    order.Placement.AfterWitness.Length==0||facility.PlacementWitnessHash!=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(order.Placement.AfterWitness))||
                    !RangerBankOrderMaterials(template,order)||vessel.mainBody==null||vessel.mainBody.bodyName!=colony.Site.Body||vessel.parts==null||vessel.parts.Count!=19||
                    vessel.parts.Any(p=>p==null||p.vessel!=vessel||p.persistentId==0)||vessel.parts.Select(p=>p.persistentId).Distinct().Count()!=19||
                    !facility.PartIds.OrderBy(x=>x).SequenceEqual(vessel.parts.Select(p=>p.persistentId).OrderBy(x=>x)))return false;
                var craftIds=new System.Collections.Generic.HashSet<uint>();
                bool cabinFound=false;
                foreach(var part in vessel.parts)
                {
                    var marker=part.Modules.OfType<ColonyPlacementMarker>().SingleOrDefault();
                    if(marker==null||marker.part!=part||!craftIds.Add(marker.craftPartId)||part.partInfo?.name!=RangerBankPartName(marker.craftPartId)||
                        marker.worldId!=runtime.state.WorldId||marker.colonyId!=colony.Id||marker.plotId!=facility.PlotId||marker.operationId!=facility.PlacementOperationId||
                        marker.requestFingerprint!=facility.PlacementRequestFingerprint||!string.Equals(marker.templateSha256,facility.CraftSha256,StringComparison.OrdinalIgnoreCase))return false;
                    if(marker.craftPartId==105)
                    {
                        if(part.persistentId!=partId||part.CrewCapacity!=4)return false;
                        cabinFound=true;
                    }
                    if(marker.craftPartId>=101&&marker.craftPartId<=104)
                    {
                        var converters=part.Modules.OfType<USI_Converter>().ToArray();
                        if(converters.Length!=1||converters[0].part!=part)return false;
                        RequireFixedGeneratorHardware(converters[0]);
                    }
                }
                if(!cabinFound||craftIds.Count!=19||Enumerable.Range(100,19).Any(id=>!craftIds.Contains((uint)id)))return false;
                string foundation;double positionError,angleError;
                if(!ColonyPlacementFoundations.IsHeld(vessel)||!ColonyPlacementFoundations.ReadWitness(vessel,out foundation,out positionError,out angleError)||
                    foundation!=facility.FoundationId||!Finite(positionError)||!Finite(angleError)||positionError<0||angleError<0||positionError>.01||angleError>.01)return false;
                evidence="Modeled paid Engineer workplace in exact loaded Ranger bank cabin "+partId+" (craft105), four exact standalone packs101–104 each paid20Pu,19 actual anchored members. Actual Engineer ownership and fresh utility generation remain separate; no native bonus, refill, replacement, home or unloaded remote grid is inferred.";
                return true;
            }
            catch{return false;}
        }

        static bool RangerBankPaidStartup(ColonyTemplate template)
        {
            if(template.StartupContents==null||template.StartupContents.Count!=11||template.EmbeddedContents==null)return false;
            var pu=template.StartupContents.Where(x=>x!=null&&x.ResourceName=="Plutonium-238").ToArray();
            var ec=template.StartupContents.Where(x=>x!=null&&x.ResourceName=="ElectricCharge").ToArray();
            return pu.Length==4&&Enumerable.Range(101,4).All(id=>pu.Count(x=>x.CraftPartId==id&&x.Amount==20000000)==1)&&
                ec.Length==7&&Enumerable.Range(101,4).All(id=>ec.Count(x=>x.CraftPartId==id&&x.Amount==1000000000)==1)&&
                Enumerable.Range(106,3).All(id=>ec.Count(x=>x.CraftPartId==id&&x.Amount==4000000000)==1)&&
                template.EmbeddedContents.Count==2&&template.EmbeddedContents.Count(x=>x!=null&&x.Resource=="Plutonium-238"&&x.Amount==80000000)==1&&
                template.EmbeddedContents.Count(x=>x!=null&&x.Resource=="ElectricCharge"&&x.Amount==16000000000)==1;
        }

        static bool RangerBankOrderMaterials(ColonyTemplate template,ConstructionOrder order)
        {
            if(template.Materials==null||template.Materials.Any(x=>x==null||x.Amount<=0)||order.Materials==null||order.Materials.Any(x=>x==null||x.Amount<=0)||
                order.Materials.Select(x=>x.Resource).Distinct(StringComparer.Ordinal).Count()!=order.Materials.Count)return false;
            var expected=template.Materials.Concat(template.EmbeddedContents).GroupBy(x=>x.Resource,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.Amount),StringComparer.Ordinal);
            return order.Materials.Count==expected.Count&&order.Materials.All(x=>expected.TryGetValue(x.Resource,out var amount)&&amount==x.Amount);
        }

        static string RangerBankPartName(uint id)
        {
            if(id==100)return "Ranger.AnchorHub";
            if(id>=101&&id<=104)return "Ranger.PowerPack";
            if(id==105)return "crewCabin";
            if(id>=106&&id<=108)return "batteryBankLarge";
            if(id==109)return "structuralPanel2";
            if(id>=110&&id<=117)return id%2==0?"strutOcto":"structuralPanel1";
            return id==118?"adapterSmallMiniTall":null;
        }
    }
}
