using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;

// Pure authority/decision tests. Fixture terrain and seats do not certify KSP.
public sealed class ColonyGrowthProposalTests
{
    [Fact] public void FreshExactProposalApprovalLinksOneDeterministicPlanAndReplayCannotSpendAgain()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);string proposal=s.Colonies[0].Proposals.Single().Id;
        var q=ColonyEngine.QuoteGrowthProposal(s,id,proposal,e);var cmd=Command(s,e,id,proposal,"approveGrowthProposal");cmd.QuoteId=q.Id;
        e.Templates.Single(t=>t.Id=="housing").BuildFunds++;Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);Assert.Empty(s.Plans);e.Templates.Single(t=>t.Id=="housing").BuildFunds--;
        s=Accept(s,e,cmd);var p=s.Colonies[0].Proposals.Single();Assert.Equal("approved",p.State);Assert.Equal(ColonyEngine.PlanningChildId(proposal,"approve"),p.PlanId);Assert.Equal(p.PlanId,s.Plans.Single().Id);
        var replay=ColonyEngine.Execute(ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s)),cmd,e);Assert.Equal("duplicate",replay.Outcome);Assert.Single(replay.State.Plans);
        cmd.OperationId=Guid.NewGuid().ToString("D");cmd.ExpectedRevision=s.Revision;Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);Assert.Single(s.Plans);
    }
    [Fact] public void DeferralSuppressesOtherCadencesAndReloadUntilExactDeadline()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);string proposal=s.Colonies[0].Proposals.Single().Id;
        s=Accept(s,e,Command(s,e,id,proposal,"deferGrowthProposal",new(){["DecisionReason"]="Wait for this funded mission to finish",["DelaySeconds"]="43200"}));
        double deadline=s.Colonies[0].Proposals.Single().DeferredUntilUt;s.Colonies[0].Charter.GrowthPolicy="automatic";
        s=ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(s));e.Ut+=21600;s=ColonyEngine.Advance(s,e);s=ColonyEngine.RunPlanningPolicies(s,e);
        Assert.Empty(s.Plans);Assert.Single(s.Colonies[0].Proposals);Assert.Equal("deferred",s.Colonies[0].Proposals[0].State);Assert.Equal(deadline,s.Colonies[0].Proposals[0].DeferredUntilUt);
        var direct=Command(s,e,id,proposal,"approveGrowthPlan");direct.QuoteId=ColonyEngine.QuoteGrowthPlan(s,id,e).Id;Assert.Equal("rejected",ColonyEngine.Execute(s,direct,e).Outcome);
        e.Ut=deadline;s=ColonyEngine.Advance(s,e);s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Plans);Assert.Equal("approved",s.Colonies[0].Proposals.Single().State);Assert.Equal(ColonyEngine.PlanningChildId(proposal,"approve"),s.Plans[0].Id);
    }
    [Fact] public void RejectionNeverReopensOrSpendsItsItemButLaterCadenceIsASeparateReview()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);string rejected=s.Colonies[0].Proposals.Single().Id;
        s=Accept(s,e,Command(s,e,id,rejected,"rejectGrowthProposal",new(){["DecisionReason"]="Do not expand this reviewed scope"}));s.Colonies[0].Charter.GrowthPolicy="automatic";
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Empty(s.Plans);Assert.Single(s.Colonies[0].Proposals);Assert.Equal("rejected",s.Colonies[0].Proposals[0].State);
        e.Ut+=21600;s=ColonyEngine.Advance(s,e);s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Plans);Assert.Equal("rejected",s.Colonies[0].Proposals.Single(p=>p.Id==rejected).State);
        Assert.NotEqual(ColonyEngine.PlanningChildId(rejected,"approve"),s.Plans[0].Id);Assert.Equal(2,s.Colonies[0].Proposals.Count);
    }
    [Fact] public void ExplicitReconsiderKeepsRejectedHistoryAndDoesNotOverrideApprovalCharter()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);string old=s.Colonies[0].Proposals[0].Id;
        s=Accept(s,e,Command(s,e,id,old,"rejectGrowthProposal",new(){["DecisionReason"]="Prefer a new independent review"}));
        var reconsider=Command(s,e,id,old,"reconsiderGrowthProposal",new(){["DecisionReason"]="Recheck current terms now"});s=Accept(s,e,reconsider);s=ColonyEngine.RunPlanningPolicies(s,e);
        Assert.Empty(s.Plans);Assert.Equal("rejected",s.Colonies[0].Proposals.Single(p=>p.Id==old).State);Assert.Equal("proposed",s.Colonies[0].Proposals.Single(p=>p.Id==reconsider.OperationId).State);
        var replay=ColonyEngine.Execute(s,reconsider,e);Assert.Equal("duplicate",replay.Outcome);Assert.Equal(2,replay.State.Colonies[0].Proposals.Count);
    }
    [Theory] [InlineData("automatic")] [InlineData("approval")]
    public void NewlyRegisteredRealPlotReevaluatesSameCadenceWithoutDuplicateOrImplicitHumanApproval(string policy)
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s.Colonies[0].Charter.GrowthPolicy=policy;var plot=s.Colonies[0].Plots.Single(p=>p.TemplateId=="housing");s.Colonies[0].Plots.Remove(plot);
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Empty(s.Plans);string proposal=s.Colonies[0].Proposals.Single().Id;Assert.Contains("surveyed",s.Colonies[0].Proposals[0].Reason);
        e.Planning.SurveyedPlots.Add(plot);s=Accept(s,e,Command(s,e,id,"","surveyGrowthPlan"));e.Planning.SurveyedPlots.Clear();s=ColonyEngine.RunPlanningPolicies(s,e);
        Assert.Single(s.Colonies[0].Proposals);Assert.Equal(proposal,s.Colonies[0].Proposals[0].Id);
        if(policy=="automatic"){Assert.Single(s.Plans);Assert.Equal(ColonyEngine.PlanningChildId(proposal,"approve"),s.Plans[0].Id);s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Plans);}
        else {Assert.Empty(s.Plans);Assert.Equal("proposed",s.Colonies[0].Proposals[0].State);}
    }
    [Fact] public void BoundedDecisionFieldsAndExactPlanLineageRejectTampering()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s=ColonyEngine.RunPlanningPolicies(s,e);string p=s.Colonies[0].Proposals[0].Id;
        var cmd=Command(s,e,id,p,"deferGrowthProposal",new(){["DecisionReason"]="wait",["DelaySeconds"]="59"});Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);
        cmd.Fields["DelaySeconds"]="648001";Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);cmd.Fields["DelaySeconds"]="60";cmd.Fields["DecisionReason"]=new string('r',513);Assert.Equal("rejected",ColonyEngine.Execute(s,cmd,e).Outcome);
        cmd.Fields["DecisionReason"]=new string('r',512);s=Accept(s,e,cmd);Assert.Equal(512,s.Colonies[0].Proposals[0].DecisionReason.Length);
        s=Accept(s,e,Command(s,e,id,p,"reconsiderGrowthProposal",new(){["DecisionReason"]="Reviewed now"}));var approve=Command(s,e,id,p,"approveGrowthProposal");approve.QuoteId=ColonyEngine.QuoteGrowthProposal(s,id,p,e).Id;s=Accept(s,e,approve);
        var changed=ColonyStateCodec.Copy(s);changed.Colonies[0].Proposals[0].PlanId=Guid.NewGuid().ToString("D");Assert.Throws<InvalidDataException>(()=>ColonyStateCodec.Serialize(changed));
    }
    [Fact] public void PriorAutoSchemaReconcilesExistingExactPlanWithoutReservingAnother()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s.Colonies[0].Charter.GrowthPolicy="automatic";s=ColonyEngine.RunPlanningPolicies(s,e);var original=s.Plans.Single().Id;
        var p=s.Colonies[0].Proposals.Single();p.State="proposed";p.PlanId="";p.DecisionReason="";
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Plans);Assert.Equal(original,s.Plans[0].Id);Assert.Equal("approved",s.Colonies[0].Proposals[0].State);Assert.Equal(original,s.Colonies[0].Proposals[0].PlanId);
    }
    [Fact] public void UnchangedBlockedFirstColonyDoesNotStarveAnotherColonyReview()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();s.Colonies[0].Facilities.Single(f=>f.RequiredWorkers>0).RequiredWorkers=0;
        var(other,oe,otherId)=ColonyPlanningTests.GrowthFixture();var colony=other.Colonies[0];
        foreach(var resident in colony.Residents){resident.RosterId="Other "+resident.RosterId;resident.Name=resident.RosterId;}
        foreach(var person in oe.People.Roster){person.RosterId="Other "+person.RosterId;person.Name=person.RosterId;}
        foreach(var seat in oe.People.Seats)seat.Occupants=seat.Occupants.Select(name=>"Other "+name).ToList();
        s.Colonies.Add(colony);e.AvailableFunds-=100;e.People.PresentByColony[otherId]=oe.People.PresentByColony[otherId].Select(name=>"Other "+name).ToList();e.People.Seats.AddRange(oe.People.Seats);e.People.Roster.AddRange(oe.People.Roster);e.Planning.Assets.AddRange(oe.Planning.Assets);
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Colonies[0].Proposals);Assert.Empty(s.Colonies[1].Proposals);
        s=ColonyEngine.RunPlanningPolicies(s,e);Assert.Single(s.Colonies[0].Proposals);Assert.Single(s.Colonies[1].Proposals);Assert.Empty(s.Plans);
    }
    static ColonyCommand Command(ColonyState s,ColonyEnvironment e,string id,string target,string kind,Dictionary<string,string>? fields=null)=>new(){OperationId=Guid.NewGuid().ToString("D"),ContextKey=e.ContextKey,ExpectedRevision=s.Revision,ColonyId=id,TargetId=target,Kind=kind,Fields=fields??new()};
    static ColonyState Accept(ColonyState s,ColonyEnvironment e,ColonyCommand c){var result=ColonyEngine.Execute(s,c,e);Assert.True(result.Outcome=="accepted",result.Reason);return result.State;}
}
