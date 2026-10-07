using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private const string IsolatedConstructionRoot = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
        private string lastConstructionPumpOrder = "";

        // A configured off-world construction contract is a priced service model,
        // not a claim that Kerbals or constructors exist at the site. Publish one
        // finite paid pool to the earliest eligible order; never multiply it by
        // the number of queued buildings or award retroactive unobserved labor.
        private void PopulateConstructionEnvironment(ColonyEnvironment env)
        {
            env.ConstructionLaborByOrder.Clear();
            PopulateConstructionRecovery(env);
            // Call after the other environment providers: namespace text alone
            // cannot authorize candidate packages in a different installation.
            env.DevelopmentMode = env.DevelopmentMode && IsAuthorizedIsolatedConstruction();
            if (state == null) return;
            foreach (var colony in state.Colonies)
            {
                var contract = colony.Logistics;
                if (contract == null || contract.State != "operational" || !contract.FundsPaid || contract.ContractorWorkers <= 0 || contract.ActivationUt > env.Ut) continue;
                var eligible = state.Construction.Where(o => o.ColonyId == colony.Id && o.State == "building" && o.FundsPaid && o.LaborFunds > 0 &&
                    !o.Dependencies.Any(id => state.Construction.Single(d => d.Id == id).State != "operational"))
                    .OrderBy(o => o.AccountedUt).ThenBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault();
                if (eligible == null) continue;
                int workers = Math.Min(32, contract.ContractorWorkers);
                double from = Math.Max(contract.ActivationUt, eligible.AccountedUt);
                string terms = string.Join("|", state.WorldId, ContextKey, colony.Id, contract.OperationId, contract.PolicyHash,
                    contract.Funds.ToString(CultureInfo.InvariantCulture), contract.ActivationUt.ToString("R", CultureInfo.InvariantCulture), eligible.Id,
                    eligible.LaborFunds.ToString(CultureInfo.InvariantCulture), workers.ToString(CultureInfo.InvariantCulture), from.ToString("R", CultureInfo.InvariantCulture), env.Ut.ToString("R", CultureInfo.InvariantCulture));
                env.ConstructionLaborByOrder[eligible.Id] = new ColonyConstructionLaborWitness
                {
                    ProviderId = "Paid contract construction service", PoolId = contract.OperationId, ContextKey = ContextKey,
                    EvidenceHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(terms)), Workers = workers, PoolCapacity = workers,
                    ValidFromUt = from, ValidThroughUt = env.Ut, CostIncludedInPaidEscrow = true
                };
            }
        }

        // Called by the existing selected-save main-thread loop. All actual craft
        // creation remains in the same product ColonyPlacementScenario.Enqueue
        // adapter. This method never calls assembly or writes a save fixture.
        private void PumpConstruction()
        {
            if (!Ready || mutating || HighLogic.CurrentGame == null || !ReferenceEquals(selectedGame, HighLogic.CurrentGame)) return;
            var scenario = ColonyPlacementScenario.Instance;
            if (scenario == null || !scenario.Ready) return;
            if (!string.IsNullOrEmpty(scenario.WorldId) && scenario.WorldId != state.WorldId) throw new InvalidOperationException("Colony construction and physical placement scenarios belong to different saved worlds.");
            if (PumpConstructionRetry(scenario)) return;
            string candidateId = ColonyEngine.NextConstructionPumpOrderId(state, lastConstructionPumpOrder);
            if (candidateId.Length == 0) return;
            lastConstructionPumpOrder = candidateId;
            var candidate = state.Construction.Single(o => o.Id == candidateId);
            var env = GetConstructionEnvironment();
            var template = env.Templates.SingleOrDefault(t => t.Id == candidate.TemplateId && t.Hash == candidate.TemplateHash);
            if (template == null) { ConstructionHold(candidate.Id, "Paid building package changed or no longer qualifies; no replacement or refund was attempted."); return; }
            var colony = state.Colonies.Single(c => c.Id == candidate.ColonyId);
            bool development = !template.RuntimeCertified;
            if (development && !(colony.Charter.Sandbox && env.DevelopmentMode && IsAuthorizedIsolatedConstruction()))
            { ConstructionHold(candidate.Id, "Candidate building requires the exact isolated development installation and explicit sandbox charter."); return; }
            if (state.Effects.Any(e => (e.State == "held" || e.State == "applying") && e.Id != candidate.Placement.EffectId && !ColonyEngine.IsConstructionActivationEffect(state, candidate.Id, e.Id))) return;
            var game = HighLogic.CurrentGame; string epoch = loadEpoch;
            bool newIntent = false;
            mutating = true;
            try
            {
                if (candidate.Placement.OperationId.Length == 0)
                {
                    var plot = colony.Plots.Single(p => p.Id == candidate.PlotId);
                    var claims = state.Colonies.Where(c => c.Site.Body == colony.Site.Body).SelectMany(c => c.Plots).ToArray();
                    var survey = ColonySiteSurvey.Survey(template, colony.Site.Body, plot.Latitude, plot.Longitude, plot.Heading, claims, ContextKey, plot.Id);
                    if (!survey.Clear)
                    {
                        Accept(ColonyEngine.AwaitConstructionEvidence(state, candidate.Id, Bound("Awaiting loaded placement survey: " + survey.Reason, 512)));
                        return;
                    }
                    Accept(ColonyEngine.RefreshConstructionSurvey(state, candidate.Id, env, survey.Plot));
                    colony = state.Colonies.Single(c => c.Id == candidate.ColonyId);
                    candidate = state.Construction.Single(o => o.Id == candidate.Id);
                    plot = colony.Plots.Single(p => p.Id == candidate.PlotId);
                    string operation = Guid.NewGuid().ToString("D");
                    var request = ConstructionRequest(state, candidate, colony, plot, template, operation, development);
                    string error = request.Validate(); if (error != null) throw new InvalidOperationException(error);
                    string payload = ColonyPlacementCodec.WriteRequest(request).ToString();
                    var intent = new ColonyConstructionPlacementIntent
                    {
                        OperationId = operation, EffectId = Guid.NewGuid().ToString("D"), Phase = "intent", ContextKey = ContextKey,
                        RequestPayload = payload, PayloadHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(payload)), RequestFingerprint = request.Fingerprint(),
                        EscrowWitness = request.EscrowWitness, BeforeWitness = ConstructionWorldWitness(state, request),
                        Reason = "Paid immutable construction request prepared for the product placement queue."
                    };
                    // Serialization and the authoritative selected-save swap finish
                    // before crossing the external queue boundary. OnSave captures
                    // intent and queue together; this is not a disk-fsync assertion.
                    Accept(ColonyEngine.PrepareConstructionPlacement(state, candidate.Id, env, intent));
                    newIntent = true;
                }
                candidate = state.Construction.Single(o => o.Id == candidate.Id);
                var saved = candidate.Placement;
                if (ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(saved.RequestPayload)) != saved.PayloadHash) throw new InvalidOperationException("Saved construction request checksum failed.");
                var parsed = ConfigNode.Parse(saved.RequestPayload).GetNodes("REQUEST");
                if (parsed.Length != 1) throw new InvalidOperationException("Saved construction request node is ambiguous.");
                var committed = ColonyPlacementCodec.ReadRequest(parsed[0]);
                if (committed.Validate() != null || committed.Fingerprint() != saved.RequestFingerprint || committed.OperationId != saved.OperationId || committed.WorldId != state.WorldId || committed.ColonyId != candidate.ColonyId || committed.PlotId != candidate.PlotId || committed.TemplateSha256 != template.CraftSha256 || committed.EscrowWitness != ColonyEngine.ConstructionEscrowWitness(state, candidate.Id))
                    throw new InvalidOperationException("Saved physical request no longer binds this exact paid package/order/world.");
                var status = scenario.GetStatus(saved.OperationId);
                if (status == null)
                {
                    if (!newIntent) throw new InvalidOperationException("Persisted placement intent has no matching queue receipt; an interrupted spawn is ambiguous. Reconcile selected-save witnesses before any retry/refund.");
                    string reason;
                    if (!scenario.Enqueue(committed, out status, out reason)) throw new InvalidOperationException("Product placement queue rejected paid intent: " + reason);
                }
                ColonyPlacementRecord record;
                if (!scenario.Records.TryGetValue(saved.OperationId, out record) || record.Request.Fingerprint() != saved.RequestFingerprint || status.RequestFingerprint != saved.RequestFingerprint)
                    throw new InvalidOperationException("Existing placement queue receipt has different immutable terms; spawning is disabled.");
                if (!ReferenceEquals(game, HighLogic.CurrentGame) || epoch != loadEpoch) throw new InvalidOperationException("Selected world changed across construction queue boundary.");
                if (status.Stage == ColonyPlacementStage.Anchored && !UniqueConstructionVessel(status.VesselId).loaded)
                {
                    Accept(ColonyEngine.AwaitConstructionEvidence(state, candidate.Id, "Awaiting loaded anchored building for actual package/utility commissioning readback; no replacement was attempted."));
                    return;
                }
                var observation = ReadConstructionPlacement(state, candidate, template, committed, status, development);
                if (saved.Phase != observation.Phase || saved.AfterWitness != observation.AfterWitness || candidate.State == "held" && observation.Phase != "RecoveryHold")
                    Accept(ColonyEngine.ObserveConstructionPlacement(state, candidate.Id, template, observation));
                candidate = state.Construction.Single(o => o.Id == candidate.Id);
                if (candidate.State == "commissioning" && status.Stage == ColonyPlacementStage.Anchored)
                {
                    var vessel = UniqueConstructionVessel(status.VesselId);
                    var facility = state.Colonies.Single(c => c.Id == candidate.ColonyId).Facilities.Single(f => f.Id == candidate.FacilityId);
                    if (!EnsureConstructionActivation(vessel, template, facility, candidate)) return;
                    var qualification = QualifyPlacedFacility(vessel, template);
                    qualification.PlacementStable &= observation.Anchored;
                    int homes = vessel.parts.Where(p => observation.QualifiedHomePartIds.Contains(p.persistentId)).Sum(p => p.CrewCapacity);
                    if (facility.Qualification.EvidenceHash != qualification.EvidenceHash || facility.Qualification.Context != qualification.Context || facility.LastReason.StartsWith("Actual anchored package", StringComparison.Ordinal))
                        Accept(ColonyEngine.CommissionConstruction(state, candidate.Id, template, qualification, homes));
                }
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(game, HighLogic.CurrentGame) && epoch == loadEpoch)
                    ConstructionHold(candidate.Id, Bound(ex.Message, 512));
                else HoldReason = "Selected save changed during construction; reload and reconcile its own saved witnesses.";
                Debug.LogError("[ExpanseColony] Construction " + candidate.Id + " held: " + ex);
            }
            finally { mutating = false; }
        }

        private void ConstructionHold(string orderId, string reason)
        {
            var order = state.Construction.Single(o => o.Id == orderId);
            if (order.State != "held" || order.Reason != reason) Accept(ColonyEngine.HoldConstructionPlacement(state, orderId, reason));
        }

        private static ColonyPlacementRequest ConstructionRequest(ColonyState state, ConstructionOrder order, ColonyRecord colony, ColonyPlot plot, ColonyTemplate template, string operation, bool development)
        {
            return new ColonyPlacementRequest
            {
                WorldId = state.WorldId, ColonyId = colony.Id, PlotId = plot.Id, OperationId = operation, FacilityName = template.Name,
                TemplateRelativePath = template.CraftRelativePath, TemplateSha256 = template.CraftSha256, CertificationId = template.CertificationId,
                SurveyRevision = plot.SurveyHash, EscrowWitness = ColonyEngine.ConstructionEscrowWitness(state, order.Id), BodyName = colony.Site.Body,
                Latitude = plot.Latitude, Longitude = plot.Longitude, HeadingDegrees = plot.Heading,
                MinX = template.MinX, MaxX = template.MaxX, MinZ = template.MinZ, MaxZ = template.MaxZ, MaximumHeight = template.MaximumHeight,
                MaximumSlopeDegrees = template.MaximumSlopeDegrees, MaximumSupportGapMetres = template.MaximumSupportGapMetres, ClearanceMetres = template.ClearanceMetres,
                TemplateRotationX = template.TemplateRotationX, TemplateRotationY = template.TemplateRotationY, TemplateRotationZ = template.TemplateRotationZ, TemplateRotationW = template.TemplateRotationW,
                ExplicitSandboxUnlockOverride = development,
                Contents = template.StartupContents.Select(c => new ColonyPlacementContent { CraftPartId = c.CraftPartId, ResourceName = c.ResourceName, Amount = c.Amount / (double)ColonyLimits.Units }).ToArray()
            };
        }

        private static ColonyConstructionPlacementObservation ReadConstructionPlacement(ColonyState state, ConstructionOrder order, ColonyTemplate template, ColonyPlacementRequest request, ColonyPlacementStatus status, bool development)
        {
            var observation = new ColonyConstructionPlacementObservation
            {
                WorldId = state.WorldId, OperationId = status.OperationId, RequestFingerprint = status.RequestFingerprint, Phase = status.Stage.ToString(),
                Reason = Bound(status.Reason, 512), AssemblyAttempted = status.AssemblyAttempted, ObservedUt = Planetarium.GetUniversalTime(),
                VesselId = status.VesselId, FoundationId = status.FoundationId, DevelopmentOnly = development,
                AfterWitness = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(ColonyPlacementCodec.WriteStatus(status).ToString()))
            };
            if (status.ColonyId != order.ColonyId || status.PlotId != order.PlotId) throw new InvalidOperationException("Placement receipt belongs to another colony plot.");
            if (status.Stage != ColonyPlacementStage.Anchored) return observation;
            var vessel = UniqueConstructionVessel(status.VesselId);
            if (!vessel.loaded || vessel.parts == null || vessel.mainBody == null || vessel.mainBody.bodyName != request.BodyName || !vessel.Landed || vessel.vesselType != VesselType.Base || !vessel.packed || vessel.persistentId != status.VesselPersistentId)
                throw new InvalidOperationException("Final building is not the exact loaded anchored landed Base package.");
            if (vessel.parts.Count != template.ExpectedPartCount || !vessel.parts.Select(p => p.persistentId).OrderBy(id => id).SequenceEqual(status.PartPersistentIds.OrderBy(id => id)) || !vessel.parts.Select(p => p.flightID).OrderBy(id => id).SequenceEqual(status.FlightIds.OrderBy(id => id)))
                throw new InvalidOperationException("Anchored actual part membership differs from product placement witness.");
            string foundation; double positionError, angleError;
            if (!ColonyPlacementFoundations.IsHeld(vessel) || !ColonyPlacementFoundations.ReadWitness(vessel, out foundation, out positionError, out angleError) || foundation != status.FoundationId || positionError > .01 || angleError > .01)
                throw new InvalidOperationException("Actual Foundation hold/pose no longer agrees with the anchored placement witness.");
            var expectedParts = ReadConstructionCraft(template);
            foreach (var part in vessel.parts)
            {
                if (part == null || part.partInfo == null) throw new InvalidOperationException("Actual building has missing installed part identity.");
                var markers = part.Modules.OfType<ColonyPlacementMarker>().ToArray();
                if (markers.Length != 1) throw new InvalidOperationException("Actual building has missing/duplicate marker.");
                var marker = markers[0]; string expectedName;
                if (marker.operationId != request.OperationId || marker.worldId != state.WorldId || marker.colonyId != order.ColonyId || marker.plotId != order.PlotId || marker.requestFingerprint != request.Fingerprint() || marker.templateSha256 != template.CraftSha256 || !expectedParts.TryGetValue(marker.craftPartId, out expectedName) || part.partInfo.name != expectedName)
                    throw new InvalidOperationException("Actual package part identity/marker differs from the paid reviewed craft.");
                observation.CraftToPersistentIds.Add(marker.craftPartId, part.persistentId);
                if (template.HomeCraftPartIds.Contains(marker.craftPartId) && QualifyHomePart(vessel, part, template)) observation.QualifiedHomePartIds.Add(part.persistentId);
            }
            if (vessel.parts.Any(p => p.protoModuleCrew.Count > 0) && order.FacilityId.Length == 0) throw new InvalidOperationException("Uncommissioned placement unexpectedly contains crew.");
            observation.Anchored = true; return observation;
        }

        private static Vessel UniqueConstructionVessel(string id)
        {
            if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 4096) throw new InvalidOperationException("Current world vessel inventory is unavailable or exceeds bounds.");
            var matches = FlightGlobals.Vessels.Where(v => v != null && v.id.ToString("D") == id).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Exact placed vessel is missing or duplicated; no automatic replacement is allowed.");
            return matches[0];
        }

        private static Dictionary<uint, string> ReadConstructionCraft(ColonyTemplate template)
        {
            string root = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "ExpanseWorldBridge", "Templates")).TrimEnd(Path.DirectorySeparatorChar);
            string relative = template.CraftRelativePath;
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(p => p.Length == 0 || p == "." || p == "..")) throw new InvalidOperationException("Construction craft path leaves trusted package root.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Construction craft path leaves trusted package root.");
            for (string ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Construction craft reparse path refused.");
            var info = new FileInfo(path); if (!info.Exists || info.Length <= 0 || info.Length > 1048576) throw new InvalidOperationException("Trusted construction craft is missing or oversized.");
            byte[] bytes = File.ReadAllBytes(path); if (bytes.Length > 1048576 || ColonyPlacementRequest.Hash(bytes) != template.CraftSha256) throw new InvalidOperationException("Reviewed construction craft bytes changed.");
            var parts = ConfigNode.Parse(new UTF8Encoding(false, true).GetString(bytes)).GetNodes("PART");
            if (parts.Length != template.ExpectedPartCount) throw new InvalidOperationException("Reviewed craft part count changed.");
            var result = new Dictionary<uint, string>();
            foreach (var part in parts)
            {
                string key = part.GetValue("part"); int separator = key.IndexOf('_');
                uint craft = uint.Parse(key.Substring(separator + 1), CultureInfo.InvariantCulture); result.Add(craft, key.Substring(0, separator));
            }
            return result;
        }

        private static string ConstructionWorldWitness(ColonyState state, ColonyPlacementRequest request)
        {
            if (FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 4096) throw new InvalidOperationException("Bounded current vessel inventory unavailable for placement intent.");
            string vessels = string.Join(";", FlightGlobals.Vessels.Where(v => v != null).OrderBy(v => v.id).Select(v => v.id.ToString("D") + ":" + v.persistentId));
            return "world=" + state.WorldId + "; request=" + request.Fingerprint() + "; escrow=" + request.EscrowWitness + "; vesselInventory=" + ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(vessels));
        }

        private static bool IsAuthorizedIsolatedConstruction()
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(root, IsolatedConstructionRoot, StringComparison.OrdinalIgnoreCase) || HighLogic.CurrentGame == null || !HighLogic.SaveFolder.StartsWith("ColonyBuild-", StringComparison.Ordinal)) return false;
            string[] args = Environment.GetCommandLineArgs(); string user = Environment.UserName;
            return new[] { "-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev." + user + ".", "-expanseEffectsPipe=ExpanseFoundations.Effects.dev." + user + ".", "-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev." + user + "." }
                .All(prefix => args.Count(arg => arg.StartsWith(prefix, StringComparison.Ordinal) && arg.Length > prefix.Length) == 1);
        }
    }
}
