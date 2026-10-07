using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.WorldBridge
{
    public enum ColonyPlacementStage
    {
        AwaitingPlacement, Prepared, Created, Settling, Anchored, RecoveryHold, Cancelled
    }

    // Explicit, already paid contents. Template defaults are never a source of stock.
    public sealed class ColonyPlacementContent
    {
        public uint CraftPartId;
        public string ResourceName;
        public double Amount;
    }

    public sealed class ColonyPlacementRequest
    {
        public string WorldId, ColonyId, PlotId, OperationId, FacilityName;
        public string TemplateRelativePath, TemplateSha256, CertificationId, SurveyRevision, EscrowWitness;
        public string BodyName;
        public double Latitude, Longitude, HeadingDegrees;
        // Local x is right across the street; local z is forward along its heading.
        public double MinX, MaxX, MinZ, MaxZ, MaximumHeight;
        public double MaximumSlopeDegrees = 3, MaximumSupportGapMetres = 0.25;
        public double ClearanceMetres = 1, SettleSeconds = 10;
        public double MaximumSettleSpeed = 0.08, MaximumAngularSpeedDegrees = 0.05;
        // A certified transform from the template's editor axes into plot axes.
        public double TemplateRotationX, TemplateRotationY, TemplateRotationZ, TemplateRotationW = 1;
        public bool ExplicitSandboxUnlockOverride;
        public ColonyPlacementContent[] Contents = new ColonyPlacementContent[0];

        public string Validate()
        {
            foreach (string value in new[] { WorldId, ColonyId, PlotId, OperationId, CertificationId, SurveyRevision, EscrowWitness })
                if (!Token(value, 128)) return "Placement requires bounded stable identity, certification, survey and committed escrow witnesses";
            if (!Token(BodyName, 80) || !Token(FacilityName, 128)) return "Body and facility name are required";
            if (!Sha(TemplateSha256)) return "Template SHA-256 is required";
            if (!Token(TemplateRelativePath, 240) || TemplateRelativePath.StartsWith("/", StringComparison.Ordinal) ||
                TemplateRelativePath.StartsWith("\\", StringComparison.Ordinal) || TemplateRelativePath.Contains(":") ||
                TemplateRelativePath.Split('/', '\\').Any(x => x == ".." || x == "." || x.Length == 0) ||
                !TemplateRelativePath.EndsWith(".craft", StringComparison.OrdinalIgnoreCase))
                return "Template must name a craft inside the installed colony Templates directory";
            if (!Finite(Latitude) || Latitude < -89.9 || Latitude > 89.9 || !Finite(Longitude) || Longitude < -180 || Longitude > 180 ||
                !Finite(HeadingDegrees) || HeadingDegrees < 0 || HeadingDegrees >= 360) return "Geodetic placement is invalid or outside supported polar bounds";
            if (!Finite(MinX) || !Finite(MaxX) || !Finite(MinZ) || !Finite(MaxZ) || MinX >= MaxX || MinZ >= MaxZ ||
                MinX < -50 || MaxX > 50 || MinZ < -50 || MaxZ > 50 || !Finite(MaximumHeight) || MaximumHeight <= 0 || MaximumHeight > 50)
                return "Certified deployment envelope is outside the 100 by 100 by 50 metre bound";
            if (!Range(MaximumSlopeDegrees, 0, 15) || !Range(MaximumSupportGapMetres, 0, 2) || !Range(ClearanceMetres, 0.25, 10) ||
                !Range(SettleSeconds, 5, 120) || !Range(MaximumSettleSpeed, 0.001, 0.2) || !Range(MaximumAngularSpeedDegrees, 0.001, 0.1))
                return "Placement safety limits are outside certified bounds";
            double norm = TemplateRotationX * TemplateRotationX + TemplateRotationY * TemplateRotationY + TemplateRotationZ * TemplateRotationZ + TemplateRotationW * TemplateRotationW;
            if (!Finite(norm) || Math.Abs(norm - 1) > 0.00001) return "Certified template rotation is not a unit quaternion";
            if (Contents == null || Contents.Length > 512 || Contents.Any(x => x == null || x.CraftPartId == 0 || !Token(x.ResourceName, 80) || !Range(x.Amount, 0, 1000000000)))
                return "Explicit commissioned contents are missing or exceed supported bounds";
            if (Contents.Select(x => x.CraftPartId.ToString(CultureInfo.InvariantCulture) + ":" + x.ResourceName).Distinct(StringComparer.Ordinal).Count() != Contents.Length)
                return "Commissioned contents contain duplicate part/resource allocations";
            return null;
        }

        public ColonyPlacementRequest Copy()
        {
            var result = (ColonyPlacementRequest)MemberwiseClone();
            result.Contents = Contents == null ? null : Contents.Select(x => x == null ? null : new ColonyPlacementContent { CraftPartId = x.CraftPartId, ResourceName = x.ResourceName, Amount = x.Amount }).ToArray();
            return result;
        }

        public string Fingerprint()
        {
            var b = new StringBuilder();
            foreach (string x in new[] { WorldId, ColonyId, PlotId, OperationId, FacilityName, TemplateRelativePath.Replace('\\', '/'), TemplateSha256.ToLowerInvariant(), CertificationId, SurveyRevision, EscrowWitness, BodyName })
                b.Append(x.Length).Append(':').Append(x).Append('|');
            foreach (double x in new[] { Latitude, Longitude, HeadingDegrees, MinX, MaxX, MinZ, MaxZ, MaximumHeight, MaximumSlopeDegrees, MaximumSupportGapMetres, ClearanceMetres, SettleSeconds, MaximumSettleSpeed, MaximumAngularSpeedDegrees, TemplateRotationX, TemplateRotationY, TemplateRotationZ, TemplateRotationW })
                b.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            b.Append(ExplicitSandboxUnlockOverride ? "sandbox-unlock|" : "normal-unlock|");
            foreach (var x in Contents.OrderBy(x => x.CraftPartId).ThenBy(x => x.ResourceName, StringComparer.Ordinal))
                b.Append(x.CraftPartId).Append(':').Append(x.ResourceName.Length).Append(':').Append(x.ResourceName).Append(':').Append(x.Amount.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            return Hash(Encoding.UTF8.GetBytes(b.ToString()));
        }

        public static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        internal static bool Range(double x, double min, double max) { return Finite(x) && x >= min && x <= max; }
        internal static bool Token(string value, int maximum) { return !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl); }
        internal static bool Sha(string value) { return value != null && value.Length == 64 && value.All(x => (x >= '0' && x <= '9') || (x >= 'A' && x <= 'F') || (x >= 'a' && x <= 'f')); }
        internal static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2", CultureInfo.InvariantCulture))); }
    }

    public sealed class ColonyPlacementStatus
    {
        public string OperationId, ColonyId, PlotId, RequestFingerprint;
        public ColonyPlacementStage Stage;
        public string Reason, VesselId, FoundationId;
        public uint VesselPersistentId;
        public uint[] PartPersistentIds = new uint[0], FlightIds = new uint[0];
        public int PartCount;
        public double CreatedUt, AnchoredUt, ActualMaximumSlopeDegrees, ActualMaximumSupportGapMetres;
        public double SurveyTerrainHeight, MeasuredPositionErrorMetres, MeasuredAngleErrorDegrees;
        public string SurfaceCollisionWitness = "";
        public string BeforeWitness, AfterWitness;
        public bool AssemblyAttempted;
        public string RetryOperationId = "";
        public long SaveGeneration;
        // OnSave is a serialization boundary. Durable means an independently read-back KSP save,
        // which this adapter deliberately cannot infer from a serialization callback.
        public bool IncludedInSaveSerialization;
    }

    public static class ColonyPlacementRecovery
    {
        public const int MaximumOperations = 512, MaximumParts = 256, MaximumPayloadCharacters = 2 * 1024 * 1024;

        public static string Reconcile(ColonyPlacementStage stage, int markerVessels, bool completeMembership, bool witnessesMatch)
        {
            if (markerVessels > 1) return "Duplicate operation markers; plot quarantined for reconciliation";
            if (markerVessels == 1 && (!completeMembership || !witnessesMatch)) return "Placement marker or membership disagrees with its saved witnesses";
            if (markerVessels == 0 && stage != ColonyPlacementStage.AwaitingPlacement && stage != ColonyPlacementStage.Cancelled)
                return "Creation may have occurred but its marked building is absent; do not respawn or refund";
            if (markerVessels == 1 && stage == ColonyPlacementStage.Cancelled) return "A cancelled placement has a physical building; recovery must protect its occupants and stock";
            return null;
        }
    }
}
