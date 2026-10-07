using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        static ColonyProposal GrowthProposal(ColonyState state,string colonyId,string proposalId) =>
            Colony(state,colonyId).Proposals.SingleOrDefault(p=>p.Id==proposalId) ?? throw new InvalidDataException("The exact expansion review is no longer available.");

        public static ColonyPlanningQuote QuoteGrowthProposal(ColonyState state,string colonyId,string proposalId,ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(state);ValidateEnvironment(state,env);
            var proposal=GrowthProposal(state,colonyId,proposalId);
            if(proposal.State!="proposed")throw new InvalidDataException(proposal.State=="deferred" ? "This expansion review is deferred. Reconsider it explicitly or wait until its deadline." : "This expansion review is closed. Reconsider a rejected review to create a separate new review.");
            return QuoteGrowthPlan(state,colonyId,env);
        }

        static string ExecuteGrowthProposal(ColonyState state,ColonyCommand command,ColonyEnvironment env)
        {
            var colony=Colony(state,command.ColonyId);var proposal=GrowthProposal(state,colony.Id,command.TargetId);
            if(command.Kind=="approveGrowthProposal")
            {
                var quote=QuoteGrowthProposal(state,colony.Id,proposal.Id,env);
                if(!quote.CanApprove || command.QuoteId!=quote.Id)throw new InvalidDataException("Expansion terms changed or remain blocked. Review the exact current itemized bill.");
                return ApproveGrowthProposal(state,colony,proposal,quote,env,"User approved the exact current expansion bill.");
            }
            string reason=Field(command,"DecisionReason");ColonyStateCodec.Text(reason,512,true);
            if(command.Kind=="reconsiderGrowthProposal")
            {
                if(proposal.State!="deferred" && proposal.State!="rejected")throw new InvalidDataException("Only deferred or rejected expansion reviews can be reconsidered.");
                if(proposal.State=="rejected")
                {
                    if(colony.Proposals.Any(p=>p.State=="proposed" || p.State=="deferred"))throw new InvalidDataException("Resolve the current expansion review before opening another.");
                    MakeGrowthProposalRoom(colony);
                    var fresh=QuoteGrowthPlan(state,colony.Id,env);
                    var next=new ColonyProposal {Id=command.OperationId,CreatedUt=env.Ut,DecisionReason=reason};
                    RefreshGrowthProposal(next,fresh);colony.Proposals.Add(next);
                    Log(state,env.Ut,colony.Id,command.OperationId,"growthReconsidered","Rejected review retained; a separate new expansion review was opened.");
                    return next.Id;
                }
                proposal.State="proposed";proposal.DeferredUntilUt=0;proposal.DecisionReason=reason;
                RefreshGrowthProposal(proposal,QuoteGrowthPlan(state,colony.Id,env));
                Log(state,env.Ut,colony.Id,command.OperationId,"growthReconsidered","The exact deferred review was reopened explicitly.");return proposal.Id;
            }
            if(proposal.State!="proposed" && proposal.State!="deferred")throw new InvalidDataException("This expansion review is already closed; its paid plan remains a separate obligation.");
            proposal.DecisionReason=reason;
            if(command.Kind=="deferGrowthProposal")
            {
                double delay=Number(command,"DelaySeconds",ColonyLimits.KerbinDay);
                if(double.IsNaN(delay) || double.IsInfinity(delay) || delay<60 || delay>30*ColonyLimits.KerbinDay)throw new InvalidDataException("Deferral must be between 60 game seconds and 30 Kerbin days.");
                proposal.State="deferred";proposal.DeferredUntilUt=env.Ut+delay;ColonyStateCodec.Time(proposal.DeferredUntilUt);
                proposal.Reason="Automatic expansion and replacement reviews are suppressed until UT "+proposal.DeferredUntilUt.ToString("R",CultureInfo.InvariantCulture)+"; explicit reconsideration can reopen this review sooner.";
            }
            else if(command.Kind=="rejectGrowthProposal")
            {
                proposal.State="rejected";proposal.DeferredUntilUt=0;
                proposal.Reason="This review is permanently closed and cannot spend. A later planning cadence may produce a separate independent review, or you may explicitly reconsider it.";
            }
            else throw new InvalidDataException("Unknown expansion review decision.");
            string detail=proposal.State+": "+reason;Log(state,env.Ut,colony.Id,command.OperationId,"growthDecision",detail.Length>512?detail.Substring(0,512):detail);return proposal.Id;
        }

        static string ApproveGrowthProposal(ColonyState state,ColonyRecord colony,ColonyProposal proposal,ColonyPlanningQuote quote,ColonyEnvironment env,string decision)
        {
            if(proposal.State!="proposed")throw new InvalidDataException("Only an open exact expansion review can be approved.");
            string planId=PlanningChildId(proposal.Id,"approve");
            if(state.Plans.Any(p=>p.Id==planId))throw new InvalidDataException("This exact expansion review already has its saved plan.");
            var command=new ColonyCommand {Kind="approveGrowthProposal",OperationId=planId,ColonyId=colony.Id,TargetId=proposal.Id,ContextKey=env.ContextKey,ExpectedRevision=state.Revision,QuoteId=quote.Id};
            ApprovePlanning(state,command,env,quote);RefreshGrowthProposal(proposal,quote);
            proposal.State="approved";proposal.PlanId=planId;proposal.DeferredUntilUt=0;proposal.DecisionReason=decision;
            proposal.Reason="Exact itemized scope approved; actual procurement, construction and commissioning continue in its linked saved plan.";return planId;
        }
        static void RefreshGrowthProposal(ColonyProposal proposal,ColonyPlanningQuote quote)
        {
            var building=quote.Buildings.FirstOrDefault();proposal.TemplateId=building==null?"":building.TemplateId;proposal.PlotId=building==null?"":building.PlotId;
            // Persist the display projection at its two-decimal precision. Exact
            // forecasts and fresh approval quotes retain every resource unit.
            proposal.DownsideCashDays=Math.Floor(quote.Forecast.DownsideSupplyDays*100)/100;
            string reason=quote.CanApprove ? quote.Rationale : string.Join(" ",quote.Blockers);proposal.Reason=reason.Length>512?reason.Substring(0,512):reason;
        }
        static void MakeGrowthProposalRoom(ColonyRecord colony)
        {
            while(colony.Proposals.Count>=32)
            {var terminal=colony.Proposals.FirstOrDefault(p=>p.State=="approved" || p.State=="rejected");if(terminal==null)throw new InvalidDataException("Expansion review history is full; resolve existing reviews before opening another.");colony.Proposals.Remove(terminal);}
        }
        static void GuardDirectGrowthApproval(ColonyState state,ColonyCommand command,ColonyEnvironment env)
        {
            if(command.Kind!="approveGrowthPlan")return;
            if(HasGrowthDecision(state,command.ColonyId,env))
                throw new InvalidDataException("Use the exact expansion review and its approve, defer, reject or reconsider controls; a general approval cannot bypass that decision.");
        }
        public static bool HasGrowthDecision(ColonyState state,string colonyId,ColonyEnvironment env)
        {
            var colony=Colony(state,colonyId);double cadence=Math.Max(60,Math.Min(30*ColonyLimits.KerbinDay,env.Planning.CadenceSeconds));double boundary=Math.Floor(env.Ut/cadence)*cadence;
            return colony.Proposals.Any(p=>p.State=="proposed"||p.State=="deferred"||p.State=="rejected"&&p.CreatedUt>=boundary);
        }
        static ColonyState? ReconcileLegacyGrowthProposal(ColonyState prior)
        {
            var match=prior.Colonies.SelectMany(c=>c.Proposals.Where(p=>p.State=="proposed"&&p.PlanId.Length==0).Select(p=>new{Colony=c,Proposal=p,Plan=prior.Plans.SingleOrDefault(plan=>plan.Id==PlanningChildId(p.Id,"approve")&&plan.ColonyId==c.Id&&plan.Quote.Kind=="growth")})).FirstOrDefault(x=>x.Plan!=null);
            if(match==null)return null;
            var state=ColonyStateCodec.Copy(prior);var p=state.Colonies.Single(c=>c.Id==match.Colony.Id).Proposals.Single(x=>x.Id==match.Proposal.Id);
            p.State="approved";p.PlanId=match.Plan!.Id;p.DeferredUntilUt=0;p.DecisionReason="Existing exact saved growth plan reconciled from the prior review schema.";
            p.Reason="The original deterministic expansion plan already owns this review; no new spending or construction was authorized.";return FinishPlanningTransition(state);
        }
    }
    public static partial class ColonyStateCodec
    {
        static void ValidateGrowthProposals(ColonyState state)
        {
            foreach(var colony in state.Colonies)foreach(var p in colony.Proposals)
            {
                Id(p.Id);Text(p.TemplateId,128);if(p.PlotId.Length>0)Id(p.PlotId);Choice(p.State,"proposed","deferred","rejected","approved");Text(p.Reason,512);Text(p.DecisionReason,512);Time(p.CreatedUt);Time(p.DeferredUntilUt);Time(p.DownsideCashDays);
                if(p.State=="deferred" ? p.DeferredUntilUt<=p.CreatedUt : p.DeferredUntilUt!=0)Fail("Expansion deferral deadline differs from its saved state.");
                if(p.PlanId.Length>0)
                {Id(p.PlanId);if(p.State!="approved"||p.PlanId!=ColonyEngine.PlanningChildId(p.Id,"approve")||!state.Plans.Any(plan=>plan.Id==p.PlanId&&plan.ColonyId==colony.Id&&plan.Quote.Kind=="growth"))Fail("Expansion review lost exact approved-plan lineage.");}
                if(p.State=="approved" && p.PlanId.Length==0 && p.DecisionReason.Length>0)Fail("New approved expansion review lacks its saved plan.");
            }
        }
    }
}
