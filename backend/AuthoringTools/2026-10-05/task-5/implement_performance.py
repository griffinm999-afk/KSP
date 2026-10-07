from pathlib import Path
p=Path('performance-work/src/Expanse.WorldBridge')
def edit(name, f):
 q=p/name;q.write_text(f(q.read_text()))
def replace(s,a,b):
 assert a in s,a[:100]
 return s.replace(a,b)
def observation(s):
 s=replace(s,'private ColonySnapshot ObserveColony(', 'private ColonyCapture ObserveColony(')
 s=replace(s,'            snapshot.Wolf = ObserveWolf(ut);','            long attempt = NextColonyAttempt(censusEpochContext);\n            snapshot.Wolf = ObserveWolf(ut);')
 s=replace(s,'                snapshot.VesselCensus = vesselCensusTracker.Observe(censusEpochContext, censusScene, null, null, new string[0]);\n                return snapshot;', '                return new ColonyCapture(snapshot, censusEpochContext, censusScene, attempt, null, null);')
 s=replace(s,'int partsInspected = 0, vesselsInspected = 0, rosterBytes = 0, rosterPeople = 0;\n            int productionBytesRemaining = 24000;', 'int partsInspected = 0, vesselsInspected = 0;')
 s=replace(s,'                Dictionary<string, ColonyTank> tanks = new Dictionary<string, ColonyTank>(StringComparer.Ordinal);','                List<ColonyTank> tanks = row.Tanks;')
 s=replace(s,'converter.inputList.Select(r => Bound(r.ResourceName, 50)).Distinct().Take(16).ToArray()', 'CaptureConverterNames(converter.inputList, snapshot)')
 s=replace(s,'converter.outputList.Select(r => Bound(r.ResourceName, 50)).Distinct().Take(16).ToArray()', 'CaptureConverterNames(converter.outputList, snapshot)')
 s=replace(s,'AddTank(tanks,', 'CaptureTank(snapshot, tanks,')
 start=s.index('                    int budget = Math.Max(512,productionBytesRemaining')
 end=s.index('\n                }',start)
 s=s[:start]+s[end:]
 start=s.index('                int rowBytes = row.CrewRoster.Sum(')
 end=s.index('                snapshot.Vessels.Add(row);',start)
 s=s[:start]+'''                row.CapturedPower = CapturePowerEstimate(vessel);
'''+s[end:]
 start=s.index('            snapshot.VesselCensus = vesselCensusTracker.Observe(censusEpochContext, censusScene,')
 end=s.index('\n        private static void ObserveCrew',start)
 s=s[:start]+'''            captureParts.Add(partsInspected);
            captureVessels.Add(vesselsInspected);
            return new ColonyCapture(snapshot, censusEpochContext, censusScene, attempt,
                !censusIdentityReady || snapshot.Status != "observed" ? null : CaptureVesselIds(FlightGlobals.Vessels),
                !censusIdentityReady || snapshot.Status != "observed" || game == null || game.flightState == null ? null : CaptureProtoIds(game.flightState.protoVessels));
        }
'''+s[end:]
 s=replace(s,'        public ColonyProductionTelemetry Production;', '        public ColonyProductionTelemetry Production;\n        public CapturedPowerEstimate CapturedPower;')
 return s
edit('ColonyObservation.cs',observation)
def world(s):
 s=replace(s,'private ColonySnapshot lastColonySnapshot;', 'private ColonyCapture lastColonyCapture;')
 s=s.replace('lastColonySnapshot = ObserveColony', 'lastColonyCapture = ObserveColony')
 s=s.replace('lastColonySnapshot = new ColonySnapshot { Status = "unavailable", Reason = "Colony observation failed; clock and deliveries remain available.", ObservedUt = ut,\n                            VesselCensus = vesselCensusTracker.Observe(censusEpochContext, scene, null, null, new string[0]) };', 'lastColonyCapture = new ColonyCapture(new ColonySnapshot { Status = "unavailable", Reason = "Colony observation failed; clock and deliveries remain available.", ObservedUt = ut }, censusEpochContext, scene, colonyAttempt, null, null);')
 # Census overlay context includes scene; production reset remains from current source.
 s=replace(s,'try { lastColonyCapture = ObserveColony(ut, censusEpochContext, scene, sample.WorldId != null); }', 'long captureStarted = Stopwatch.GetTimestamp();\n                    try { lastColonyCapture = ObserveColony(ut, censusEpochContext, scene, sample.WorldId != null); }')
 s=replace(s,'                sample.Colony = lastColonySnapshot;', '                sample.Capture = lastColonyCapture;')
 # Add finally after the specific capture catch, retaining other exception paths.
 marker='censusEpochContext, scene, colonyAttempt, null, null);\n                    }'
 s=replace(s,marker,marker+'\n                    finally { captureTiming.Record(Stopwatch.GetTimestamp() - captureStarted); }')
 s=s.replace('lastColonySnapshot = null;', 'lastColonyCapture = null;')
 s=replace(s,'Interlocked.Exchange(ref latest, sample);', 'PublishLatestSample(sample);')
 s=replace(s,'                DrainPendingWorkerLog();\n                NamedPipeClientStream', '                DrainPendingWorkerLog();\n                ReportPerformance();\n                NamedPipeClientStream')
 s=replace(s,'                        DrainPendingWorkerLog();\n                        ClockSample sample', '                        DrainPendingWorkerLog();\n                        ReportPerformance();\n                        ClockSample sample')
 s=replace(s,'                        if (sample != null) WriteFrame(pipe, Json(sample));', '''                        if (sample != null)
                        {
                            CompleteColonySample(sample);
                            if (sample.ContextGeneration == Interlocked.Read(ref clockContextGeneration))
                            {
                                long serializedAt = Stopwatch.GetTimestamp();
                                string json = Json(sample);
                                serializationTiming.Record(Stopwatch.GetTimestamp() - serializedAt);
                                if (sample.ContextGeneration == Interlocked.Read(ref clockContextGeneration))
                                {
                                    long sentAt = Stopwatch.GetTimestamp();
                                    WriteFrame(pipe, json);
                                    transportTiming.Record(Stopwatch.GetTimestamp() - sentAt);
                                }
                            }
                        }''')
 s=replace(s,'            if (elapsedTicks < 0) elapsedTicks = 0;', '            sampleTiming.Record(elapsedTicks);\n            if (elapsedTicks < 0) elapsedTicks = 0;')
 s=replace(s,'        public ColonySnapshot Colony;', '        public ColonySnapshot Colony;\n        internal ColonyCapture Capture;\n        internal long ContextGeneration;')
 assert 'lastColonySnapshot' not in s
 return s
edit('WorldBridgeAddon.cs',world)
def census(s):
 s=replace(s,'ICollection<Guid> saveIds, IEnumerable<string> observedColonyIds)', 'ICollection<Guid> saveIds, IEnumerable<string> observedColonyIds, long? attemptSequence = null)')
 s=replace(s,'            ColonyVesselCensus result = new ColonyVesselCensus { ObservationSequence = ++observationSequence };', '''            if (attemptSequence.HasValue && attemptSequence.Value != observationSequence + 1)
            { previousReliable = false; previousIds = new string[0]; }
            observationSequence = attemptSequence ?? observationSequence + 1;
            ColonyVesselCensus result = new ColonyVesselCensus { ObservationSequence = observationSequence };''')
 return s
edit('VesselCensusTracker.cs',census)
def production(s):
 s=replace(s,'row.Prepared=sample.Prepared;', 'row.Prepared=CopyProductionPotential(sample.Prepared);')
 return s
edit('ColonyProductionTelemetry.cs',production)
