using System;
using System.Linq;
using Expanse.Domain.Colonies;
namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static void AddGrowthProposalCapabilities(ColonyManagementSnapshot snapshot,ColonyEnvironment env,ColonyRecord colony)
        {
            foreach(var p in colony.Proposals)
            {
                bool open=p.State=="proposed",decidable=open||p.State=="deferred";
                if(open)snapshot.Capabilities.Add(new ColonyManagementCapability {Kind="approveGrowthProposal",ColonyId=colony.Id,TargetId=p.Id,Label="Approve reviewed expansion proposal",Available=true,Reason="Review this exact proposal's complete current funded bill; no package, person or paid work is inferred from its summary."});
                if(decidable)
                {
                    snapshot.Capabilities.Add(new ColonyManagementCapability {Kind="deferGrowthProposal",ColonyId=colony.Id,TargetId=p.Id,Label="Defer this expansion review",Available=true,Reason="Suppresses automatic expansion and all replacement reviews until the reviewed game-time deadline. Explicit reconsideration may reopen it sooner."});
                    snapshot.Capabilities.Add(new ColonyManagementCapability {Kind="rejectGrowthProposal",ColonyId=colony.Id,TargetId=p.Id,Label="Reject this expansion review",Available=true,Reason="Permanently closes this exact review. A later planning cadence may create a separate independent review; no paid plan is cancelled or refunded."});
                }
                if(p.State=="deferred"||p.State=="rejected")snapshot.Capabilities.Add(new ColonyManagementCapability {Kind="reconsiderGrowthProposal",ColonyId=colony.Id,TargetId=p.Id,Label="Reconsider expansion review",Available=true,Reason=p.State=="deferred" ? "Explicitly removes this review's deferral. Automatic charter authority may approve a fresh qualified bill after reconsideration." : "Keeps the rejected review closed and creates a separate fresh review. Automatic charter authority may approve that new qualified bill."});
            }
        }
    }
}
