from pathlib import Path
import re, shutil
root=Path('performance-work')
def resolve(rel, fn):
 p=root/rel;s=p.read_text();n=0
 def sub(m):
  nonlocal n
  n+=1;return fn(n,m.group(1),m.group(2))
 s=re.sub(r'(?m)^<<<<<<<[^\n]*\n(.*?)^=======\n(.*?)^>>>>>>>[^\n]*\n',sub,s,flags=re.S|re.M)
 assert '<<<<<<<' not in s
 p.write_text(s)
resolve('src/Expanse.WorldBridge/ColonyObservation.cs',lambda n,a,b: b+'            int productionBytesRemaining = 24000;\n' if n==1 else a+b if n==2 else '        private static string ColonyJson(ColonySnapshot s, string wolfJson = null, bool includeRoster = true, bool omitProduction = false)\n')
resolve('src/Expanse.WorldBridge/WorldBridgeAddon.cs',lambda n,a,b: '\n'.join(a.splitlines()[:2]).replace('Wolf),true)','Wolf),false,true)')+'\n'+b)
resolve('src/Expanse.Clock.Core/ColonyProtocol.cs',lambda n,a,b: a.rstrip().removesuffix(');')+',\n'+b)
def core(n,a,b):
 # Progressively omit optional production, then roster, then census, without
 # accidentally reintroducing an already omitted payload on a later fallback.
 b=b.replace('view.Colony with','reduced with').replace('view.Colony.Vessels','reduced.Vessels')
 b=b.replace('            try\n            {\n                return Encode(view with { Colony = reduced with { Vessels = reduced.Vessels\n                    .Select(v => v with { CrewRoster = null, CrewRosterComplete = false }).ToArray() } });\n            }', '            reduced = reduced with { Vessels = reduced.Vessels.Select(v => v with { CrewRoster = null, CrewRosterComplete = false }).ToArray() };\n            try { return Encode(view with { Colony = reduced }); }')
 return a+b
resolve('src/Expanse.Clock.Core/ClockProtocol.cs',core)
overlay=Path(r'C:\Users\griff\Documents\Codex\2026-10-04\task\vessel-census\package\source')
shutil.copy2(Path(r'C:\Users\griff\Documents\Codex\2026-10-04\task\roster-extension\package\source\ColonySeatCapacity.cs'),root/'src/Expanse.WorldBridge/ColonySeatCapacity.cs')
checks=root/'dev/PerformanceChecks';checks.mkdir(parents=True,exist_ok=True)
shutil.copy2(overlay/'Program.cs',checks/'Program.cs')
(checks/'PerformanceChecks.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include="../../src/Expanse.Clock.Core/Expanse.Clock.Core.csproj"/><Compile Include="../../src/Expanse.WorldBridge/VesselCensusTracker.cs" Link="VesselCensusTracker.cs"/><Compile Include="../../src/Expanse.WorldBridge/ColonySeatCapacity.cs" Link="ColonySeatCapacity.cs"/></ItemGroup></Project>''')
