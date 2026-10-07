from pathlib import Path
root=Path('performance-work/src/Expanse.WorldBridge')
pieces=[]
for name,start,end in [
 ('ColonyObservation.cs','    internal sealed class ColonySnapshot','    public sealed partial class WorldBridgeAddon'),
 ('ColonyPowerTelemetry.cs','    internal sealed class ColonyPowerRate','    // The postfix observes'),
 ('ColonyProductionTelemetry.cs','    internal sealed class ColonyProductionRateTelemetry','    internal sealed class ColonyBrokerSample'),
 ('WolfLedgerBridge.cs','    internal sealed class WolfSnapshot','    [Serializable]')]:
 p=root/name;s=p.read_text();a=s.index(start);b=s.index(end,a);pieces.append(s[a:b]);p.write_text(s[:a]+s[b:])
(root/'ColonyObservationData.cs').write_text('using System;\nusing System.Collections.Generic;\n\nnamespace Expanse.WorldBridge\n{\n'+''.join(pieces)+'}\n')
p=Path('performance-work/dev/PerformanceChecks/PerformanceChecks.csproj');s=p.read_text();s=s.replace('</ItemGroup>', ''.join('<Compile Include="../../src/Expanse.WorldBridge/'+n+'.cs" Link="'+n+'.cs"/>' for n in ['ColonyObservationData','ColonyObservationWorker','ObservationTiming'])+'</ItemGroup>');s=s.replace('<Nullable>enable</Nullable>','<Nullable>disable</Nullable><NoWarn>CS8632;CS0649</NoWarn>');p.write_text(s)
p=Path('performance-work/dev/PerformanceChecks/Program.cs');p.write_text(p.read_text()+'\nDerivationChecks.Run();\n')
