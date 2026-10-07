using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;
public sealed class GrowthDisplayPrecision52Tests
{
 [Fact] public void ProjectionFloorsConservativelyAndKeepsExactFreshQuote()
 {
  var(s,e,id)=ColonyPlanningTests.GrowthFixture();var stock=s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies");stock.Amount-=12345;
  s=ColonyEngine.RunPlanningPolicies(s,e);var proposal=s.Colonies[0].Proposals.Single();var q=ColonyEngine.QuoteGrowthProposal(s,id,proposal.Id,e);
  Assert.Equal(Math.Floor(q.Forecast.DownsideSupplyDays*100)/100,proposal.DownsideCashDays);
  Assert.True(proposal.DownsideCashDays<=q.Forecast.DownsideSupplyDays);Assert.InRange(q.Forecast.DownsideSupplyDays-proposal.DownsideCashDays,0,.01);
  Assert.NotEqual(proposal.DownsideCashDays,q.Forecast.DownsideSupplyDays);
  string exact=q.Id;double days=q.Forecast.DownsideSupplyDays;proposal.DownsideCashDays=1;
  var fresh=ColonyEngine.QuoteGrowthProposal(s,id,proposal.Id,e);Assert.Equal(exact,fresh.Id);Assert.Equal(days,fresh.Forecast.DownsideSupplyDays);
 }
 [Fact] public void AdvancingUtInsideDisplayedBucketReturnsPriorAndCrossingUpdatesIt()
 {
  var(s,e,id)=ColonyPlanningTests.GrowthFixture();var stock=s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies");stock.Amount-=12345;
  s=ColonyEngine.RunPlanningPolicies(s,e);double display=s.Colonies[0].Proposals.Single().DownsideCashDays;
  e.Ut+=.5;s=ColonyEngine.Advance(s,e);var same=ColonyEngine.RunPlanningPolicies(s,e);Assert.Same(s,same);Assert.Equal(display,same.Colonies[0].Proposals.Single().DownsideCashDays);
  var q=ColonyEngine.QuoteGrowthPlan(s,id,e);var daily=(decimal)q.Forecast.SupportedPeople*s.Colonies[0].SupportMicroUnitsPerPersonDay;
  var supplies=s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies");supplies.Amount=supplies.Reserved+checked((long)(daily*(decimal)(display-.0001)));
  var crossed=ColonyEngine.RunPlanningPolicies(s,e);Assert.NotSame(s,crossed);Assert.Equal(display-.01,crossed.Colonies[0].Proposals.Single().DownsideCashDays,8);Assert.Equal(s.Revision+1,crossed.Revision);
 }
 [Fact] public void StoredProjectionCannotApproveStaleOrBlockedResourceTerms()
 {
  var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);var p=s.Colonies[0].Proposals.Single();var quote=ColonyEngine.QuoteGrowthProposal(s,id,p.Id,e);
  p.DownsideCashDays=999999;s.Colonies[0].Stock.Single(x=>x.Resource=="Supplies").Amount=ColonyLimits.Units;
  var fresh=ColonyEngine.QuoteGrowthProposal(s,id,p.Id,e);Assert.False(fresh.CanApprove);
  var command=new ColonyCommand{OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,TargetId=p.Id,Kind="approveGrowthProposal",QuoteId=quote.Id};
  Assert.Equal("rejected",ColonyEngine.Execute(s,command,e).Outcome);Assert.Empty(s.Plans);
  s.Colonies[0].Charter.GrowthPolicy="automatic";s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Empty(s.Plans);
 }
}
