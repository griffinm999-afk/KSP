using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed class ColonyTemplateCatalogIssue
    {
        public string TemplateId { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class ColonyTemplateCatalogSnapshot
    {
        public string PackageHash { get; set; } = "";
        public string SourcePartCacheHash { get; set; } = "";
        public string InstalledPartCacheHash { get; set; } = "";
        public List<ColonyTemplate> Templates { get; set; } = new List<ColonyTemplate>();
        public List<ColonyTemplateCatalogIssue> Issues { get; set; } = new List<ColonyTemplateCatalogIssue>();
    }

    // Read-only qualification of administrator-installed package data. No caller
    // supplies a path, no part prefab is modified, and static checks cannot create
    // a runtime certificate. Cache this snapshot only within the same loaded part
    // database; re-load it after a package/scene installation-context change.
    public static class ColonyTemplateCatalog
    {
        private const int MaximumCatalogBytes = 4 * 1024 * 1024;
        private const int MaximumManifestBytes = 1024 * 1024;

        public static ColonyTemplateCatalogSnapshot LoadInstalled()
        {
            var result = new ColonyTemplateCatalogSnapshot();
            try
            {
                string application = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = Path.Combine(application, "GameData", "ExpanseWorldBridge", "Templates");
                string path = TrustedPath(root, "colony-template-catalog.json");
                byte[] bytes = ReadBounded(path, MaximumCatalogBytes);
                result.PackageHash = ColonyPlacementRequest.Hash(bytes);
                var catalog = ColonyCatalogJson.Parse(bytes);
                Require(catalog.Object != null && catalog.Get("SchemaVersion").Integer() == 1, "Unsupported colony template catalog schema");
                result.SourcePartCacheHash = catalog.Get("PartConfigurationHash").String();
                Require(ColonyPlacementRequest.Sha(result.SourcePartCacheHash), "Catalog source cache SHA-256 is missing");
                var permittedSourceHashes = ReadSourcePartCacheHashes(catalog);
                var entries = catalog.Get("Templates").Array;
                Require(entries != null && entries.Count > 0 && entries.Count <= 128, "Template catalog exceeds its 1–128 package bound");
                var requestedParts = new HashSet<string>(entries.SelectMany(e => e.Get("RequiredPartNames").Array.Select(p => p.String())), StringComparer.Ordinal);
                Require(requestedParts.Count > 0 && requestedParts.Count <= 4096, "Catalog selected-part set exceeds its bound");
                string cachePath = TrustedPath(Path.Combine(application, "GameData"), "ModuleManager.ConfigCache");
                var cacheInfo = new FileInfo(cachePath); Require(cacheInfo.Exists && cacheInfo.Length <= 64 * 1024 * 1024, "Installed MM cache is missing or exceeds 64 MiB bound");
                using (var stream = File.OpenRead(cachePath)) using (var sha = System.Security.Cryptography.SHA256.Create())
                    result.InstalledPartCacheHash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                var installedConfigs = ReadSelectedCacheParts(cachePath, requestedParts);
                var compilerConfig = ReadCacheConfiguration(cachePath, "KISConfig");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    string id = "unreadable";
                    try
                    {
                        id = entry.Get("Id").String();
                        Require(SafeId(id) && ids.Add(id), "Template ID is invalid or duplicated");
                        byte[] manifestBytes = ReadBounded(TrustedPath(root, id + ".manifest.json"), MaximumManifestBytes);
                        var manifest = ColonyCatalogJson.Parse(manifestBytes);
                        ColonyTemplate template = DecodeManifest(manifest);
                        // Hash every entry too: mismatching displayed quote/manifest
                        // terms cannot be hidden behind an unchanged Hash field.
                        Require(ManifestHash(entry) == template.Hash && entry.Get("Hash").String() == template.Hash && template.Id == id,
                            "Catalog entry disagrees with its individual manifest");
                        Require(permittedSourceHashes.Contains(template.PartConfigurationHash), "Template source part-cache provenance is not explicitly listed in catalog");
                        Require(manifest.Get("RuntimeCompilerInputs").Get("KisConfigurationSha256").String() == ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(ConfigTerms(compilerConfig))), "Installed KIS compiler settings changed after the reviewed package");
                        ValidateKisConfiguration(compilerConfig);
                        var craftBytes = ReadBounded(TrustedPath(root, template.CraftRelativePath), MaximumManifestBytes);
                        Require(ColonyPlacementRequest.Hash(craftBytes) == template.CraftSha256, "Installed craft bytes changed after the reviewed manifest");
                        ValidatePhysicalReferences(template, manifest, ConfigNode.Parse(new UTF8Encoding(false, true).GetString(craftBytes)), installedConfigs, compilerConfig);
                        result.Templates.Add(template);
                    }
                    catch (Exception ex) { result.Issues.Add(new ColonyTemplateCatalogIssue { TemplateId = id, Reason = ex.Message }); }
                }
            }
            catch (Exception ex) { result.Issues.Add(new ColonyTemplateCatalogIssue { Reason = ex.Message }); }
            return result;
        }

        // Useful for package tooling and audit tests; performs no KSP operations.
        // The returned candidate flag remains false. Physical checks happen only
        // in LoadInstalled against loaded current installed definitions/prefabs.
        public static ColonyTemplate ReadManifest(byte[] bytes)
        {
            Require(bytes != null && bytes.Length > 0 && bytes.Length <= MaximumManifestBytes, "Invalid manifest length");
            return DecodeManifest(ColonyCatalogJson.Parse(bytes));
        }

        internal static HashSet<string> ReadSourcePartCacheHashes(ColonyCatalogJson catalog)
        {
            string original = catalog.Get("PartConfigurationHash").String();
            Require(ColonyPlacementRequest.Sha(original), "Catalog source cache SHA-256 is missing");
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { original };
            if (catalog.Object.ContainsKey("AdditionalSourcePartCacheHashes"))
            {
                var extra = catalog.Get("AdditionalSourcePartCacheHashes").Array;
                Require(extra != null && extra.Count <= 128, "Additional source cache provenance requires an array of at most128 unique hashes");
                foreach (var row in extra)
                {
                    string hash = row.String();
                    Require(ColonyPlacementRequest.Sha(hash) && result.Add(hash), "Additional source cache provenance is invalid or duplicated");
                }
            }
            return result;
        }

        private static ColonyTemplate DecodeManifest(ColonyCatalogJson manifest)
        {
            Require(manifest.Object != null, "Template manifest must be a JSON object");
            string expected = manifest.Get("Hash").String();
            Require(ColonyPlacementRequest.Sha(expected) && expected == ManifestHash(manifest), "Template manifest SHA-256 does not match its complete reviewed terms");
            var template = (ColonyTemplate)manifest.Decode(typeof(ColonyTemplate));
            Require(SafeId(template.Id) && template.Version == 1 && template.ExpectedPartCount > 0 && template.ExpectedPartCount <= ColonyPlacementRecovery.MaximumParts, "Invalid template identity/version/part count");
            Require(ColonyPlacementRequest.Sha(template.CraftSha256) && ColonyPlacementRequest.Sha(template.PartConfigurationHash), "Template craft/configuration SHA-256 missing");
            Require(ColonyPlacementRequest.Sha(manifest.Get("RuntimeCompilerInputs").Get("KisConfigurationSha256").String()), "Template runtime KIS compiler provenance is missing");
            Require(template.BuildFunds >= 0 && template.BuildFunds <= ColonyLimits.MaxFunds && ColonyPlacementRequest.Range(template.LaborSeconds, 1, 1000000000), "Template price/labor terms exceed supported bounds");
            Require(template.LaborFunds > 0 && template.LaborFunds <= template.BuildFunds, "Template lacks explicitly priced construction labor");
            Require(template.Homes >= 0 && template.Homes <= ColonyLimits.Residents && template.Workers >= 0 && template.Workers <= ColonyLimits.Residents, "Template nominal home/worker limits invalid");
            Require(template.HomeCraftPartIds != null && template.HomeCraftPartIds.Count <= template.ExpectedPartCount && template.HomeCraftPartIds.All(id => id != 0) && template.HomeCraftPartIds.Distinct().Count() == template.HomeCraftPartIds.Count && (template.Homes == 0 || template.HomeCraftPartIds.Count > 0), "Template housing must name explicit craft parts; work/control seats cannot infer homes");
            Require(template.WidthMeters == template.MaxX - template.MinX && template.LengthMeters == template.MaxZ - template.MinZ, "Template street dimensions disagree with its deployment envelope");
            Require(template.StartupContents != null && template.StartupContents.Count <= 512 && template.StartupContents.All(c => c != null && c.Amount > 0 && c.Amount <= ColonyLimits.MaxQuantity), "Invalid startup allocation quantities");
            var safety = new ColonyPlacementRequest
            {
                WorldId = "catalog", ColonyId = "catalog", PlotId = "catalog", OperationId = "catalog", FacilityName = template.Name,
                TemplateRelativePath = template.CraftRelativePath, TemplateSha256 = template.CraftSha256, CertificationId = template.CertificationId,
                SurveyRevision = "catalog-envelope-only", EscrowWitness = "no-debit-static-catalog-check", BodyName = "Kerbin",
                MinX = template.MinX, MaxX = template.MaxX, MinZ = template.MinZ, MaxZ = template.MaxZ, MaximumHeight = template.MaximumHeight,
                MaximumSlopeDegrees = template.MaximumSlopeDegrees, MaximumSupportGapMetres = template.MaximumSupportGapMetres, ClearanceMetres = template.ClearanceMetres,
                TemplateRotationX = template.TemplateRotationX, TemplateRotationY = template.TemplateRotationY, TemplateRotationZ = template.TemplateRotationZ, TemplateRotationW = template.TemplateRotationW,
                Contents = template.StartupContents.Select(c => new ColonyPlacementContent { CraftPartId = c.CraftPartId, ResourceName = c.ResourceName, Amount = c.Amount / (double)ColonyLimits.Units }).ToArray()
            };
            string error = safety.Validate(); Require(error == null, error);
            Require(template.Materials != null && template.Materials.Count > 0, "Template construction BOM missing");
            ValidateRequirements(template.Materials); ValidateRequirements(template.EmbeddedContents);
            var aggregate = template.StartupContents.GroupBy(c => c.ResourceName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount), StringComparer.Ordinal);
            Require(aggregate.Count == template.EmbeddedContents.Count && template.EmbeddedContents.All(r => aggregate.ContainsKey(r.Resource) && aggregate[r.Resource] == r.Amount), "Per-part startup allocations do not exactly equal the once-only embedded charge");
            Require(template.RequiredPartNames != null && template.RequiredPartNames.Count > 0 && template.RequiredPartNames.Count <= 256 && template.RequiredPartNames.Distinct(StringComparer.Ordinal).Count() == template.RequiredPartNames.Count,
                "Required installed part list is missing or duplicated");
            Require(template.RequiredTech != null && template.RequiredTech.Count <= 256 && template.RequiredTech.All(t => ColonyPlacementRequest.Token(t, 80)) && template.RequiredTech.Distinct(StringComparer.Ordinal).Count() == template.RequiredTech.Count,
                "Required technology list is invalid");
            if (template.RuntimeCertified)
                Require(!template.CertificationId.EndsWith("-candidate", StringComparison.Ordinal) && ColonyPlacementRequest.Token(template.CertificationEvidence, 4096), "A candidate cannot claim runtime certification; publish root-accepted certificate evidence first");
            return template;
        }

        private static void ValidateRequirements(List<MaterialRequirement> requirements)
        {
            Require(requirements != null && requirements.Count <= ColonyLimits.Resources && requirements.All(r => r != null && ColonyPlacementRequest.Token(r.Resource, 80) && r.Amount > 0 && r.Amount <= ColonyLimits.MaxQuantity) &&
                requirements.Select(r => r.Resource).Distinct(StringComparer.Ordinal).Count() == requirements.Count, "Invalid/duplicated material or embedded charge rows");
        }

        private static void ValidatePhysicalReferences(ColonyTemplate template, ColonyCatalogJson manifest, ConfigNode craft, Dictionary<string, ConfigNode> installedConfigs, ConfigNode compilerConfig)
        {
            Require(craft != null, "Installed craft ConfigNode could not be parsed");
            var parts = craft.GetNodes("PART"); Require(parts.Length == template.ExpectedPartCount, "Actual craft part count disagrees with manifest");
            var raw = manifest.Get("RawParts").Array; Require(raw != null && raw.Count == parts.Length, "Every actual craft part requires an installed configuration witness");
            var byId = raw.ToDictionary(p => checked((uint)p.Get("CraftPartId").Integer()));
            var partNames = new HashSet<string>(StringComparer.Ordinal); var tech = new HashSet<string>(StringComparer.Ordinal);
            var tanks = new Dictionary<string, double>(StringComparer.Ordinal); var ids = new HashSet<uint>();
            foreach (var part in parts)
            {
                string key = part.GetValue("part"); int separator = key == null ? -1 : key.IndexOf('_'); uint id;
                Require(separator > 0 && uint.TryParse(key.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0, "Craft key does not follow native dotted part-name/numeric-ID syntax");
                id = uint.Parse(key.Substring(separator + 1), CultureInfo.InvariantCulture);
                Require(ids.Add(id) && byId.ContainsKey(id), "Actual craft identity is duplicated or missing from its manifest");
                var info = PartLoader.getPartInfoByName(key.Substring(0, separator));
                Require(info != null && info.partPrefab != null && info.partConfig != null, "Current installed part is unavailable: " + key);
                Require(info.partPrefab.Modules.OfType<ColonyPlacementMarker>().Count() == 1, "Required ColonyPlacement.cfg marker is absent or duplicated: " + key);
                string configName = byId[id].Get("PartName").String(), expected = byId[id].Get("PartConfigSha256").String(); ConfigNode original;
                Require(info.name == configName.Replace('_', '.') && installedConfigs.TryGetValue(configName, out original), "Installed part alias resolves to a different configuration: craft=" + key + "; loaded=" + info.name + "; expected=" + configName);
                original = installedConfigs[configName];
                string actual = ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(ConfigTerms(original)));
                Require(ColonyPlacementRequest.Sha(expected) && actual == expected, "Patched installed part changed: " + configName + "; expected=" + expected + "; actual=" + actual);
                ValidateCompilerConsumption(original, info, compilerConfig);
                partNames.Add(configName); tech.Add(info.TechRequired);
                Require(!part.HasValue("crew") && part.GetValue("persistentId") == "0", "Template retains crew or a prior persistent identity");
                foreach (var resource in part.GetNodes("RESOURCE"))
                {
                    string name = resource.GetValue("name"); double amount, capacity;
                    Require(ColonyPlacementRequest.Token(name, 80) && double.TryParse(resource.GetValue("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) && amount == 0 &&
                        double.TryParse(resource.GetValue("maxAmount"), NumberStyles.Float, CultureInfo.InvariantCulture, out capacity) && ColonyPlacementRequest.Range(capacity, 0, 1000000000), "Template tank has nonzero defaults or invalid capacity");
                    capacity = double.Parse(resource.GetValue("maxAmount"), CultureInfo.InvariantCulture);
                    tanks.Add(id.ToString(CultureInfo.InvariantCulture) + ":" + name, capacity);
                }
            }
            Require(partNames.SetEquals(template.RequiredPartNames) && tech.SetEquals(template.RequiredTech), "Installed required parts/technologies disagree with the reviewed manifest");
            Require(template.HomeCraftPartIds.All(ids.Contains), "Manifest home identity is absent from the actual craft");
            foreach (var content in template.StartupContents)
            {
                double capacity; string key = content.CraftPartId.ToString(CultureInfo.InvariantCulture) + ":" + content.ResourceName;
                Require(tanks.TryGetValue(key, out capacity) && content.Amount <= checked((long)Math.Round(capacity * ColonyLimits.Units)), "Paid startup allocation has no matching physical tank capacity: " + key);
            }
            var declared = manifest.Get("ResourceCapacities").Array;
            Require(declared != null && declared.Count == tanks.Count, "Manifest capacity rows disagree with actual craft tanks");
            var capacityKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in declared)
            {
                string key = row.Get("CraftPartId").Integer().ToString(CultureInfo.InvariantCulture) + ":" + row.Get("ResourceName").String(); double capacity;
                Require(capacityKeys.Add(key) && tanks.TryGetValue(key, out capacity) && PhysicalTankCapacityMatches(row.Get("Capacity").Integer(), capacity), "Manifest tank capacity/identity disagrees with actual craft: " + key);
            }
        }

        private static long PhysicalTankUnits(double capacity)
        {
            Require(!double.IsNaN(capacity) && !double.IsInfinity(capacity) && capacity >= 0 && capacity <= 1000000000, "Invalid physical tank capacity");
            long units = checked((long)Math.Floor(capacity * ColonyLimits.Units));
            if (units / (double)ColonyLimits.Units > capacity) units--;
            return units;
        }

        private static bool PhysicalTankCapacityMatches(long declared, double capacity)
        {
            long conservative = PhysicalTankUnits(capacity);
            // Legacy catalogs retain their exact paid terms; strict allocation
            // still rejects any native overfill. No tolerance or clamping.
            return declared == conservative || declared == checked((long)Math.Round(capacity * ColonyLimits.Units));
        }

        private static void ValidateCompilerConsumption(ConfigNode original, AvailablePart actual, ConfigNode compilerConfig)
        {
            // Installed stock PartLoader calls this exact routine with
            // removeAfterUse=true. Its retained config no longer has identity,
            // tech/cost/category/title/etc. Reproduce consumption on a detached
            // metadata object, never on a prefab or game object, then compare
            // every remaining config term and every consumed present field.
            AvailablePart expected; var remaining = CompilerConfiguration(original, compilerConfig, out expected);
            ShipConstruction.SanitizePartCosts(expected, remaining);
            ValidateCompiledConfiguration(remaining, actual.partConfig, expected.name);
            foreach (var field in typeof(AvailablePart).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // Source hashes still bind all editorial fields. Loaded search
                // tags/localized descriptions/icons/categories are changed by
                // KSP and other editor helpers; compare economic/runtime fields.
                if (!original.HasValue(field.Name) || !RuntimeMetadata.Contains(field.Name)) continue;
                object left = field.GetValue(expected), right = field.GetValue(actual);
                Require(Equals(left, right), "Loaded part metadata changed: " + expected.name + "." + field.Name + "; expected=" + Convert.ToString(left, CultureInfo.InvariantCulture) + "; actual=" + Convert.ToString(right, CultureInfo.InvariantCulture));
            }
            if (original.HasValue("entryCost")) Require(AvailablePart._GetEntryCost(expected) == AvailablePart._GetEntryCost(actual), "Loaded raw entry cost disagrees with installed config: " + expected.name);
        }

        private static void ValidateCompiledConfiguration(ConfigNode expected, ConfigNode actual, string name)
        {
            Require(ConfigTerms(expected) == ConfigTerms(actual), "Loaded compiler-normalized part terms changed: " + name + "; expected=" + ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(ConfigTerms(expected))) + "; actual=" + ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(ConfigTerms(actual))));
        }

        private static readonly HashSet<string> RuntimeMetadata = new HashSet<string>(new[]
        { "name", "TechRequired", "TechHidden", "identicalParts", "amountAvailable", "cost", "mapActionsToSymmetryParts", "preferredStage" }, StringComparer.Ordinal);

        private static ConfigNode CompilerConfiguration(ConfigNode original, ConfigNode compilerConfig, out AvailablePart expected)
        {
            var remaining = original.CreateCopy(); expected = new AvailablePart();
            // GameDatabase parses with localization enabled. This also decodes
            // literal \\n/\\t/\\\" throughout physics curves and module strings.
            if (KSP.Localization.Localizer.Instance != null) KSP.Localization.Localizer.TranslateBranch(remaining);
            else DecodeStockEscapes(remaining); // detached native audit only
            ConfigNode.LoadObjectFromConfig(expected, remaining, 0, true);
            string entryCost = remaining.GetValue("entryCost");
            if (!string.IsNullOrEmpty(entryCost)) expected.SetEntryCost(int.Parse(entryCost, CultureInfo.InvariantCulture));
            remaining.RemoveValues("entryCost"); expected.name = original.GetValue("name").Replace('_', '.');
            if (expected.description != null) expected.description = expected.description.Replace("\\n", "\n");
            foreach (var module in remaining.GetNodes("MODULE"))
                // Installed NearFutureElectrical.ModuleCoreHeatNoCatchup.OnLoad
                // invokes base.OnSave on the prefab, adding this zero clock.
                // Every heat setting remains exact; nonzero time is rejected.
                if (module.GetValue("name") == "ModuleCoreHeatNoCatchup" && !module.HasValue("lastUpdateTime")) module.AddValue("lastUpdateTime", 0);
            int seats = 0; string capacity = original.GetValue("CrewCapacity");
            Require(capacity == null || int.TryParse(capacity, NumberStyles.None, CultureInfo.InvariantCulture, out seats) && seats >= 0 && seats <= 64, "Invalid compiled crew capacity");
            var eva = compilerConfig.GetNode("EvaInventory"); Require(eva != null, "KIS seat inventory compiler settings missing");
            int configured = remaining.GetNodes("MODULE").Count(m => m.GetValue("name") == "ModuleKISInventory" && m.GetValue("invType") == "Pod");
            Require(configured <= seats, "Preconfigured KIS pod inventories exceed seat count");
            for (int seat = configured; seat < seats; seat++)
            {
                var pod = new ConfigNode("MODULE"); eva.CopyTo(pod); pod.name = "MODULE";
                pod.SetValue("name", "ModuleKISInventory", true); pod.SetValue("invType", "Pod", true); pod.SetValue("podSeat", seat, true);
                remaining.AddNode(pod);
            }
            return remaining;
        }

        private static void DecodeStockEscapes(ConfigNode node)
        {
            foreach (ConfigNode.Value value in node.values) value.value = value.value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\t", "\t");
            foreach (ConfigNode child in node.nodes) DecodeStockEscapes(child);
        }

        private static void ValidateKisConfiguration(ConfigNode source)
        {
            var loaded = GameDatabase.Instance.GetConfigNodes("KISConfig"); Require(loaded.Length == 1, "Loaded KIS compiler configuration is ambiguous or absent");
            var expected = source.CreateCopy(); KSP.Localization.Localizer.TranslateBranch(expected);
            Require(ConfigTerms(expected) == ConfigTerms(loaded[0]), "Loaded KIS compiler configuration differs from its reviewed source");
            var inventory = source.GetNode("Editor")?.GetNode("PodInventory");
            Require(inventory != null && inventory.values.Count == 0 && inventory.nodes.Count == 0, "KIS automatic seat contents require explicit paid allocation qualification");
            Require(source.GetNode("EvaInventory") != null && source.GetNode("EvaInventory").nodes.Count == 0, "Unexpected executable KIS seat compiler terms");
        }

        private static ConfigNode ReadCacheConfiguration(string path, string tag)
        {
            int depth = 0; StringBuilder captured = null; ConfigNode result = null;
            using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Require(line.Length <= 65536, "MM compiler input line exceeds bound"); string text = line.Trim();
                    if (captured == null && depth == 1 && text == tag) captured = new StringBuilder();
                    if (captured != null) { captured.Append(line).Append('\n'); Require(captured.Length <= MaximumManifestBytes, "MM compiler input exceeds 1 MiB"); }
                    if (text == "{") depth++; else if (text == "}") depth--;
                    Require(depth >= 0 && depth <= 64, "MM compiler input structure exceeds bounded depth");
                    if (captured != null && text == "}" && depth == 1)
                    {
                        Require(result == null, "Duplicate MM compiler input " + tag); result = ConfigNode.Parse(captured.ToString()).GetNode(tag); captured = null;
                    }
                }
            }
            Require(depth == 0 && captured == null && result != null, "Missing MM compiler input " + tag); return result;
        }

        // Stream the bounded cache once, parsing ONLY referenced PART blocks.
        // KSP's ConfigCache contains UrlConfig wrappers; values can contain braces
        // but syntax braces occupy their own lines. Duplicate native aliases are
        // ambiguous even when their unnormalized source names differ.
        private static Dictionary<string, ConfigNode> ReadSelectedCacheParts(string path, HashSet<string> wanted)
        {
            var result = new Dictionary<string, ConfigNode>(StringComparer.Ordinal); var aliases = new HashSet<string>(StringComparer.Ordinal);
            int depth = 0; long selectedCharacters = 0; StringBuilder part = null; string name = null;
            using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Require(line.Length <= 65536, "MM cache line exceeds bounded length"); string text = line.Trim();
                    if (part == null && depth == 1 && text == "PART") { part = new StringBuilder(); name = null; }
                    if (part != null)
                    {
                        part.Append(line).Append('\n'); Require(part.Length <= MaximumManifestBytes, "MM PART definition exceeds 1 MiB bound");
                        if (depth == 2 && name == null)
                        { int equal = text.IndexOf('='); if (equal > 0 && text.Substring(0, equal).Trim() == "name") name = text.Substring(equal + 1).Trim(); }
                    }
                    if (text == "{") depth++; else if (text == "}") depth--;
                    Require(depth >= 0 && depth <= 64, "MM cache structure exceeds bounded depth");
                    if (part != null && text == "}" && depth == 1)
                    {
                        if (name != null)
                        {
                            string alias = name.Replace('_', '.'); bool unique = aliases.Add(alias);
                            if (wanted.Any(n => n.Replace('_', '.') == alias)) Require(unique, "MM cache has duplicate native part alias: " + alias);
                            if (wanted.Contains(name))
                            {
                                var node = ConfigNode.Parse(part.ToString()).GetNode("PART"); Require(node != null && node.GetValue("name") == name && !result.ContainsKey(name), "Selected MM PART is ambiguous");
                                selectedCharacters += part.Length; Require(selectedCharacters <= 16 * 1024 * 1024, "Selected MM PART definitions exceed 16 MiB bound"); result.Add(name, node);
                            }
                        }
                        part = null; name = null;
                    }
                }
            }
            Require(depth == 0 && part == null && wanted.SetEquals(result.Keys), "Installed MM cache does not contain every selected current PART"); return result;
        }

        // Same exact ConfigNode text contract as the source package generator:
        // insertion order, grouped repeated value keys, tabs, LF and no final LF.
        // Exclude only our inert marker addition; any other patched change holds.
        private static string ConfigTerms(ConfigNode node, int depth = 0)
        {
            var b = new StringBuilder(); string tab = new string('\t', depth);
            b.Append(tab).Append(node.name).Append('\n').Append(tab).Append("{\n");
            foreach (var group in node.values.Cast<ConfigNode.Value>().GroupBy(v => v.name, StringComparer.Ordinal))
                foreach (var value in group) b.Append(tab).Append('\t').Append(value.name.Trim()).Append(" = ").Append(value.value.Trim()).Append('\n');
            foreach (ConfigNode child in node.nodes)
            {
                if (depth == 0 && child.name == "MODULE" && child.GetValue("name") == nameof(ColonyPlacementMarker))
                {
                    Require(child.nodes.Count == 0 && child.values.Count == 1 && child.GetValues("name").Length == 1, "Colony placement marker config has unexpected executable/default terms");
                    continue;
                }
                b.Append(ConfigTerms(child, depth + 1)).Append('\n');
            }
            return b.Append(tab).Append('}').ToString();
        }

        private static string ManifestHash(ColonyCatalogJson manifest)
        { return ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(manifest.Canonical(true))); }
        private static bool SafeId(string id)
        { return id != null && id.Length > 0 && id.Length <= 80 && id.All(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-'); }
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason ?? "Invalid colony package"); }
        private static byte[] ReadBounded(string path, int maximum)
        { var info = new FileInfo(path); Require(info.Exists && info.Length > 0 && info.Length <= maximum, "Package file missing/oversized: " + info.Name); byte[] bytes = File.ReadAllBytes(path); Require(bytes.Length <= maximum, "Package file grew beyond its bound"); return bytes; }
        private static string TrustedPath(string root, string relative)
        {
            Require(!string.IsNullOrEmpty(relative) && relative.Length <= 240 && !Path.IsPathRooted(relative) && !relative.Contains(":") && relative.Split('/', '\\').All(p => p.Length > 0 && p != "." && p != ".."), "Package path leaves trusted template root");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            Require(path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Package path leaves trusted template root");
            for (string ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                Require(!(File.Exists(ancestor) || Directory.Exists(ancestor)) || (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0, "Package reparse path refused");
            return path;
        }
    }

    // Bounded JSON reader retaining number/string lexemes. Python's package
    // canonical form sorts object keys and removes whitespace, with original
    // exact JSON numeric tokens (not a locale/rounding-dependent double rewrite).
    // Duplicate decoded keys, invalid Unicode, depth and graph overflows fail.
    internal sealed class ColonyCatalogJson
    {
        internal Dictionary<string, ColonyCatalogJson> Object;
        internal List<ColonyCatalogJson> Array;
        private string atom, decoded;
        private string keyToken;
        internal ColonyCatalogJson Get(string key)
        { ColonyCatalogJson value; if (Object == null || !Object.TryGetValue(key, out value)) throw new InvalidDataException("Missing manifest field " + key); return value; }
        internal string String() { if (atom == null || !atom.StartsWith("\"", StringComparison.Ordinal)) throw new InvalidDataException("Expected manifest string"); return decoded; }
        internal long Integer() { long n; if (!long.TryParse(atom, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) throw new InvalidDataException("Expected exact bounded manifest integer"); return n; }
        internal string Canonical(bool blankHash = false)
        {
            if (Object != null) return "{" + string.Join(",", Object.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value.keyToken + ":" + (blankHash && p.Key == "Hash" ? "\"\"" : p.Value.Canonical()))) + "}";
            if (Array != null) return "[" + string.Join(",", Array.Select(v => v.Canonical())) + "]";
            return atom;
        }
        internal object Decode(Type type)
        {
            if (type == typeof(string)) return String();
            if (type == typeof(bool)) { if (atom != "true" && atom != "false") throw new InvalidDataException("Expected manifest boolean"); return atom == "true"; }
            if (type == typeof(double)) { double n; if (!double.TryParse(atom, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || !ColonyPlacementRequest.Finite(n)) throw new InvalidDataException("Invalid manifest numeric term"); return n; }
            if (type == typeof(int)) return checked((int)Integer());
            if (type == typeof(uint)) return checked((uint)Integer());
            if (type == typeof(long)) return Integer();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            { if (Array == null) throw new InvalidDataException("Expected manifest array"); var list = (IList)Activator.CreateInstance(type); foreach (var value in Array) list.Add(value.Decode(type.GetGenericArguments()[0])); return list; }
            if (Object == null) throw new InvalidDataException("Expected manifest record");
            object target = Activator.CreateInstance(type);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite)) property.SetValue(target, Get(property.Name).Decode(property.PropertyType), null);
            return target;
        }
        internal static ColonyCatalogJson Parse(byte[] bytes)
        { var reader = new Reader(new UTF8Encoding(false, true).GetString(bytes)); var result = reader.Value(0); reader.White(); if (reader.Position != reader.Text.Length) throw new InvalidDataException("Trailing JSON package data"); return result; }
        private sealed class Reader
        {
            internal readonly string Text; internal int Position; private int count;
            internal Reader(string text) { Text = text; }
            internal void White() { while (Position < Text.Length && (Text[Position] == ' ' || Text[Position] == '\t' || Text[Position] == '\r' || Text[Position] == '\n')) Position++; }
            private void Need(char c) { White(); if (Position >= Text.Length || Text[Position++] != c) throw new InvalidDataException("Malformed JSON package structure"); }
            internal ColonyCatalogJson Value(int depth)
            {
                if (depth > 32 || ++count > 100000) throw new InvalidDataException("JSON package graph exceeds bounded depth/items");
                White(); if (Position >= Text.Length) throw new InvalidDataException("Truncated JSON package");
                var result = new ColonyCatalogJson(); char c = Text[Position];
                if (c == '{')
                {
                    Position++; White(); result.Object = new Dictionary<string, ColonyCatalogJson>(StringComparer.Ordinal);
                    if (Position < Text.Length && Text[Position] == '}') { Position++; return result; }
                    while (true)
                    {
                        var key = Quoted(); Need(':'); var value = Value(depth + 1); value.keyToken = key.atom;
                        if (result.Object.ContainsKey(key.decoded)) throw new InvalidDataException("Duplicate decoded JSON package key"); result.Object.Add(key.decoded, value);
                        White(); if (Position < Text.Length && Text[Position] == '}') { Position++; break; } Need(',');
                    }
                }
                else if (c == '[')
                {
                    Position++; White(); result.Array = new List<ColonyCatalogJson>();
                    if (Position < Text.Length && Text[Position] == ']') { Position++; return result; }
                    while (true) { result.Array.Add(Value(depth + 1)); White(); if (Position < Text.Length && Text[Position] == ']') { Position++; break; } Need(','); }
                }
                else if (c == '"') result = Quoted();
                else
                {
                    int begin = Position;
                    while (Position < Text.Length && ",]} \r\n\t".IndexOf(Text[Position]) < 0) Position++;
                    result.atom = Text.Substring(begin, Position - begin);
                    if (result.atom != "true" && result.atom != "false" && result.atom != "null" && !ValidNumber(result.atom)) throw new InvalidDataException("Invalid JSON package atom");
                }
                return result;
            }
            private ColonyCatalogJson Quoted()
            {
                White(); int begin = Position; Need('"'); var b = new StringBuilder(); bool ended = false;
                while (Position < Text.Length)
                {
                    char c = Text[Position++]; if (c == '"') { ended = true; break; }
                    if (c < 32) throw new InvalidDataException("Control character in JSON package string");
                    if (c == '\\')
                    {
                        if (Position >= Text.Length) throw new InvalidDataException("Truncated JSON package escape");
                        c = Text[Position++];
                        if (c == 'u')
                        { if (Position + 4 > Text.Length) throw new InvalidDataException("Truncated Unicode escape"); c = (char)int.Parse(Text.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); Position += 4; }
                        else { int k = "\"\\/bfnrt".IndexOf(c); if (k < 0) throw new InvalidDataException("Invalid JSON escape"); c = "\"\\/\b\f\n\r\t"[k]; }
                    }
                    b.Append(c); if (b.Length > 65536) throw new InvalidDataException("Oversized JSON package string");
                }
                if (!ended) throw new InvalidDataException("Unclosed JSON package string");
                string decoded = b.ToString();
                for (int i = 0; i < decoded.Length; i++) if (char.IsSurrogate(decoded[i])) { if (!char.IsHighSurrogate(decoded[i]) || i + 1 >= decoded.Length || !char.IsLowSurrogate(decoded[++i])) throw new InvalidDataException("Invalid JSON Unicode surrogate"); }
                return new ColonyCatalogJson { atom = Text.Substring(begin, Position - begin), decoded = decoded };
            }
            private static bool ValidNumber(string text)
            {
                int i = 0; if (text.Length > 128 || text.Length == 0) return false; if (text[i] == '-') { if (++i == text.Length) return false; }
                if (text[i] == '0') i++; else { if (text[i] < '1' || text[i] > '9') return false; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; }
                if (i < text.Length && text[i] == '.') { int start = ++i; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; if (start == i) return false; }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E')) { i++; if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++; int start = i; while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++; if (start == i) return false; }
                return i == text.Length;
            }
        }
    }
}
