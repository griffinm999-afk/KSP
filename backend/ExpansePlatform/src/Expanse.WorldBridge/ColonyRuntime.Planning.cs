using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        List<ColonyEconomyPolicy> economyPolicies;

        void PopulatePlanningEnvironment(ColonyEnvironment env)
        {
            if (economyPolicies == null)
            {
                economyPolicies = new List<ColonyEconomyPolicy>();
                if (GameDatabase.Instance != null && PartResourceLibrary.Instance != null)
                foreach (var config in GameDatabase.Instance.GetConfigNodes("EXPANSE_COLONY_ECONOMY").Take(17))
                {
                    try
                    {
                        if (economyPolicies.Count >= 16) throw new InvalidDataException("Economy catalog supports at most sixteen configured routes.");
                        var policy = new ColonyEconomyPolicy { Id = Required(config, "id"), Body = Required(config, "destinationBody"), Provider = Required(config, "provider"),
                            SetupFunds = Whole(config, "stagingSetupFunds"), SetupTravelSeconds = Value(config, "stagingTravelSeconds"), ContractorWorkers = checked((int)Whole(config, "contractorWorkers")),
                            Reason = "Configured contract model: paid empty staging stores, finite off-world inventory and shared freight slots. Construction labor is included in each building's escrow." };
                        if (economyPolicies.Any(x => x.Id == policy.Id || x.Body == policy.Body)) throw new InvalidDataException("Duplicate economy route identity or destination.");
                        string pool = Required(config, "freightPool");
                        decimal multiplier = decimal.Parse(Required(config, "unitPriceMultiplier"), CultureInfo.InvariantCulture);
                        if (multiplier < 1 || multiplier > 100) throw new InvalidDataException("Import price multiplier is outside its configured bound.");
                        var resourceNames = Required(config, "resources").Split(',').Select(x => x.Trim()).ToArray();
                        if (resourceNames.Length > ColonyLimits.Resources || resourceNames.Distinct(StringComparer.Ordinal).Count() != resourceNames.Length) throw new InvalidDataException("Duplicate or oversized staging resource catalog.");
                        foreach (string resource in resourceNames)
                        {
                            var definition = PartResourceLibrary.Instance.GetDefinition(resource);
                            if (definition == null || !Finite(definition.density) || definition.density < 0 || !Finite(definition.volume) || definition.volume < 0 || !Finite(definition.unitCost) || definition.unitCost < 0)
                                throw new InvalidDataException("Installed resource definition is absent or invalid: " + resource);
                            // Installed resource density is tonnes/unit; volume is litres/unit.
                            var store = new ColonyStock { Resource = resource, Capacity = checked(Whole(config, "stagingCapacityUnits") * ColonyLimits.Units),
                                UnitMassMicroTonnes = checked((long)decimal.Ceiling((decimal)definition.density * 1000000m)), UnitVolumeMilliLiters = checked((long)decimal.Ceiling((decimal)definition.volume * 1000m)) };
                            policy.Stores.Add(store);
                            long unitPrice = Math.Max(1, checked((long)decimal.Ceiling((decimal)definition.unitCost * multiplier)));
                            // The legacy Ore recovery sale pays100/unit. Imported Ore,
                            // if enabled by a later policy, must never form a free arbitrage loop.
                            if (resource == "Ore") unitPrice = Math.Max(101, unitPrice);
                            policy.Suppliers.Add(new ColonySupplier { Id = policy.Id + ":" + resource, FreightPoolId = pool, DestinationBody = policy.Body, Resource = resource,
                                Available = checked(Whole(config, "supplierStockUnits") * ColonyLimits.Units), FundsPerUnit = unitPrice, FreightFunds = Whole(config, "freightFunds"),
                                TravelSeconds = Value(config, "travelSeconds"), ConcurrentCapacity = checked((int)Whole(config, "concurrentFreighters")),
                                MassCapacityMicroTonnes = checked(Whole(config, "freightMassTonnes") * 1000000), VolumeCapacityMilliLiters = checked(Whole(config, "freightVolumeLitres") * 1000) });
                        }
                        policy.Hash = ColonyEngine.EconomyPolicyHash(policy);
                        ColonyStateCodec.ValidateEconomyPolicy(policy); economyPolicies.Add(policy);
                    }
                    catch (Exception ex) { Debug.LogError("[ExpanseColony] Economy route rejected: " + Bound(ex.Message, 360)); }
                }
            }
            env.EconomyPolicies = economyPolicies.ToList();
        }

        void PreparePlanningCommand(ColonyCommand command, ColonyEnvironment env)
        {
            if (command == null || command.Kind != "surveyPlot") return;
            try
            {
                if (command.ContextKey != ContextKey || command.ExpectedRevision != state.Revision) throw new InvalidDataException("Survey request belongs to an old save/revision.");
                var colony = state.Colonies.SingleOrDefault(x => x.Id == command.ColonyId) ?? throw new InvalidDataException("Registered colony is unavailable.");
                var package = templates.SingleOrDefault(x => x.Id == command.Fields["TemplateId"] && x.Hash == command.Fields["TemplateHash"]) ?? throw new InvalidDataException("Selected package changed or is unavailable.");
                ColonyStateCodec.Id(command.TargetId);
                var existing = colony.Plots.SingleOrDefault(x => x.Id == command.TargetId);
                if (existing != null && (existing.OccupiedBy.Length > 0 || existing.ReservedBy.Length > 0)) throw new InvalidDataException("Committed plot cannot be moved.");
                var plots = state.Colonies.Where(x => x.Site.Body == colony.Site.Body).SelectMany(x => x.Plots).ToArray();
                var survey = ColonySiteSurvey.Survey(package, colony.Site.Body,
                    double.Parse(command.Fields["Latitude"], CultureInfo.InvariantCulture), double.Parse(command.Fields["Longitude"], CultureInfo.InvariantCulture),
                    double.Parse(command.Fields["Heading"], CultureInfo.InvariantCulture), plots, ContextKey, existing == null ? null : existing.Id);
                if (!survey.Clear) throw new InvalidDataException(survey.Reason);
                survey.Plot.Id = command.TargetId;
                env.SurveyedPlot = survey.Plot;
            }
            catch (Exception ex) { env.SurveyFailure = Bound(ex.Message, 512); }
        }

        static string Required(ConfigNode node, string key) { string value = node.GetValue(key); if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new InvalidDataException("Missing/bounded economy setting: " + key); return value; }
        static long Whole(ConfigNode node, string key) { long value = long.Parse(Required(node, key), CultureInfo.InvariantCulture); if (value < 0) throw new InvalidDataException("Negative economy setting: " + key); return value; }
        static double Value(ConfigNode node, string key) { double value = double.Parse(Required(node, key), CultureInfo.InvariantCulture); if (!Finite(value) || value <= 0) throw new InvalidDataException("Invalid economy setting: " + key); return value; }
    }
}
