using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public sealed class SettlementHeads
    {
        public long RecoverySequence { get; set; }
        public string RecoveryHash { get; set; } = "";
        public long ColonySequence { get; set; }
        public string ColonyHash { get; set; } = "";
        public long ColonyRevision { get; set; }
    }
    public sealed class SettlementBaseline
    {
        public double GameUt { get; set; }
        public long Funds { get; set; } = 100000;
        public long Science { get; set; }
        public SettlementHeads Heads { get; set; } = new SettlementHeads();
    }
    public sealed class SettlementEvent
    {
        public string Source { get; set; } = "";
        public string EventId { get; set; } = "";
        public long Sequence { get; set; }
        public string Cursor { get; set; } = "";
        public string WorldId { get; set; } = "";
        public string BranchId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string OriginBranchId { get; set; } = "";
        public double SettledUt { get; set; }
        public string Kind { get; set; } = "";
        public long FundsDelta { get; set; }
        public long ScienceDelta { get; set; }
        public string FundsDeltaExact { get; set; } = "0";
        public string ScienceDeltaExact { get; set; } = "0";
        public string? ColonyId { get; set; }
        public string? RouteId { get; set; }
        public long? RouteVersion { get; set; }
        public string? ShipmentId { get; set; }
        public string? VesselId { get; set; }
        public string? SourceDepotId { get; set; }
        public string EvidenceHash { get; set; } = "";
        public string PreviousHash { get; set; } = "";
        public string Hash { get; set; } = "";
    }
    public sealed class SettlementJournalState
    {
        public int Version { get; set; } = 2;
        public string WorldId { get; set; } = "";
        public string BranchId { get; set; } = "";
        public string? ParentBranchId { get; set; }
        public SettlementBaseline Baseline { get; set; } = new SettlementBaseline();
        public SettlementHeads Heads { get; set; } = new SettlementHeads();
        public List<SettlementEvent> Events { get; set; } = new List<SettlementEvent>();
        public string GapReason { get; set; } = "";
        public double ObservedGameUt { get; set; }
        public long HeadSequence { get; set; }
        public string HeadHash { get; set; } = "";
        public long RetainedFromSequence { get; set; }
        public string RetainedFromHash { get; set; } = "";
        public List<SettlementCoverageGap> CoverageGaps { get; set; } = new List<SettlementCoverageGap>();
    }
    public sealed class SettlementCoverageGap
    {
        public long FromSequence { get; set; }
        public long ThroughSequence { get; set; }
        public string BeforeHash { get; set; } = "";
        public string AfterHash { get; set; } = "";
        public string Reason { get; set; } = "";
        public double ThroughUt { get; set; }
        public bool RangeKnown { get; set; } = true;
    }
    public sealed class SettlementReadRequest
    {
        public string? AfterCursor { get; set; }
        public string? ThroughCursor { get; set; }
        public int Limit { get; set; } = 100;
    }
    public sealed class SettlementPage
    {
        public string Status { get; set; } = "unavailable";
        public string Reason { get; set; } = "";
        public string? AfterCursor { get; set; }
        public string? CoveredThrough { get; set; }
        public string? NextCursor { get; set; }
        public string? Head { get; set; }
        public string? ReadThrough { get; set; }
        public string WorldId { get; set; } = "";
        public string BranchId { get; set; } = "";
        public string? ParentBranchId { get; set; }
        public bool Complete { get; set; }
        public bool HasMore { get; set; }
        public List<string> Gaps { get; set; } = new List<string>();
        public double GameUt { get; set; }
        public SettlementBaseline? Baseline { get; set; }
        public SettlementHeads? Heads { get; set; }
        public List<SettlementEvent> Events { get; set; } = new List<SettlementEvent>();
        public string? BaselineCursor { get; set; }
        public string? RetainedFromCursor { get; set; }
        public string? RetainedFromHash { get; set; }
        public List<SettlementCoverageGap> CoverageGaps { get; set; } = new List<SettlementCoverageGap>();
    }
    // Independent from compacted operational receipts. Never reconstruct old history.
    public static class SettlementJournal
    {
        public const int MaxBytes = 8 * 1024 * 1024;
        public const int MaxEvents = 2048;
        public static SettlementJournalState Create(string world, double ut, SettlementHeads heads)
        {
            var state = new SettlementJournalState { WorldId = world, BranchId = Guid.NewGuid().ToString("D"),
                Baseline = new SettlementBaseline { GameUt = ut, Heads = CopyHeads(heads) }, Heads = CopyHeads(heads), ObservedGameUt=ut };
            state.HeadHash=Genesis(state);state.RetainedFromHash=state.HeadHash;Encode(state); return state;
        }
        public static SettlementHeads CopyHeads(SettlementHeads h) => new SettlementHeads { RecoverySequence=h.RecoverySequence, RecoveryHash=h.RecoveryHash, ColonySequence=h.ColonySequence, ColonyHash=h.ColonyHash, ColonyRevision=h.ColonyRevision };
        public static string Digest(byte[] bytes) { using (var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string Evidence(string text) => Digest(Encoding.UTF8.GetBytes(text));
        public static byte[] Encode(SettlementJournalState state) { Validate(state); return ColonyJson.Serialize(state,MaxBytes); }
        public static SettlementJournalState Decode(byte[] bytes) { var s=ColonyJson.Deserialize<SettlementJournalState>(bytes,MaxBytes); Validate(s); return s; }
        public static SettlementJournalState Copy(SettlementJournalState state) => Decode(Encode(state));
        static string Genesis(SettlementJournalState s) => Digest(ColonyJson.Serialize(s.Baseline,4096));
        public static string HeadHash(SettlementJournalState s) => s.HeadHash;
        public static string Cursor(SettlementJournalState s, long sequence)
        {
            string hash=sequence==0 ? Genesis(s) : sequence==s.RetainedFromSequence ? s.RetainedFromHash : sequence==s.HeadSequence ? s.HeadHash : s.Events.Single(e=>e.Sequence==sequence).Hash;
            return s.BranchId+":"+sequence.ToString(CultureInfo.InvariantCulture)+":"+hash;
        }
        static string EventHash(SettlementEvent e)
        {
            var oldHash=e.Hash; var oldCursor=e.Cursor; e.Hash="";e.Cursor="";
            try {return Digest(ColonyJson.Serialize(e,16384));} finally {e.Hash=oldHash;e.Cursor=oldCursor;}
        }
        public static SettlementJournalState Append(SettlementJournalState prior, SettlementEvent item, SettlementHeads heads,int retainedLimit=MaxEvents)
        {
            if(retainedLimit<1 || retainedLimit>MaxEvents)throw new ArgumentOutOfRangeException(nameof(retainedLimit));
            bool recovery=item.Source=="recovery" && item.Kind=="recoverySale" && item.FundsDelta>0 && item.RouteId!=null && item.RouteVersion.HasValue && item.ShipmentId!=null;
            bool purchase=item.Source=="colony" && (item.Kind=="constructionEscrow" || item.Kind=="importPurchase" || item.Kind=="passengerFare" || item.Kind=="logisticsSetup" || item.Kind=="wolfPurchase") && item.FundsDelta<=0 && item.ColonyId!=null;
            bool refund=item.Source=="colony" && item.Kind=="constructionRefund" && item.FundsDelta>0 && item.ColonyId!=null;
            if(!recovery && !purchase && !refund || item.ScienceDelta!=0)throw new InvalidDataException("Unsupported settlement kind or amount.");
            var s=Copy(prior);
            item.WorldId=s.WorldId;item.BranchId=s.BranchId;
            item.OriginBranchId=item.BranchId;
            item.FundsDeltaExact=item.FundsDelta.ToString(CultureInfo.InvariantCulture);item.ScienceDeltaExact="0";
            item.EventId=Evidence(item.WorldId+"\n"+item.BranchId+"\n"+item.Source+"\n"+item.OperationId+"\n"+item.EventId);
            if(s.Events.Any(e=>e.EventId==item.EventId))return prior;
            item.Sequence=checked(s.HeadSequence+1);item.PreviousHash=HeadHash(s);item.Hash=EventHash(item);
            s.Events.Add(item);s.HeadSequence=item.Sequence;s.HeadHash=item.Hash;item.Cursor=Cursor(s,item.Sequence);s.Heads=CopyHeads(heads);
            while(s.Events.Count>retainedLimit) EvictFirst(s,"retentionLimit");
            // Byte/DTO bounds are observational limits too. Preserve a bounded
            // explicit lost prefix rather than vetoing the accepted payment.
            while(true)
            {
                try {Encode(s);return s;}
                catch(InvalidDataException) {if(s.Events.Count==0)throw;EvictFirst(s,"encodingLimit");}
            }
        }
        static void LostPrefix(SettlementJournalState s,string reason,double ut)
        {
            string combined=s.CoverageGaps.Count==0 ? reason : s.CoverageGaps[0].Reason.Contains(reason) ? s.CoverageGaps[0].Reason : s.CoverageGaps[0].Reason+";"+reason;
            if(combined.Length>512)combined="coverageLoss";
            s.CoverageGaps=new List<SettlementCoverageGap> {new SettlementCoverageGap {FromSequence=1,ThroughSequence=s.RetainedFromSequence,BeforeHash=Genesis(s),AfterHash=s.RetainedFromHash,Reason=combined,ThroughUt=ut}};
        }
        static void EvictFirst(SettlementJournalState s,string reason)
        {
            var first=s.Events[0];s.Events.RemoveAt(0);s.RetainedFromSequence=first.Sequence;s.RetainedFromHash=first.Hash;LostPrefix(s,reason,first.SettledUt);
        }
        public static SettlementJournalState RecordLoss(SettlementJournalState prior,string reason,double ut,SettlementHeads? heads=null)
        {
            var s=Copy(prior);long sequence=checked(s.HeadSequence+1);
            s.HeadHash=Evidence(s.HeadHash+"\ncoverageGap\n"+sequence.ToString(CultureInfo.InvariantCulture)+"\n"+reason);
            s.HeadSequence=sequence;s.RetainedFromSequence=sequence;s.RetainedFromHash=s.HeadHash;s.Events.Clear();
            LostPrefix(s,"captureFailure:"+(reason.Length>128 ? reason.Substring(0,128) : reason),ut);
            if(heads!=null)s.Heads=CopyHeads(heads);s.ObservedGameUt=ut;Encode(s);return s;
        }
        public static SettlementJournalState Observe(SettlementJournalState prior,Func<SettlementEvent> capture,SettlementHeads heads,double ut)
        {
            try {return Append(prior,capture(),heads);}catch(Exception ex){return RecordLoss(prior,"Capture failed: "+ex.GetType().Name,ut,heads);}
        }
        public static SettlementJournalState Fork(SettlementJournalState prior)
        {
            var s=Copy(prior);s.ParentBranchId=s.BranchId;s.BranchId=Guid.NewGuid().ToString("D");foreach(var e in s.Events)e.Cursor=Cursor(s,e.Sequence);return s;
        }
        public static SettlementJournalState ReconcileLoad(SettlementJournalState loaded, SettlementJournalState? tip)
        {
            Validate(loaded);if(tip==null)return loaded;Validate(tip);
            if(tip.WorldId!=loaded.WorldId)throw new InvalidDataException("Fork witness belongs to a different world.");
            if(tip.BranchId!=loaded.BranchId)return Fork(loaded);
            bool rollback=loaded.HeadSequence<tip.HeadSequence || loaded.Heads.RecoverySequence<tip.Heads.RecoverySequence || loaded.Heads.ColonySequence<tip.Heads.ColonySequence || loaded.Heads.ColonyRevision<tip.Heads.ColonyRevision || loaded.ObservedGameUt<tip.ObservedGameUt;
            bool divergence=loaded.HeadSequence==tip.HeadSequence && HeadHash(loaded)!=HeadHash(tip) || loaded.Heads.RecoverySequence==tip.Heads.RecoverySequence && loaded.Heads.RecoveryHash!=tip.Heads.RecoveryHash || loaded.Heads.ColonyRevision==tip.Heads.ColonyRevision && loaded.Heads.ColonyHash!=tip.Heads.ColonyHash;
            return rollback || divergence ? Fork(loaded) : loaded;
        }
        public static void Validate(SettlementReadRequest r)
        {
            if(r==null || r.Limit<1 || r.Limit>250 || (r.AfterCursor?.Length??0)>160 || (r.ThroughCursor?.Length??0)>160)throw new InvalidDataException("Invalid settlement page request.");
        }
        public static void Validate(SettlementPage p)
        {
            if(p==null || p.Events==null || p.Events.Count>250 || p.Gaps==null || p.Gaps.Count>16 || p.Reason==null || p.Reason.Length>4096 || (p.Status!="ok" && p.Status!="gap" && p.Status!="unavailable") || p.Complete && (p.Status!="ok" || p.Gaps.Count>0))throw new InvalidDataException("Invalid settlement page response.");
            if(p.CoverageGaps==null || p.CoverageGaps.Count>2)throw new InvalidDataException("Invalid coverage loss metadata.");
            if(p.Complete && p.CoverageGaps.Count>0)
            {
                long acknowledged=0;var parts=p.AfterCursor?.Split(':');
                if(parts!=null && (parts.Length!=3 || parts[0]!=p.BranchId || !long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out acknowledged)))throw new InvalidDataException("Invalid acknowledged page cursor.");
                if(p.CoverageGaps.Any(g=>!g.RangeKnown || g.ThroughSequence>acknowledged))throw new InvalidDataException("Requested page crosses missing coverage.");
            }
            ColonyStateCodec.Time(p.GameUt);
            foreach(var cursor in new[]{p.AfterCursor,p.CoveredThrough,p.NextCursor,p.Head,p.ReadThrough})if((cursor?.Length??0)>160)throw new InvalidDataException("Invalid page cursor length.");
            foreach(var gap in p.Gaps)if(gap==null || gap.Length>4096)throw new InvalidDataException("Invalid page gap.");
            if(p.Status=="unavailable") {if(p.Events.Count>0 || p.Complete)throw new InvalidDataException("Unavailable page claims coverage.");return;}
            if(p.Status=="gap" && p.Baseline==null && p.Events.Count==0 && !p.Complete)return;
            if(!Guid.TryParse(p.WorldId,out _) || !Guid.TryParse(p.BranchId,out _) || p.Baseline==null || p.Heads==null || p.Baseline.Funds!=100000 || p.Baseline.Science!=0)throw new InvalidDataException("Settlement page lacks coherent baseline identity.");
            string? previous=null;long sequence=0;
            foreach(var e in p.Events)
            {
                if(e==null || e.WorldId!=p.WorldId || e.OriginBranchId!=e.BranchId || e.Hash!=EventHash(e) || e.ScienceDelta!=0 || e.ScienceDeltaExact!="0" || e.FundsDeltaExact!=e.FundsDelta.ToString(CultureInfo.InvariantCulture) || previous!=null && (e.PreviousHash!=previous || e.Sequence!=sequence+1) || e.Cursor!=p.BranchId+":"+e.Sequence.ToString(CultureInfo.InvariantCulture)+":"+e.Hash)throw new InvalidDataException("Settlement page events are altered or discontinuous.");
                previous=e.Hash;sequence=e.Sequence;
            }
            if(p.Events.Count>0 && p.CoveredThrough!=p.Events[p.Events.Count-1].Cursor)throw new InvalidDataException("Settlement page coverage differs from events.");
        }
        public static void Validate(SettlementJournalState s)
        {
            if(s==null || s.Version!=2 || !Guid.TryParse(s.WorldId,out _) || !Guid.TryParse(s.BranchId,out _) || s.Baseline==null || s.Baseline.Heads==null || s.Heads==null || s.Events==null || s.Events.Count>MaxEvents || s.GapReason==null || s.GapReason.Length>1024 || s.RetainedFromSequence<0 || s.HeadSequence<s.RetainedFromSequence || s.Events.Count!=s.HeadSequence-s.RetainedFromSequence || s.CoverageGaps==null || s.CoverageGaps.Count>1)throw new InvalidDataException("Invalid settlement journal.");
            ColonyStateCodec.Time(s.Baseline.GameUt);
            ColonyStateCodec.Time(s.ObservedGameUt);
            if(s.Baseline.Funds!=100000 || s.Baseline.Science!=0)throw new InvalidDataException("Invalid new ledger baseline.");
            string previous=s.RetainedFromHash;long seq=s.RetainedFromSequence;var ids=new HashSet<string>(StringComparer.Ordinal);
            if(seq==0 && previous!=Genesis(s) || seq>0 && (s.CoverageGaps.Count!=1 || s.CoverageGaps[0].FromSequence!=1 || s.CoverageGaps[0].ThroughSequence!=seq || s.CoverageGaps[0].BeforeHash!=Genesis(s) || s.CoverageGaps[0].AfterHash!=previous))throw new InvalidDataException("Retained prefix lacks exact gap anchors.");
            if(seq==0 && s.CoverageGaps.Count!=0 || previous.Length!=64 || s.HeadHash.Length!=64)throw new InvalidDataException("Invalid retained-boundary hash or coverage loss.");
            foreach(var e in s.Events)
            {
                if(e==null || e.Sequence!=++seq || e.WorldId!=s.WorldId || e.OriginBranchId!=e.BranchId || !Guid.TryParse(e.BranchId,out _) || !ids.Add(e.EventId) || e.EventId.Length!=64 || e.EvidenceHash.Length!=64 || e.OperationId.Length==0 || e.OperationId.Length>128 || e.ScienceDelta!=0 || e.PreviousHash!=previous || e.Hash!=EventHash(e) || e.Cursor!=Cursor(s,e.Sequence))throw new InvalidDataException("Settlement journal chain is incomplete or altered.");
                ColonyStateCodec.Time(e.SettledUt);
                if(e.FundsDeltaExact!=e.FundsDelta.ToString(CultureInfo.InvariantCulture) || e.ScienceDeltaExact!="0")throw new InvalidDataException("Exact amount text differs from settlement amount.");
                bool recovery=e.Source=="recovery" && e.Kind=="recoverySale" && e.FundsDelta>0 && e.RouteId!=null && e.RouteVersion.HasValue && e.ShipmentId!=null;
                bool purchase=e.Source=="colony" && (e.Kind=="constructionEscrow" || e.Kind=="importPurchase" || e.Kind=="passengerFare" || e.Kind=="logisticsSetup" || e.Kind=="wolfPurchase") && e.FundsDelta<=0 && e.ColonyId!=null;
                bool refund=e.Source=="colony" && e.Kind=="constructionRefund" && e.FundsDelta>0 && e.ColonyId!=null;
                if(!recovery && !purchase && !refund)throw new InvalidDataException("Unsupported settlement kind or delta.");
                previous=e.Hash;
            }
            if(previous!=s.HeadHash || seq!=s.HeadSequence)throw new InvalidDataException("Journal head differs from retained tail.");
        }
        static bool Parse(SettlementJournalState s,string cursor,out long sequence,out string reason)
        {
            sequence=0;reason="";var parts=cursor.Split(':');
            if(parts.Length!=3 || !long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out sequence) || sequence<0){reason="invalidCursor";return false;}
            if(parts[0]!=s.BranchId){reason="branchChanged";return false;}
            if(sequence>s.HeadSequence){reason="rollback";return false;}
            if(sequence>0 && sequence<s.RetainedFromSequence){reason="retentionGap";return false;}
            if(cursor!=Cursor(s,sequence)){reason="cursorGap";return false;}return true;
        }
        public static SettlementPage Read(SettlementJournalState state, SettlementReadRequest request,double gameUt)
        {
            Validate(request);Validate(state);ColonyStateCodec.Time(gameUt);
            var s=Copy(state);var page=new SettlementPage {Status="ok",AfterCursor=request.AfterCursor,WorldId=s.WorldId,BranchId=s.BranchId,ParentBranchId=s.ParentBranchId,Head=Cursor(s,s.HeadSequence),GameUt=gameUt,Baseline=s.Baseline,Heads=s.Heads,BaselineCursor=Cursor(s,0),RetainedFromCursor=Cursor(s,s.RetainedFromSequence),RetainedFromHash=s.RetainedFromHash,CoverageGaps=s.CoverageGaps};
            long after=0,through=s.HeadSequence;string reason;
            if((request.AfterCursor!=null && !Parse(s,request.AfterCursor,out after,out reason))){page.Status="gap";page.Gaps.Add(reason);return page;}
            if(request.ThroughCursor!=null && !Parse(s,request.ThroughCursor,out through,out reason)){page.Status="gap";page.Gaps.Add(reason);return page;}
            if(after<s.RetainedFromSequence){page.Status="gap";page.Gaps.Add("retentionOrCaptureLoss");}
            if(through<after){page.Status="gap";page.Gaps.Add("rollback");return page;}
            if(through<s.RetainedFromSequence){page.Status="gap";page.Gaps.Add("retentionGap");page.ReadThrough=Cursor(s,through);return page;}
            page.ReadThrough=Cursor(s,through);after=Math.Max(after,s.RetainedFromSequence);page.Events=s.Events.Where(e=>e.Sequence>after && e.Sequence<=through).Take(request.Limit).ToList();
            long covered=after+page.Events.Count;page.CoveredThrough=Cursor(s,covered);page.NextCursor=page.CoveredThrough;page.HasMore=covered<through;
            if(s.GapReason.Length>0){page.Status="gap";page.Gaps.Add(s.GapReason);}page.Complete=page.Gaps.Count==0;return page;
        }
    }
}
