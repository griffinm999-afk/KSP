$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$directories = @((Join-Path $stage 'src\Expanse.WorldBridge\bin\Release\net472'), 'C:\Kerbal Space Program\KSP_x64_Data\Managed', 'C:\Kerbal Space Program\GameData\000_Harmony', 'C:\Kerbal Space Program\GameData\000_USITools', 'C:\Kerbal Space Program\GameData\UmbraSpaceIndustries\WOLF')
$resolver = [ResolveEventHandler]{param($sender,$eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name
    foreach ($directory in $directories) {
        $path = Join-Path $directory ($name + '.dll')
        if ([IO.File]::Exists($path)) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $directories[0] 'Expanse.WorldBridge.dll'))
    # Reflection is test scaffolding only. The uninitialized addon below never
    # receives Awake/Start/Update, opens a pipe, installs hooks, or touches KSP.
    Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
#pragma warning disable SYSLIB0050 // Detached test allocation, no serialization.

public static class HandoffChecks
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly assembly;
    static Type TypeOf(string name) { return assembly.GetType("Expanse.WorldBridge." + name, true); }
    static object New(string name) { return Activator.CreateInstance(TypeOf(name), true); }
    static object Get(object owner, string name) { return owner.GetType().GetField(name, Fields).GetValue(owner); }
    static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Fields).SetValue(owner, value); }
    static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Methods).Invoke(owner, args); }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Capture(long attempt, string epoch, string scene)
    {
        var raw = New("ColonySnapshot"); Set(raw, "Status", "observed"); Set(raw, "ObservedUt", (double?)100);
        return Activator.CreateInstance(TypeOf("ColonyCapture"), Fields, null,
            new object[] { raw, epoch, scene, attempt, new Guid[0], new Guid[0] }, null);
    }
    static object Sample(object capture, string loadEpoch, string scene, bool active)
    {
        var sample = New("ClockSample");
        Set(sample, "SessionId", "session"); Set(sample, "LoadEpoch", loadEpoch);
        Set(sample, "WorldId", "world"); Set(sample, "SaveFolder", "save");
        Set(sample, "Scene", scene); Set(sample, "ActiveWorld", active);
        Set(sample, "UtSeconds", (double?)100); Set(sample, "Capture", capture);
        return sample;
    }
    static object Census(object sample) { return Get(Get(sample, "Colony"), "VesselCensus"); }
    static object Complete(object addon, object sample)
    {
        Call(addon, "PublishLatestSample", sample);
        var taken = Call(addon, "TakeLatestSample");
        Check(Object.ReferenceEquals(sample, taken), "Single sample publication changed identity");
        Call(addon, "CompleteColonySample", taken);
        return taken;
    }
    public static void Run(Assembly candidate)
    {
        assembly = candidate;
        var addonType = TypeOf("WorldBridgeAddon");
        var addon = FormatterServices.GetUninitializedObject(addonType);
        foreach (var field in addonType.GetFields(Fields))
            if (field.FieldType == TypeOf("ObservationTiming") || field.FieldType == TypeOf("ObservationCount") || field.FieldType == TypeOf("VesselCensusTracker"))
                field.SetValue(addon, Activator.CreateInstance(field.FieldType, true));

        // No consumer during this burst: the real publication method retains
        // only the newest clock sample and counts every replaced predecessor.
        object newest = null;
        for (int i = 0; i < 10000; i++) {
            newest = Sample(null, "epoch", "FLIGHT", true); Set(newest, "Sequence", (long)i);
            Call(addon, "PublishLatestSample", newest);
        }
        Check(Object.ReferenceEquals(newest, Call(addon, "TakeLatestSample")), "Backpressure must retain newest sample");
        Check(Call(addon, "TakeLatestSample") == null, "Latest slot must drain exactly once");
        Check((long)Call(Get(addon, "replacedSamples"), "Drain") == 9999, "Bounded replacement accounting");

        // Exercise actual atomic publication/drain concurrently, with one
        // producer and one deliberately slower consumer, like the runtime.
        int done = 0; long last = -1;
        var producer = Task.Run(() => {
            try { for (long i = 0; i < 3000; i++) {
                var sample = Sample(null, "epoch", "FLIGHT", true); Set(sample, "Sequence", i);
                Call(addon, "PublishLatestSample", sample);
            } } finally { Volatile.Write(ref done, 1); }
        });
        do {
            var sample = Call(addon, "TakeLatestSample");
            if (sample != null) { long sequence = (long)Get(sample, "Sequence"); Check(sequence > last, "Duplicate or reordered sample"); last = sequence; }
            Thread.Yield();
        } while (Volatile.Read(ref done) == 0 || Get(addon, "latest") != null);
        producer.GetAwaiter().GetResult();
        Check(last == 2999, "Consumer must eventually observe newest sample");

        var firstCapture = Capture(1, "session/epoch/world", "FLIGHT");
        var first = Complete(addon, Sample(firstCapture, "epoch", "FLIGHT", true));
        Check((string)Get(Census(first), "Status") == "unavailable", "First independent census is provisional");
        var heartbeat = Complete(addon, Sample(firstCapture, "epoch", "FLIGHT", true));
        Check(Object.ReferenceEquals(Get(first, "Colony"), Get(heartbeat, "Colony")), "Heartbeat must reuse derived result");
        Check((long)Get(Census(heartbeat), "ObservationSequence") == 1, "Heartbeat must not advance census");
        var second = Complete(addon, Sample(Capture(2, "session/epoch/world", "FLIGHT"), "epoch", "FLIGHT", true));
        Check((string)Get(Census(second), "Status") == "complete", "Independent adjacent capture confirms census");
        var gap = Complete(addon, Sample(Capture(4, "session/epoch/world", "FLIGHT"), "epoch", "FLIGHT", true));
        Check((string)Get(Census(gap), "Status") == "unavailable", "Overwritten capture breaks census proof");

        var obsolete = Sample(Capture(5, "session/epoch/world", "FLIGHT"), "epoch", "FLIGHT", true);
        Call(addon, "PublishLatestSample", obsolete); Call(addon, "TakeLatestSample");
        // Both transition samples may be overwritten before the worker sees
        // them. Generation still records the round trip and invalidates proof.
        Call(addon, "PublishLatestSample", Sample(null, "epoch", "SPACECENTER", false));
        var returned = Sample(Capture(6, "session/epoch/world", "FLIGHT"), "epoch", "FLIGHT", true);
        Call(addon, "PublishLatestSample", returned);
        Call(addon, "CompleteColonySample", obsolete);
        Check(Get(obsolete, "Colony") == null, "Old generation must not acquire colony data");
        Call(addon, "CompleteColonySample", Call(addon, "TakeLatestSample"));
        Check((string)Get(Census(returned), "Status") == "unavailable", "Hidden scene round trip must reset stability");
        Call(addon, "PublishLatestSample", Sample(null, "epoch", "FLIGHT", true));
        var resumed = Complete(addon, Sample(Capture(7, "session/epoch/world", "FLIGHT"), "epoch", "FLIGHT", true));
        Check((string)Get(Census(resumed), "Status") == "unavailable", "Hidden unavailable-capture interval must reset stability");
        var reset = Complete(addon, Sample(Capture(1, "session/new-epoch/world", "FLIGHT"), "new-epoch", "FLIGHT", true));
        Check((long)Get(Census(reset), "ObservationSequence") == 1 && (string)Get(Census(reset), "Status") == "unavailable", "Load epoch resets attempt identity");
        var mismatched = Complete(addon, Sample(Capture(2, "session/epoch/world", "FLIGHT"), "new-epoch", "FLIGHT", true));
        Check(Get(mismatched, "Colony") == null, "Capture from another epoch must not be attached");

        // Freshly copied broker vectors must not follow subsequent main-thread
        // mutations of the source broker observation.
        var rate = New("ColonyProductionRateTelemetry"); Set(rate, "UnitsPerSecond", 2d);
        var rates = Array.CreateInstance(rate.GetType(), 1); rates.SetValue(rate, 0);
        var vector = New("ColonyProductionVector"); Set(vector, "Inputs", rates);
        var potential = New("ColonyProductionPotential"); Set(potential, "Rates", vector);
        var copy = addonType.GetMethod("CopyProductionPotential", Methods).Invoke(null, new[] { potential });
        Set(rate, "UnitsPerSecond", 99d);
        var copiedRate = ((Array)Get(Get(copy, "Rates"), "Inputs")).GetValue(0);
        Check((double)Get(copiedRate, "UnitsPerSecond") == 2d && !Object.ReferenceEquals(rate, copiedRate), "Broker array escaped into capture");
        Console.WriteLine("Actual candidate handoff: bounded slot, concurrent replacement, heartbeat cache, skipped capture, scene/epoch reset and broker-copy checks PASS.");
    }
}
'@
    [HandoffChecks]::Run($assembly)
    # The real writer and reporter are exercised only against workspace-owned
    # fixtures. No Awake/Update, native game calls, hooks, or live log writes.
    $addonType=$assembly.GetType('Expanse.WorldBridge.WorldBridgeAddon',$true)
    $flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
    $fixture=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($addonType)
    foreach($field in $addonType.GetFields($flags)){
      if($field.FieldType.FullName -in @('Expanse.WorldBridge.ObservationTiming','Expanse.WorldBridge.ObservationCount')){
        $field.SetValue($fixture,[Activator]::CreateInstance($field.FieldType,$true))
      }
    }
    $fixtureRoot=Join-Path $PSScriptRoot 'TestResults\diagnostics'
    $null=New-Item -ItemType Directory -Path $fixtureRoot -Force
    $addonType.GetField('installNamespace',$flags).SetValue($fixture,$fixtureRoot)
    $reporter=$addonType.GetMethod('ReportPerformance',$flags)
    $null=$reporter.Invoke($fixture,@())
    $pending=$addonType.GetField('pendingTimingLog',$flags)
    $message=$pending.GetValue($fixture)
    $expected=Join-Path $fixtureRoot 'Logs\ExpanseFoundations\ClockBridge\worker.log'
    if(!$message.Contains('Observation performance') -or !$message.Contains($expected) -or !(Test-Path -LiteralPath $expected)){throw 'Timing reporter path/main-thread handoff failed.'}
    $pending.SetValue($fixture,$null);$null=$reporter.Invoke($fixture,@())
    if($null -ne $pending.GetValue($fixture)){throw 'Timing reporter was not rate limited.'}
    $blocker=Join-Path $fixtureRoot 'not-a-directory';Set-Content -LiteralPath $blocker -Value 'fixture'
    $addonType.GetField('workerDiagnosticLogPath',$flags).SetValue($fixture,(Join-Path $blocker 'worker.log'))
    $writer=$addonType.GetMethod('WriteWorkerLogLine',$flags);$null=$writer.Invoke($fixture,@('fixture failure'))
    $failure=$addonType.GetField('pendingLogFailure',$flags)
    if(!$failure.GetValue($fixture).Contains('Diagnostic file unavailable')){throw 'File failure did not reach readable main-thread diagnostic.'}
    $failure.SetValue($fixture,$null);$null=$writer.Invoke($fixture,@('second fixture failure'))
    if($null -ne $failure.GetValue($fixture)){throw 'File failure was not rate limited.'}
    'Actual diagnostics: deterministic workspace path, timing handoff, write-failure visibility and rate limits PASS.'
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}
