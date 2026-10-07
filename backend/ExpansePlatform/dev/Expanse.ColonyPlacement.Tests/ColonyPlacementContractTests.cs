using System;
using System.Globalization;
using Expanse.WorldBridge;
using Xunit;

public sealed class ColonyPlacementContractTests
{
    private static ColonyPlacementRequest Valid() => new ColonyPlacementRequest
    {
        WorldId = "copied-world", ColonyId = "colony-1", PlotId = "plot-1", OperationId = "construction-1", FacilityName = "Resident home",
        TemplateRelativePath = "Habitat-v1.craft", TemplateSha256 = new string('a', 64), CertificationId = "habitat-v1-cert", SurveyRevision = "survey-1", EscrowWitness = "paid-escrow-1",
        BodyName = "Minmus", Latitude = 1, Longitude = 1, HeadingDegrees = 90, MinX = -5, MaxX = 5, MinZ = -7, MaxZ = 7, MaximumHeight = 5,
        Contents = new[] { new ColonyPlacementContent { CraftPartId = 100, ResourceName = "ElectricCharge", Amount = 200 }, new ColonyPlacementContent { CraftPartId = 102, ResourceName = "Machinery", Amount = 10 } }
    };

    [Fact] public void ValidPaidRequestIsAccepted() => Assert.Null(Valid().Validate());

    [Theory]
    [InlineData("../Outside.craft")]
    [InlineData("sub/../../Outside.craft")]
    [InlineData("C:\\live\\save.craft")]
    [InlineData("/absolute.craft")]
    [InlineData("\\\\server\\share.craft")]
    [InlineData("sub//Hab.craft")]
    [InlineData("Hab.sfs")]
    public void RejectsExternalOrMalformedTemplates(string path)
    { var request = Valid(); request.TemplateRelativePath = path; Assert.NotNull(request.Validate()); }

    [Fact] public void DuplicateTankAllocationIsRejectedBeforeMutation()
    { var request = Valid(); request.Contents = new[] { request.Contents[0], request.Contents[0] }; Assert.NotNull(request.Validate()); }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1000000001)]
    public void InvalidOrUnboundedStockIsRejected(double amount)
    { var request = Valid(); request.Contents[0].Amount = amount; Assert.NotNull(request.Validate()); }

    [Fact] public void NoEscrowWitnessCannotCreateFreeBuilding()
    { var request = Valid(); request.EscrowWitness = ""; Assert.NotNull(request.Validate()); }

    [Fact] public void FingerprintBindsWorldPlotAndStartupStock()
    {
        var request = Valid(); string original = request.Fingerprint();
        var branch = request.Copy(); branch.WorldId = "another-world"; Assert.NotEqual(original, branch.Fingerprint());
        var plot = request.Copy(); plot.Latitude += 0.00001; Assert.NotEqual(original, plot.Fingerprint());
        var freeContent = request.Copy(); freeContent.Contents[0].Amount++; Assert.NotEqual(original, freeContent.Fingerprint());
        var alteredEnvelope = request.Copy(); alteredEnvelope.MaxX++; Assert.NotEqual(original, alteredEnvelope.Fingerprint());
        var unlock = request.Copy(); unlock.ExplicitSandboxUnlockOverride = true; Assert.NotEqual(original, unlock.Fingerprint());
    }

    [Fact] public void CallerCannotAlterCopiedCommittedContents()
    { var request = Valid(); var copy = request.Copy(); string committed = copy.Fingerprint(); request.Contents[0].Amount = 900; request.MinX = -20; Assert.Equal(committed, copy.Fingerprint()); }

    [Fact] public void SemanticallyEqualContentOrderAndCultureProduceSameFingerprint()
    {
        var request = Valid(); string expected = request.Fingerprint(); Array.Reverse(request.Contents);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.Equal(expected, request.Fingerprint()); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact] public void MissingMarkerAfterPotentialCreationMustNeverAuthorizeRespawn()
    {
        foreach (var stage in new[] { ColonyPlacementStage.Prepared, ColonyPlacementStage.Created, ColonyPlacementStage.Settling, ColonyPlacementStage.Anchored, ColonyPlacementStage.RecoveryHold })
            Assert.NotNull(ColonyPlacementRecovery.Reconcile(stage, 0, true, true));
    }

    [Fact] public void DuplicateMarkersAlwaysQuarantine()
    { foreach (ColonyPlacementStage stage in Enum.GetValues<ColonyPlacementStage>()) Assert.NotNull(ColonyPlacementRecovery.Reconcile(stage, 2, true, true)); }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void PartialOrChangedExistingBuildingCannotComplete(bool members, bool witnesses)
    { Assert.NotNull(ColonyPlacementRecovery.Reconcile(ColonyPlacementStage.Created, 1, members, witnesses)); }

    [Fact] public void CancelledReceiptCannotHideExistingBuilding()
    { Assert.NotNull(ColonyPlacementRecovery.Reconcile(ColonyPlacementStage.Cancelled, 1, true, true)); }

    [Fact] public void SupportedRecoveryRequiresExistingMatchingBuilding()
    { Assert.Null(ColonyPlacementRecovery.Reconcile(ColonyPlacementStage.Settling, 1, true, true)); }

    [Fact] public void BoundsRejectExcessiveGeometryAndUnsupportedQuaternion()
    {
        var request = Valid(); request.MaxX = 51; Assert.NotNull(request.Validate());
        request = Valid(); request.MaximumSupportGapMetres = 2.1; Assert.NotNull(request.Validate());
        request = Valid(); request.TemplateRotationW = 0.5; Assert.NotNull(request.Validate());
        request = Valid(); request.MaximumSlopeDegrees = 16; Assert.NotNull(request.Validate());
    }
}
