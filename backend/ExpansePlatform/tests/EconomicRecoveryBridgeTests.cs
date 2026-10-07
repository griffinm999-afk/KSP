#nullable disable
using Expanse.Domain;
using Expanse.WorldBridge;

// Exercise the shipping economic effect method with synchronous KSP API doubles.
// These verify callback ordering; an in-game persistence check remains separate.
[CollectionDefinition("Economic bridge", DisableParallelization = true)]
public sealed class EconomicBridgeCollection { }

[Collection("Economic bridge")]
public sealed class EconomicRecoveryBridgeTests
{
    static (WorldBridgeAddon Bridge, EffectProposal Proposal, Funding Account, RecoveryCapsuleModule Recovery) Setup()
    {
        var state = OreExportTests.Dispatched().State;
        HighLogic.CurrentGame = new Game { Mode = Game.Modes.CAREER };
        var account = Funding.Instance = new Funding(125.5);
        var recovery = RecoveryCapsuleModule.Instance = new RecoveryCapsuleModule(state);
        var intent = new EconomicRecoveryIntent { ShipmentId = "ore-shipment", FundsDelta = 500000 };
        var proposal = new EffectProposal
        {
            WorldId = state.WorldId, OperationKind = "recoverySale", ClientRequestId = "sale", CommandSequence = 4,
            OperationId = OperationIdentity.Create(state.WorldId, 4, "sale"), PayloadHash = OperationIdentity.RecoveryPayloadHash(intent),
            ExpectedRevision = state.Revision, ExpectedStateHash = AcceptedStateCodec.ComputeHash(state),
            Delivery = new DeliveryEffectPayload { Kind = "recoverySale", RecoveryIntent = intent }
        };
        return (new WorldBridgeAddon(), proposal, account, recovery);
    }

    [Fact]
    public void CallbackSaveContainsUncertainReceiptBeforeSuccessAndRetryCannotPayAgain()
    {
        var f = Setup(); AcceptedState saved = null; double savedFunds = 0;
        f.Account.Changed = () => { saved = AcceptedStateCodec.Deserialize(f.Recovery.Bytes); savedFunds = f.Account.Funds; };
        var receipt = f.Bridge.Settle(f.Proposal, f.Recovery, 64810);
        Assert.Equal("accepted", receipt.Outcome);
        Assert.Equal(500125.5, savedFunds); Assert.True(saved.WritesBlocked); Assert.Single(saved.ActiveShipments);
        Assert.Equal("faulted", saved.Receipts.Last().Outcome);
        Assert.Equal("accepted", f.Recovery.State.Receipts.Last().Outcome); Assert.Empty(f.Recovery.State.ActiveShipments);
        Assert.Equal("duplicate", f.Bridge.Settle(f.Proposal, f.Recovery, 64810).Outcome); Assert.Equal(1, f.Account.Writes);
        // A save made inside a mod callback is safe but conservatively held on reload.
        var reloaded = new RecoveryCapsuleModule(saved);
        Assert.Equal("duplicate", f.Bridge.Settle(f.Proposal, reloaded, 64810).Outcome); Assert.Equal(1, f.Account.Writes);
    }

    [Fact]
    public void ChangedFundsInsideCallbackRetainsShipmentAndBlocksAnotherPayment()
    {
        var f = Setup(); f.Account.Changed = () => f.Account.AdjustForCallback(7);
        var receipt = f.Bridge.Settle(f.Proposal, f.Recovery, 64810);
        Assert.Equal("faulted", receipt.Outcome); Assert.True(f.Recovery.State.WritesBlocked);
        Assert.Single(f.Recovery.State.ActiveShipments); Assert.Equal(500132.5, receipt.EconomicResult.ObservedAfterFunds);
        Assert.Equal("duplicate", f.Bridge.Settle(f.Proposal, f.Recovery, 64810).Outcome); Assert.Equal(1, f.Account.Writes);
    }

    [Fact]
    public void CallbackLoadCannotOverwriteNewWorldCapsule()
    {
        var f = Setup(); var other = new AcceptedState { WorldId = "other-world", CheckpointId = "loaded" };
        f.Account.Changed = () =>
        {
            HighLogic.CurrentGame = new Game { Mode = Game.Modes.CAREER };
            RecoveryCapsuleModule.Instance = new RecoveryCapsuleModule(other);
            f.Bridge.ChangeEpoch();
        };
        Assert.Equal("rejected", f.Bridge.Settle(f.Proposal, f.Recovery, 64810).Outcome);
        Assert.Equal("other-world", RecoveryCapsuleModule.Instance.WorldId); Assert.Equal(0, RecoveryCapsuleModule.Instance.State.AcceptedSequence);
        Assert.True(f.Recovery.State.WritesBlocked);
    }

    [Fact]
    public void CallbackExceptionAfterExactPaymentCanBeVerifiedWithoutSecondWrite()
    {
        var f = Setup(); f.Account.Changed = () => throw new InvalidOperationException("mod callback failure");
        Assert.Equal("accepted", f.Bridge.Settle(f.Proposal, f.Recovery, 64810).Outcome);
        Assert.Equal(500125.5, f.Account.Funds); Assert.Equal(1, f.Account.Writes);
    }

    [Fact]
    public void FailedExactFaultPreparationReturnsThePersistedConservativeEvidence()
    {
        var f = Setup(); f.Account.Changed = () => { f.Account.AdjustForCallback(1); f.Recovery.FailPreparation = true; };
        var receipt = f.Bridge.Settle(f.Proposal, f.Recovery, 64810);
        Assert.Equal("faulted", receipt.Outcome); Assert.False(receipt.EconomicResult.ObservedAfterKnown);
        Assert.Equal(f.Recovery.State.EconomicFaults.Single().Result.Reason, receipt.EconomicResult.Reason);
        Assert.True(f.Recovery.State.WritesBlocked);
    }

    [Fact]
    public void InsufficientTravelTimeOrSandboxCannotWriteFunds()
    {
        var f = Setup(); Assert.Equal("held", f.Bridge.Settle(f.Proposal, f.Recovery, 64809).Outcome); Assert.Equal(0, f.Account.Writes);
        HighLogic.CurrentGame.Mode = Game.Modes.SANDBOX;
        Assert.Equal("rejected", f.Bridge.Settle(f.Proposal, f.Recovery, 64810).Outcome); Assert.Equal(0, f.Account.Writes);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ObserverExhaustionOrCaptureFailureDoesNotVetoQualifiedPayment(bool captureFailure)
    {
        var f=Setup();
        var heads=new Expanse.Domain.Colonies.SettlementHeads {RecoverySequence=3,RecoveryHash=new string('a',64),ColonyHash=new string('b',64)};
        var journal=Expanse.Domain.Colonies.SettlementJournal.Create("3bba0a04-1978-47e4-b12f-11618c19b2a9",64809,heads);
        Expanse.Domain.Colonies.SettlementEvent Item(string op)=>new() {Source="recovery",EventId=op,OperationId=op,Kind="recoverySale",SettledUt=64810,FundsDelta=500000,RouteId="ore-route",RouteVersion=1,ShipmentId="ore-shipment",EvidenceHash=new string('e',64)};
        journal=Expanse.Domain.Colonies.SettlementJournal.Append(journal,Item("already-retained"),heads,1);
        var receipt=f.Bridge.Settle(f.Proposal,f.Recovery,64810);
        Assert.Equal("accepted",receipt.Outcome);Assert.Equal(500125.5,f.Account.Funds);Assert.Equal(1,f.Account.Writes);
        journal=captureFailure ? Expanse.Domain.Colonies.SettlementJournal.Observe(journal,()=>throw new IOException("Capture fault"),heads,64810) : Expanse.Domain.Colonies.SettlementJournal.Append(journal,Item(receipt.OperationId),heads,1);
        var restored=Expanse.Domain.Colonies.SettlementJournal.Decode(Expanse.Domain.Colonies.SettlementJournal.Encode(journal));
        var page=Expanse.Domain.Colonies.SettlementJournal.Read(restored,new(),64810);
        Assert.Equal("gap",page.Status);Assert.False(page.Complete);Assert.Single(page.CoverageGaps);
        Assert.Equal("accepted",f.Recovery.State.Receipts.Last().Outcome);Assert.Empty(f.Recovery.State.ActiveShipments);
    }
}

// Minimal KSP doubles only exist in this test assembly.
public sealed class Game { public enum Modes { CAREER, SANDBOX } public Modes Mode; }
public static class HighLogic { public static Game CurrentGame; }
public enum TransactionReasons { VesselRecovery }
public sealed class Funding
{
    public static Funding Instance;
    public double Funds { get; private set; }
    public int Writes { get; private set; }
    public Action Changed;
    public Funding(double funds) { Funds = funds; }
    public void SetFunds(double funds, TransactionReasons reason) { Funds = funds; Writes++; Changed?.Invoke(); }
    public void AdjustForCallback(double delta) { Funds += delta; }
}

namespace Expanse.WorldBridge
{
    public sealed class RecoveryCapsuleModule
    {
        public static RecoveryCapsuleModule Instance;
        public AcceptedState State;
        public byte[] Bytes;
        public bool FailPreparation;
        public string WorldId => State.WorldId;
        public string StateHash => AcceptedStateCodec.ComputeHash(State);
        internal sealed class PreparedAcceptedState { internal AcceptedState State; internal string Hash; internal byte[] Bytes; }
        public RecoveryCapsuleModule(AcceptedState state) { State = state; Bytes = AcceptedStateCodec.Serialize(state); }
        internal PreparedAcceptedState PrepareAcceptedStateReplacement(string world, byte[] bytes, out string reason)
        {
            reason = FailPreparation ? "Test preparation failure" : null;
            if (FailPreparation) return null;
            var state = AcceptedStateCodec.Deserialize(bytes);
            return new PreparedAcceptedState { State = state, Bytes = bytes, Hash = AcceptedStateCodec.ComputeHash(state) };
        }
        internal void CommitPreparedState(PreparedAcceptedState prepared) { State = prepared.State; Bytes = prepared.Bytes; }
    }

    public sealed partial class WorldBridgeAddon
    {
        private string loadEpoch = "epoch", runId = "run";
        public void ChangeEpoch() { loadEpoch = "changed"; }
        public EffectReceipt Settle(EffectProposal proposal, RecoveryCapsuleModule recovery, double ut)
            => ApplyEconomicRecoveryProposal(proposal, recovery.State, recovery, ut);
        private EffectReceipt BuildRejectedReceipt(EffectProposal p, string reason) => new EffectReceipt { Outcome = "rejected", Reason = reason };
        private EffectReceipt BuildReceipt(EffectProposal p, string outcome, double ut, long delta, AcceptedState state, string reason,
            object physicalResult = null, RecoveryCapsuleModule.PreparedAcceptedState witness = null, EconomicEffectResult economicResult = null)
            => new EffectReceipt { Outcome = outcome, Reason = reason, EconomicResult = economicResult };
        private static bool SafeFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        private static string BoundEffect(string s, int length) => s.Length <= length ? s : s.Substring(0, length);
        private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        private static string F(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string I(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
