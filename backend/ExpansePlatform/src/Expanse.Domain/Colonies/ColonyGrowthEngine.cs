using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyEngine
    {
        public static ColonyPlanningQuote QuoteGrowthPlan(ColonyState state, string colonyId, ColonyEnvironment env)
        {
            var q = BuildPlanningQuote(state, colonyId, env, "growth");
            var colony = Colony(state, colonyId);
            if (colony.Charter.GrowthPolicy == "disabled") q.Blockers.Add("Natural expansion is disabled by the charter.");
            if (!colony.SupportCommissionedUt.HasValue || colony.Status != "operational") q.Blockers.Add("Founding support must be commissioned before natural expansion.");
            int recordedHomes=colony.Facilities.Where(f => (f.State=="operational" || f.State=="adopted") && f.Qualification.HousingCertified).Sum(f => f.CertifiedHomes);
            if(q.Forecast.QualifiedHomes<recordedHomes) q.Blockers.Add("Verify or restore existing housing before expansion; current qualified homes do not cover the recorded certified homes.");
            int committedPeople = colony.Residents.Count(r => r.Status != "missing");
            int demand = Math.Max(colony.Charter.PopulationTarget, committedPeople + q.Forecast.OpenQualifiedJobs);
            if (q.Forecast.QualifiedHomes >= colony.Charter.ResidentLimit) q.Blockers.Add("The charter resident limit is already housed.");
            if (demand <= q.Forecast.QualifiedHomes) q.Blockers.Add("Existing qualified homes already cover real demand and job vacancies.");
            if (q.Forecast.OpenQualifiedJobs <= 0 && colony.Charter.PopulationTarget <= q.Forecast.QualifiedHomes) q.Blockers.Add("No actual operational, input-supported job vacancy justifies expansion.");
            if (!q.Forecast.Sustainable) q.Blockers.Add("Zero-export, delayed-import support forecast is not sustainable: " + string.Join(" ", q.Forecast.Shortages));
            if (q.TotalFunds > q.Forecast.DownsideCash) q.Blockers.Add("The habitat and its inputs would spend downside replacement-supply cash.");
            q.CanApprove = q.Blockers.Count == 0;
            q.Id = ColonyStateCodec.PlanningQuoteHash(q);
            return q;
        }

        static string ConfigureReorder(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            var colony = Colony(state, command.ColonyId);
            string resource = Field(command, "Resource"); Stock(colony, resource);
            var policy = new ColonyReorderPolicy { ColonyId = colony.Id, Resource = resource, Enabled = bool.Parse(Field(command,"Enabled")),
                ReorderPoint = Integer(command,"ReorderPointMicroUnits"), TargetAmount = Integer(command,"TargetMicroUnits"),
                CadenceSeconds = Number(command,"CadenceSeconds", ColonyLimits.KerbinDay), NextReviewUt = env.Ut };
            ColonyStateCodec.ValidateReorderPolicy(policy);
            if (policy.TargetAmount > Stock(colony,resource).Capacity) throw new InvalidDataException("Reorder target exceeds owned receiving capacity.");
            if (policy.Enabled && !state.Suppliers.Any(s => s.Resource == resource && (s.DestinationBody.Length == 0 || s.DestinationBody == colony.Site.Body))) throw new InvalidDataException("No finite supplier serves this reorder resource and body.");
            state.ReorderPolicies.RemoveAll(p => p.ColonyId == colony.Id && p.Resource == resource);
            state.ReorderPolicies.Add(policy); return colony.Id;
        }

        static ColonyState RunReordersAndGrowth(ColonyState prior, ColonyEnvironment env)
        {
            var legacy=ReconcileLegacyGrowthProposal(prior);if(legacy!=null)return legacy;
            // Coalesce missed reviews at actual current UT. No backdated purchase
            // or delivery appears merely because the game was unloaded.
            foreach (var original in prior.ReorderPolicies.Where(p => p.Enabled && p.NextReviewUt <= env.Ut).OrderBy(p => p.NextReviewUt).ThenBy(p => p.ColonyId).ThenBy(p => p.Resource))
            {
                var state = ColonyStateCodec.Copy(prior); var p = state.ReorderPolicies.Single(x => x.ColonyId == original.ColonyId && x.Resource == original.Resource);
                p.NextReviewUt = env.Ut + p.CadenceSeconds;
                var colony = Colony(state,p.ColonyId); var stock = Stock(colony,p.Resource);
                long freeAndIncoming = checked(stock.Amount - stock.Reserved + stock.IncomingReserved - state.Plans.Where(x => x.ColonyId == colony.Id && PlanningActive(x))
                    .SelectMany(x => x.Incoming).Where(i => i.Resource == stock.Resource && !i.Credited).Sum(i => i.MaterialAmount)-state.PhysicalTransfers.Where(o=>o.ColonyId==colony.Id&&o.Resource==stock.Resource&&PhysicalPending(o)).Sum(o=>o.PlannedMaterialAmount));
                long floor = stock.SupportFloor;
                long target = Math.Max(p.TargetAmount, floor);
                if (freeAndIncoming >= Math.Max(p.ReorderPoint,floor)) { p.Reason = "Owned and in-transit stock already cover the reorder point and support reserve."; return FinishPlanningTransition(state); }
                long shortage = Math.Min(Math.Max(0,target-freeAndIncoming),stock.Capacity-stock.Amount-stock.IncomingReserved);
                try
                {
                    if (shortage <= 0) throw new InvalidDataException("Receiving capacity is already committed.");
                    if(TryReserveLocalShortage(state,colony,p.Resource,shortage,env,""))
                    {p.Reason="Verified accessible warehouse stock reserved before purchasing a deficit; no quantity credited until exact provider receipt.";return FinishPlanningTransition(state);}
                    var supplier = state.Suppliers.Where(s => s.Resource == p.Resource && (s.DestinationBody.Length == 0 || s.DestinationBody == colony.Site.Body) &&
                        s.Available - s.Reserved - PlanningSupplierReserved(state,s.Id) > 0 && PlanningMaximumBatch(s,stock) > 0)
                        .OrderBy(s => s.FundsPerUnit).ThenBy(s => s.FreightFunds).ThenBy(s => s.Id).FirstOrDefault() ?? throw new InvalidDataException("Finite supplier inventory is exhausted.");
                    long amount = Math.Min(shortage,Math.Min(PlanningMaximumBatch(supplier,stock),supplier.Available-supplier.Reserved-PlanningSupplierReserved(state,supplier.Id)));
                    long funds = checked(ScaledProduct(amount,supplier.FundsPerUnit)+supplier.FreightFunds);
                    var command = new ColonyCommand { Kind = "approveTrade", OperationId = PlanningChildId(colony.Id,"reorder:"+p.Resource+":"+env.Ut.ToString("R",CultureInfo.InvariantCulture)),
                        ColonyId=colony.Id, ContextKey=env.ContextKey, ExpectedRevision=state.Revision, Fields=new Dictionary<string,string> {
                            ["SupplierId"]=supplier.Id,["AmountMicroUnits"]=amount.ToString(CultureInfo.InvariantCulture),["QuotedFunds"]=funds.ToString(CultureInfo.InvariantCulture) } };
                    ReserveImport(state,command,env); p.Reason="One finite freight load reserved at current UT; owned/incoming stock prevents duplicate replenishment.";
                    return FinishPlanningTransition(state);
                }
                catch(Exception ex) when(ex is InvalidDataException || ex is OverflowException)
                { p.Reason=ex.Message.Length>512?ex.Message.Substring(0,512):ex.Message; return FinishPlanningTransition(state); }
            }
            foreach(var colony in prior.Colonies.Where(c=>c.SupportCommissionedUt.HasValue && c.Charter.GrowthPolicy!="disabled"))
            {
                if(prior.Plans.Any(p=>p.ColonyId==colony.Id && PlanningActive(p))) continue;
                double cadence = Math.Max(60, Math.Min(30*ColonyLimits.KerbinDay,env.Planning.CadenceSeconds));
                double boundary = Math.Floor(env.Ut/cadence)*cadence;
                // A deferral suppresses every replacement cadence until its
                // deadline. A rejected review is never reopened implicitly.
                if(colony.Proposals.Any(p=>p.State=="deferred"&&p.DeferredUntilUt>env.Ut))continue;
                var existing=colony.Proposals.Where(p=>p.State=="proposed"||p.State=="deferred").OrderBy(p=>p.CreatedUt).ThenBy(p=>p.Id,StringComparer.Ordinal).FirstOrDefault();
                string proposalId=existing==null ? PlanningChildId(colony.Id,"growth-review:"+boundary.ToString("R",CultureInfo.InvariantCulture)) : existing.Id;
                if(existing==null&&colony.Proposals.Any(p=>p.Id==proposalId)) continue;
                var quote=QuoteGrowthPlan(prior,colony.Id,env);
                // Inspect the current review by reference; detach only when a transition
                // is needed. Validation makes the six additive copy migrations no-ops.
                // Deferred reopening and automatic approval still use the original path.
                if(existing!=null && existing.State=="proposed" &&
                    !(quote.CanApprove && colony.Charter.GrowthPolicy=="automatic"))
                {
                    var projection=new ColonyProposal();RefreshGrowthProposal(projection,quote);
                    if(existing.TemplateId==projection.TemplateId && existing.PlotId==projection.PlotId &&
                        existing.DownsideCashDays==projection.DownsideCashDays && existing.Reason==projection.Reason)
                        continue;
                }
                var state=ColonyStateCodec.Copy(prior); var target=Colony(state,colony.Id);
                var proposal=target.Proposals.SingleOrDefault(p=>p.Id==proposalId);
                if(proposal==null){MakeGrowthProposalRoom(target);proposal=new ColonyProposal {Id=proposalId,CreatedUt=env.Ut};target.Proposals.Add(proposal);}
                if(proposal.State=="deferred"){proposal.State="proposed";proposal.DeferredUntilUt=0;}
                RefreshGrowthProposal(proposal,quote);
                if(quote.CanApprove && colony.Charter.GrowthPolicy=="automatic")
                {
                    var fresh=QuoteGrowthPlan(state,colony.Id,env);
                    ApproveGrowthProposal(state,target,proposal,fresh,env,"Automatic expansion approved within the enabled charter and exact current funded bill.");
                }
                // These fields are the only growth-review projection refreshed above. A
                // known difference proves the detached state changed; retain the full
                // comparison fallback when none differs (including additive migrations).
                bool reviewChanged=existing==null || existing.State!=proposal.State ||
                    existing.DeferredUntilUt!=proposal.DeferredUntilUt || existing.TemplateId!=proposal.TemplateId ||
                    existing.PlotId!=proposal.PlotId || existing.DownsideCashDays!=proposal.DownsideCashDays || existing.Reason!=proposal.Reason;
                var changed=reviewChanged ? FinishPlanningTransition(state) : FinishPlanningIfChanged(prior,state);
                if(!ReferenceEquals(changed,prior))return changed;
            }
            return prior;
        }

        public static ColonyState RunPlanningPolicies(ColonyState prior, ColonyEnvironment env)
        {
            ColonyStateCodec.Validate(prior);ValidateEnvironment(prior,env);
            return RunPlanningPoliciesValidated(prior,env);
        }

        static ColonyState RunPlanningPoliciesValidated(ColonyState prior, ColonyEnvironment env)
        {
            ValidateEnvironment(prior,env);
            if(prior.SimulatedUt!=env.Ut || prior.Effects.Any(e=>e.State=="applying"||e.State=="held"))return prior;
            var immigration=RunAutomaticImmigration(prior,env,out string waitingColony,out string waitingReason);
            if(!ReferenceEquals(immigration,prior))return immigration;
            return RecordImmigrationWaiting(RunReordersAndGrowth(prior,env),env,waitingColony,waitingReason);
        }

        static string RegisterPlanningSurveys(ColonyState state, ColonyCommand command, ColonyEnvironment env)
        {
            if(env.Planning.SurveyFailure.Length>0) throw new InvalidDataException(env.Planning.SurveyFailure);
            if(env.Planning.SurveyedPlots.Count==0 || env.Planning.SurveyedPlots.Count>64) throw new InvalidDataException("No complete bounded loaded-site plot survey was returned.");
            foreach(var plot in env.Planning.SurveyedPlots)
            {
                env.SurveyedPlot=plot;
                RegisterSurveyedPlot(state,new ColonyCommand { OperationId=command.OperationId,ColonyId=command.ColonyId,TargetId=plot.Id,
                    Fields=new Dictionary<string,string> { ["TemplateId"]=plot.TemplateId,["TemplateHash"]=plot.TemplateHash,
                        ["Latitude"]=plot.Latitude.ToString("R",CultureInfo.InvariantCulture),["Longitude"]=plot.Longitude.ToString("R",CultureInfo.InvariantCulture),
                        ["Heading"]=plot.Heading.ToString("R",CultureInfo.InvariantCulture) } },env);
            }
            return command.ColonyId;
        }
    }
}
