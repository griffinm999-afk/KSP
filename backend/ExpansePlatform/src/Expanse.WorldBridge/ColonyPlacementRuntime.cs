using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Expanse.WorldBridge
{
    // A serialized, one-building-at-a-time main-thread adapter. Background construction
    // can finish anywhere; physical placement deliberately waits for qualified terrain.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class ColonyPlacementRuntime : MonoBehaviour
    {
        private float nextWork;
        private object selectedGame;
        private static readonly MethodInfo Assemble = typeof(ShipConstruction).GetMethod("AssembleForLaunch", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(ShipConstruct), typeof(string), typeof(string), typeof(string), typeof(Game), typeof(VesselCrewManifest), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(Orbit), typeof(bool), typeof(bool) }, null);
        // Tests may request a catchable interruption. Never persisted, and never enabled
        // outside an explicitly armed isolated development executable/save.
        internal static Action<string, string> FailureInjector = null;
        private void OnDestroy() { ColonyPlacementGroundPositioning.RestoreAll("Placement flight runtime destroyed"); }

        private void Update()
        {
            ColonyPlacementGroundPositioning.Refresh();
            if (Time.realtimeSinceStartup < nextWork) return;
            nextWork = Time.realtimeSinceStartup + 0.25f;
            var scenario = ColonyPlacementScenario.Instance;
            if (scenario == null || !scenario.Ready || HighLogic.CurrentGame == null) return;
            // ProtoScenarioModule.Load sets snapshot only after OnLoad returns.
            // Wait until the exact new snapshot belongs to the selected Game;
            // an older game can retain a moduleRef during a load transition.
            if (HighLogic.CurrentGame.scenarios == null || !HighLogic.CurrentGame.scenarios.Any(s => ReferenceEquals(s, scenario.snapshot) && ReferenceEquals(s.moduleRef, scenario))) return;
            if (!ReferenceEquals(selectedGame, HighLogic.CurrentGame))
            {
                selectedGame = HighLogic.CurrentGame;
                foreach (var record in scenario.Records.Values) { record.NeedsReconcile = true; record.StableSince = -1; }
            }
            var current = scenario.Records.Values.Where(x => x.Status.Stage != ColonyPlacementStage.Cancelled)
                .OrderBy(x => x.Status.Stage == ColonyPlacementStage.Settling || x.Status.Stage == ColonyPlacementStage.Created || x.Status.Stage == ColonyPlacementStage.Prepared ? 0 : 1)
                .ThenBy(x => x.Request.OperationId, StringComparer.Ordinal).FirstOrDefault(x => x.NeedsReconcile || (x.Status.Stage != ColonyPlacementStage.Anchored && x.Status.Stage != ColonyPlacementStage.RecoveryHold));
            if (current == null) return;
            try
            {
                if (current.NeedsReconcile) { Reconcile(current); current.NeedsReconcile = false; }
                if (current.Status.Stage == ColonyPlacementStage.AwaitingPlacement) Place(current);
                else if (current.Status.Stage == ColonyPlacementStage.Created || current.Status.Stage == ColonyPlacementStage.Settling) Settle(current);
            }
            catch (Exception e)
            {
                Hold(current, "Placement exception; reconcile saved markers before any retry: " + RootException(e).Message);
                Debug.LogException(e);
            }
        }

        internal static string SafeWindow(ColonyPlacementRequest request, out CelestialBody body)
        {
            body = null;
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || HighLogic.CurrentGame == null) return "Visit the colony in a fully loaded flight scene";
            if (FlightDriver.Pause || Time.timeScale <= 0 || TimeWarp.CurrentRate != 1) return "Return to unpaused 1× time for physical placement";
            if (Assemble == null || Assemble.ReturnType != typeof(Vessel)) return "Installed KSP has no qualified assembly API that preserves the active vessel";
            var reference = FlightGlobals.ActiveVessel;
            if (reference == null || !reference.loaded || reference.mainBody == null || reference.mainBody.bodyName != request.BodyName) return "Visit this colony's celestial body in flight";
            body = reference.mainBody;
            if (body.pqsController == null) return "This body has no terrain provider";
            string referenceReason = ColonyPlacementReference.Stability(reference, request.MaximumSettleSpeed);
            if (referenceReason != null) return referenceReason;
            var point = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, TerrainHeight(body, request.Latitude, request.Longitude));
            // Stock initializes a new vessel from these actual current defaults.
            // A loaded packed craft outside the surface unpack radius cannot
            // finish easing/stability while preserving the active reference.
            var defaults = PhysicsGlobals.Instance == null ? null : PhysicsGlobals.Instance.VesselRangesDefault;
            var surface = defaults == null ? null : defaults.landed;
            if (surface == null || !ColonyPlacementRequest.Finite(surface.unpack) || surface.unpack <= 0) return "Stock surface physics unpack range is unavailable; wait for a qualified flight context";
            double x = Math.Max(Math.Abs(request.MinX), Math.Abs(request.MaxX)) + request.ClearanceMetres;
            double z = Math.Max(Math.Abs(request.MinZ), Math.Abs(request.MaxZ)) + request.ClearanceMetres;
            double radius = Math.Sqrt(x * x + z * z);
            double maximumDistance = Math.Min(1000, surface.unpack - radius - 5);
            if (maximumDistance <= 0) return "Package envelope exceeds the current stock surface physics unpack range";
            if (reference.rootPart == null || (point - (Vector3d)reference.rootPart.transform.position).magnitude >= maximumDistance)
                return "Visit a stable landed vessel within " + maximumDistance.ToString("F0", CultureInfo.InvariantCulture) + " metres of this plot so the complete package can unpack under current stock physics";
            string reason = ColonyPlacementFoundations.Availability();
            return reason;
        }

        private static void Place(ColonyPlacementRecord record)
        {
            CelestialBody body;
            string reason = SafeWindow(record.Request, out body);
            if (reason != null) { record.Status.Reason = "Construction complete; awaiting placement window: " + reason; return; }
            var surfaceCollision = CheckSurface(body, record.Request);
            if (!surfaceCollision.Ready) { record.Status.Reason = "Construction complete; awaiting surface collision provider: " + surfaceCollision.Reason; return; }
            record.Status.SurfaceCollisionWitness = surfaceCollision.Witness;
            if (!surfaceCollision.Clear) { Hold(record, "Pre-assembly surface collision validation failed; no registered building created: " + surfaceCollision.Reason); return; }
            string templatePath = ResolveTemplate(record.Request.TemplateRelativePath);
            var bytes = File.ReadAllBytes(templatePath);
            if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Template exceeds 1 MiB bound");
            if (ColonyPlacementRequest.Hash(bytes) != record.Request.TemplateSha256.ToLowerInvariant()) throw new InvalidOperationException("Installed template changed after quote/certification");
            var config = ConfigNode.Load(templatePath);
            if (config == null) throw new InvalidOperationException("Installed template cannot be read");
            var nodes = config.GetNodes("PART");
            if (nodes.Length < 1 || nodes.Length > ColonyPlacementRecovery.MaximumParts) throw new InvalidOperationException("Template part count is outside the 1–256 part bound");
            var originalCraftIds = new HashSet<uint>();
            foreach (var node in nodes)
            {
                string partName = "", craftId = "";
                KSPUtil.GetPartInfo(node.GetValue("part"), ref partName, ref craftId);
                var partInfo = PartLoader.getPartInfoByName(partName);
                if (partInfo == null || partInfo.partPrefab == null) throw new InvalidOperationException("Installed part unavailable: " + partName);
                if (!partInfo.partPrefab.Modules.OfType<ColonyPlacementMarker>().Any()) throw new InvalidOperationException("ColonyPlacement.cfg is missing from patched part prefabs; reload KSP after installing matching package");
                if (!record.Request.ExplicitSandboxUnlockOverride && !ResearchAndDevelopment.PartTechAvailable(partInfo)) throw new InvalidOperationException("Part technology is not unlocked: " + partName);
                uint parsed = uint.Parse(craftId, CultureInfo.InvariantCulture);
                if (parsed == 0 || !originalCraftIds.Add(parsed)) throw new InvalidOperationException("Template craft IDs are zero or duplicated");
            }
            SanitizeTemplate(config);
            ShipConstruct ship = null;
            bool assemblyAttempted = false;
            Vessel beforeActive = FlightGlobals.ActiveVessel;
            try
            {
                ship = new ShipConstruct();
                if (!ship.LoadShip(config, FlightGlobals.GetUniquepersistentId())) throw new InvalidOperationException("Stock template assembly rejected its attachment tree");
                if (ship.parts.Count != nodes.Length) throw new InvalidOperationException("Template part count changed during assembly");
                ValidateTree(ship);
                ship.shipName = record.Request.FacilityName;
                ClearAndAllocateContents(ship, record.Request);
                foreach (var part in ship.parts)
                {
                    var marker = part.Modules.OfType<ColonyPlacementMarker>().Single();
                    marker.operationId = record.Request.OperationId; marker.worldId = record.Request.WorldId; marker.colonyId = record.Request.ColonyId;
                    marker.plotId = record.Request.PlotId; marker.requestFingerprint = record.Status.RequestFingerprint;
                    marker.templateSha256 = record.Request.TemplateSha256.ToLowerInvariant(); marker.craftPartId = part.craftID;
                    part.protoModuleCrew.Clear();
                }
                ColonyPlacementInitialAnimation.Prepare(ship, config);
                var survey = SurveyAndPosition(ship, body, record.Request);
                // Part.LoadShip/Awake can call other real modules. Re-read the
                // provider before the irreversible native assembly boundary.
                var freshSurface = CheckSurface(body, record.Request);
                if (!freshSurface.Ready) { DestroyUnregistered(ship); ship = null; record.Status.Reason = "Construction complete; awaiting fresh surface collision data: " + freshSurface.Reason; return; }
                if (!freshSurface.Clear) throw new InvalidOperationException(freshSurface.Reason);
                record.Status.SurfaceCollisionWitness = freshSurface.Witness;
                record.Status.ActualMaximumSlopeDegrees = survey.MaximumSlope; record.Status.ActualMaximumSupportGapMetres = survey.MaximumGap; record.Status.SurveyTerrainHeight = survey.TerrainHeight;
                record.Status.BeforeWitness = Witness(beforeActive);
                record.Status.VesselPersistentId = ship.persistentId;
                record.Status.PartPersistentIds = ship.parts.Select(x => x.persistentId).OrderBy(x => x).ToArray(); record.Status.PartCount = ship.parts.Count;
                record.Status.Stage = ColonyPlacementStage.Prepared; record.Status.Reason = "Validated terrain, clearance, uncrewed contents and fresh membership; assembly prepared";
                record.Status.IncludedInSaveSerialization = false;
                Inject("before-assembly", record.Request.OperationId);
                assemblyAttempted = true;
                record.Status.AssemblyAttempted = true;
                bool activeChangeEvent = false;
                EventData<Vessel>.OnEvent observeControl = changed => { if (changed != beforeActive) activeChangeEvent = true; };
                Vessel vessel;
                GameEvents.onVesselChange.Add(observeControl);
                try { vessel = (Vessel)Assemble.Invoke(null, new object[] { ship, "", "", HighLogic.CurrentGame.flagURL, HighLogic.CurrentGame, new VesselCrewManifest(), true, false, true, false, null, false, false }); }
                finally { GameEvents.onVesselChange.Remove(observeControl); }
                // No public overload is called and active control is never restored by
                // switching it back. A change is an adapter failure, even if caused by a mod.
                if (activeChangeEvent || FlightGlobals.ActiveVessel != beforeActive) throw new InvalidOperationException("An assembly callback changed the active vessel (including a transient control event); isolate that mod before retrying");
                if (vessel == null || !FlightGlobals.Vessels.Contains(vessel)) throw new InvalidOperationException("Stock assembly did not register the placed vessel");
                vessel.vesselType = VesselType.Base;
                record.Status.VesselId = vessel.id.ToString("D"); record.Status.VesselPersistentId = vessel.persistentId;
                record.Status.FlightIds = vessel.parts.Select(x => x.flightID).OrderBy(x => x).ToArray(); record.Status.CreatedUt = Planetarium.GetUniversalTime();
                record.Status.Stage = ColonyPlacementStage.Created; record.Status.Reason = "Real building created; waiting for stock physics easing and measured settlement";
                record.Status.AfterWitness = Witness(vessel);
                string identityError = ValidateBuilding(record, vessel, true);
                if (identityError != null) throw new InvalidOperationException(identityError);
                ColonyPlacementGroundPositioning.Begin(record, vessel);
                Inject("after-assembly", record.Request.OperationId);
                record.Status.Stage = ColonyPlacementStage.Settling; record.StableSince = -1;
                Debug.Log("[ExpanseColonyPlacement] Created " + record.Request.OperationId + " vessel=" + record.Status.VesselId + " parts=" + record.Status.PartCount + " active=" + beforeActive.id);
            }
            catch (Exception e)
            {
                if (!assemblyAttempted)
                {
                    DestroyUnregistered(ship);
                    // A request remains reserved. Failures are actionable and visible,
                    // never silently refunded or charged a second time.
                    Hold(record, "Pre-assembly validation failed; no registered building created: " + RootException(e).Message);
                }
                else Hold(record, "Assembly may have applied; reconcile marked building without respawn/refund: " + RootException(e).Message);
                Debug.LogException(e);
            }
        }

        internal static void SanitizeTemplate(ConfigNode config)
        {
            config.SetValue("persistentId", "0", true);
            config.RemoveNodes("VESSELMODULES"); config.RemoveNodes("VESSELNAMING");
            foreach (var part in config.GetNodes("PART"))
            {
                part.SetValue("persistentId", "0", true);
                foreach (string field in new[] { "uid", "mid", "flightID", "missionID", "launchID", "crew" }) part.RemoveValues(field);
                foreach (var module in part.GetNodes("MODULE"))
                {
                    string name = module.GetValue("name") ?? "";
                    if (name == "ColonyPlacementMarker")
                    {
                        foreach (string key in new[] { "operationId", "worldId", "colonyId", "plotId", "requestFingerprint", "templateSha256", "craftPartId" }) module.RemoveValues(key);
                    }
                    if (name == "WOLF_HopperModule" || name == "WolfHopper")
                    {
                        module.SetValue("HopperId", "", true); module.SetValue("IsConnectedToDepot", "False", true);
                        module.SetValue("DepotBiome", "", true); module.SetValue("DepotBody", "", true);
                    }
                    if (name == "USI_InertialDampener") module.SetValue("isActive", "False", true);
                    foreach (string key in new[] { "IsActivated", "isActivated", "isRunning", "IsRunning", "isHopperActive" }) if (module.HasValue(key)) module.SetValue(key, "False", true);
                    if (name.IndexOf("Foundation", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("DepotRegistry", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("Template contains a saved anchor/depot module instead of certified hardware");
                }
                foreach (var resource in part.GetNodes("RESOURCE")) resource.SetValue("amount", "0", true);
                part.RemoveNodes("VESSELNAMING");
            }
        }

        private static void ClearAndAllocateContents(ShipConstruct ship, ColonyPlacementRequest request)
        {
            if (!ship.parts.Any(x => x.Modules.OfType<ModuleCommand>().Any(m => m.minimumCrew == 0))) throw new InvalidOperationException("Certified building has no uncrewed command source");
            foreach (var part in ship.parts)
            {
                if (part.Modules.OfType<LaunchClamp>().Any()) throw new InvalidOperationException("Launch clamps are not qualified colony supports");
                if (part.Resources != null) foreach (PartResource resource in part.Resources) resource.amount = 0;
                foreach (ModuleResourceConverter converter in part.Modules.OfType<ModuleResourceConverter>()) converter.StopResourceConverter();
            }
            foreach (var content in request.Contents)
            {
                var matches = ship.parts.Where(x => x.craftID == content.CraftPartId).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Commissioned content part is absent or ambiguous");
                var resources = matches[0].Resources.Cast<PartResource>().Where(x => x.resourceName == content.ResourceName).ToArray();
                if (resources.Length != 1 || content.Amount > resources[0].maxAmount) throw new InvalidOperationException(ContentCapacityFailure(matches[0], content, resources));
                resources[0].amount = content.Amount;
            }
        }

        private static string ContentCapacityFailure(Part part, ColonyPlacementContent content, PartResource[] resources)
        {
            string maxima = resources.Length == 0 ? "absent" : string.Join(",", resources.Take(3).Select(r => r.maxAmount.ToString("R", CultureInfo.InvariantCulture)).ToArray());
            if (resources.Length > 3) maxima += ",...";
            string partName = part.partInfo == null ? "unknown" : ContentDiagnosticName(part.partInfo.name, 64);
            string result = "Commissioned contents do not fit their specified physical tank; craft=" + content.CraftPartId.ToString(CultureInfo.InvariantCulture) +
                "; resource=" + ContentDiagnosticName(content.ResourceName, 80) + "; requested=" + content.Amount.ToString("R", CultureInfo.InvariantCulture) +
                "; matches=" + resources.Length.ToString(CultureInfo.InvariantCulture) + "; max=[" + maxima + "]; part=" + partName;
            return result.Length <= 420 ? result : result.Substring(0, 420);
        }
        private static string ContentDiagnosticName(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            string clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            return clean.Length <= maximum ? clean : clean.Substring(0, maximum);
        }

        private static void ValidateTree(ShipConstruct ship)
        {
            var roots = ship.parts.Where(x => x.parent == null).ToArray();
            if (roots.Length != 1 || roots[0] != ship.parts[0].localRoot) throw new InvalidOperationException("Template must have one fixed connected root");
            if (roots[0].Modules.OfType<ModuleDeployablePart>().Any() || roots[0].Modules.OfType<ModuleWheelBase>().Any()) throw new InvalidOperationException("Template root must be fixed structure for Foundations");
            var visited = new HashSet<Part>(); var pending = new Stack<Part>(); pending.Push(roots[0]);
            while (pending.Count > 0)
            {
                var current = pending.Pop(); if (!visited.Add(current)) throw new InvalidOperationException("Template attachment tree cycles or repeats a child");
                foreach (var child in current.children)
                {
                    if (child == null || child.parent != current || !ship.parts.Contains(child)) throw new InvalidOperationException("Template has a nonreciprocal child attachment");
                    pending.Push(child);
                }
            }
            if (visited.Count != ship.parts.Count || ship.parts.Any(x => x.persistentId == 0) || ship.parts.Select(x => x.persistentId).Distinct().Count() != ship.parts.Count)
                throw new InvalidOperationException("Template is disconnected or has nonunique fresh part identities");
        }

        internal static string ResolveTemplate(string relativePath)
        {
            string root = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "ExpanseWorldBridge", "Templates"));
            string full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Template escaped the trusted installed package");
            string cursor = full;
            while (!string.IsNullOrEmpty(cursor))
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Template package contains a reparse point");
                if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase)) break;
                cursor = Path.GetDirectoryName(cursor);
            }
            return full;
        }

        private sealed class SurveyResult { public double TerrainHeight, MaximumSlope, MaximumGap; }

        private static SurveyResult SurveyAndPosition(ShipConstruct ship, CelestialBody body, ColonyPlacementRequest request)
        {
            double height = TerrainHeight(body, request.Latitude, request.Longitude);
            var surface = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, height);
            var radial = (surface - body.position).normalized;
            RaycastHit centerHit;
            string terrainReason;
            if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)(surface + radial * 50), (Vector3)(-radial), 100, out centerHit, out terrainReason))
                throw new InvalidOperationException(terrainReason);
            var up = centerHit.normal.normalized;
            if (Vector3.Dot(up, (Vector3)radial) < 0.9f) throw new InvalidOperationException("Plot center terrain normal is unsafe");
            var northPoint = body.GetWorldSurfacePosition(request.Latitude + 0.001, request.Longitude, height);
            var north = Vector3.ProjectOnPlane((Vector3)(northPoint - surface), up).normalized;
            var forward = Quaternion.AngleAxis((float)request.HeadingDegrees, up) * north;
            var frame = Quaternion.LookRotation(forward, up);
            var template = new Quaternion((float)request.TemplateRotationX, (float)request.TemplateRotationY, (float)request.TemplateRotationZ, (float)request.TemplateRotationW);
            var root = ship.parts[0].localRoot;
            var originalRoot = root.transform.position;
            var geometry = ship.parts.Select(x => new { Part = x, Position = x.transform.position, Rotation = x.transform.rotation }).ToArray();
            // Reset the editor origin explicitly, then orient the certified template.
            Quaternion rotation = frame * template;
            var positions = geometry.Select(x => new { x.Part, Position = rotation * (x.Position - originalRoot), Rotation = rotation * x.Rotation }).ToArray();
            foreach (var item in positions) item.Part.transform.SetPositionAndRotation(item.Position, item.Rotation);
            Physics.SyncTransforms();
            // Stock Part.Awake temporarily makes Part.collider a trigger. Its
            // native flight Start restores that exact primary collider before
            // physical settlement. Read its real bounds in the unstarted craft
            // preview without changing any collider state; arbitrary triggers
            // remain excluded. Final deployed checks require real non-triggers.
            var colliders = ship.parts.SelectMany(p => p.GetComponentsInChildren<Collider>().Where(c => c.enabled &&
                (!c.isTrigger || !p.started && c == p.collider) && c.gameObject.layer != 21)).Distinct().ToArray();
            if (colliders.Length == 0) throw new InvalidOperationException("Template has no qualified physical colliders");
            var inverse = Quaternion.Inverse(frame);
            ColonyPlacementPreviewGeometryDiagnostic.Observe(colliders, inverse, request);
            double minimum = double.MaxValue; var boundsCorners = new List<Vector3>();
            foreach (var collider in colliders)
            {
                Vector3 min, max; string geometryReason;
                if (!ColonyPlacementColliderGeometry.TryBounds(collider, Vector3.zero, inverse, out min, out max, out geometryReason))
                    throw new InvalidOperationException("Unqualified preview collider geometry: " + geometryReason);
                foreach (var local in Corners(new Bounds((min + max) / 2, max - min)))
                {
                    boundsCorners.Add(local);
                    if (local.x < request.MinX - 0.01 || local.x > request.MaxX + 0.01 || local.z < request.MinZ - 0.01 || local.z > request.MaxZ + 0.01)
                        throw new InvalidOperationException(PreviewFootprintFailure(collider, min, max, request));
                    minimum = Math.Min(minimum, local.y);
                }
            }
            double maximumGeometryHeight = boundsCorners.Max(x => x.y) - minimum;
            if (maximumGeometryHeight > request.MaximumHeight + 0.01) throw new InvalidOperationException("Actual collider height exceeds the certified deployment envelope");
            int xSteps = Math.Max(2, (int)Math.Ceiling(request.MaxX - request.MinX));
            int zSteps = Math.Max(2, (int)Math.Ceiling(request.MaxZ - request.MinZ));
            if ((xSteps + 1) * (zSteps + 1) > 4096) throw new InvalidOperationException("Full-footprint terrain sampling exceeds its 4,096 point bound");
            double maximumOffset = double.MinValue, minimumOffset = double.MaxValue, maximumSlope = 0;
            for (int x = 0; x <= xSteps; x++) for (int z = 0; z <= zSteps; z++)
            {
                float localX = (float)(request.MinX + (request.MaxX - request.MinX) * x / xSteps);
                float localZ = (float)(request.MinZ + (request.MaxZ - request.MinZ) * z / zSteps);
                var projected = (Vector3d)centerHit.point + (Vector3d)(frame * new Vector3(localX, 0, localZ));
                RaycastHit hit;
                if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)(projected + (Vector3d)up * 20), -up, 40, out hit, out terrainReason))
                    throw new InvalidOperationException(terrainReason);
                double offset = Vector3.Dot(hit.point - centerHit.point, up);
                maximumOffset = Math.Max(maximumOffset, offset); minimumOffset = Math.Min(minimumOffset, offset);
                maximumSlope = Math.Max(maximumSlope, Vector3.Angle(hit.normal, (Vector3)((Vector3d)hit.point - body.position).normalized));
            }
            if (maximumSlope > request.MaximumSlopeDegrees) throw new InvalidOperationException("Surveyed slope exceeds the template's certified limit: " + maximumSlope.ToString("F3", CultureInfo.InvariantCulture) + "°");
            double gap = maximumOffset - minimumOffset;
            if (gap > request.MaximumSupportGapMetres) throw new InvalidOperationException("Terrain support gap exceeds certified supports: " + gap.ToString("F3", CultureInfo.InvariantCulture) + " m");
            var translation = centerHit.point + up * (float)(maximumOffset - minimum + 0.05);
            foreach (var item in positions) item.Part.transform.SetPositionAndRotation(item.Position + translation, item.Rotation);
            Physics.SyncTransforms();
            // Full deployment envelope plus access clearance, checked against existing
            // colliders. The terrain is checked above and excluded from overlap queries.
            var half = new Vector3((float)((request.MaxX - request.MinX) / 2 + request.ClearanceMetres), (float)(request.MaximumHeight / 2), (float)((request.MaxZ - request.MinZ) / 2 + request.ClearanceMetres));
            var localCenter = new Vector3((float)((request.MaxX + request.MinX) / 2), (float)(maximumOffset + request.MaximumHeight / 2 + 0.02), (float)((request.MaxZ + request.MinZ) / 2));
            var center = centerHit.point + frame * localCenter;
            var own = new HashSet<Collider>(colliders);
            var overlaps = Physics.OverlapBox(center, half, frame, ~(1 << 15 | 1 << 21), QueryTriggerInteraction.Ignore);
            if (overlaps.Length > 4096) throw new InvalidOperationException("Clearance query exceeds collider bound");
            if (overlaps.Any(x => x != null && !own.Contains(x) && !ship.parts.Any(p => x.transform.IsChildOf(p.transform))))
                throw new InvalidOperationException("Deployment or access envelope intersects existing hardware; select or adjust a plot");
            foreach (var part in ship.parts) part.UpdateOrgPosAndRot(root);
            return new SurveyResult { TerrainHeight = height, MaximumSlope = maximumSlope, MaximumGap = gap };
        }

        // Capture the actual rejected shape before the unregistered preview is
        // destroyed. Observation only: all geometry predicates remain unchanged.
        private static string PreviewFootprintFailure(Collider collider, Vector3 minimum, Vector3 maximum, ColonyPlacementRequest request)
        {
            var part = collider.GetComponentInParent<Part>();
            var path = new List<string>(); var cursor = collider.transform;
            for (int depth = 0; cursor != null && (part == null || cursor != part.transform) && depth < 16; depth++, cursor = cursor.parent)
                path.Add(cursor.name);
            path.Reverse();
            string label = string.Join("/", path.ToArray()).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            if (label.Length > 80) label = label.Substring(0, 80);
            string partName = part == null || part.partInfo == null ? "unknown" : part.partInfo.name;
            if (partName.Length > 32) partName = partName.Substring(0, 32);
            Func<Vector3, string> vector = value => "(" + value.x.ToString("G7", CultureInfo.InvariantCulture) + "," + value.y.ToString("G7", CultureInfo.InvariantCulture) + "," + value.z.ToString("G7", CultureInfo.InvariantCulture) + ")";
            string detail = "Actual loaded collider extends outside the certified deployment footprint" +
                "; craft=" + (part == null ? "unknown" : part.craftID.ToString(CultureInfo.InvariantCulture)) + "/" + partName +
                "; collider=" + collider.GetType().Name +
                "; surface-frame min=" + vector(minimum) + "; max=" + vector(maximum) +
                "; certified x=[" + request.MinX.ToString("R", CultureInfo.InvariantCulture) + "," + request.MaxX.ToString("R", CultureInfo.InvariantCulture) +
                "], z=[" + request.MinZ.ToString("R", CultureInfo.InvariantCulture) + "," + request.MaxZ.ToString("R", CultureInfo.InvariantCulture) + "]; tolerance=0.01 m; path=" + label;
            // The persisted domain reason is bounded to512 including the
            // pre-assembly prefix. Keep measurements before any truncated path.
            return detail.Length <= 420 ? detail : detail.Substring(0, 420);
        }

        private static IEnumerable<Vector3> Corners(Bounds bounds)
        {
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                yield return bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
        }
        private static double TerrainHeight(CelestialBody body, double latitude, double longitude)
        {
            var radial = QuaternionD.AngleAxis(longitude, Vector3d.down) * QuaternionD.AngleAxis(latitude, Vector3d.forward) * Vector3d.right;
            double height = body.pqsController.GetSurfaceHeight(radial) - body.Radius;
            if (!ColonyPlacementRequest.Finite(height)) throw new InvalidOperationException("Terrain height is not finite");
            return height;
        }

        private static void Reconcile(ColonyPlacementRecord record)
        {
            ColonyPlacementGroundPositioning.RestoreInterruptedSnapshot(record);
            var candidates = MarkedVessels(record.Request.OperationId);
            bool complete = candidates.Length != 1 || Membership(candidates[0]).SequenceEqual(record.Status.PartPersistentIds);
            bool matches = candidates.Length != 1 || ValidateBuilding(record, candidates[0], false) == null;
            string error = ColonyPlacementRecovery.Reconcile(record.Status.Stage, candidates.Length, complete, matches);
            error = ColonyPlacementRecoveryReason.Truthful(record.Status, candidates.Length, error);
            if (error != null) { Hold(record, error); return; }
            if (candidates.Length != 1) return;
            var vessel = candidates[0];
            if (record.Status.Stage == ColonyPlacementStage.AwaitingPlacement)
            { Hold(record, "Marked building exists before the queue records an assembly attempt; hold for branch reconciliation"); return; }
            if (record.Status.Stage == ColonyPlacementStage.Anchored)
            {
                if (vessel.loaded && !ColonyPlacementFoundations.IsHeld(vessel)) Hold(record, "Saved commissioned building no longer has its Foundations hold");
                return;
            }
            // An uncertain operation never auto-resumes. It is visible for an explicit
            // reconciliation request after independent physical/escrow review.
            if (record.Status.Stage == ColonyPlacementStage.RecoveryHold) return;
            record.Status.VesselId = vessel.id.ToString("D"); record.Status.VesselPersistentId = vessel.persistentId;
            record.Status.FlightIds = FlightMembership(vessel); record.Status.AfterWitness = Witness(vessel);
            if (record.GroundPositioningEvidence != null) ColonyPlacementGroundPositioning.Seal(record);
            record.Status.Stage = ColonyPlacementStage.Settling; record.Status.Reason = "Existing marked building reconciled; resume stability and anchor qualification";
        }

        private static void Settle(ColonyPlacementRecord record)
        {
            var candidates = MarkedVessels(record.Request.OperationId);
            if (candidates.Length != 1) { Hold(record, "Expected exactly one marked building while settling"); return; }
            var vessel = candidates[0];
            string identityError = ValidateBuilding(record, vessel, false);
            if (identityError != null) { Hold(record, identityError); return; }
            if (!HighLogic.LoadedSceneIsFlight || FlightDriver.Pause || Time.timeScale <= 0 || TimeWarp.CurrentRate != 1 || !vessel.loaded)
            { RestartStability(record, "Created building awaits a loaded unpaused 1× settlement window"); return; }
            if (ColonyPlacementFoundations.IsHeld(vessel)) { CompleteAnchor(record, vessel); return; }
            ColonyPlacementGroundContacts.Attach(vessel, record.Request.OperationId);
            // Stock easing measures the actual undeployed collider geometry and
            // moves a newly assembled vessel before unpacking. Native deployment
            // must not change that geometry during the stock ground correction.
            // Wait for ordinary stable landed physics; never force unpack or move
            // the reference vessel to make this window occur.
            string deploymentWindow = ColonyPlacementDeploymentSafety.Eligibility(vessel.loaded, vessel.Landed, vessel.Splashed, vessel.packed, vessel.HoldPhysics, vessel.easingInToSurface, vessel.srfSpeed, record.Request.MaximumSettleSpeed);
            if (deploymentWindow != null) { RestartStability(record, deploymentWindow); return; }
            string deploymentWait = EnsurePlannedPlanetaryDeployments(record, vessel);
            if (deploymentWait != null) { RestartStability(record, deploymentWait); return; }
            string reason = ColonyPlacementFoundations.Eligibility(vessel);
            if (reason != null || !vessel.Landed || vessel.Splashed || vessel.packed || vessel.HoldPhysics || vessel.easingInToSurface || vessel.srfSpeed > record.Request.MaximumSettleSpeed)
            { RestartStability(record, reason ?? "Waiting for stable landed physics"); return; }
            if (vessel.mainBody == null || vessel.mainBody.bodyName != record.Request.BodyName) { Hold(record, "Building is on another celestial body"); return; }
            var pose = BodyRelativePosition(vessel);
            var rotation = Quaternion.Inverse(vessel.mainBody.bodyTransform.rotation) * vessel.rootPart.transform.rotation;
            double now = Time.realtimeSinceStartup;
            if (record.StableSince < 0 || now < record.StableSince)
            { record.StableSince = now; record.StableRotation = rotation; record.StablePosition = pose; record.Status.Reason = StabilityIntervalReason(record.Status.Reason); return; }
            double elapsed = now - record.StableSince;
            if (elapsed < record.Request.SettleSeconds) return;
            double speed = (pose - record.StablePosition).magnitude / elapsed;
            double angularSpeed = Quaternion.Angle(record.StableRotation, rotation) / elapsed;
            if (speed > record.Request.MaximumSettleSpeed || angularSpeed > record.Request.MaximumAngularSpeedDegrees)
            { RestartStability(record, "Body-relative pose is still moving; stability interval restarted"); return; }
            var expected = vessel.mainBody.GetWorldSurfacePosition(record.Request.Latitude, record.Request.Longitude, record.Status.SurveyTerrainHeight);
            // GetWorldPos3D returns CoMD. Cargo and asymmetric machinery may
            // move that point while the surveyed root remains exactly in place.
            var actual = (Vector3d)vessel.rootPart.transform.position; var up = (expected - vessel.mainBody.position).normalized;
            double horizontal = ((actual - expected) - up * Vector3d.Dot(actual - expected, up)).magnitude;
            if (horizontal > 0.5) { Hold(record, "Settled building drifted more than 0.5 metres from its surveyed root position"); return; }
            var surfaceCollision = CheckSurface(vessel.mainBody, record.Request);
            if (!surfaceCollision.Ready) { RestartStability(record, surfaceCollision.Reason); return; }
            if (!surfaceCollision.Clear) { Hold(record, "Final deployment/support/access surface collision validation failed: " + surfaceCollision.Reason); return; }
            record.Status.SurfaceCollisionWitness = surfaceCollision.Witness;
            if (!SettledEnvelope(vessel, record.Request)) { Hold(record, "Deployed collider geometry exceeds the reviewed plot envelope; anchoring cannot certify that plot"); return; }
            if (!SettledClearance(vessel, record.Request.ClearanceMetres)) { Hold(record, "Settled building overlaps another physical object; anchoring cannot conceal the collision"); return; }
            ConfigNode footing; string footingReason;
            if (!ColonyPlacementGroundContacts.ReadWitness(vessel, record.Request, out footing, out footingReason))
            { RestartStability(record, footingReason); return; }
            record.GroundContactEvidence = footing;
            Inject("before-anchor", record.Request.OperationId);
            if (!ColonyPlacementFoundations.Anchor(vessel)) { Hold(record, "Foundations rejected settled building: " + ColonyPlacementFoundations.LastMessage()); return; }
            Inject("after-anchor", record.Request.OperationId);
            CompleteAnchor(record, vessel);
        }

        private const string StabilityMeasuring = "Measuring ten-second stability before anchoring";
        private const string StabilityWait = "Waiting to anchor: ";
        private const string StabilityLastWait = StabilityMeasuring + "; last wait: ";
        private static string StabilityWaitReason(string statusReason)
        {
            if (statusReason == null) return null;
            if (statusReason.StartsWith(StabilityLastWait, StringComparison.Ordinal)) return statusReason.Substring(StabilityLastWait.Length);
            if (statusReason.StartsWith(StabilityWait, StringComparison.Ordinal)) return statusReason.Substring(StabilityWait.Length);
            return null;
        }
        private static string StabilityIntervalReason(string statusReason)
        {
            string wait = StabilityWaitReason(statusReason);
            return string.IsNullOrEmpty(wait) ? StabilityMeasuring : StabilityLastWait + wait;
        }
        private static void RestartStability(ColonyPlacementRecord record, string reason)
        {
            // Retain the last failed gate through the next measurement interval;
            // otherwise a quarter-second reset is invisible in slower snapshots.
            string wait = string.IsNullOrEmpty(reason) ? "Settlement provider is not ready" : reason;
            if (wait.Length > 360) wait = wait.Substring(0, 360);
            if (!string.Equals(StabilityWaitReason(record.Status.Reason), wait, StringComparison.Ordinal))
                Debug.Log("[ExpanseColonyPlacement] Stability wait operation=" + record.Request.OperationId + ": " + wait);
            record.StableSince = -1; record.Status.Reason = StabilityWait + wait;
        }

        private static string EnsurePlannedPlanetaryDeployments(ColonyPlacementRecord record, Vessel vessel)
        {
            string window = ColonyPlacementDeploymentSafety.Eligibility(vessel.loaded, vessel.Landed, vessel.Splashed, vessel.packed, vessel.HoldPhysics, vessel.easingInToSurface, vessel.srfSpeed, record.Request.MaximumSettleSpeed);
            if (window != null) return window;
            if (record.PlannedPlanetaryDeployments == null)
            {
                string path = ResolveTemplate(record.Request.TemplateRelativePath); byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > 1024 * 1024 || ColonyPlacementRequest.Hash(bytes) != record.Request.TemplateSha256.ToLowerInvariant())
                    throw new InvalidOperationException("Exact deployment package changed after paid placement intent");
                var config = ConfigNode.Load(path); if (config == null) throw new InvalidOperationException("Exact deployment package cannot be read");
                var intended = new List<uint>();
                foreach (var part in config.GetNodes("PART"))
                {
                    var deployments = part.GetNodes("MODULE").Where(n => n.GetValue("name") == "PlanetaryModule").ToArray();
                    if (deployments.Length > 1) throw new InvalidOperationException("Package has ambiguous native planetary deployment modules");
                    if (deployments.Length == 0) continue;
                    double time; if (!double.TryParse(deployments[0].GetValue("animationTime"), NumberStyles.Float, CultureInfo.InvariantCulture, out time) || !ColonyPlacementRequest.Finite(time) || time < 0 || time > 1)
                        throw new InvalidOperationException("Package lacks explicit bounded native planetary deployment intent");
                    if (time < .999) continue;
                    string name = "", id = ""; KSPUtil.GetPartInfo(part.GetValue("part"), ref name, ref id);
                    intended.Add(uint.Parse(id, CultureInfo.InvariantCulture));
                }
                record.PlannedPlanetaryDeployments = intended.ToArray();
            }
            if (record.PlannedPlanetaryDeployments.Length == 0) return null;
            if (!vessel.parts.All(p => p.started)) return "Waiting for all actual native parts before package deployment";
            var surfaceCollision = CheckSurface(vessel.mainBody, record.Request);
            if (!surfaceCollision.Ready) return "Waiting for native collision-provider readiness before deployment: " + surfaceCollision.Reason;
            if (!surfaceCollision.Clear) throw new InvalidOperationException(surfaceCollision.Reason);
            if (!SettledEnvelope(vessel, record.Request))
                throw new InvalidOperationException("Native deployment physical collider geometry exceeds the declared envelope; hold original building without deployment/anchoring");
            if (!DeclaredDeploymentClearance(vessel, record.Request))
                throw new InvalidOperationException("Native deployment reserved sweep or access volume intersects other hardware; hold original building without deployment/anchoring");
            bool waiting = false;
            foreach (uint craft in record.PlannedPlanetaryDeployments)
            {
                var matches = vessel.parts.Where(p => p.Modules.OfType<ColonyPlacementMarker>().Any(m => m.operationId == record.Request.OperationId && m.craftPartId == craft)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Actual native deployment part is absent or duplicated");
                var part = matches[0]; var modules = part.Modules.Cast<PartModule>().Where(m => m.moduleName == "PlanetaryModule").ToArray();
                if (modules.Length != 1 || modules[0].GetType().FullName != "PlanetarySurfaceStructures.PlanetaryModule") throw new InvalidOperationException("Actual native planetary deployment API differs from qualified package");
                var module = modules[0]; var type = module.GetType();
                string phase = Convert.ToString(type.GetField("moduleStatus", BindingFlags.Instance | BindingFlags.Public).GetValue(module), CultureInfo.InvariantCulture);
                int deployedCapacity = Convert.ToInt32(type.GetField("crewCapacityDeployed", BindingFlags.Instance | BindingFlags.Public).GetValue(module), CultureInfo.InvariantCulture);
                if (phase == "Deployed")
                { if (part.CrewCapacity != deployedCapacity) throw new InvalidOperationException("Actual deployed capacity differs from native module"); continue; }
                waiting = true;
                if (phase == "Deploying") continue;
                if (phase != "Retracted" && phase != "Retracting") throw new InvalidOperationException("Unknown actual native planetary deployment state");
                if (!(bool)type.GetField("hasBeenInitialized", BindingFlags.Instance | BindingFlags.Public).GetValue(module)) return "Waiting for native planetary deployment initialization";
                var action = module.Events["toggleAnimation"];
                if (action == null || !action.active || !action.guiActive) throw new InvalidOperationException("Actual native planetary deploy event is unavailable");
                // Another real module's event can synchronously alter geometry.
                // Repeat the complete current physical and reserved-volume
                // checks at each external event boundary on this Unity thread.
                surfaceCollision = CheckSurface(vessel.mainBody, record.Request);
                if (!surfaceCollision.Ready) return "Waiting for fresh native collision-provider readback at deployment: " + surfaceCollision.Reason;
                if (!surfaceCollision.Clear) throw new InvalidOperationException(surfaceCollision.Reason);
                record.Status.SurfaceCollisionWitness = surfaceCollision.Witness;
                if (!SettledEnvelope(vessel, record.Request) || !DeclaredDeploymentClearance(vessel, record.Request))
                    throw new InvalidOperationException("Fresh native deployment geometry or full reserved sweep/access preflight failed; hold original building");
                // The native method owns animation, capacity and IVA state. Never
                // assign deployment fields/capacity to manufacture a certificate.
                ColonyPlacementDeploymentSafety.Invoke(vessel.loaded, vessel.Landed, vessel.Splashed, vessel.packed, vessel.HoldPhysics, vessel.easingInToSurface, vessel.srfSpeed, record.Request.MaximumSettleSpeed, action.Invoke);
                record.StableSince = -1;
                Debug.Log("[ExpanseColonyPlacement] Native deploy event operation=" + record.Request.OperationId + " craftPart=" + craft);
            }
            return waiting ? "Waiting for actual native planetary deployment and complete swept-envelope clearance before stability/anchoring" : null;
        }

        private static bool DeclaredDeploymentClearance(Vessel vessel, ColonyPlacementRequest request)
        {
            var body = vessel.mainBody; if (body == null) return false;
            double height = TerrainHeight(body, request.Latitude, request.Longitude);
            var center = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, height);
            var radial = (center - body.position).normalized; RaycastHit terrain;
            string terrainReason;
            if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)(center + radial * 50), (Vector3)(-radial), 100, out terrain, out terrainReason)) return false;
            var surfaceCollision = CheckSurface(body, request); if (!surfaceCollision.Ready || !surfaceCollision.Clear) return false;
            var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + .001, request.Longitude, height) - center), terrain.normal).normalized;
            var frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees, terrain.normal) * north, terrain.normal);
            var half = new Vector3((float)((request.MaxX-request.MinX)/2+request.ClearanceMetres),(float)(request.MaximumHeight/2+.05),(float)((request.MaxZ-request.MinZ)/2+request.ClearanceMetres));
            var localCenter = new Vector3((float)((request.MaxX+request.MinX)/2),(float)(request.MaximumHeight/2),(float)((request.MaxZ+request.MinZ)/2));
            var hits = new Collider[4097]; Physics.SyncTransforms();
            int count = Physics.OverlapBoxNonAlloc(terrain.point+frame*localCenter,half,hits,frame,~(1<<15|1<<21),QueryTriggerInteraction.Ignore);
            if (count >= hits.Length) return false;
            var own = new HashSet<Part>(vessel.parts);
            for (int i=0;i<count;i++) { var owner=hits[i].GetComponentInParent<Part>(); if(owner==null || !own.Contains(owner))return false; }
            return true;
        }

        private static void CompleteAnchor(ColonyPlacementRecord record, Vessel vessel)
        {
            ColonyPlacementGroundPositioning.Restore(record, "Completed Foundation handoff");
            if (!ColonyPlacementFoundations.IsHeld(vessel) || !vessel.packed) { Hold(record, "Foundation handoff did not read back a held packed building"); return; }
            string id; double positionError, angleError;
            if (!ColonyPlacementFoundations.ReadWitness(vessel, out id, out positionError, out angleError)) { Hold(record, "Foundation anchor witness is unavailable"); return; }
            if (positionError > 0.01 || angleError > 0.01) { Hold(record, "Foundation applied pose exceeds the 0.01 m/0.01° readback budget"); return; }
            record.Status.FoundationId = id; record.Status.MeasuredPositionErrorMetres = positionError; record.Status.MeasuredAngleErrorDegrees = angleError;
            record.Status.AnchoredUt = Planetarium.GetUniversalTime(); record.Status.AfterWitness = Witness(vessel);
            if (record.GroundContactEvidence != null) record.Status.AfterWitness += ";nativeFooting=" + ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(record.GroundContactEvidence.ToString()));
            if (record.GroundPositioningEvidence != null) record.Status.AfterWitness += ";nativeGroundPositioning=" + ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(record.GroundPositioningEvidence.ToString()));
            if (!string.IsNullOrEmpty(record.Status.SurfaceCollisionWitness)) record.Status.AfterWitness += ";nativeSurfaceCollision=" + record.Status.SurfaceCollisionWitness;
            record.Status.Stage = ColonyPlacementStage.Anchored; record.Status.Reason = "Physical building and Foundations hold read back; colony commissioning remains required";
            record.Status.IncludedInSaveSerialization = false;
            Debug.Log("[ExpanseColonyPlacement] Anchored " + record.Request.OperationId + " foundation=" + id);
        }

        private static bool SettledClearance(Vessel vessel, double margin)
        {
            Physics.SyncTransforms();
            var ownParts = new HashSet<Part>(vessel.parts);
            foreach (var part in vessel.parts)
            {
                foreach (var collider in part.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled || collider.isTrigger || collider.gameObject.layer == 21) continue;
                    var bounds = collider.bounds;
                    var expanded = bounds.extents + new Vector3((float)margin, 0, (float)margin);
                    var hits = Physics.OverlapBox(bounds.center, expanded, Quaternion.identity, ~(1 << 15 | 1 << 21), QueryTriggerInteraction.Ignore);
                    if (hits.Length > 4096) return false;
                    foreach (var hit in hits)
                    {
                        if (hit == null) continue;
                        var owner = hit.GetComponentInParent<Part>();
                        if (owner == null || !ownParts.Contains(owner)) return false;
                    }
                }
            }
            return true;
        }

        private static bool SettledEnvelope(Vessel vessel, ColonyPlacementRequest request)
        {
            var body = vessel.mainBody;
            double height = TerrainHeight(body, request.Latitude, request.Longitude);
            var center = body.GetWorldSurfacePosition(request.Latitude, request.Longitude, height);
            var radial = (center - body.position).normalized;
            RaycastHit hit;
            string terrainReason;
            if (!ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)(center + radial * 50), (Vector3)(-radial), 100, out hit, out terrainReason)) return false;
            var north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude + 0.001, request.Longitude, height) - center), hit.normal).normalized;
            var forward = Quaternion.AngleAxis((float)request.HeadingDegrees, hit.normal) * north;
            var inverse = Quaternion.Inverse(Quaternion.LookRotation(forward, hit.normal));
            foreach (var part in vessel.parts)
            {
                if (!part.started) return false;
                foreach (var collider in part.GetComponentsInChildren<Collider>())
                {
                    if (!collider.enabled || collider.isTrigger || collider.gameObject.layer == 21) continue;
                    Vector3 min, max; string geometryReason;
                    if (!ColonyPlacementColliderGeometry.TryBounds(collider, hit.point, inverse, out min, out max, out geometryReason))
                        throw new InvalidOperationException("Unqualified settled collider geometry: " + geometryReason);
                    if (min.x < request.MinX - 0.02 || max.x > request.MaxX + 0.02 || min.z < request.MinZ - 0.02 || max.z > request.MaxZ + 0.02 || max.y > request.MaximumHeight + 0.02)
                        return false;
                }
            }
            return true;
        }

        private static ColonyPlacementSurfaceCheck CheckSurface(CelestialBody body, ColonyPlacementRequest request)
        { return ColonyPlacementParallaxSurface.Check(body, request.Latitude, request.Longitude, TerrainHeight(body, request.Latitude, request.Longitude), request.HeadingDegrees, request.MinX, request.MaxX, request.MinZ, request.MaxZ, request.MaximumHeight, request.ClearanceMetres); }

        internal static Vessel[] MarkedVessels(string operationId)
        {
            if (FlightGlobals.Vessels == null) return new Vessel[0];
            // Bounded supported world size; exceeding the bound holds rather than
            // truncating discovery and making a missing marker look absent.
            if (FlightGlobals.Vessels.Count > 4096) throw new InvalidOperationException("World vessel count exceeds qualified 4,096-vessel placement discovery bound");
            return FlightGlobals.Vessels.Where(v => v != null && HasMarker(v, operationId)).ToArray();
        }
        private static bool HasMarker(Vessel vessel, string operationId)
        {
            if (vessel.loaded && vessel.parts != null) return vessel.parts.Any(p => p != null && p.Modules.OfType<ColonyPlacementMarker>().Any(m => m.operationId == operationId));
            if (vessel.protoVessel == null || vessel.protoVessel.protoPartSnapshots == null) return false;
            return vessel.protoVessel.protoPartSnapshots.Any(p => p != null && p.modules.Any(m => m.moduleName == "ColonyPlacementMarker" && m.moduleValues.GetValue("operationId") == operationId));
        }

        internal static string ValidateBuilding(ColonyPlacementRecord record, Vessel vessel, bool checkContents)
        {
            if (vessel == null || vessel.persistentId != record.Status.VesselPersistentId) return "Placed vessel persistent identity differs from preparation";
            if (!string.IsNullOrEmpty(record.Status.VesselId) && vessel.id.ToString("D") != record.Status.VesselId) return "Placed vessel identity changed or merged; independent membership reconciliation required";
            var persistent = Membership(vessel); var flights = FlightMembership(vessel);
            if (persistent.Length != record.Status.PartCount || persistent.Any(x => x == 0) || persistent.Distinct().Count() != persistent.Length || !persistent.SequenceEqual(record.Status.PartPersistentIds) ||
                flights.Length != persistent.Length || flights.Any(x => x == 0) || flights.Distinct().Count() != flights.Length ||
                (record.Status.FlightIds.Length > 0 && !flights.SequenceEqual(record.Status.FlightIds))) return "Placed part/flight membership is missing, duplicated, merged or changed";
            if (vessel.loaded && vessel.parts != null)
            {
                foreach (var part in vessel.parts)
                {
                    var markers = part.Modules.OfType<ColonyPlacementMarker>().ToArray();
                    if (markers.Length != 1 || !MarkerMatches(record, markers[0].operationId, markers[0].worldId, markers[0].colonyId, markers[0].plotId, markers[0].requestFingerprint, markers[0].templateSha256) || markers[0].craftPartId != part.craftID)
                        return "Placed part marker differs from its operation/template witnesses";
                    if (checkContents)
                    {
                        if (part.protoModuleCrew.Count != 0) return "Uncrewed placement unexpectedly contains a Kerbal";
                        foreach (PartResource resource in part.Resources)
                        {
                            var content = record.Request.Contents.FirstOrDefault(x => x.CraftPartId == part.craftID && x.ResourceName == resource.resourceName);
                            double expected = content == null ? 0 : content.Amount;
                            if (Math.Abs(resource.amount - expected) > Math.Max(0.000001, expected * 0.00000001)) return "Startup stock readback differs from explicit paid contents";
                        }
                    }
                }
            }
            else if (vessel.protoVessel != null)
            {
                foreach (var part in vessel.protoVessel.protoPartSnapshots)
                {
                    var markers = part.modules.Where(x => x.moduleName == "ColonyPlacementMarker").ToArray();
                    if (markers.Length != 1) return "Unloaded placed part has no unique persistent operation marker";
                    var n = markers[0].moduleValues;
                    if (!MarkerMatches(record, n.GetValue("operationId"), n.GetValue("worldId"), n.GetValue("colonyId"), n.GetValue("plotId"), n.GetValue("requestFingerprint"), n.GetValue("templateSha256")))
                        return "Unloaded placement marker differs from its operation/template witnesses";
                }
            }
            else return "Placed building has no loaded/proto identity provider";
            return null;
        }
        private static bool MarkerMatches(ColonyPlacementRecord record, string operation, string world, string colony, string plot, string fingerprint, string template)
        { return operation == record.Request.OperationId && world == record.Request.WorldId && colony == record.Request.ColonyId && plot == record.Request.PlotId && fingerprint == record.Status.RequestFingerprint && template == record.Request.TemplateSha256.ToLowerInvariant(); }
        private static uint[] Membership(Vessel vessel) { return vessel.loaded && vessel.parts != null ? vessel.parts.Select(x => x.persistentId).OrderBy(x => x).ToArray() : vessel.protoVessel.protoPartSnapshots.Select(x => x.persistentId).OrderBy(x => x).ToArray(); }
        private static uint[] FlightMembership(Vessel vessel) { return vessel.loaded && vessel.parts != null ? vessel.parts.Select(x => x.flightID).OrderBy(x => x).ToArray() : vessel.protoVessel.protoPartSnapshots.Select(x => x.flightID).OrderBy(x => x).ToArray(); }
        private static Vector3d BodyRelativePosition(Vessel vessel) { return (Vector3d)(Quaternion.Inverse(vessel.mainBody.bodyTransform.rotation) * (Vector3)(vessel.rootPart.transform.position - (Vector3)vessel.mainBody.position)); }
        private static string Witness(Vessel vessel)
        {
            if (vessel == null) return "none";
            return "vessel=" + vessel.id.ToString("D") + ";persistent=" + vessel.persistentId + ";body=" + (vessel.mainBody == null ? "unknown" : vessel.mainBody.bodyName) +
                ";lat=" + vessel.latitude.ToString("R", CultureInfo.InvariantCulture) + ";lon=" + vessel.longitude.ToString("R", CultureInfo.InvariantCulture) + ";alt=" + vessel.altitude.ToString("R", CultureInfo.InvariantCulture) +
                ";parts=" + Membership(vessel).Length + ";partHash=" + ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join(",", Membership(vessel).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()))) +
                ";flightHash=" + ColonyPlacementRequest.Hash(System.Text.Encoding.UTF8.GetBytes(string.Join(",", FlightMembership(vessel).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()))) +
                ";crew=" + vessel.GetCrewCount() + ";packed=" + vessel.packed;
        }
        private static void DestroyUnregistered(ShipConstruct ship)
        {
            if (ship == null) return;
            foreach (var part in ship.parts.ToArray())
            {
                if (part != null && (part.vessel == null || !FlightGlobals.Vessels.Contains(part.vessel))) UnityEngine.Object.Destroy(part.gameObject);
            }
            ship.Clear();
        }
        private static Exception RootException(Exception exception) { return exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception; }
        private static void Hold(ColonyPlacementRecord record, string reason)
        { ColonyPlacementGroundPositioning.Restore(record, "Placement failure/hold"); record.StableSince = -1; record.Status.Stage = ColonyPlacementStage.RecoveryHold; record.Status.Reason = reason.Length > 1024 ? reason.Substring(0, 1024) : reason; record.Status.IncludedInSaveSerialization = false; Debug.LogError("[ExpanseColonyPlacement] " + record.Request.OperationId + ": " + reason); }
        private static void Inject(string phase, string operationId) { if (FailureInjector != null) FailureInjector(phase, operationId); }
    }

    internal static class ColonyPlacementFoundations
    {
        private static Type hold, registry;
        private static void Resolve()
        {
            if (hold != null && registry != null) return;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "ExpanseFoundations") continue;
                hold = assembly.GetType("Expanse.Foundations.Hold", false); registry = assembly.GetType("Expanse.Foundations.FoundationRegistry", false); break;
            }
        }
        internal static string Availability()
        {
            Resolve();
            if (hold == null || registry == null) return "Matching Expanse Foundations is not installed";
            var instanceField = registry.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceField == null ? null : instanceField.GetValue(null);
            var ready = registry.GetField("Ready", BindingFlags.Public | BindingFlags.Instance);
            if (instance == null || ready == null || !(bool)ready.GetValue(instance)) return "Waiting for this save's Foundations registry";
            var hooks = hold.GetField("HooksReady", BindingFlags.Public | BindingFlags.Static);
            if (hooks == null || !(bool)hooks.GetValue(null)) return "Foundations KSP hooks are unavailable";
            foreach (string name in new[] { "Eligibility", "AnchorBase", "IsHeld", "Get" }) if (hold.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vessel) }, null) == null) return "Installed Foundations adapter API is incompatible";
            return null;
        }
        private static object Call(string method, Vessel vessel)
        {
            Resolve(); if (hold == null) throw new InvalidOperationException("Expanse Foundations is unavailable");
            return hold.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vessel) }, null).Invoke(null, new object[] { vessel });
        }
        internal static bool IsHeld(Vessel vessel) { return Availability() == null && (bool)Call("IsHeld", vessel); }
        internal static string Eligibility(Vessel vessel) { string unavailable = Availability(); return unavailable ?? (string)Call("Eligibility", vessel); }
        internal static bool Anchor(Vessel vessel) { return Availability() == null && (bool)Call("AnchorBase", vessel); }
        internal static string LastMessage() { Resolve(); var field = hold == null ? null : hold.GetField("LastMessage", BindingFlags.Public | BindingFlags.Static); return field == null ? "No foundation readback" : (string)field.GetValue(null); }
        internal static bool ReadWitness(Vessel vessel, out string id, out double positionError, out double angleError)
        {
            id = null; positionError = angleError = double.NaN;
            if (Availability() != null) return false;
            object binding = Call("Get", vessel); if (binding == null) return false;
            var type = binding.GetType(); object anchor = type.GetField("Anchor").GetValue(binding); if (anchor == null) return false;
            id = (string)anchor.GetType().GetField("Id").GetValue(anchor);
            positionError = (double)type.GetField("AppliedPositionError").GetValue(binding); angleError = (double)type.GetField("AppliedAngleError").GetValue(binding);
            return ColonyPlacementRequest.Token(id, 128) && ColonyPlacementRequest.Finite(positionError) && ColonyPlacementRequest.Finite(angleError);
        }
    }
}
