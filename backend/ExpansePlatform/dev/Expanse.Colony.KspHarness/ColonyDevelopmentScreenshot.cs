using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Expanse.WorldBridge;
using UnityEngine;

namespace Expanse.Colony.KspHarness
{
    // Dev-only stock capture, after the existing exact isolated control claims
    // its operation. No camera, clock, vessel, scene, physics or save mutation.
    internal static class ColonyDevelopmentScreenshot
    {
        const string Root = @"C:\Users\griff\Documents\Codex\KSP-Colony-Demo";
        const string BridgeHash = "30dcb250680c952ad41d3b7090e1324a28fe4519926482b35344991a08b3b607";
        const int MaximumBytes = 32 * 1024 * 1024;
        internal static IEnumerator Capture(ConfigNode command, string output)
        {
            string image = "", evidence = "", queued = ""; ConfigNode context = null;
            Vessel target = null; string folder = "", epoch = "", active = "", placement = "", anchor = "", afterWitness = "", body = ""; uint[] parts = null;
            int width = 0, height = 0; float deadline = 0;
            Exception failed = null;
            try
            {
                Guid operation, vesselId;
                if (!Guid.TryParseExact(command.GetValue("operationId"), "D", out operation) || operation == Guid.Empty ||
                    !Guid.TryParseExact(command.GetValue("name"), "D", out vesselId) || vesselId == Guid.Empty)
                    throw new InvalidDataException("Screenshot requires exact operation and target vessel UUIDs.");
                if (Hash(typeof(ColonyRuntime).Assembly.Location) != BridgeHash || !HighLogic.LoadedSceneIsFlight || ColonyRuntime.Current == null || FlightGlobals.Vessels == null || FlightGlobals.Vessels.Count > 2048)
                    throw new InvalidOperationException("Screenshot requires the reviewed native14 loaded flight context.");
                target = FlightGlobals.Vessels.Single(v => v != null && v.id == vesselId);
                if (!target.loaded || target.parts == null || target.parts.Count < 1 || target.parts.Count > 512 || !target.parts.All(p => p != null && p.started))
                    throw new InvalidOperationException("Exact screenshot target is not completely loaded.");
                var markers = target.parts.SelectMany(p => p.Modules.OfType<ColonyPlacementMarker>()).ToArray();
                if (markers.Length != target.parts.Count || markers.Select(m => m.operationId).Distinct().Count() != 1)
                    throw new InvalidDataException("Screenshot target lacks exact product birth markers.");
                placement = markers[0].operationId; var receipt = ColonyPlacementScenario.Instance.GetStatus(placement);
                if (receipt == null || receipt.Stage != ColonyPlacementStage.Anchored || receipt.VesselId != target.id.ToString("D") || receipt.VesselPersistentId != target.persistentId)
                    throw new InvalidOperationException("Screenshot target lacks its exact anchored receipt.");
                anchor = receipt.FoundationId; afterWitness = receipt.AfterWitness; body = target.mainBody.bodyName; NativeAnchor(target, anchor);
                parts = target.parts.Select(p => p.persistentId).OrderBy(p => p).ToArray();
                folder = HighLogic.SaveFolder; epoch = ColonyRuntime.Current.ContextKey;
                active = FlightGlobals.ActiveVessel == null ? "" : FlightGlobals.ActiveVessel.id.ToString("D");
                width = Screen.width; height = Screen.height;
                if (width < 64 || height < 64 || width > 8192 || height > 8192) throw new InvalidOperationException("Native capture dimensions exceed bounds.");
                string directory = Path.Combine(Root, "colony-native-screenshots"); Private(directory); Directory.CreateDirectory(directory);
                image = Path.Combine(directory, operation.ToString("D") + ".png"); evidence = Path.Combine(directory, operation.ToString("D") + ".cfg"); queued = Path.Combine(directory, operation.ToString("D") + ".queued.cfg");
                foreach (string path in new[] { image, evidence, queued, output }) { Private(path); if (File.Exists(path)) throw new InvalidDataException("Screenshot output already exists; inspect the prior operation."); }
                context = new ConfigNode("COLONY_NATIVE_SCREENSHOT"); context.AddValue("operationId", operation); context.AddValue("status", "queued"); context.AddValue("processId", System.Diagnostics.Process.GetCurrentProcess().Id);
                context.AddValue("scene", HighLogic.LoadedScene); context.AddValue("saveFolder", folder); context.AddValue("contextKey", epoch); context.AddValue("body", target.mainBody.bodyName);
                context.AddValue("targetVesselId", target.id); context.AddValue("targetPersistentId", target.persistentId); context.AddValue("placementOperationId", placement); context.AddValue("foundationId", anchor);
                context.AddValue("activeVesselId", active); context.AddValue("targetPartPersistentIds", string.Join(",", parts)); context.AddValue("afterWitness", receipt.AfterWitness);
                context.AddValue("queuedUtc", DateTime.UtcNow.ToString("o")); context.AddValue("queuedUt", R(Planetarium.GetUniversalTime())); context.AddValue("width", width); context.AddValue("height", height);
                context.AddValue("nativeApi", "UnityEngine.ScreenCapture.CaptureScreenshot(path,1)"); context.AddValue("cameraChangedByHarness", false); context.AddValue("geometryQualification", false);
                CameraContext(context, "before"); File.WriteAllText(queued, context.ToString());
                deadline = Time.realtimeSinceStartup + 30; ScreenCapture.CaptureScreenshot(image, 1);
            }
            catch (Exception ex) { failed = ex; }
            if (failed != null) { Failure(output, context, failed); yield break; }
            byte[] completed = null; string firstHash = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                try
                {
                    if (!HighLogic.LoadedSceneIsFlight || HighLogic.SaveFolder != folder || ColonyRuntime.Current == null || ColonyRuntime.Current.ContextKey != epoch ||
                        (FlightGlobals.ActiveVessel == null ? "" : FlightGlobals.ActiveVessel.id.ToString("D")) != active || target == null || !target.loaded ||
                        target.mainBody == null || target.mainBody.bodyName != body || !target.parts.Select(p => p.persistentId).OrderBy(p => p).SequenceEqual(parts) || Screen.width != width || Screen.height != height)
                        throw new InvalidOperationException("Native screenshot scene/save/vessel/epoch/dimensions changed after queueing.");
                    var receipt = ColonyPlacementScenario.Instance.GetStatus(placement);
                    if (receipt == null || receipt.Stage != ColonyPlacementStage.Anchored || receipt.FoundationId != anchor || receipt.VesselId != target.id.ToString("D") || receipt.AfterWitness != afterWitness)
                        throw new InvalidOperationException("Native screenshot anchored receipt changed.");
                    NativeAnchor(target, anchor);
                    Private(image);
                    if (!File.Exists(image)) continue;
                    if (new FileInfo(image).Length > MaximumBytes) throw new InvalidDataException("Screenshot exceeds 32MiB.");
                    byte[] bytes;
                    try { using (var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read)) { bytes = new byte[stream.Length]; int read = 0; while (read < bytes.Length) { int count = stream.Read(bytes, read, bytes.Length - read); if (count == 0) break; read += count; } if (read != bytes.Length) continue; } }
                    catch (IOException) { continue; }
                    if (!CompletePng(bytes, width, height)) continue;
                    string current = Digest(bytes);
                    if (firstHash != current) { firstHash = current; continue; }
                    completed = bytes; break;
                }
                catch (Exception ex) { failed = ex; break; }
            }
            if (failed == null && completed == null) failed = new TimeoutException("Native screenshot remained queued without complete stable PNG for30seconds.");
            if (failed != null) { Failure(output, context, failed); yield break; }
            try
            {
                context.SetValue("status", "completed", true); context.AddValue("completedUtc", DateTime.UtcNow.ToString("o")); context.AddValue("completedUt", R(Planetarium.GetUniversalTime()));
                context.AddValue("file", image); context.AddValue("bytes", completed.Length); context.AddValue("sha256", Digest(completed)); CameraContext(context, "after");
                if (File.Exists(evidence) || File.Exists(output)) throw new InvalidDataException("Capture evidence already exists.");
                File.WriteAllText(evidence, context.ToString()); File.WriteAllText(output, DateTime.UtcNow.ToString("o") + " screenshot completed " + image + " sha256=" + Digest(completed) + "\nscene=" + HighLogic.LoadedScene + "\nsave=" + folder + "\nut=" + R(Planetarium.GetUniversalTime()));
            }
            catch (Exception ex) { Failure(output, context, ex); }
        }
        static void NativeAnchor(Vessel vessel, string expected)
        {
            var type = typeof(ColonyRuntime).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementFoundations", true);
            var read = type.GetMethod("ReadWitness", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(Vessel), typeof(string).MakeByRefType(), typeof(double).MakeByRefType(), typeof(double).MakeByRefType() }, null);
            var held = type.GetMethod("IsHeld", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(Vessel) }, null);
            var args = new object[] { vessel, null, 0.0, 0.0 };
            if (read == null || held == null || !(bool)held.Invoke(null, new object[] { vessel }) || !(bool)read.Invoke(null, args) || (string)args[1] != expected ||
                double.IsNaN((double)args[2]) || double.IsNaN((double)args[3]) || (double)args[2] < 0 || (double)args[3] < 0 || (double)args[2] > .01 || (double)args[3] > .01)
                throw new InvalidOperationException("Current exact Foundation anchor/pose witness is unavailable.");
        }
        static bool CompletePng(byte[] bytes, int width, int height)
        {
            if (bytes.Length < 45 || !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
            int position = 8; bool header = false;
            while (position <= bytes.Length - 12)
            {
                uint length = U32(bytes, position); if (length > MaximumBytes || position + (long)length + 12 > bytes.Length) return false;
                string kind = System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4);
                if (!header) { if (kind != "IHDR" || length != 13 || U32(bytes, position + 8) != width || U32(bytes, position + 12) != height) return false; header = true; }
                if (kind == "IEND") return length == 0 && position + 12 == bytes.Length;
                position += (int)length + 12;
            }
            return false;
        }
        static uint U32(byte[] bytes, int offset) => (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
        static void CameraContext(ConfigNode node, string suffix) { if (FlightCamera.fetch != null) { node.AddValue("cameraPosition" + suffix, FlightCamera.fetch.transform.position.ToString("F9")); node.AddValue("cameraRotation" + suffix, FlightCamera.fetch.transform.rotation.ToString("F9")); } }
        static void Failure(string output, ConfigNode context, Exception ex) { Debug.LogError("[ExpanseColonyScreenshot] " + ex); if (!string.IsNullOrEmpty(output) && !File.Exists(output)) { Private(output); File.WriteAllText(output, DateTime.UtcNow.ToString("o") + " FAILED screenshot " + ex.Message + "\n" + (context == null ? "" : context.ToString())); } }
        static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static void Private(string path) { string full = Path.GetFullPath(path); if (!full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture path leaves private root."); for (string item = full; item != null; item = Path.GetDirectoryName(item)) if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Capture reparse path refused."); }
    }
}
