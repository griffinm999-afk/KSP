using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace Expanse.Colony.KspHarness
{
    // Development only. Loads a hash-verified fresh copy; does not build, fund,
    // populate, warp or change production. All those effects go through product APIs.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ColonyDevelopmentLoader : MonoBehaviour
    {
        private const string ApprovedRoot = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
        private static bool claimed;
        private void Start()
        {
            if (!Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/').Equals(ApprovedRoot, StringComparison.OrdinalIgnoreCase)) return;
            StartCoroutine(WaitForReadyScene());
        }
        private IEnumerator WaitForReadyScene()
        {
            float deadline = Time.realtimeSinceStartup + 90;
            int stableFrames = 0;
            while (Time.realtimeSinceStartup < deadline)
            {
                var sun = Sun.Instance;
                bool ready = HighLogic.LoadedScene == GameScenes.MAINMENU && PSystemManager.Instance != null &&
                    PSystemManager.Instance.localBodies != null && PSystemManager.Instance.localBodies.Count > 0 &&
                    sun != null && sun.sunFlare != null &&
                    typeof(Sun).GetField("lgt", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sun) != null &&
                    (!sun.useLocalSpaceSunLight || typeof(Sun).GetField("scaledSunLight", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sun) != null);
                stableFrames = ready ? stableFrames + 1 : 0;
                if (stableFrames >= 5) { LoadVerifiedCopy(); yield break; }
                yield return null;
            }
            Debug.LogError("[ExpanseColonyTest] Load refused: main-menu planetary scene never became ready.");
        }
        private void LoadVerifiedCopy()
        {
            string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/');
            if (!root.Equals(ApprovedRoot, StringComparison.OrdinalIgnoreCase) || claimed) return;
            try
            {
                string request = Path.Combine(root, "colony-load.cfg");
                if (!File.Exists(request)) return;
                CheckPath(request); if (new FileInfo(request).Length > 4096) throw new InvalidDataException("Oversized development load request.");
                ConfigNode config = ConfigNode.Load(request);
                if (config.GetValue("authorization") != "verified-isolated-development-only") throw new InvalidDataException("No isolated development authorization.");
                string folder = config.GetValue("saveFolder"), expected = config.GetValue("sha256");
                if (folder == null || !folder.StartsWith("ColonyBuild-", StringComparison.Ordinal) || folder.Length > 80 || folder.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Invalid disposable save folder.");
                string save = Path.Combine(root, "saves", folder, "persistent.sfs"); CheckPath(save);
                string actual;
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(save)) actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Fresh copied save hash mismatch.");
                if (!Environment.GetCommandLineArgs().Any(x => x.StartsWith("-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev.", StringComparison.Ordinal)) ||
                    !Environment.GetCommandLineArgs().Any(x => x.StartsWith("-expanseEffectsPipe=ExpanseFoundations.Effects.dev.", StringComparison.Ordinal)) ||
                    !Environment.GetCommandLineArgs().Any(x => x.StartsWith("-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev.", StringComparison.Ordinal))) throw new InvalidDataException("Development IPC namespace is incomplete.");
                claimed = true; File.Move(request, request + ".claimed-" + DateTime.UtcNow.Ticks);
                HighLogic.SaveFolder = folder;
                Game game = GamePersistence.LoadGame("persistent", folder, true, false);
                if (game == null || game.flightState == null || game.flightState.protoVessels == null || game.flightState.protoVessels.Count == 0) throw new InvalidDataException("Copied game has no flight state.");
                string reference = config.GetValue("referenceVesselId");
                if (!string.IsNullOrEmpty(reference))
                {
                    Guid id; if (!Guid.TryParse(reference, out id)) throw new InvalidDataException("Invalid reference vessel ID.");
                    int index = game.flightState.protoVessels.FindIndex(v => v.vesselID == id && v.situation == Vessel.Situations.LANDED);
                    if (index < 0) throw new InvalidDataException("Requested existing landed reference vessel is absent.");
                    game.flightState.activeVesselIdx = index;
                }
                game.Title = "Colony Integration — isolated copy";
                // Match the stock load UI: LoadGame itself does not add newly installed
                // AddToExistingGames scenario modules to a previously saved game.
                GamePersistence.UpdateScenarioModules(game);
                HighLogic.CurrentGame = game; game.startScene = GameScenes.FLIGHT; game.Start();
                Debug.Log("[ExpanseColonyTest] Loaded verified isolated copy folder=" + folder + " sha256=" + actual);
            }
            catch (Exception ex) { Debug.LogError("[ExpanseColonyTest] Load refused: " + ex); }
        }
        private static void CheckPath(string path)
        {
            string value = Path.GetFullPath(path);
            if (!value.StartsWith(ApprovedRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path leaves isolated installation.");
            for (string current = value; current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse path refused.");
        }
    }
}
