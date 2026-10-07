using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;
using WOLF;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private const string WolfConstructionProvider = "WOLF.USI_WOLF.1.0.0.0.costed-module.v1";
        private static readonly Version SupportedWolfVersion = new Version(1, 0, 0, 0);
        private string wolfCatalogEpoch;
        private ColonyWolfEnvironment wolfInstalledCatalog;
        private string wolfCatalogFailure;

        private static bool TryWolfRegistry(out WOLF_ScenarioModule scenario, out IRegistryCollection registry, out string reason)
        {
            scenario = null; registry = null; reason = "Installed WOLF provider is not ready.";
            if (typeof(IDepot).Assembly.GetName().Version != SupportedWolfVersion) { reason = "Installed WOLF version has not been qualified for costed allocation."; return false; }
            var game = HighLogic.CurrentGame;
            if (game == null || game.scenarios == null || game.scenarios.Count > 256 || game.scenarios.Any(x => x == null)) return false;
            // Resolve the current selected game's exact scenario, rather than a
            // global Unity scene scan or a cached registry from an earlier save.
            var rows = game.scenarios.Where(x => x.moduleRef is WOLF_ScenarioModule || x.moduleName == typeof(WOLF_ScenarioModule).Name || x.moduleName == typeof(WOLF_ScenarioModule).FullName).ToArray();
            if (rows.Length != 1 || rows[0].moduleRef == null || rows[0].moduleRef.GetType() != typeof(WOLF_ScenarioModule))
            { reason = "Selected game has no unique initialized installed WOLF scenario."; return false; }
            scenario = (WOLF_ScenarioModule)rows[0].moduleRef;
            if (scenario.ServiceManager == null) return false;
            registry = scenario.ServiceManager.GetService<IRegistryCollection>();
            var persister = registry as ScenarioPersister;
            if (persister == null || !persister.IsLoaded) return false;
            var depots = registry.GetDepots();
            if (depots == null || depots.Count > 256 || depots.Any(x => x == null) || depots.GroupBy(x => x.Body + "\n" + x.Biome, StringComparer.Ordinal).Any(x => x.Count() != 1))
            { reason = "WOLF depot identity collection is invalid or exceeds its bound."; return false; }
            if (!ReferenceEquals(game, HighLogic.CurrentGame) || !ReferenceEquals(rows[0].moduleRef, scenario) || !game.scenarios.Contains(rows[0]))
            { reason = "Selected game changed during WOLF provider observation."; return false; }
            reason = ""; return true;
        }

        private void PopulateWolfEnvironment(ColonyEnvironment env)
        {
            if (state == null) return;
            if (PartLoader.LoadedPartsList == null || PartLoader.LoadedPartsList.Count == 0) { env.Wolf.Reason = "WOLF part database is not ready."; return; }
            WOLF_ScenarioModule scenario; IRegistryCollection registry; string reason;
            if (!TryWolfRegistry(out scenario, out registry, out reason)) { env.Wolf.Reason = reason; return; }
            try
            {
                if (wolfCatalogEpoch != loadEpoch)
                {
                    wolfCatalogEpoch = loadEpoch; wolfInstalledCatalog = null; wolfCatalogFailure = null;
                    try
                    {
                        var installed = new ColonyEnvironment(); ReadInstalledWolfCatalog(installed);
                        wolfInstalledCatalog = installed.Wolf;
                    }
                    catch (Exception ex) { wolfCatalogFailure = Bound(ex.Message, 360); }
                }
                if (wolfInstalledCatalog == null) throw new InvalidDataException(wolfCatalogFailure ?? "WOLF part database is not ready.");
                // The cache owns immutable part/configuration terms only. Current
                // selected-save depot observations and technology are refreshed.
                env.Wolf.DepotConstruction = wolfInstalledCatalog.DepotConstruction;
                env.Wolf.Recipes = wolfInstalledCatalog.Recipes.ToList();
                foreach (string tech in env.Wolf.Recipes.Select(x => x.Tech).Concat(new[] { env.Wolf.DepotConstruction.Tech }).Distinct(StringComparer.Ordinal)) AddWolfUnlockedTech(env, tech);
                if (env.Wolf.DepotConstruction.ConfigurationHash.Length == 0 || env.Wolf.Recipes.Count == 0) throw new InvalidDataException("Installed WOLF depot/recipe catalog is unavailable.");
                foreach (var colony in state.Colonies)
                {
                    var site = new ColonyWolfSiteObservation { ColonyId = colony.Id, ContextKey = env.ContextKey, ObservedUt = env.Ut,
                        Depot = CaptureWolfDepot(registry, colony.Site.Body, colony.Site.Biome) };
                    var body = FlightGlobals.Bodies == null ? null : FlightGlobals.Bodies.FirstOrDefault(x => x != null && x.bodyName == colony.Site.Body);
                    if (body == null) continue;
                    site.HomeGround = body.isHomeWorld;
                    if (!site.Depot.Surveyed)
                    {
                        WOLF_SurveyModule scanner = FindOwnedWolfSurveyor(colony, 0);
                        if (scanner == null) site.Reason = "New survey requires an adopted loaded landed Surface Scanner in this exact biome; WOLF veins cannot be purchased or edited into existence.";
                        else
                        {
                            site.SurveyPartId = scanner.part.persistentId; site.SurveyConfigurationHash = WolfPartHash(scanner.part.partInfo);
                            var vessel = scanner.vessel;
                            var abundance = ResourceManager.GetResourceAbundance(body.flightGlobalsIndex, vessel.altitude, vessel.latitude, vessel.longitude,
                                new[] { HarvestTypes.Atmospheric, HarvestTypes.Planetary }, scenario.Configuration, body.isHomeWorld);
                            site.SurveyVeins = abundance.Where(x => x.Value > 0).OrderBy(x => x.Key, StringComparer.Ordinal)
                                .Select(x => new ColonyWolfIngredient { Resource = x.Key, Points = x.Value }).ToList();
                            ColonyStateCodec.WolfIngredients(site.SurveyVeins);
                        }
                    }
                    env.Wolf.Sites.Add(site);
                }
                env.Wolf.Ready = true; env.Wolf.Provider = WolfConstructionProvider; env.Wolf.Reason = "Qualified installed recipes; existing external WOLF capacity remains separately observed.";
            }
            catch (Exception ex) { env.Wolf.Ready = false; env.Wolf.Reason = Bound(ex.Message, 360); }
        }

        private static string WolfPartHash(AvailablePart info)
        {
            if (info == null || info.partConfig == null || info.partPrefab == null) throw new InvalidDataException("Installed WOLF part definition is unavailable.");
            string text = info.partConfig.ToString();
            if (text.Length > 256 * 1024) throw new InvalidDataException("Installed WOLF configuration exceeds its bound.");
            // KSP normalizes/removes cost and technology from partConfig during
            // loading. Bind their authoritative AvailablePart values as well.
            return ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(info.name + "\n" + info.cost.ToString("R", CultureInfo.InvariantCulture) + "\n" + info.TechRequired + "\n" + text));
        }
        private static void ReadInstalledWolfCatalog(ColonyEnvironment env)
        {
            if (PartLoader.LoadedPartsList == null || PartLoader.LoadedPartsList.Count > 8192) throw new InvalidDataException("Loaded part database is unavailable or exceeds the catalog bound.");
            foreach (var part in PartLoader.LoadedPartsList)
            {
                if (part == null || part.partPrefab == null || part.partConfig == null) continue;
                bool depot = part.partPrefab.FindModulesImplementing<WOLF_DepotModule>().Any(x => !(x is WOLF_SurveyModule));
                bool converter = part.partPrefab.FindModulesImplementing<WOLF_ConverterModule>().Any();
                if (!depot && !converter) continue;
                string hash = WolfPartHash(part);
                double price = part.cost;
                if (!Finite(price) || price <= 0 || price > ColonyLimits.MaxFunds) throw new InvalidDataException("Installed WOLF part price is invalid.");
                long funds = checked((long)Math.Ceiling(price));
                if (depot)
                {
                    if (env.Wolf.DepotConstruction.ConfigurationHash.Length != 0) throw new InvalidDataException("Multiple installed depot definitions require explicit construction-provider qualification.");
                    env.Wolf.DepotConstruction = new ColonyWolfRecipe { Id = part.name + ":depot", PartName = part.name, ConfigurationHash = hash, Tech = part.TechRequired, Funds = funds };
                    AddWolfUnlockedTech(env, part.TechRequired); continue;
                }
                var options = part.partConfig.GetNodes("MODULE").Where(x => x.GetValue("name") == "WOLF_RecipeOption").ToArray();
                if (options.Length == 0) options = part.partConfig.GetNodes("MODULE").Where(x => x.GetValue("name") == "WOLF_ConverterModule" || x.GetValue("name") == "WOLF_HarvesterModule").ToArray();
                for (int i = 0; i < options.Length; i++)
                {
                    var recipe = new ColonyWolfRecipe { Id = part.name + ":" + i.ToString(CultureInfo.InvariantCulture), PartName = part.name,
                        ConfigurationHash = hash, Tech = part.TechRequired, Funds = funds,
                        Inputs = ParseWolfIngredients(options[i].GetValue("InputResources")), Outputs = ParseWolfIngredients(options[i].GetValue("OutputResources")) };
                    if (recipe.Outputs.Count == 0) continue;
                    ColonyStateCodec.ValidateWolfRecipe(recipe);
                    if (env.Wolf.Recipes.Count >= 128) throw new InvalidDataException("Installed WOLF recipe catalog exceeds its 128-row bound.");
                    env.Wolf.Recipes.Add(recipe);
                }
                AddWolfUnlockedTech(env, part.TechRequired);
            }
        }
        private static void AddWolfUnlockedTech(ColonyEnvironment env, string tech)
        {
            if (HighLogic.CurrentGame != null && (HighLogic.CurrentGame.Mode != Game.Modes.CAREER || ResearchAndDevelopment.Instance != null && ResearchAndDevelopment.GetTechnologyState(tech) == RDTech.State.Available))
                if (!env.UnlockedTech.Contains(tech)) env.UnlockedTech.Add(tech);
        }
        internal static List<ColonyWolfIngredient> ParseWolfIngredients(string text)
        {
            var result = new List<ColonyWolfIngredient>(); if (string.IsNullOrWhiteSpace(text)) return result;
            if (text.Length > 4096) throw new InvalidDataException("Installed WOLF ingredient list exceeds its bound.");
            string[] tokens = text.Split(','); if (tokens.Length % 2 != 0 || tokens.Length > 32) throw new InvalidDataException("Installed WOLF ingredient list is malformed.");
            for (int i = 0; i < tokens.Length; i += 2) result.Add(new ColonyWolfIngredient { Resource = tokens[i].Trim(), Points = int.Parse(tokens[i + 1].Trim(), CultureInfo.InvariantCulture) });
            ColonyStateCodec.WolfIngredients(result); return result;
        }

        private static ColonyWolfDepot CaptureWolfDepot(IRegistryCollection registry, string body, string biome)
        {
            IDepot actual; bool exists = registry.TryGetDepot(body, biome, out actual);
            var snapshot = new ColonyWolfDepot { Body = body, Biome = biome, Exists = exists, Established = exists && actual.IsEstablished, Surveyed = exists && actual.IsSurveyed };
            if (exists)
            {
                var streams = actual.GetResources(); if (streams == null || streams.Count > 128 || streams.Any(x => x == null)) throw new InvalidDataException("WOLF stream collection is invalid or exceeds its bound.");
                snapshot.Streams = streams.Select(x => new ColonyWolfStream { Resource = x.ResourceName, Incoming = x.Incoming, Outgoing = x.Outgoing }).ToList();
                if (streams.Any(x => x.Available != x.Incoming - x.Outgoing)) throw new InvalidDataException("WOLF stream available value differs from allocation ledger.");
            }
            ColonyStateCodec.ValidateWolfDepot(snapshot); return snapshot;
        }
        private WOLF_SurveyModule FindOwnedWolfSurveyor(ColonyRecord colony, uint targetPart)
        {
            if (!HighLogic.LoadedSceneIsFlight || FlightGlobals.currentMainBody == null || FlightGlobals.currentMainBody.bodyName != colony.Site.Body || FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 2048) return null;
            var members = new HashSet<uint>(colony.Facilities.Where(x => x.State != "retired").SelectMany(x => x.PartIds));
            // Persistent identity must be unique in the selected world, including
            // unloaded vessels. An ambiguous scanner can never authorize a survey.
            var identities = new HashSet<uint>();
            foreach (var vessel in FlightGlobals.Vessels)
            {
                if (vessel == null) continue;
                var ids = vessel.loaded && vessel.parts != null ? vessel.parts.Where(x => x != null).Select(x => x.persistentId) : vessel.protoVessel == null ? new uint[0] : vessel.protoVessel.protoPartSnapshots.Select(x => x.persistentId);
                foreach (uint id in ids) if (members.Contains(id) && !identities.Add(id)) return null;
            }
            foreach (var vessel in FlightGlobals.Vessels.Where(x => x != null && x.loaded && !x.packed && x.Landed && x.mainBody != null && x.mainBody.bodyName == colony.Site.Body && x.parts != null))
            {
                var facility = colony.Facilities.FirstOrDefault(x => x.State != "retired" && x.VesselId == vessel.id.ToString("D"));
                if (facility == null || WOLF_AbstractPartModule.GetVesselBiome(vessel) != colony.Site.Biome || ColonyEngine.SurfaceDistance(colony.Site, new ColonySite { Body = colony.Site.Body, Latitude = vessel.latitude, Longitude = vessel.longitude }, vessel.mainBody.Radius) > colony.Site.RadiusMeters) continue;
                foreach (var part in vessel.parts.Where(x => x != null && facility.PartIds.Contains(x.persistentId) && (targetPart == 0 || x.persistentId == targetPart)))
                {
                    var scanners = part.FindModulesImplementing<WOLF_SurveyModule>();
                    if (scanners.Count == 1) return scanners[0];
                }
            }
            return null;
        }

        private void ApplyOneWolfEffect()
        {
            if (mutating || !Ready || !ReferenceEquals(selectedGame, HighLogic.CurrentGame) || state.Effects.Any(x => x.State == "held" || x.State == "applying")) return;
            var order = state.WolfOrders.FirstOrDefault(x => x.State == "ready"); if (order == null) return;
            WOLF_ScenarioModule scenario; IRegistryCollection registry; string reason;
            if (!TryWolfRegistry(out scenario, out registry, out reason)) return;
            var game = HighLogic.CurrentGame; string epoch = loadEpoch; var colony = state.Colonies.Single(x => x.Id == order.ColonyId); var q = ColonyEngine.WolfAllocationQuote(order);
            ColonyState applying = null;
            try
            {
                var env = GetEnvironment(); var site = env.Wolf.Sites.SingleOrDefault(x => x.ColonyId == colony.Id);
                if (!env.Wolf.Ready || site == null) throw new InvalidDataException(env.Wolf.Reason);
                string before = ColonyStateCodec.WolfDepotHash(site.Depot);
                if (before != ColonyStateCodec.WolfDepotHash(q.Before)) throw new InvalidDataException("WOLF whole-depot ledger changed during supplier lead time. No allocation was attempted; retain paid hardware for explicit replan.");
                foreach (var module in q.Modules)
                {
                    var current = env.Wolf.Recipes.SingleOrDefault(x => x.Id == module.Recipe.Id);
                    if (current == null || !SameWolfRecipe(current, module.Recipe)) throw new InvalidDataException("Installed WOLF recipe or purchase terms changed; automatic setup disabled.");
                }
                if (q.EstablishDepot && !SameWolfRecipe(env.Wolf.DepotConstruction, q.DepotConstruction)) throw new InvalidDataException("Installed WOLF depot construction terms changed.");
                WOLF_SurveyModule scanner = null;
                if (q.SurveyDepot)
                {
                    scanner = FindOwnedWolfSurveyor(colony, q.SurveyPartId);
                    if (scanner == null || site.SurveyPartId != q.SurveyPartId || site.SurveyConfigurationHash != q.SurveyConfigurationHash || !SameWolfIngredients(site.SurveyVeins, q.SurveyVeins)) throw new InvalidDataException("Quoted physical scanner or genuine geological survey changed; no WOLF mutation attempted.");
                }
                applying = ColonyEngine.MarkWolfApplying(state, order.Id, WolfConstructionProvider, before);
                var complete = ColonyEngine.CompleteWolfAllocation(applying, order.Id, ColonyStateCodec.WolfDepotHash(q.After), Planetarium.GetUniversalTime());
                byte[] completeBytes = ColonyStateCodec.Serialize(complete); string completeHash = ColonyStateCodec.Hash(completeBytes);
                // Durable unknown outcome is authoritative BEFORE any WOLF API or
                // survey/science callback. A partial throw is never rolled back by
                // guessing which point deltas succeeded, and never resent.
                Accept(applying); mutating = true;
                if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed before WOLF setup.");
                IDepot depot;
                if (!registry.TryGetDepot(q.Before.Body, q.Before.Biome, out depot))
                {
                    depot = registry.CreateDepot(q.Before.Body, q.Before.Biome);
                    if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed during WOLF depot creation; no further old-world mutations allowed.");
                }
                if (q.EstablishDepot)
                {
                    depot.Establish();
                    if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed during WOLF establishment; no further old-world mutations allowed.");
                    var starting = q.HomeGround ? new Dictionary<string, int> { { "Food", 1 }, { "MaterialKits", 5 }, { "Oxygen", 1 }, { "Power", 10 }, { "Water", 5 } } : new Dictionary<string, int> { { "Power", 5 } };
                    if (depot.NegotiateProvider(starting) is FailedNegotiationResult) throw new InvalidOperationException("WOLF rejected installed depot starting resources.");
                    if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed during WOLF starting-resource allocation; no further old-world mutations allowed.");
                }
                if (scanner != null) scanner.ConnectToDepotEvent();
                if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed during WOLF setup.");
                var recipes = new List<IRecipe>();
                foreach (var module in q.Modules) for (int i = 0; i < module.Count; i++) recipes.Add(new Recipe(module.Recipe.Inputs.ToDictionary(x => x.Resource, x => x.Points), module.Recipe.Outputs.ToDictionary(x => x.Resource, x => x.Points)));
                if (recipes.Count > 0 && depot.Negotiate(recipes) is FailedNegotiationResult) throw new InvalidOperationException("Installed WOLF API rejected quoted dependency allocation.");
                if (!SameWolfContext(game, epoch, applying, scenario, registry)) throw new InvalidOperationException("Selected save/provider changed during WOLF allocation.");
                string after = ColonyStateCodec.WolfDepotHash(CaptureWolfDepot(registry, q.Before.Body, q.Before.Biome));
                if (after != ColonyStateCodec.WolfDepotHash(q.After)) throw new InvalidOperationException("WOLF full-depot readback differs from quoted deterministic result. Partial/unknown outcome held; replay disabled.");
                state = complete; acceptedBytes = completeBytes; acceptedHash = completeHash;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(game, HighLogic.CurrentGame) && epoch == loadEpoch && (applying == null || state == applying))
                {
                    string after = ""; try { after = ColonyStateCodec.WolfDepotHash(CaptureWolfDepot(registry, q.Before.Body, q.Before.Biome)); } catch { }
                    Accept(ColonyEngine.HoldWolfAllocation(state, order.Id, after, Bound(ex.Message, 360)));
                }
                else HoldReason = "Selected world changed during WOLF allocation; preserve/reconcile the selected save's durable witness.";
            }
            finally { mutating = false; }
        }
        private bool SameWolfContext(object game, string epoch, ColonyState applying, WOLF_ScenarioModule scenario, IRegistryCollection registry)
        {
            WOLF_ScenarioModule currentScenario; IRegistryCollection currentRegistry; string reason;
            return ReferenceEquals(game, HighLogic.CurrentGame) && ReferenceEquals(game, selectedGame) && epoch == loadEpoch && state == applying &&
                TryWolfRegistry(out currentScenario, out currentRegistry, out reason) && ReferenceEquals(scenario, currentScenario) && ReferenceEquals(registry, currentRegistry);
        }
        private static bool SameWolfRecipe(ColonyWolfRecipe a, ColonyWolfRecipe b) => a.Id == b.Id && a.PartName == b.PartName && a.ConfigurationHash == b.ConfigurationHash && a.Tech == b.Tech && a.Funds == b.Funds && a.LaborSeconds == b.LaborSeconds && SameWolfIngredients(a.Inputs, b.Inputs) && SameWolfIngredients(a.Outputs, b.Outputs);
        private static bool SameWolfIngredients(List<ColonyWolfIngredient> a, List<ColonyWolfIngredient> b) => a.Count == b.Count && a.All(x => b.Any(y => y.Resource == x.Resource && y.Points == x.Points));
    }
}
