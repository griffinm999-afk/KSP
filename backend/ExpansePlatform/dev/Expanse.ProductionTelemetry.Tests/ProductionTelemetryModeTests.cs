using Expanse.WorldBridge;

namespace Expanse.Clock.Tests;

public sealed class ProductionTelemetryModeTests
{
    [Fact] public void BeforeOnRailsFlagChangesNeitherOldNorNestedCaptureQualifies()
    {
        var mode=new ProductionTelemetryMode();Assert.True(mode.TryCapture(false,out var old));
        mode.Transition(true);
        Assert.False(mode.Matches(old,false,false));Assert.False(mode.TryCapture(false,out _));
        Assert.False(mode.Matches(old,false,true));
        Assert.True(mode.TryCapture(true,out var next));Assert.True(mode.Matches(next,true,true));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SameUtRoundtripInvalidatesBothModes(bool packed)
    {
        var mode=new ProductionTelemetryMode();mode.TryCapture(packed,out var old);
        mode.Transition(!packed);mode.Transition(packed);
        Assert.False(mode.Matches(old,packed,packed));
        Assert.True(mode.TryCapture(packed,out var next));Assert.True(mode.Matches(next,packed,packed));
    }
    [Fact] public void UnannouncedFlagChangeAlsoInvalidates()
    {
        var mode=new ProductionTelemetryMode();mode.TryCapture(false,out var old);
        Assert.False(mode.Matches(old,false,true));Assert.False(mode.Matches(old,false,false));
    }
    [Fact] public void UnfinishedTransitionRemainsUnknownUntilTargetOrNewEvent()
    {
        var mode=new ProductionTelemetryMode();mode.Transition(true);
        for(int i=0;i<4;i++)Assert.False(mode.TryCapture(false,out _));
        mode.Transition(false);Assert.True(mode.TryCapture(false,out var next));
        Assert.True(mode.Matches(next,false,false));
    }
    [Fact] public void TransitionDuringCallbackInvalidatesReceiptAndRestoresParent()
    {
        var mode=new ProductionTelemetryMode();mode.TryCapture(true,out var epoch);
        var ledger=new ProductionTelemetryLedger();var broker=new object();var part=new object();
        var parent=ledger.Enter("session",broker,part,"parent",100,1,true);
        var child=ledger.Enter("session",broker,part,"child",100,1,true);
        ledger.Record(broker,part,"Supplies",5,-2,true);mode.Transition(false);
        child.Valid=mode.Matches(epoch,true,false);
        Assert.False(ledger.Complete(child,true,1));
        ledger.Record(broker,part,"Water",3,1,false);Assert.True(ledger.Complete(parent,true,1));
        Assert.Empty(parent.Outputs);Assert.Equal(1,parent.Inputs["Water"]);
    }
    [Theory][InlineData(0)][InlineData(1.5)][InlineData(6)]
    public void PackedNormalCompletionPreservesAcceptedOutputAndZero(double accepted)
    {
        var mode=new ProductionTelemetryMode();Assert.True(mode.TryCapture(true,out var epoch));
        var ledger=new ProductionTelemetryLedger();var broker=new object();var part=new object();
        var frame=ledger.Enter("session",broker,part,"bay:difficulty",100,2,true);
        ledger.Record(broker,part,"Supplies",6,-accepted,true);
        Assert.True(mode.Matches(epoch,true,true));Assert.True(ledger.Complete(frame,true,2));
        Assert.Equal(accepted/2,frame.Outputs["Supplies"]/frame.Seconds);
        Assert.True(ProductionTelemetryLedger.Fresh(frame,"session","bay:difficulty",100,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"session","different-bay",100,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"new-session","bay:difficulty",100,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"session","bay:difficulty",111,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"session","bay:difficulty",99,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"session","bay:difficulty",100,false));
        Assert.False(ProductionTelemetryLedger.Fresh(null!,"session","bay:difficulty",100,true));
        Assert.False(ledger.Complete(frame,true,2)); // repeated finalizer cannot credit twice
    }
}
