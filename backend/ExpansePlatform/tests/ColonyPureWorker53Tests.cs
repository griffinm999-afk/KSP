using System.Reflection;
using System.IO;
using System.Threading.Tasks;
using Expanse.Domain.Colonies;
using Expanse.WorldBridge;

namespace Expanse.Clock.Tests;

public sealed class ColonyPureWorker53Tests
{
    static (ColonyState s,ColonyEnvironment e,object game,byte[] bytes) Fixture()
    {
        var s=ColonyEngine.Create(Guid.NewGuid().ToString("D"),100);
        var e=new ColonyEnvironment{WorldId=s.WorldId,ContextKey="save/epoch",Ut=101,AvailableFunds=1000};
        return(s,e,new object(),ColonyStateCodec.Serialize(s));
    }
    static async Task Complete(ColonyPureTickWork work)
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(!work.IsCompleted&&DateTime.UtcNow<deadline)await Task.Delay(1);
        Assert.True(work.IsCompleted,"Bounded pure task did not complete");
    }

    [Theory]
    [InlineData("state")]
    [InlineData("game")]
    [InlineData("world")]
    [InlineData("context")]
    [InlineData("epoch")]
    public async Task ExactAuthorityMismatchRejectsResult(string change)
    {
        var(s,e,g,b)=Fixture();var work=new ColonyPureTickWork(s,g,s.WorldId,e.ContextKey,"epoch",b,e);await Complete(work);
        Assert.False(work.TryTake(change=="state"?ColonyStateCodec.Copy(s):s,change=="game"?new object():g,
            change=="world"?Guid.NewGuid().ToString("D"):s.WorldId,change=="context"?"another save":e.ContextKey,
            change=="epoch"?"new load":"epoch",out _,out _));
        Assert.Equal(b,ColonyStateCodec.Serialize(s));
    }

    [Fact] public async Task DetachedInputSurvivesCallerGraphAndByteMutation()
    {
        var(s,e,g,b)=Fixture();var priorWorld=s.WorldId;var context=e.ContextKey;
        e.Templates.Add(new(){Id="test",Name="Original"});
        var work=new ColonyPureTickWork(s,g,priorWorld,context,"epoch",b,e);
        s.SimulatedUt=999;e.Ut=999;e.Templates[0].Name="Changed";Array.Fill(b,(byte)0);
        await Complete(work);
        Assert.True(work.TryTake(s,g,priorWorld,context,"epoch",out var result,out var error),error);
        Assert.Equal(101,result.State.SimulatedUt);Assert.Equal(101,result.Environment.Ut);
        Assert.Equal("Original",result.Environment.Templates[0].Name);
        Assert.Equal(result.Bytes,ColonyStateCodec.Serialize(result.State));
        Assert.Equal(999,s.SimulatedUt);
    }

    [Fact] public async Task TwoPassesMatchSerialSimulationAndPlanningWithSurveyBetweenThem()
    {
        var(s,e,id)=ColonyPlanningTests.GrowthFixture();e.Ut+=.5;var game=new object();
        var original=ColonyStateCodec.Serialize(s);var expectedAdvance=ColonyEngine.Advance(s,e,out var advanceBytes);
        var work=new ColonyPureTickWork(s,game,s.WorldId,e.ContextKey,"epoch",original,e);await Complete(work);
        Assert.True(work.TryTake(s,game,s.WorldId,e.ContextKey,"epoch",out var advanced,out var error),error);
        Assert.Equal(advanceBytes,advanced.Bytes);Assert.Equal(ColonyStateCodec.Serialize(expectedAdvance),advanced.Bytes);
        // The main-thread survey transition is represented by a changed accepted
        // state, not a worker callback. Both paths start planning after it.
        expectedAdvance.Colonies[0].Name="Survey accepted before planning";
        advanced.State.Colonies[0].Name="Survey accepted before planning";
        var postSurvey=ColonyStateCodec.Serialize(advanced.State);
        var expected=ColonyEngine.RunPlanningAndProcurement(expectedAdvance,e,"");
        work.StartPlanning(advanced.State,game,s.WorldId,e.ContextKey,"epoch",postSurvey,advanced.Environment,"");await Complete(work);
        Assert.True(work.TryTake(advanced.State,game,s.WorldId,e.ContextKey,"epoch",out var planned,out error),error);
        Assert.Equal(ColonyStateCodec.Serialize(expected),planned.Bytes);Assert.False(planned.AdvanceOnly);
        Assert.Equal(original,ColonyStateCodec.Serialize(s));
        Assert.False(work.TryTake(advanced.State,game,s.WorldId,e.ContextKey,"epoch",out _,out _));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("save")]
    [InlineData("exit")]
    [InlineData("load")]
    public async Task InvalidationDoesNotWaitForPendingWorkOrLoseAcceptedBytes(string boundary)
    {
        var(s,e,g,b)=Fixture();var work=new ColonyPureTickWork(s,g,s.WorldId,e.ContextKey,"epoch",b,e);await Complete(work);
        var held=new TaskCompletionSource<ColonyPureTickWork.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ColonyPureTickWork).GetField("task",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(work,held.Task);
        var invalidated=Task.Run(work.Invalidate);
        Assert.Same(invalidated,await Task.WhenAny(invalidated,Task.Delay(1000)));
        Assert.False(work.IsCompleted);Assert.Equal(b,ColonyStateCodec.Serialize(s));
        Assert.False(work.TryTake(s,g,s.WorldId,e.ContextKey,"epoch",out _,out _));
        held.SetResult(new(){State=ColonyStateCodec.Copy(s),Bytes=b,Environment=e,AdvanceOnly=true});await Complete(work);
        Assert.False(work.TryTake(s,g,s.WorldId,e.ContextKey,"epoch",out _,out _));
        Assert.Equal(b,ColonyStateCodec.Serialize(s));Assert.NotEmpty(boundary);
    }

    [Fact] public async Task FaultedPureInputIsDiscardedAndNextOperationRecovers()
    {
        var(s,e,g,b)=Fixture();var bad=new ColonyPureTickWork(s,g,s.WorldId,e.ContextKey,"epoch",new byte[]{1,2,3},e);await Complete(bad);
        Assert.False(bad.TryTake(s,g,s.WorldId,e.ContextKey,"epoch",out _,out var error));Assert.NotEmpty(error);
        Assert.Equal(b,ColonyStateCodec.Serialize(s));
        var good=new ColonyPureTickWork(s,g,s.WorldId,e.ContextKey,"epoch",b,e);await Complete(good);
        Assert.True(good.TryTake(s,g,s.WorldId,e.ContextKey,"epoch",out var result,out error),error);
        Assert.Equal(result.Bytes,ColonyStateCodec.Serialize(result.State));
    }

    [Fact] public async Task PlanningCannotOverlapPendingAdvance()
    {
        var(s,e,g,b)=Fixture();var work=new ColonyPureTickWork(s,g,s.WorldId,e.ContextKey,"epoch",b,e);await Complete(work);
        var held=new TaskCompletionSource<ColonyPureTickWork.Result>();
        typeof(ColonyPureTickWork).GetField("task",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(work,held.Task);
        Assert.Throws<InvalidOperationException>(()=>work.StartPlanning(s,g,s.WorldId,e.ContextKey,"epoch",b,e,""));
        held.SetResult(new(){Error="test completion"});
    }

    [Fact] public async Task SavedPaidClaimsRemainExactWhenPendingResultIsInvalidated()
    {
        var(s,e,id)=ColonyPlanningTests.Fixture();s=ColonyPlanningTests.Stage(s,e,id);
        Assert.True(s.Colonies[0].SpentFunds>0);Assert.Contains(s.Effects,x=>x.State=="applied");
        var saved=ColonyStateCodec.Serialize(s);var game=new object();e.Ut+=.5;
        var work=new ColonyPureTickWork(s,game,s.WorldId,e.ContextKey,"epoch",saved,e);work.Invalidate();await Complete(work);
        Assert.False(work.TryTake(s,game,s.WorldId,e.ContextKey,"epoch",out _,out _));
        Assert.Equal(saved,ColonyStateCodec.Serialize(s));
        Assert.Equal(s.Colonies[0].SpentFunds,ColonyStateCodec.Deserialize(saved).Colonies[0].SpentFunds);
    }

    [Fact] public async Task RecordedNative52SnapshotMatchesBothSerialPassesAndAcceptedBytes()
    {
        var snapshot=ColonyManagementWire.DecodeResponse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"recorded52.json"))).Snapshot!;
        var s=snapshot.State!;var e=new ColonyEnvironment{WorldId=s.WorldId,ContextKey=snapshot.ContextKey,Ut=snapshot.ObservedUt,
            AvailableFunds=snapshot.AvailableFunds!.Value,Templates=snapshot.Templates,UnlockedTech=snapshot.UnlockedTech,
            DevelopmentMode=snapshot.DevelopmentMode,People=snapshot.People,Services=snapshot.Services,EconomyPolicies=snapshot.EconomyPolicies,
            Wolf=snapshot.Wolf,Support=snapshot.Support,Planning=snapshot.Planning,Production=snapshot.Production,BodyRadiiMeters=snapshot.BodyRadiiMeters,
            AdoptableFacilities=snapshot.AdoptableFacilities,FacilitySites=snapshot.FacilitySites};
        var b=ColonyStateCodec.Serialize(s);var game=new object();var expected=ColonyEngine.Advance(s,e,out var advancedBytes);
        var work=new ColonyPureTickWork(s,game,s.WorldId,e.ContextKey,"epoch",b,e);await Complete(work);
        Assert.True(work.TryTake(s,game,s.WorldId,e.ContextKey,"epoch",out var advanced,out var error),error);
        Assert.Equal(advancedBytes,advanced.Bytes);Assert.Equal(ColonyStateCodec.Serialize(advanced.State),advanced.Bytes);
        expected=ColonyEngine.RunPlanningAndProcurement(expected,e,"");
        work.StartPlanning(advanced.State,game,s.WorldId,e.ContextKey,"epoch",advanced.Bytes,advanced.Environment,"");await Complete(work);
        Assert.True(work.TryTake(advanced.State,game,s.WorldId,e.ContextKey,"epoch",out var planned,out error),error);
        Assert.Equal(ColonyStateCodec.Serialize(expected),planned.Bytes);Assert.Equal(ColonyStateCodec.Serialize(planned.State),planned.Bytes);
        Assert.Equal(b,ColonyStateCodec.Serialize(s));
    }
}
