using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Tests;

public class SettlementJournalTests
{
    const string World="3bba0a04-1978-47e4-b12f-11618c19b2a9";
    const string Colony="be28c55a-a190-4508-beaf-b964cd071e90";
    static SettlementHeads Heads(long n=200) => new() {RecoverySequence=n,RecoveryHash=new string('a',64),ColonySequence=n,ColonyRevision=n,ColonyHash=new string('b',64)};
    static SettlementJournalState New() => SettlementJournal.Create(World,500,Heads());
    static SettlementEvent Sale(string op,long amount=12345) => new() {Source="recovery",OperationId=op,EventId=op,Kind="recoverySale",SettledUt=501,FundsDelta=amount,RouteId="ore-route",RouteVersion=7,ShipmentId="shipment-"+op,SourceDepotId="source",VesselId="vessel",ColonyId=Colony,EvidenceHash=new string('e',64)};
    static SettlementEvent Purchase(string op,string kind="importPurchase",long amount=-100) => new() {Source="colony",EventId="effect-"+op,OperationId=op,Kind=kind,SettledUt=502,FundsDelta=amount,ColonyId=Colony,EvidenceHash=new string('f',64)};

    [Fact] public void NewBaselineSkipsOldSourceHeadsAndContainsNoHistoricalIncome()
    {
        var s=New();var page=SettlementJournal.Read(s,new(),500);
        Assert.Equal(100000,page.Baseline!.Funds);Assert.Equal(0,page.Baseline.Science);
        Assert.Equal(200,page.Baseline.Heads.RecoverySequence);Assert.Equal(500,page.Baseline.GameUt);
        Assert.Empty(page.Events);Assert.True(page.Complete);Assert.Equal(page.Head,page.NextCursor);
        Assert.Equal(s.BranchId,SettlementJournal.Decode(SettlementJournal.Encode(s)).BranchId);
    }
    [Fact] public void CapturedEventsOutliveBothOperationalReceiptWindowsAndDeduplicate()
    {
        var s=New();for(int i=0;i<70;i++)s=SettlementJournal.Append(s,Sale("op-"+i),Heads(201+i));
        var before=SettlementJournal.Encode(s);var same=SettlementJournal.Append(s,Sale("op-69"),Heads(270));
        Assert.Same(s,same);Assert.Equal(before,SettlementJournal.Encode(same));
        var reload=SettlementJournal.Decode(before);Assert.Equal(70,reload.Events.Count);
        Assert.Equal(7,reload.Events[0].RouteVersion);Assert.Equal("vessel",reload.Events[0].VesselId);
        Assert.Equal(Colony,reload.Events[0].ColonyId);Assert.Equal("source",reload.Events[0].SourceDepotId);
    }
    [Fact] public void ExactIncomeExpenseAndRefundAreSeparateFromCargoAndScience()
    {
        var s=SettlementJournal.Append(New(),Sale("sale",9007199254740993L),Heads(201));
        s=SettlementJournal.Append(s,Purchase("purchase"),Heads(202));
        s=SettlementJournal.Append(s,Purchase("refund","constructionRefund",33),Heads(203));
        var page=SettlementJournal.Read(SettlementJournal.Decode(SettlementJournal.Encode(s)),new(),503);
        Assert.Equal("9007199254740993",page.Events[0].FundsDeltaExact);
        Assert.Equal(-100,page.Events[1].FundsDelta);Assert.Equal(33,page.Events[2].FundsDelta);
        Assert.All(page.Events,e=>Assert.Equal(0,e.ScienceDelta));
        Assert.Throws<InvalidDataException>(()=>SettlementJournal.Append(s,Purchase("cargo","cargoValue",100),Heads()));
    }
    [Fact] public void PaginationPinsCoverageWhileNewSettlementsArrive()
    {
        var s=New();for(int i=0;i<5;i++)s=SettlementJournal.Append(s,Sale("op-"+i),Heads(201+i));
        var first=SettlementJournal.Read(s,new(){Limit=2},510);
        Assert.True(first.HasMore);Assert.True(first.Complete);Assert.Equal(2,first.Events.Count);
        s=SettlementJournal.Append(s,Sale("later"),Heads(206));
        var second=SettlementJournal.Read(s,new(){AfterCursor=first.NextCursor,ThroughCursor=first.ReadThrough,Limit=2},511);
        var last=SettlementJournal.Read(s,new(){AfterCursor=second.NextCursor,ThroughCursor=first.ReadThrough,Limit=2},512);
        Assert.Single(last.Events);Assert.False(last.HasMore);Assert.Equal(first.Head,last.CoveredThrough);
        Assert.NotEqual(last.Head,last.ReadThrough);Assert.Equal(first.NextCursor,second.AfterCursor);
    }
    [Fact] public void OrdinaryReloadPreservesBranchButRollbackAndDivergenceFork()
    {
        var baseline=New();var tip=SettlementJournal.Append(baseline,Sale("sale"),Heads(201));
        var ordinary=SettlementJournal.ReconcileLoad(SettlementJournal.Copy(tip),tip);
        Assert.Equal(tip.BranchId,ordinary.BranchId);
        var rolled=SettlementJournal.ReconcileLoad(SettlementJournal.Copy(baseline),tip);
        Assert.NotEqual(tip.BranchId,rolled.BranchId);Assert.Equal(tip.BranchId,rolled.ParentBranchId);
        var gap=SettlementJournal.Read(rolled,new(){AfterCursor=SettlementJournal.Cursor(tip,1)},501);
        Assert.False(gap.Complete);Assert.Contains("branchChanged",gap.Gaps);
        var divergent=SettlementJournal.Append(baseline,Sale("other"),Heads(201));
        Assert.NotEqual(divergent.BranchId,SettlementJournal.ReconcileLoad(divergent,tip).BranchId);
    }
    [Fact] public void ForksAlsoDetectSourceRollbackWithoutAnyNewFinancialEvent()
    {
        var old=New();var tip=SettlementJournal.Copy(old);tip.Heads=Heads(201);tip.ObservedGameUt=600;
        Assert.NotEqual(old.BranchId,SettlementJournal.ReconcileLoad(old,tip).BranchId);
        var withEvent=SettlementJournal.Append(old,Sale("old"),Heads(201));
        var fork=SettlementJournal.Fork(withEvent);SettlementJournal.Encode(fork);
        Assert.Equal(withEvent.Events[0].EventId,fork.Events[0].EventId);
        Assert.StartsWith(fork.BranchId+":",SettlementJournal.Read(fork,new(),600).Events[0].Cursor);
    }
    [Fact] public void CursorRollbackAlterationAndDeclaredLossAreExplicitGaps()
    {
        var old=New();var tip=SettlementJournal.Append(old,Sale("sale"),Heads(201));
        Assert.Contains("rollback",SettlementJournal.Read(old,new(){AfterCursor=SettlementJournal.Cursor(tip,1)},500).Gaps);
        var bad=SettlementJournal.Cursor(old,0)[..^1]+"f";
        Assert.Contains("cursorGap",SettlementJournal.Read(old,new(){AfterCursor=bad},500).Gaps);
        old.GapReason="Archive unavailable";var page=SettlementJournal.Read(old,new(),500);
        Assert.False(page.Complete);Assert.Contains("Archive unavailable",page.Gaps);
        Assert.False(SettlementJournal.Read(SettlementJournal.Append(old,Sale("new"),Heads()),new(),501).Complete);
    }
    [Fact] public void AlteredOrMissingJournalEventsCannotDecodeAsComplete()
    {
        var s=SettlementJournal.Append(New(),Sale("one"),Heads(201));s=SettlementJournal.Append(s,Sale("two"),Heads(202));
        string json=Encoding.UTF8.GetString(SettlementJournal.Encode(s));
        Assert.Throws<InvalidDataException>(()=>SettlementJournal.Decode(Encoding.UTF8.GetBytes(json.Replace("12345","12346"))));
        s.Events.RemoveAt(0);Assert.Throws<InvalidDataException>(()=>SettlementJournal.Encode(s));
    }
    [Fact] public void AdditiveWireReadRoundTripsWithoutChangingSnapshotOrSubmit()
    {
        var request=new ColonyManagementWireRequest {RequestId=Guid.NewGuid().ToString("D"),Kind="settlements",Settlements=new(){Limit=7}};
        var decoded=ColonyManagementWire.DecodeRequest(ColonyManagementWire.Encode(request));Assert.Equal(7,decoded.Settlements!.Limit);
        var response=new ColonyManagementWireResponse {RequestId=request.RequestId,Outcome="settlements",Settlements=SettlementJournal.Read(New(),new(),500)};
        Assert.Equal(100000,ColonyManagementWire.DecodeResponse(ColonyManagementWire.Encode(response)).Settlements!.Baseline!.Funds);
        request.Command=new();Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(request));
        request.Command=null;request.Settlements.Limit=251;Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.Encode(request));
    }
    [Theory][InlineData("12345.5")][InlineData("-0.5")][InlineData("12345e0")]
    public void FractionalOrNonIntegerWireAmountsAreRejectedWithoutRounding(string amount)
    {
        var state=SettlementJournal.Append(New(),Sale("sale"),Heads(201));
        var response=new ColonyManagementWireResponse {RequestId=Guid.NewGuid().ToString("D"),Outcome="settlements",Settlements=SettlementJournal.Read(state,new(),501)};
        var json=Encoding.UTF8.GetString(ColonyManagementWire.Encode(response));
        Assert.Contains("\"FundsDelta\":12345",json);
        json=json.Replace("\"FundsDelta\":12345","\"FundsDelta\":"+amount);
        Assert.Throws<InvalidDataException>(()=>ColonyManagementWire.DecodeResponse(Encoding.UTF8.GetBytes(json)));
    }
    [Fact] public void FractionalAccountBalancePreservesWholeSettlementDeltaAndRoundedReadbackRejects()
    {
        var result=new Expanse.Domain.EconomicEffectResult {Status="applied",BeforeFunds=125.5,IntendedDeltaFunds=500000,IntendedAfterFunds=500125.5,ObservedAfterKnown=true,ObservedAfterFunds=500125.5};
        Expanse.Domain.OreExportPolicy.ValidateResult(result);
        Assert.Equal(500000,result.ObservedAfterFunds-result.BeforeFunds);
        result.ObservedAfterFunds=Math.Floor(result.ObservedAfterFunds);
        Assert.Throws<InvalidDataException>(()=>Expanse.Domain.OreExportPolicy.ValidateResult(result));
    }
    [Fact] public void BoundedTailPersistsExactLostPrefixAndNeverResetsJournalSequence()
    {
        var s=New();string baseline=SettlementJournal.Cursor(s,0);
        for(int i=0;i<5;i++)s=SettlementJournal.Append(s,Sale("retained-"+i),Heads(201+i),2);
        Assert.Equal(5,s.HeadSequence);Assert.Equal(3,s.RetainedFromSequence);Assert.Equal(2,s.Events.Count);
        Assert.Equal(new long[]{4,5},s.Events.Select(e=>e.Sequence));
        var reload=SettlementJournal.Decode(SettlementJournal.Encode(s));var page=SettlementJournal.Read(reload,new(),510);
        Assert.Equal(baseline,page.BaselineCursor);Assert.Equal(SettlementJournal.Cursor(s,3),page.RetainedFromCursor);
        Assert.Equal(s.RetainedFromHash,page.RetainedFromHash);Assert.Equal("gap",page.Status);Assert.False(page.Complete);
        Assert.Equal(1,page.CoverageGaps.Single().FromSequence);Assert.Equal(3,page.CoverageGaps.Single().ThroughSequence);
        Assert.Equal(s.RetainedFromHash,page.CoverageGaps.Single().AfterHash);
        var fork=SettlementJournal.Fork(reload);Assert.Equal(5,fork.HeadSequence);Assert.Equal(s.Events[0].BranchId,fork.Events[0].BranchId);
        Assert.NotEqual(fork.BranchId,fork.Events[0].BranchId);Assert.StartsWith(fork.BranchId+":",fork.Events[0].Cursor);
        fork=SettlementJournal.Append(fork,Sale("child"),Heads(206),2);Assert.Equal(6,fork.Events.Last().Sequence);
    }
    [Fact] public void CaptureFailureHasPersistedGapAndFutureAcceptedEventsRemainObservable()
    {
        var s=SettlementJournal.Append(New(),Sale("before-failure"),Heads(201));
        s=SettlementJournal.Observe(s,()=>throw new IOException("Injected observer capture failure"),Heads(202),502);
        Assert.Equal(2,s.HeadSequence);Assert.Empty(s.Events);Assert.Single(s.CoverageGaps);
        s=SettlementJournal.Append(s,Sale("after-failure"),Heads(203));
        var page=SettlementJournal.Read(SettlementJournal.Decode(SettlementJournal.Encode(s)),new(),503);
        Assert.Equal("gap",page.Status);Assert.False(page.Complete);Assert.Equal(3,page.Events.Single().Sequence);
        Assert.Contains("captureFailure",page.CoverageGaps.Single().Reason);
    }
    [Fact] public void AcknowledgedBoundaryContinuesCompleteSuffixAfterPruningButLaggingCursorGaps()
    {
        var s=New();s=SettlementJournal.Append(s,Sale("one"),Heads(201),2);
        string lagging=SettlementJournal.Cursor(s,1);
        s=SettlementJournal.Append(s,Sale("two"),Heads(202),2);
        string acknowledged=SettlementJournal.Cursor(s,2);
        s=SettlementJournal.Append(s,Sale("three"),Heads(203),2);
        s=SettlementJournal.Append(s,Sale("four"),Heads(204),2);
        var caughtUp=SettlementJournal.Read(s,new(){AfterCursor=acknowledged},505);
        Assert.Equal("ok",caughtUp.Status);Assert.True(caughtUp.Complete);Assert.Equal(new long[]{3,4},caughtUp.Events.Select(e=>e.Sequence));
        Assert.Single(caughtUp.CoverageGaps);SettlementJournal.Validate(caughtUp);
        var laggingPage=SettlementJournal.Read(s,new(){AfterCursor=lagging},505);
        Assert.Equal("gap",laggingPage.Status);Assert.False(laggingPage.Complete);Assert.Contains("retentionGap",laggingPage.Gaps);
        var tampered=acknowledged[..^1]+(acknowledged[^1]=='a' ? "b" : "a");
        Assert.Contains("cursorGap",SettlementJournal.Read(s,new(){AfterCursor=tampered},505).Gaps);
    }
}
