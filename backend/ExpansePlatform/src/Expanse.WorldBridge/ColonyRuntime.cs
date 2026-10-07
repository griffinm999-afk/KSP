using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // Selected KSP save is the sole colony authority. The desktop app submits
    // commands; it cannot restore a host-side ledger over a quickloaded game.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public sealed partial class ColonyRuntime : ScenarioModule
    {
        public static ColonyRuntime Current { get; private set; }
        public string ContextKey { get { return (state == null ? "uninitialized" : state.WorldId) + "/" + loadEpoch; } }
        public string HoldReason { get; private set; }
        public bool Ready { get { return state != null && string.IsNullOrEmpty(HoldReason); } }
        public bool HasUnresolvedExternalEffects { get { return mutating || !string.IsNullOrEmpty(HoldReason) || state != null && state.Effects.Any(x => x.State == "held" || x.State == "applying"); } }
        private ColonyState state;
        private ConfigNode preserved;
        private string loadEpoch = Guid.NewGuid().ToString("D");
        private float nextTick, nextObservation;
        private object selectedGame;
        private bool mutating;
        private readonly List<ColonyFacility> adoptable = new List<ColonyFacility>();
        private readonly Dictionary<string, ColonySite> sites = new Dictionary<string, ColonySite>();
        private readonly List<ColonyTemplate> templates = new List<ColonyTemplate>();
        private bool templatesInitialized;
        private byte[] acceptedBytes;
        private string acceptedHash;

        public override void OnAwake() { base.OnAwake(); Current = this; }
        public void OnDestroy() { InvalidatePureTick(); if (Current == this) Current = null; StopManagementEndpoint(); StopUtilityObservers(); StopReactorObservers(); StopProductionObservers(); }

        public override void OnLoad(ConfigNode node)
        {
            InvalidatePureTick();
            base.OnLoad(node);
            LoadSettlements(node);
            state = null; acceptedBytes = null; acceptedHash = null; HoldReason = null;
            preserved = node.CreateCopy(); selectedGame = null; loadEpoch = Guid.NewGuid().ToString("D");
            adoptable.Clear(); sites.Clear(); templates.Clear(); templatesInitialized = false; nextTick = 0; nextObservation = 0;
            economyPolicies = null;
            ResetPerformanceSamples();
            var records = node.GetNodes("COLONY_STATE");
            if (records.Length == 0) return;
            try
            {
                if (records.Length != 1) throw new InvalidDataException("Multiple colony state nodes; original data preserved.");
                string encoded = records[0].GetValue("payload");
                if (string.IsNullOrEmpty(encoded) || encoded.Length > (ColonyLimits.MaxBytes + 2) / 3 * 4) throw new InvalidDataException("Colony payload has invalid size.");
                byte[] bytes = ColonyStateCodec.DecodeSaveValue(encoded);
                string hash = ColonyStateCodec.Hash(bytes);
                if (!string.Equals(hash, records[0].GetValue("sha256"), StringComparison.Ordinal)) throw new InvalidDataException("Colony save checksum failed; original data preserved.");
                state = ColonyStateCodec.Deserialize(bytes); acceptedBytes = bytes; acceptedHash = hash;
                // A persisted mid-callback effect is not safe to repeat. Its witness
                // is retained; explicit provider reconciliation must resolve it.
                foreach (var effect in state.Effects.Where(x => x.State == "applying").ToArray())
                    Accept(ColonyEngine.HoldEffect(state, effect.Id, "Save captured an unresolved external effect; reconcile its stored before/after witnesses."));
            }
            catch (Exception ex)
            {
                state = null; HoldReason = "Colony state unavailable: " + Bound(ex.Message, 360);
                Debug.LogError("[ExpanseColony] " + HoldReason);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            // Save only accepted authority. Never wait for, or persist, a worker.
            InvalidatePureTick();
            base.OnSave(node);
            if (state == null)
            {
                SaveSettlements(node);
                // Never replace a future/corrupt schema with an empty new colony.
                if (preserved != null) foreach (ConfigNode child in preserved.GetNodes("COLONY_STATE")) node.AddNode(child.CreateCopy());
                return;
            }
            // A management read can discover unsafe loaded hardware just before
            // a scene save. Do not persist its older background qualification.
            RefreshLoadedReactorContinuations(true);
            SaveSettlements(node);
            var saved = node.AddNode("COLONY_STATE");
            saved.AddValue("schema", state.SchemaVersion); saved.AddValue("sha256", acceptedHash);
            saved.AddValue("payload", ColonyStateCodec.EncodeSaveValue(acceptedBytes));
        }

        public ColonyState GetStateCopy() { return state == null ? null : ColonyStateCodec.Copy(state); }

        public ColonyEnvironment GetEnvironment()
        {
            long started = performance.Start();
            try { return BuildEnvironment(); }
            finally { performance.End(ColonyPerformanceChannel.Environment, started); }
        }

        private void EnsureTemplateCatalogInitialized()
        {
            if (!templatesInitialized && PartLoader.LoadedPartsList != null && PartLoader.LoadedPartsList.Count > 0)
            {
                templatesInitialized = true;
                var catalog = ColonyTemplateCatalog.LoadInstalled();
                templates.AddRange(catalog.Templates);
                Debug.Log("[ExpanseColony] Static template catalog accepted=" + templates.Count + " rejected=" + catalog.Issues.Count + " package=" + catalog.PackageHash);
                foreach (var issue in catalog.Issues) Debug.LogWarning("[ExpanseColony] Template " + issue.TemplateId + ": " + issue.Reason);
            }
        }

        // Construction consumes paid package/authorization and fresh base time/funds.
        // Native commissioning still reads its own actual hardware below the pump.
        // Do not reuse a pre-effect tick observation or a display snapshot here.
        private ColonyEnvironment GetConstructionEnvironment()
        {
            EnsureTemplateCatalogInitialized();
            var game = HighLogic.CurrentGame;
            return new ColonyEnvironment { WorldId = state == null ? "" : state.WorldId, ContextKey = ContextKey,
                Ut = Planetarium.GetUniversalTime(), AvailableFunds = FundsAvailable(), Templates = templates.ToList(),
                DevelopmentMode = game != null && game.CrewRoster != null && state != null &&
                    IsAuthorizedDevelopmentContext() && IsAuthorizedIsolatedConstruction() };
        }

        private ColonyEnvironment BuildEnvironment()
        {
            EnsureTemplateCatalogInitialized();
            var env = new ColonyEnvironment { WorldId = state == null ? "" : state.WorldId, ContextKey = ContextKey,
                Ut = Planetarium.GetUniversalTime(), AvailableFunds = FundsAvailable(), Templates = templates.ToList(),
                AdoptableFacilities = adoptable.ToList(), FacilitySites = new Dictionary<string, ColonySite>(sites) };
            if (FlightGlobals.Bodies != null)
                foreach (var body in FlightGlobals.Bodies) if (body != null && body.Radius > 0) env.BodyRadiiMeters[body.bodyName] = body.Radius;
            if (ResearchAndDevelopment.Instance != null)
                foreach (var tech in templates.SelectMany(x => x.RequiredTech).Distinct(StringComparer.Ordinal))
                    if (ResearchAndDevelopment.GetTechnologyState(tech) == RDTech.State.Available) env.UnlockedTech.Add(tech);
            // Construction labor is published only by a qualified construction
            // provider. No automatic crew/seat inference or gratis builders.
            var environmentConfigs = new ColonyEnvironmentConfigObservation();
            long observed = performance.Start();
            try { PopulatePlanningEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentPlanning, observed); }
            observed = performance.Start();
            try { PopulateWolfEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentWolf, observed); }
            observed = performance.Start();
            try { PopulateUtilityEnvironment(env, environmentConfigs); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentUtilities, observed); }
            observed = performance.Start();
            try { PopulatePeopleEnvironment(env, environmentConfigs); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentPeople, observed); }
            observed = performance.Start();
            try { PopulateSupportEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentSupport, observed); }
            observed = performance.Start();
            try { PopulateServiceEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentServices, observed); }
            observed = performance.Start();
            try { PopulateConstructionEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentConstruction, observed); }
            observed = performance.Start();
            try { PopulatePlanningPolicyEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentPlanningPolicy, observed); }
            observed = performance.Start();
            try { PopulatePhysicalProcurementEnvironment(env, environmentConfigs); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentPhysicalProcurement, observed); }
            observed = performance.Start();
            try { PopulateProductionEnvironment(env); }
            finally { performance.End(ColonyPerformanceChannel.EnvironmentProduction, observed); }
            return env;
        }

        public ColonyResult Submit(ColonyCommand command)
        {
            InvalidatePureTick();
            if (!Ready || mutating || HighLogic.CurrentGame == null || !ReferenceEquals(selectedGame, HighLogic.CurrentGame))
                return new ColonyResult { State = GetStateCopy(), Reason = HoldReason ?? "Colony world is not ready for changes." };
            if (WorldBridgeAddon.Current == null || WorldBridgeAddon.Current.LegacyAuthorityWriteHeld)
                return new ColonyResult { State = GetStateCopy(), Outcome = "held", Reason = "Delivery accounting is unavailable or has an unresolved operation; colony changes are waiting for reconciliation." };
            // A display cache is never the authority for adopting a vessel. Rebuild
            // witnesses from the selected world immediately before the transition.
            if (command != null && (command.Kind == "foundColony" || command.Kind == "adoptFacility" || command.Kind == "qualifyAdoptedHabitat"))
            {
                adoptable.Clear(); sites.Clear();
                if (FlightGlobals.Vessels != null)
                    foreach (var vessel in FlightGlobals.Vessels) ObserveFacility(vessel);
            }
            var environment = GetEnvironment();
            PreparePlanningCommand(command, environment);
            PreparePlanningPolicyCommand(command, environment);
            var result = ColonyEngine.Execute(state, command, environment);
            if (result.Outcome == "accepted") Accept(result.State);
            // Public callers must never receive the accepted mutable state graph.
            // This also covers duplicate/rejected transitions which return prior.
            result.State = result.State == null ? null : ColonyStateCodec.Copy(result.State);
            return result;
        }

        private void Update()
        {
            long started = performance.Start();
            try { UpdateCore(); }
            finally
            {
                performance.End(ColonyPerformanceChannel.FrameCallback, started);
                performance.RecordFrameInterval(Time.unscaledDeltaTime);
            }
        }

        private void UpdateCore()
        {
            long managementStarted = performance.Start();
            try { PumpManagementEndpoint(); }
            finally { performance.End(ColonyPerformanceChannel.ManagementEndpoint, managementStarted); }
            if (HighLogic.CurrentGame == null) return;
            if (selectedGame == null) selectedGame = HighLogic.CurrentGame;
            if (!ReferenceEquals(selectedGame, HighLogic.CurrentGame)) return;
            if (state == null && string.IsNullOrEmpty(HoldReason))
            {
                var recovery = RecoveryCapsuleModule.Instance;
                if (recovery == null || !recovery.HasAcceptedState || recovery.IsCorrupt) return;
                try { Accept(ColonyEngine.Create(recovery.WorldId, Planetarium.GetUniversalTime())); }
                catch (Exception ex)
                {
                    HoldReason = "Colony initialization held: " + Bound(ex.Message, 360);
                    Debug.LogError("[ExpanseColony] " + HoldReason + " " + ex);
                    return;
                }
            }
            if (!Ready) return;
            try { EnsureSettlements(); FlushSettlements(); } catch (Exception) { /* Sources can still be loading; reads and financial preflight report unavailability. */ }
            if (RecoveryCapsuleModule.Instance != null && RecoveryCapsuleModule.Instance.HasAcceptedState && RecoveryCapsuleModule.Instance.WorldId != state.WorldId)
            { HoldReason = "Colony and delivery records belong to different worlds; restore a consistent save."; return; }
            if (pendingPureTick != null)
            {
                if (FlightDriver.Pause || Time.timeScale <= 0) InvalidatePureTick();
                if (!pendingPureTick.IsCompleted) return;
                CompletePureTick();
                return;
            }
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + .5f;
            long tickStarted = performance.Start();
            bool activeTick = false;
            try
            {
                if (Time.realtimeSinceStartup >= nextObservation)
                {
                    nextObservation = Time.realtimeSinceStartup + 3;
                    long discoveryStarted = performance.Start();
                    try { ObserveAdoptableFacilities(); }
                    finally { performance.End(ColonyPerformanceChannel.Discovery, discoveryStarted); }
                }
                if (FlightDriver.Pause || Time.timeScale <= 0) return;
                activeTick = true;
                var env = GetEnvironment();
                if (env.Ut < state.SimulatedUt) { HoldReason = "Game time predates colony state; waiting for the selected save to reload."; return; }
                RefreshLoadedReactorContinuations();
                try { pendingPureTick = new ColonyPureTickWork(state, selectedGame, state.WorldId, ContextKey, loadEpoch, acceptedBytes, env); }
                catch (Exception ex) { Debug.LogWarning("[ExpanseColony] Pure tick snapshot deferred: " + Bound(ex.Message, 360)); }
            }
            catch (Exception ex) { HoldReason = "Colony processing held: " + Bound(ex.Message, 360); Debug.LogError("[ExpanseColony] " + HoldReason); }
            finally { if (activeTick) performance.End(ColonyPerformanceChannel.ActiveTick, tickStarted); }
        }

        private void Accept(ColonyState next) => Accept(next, null);

        // Only the immediate Advance call supplies prepared bytes; the graph
        // cannot change between its validation and this authoritative swap.
        private void Accept(ColonyState next, byte[] preparedBytes)
        {
            InvalidatePureTick();
            long started = performance.Start();
            try
            {
                // Prepare all allocating serialization work before the authoritative swap.
                byte[] bytes = preparedBytes ?? ColonyStateCodec.Serialize(next); string hash = ColonyStateCodec.Hash(bytes);
                state = next; acceptedBytes = bytes; acceptedHash = hash;
            }
            finally { performance.End(ColonyPerformanceChannel.AcceptanceSerialization, started); }
        }

        private void ApplyOneFundsEffect()
        {
            if (mutating || !Ready || HighLogic.CurrentGame.Mode != Game.Modes.CAREER || Funding.Instance == null) return;
            var pending = state.Effects.FirstOrDefault(x => x.State == "prepared" &&
                (x.Kind == "constructionEscrow" || x.Kind == "constructionRefund" || x.Kind == "importPurchase" || x.Kind == "passengerFare" || x.Kind == "logisticsSetup" || x.Kind == "wolfPurchase"));
            if (pending == null || state.Effects.Any(x => x.State == "held" || x.State == "applying")) return;
            var funding = Funding.Instance; var game = HighLogic.CurrentGame; string epoch = loadEpoch;
            double before = funding.Funds, after = before + pending.FundsDelta;
            if (!Finite(before) || !Finite(after) || after < 0 || after - before != pending.FundsDelta)
            { Accept(ColonyEngine.HoldEffect(state, pending.Id, "Funds changed or cannot represent this exact payment.")); return; }
            long floor = state.Colonies.Count == 0 ? 0 : state.Colonies.Max(x => x.Charter.CashFloor);
            long otherCommitted = checked(state.Effects.Where(x => x.Id != pending.Id && x.State == "prepared" && x.FundsDelta < 0).Sum(x => -x.FundsDelta) + ColonyEngine.PlanningCommittedFunds(state));
            if (pending.FundsDelta < 0 && after - otherCommitted < floor)
            { Accept(ColonyEngine.HoldEffect(state, pending.Id, "Player funds changed; cash floor and other reservations prevent payment.")); return; }
            string beforeWitness = "Career funds=" + before.ToString("R", CultureInfo.InvariantCulture) + "; epoch=" + epoch;
            var applying = ColonyEngine.MarkEffectApplying(state, pending.Id, "KSP.Funding", beforeWitness);
            string afterWitness = "Career funds=" + after.ToString("R", CultureInfo.InvariantCulture) + "; delta=" + pending.FundsDelta.ToString(CultureInfo.InvariantCulture) + "; epoch=" + epoch;
            var complete = ColonyEngine.CompleteFundsEffect(applying, pending.Id, checked((long)Math.Floor(before)), checked((long)Math.Floor(after)), Planetarium.GetUniversalTime(), afterWitness);
            // Pre-serialize both outcomes before any mod callback can run.
            byte[] completeBytes = ColonyStateCodec.Serialize(complete); string completeHash = ColonyStateCodec.Hash(completeBytes);
            var preparedSettlement = PrepareColonySettlement(complete, pending.Id);
            Accept(applying); mutating = true;
            try
            {
                if (pending.FundsDelta != 0) funding.SetFunds(after, TransactionReasons.None);
                if (!ReferenceEquals(game, HighLogic.CurrentGame) || !ReferenceEquals(funding, Funding.Instance) || epoch != loadEpoch || state != applying)
                    throw new InvalidOperationException("World changed during funds callback; selected save witnesses must be reconciled.");
                if (funding.Funds != after) throw new InvalidOperationException("Funds readback differs from intended amount; automatic retry is disabled.");
                state = complete; acceptedBytes = completeBytes; acceptedHash = completeHash;
                CommitSettlement(preparedSettlement);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(game, HighLogic.CurrentGame) && epoch == loadEpoch && state == applying)
                    Accept(ColonyEngine.HoldEffect(state, pending.Id, Bound(ex.Message, 360)));
                else HoldReason = "Game context changed during payment; reload/reconcile the selected save.";
            }
            finally { mutating = false; }
        }

        private void ObserveAdoptableFacilities()
        {
            // Discovery can be partial. It never removes registered assets. Scan a
            // bounded number of vessels with a cursor rather than prioritize a name.
            ObserveFacilitySlice();
        }
        private int observationCursor;
        private void ObserveFacilitySlice()
        {
            var vessels = FlightGlobals.Vessels;
            if (vessels == null || vessels.Count == 0) return;
            int count = Math.Min(16, vessels.Count);
            for (int i = 0; i < count; i++)
            {
                if (observationCursor >= vessels.Count) observationCursor = 0;
                ObserveFacility(vessels[observationCursor++]);
            }
        }
        private void ObserveFacility(Vessel vessel)
        {
                if (vessel == null || vessel.mainBody == null || !vessel.Landed || vessel.isEVA || vessel.vesselType == VesselType.Debris || vessel.vesselType == VesselType.Flag || vessel.vesselType == VesselType.SpaceObject) return;
                string id = vessel.id.ToString("D");
                var observation = ColonyAdoptionEligibility.Observe(vessel);
                if (observation.Kind == ColonyAdoptionKind.NaturalLode)
                {
                    // Discovery is disposable; the selected save's registered
                    // facilities are independent and must never be deleted here.
                    adoptable.RemoveAll(x => x.Id == id);
                    bool registered = state != null && state.Colonies.Any(c => c.Facilities.Any(f => f.VesselId == id));
                    if (!registered) sites.Remove(id);
                    return;
                }
                // Incomplete current telemetry cannot reclassify a cached asset.
                // Fresh adoption commands already rebuild discovery from scratch.
                if (observation.Kind == ColonyAdoptionKind.Unknown) return;
                var observedParts = observation.PartIds;
                var row = adoptable.Find(x => x.Id == id);
                if (row == null) { if (adoptable.Count >= ColonyLimits.Facilities) return; row = new ColonyFacility { Id = id, VesselId = id }; adoptable.Add(row); }
                row.Name = Bound(vessel.vesselName, 160);
                row.PartIds = observedParts;
                row.Qualification.Context = vessel.loaded ? (vessel.packed ? "loaded-packed" : "loaded-unpacked") : "unloaded";
                row.Qualification.ObservedUt = Planetarium.GetUniversalTime();
                sites[id] = new ColonySite { Body = vessel.mainBody.bodyName, Biome = ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude),
                    Latitude = vessel.latitude, Longitude = ((vessel.longitude + 180) % 360 + 360) % 360 - 180 };
        }

        private static long FundsAvailable()
        {
            if (Funding.Instance == null || !Finite(Funding.Instance.Funds) || Funding.Instance.Funds < 0) return 0;
            return (long)Math.Min(ColonyLimits.MaxFunds, Math.Floor(Funding.Instance.Funds));
        }
        private static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static string Bound(string value, int n) { return string.IsNullOrEmpty(value) ? "" : value.Length <= n ? value : value.Substring(0, n); }

        // Endpoint integration is supplied in a separate partial, with no Unity calls
        // on its worker threads. Partial methods allow domain/runtime builds alone.
        partial void PumpManagementEndpoint();
        partial void StopManagementEndpoint();
    }
}
