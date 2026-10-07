using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;

// Kernel receipt tests with explicit trusted adapter fixtures. These do not
// certify actual KSP placement, native activation or physical production.
public sealed class ColonyProductionLifecycleTests
{
    static string Id()=>Guid.NewGuid().ToString("D");
    internal static (ColonyState State,ColonyEnvironment Env,string PlanId,string InvestmentId,List<ColonyProductionStep> Steps) Ready(ColonyProductionOperationsIntent? operations=null,int packages=1)
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture(packages);
        if(operations!=null)ColonyProductionInventoryTests.AddOperations(s,e,intent,operations);
        s=ColonyProductionInvariantTests.Approve(s,e,id,intent);s=ColonyProductionInvariantTests.ReserveWolfChild(s,e);
        var plan=s.Plans.Single();var colony=s.Colonies.Single();
        foreach(var b in plan.Buildings)
        {
            ConstructionOrder order;
            if(b.OrderId.Length==0)
            {
                b.OrderId=ColonyEngine.PlanningChildId(plan.Id,b.Id);plan.RemainingFunds-=b.Funds;
                foreach(var material in b.Materials){var claim=plan.Claims.Single(c=>c.Resource==material.Resource);claim.Remaining-=material.Amount;claim.Reserved-=material.Amount;var stock=colony.Stock.Single(r=>r.Resource==material.Resource);stock.Reserved-=material.Amount;stock.Amount-=material.Amount;}
                order=new(){Id=b.OrderId,ColonyId=id,PlotId=b.PlotId,TemplateId=b.TemplateId,TemplateHash=b.TemplateHash,Funds=b.Funds,WorkRequired=b.LaborSeconds,Materials=b.Materials};s.Construction.Add(order);
            }
            else
            {
                order=s.Construction.Single(o=>o.Id==b.OrderId);
                if(!order.MaterialsConsumed)foreach(var material in order.Materials){var stock=colony.Stock.Single(r=>r.Resource==material.Resource);stock.Reserved-=material.Amount;stock.Amount-=material.Amount;}
            }
            order.FundsPaid=true;order.MaterialsConsumed=true;order.FundsConsumed=order.Funds;order.WorkCompleted=order.WorkRequired;order.State="operational";order.FacilityId=Id();
            foreach(var effect in s.Effects.Where(f=>f.TargetId==order.Id))effect.State="applied";
            var plot=colony.Plots.Single(p=>p.Id==b.PlotId);plot.ReservedBy="";plot.OccupiedBy=order.FacilityId;
            int productionIndex=plan.Quote.ProductionInvestments.FindIndex(i=>i.BuildingId==b.Id||i.HopperBuildingId==b.Id);uint offset=(uint)(Math.Max(0,productionIndex)*20000)+(b.Role=="productionFeed"?10000u:0);
            colony.Facilities.Add(new(){Id=order.FacilityId,VesselId=Id(),Name=b.Name,State="operational",TemplateId=b.TemplateId,TemplateHash=b.TemplateHash,PlotId=b.PlotId,PartIds=(b.Role=="productionFeed"?new uint[]{106,115,119,120}:b.Role=="production"?new uint[]{100,101,104,106,107,108}:new uint[]{100}).Select(id=>id+offset).ToList()});
        }
        var investment=plan.Quote.ProductionInvestments.First();var claimProduction=plan.Production.First();claimProduction.State=operations==null?"ready":"planned";
        var steps=new List<ColonyProductionStep>();
        foreach(var feed in investment.Recipe.Feeds)
        {
            var facility=colony.Facilities.Single(f=>f.Id==s.Construction.Single(o=>o.Id==plan.Buildings.Single(b=>b.Id==investment.HopperBuildingId).OrderId).FacilityId);
            foreach(string action in new[]{"connect","start"})steps.Add(new(){Id=ColonyEngine.PlanningChildId(plan.Id,claimProduction.Id+":"+action+":"+feed.CraftPartId),Kind=action=="connect"?"hopperConnect":"converterStart",FacilityId=facility.Id,VesselId=facility.VesselId,PartId=feed.CraftPartId+10000,ModuleId=feed.CraftPartId+1000,OptionHash=feed.OptionHash});
        }
        var farm=colony.Facilities.Single(f=>f.Id==s.Construction.Single(o=>o.Id==plan.Buildings.Single(b=>b.Id==investment.BuildingId).OrderId).FacilityId);
        steps.Add(new(){Id=ColonyEngine.PlanningChildId(plan.Id,claimProduction.Id+":start:cultivation"),Kind="converterStart",FacilityId=farm.Id,VesselId=farm.VesselId,PartId=100,ModuleId=1100,OptionHash=investment.Recipe.OptionHash});
        foreach(var stock in colony.Stock) Assert.True(stock.Reserved==ColonyEngine.PlanningMaterialReserved(s,id,stock.Resource), stock.Resource+" actual "+stock.Reserved+" plan "+ColonyEngine.PlanningMaterialReserved(s,id,stock.Resource));
        ColonyStateCodec.Validate(s);return(s,e,plan.Id,claimProduction.Id,steps);
    }
    [Fact] public void NativeFiveStepOrderCannotSkipStartOrLoseCreatedHopperReceipt()
    {
        var(s,e,p,c,steps)=Ready();s=ColonyEngine.PrepareProductionSteps(s,p,c,steps);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.MarkProductionApplying(s,p,c,steps[1].Id,"before"));
        s=ColonyEngine.MarkProductionApplying(s,p,c,steps[0].Id,"exact before");
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.CompleteProductionStep(s,p,c,steps[0].Id,"actual after"));
        s=ColonyEngine.CompleteProductionStep(s,p,c,steps[0].Id,"actual after","actual native HopperId");
        s=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));Assert.Equal("actual native HopperId",s.Plans.Single().Production.Single().Steps[0].HopperId);
        Assert.Equal("configuring",s.Plans.Single().Production.Single().State);Assert.Equal(100*ColonyLimits.Units,s.Colonies.Single().Stock.Single(r=>r.Resource=="Supplies").Amount);
    }
    [Fact] public void UnknownNativeEffectReloadHoldsExactChildAndCannotRepeat()
    {
        var(s,e,p,c,steps)=Ready();s=ColonyEngine.PrepareProductionSteps(s,p,c,steps);s=ColonyEngine.MarkProductionApplying(s,p,c,steps[0].Id,"actual before");
        s=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));s=ColonyEngine.HoldEffect(s,steps[0].Id,"Reloaded unknown native outcome");
        Assert.Equal("held",s.Plans.Single().Production.Single().State);Assert.Equal("held",s.Plans.Single().Production.Single().Steps[0].State);
        Assert.Throws<InvalidDataException>(()=>ColonyEngine.MarkProductionApplying(s,p,c,steps[0].Id,"retry"));
        Assert.Empty(s.Plans.Single().Production.Single().OutputWitness);
    }
    [Fact] public void SavedStepEffectMismatchAndReplacementHopperReceiptAreRejected()
    {
        var(s,e,p,c,steps)=Ready();s=ColonyEngine.PrepareProductionSteps(s,p,c,steps);s=ColonyEngine.MarkProductionApplying(s,p,c,steps[0].Id,"before");
        var bad=ColonyStateCodec.Copy(s);bad.Effects.Single(f=>f.Id==steps[0].Id).State="applied";Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(bad));
        s=ColonyEngine.CompleteProductionStep(s,p,c,steps[0].Id,"after","retained-id");s.Plans.Single().Production.Single().Steps[0].HopperId="";Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(s));
    }
    [Fact] public void ComparisonShowsCostedAlternativeWithoutReservingIt()
    {
        var(s,e,id,intent)=ColonyProductionInvariantTests.Fixture();intent.Production!.Mode="compare";
        var imports=ColonyEngine.QuoteFoundingPlan(s,id,e,intent);var local=ColonyEngine.QuoteProductionAlternative(s,id,e,intent);
        Assert.Empty(imports.ProductionInvestments);Assert.Single(local.ProductionInvestments);Assert.True(local.TotalFunds>imports.TotalFunds);Assert.Contains(local.TotalFunds.ToString(),imports.MakeOrImport);
        Assert.Empty(s.WolfOrders);Assert.Empty(s.Plans);
    }
    [Fact] public void NullProductionPreservesIndependentPriorIntentCommandBytes()
    {
        var command=new ColonyCommand{Kind="surveyFoundingPlan",ContextKey="prior context",ExpectedRevision=7,ColonyId=Id(),FoundingIntent=new(){FillPopulationTarget=false}};
        var node=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(command.FoundingIntent))!;node.AsObject().Remove("Production");
        string Canonical(JsonNode? n)=>n is JsonObject obj?"{"+string.Join(",",obj.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>System.Text.Json.JsonSerializer.Serialize(p.Key)+":"+Canonical(p.Value)))+"}":n is JsonArray arr?"["+string.Join(",",arr.Select(Canonical))+"]":n?.ToJsonString()??"null";
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
        writer.Write(command.ContextKey);writer.Write(command.ExpectedRevision);writer.Write(command.Kind);writer.Write(command.ColonyId);writer.Write(command.TargetId);writer.Write(command.QuoteId);
        byte[] intent=Encoding.UTF8.GetBytes(Canonical(node));writer.Write(-1);writer.Write("FoundingIntent.v1");writer.Write(intent.Length);writer.Write(intent);writer.Flush();
        Assert.Equal(ColonyStateCodec.Hash(stream.ToArray()),ColonyStateCodec.CommandHash(command));
    }
}
