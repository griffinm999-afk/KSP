using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.Domain;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private SettlementJournalState settlementJournal;
        private byte[] settlementBytes;
        private string settlementError;
        private ConfigNode settlementRaw;
        private bool settlementNeedsFlush;
        private bool settlementReady;
        private string settlementUnknownLoss;
        // Survives scene loads. The disk witness survives process restarts.
        private static readonly Dictionary<string,SettlementJournalState> settlementTips = new Dictionary<string,SettlementJournalState>();
        internal sealed class PreparedSettlement
        {
            internal ColonyRuntime Owner;
            internal object Game;
            internal string Epoch;
            internal string World;
            internal string Error;
            internal double Ut;
            internal List<SettlementEvent> Events = new List<SettlementEvent>();
        }
        private void LoadSettlements(ConfigNode node)
        {
            settlementJournal=null;settlementBytes=null;settlementError=null;settlementNeedsFlush=false;settlementReady=false;settlementRaw=null;
            settlementUnknownLoss=null;
            var unknown=node.GetNodes("SETTLEMENT_OBSERVER_UNKNOWN_GAP");
            if(unknown.Length>0)settlementUnknownLoss=unknown.Length==1 ? Bound(unknown[0].GetValue("reason") ?? "Persisted observer coverage loss",512) : "Duplicate observer loss markers; coverage cannot be certified.";
            var nodes=node.GetNodes("SETTLEMENT_JOURNAL");
            if(nodes.Length==0)return;
            settlementRaw=node.CreateCopy();
            try
            {
                if(nodes.Length!=1)throw new InvalidDataException("Duplicate settlement journal node.");
                var payload=nodes[0].GetValue("payload");
                if(payload==null || payload.Length>(SettlementJournal.MaxBytes+2)/3*4)throw new InvalidDataException("Settlement payload exceeds bound.");
                var bytes=ColonyStateCodec.DecodeSaveValue(payload);
                if(SettlementJournal.Digest(bytes)!=nodes[0].GetValue("sha256"))throw new InvalidDataException("Settlement checksum failed.");
                settlementJournal=SettlementJournal.Decode(bytes);settlementBytes=bytes;
            }
            catch(Exception ex){settlementError="Settlement journal unavailable: "+Bound(ex.Message,512);}
        }
        private string SettlementArchivePath(string world)
        {
            Guid id;if(!Guid.TryParse(world,out id))throw new InvalidDataException("Invalid journal world.");
            // Observational journal witness only, outside saves and game authority.
            return Path.Combine(KSPUtil.ApplicationRootPath,"GameData","ExpanseWorldBridge","PluginData","SettlementJournal",id.ToString("D")+".json");
        }
        private SettlementHeads SettlementSourceHeads(AcceptedState recoveryOverride=null,ColonyState colonyOverride=null)
        {
            var recovery=RecoveryCapsuleModule.Instance;
            var r=recoveryOverride ?? (recovery==null || !recovery.HasAcceptedState ? null : AcceptedStateCodec.Deserialize(recovery.GetAcceptedStateBytes()));
            var c=colonyOverride ?? state;
            if(r==null || c==null || r.WorldId!=c.WorldId)throw new InvalidDataException("Waiting for coherent recovery and colony authorities.");
            return new SettlementHeads {RecoverySequence=r.AcceptedSequence,RecoveryHash=AcceptedStateCodec.ComputeHash(r),ColonySequence=c.NextSequence-1,ColonyRevision=c.Revision,ColonyHash=ColonyStateCodec.Hash(ColonyStateCodec.Serialize(c))};
        }
        private void EnsureSettlements()
        {
            if(settlementError!=null)throw new InvalidDataException(settlementError);
            if(!Ready || Current!=this || HighLogic.CurrentGame==null || WorldBridgeAddon.Current==null || WorldBridgeAddon.Current.IsLoadUnresolved)throw new InvalidDataException("Settlement source context is unavailable.");
            if(settlementJournal!=null && settlementJournal.WorldId!=state.WorldId)throw new InvalidDataException("Settlement and colony world mismatch.");
            // Do this only once per loaded runtime, before any new settlement.
            if(settlementReady)return;
            SettlementJournalState tip;
            if(!settlementTips.TryGetValue(state.WorldId,out tip))
            {
                string path=SettlementArchivePath(state.WorldId);
                if(File.Exists(path))
                {
                    var info=new FileInfo(path);if(info.Length>SettlementJournal.MaxBytes)throw new InvalidDataException("Settlement archive exceeds bound.");
                    tip=SettlementJournal.Decode(File.ReadAllBytes(path));settlementTips[state.WorldId]=tip;
                }
            }
            if(settlementJournal==null)
            {
                settlementJournal=SettlementJournal.Create(state.WorldId,Planetarium.GetUniversalTime(),SettlementSourceHeads());
                if(tip!=null){settlementJournal.ParentBranchId=tip.BranchId;settlementJournal.GapReason="Loaded save predates the ledger baseline; old ledger history was not replayed.";}
            }
            else
            {
                settlementJournal=SettlementJournal.ReconcileLoad(settlementJournal,tip);
                var heads=SettlementSourceHeads();var savedHeads=settlementJournal.Heads;
                if(heads.RecoverySequence<savedHeads.RecoverySequence || heads.ColonyRevision<savedHeads.ColonyRevision || heads.ColonySequence<savedHeads.ColonySequence ||
                    heads.RecoverySequence==savedHeads.RecoverySequence && heads.RecoveryHash!=savedHeads.RecoveryHash || heads.ColonyRevision==savedHeads.ColonyRevision && heads.ColonyHash!=savedHeads.ColonyHash)
                    settlementJournal.GapReason="Loaded source authorities differ from the saved settlement heads; coverage cannot be certified.";
            }
            settlementBytes=SettlementJournal.Encode(settlementJournal);settlementTips[state.WorldId]=settlementJournal;settlementNeedsFlush=true;settlementReady=true;
        }
        internal static PreparedSettlement PrepareRecoveryObservation(AcceptedState prior,AcceptedState next)
        {
            var receipts=next.Receipts.Where(r=>r.OperationKind=="recoverySale" && r.Outcome=="accepted" && r.FundsWitness!=null && !prior.Receipts.Any(p=>p.OperationId==r.OperationId && p.Outcome=="accepted")).ToArray();
            if(receipts.Length==0)return null;
            var owner=Current;var prepared=new PreparedSettlement {Owner=owner,World=next.WorldId,Game=HighLogic.CurrentGame,Epoch=owner==null ? null : owner.loadEpoch,Ut=receipts[receipts.Length-1].AppliedUt};
            try
            {
            if(owner==null)throw new InvalidDataException("Settlement observer runtime unavailable.");
            owner.EnsureSettlements();
            foreach(var receipt in receipts)
            {
                // Shipment disappears on successful recovery; match it in the exact prior state.
                var removed=prior.ActiveShipments.Where(s=>!next.ActiveShipments.Any(n=>n.ShipmentId==s.ShipmentId)).ToArray();
                if(removed.Length!=1)throw new InvalidDataException("Recovery settlement lacks one exact source shipment.");
                var shipment=removed[0];string vessel=null,colony=null;
                var registry=DepotRegistryModule.Instance;
                var registration=registry==null ? null : registry.Registrations.SingleOrDefault(r=>r.DepotId==shipment.SourceDepotId);
                if(registration!=null)
                {
                    var vessels=(FlightGlobals.Vessels ?? new List<Vessel>()).Where(v=>v!=null &&
                        (v.parts!=null && v.parts.Any(p=>p!=null && p.persistentId==registration.Anchor) || v.protoVessel!=null && v.protoVessel.protoPartSnapshots.Any(p=>p.persistentId==registration.Anchor))).Take(2).ToArray();
                    if(vessels.Length==1)vessel=vessels[0].id.ToString("D");
                    if(registration.OwnerKind=="colony")colony=registration.OwnerColonyId;
                    else if(vessel!=null)
                    {
                        var owners=owner.state.Colonies.Where(c=>c.Facilities.Any(f=>f.VesselId==vessel)).Take(2).ToArray();if(owners.Length==1)colony=owners[0].Id;
                    }
                }
                var w=receipt.FundsWitness;
                var item=new SettlementEvent {Source="recovery",EventId=receipt.CommandSequence.ToString(CultureInfo.InvariantCulture),OperationId=receipt.OperationId,Kind="recoverySale",SettledUt=receipt.AppliedUt,FundsDelta=w.IntendedDeltaFunds,
                    RouteId=shipment.RouteId,RouteVersion=shipment.RouteVersion,ShipmentId=shipment.ShipmentId,SourceDepotId=shipment.SourceDepotId,VesselId=vessel,ColonyId=colony,
                    EvidenceHash=SettlementJournal.Evidence(receipt.PayloadHash+"|"+w.BeforeFunds.ToString("R",CultureInfo.InvariantCulture)+"|"+w.IntendedDeltaFunds.ToString(CultureInfo.InvariantCulture)+"|"+w.IntendedAfterFunds.ToString("R",CultureInfo.InvariantCulture)+"|"+w.ObservedAfterFunds.ToString("R",CultureInfo.InvariantCulture))};
                prepared.Events.Add(item);
            }
            }
            catch(Exception ex){prepared.Events.Clear();prepared.Error="Recovery capture failed: "+ex.GetType().Name;}
            return prepared;
        }
        private PreparedSettlement PrepareColonySettlement(ColonyState complete,string effectId)
        {
            var prepared=new PreparedSettlement {Owner=this,World=complete.WorldId,Game=HighLogic.CurrentGame,Epoch=loadEpoch,Ut=Planetarium.GetUniversalTime()};
            try
            {
            EnsureSettlements();var effect=complete.Effects.Single(e=>e.Id==effectId);
            if(effect.State!="applied" || effect.Provider!="KSP.Funding" || effect.BeforeWitness.Length==0 || effect.AfterWitness.Length==0)throw new InvalidDataException("Colony settlement lacks verified funds evidence.");
            var entry=complete.Journal.LastOrDefault(j=>j.OperationId==effect.OperationId && j.Kind==effect.Kind && j.FundsDelta==effect.FundsDelta);
            if(entry==null)throw new InvalidDataException("Colony settlement lacks accepted journal witness.");
            var item=new SettlementEvent {Source="colony",EventId=effect.Id,OperationId=effect.OperationId,Kind=effect.Kind,SettledUt=entry.Ut,FundsDelta=effect.FundsDelta,ColonyId=effect.ColonyId,ShipmentId=effect.Kind=="importPurchase" ? effect.TargetId : null,
                EvidenceHash=SettlementJournal.Evidence(effect.Id+"|"+effect.Provider+"|"+effect.BeforeWitness+"|"+effect.AfterWitness)};
            prepared.Events.Add(item);
            }
            catch(Exception ex){prepared.Error="Colony capture failed: "+ex.GetType().Name;}
            return prepared;
        }
        internal static void CommitSettlement(PreparedSettlement prepared)
        {
            if(prepared==null)return;
            var owner=prepared.Owner;
            // Called only after accepted gameplay state and exact funds readback.
            // No observer exception may escape into financial acceptance.
            try
            {
                if(owner==null || Current!=owner || !ReferenceEquals(prepared.Game,HighLogic.CurrentGame) || prepared.Epoch!=owner.loadEpoch)throw new InvalidDataException("Observer context unavailable at accepted settlement.");
                owner.EnsureSettlements();var journal=owner.settlementJournal;var heads=owner.SettlementSourceHeads();
                if(prepared.Error!=null)journal=SettlementJournal.RecordLoss(journal,prepared.Error,prepared.Ut,heads);
                else foreach(var item in prepared.Events)journal=SettlementJournal.Observe(journal,()=>item,heads,prepared.Ut);
                journal.ObservedGameUt=prepared.Ut;
                var bytes=SettlementJournal.Encode(journal);owner.settlementJournal=journal;owner.settlementBytes=bytes;owner.settlementNeedsFlush=true;
                settlementTips[prepared.World]=journal;
            }
            catch(Exception ex)
            {
                string reason=prepared.Error ?? "Observer acceptance failed: "+ex.GetType().Name;
                // If the journal itself is still usable, persist the known lost
                // position even when collecting current source heads failed.
                try
                {
                    if(owner!=null && Current==owner && owner.settlementJournal!=null && owner.settlementJournal.WorldId==prepared.World)
                    {
                        var lost=SettlementJournal.RecordLoss(owner.settlementJournal,reason,prepared.Ut);
                        owner.settlementBytes=SettlementJournal.Encode(lost);owner.settlementJournal=lost;owner.settlementNeedsFlush=true;settlementTips[prepared.World]=lost;
                    }
                }
                catch { /* The independent recovery loss marker preserves uncertainty. */ }
                var recovery=RecoveryCapsuleModule.Instance;
                if(recovery!=null)recovery.RecordSettlementObserverLoss(reason,prepared.Ut);
                if(owner!=null){owner.settlementError=reason;owner.settlementUnknownLoss=reason;}
            }
        }
        private void FlushSettlements()
        {
            if(!settlementNeedsFlush || settlementJournal==null)return;
            try
            {
                var path=SettlementArchivePath(settlementJournal.WorldId);Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp=path+".tmp";File.WriteAllBytes(temp,settlementBytes);
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
                settlementNeedsFlush=false;
            }
            catch(Exception ex){settlementError="Settlement fork witness could not be persisted: "+Bound(ex.Message,512);settlementUnknownLoss=settlementError;if(RecoveryCapsuleModule.Instance!=null)RecoveryCapsuleModule.Instance.RecordSettlementObserverLoss(settlementError,Planetarium.GetUniversalTime());}
        }
        private void SaveSettlements(ConfigNode node)
        {
            if(settlementBytes==null){if(settlementRaw!=null)foreach(var raw in settlementRaw.GetNodes("SETTLEMENT_JOURNAL"))node.AddNode(raw.CreateCopy());SaveUnknownSettlementLoss(node);return;}
            // A saved reload witness includes both authoritative heads and UT.
            try
            {
                var savedState=SettlementJournal.Copy(settlementJournal);savedState.Heads=SettlementSourceHeads();savedState.ObservedGameUt=Planetarium.GetUniversalTime();
                settlementJournal=savedState;settlementBytes=SettlementJournal.Encode(savedState);settlementTips[savedState.WorldId]=savedState;settlementNeedsFlush=true;
            }
            catch(Exception ex){settlementError="Settlement save heads unavailable: "+Bound(ex.Message,512);settlementUnknownLoss=settlementError;if(RecoveryCapsuleModule.Instance!=null)RecoveryCapsuleModule.Instance.RecordSettlementObserverLoss(settlementError,Planetarium.GetUniversalTime());}
            FlushSettlements();SaveUnknownSettlementLoss(node);var saved=node.AddNode("SETTLEMENT_JOURNAL");saved.AddValue("sha256",SettlementJournal.Digest(settlementBytes));saved.AddValue("payload",ColonyStateCodec.EncodeSaveValue(settlementBytes));
        }
        private void SaveUnknownSettlementLoss(ConfigNode node)
        {
            if(settlementUnknownLoss==null)return;
            var loss=node.AddNode("SETTLEMENT_OBSERVER_UNKNOWN_GAP");loss.AddValue("reason",settlementUnknownLoss);
        }
        private SettlementPage CaptureSettlements(SettlementReadRequest request)
        {
            try
            {
                EnsureSettlements();FlushSettlements();
                var page=SettlementJournal.Read(settlementJournal,request,Planetarium.GetUniversalTime());
                // Source heads are sampled with game UT on this same Unity thread.
                page.Heads=SettlementSourceHeads();
                var recovery=RecoveryCapsuleModule.Instance;
                if(settlementUnknownLoss!=null || recovery!=null && recovery.ObserverFailureCount>0)
                {
                    page.Status="gap";page.Complete=false;page.Gaps.Add("persistedObserverLoss");
                    page.CoverageGaps.Add(new SettlementCoverageGap {RangeKnown=false,Reason=settlementUnknownLoss ?? recovery.ObserverFailureReason ?? "Persisted observer coverage loss"});
                }
                if(settlementError!=null){page.Status="gap";page.Complete=false;page.Gaps.Add(settlementError);}return page;
            }
            catch(Exception ex){return new SettlementPage {Status="gap",AfterCursor=request.AfterCursor,Reason=Bound(ex.Message,1024),Gaps=new List<string>{"observerCoverageUnavailable"},CoverageGaps=new List<SettlementCoverageGap>{new SettlementCoverageGap {RangeKnown=false,Reason=Bound(ex.Message,512)}}};}
        }
    }
}
