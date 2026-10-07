using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace Expanse.Colony.KspHarness
{
    // Not shipped. Drives stock save/scene/warp APIs in the authorized disposable
    // game, so acceptance can exercise background operation without a live Host.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class ColonyDevelopmentControl : MonoBehaviour
    {
        const string Root = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
        float next;
        void Start()
        {
            if (!Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/').Equals(Root, StringComparison.OrdinalIgnoreCase)) { Destroy(this); return; }
            DontDestroyOnLoad(gameObject);
        }
        void Update()
        {
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + .5f;
            if (!Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd('\\', '/').Equals(Root, StringComparison.OrdinalIgnoreCase)) return;
            string input = Path.Combine(Root, "colony-control.cfg");
            if (!File.Exists(input)) return;
            string operation = "", output = "";
            try
            {
                PrivatePath(input); if (new FileInfo(input).Length > 8192) throw new InvalidDataException("Oversized test control request.");
                var command = ConfigNode.Load(input); Guid id;
                if (!Guid.TryParseExact(command.GetValue("operationId"), "D", out id) || id == Guid.Empty) throw new InvalidDataException("A new test operation UUID is required.");
                operation = id.ToString("D"); output = Path.Combine(Root, "colony-control-result-" + operation + ".txt");
                if (File.Exists(output)) throw new InvalidDataException("Test operation already has a result; inspect it before any retry.");
                string folder = HighLogic.SaveFolder;
                if (command.GetValue("authorization") != "verified-isolated-development-only" || !HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null ||
                    folder != command.GetValue("saveFolder") || !folder.StartsWith("ColonyBuild-", StringComparison.Ordinal) || folder != Path.GetFileName(folder)) throw new InvalidDataException("Wrong isolated game context.");
                string token = command.GetValue("token");
                Guid nonce; if (!Guid.TryParseExact(token, "N", out nonce)) throw new InvalidDataException("Test IPC token is invalid.");
                var args = Environment.GetCommandLineArgs();
                foreach (string prefix in new[] { "-expanseClockPublisherPipe=", "-expanseEffectsPipe=", "-expanseWolfPipe=", "-expanseColonyPipe=" })
                {
                    var matches = args.Where(a => a.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1 || !matches[0].Contains(".dev.") || !matches[0].EndsWith("." + token, StringComparison.Ordinal)) throw new InvalidDataException("Test process is not isolated on every IPC endpoint.");
                }
                PrivatePath(Path.Combine(Root, "saves", folder, "persistent.sfs"));
                // Claim before invoking the real API. A crash leaves a claimed
                // operation for inspection and can never blindly repeat it.
                File.Move(input, input + ".claimed-" + operation);
                string method = command.GetValue("method");
                if (method == "save")
                {
                    string name = SaveName(command); string path = Path.Combine(Root, "saves", folder, name + ".sfs"); PrivatePath(path);
                    if (File.Exists(path)) throw new InvalidDataException("Named test save already exists; use another name.");
                    GamePersistence.SaveGame(name, folder, SaveMode.OVERWRITE);
                    if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("Stock save did not produce a file.");
                    Write(output, "saved " + name + " sha256=" + Hash(path));
                }
                else if (method == "load")
                {
                    string name = SaveName(command); string path = Path.Combine(Root, "saves", folder, name + ".sfs"); PrivatePath(path);
                    if (Hash(path) != command.GetValue("sha256")) throw new InvalidDataException("Test save SHA does not match reviewed load request.");
                    Game game = GamePersistence.LoadGame(name, folder, true, false); if (game == null) throw new InvalidDataException("Stock load returned no game.");
                    GamePersistence.UpdateScenarioModules(game); HighLogic.CurrentGame = game; game.startScene = GameScenes.FLIGHT; game.Start();
                    Write(output, "load requested " + name + "; verify new selected-save epoch and physical markers");
                }
                else if (method == "spaceCenter")
                {
                    if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR)
                        throw new InvalidOperationException("Stock flight state is not clear to save and exit.");
                    // Mirror the stock PauseMenu save-and-exit boundary. A raw
                    // LoadScene reloads the old persistent file and is a revert.
                    GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);
                    Game leaving = HighLogic.CurrentGame.Updated();
                    var clearIds = typeof(FlightGlobals).GetMethod("ClearpersistentIdDictionaries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (clearIds == null) throw new MissingMethodException("Reviewed stock exit identity reset is unavailable.");
                    clearIds.Invoke(null, null);
                    if (GamePersistence.SaveGame(leaving, "persistent", folder, SaveMode.OVERWRITE) != "persistent")
                        throw new IOException("Stock persistent save did not succeed; scene was not changed.");
                    HighLogic.LoadScene(GameScenes.SPACECENTER);
                    Write(output, "stock saved exit to space center requested; verify scene and unloaded vessels");
                }
                else if (method == "warpTo")
                {
                    double target = double.Parse(command.GetValue("ut"), CultureInfo.InvariantCulture), now = Planetarium.GetUniversalTime();
                    if (double.IsNaN(target) || double.IsInfinity(target) || target <= now || target - now > 30 * 21600 || HighLogic.LoadedScene != GameScenes.SPACECENTER || TimeWarp.fetch == null)
                        throw new InvalidDataException("Bounded test warp requires Space Center and a target within thirty Kerbin days.");
                    TimeWarp.fetch.WarpTo(target); Write(output, "stock warp requested target=" + target.ToString("R", CultureInfo.InvariantCulture));
                }
                else if (method == "stopWarp") { if (TimeWarp.fetch != null) TimeWarp.fetch.CancelAutoWarp(); TimeWarp.SetRate(0, true); Write(output, "stock warp stopped"); }
                else if (method == "profileReset")
                {
                    var runtime = Expanse.WorldBridge.ColonyRuntime.Current;
                    if (runtime == null) throw new InvalidOperationException("No selected colony runtime to profile.");
                    runtime.ResetPerformanceSamples(); Write(output, "read-only runtime performance samples reset");
                }
                else if (method == "profileReport")
                {
                    var runtime = Expanse.WorldBridge.ColonyRuntime.Current;
                    if (runtime == null) throw new InvalidOperationException("No selected colony runtime to profile.");
                    string path = Path.Combine(Root, "colony-profile-" + operation + ".cfg"); PrivatePath(path);
                    if (File.Exists(path)) throw new InvalidDataException("This profile report already exists.");
                    runtime.CapturePerformanceSamples().Save(path);
                    Write(output, "read-only runtime performance report " + path);
                }
                else if (method == "screenshot") StartCoroutine(ColonyDevelopmentScreenshot.Capture(command, output));
                else if (method == "status") Write(output, "status");
                else throw new InvalidDataException("Unsupported development control method.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ExpanseColonyTestControl] " + operation + " " + ex);
                // Refused requests are moved out of the polling slot as evidence;
                // they cannot block the game by throwing on every frame.
                if (File.Exists(input)) File.Move(input, input + ".refused-" + DateTime.UtcNow.Ticks);
                if (output.Length != 0 && !File.Exists(output)) Write(output, "FAILED " + ex.Message);
            }
        }
        static string SaveName(ConfigNode command)
        {
            string name = command.GetValue("name");
            if (string.IsNullOrEmpty(name) || name.Length > 80 || !name.StartsWith("colony-test-", StringComparison.Ordinal) || name.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new InvalidDataException("Test saves require a bounded colony-test- name.");
            return name;
        }
        static void PrivatePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Test path leaves isolated root.");
            for (string item = full; item != null; item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Reparse path refused.");
        }
        static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static void Write(string path, string message)
        {
            string result = DateTime.UtcNow.ToString("o") + " " + message + "\nscene=" + HighLogic.LoadedScene + "\nsave=" + HighLogic.SaveFolder + "\nut=" + Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture);
            File.WriteAllText(path, result); Debug.Log("[ExpanseColonyTestControl] " + result.Replace('\n', ' '));
        }
    }
}
