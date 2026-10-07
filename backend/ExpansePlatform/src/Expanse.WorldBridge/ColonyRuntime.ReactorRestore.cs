using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        static Type ReactorRestoreOuter, ReactorRestoreChild, ReactorRestoreConverter, ReactorRestoreProcessor, ReactorRestoreModule;
        static MethodInfo ReactorRestoreChildCall, ReactorRestoreRegisteredAdapter;

        // Called by the existing Startup.Instantly BRP bootstrap, before any
        // vessel restoration. The pinned provider's SelectFirst omits this
        // forwarding although its selected child owns native LastUpdateTime.
        internal static void RegisterReactorRestore(Harmony patch, Assembly brp)
        {
            var heat = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "SystemHeat").Take(2).ToArray();
            if (heat.Length == 0) return;
            if (heat.Length != 1 || heat[0].GetName().Version != new Version(0, 9, 1, 0)) throw new InvalidOperationException("Reactor restore SystemHeat identity differs.");
            using (var stream = File.OpenRead(heat[0].Location)) using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != "DF09ECA99DFEBF937A9FF0B25298C876F8049605F8D242C3551D4FED46910EB6")
                    throw new InvalidOperationException("Reactor restore SystemHeat content differs.");
            var parent = brp.GetType("BackgroundResourceProcessing.Converter.BackgroundConverter", true);
            var outer = brp.GetType("BackgroundResourceProcessing.Converter.SelectFirst", true);
            var child = brp.GetType("BackgroundResourceProcessing.Converter.BackgroundConstantConverter", true);
            var converter = brp.GetType("BackgroundResourceProcessing.Core.ResourceConverter", true);
            var inherited = outer.GetMethod("OnRestore", new[] { typeof(PartModule), converter });
            // Harmony needs the MethodInfo reflected on its declaring type;
            // an inherited MethodInfo reflected on SelectFirst is rejected.
            var target = parent.GetMethod("OnRestore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(PartModule), converter }, null);
            var leaf = child.GetMethod("OnRestore", new[] { typeof(PartModule), converter });
            if (target == null || target.DeclaringType != parent || target.ReflectedType != parent || target.ReturnType != typeof(void) || !target.IsVirtual ||
                inherited == null || inherited.DeclaringType != parent ||
                leaf == null || leaf.DeclaringType != child || leaf.ReturnType != typeof(void)) throw new MissingMethodException("Reviewed native reactor restore callbacks differ.");
            ReactorRestoreOuter = outer; ReactorRestoreChild = child; ReactorRestoreConverter = converter;
            ReactorRestoreProcessor = brp.GetType("BackgroundResourceProcessing.BackgroundResourceProcessor", true);
            ReactorRestoreModule = heat[0].GetType("SystemHeat.ModuleSystemHeatFissionReactor", true); ReactorRestoreChildCall = leaf;
            ReactorRestoreRegisteredAdapter = parent.GetMethod("GetConverterForModule", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(PartModule) }, null);
            if (ReactorRestoreRegisteredAdapter == null) throw new MissingMethodException("Native reactor adapter registry differs.");
            patch.Patch(target, postfix: new HarmonyMethod(typeof(ColonyRuntime).GetMethod(nameof(ForwardNativeReactorRestore), BindingFlags.Static | BindingFlags.NonPublic)));
            Debug.Log("[ExpanseColony] Native SelectFirst reactor restore forwarding installed at startup.");
        }

        static bool OwnsReactorRestore(ColonyRuntime runtime, PartModule module, object converter)
        {
            var game = HighLogic.CurrentGame; var vessel = module?.vessel; var part = module?.part;
            // Scenario membership binds cold-load authority before the first
            // environment observation assigns selectedGame. No saved IDs are rekeyed.
            if (runtime == null || !runtime.Ready || runtime.state == null || game == null || game.scenarios == null ||
                game.scenarios.Count(s => ReferenceEquals(s.moduleRef, runtime)) != 1 || runtime.selectedGame != null && !ReferenceEquals(runtime.selectedGame, game) ||
                vessel == null || !vessel.loaded || vessel.isEVA || !vessel.LandedOrSplashed || part == null || part.vessel != vessel ||
                vessel.parts == null || vessel.parts.Count == 0 || vessel.parts.Count > 256 || vessel.parts.Any(p => p == null || p.vessel != vessel || p.persistentId == 0) ||
                vessel.parts.Count(p => ReferenceEquals(p, part)) != 1 || part.Modules == null || part.Modules.Count > 128 ||
                part.Modules.Cast<PartModule>().Count(m => ReferenceEquals(m, module)) != 1 || part.flightID == 0) return false;
            var owners = runtime.state.Colonies.SelectMany(c => c.Facilities).Where(f => f.VesselId == vessel.id.ToString("D")).Take(2).ToArray();
            if (owners.Length != 1 || owners[0].ProductionOwner != "physical" || owners[0].PartIds == null) return false;
            var ids = owners[0].PartIds;
            if (ids.Count != vessel.parts.Count || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count ||
                vessel.parts.Select(p => p.persistentId).Distinct().Count() != ids.Count || !ids.OrderBy(i => i).SequenceEqual(vessel.parts.Select(p => p.persistentId).OrderBy(i => i))) return false;
            var flight = UtilityRead(converter, "FlightId"); var moduleId = UtilityRead(converter, "ModuleId");
            // BRP's uint PartModuleList indexer resolves stock persistent IDs,
            // not positions. Read the existing ID; never allocate a surrogate.
            if (!(flight is uint) || (uint)flight != part.flightID || !(moduleId is uint) || (uint)moduleId == 0 ||
                module.PersistentId != (uint)moduleId || part.Modules.Cast<PartModule>().Count(m => m != null && m.PersistentId == (uint)moduleId) != 1 ||
                vessel.parts.Count(p => p.flightID == part.flightID) != 1) return false;
            var processors = vessel.GetComponents(ReactorRestoreProcessor);
            if (processors.Length != 1) return false;
            var engine = ReviewedPrivateField(processors[0], "processor");
            var rows = engine == null ? null : UtilityRead(engine, "converters") as IEnumerable;
            if (rows == null) return false;
            var actual = rows.Cast<object>().Take(513).ToArray();
            return actual.Length <= 512 && actual.Count(c => ReferenceEquals(c, converter)) == 1 &&
                actual.Count(c => c != null && Equals(UtilityRead(c, "FlightId"), flight) && Equals(UtilityRead(c, "ModuleId"), moduleId)) == 1;
        }

        static bool NativeRestoreCondition(object condition, PartModule module)
        {
            if (condition == null || condition.GetType().Assembly != ReactorRestoreOuter.Assembly ||
                condition.GetType().FullName != "BackgroundResourceProcessing.Utils.ConditionalExpression") return false;
            return (bool)condition.GetType().GetMethod("Evaluate", new[] { typeof(PartModule) }).Invoke(condition, new object[] { module });
        }

        static void ForwardNativeReactorRestore(object __instance, PartModule module, object converter)
        {
            try
            {
                if (__instance == null || __instance.GetType() != ReactorRestoreOuter || module == null || module.GetType() != ReactorRestoreModule ||
                    converter == null || converter.GetType() != ReactorRestoreConverter || !OwnsReactorRestore(Current, module, converter)) return;
                ReadReactorProviderHash(); // Current installed configuration, not a cached qualification.
                if (!ReferenceEquals(ReactorRestoreRegisteredAdapter.Invoke(null, new object[] { module }), __instance) ||
                    !UtilityBool(module, "Enabled") || !UtilityBool(module, "ManualControl") ||
                    !NativeRestoreCondition(UtilityRead(__instance, "ActiveCondition"), module)) throw new InvalidOperationException("Owned reactor restore is not the reviewed active manual branch.");
                var options = ReviewedPrivateField(__instance, "options") as IList;
                if (options == null || options.Count != 4) throw new InvalidOperationException("Owned reactor restore branch inventory differs.");
                // The exact current manual branch is first. Evaluate its real
                // native condition; do not rebuild recipes or choose another branch.
                var selected = options[0]; var child = UtilityRead(selected, "converter");
                if (!NativeRestoreCondition(UtilityRead(selected, "condition"), module) || child == null || child.GetType() != ReactorRestoreChild ||
                    Convert.ToString(UtilityRead(child, "LastUpdateField"), CultureInfo.InvariantCulture) != "LastUpdateTime" ||
                    ReviewedPrivateField(child, "lastUpdateField") == null) throw new InvalidOperationException("Owned reactor restore selected native child differs.");
                ReactorRestoreChildCall.Invoke(child, new object[] { module, converter });
                Debug.Log("[ExpanseColony] Forwarded native reactor restore vessel=" + module.vessel.id.ToString("D") + " part=" + module.part.persistentId +
                    " UT=" + Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Debug.LogError("[ExpanseColony] Owned native reactor restore forwarding unavailable: " + Bound((ex.InnerException ?? ex).Message, 256));
            }
        }
    }
}
