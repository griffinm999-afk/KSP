using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Expanse.Domain.Colonies;
using WOLF;

internal static class Program
{
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static string Id() { return Guid.NewGuid().ToString("D"); }
    static Dictionary<string, int> Quantities(params object[] values) { var result = new Dictionary<string, int>(); for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], (int)values[i + 1]); return result; }
    static List<ColonyWolfIngredient> Ingredients(string text)
    {
        var result = new List<ColonyWolfIngredient>(); if (string.IsNullOrWhiteSpace(text)) return result;
        var tokens = text.Split(','); for (int i = 0; i < tokens.Length; i += 2) result.Add(new ColonyWolfIngredient { Resource = tokens[i].Trim(), Points = int.Parse(tokens[i + 1], CultureInfo.InvariantCulture) }); return result;
    }
    static ColonyWolfRecipe Installed(string file, string output)
    {
        // Static installed CFG bytes and actual WOLF assembly only. No player save,
        // plugin DLL, game process, resource map or ScenarioPersister is touched.
        byte[] bytes = File.ReadAllBytes(Path.Combine(@"C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF\Parts", file));
        var parsed = ConfigNode.Parse(System.Text.Encoding.UTF8.GetString(bytes)); var part = parsed.GetNode("PART") ?? parsed;
        ConfigNode option = output == "" ? null : part.GetNodes("MODULE").First(x => x.GetValue("name") == "WOLF_RecipeOption" && (x.GetValue("OutputResources") ?? "").Trim() == output);
        return new ColonyWolfRecipe { Id = part.GetValue("name") + ":" + output, PartName = part.GetValue("name"), ConfigurationHash = ColonyStateCodec.Hash(bytes),
            Funds = long.Parse(part.GetValue("cost"), CultureInfo.InvariantCulture), Tech = part.GetValue("TechRequired"),
            Inputs = option == null ? new List<ColonyWolfIngredient>() : Ingredients(option.GetValue("InputResources")), Outputs = option == null ? new List<ColonyWolfIngredient>() : Ingredients(option.GetValue("OutputResources")) };
    }
    static ColonyWolfDepot Snapshot(IDepot depot)
    {
        return new ColonyWolfDepot { Body = depot.Body, Biome = depot.Biome, Exists = true, Established = depot.IsEstablished, Surveyed = depot.IsSurveyed,
            Streams = depot.GetResources().Select(x => new ColonyWolfStream { Resource = x.ResourceName, Incoming = x.Incoming, Outgoing = x.Outgoing }).ToList() };
    }
    static List<IRecipe> ActualRecipes(ColonyWolfQuote quote)
    {
        var result = new List<IRecipe>(); foreach (var module in quote.Modules) for (int i = 0; i < module.Count; i++) result.Add(new Recipe(module.Recipe.Inputs.ToDictionary(x => x.Resource, x => x.Points), module.Recipe.Outputs.ToDictionary(x => x.Resource, x => x.Points))); return result;
    }
    static int Main()
    {
        var harvester = Installed("Harvester_375.cfg", "Gypsum,10"); var power = Installed("PowerModule.cfg", "Power,5"); var depotConstruction = Installed("Depot.cfg", "");
        Check(harvester.Funds == 56410 && power.Funds == 55620 && depotConstruction.Funds == 56410, "quotes read actual installed part costs");
        Check(harvester.Inputs.Single(x => x.Resource == "Power").Points == 5 && harvester.Inputs.Single(x => x.Resource == "GypsumVein").Points == 5, "quotes read actual installed raw recipe inputs");
        Check(power.Inputs.Count == 0 && power.Outputs.Single().Points == 5, "no virtual crew or Maintenance invented by low Power recipe");
        foreach (int desired in new[] { 10, 20, 30 })
        {
            var actual = new Depot("Minmus", "Greater Flats"); actual.Establish(); actual.Survey();
            // Explicit in-memory external-capacity fixture, NOT the normalpath
            // construction API. It never attaches to a game/scenario/save.
            actual.NegotiateProvider(Quantities("Power", 5, "GypsumVein", 45)); actual.NegotiateConsumer(Quantities("Power", 5));
            var state = ColonyEngine.Create(Id(), 0); var colony = new ColonyRecord { Id = Id(), Name = "API contract fixture", Site = new ColonySite { Body = actual.Body, Biome = actual.Biome }, Charter = new ColonyCharter { FoundingBudget = 2_000_000, SpendingLimit = 2_000_000 } }; state.Colonies.Add(colony);
            var env = new ColonyEnvironment { WorldId = state.WorldId, ContextKey = "in-memory-only", AvailableFunds = 2_000_000, UnlockedTech = new List<string> { "advScienceTech" },
                Wolf = new ColonyWolfEnvironment { Ready = true, DepotConstruction = depotConstruction, Recipes = new List<ColonyWolfRecipe> { harvester, power }, Sites = new List<ColonyWolfSiteObservation> { new ColonyWolfSiteObservation { ColonyId = colony.Id, ContextKey = "in-memory-only", Depot = Snapshot(actual) } } } };
            var quote = ColonyEngine.QuoteWolfSupply(state, colony.Id, "Gypsum", desired, env);
            Check(quote.Funds == (56410 + 55620) * (desired / 10), "full installed module cost " + desired);
            var command = new ColonyCommand { OperationId = Id(), Kind = "approveWolfSupply", ContextKey = env.ContextKey, ExpectedRevision = state.Revision, ColonyId = colony.Id, QuoteId = quote.Id, Fields = new Dictionary<string, string> { { "Resource", "Gypsum" }, { "DesiredAvailable", desired.ToString(CultureInfo.InvariantCulture) } } };
            var accepted = ColonyEngine.Execute(state, command, env); Check(accepted.Outcome == "accepted", "costed contract accepts " + desired);
            var payment = accepted.State.Effects.Single(); var paying = ColonyEngine.MarkEffectApplying(accepted.State, payment.Id, "funds-test-readback", "before exact");
            var paid = ColonyEngine.CompleteFundsEffect(paying, payment.Id, env.AvailableFunds, env.AvailableFunds - quote.Funds, 0, "after exact"); env.Ut = quote.LaborSeconds;
            var ready = ColonyEngine.Advance(paid, env); var applying = ColonyEngine.MarkWolfApplying(ready, command.OperationId, "actual installed WOLF", ColonyStateCodec.WolfDepotHash(Snapshot(actual)));
            Check(actual.Negotiate(ActualRecipes(quote)) is OkNegotiationResult, "actual installed batched dependency negotiation " + desired);
            string after = ColonyStateCodec.WolfDepotHash(Snapshot(actual)); Check(after == ColonyStateCodec.WolfDepotHash(quote.After), "actual full ledger exactly matches predicted input/output offsets " + desired);
            var complete = ColonyEngine.CompleteWolfAllocation(applying, command.OperationId, after, env.Ut);
            Check(complete.WolfOrders.Single().State == "operational" && complete.Colonies.Single().Stock.Count == 0, "capacity acknowledged without physical stock credit " + desired);
            var saved = new ConfigNode("DEPOTS"); actual.OnSave(saved); var restored = new Depot(); restored.OnLoad(saved.GetNode("DEPOT"));
            Check(ColonyStateCodec.WolfDepotHash(Snapshot(restored)) == after, "actual WOLF OnSave/OnLoad exact full depot roundtrip " + desired);
            var restoredColony = ColonyStateCodec.Deserialize(ColonyStateCodec.Serialize(complete)); Check(restoredColony.WolfOrders.Single().AfterWitness == after, "colony witness roundtrip " + desired);
        }
        var deficient = new Depot("Minmus", "Fixture"); deficient.Establish(); deficient.Survey(); deficient.NegotiateProvider(Quantities("Power", 5));
        string beforeDeficit = ColonyStateCodec.WolfDepotHash(Snapshot(deficient));
        Check(deficient.Negotiate(new List<IRecipe> { new Recipe(Quantities("Power", 5, "GypsumVein", 5), Quantities("Gypsum", 10)) }) is FailedNegotiationResult, "installed API rejects missing genuine vein");
        Check(ColonyStateCodec.WolfDepotHash(Snapshot(deficient)) == beforeDeficit, "installed expected failure leaves all allocations unchanged");
        Console.WriteLine(checks + " installed-API/CFG contract checks passed. No game process or player save was touched. Physical survey, Scene/OnSave callback boundaries and actual game persistence remain unproved here."); return 0;
    }
}
