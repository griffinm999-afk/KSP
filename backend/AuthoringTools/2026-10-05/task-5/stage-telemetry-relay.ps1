$ErrorActionPreference = 'Stop'
$relayHome = 'C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay'
$source = Get-Content -LiteralPath (Join-Path $relayHome 'relay.cjs') -Raw
if ($source.Contains('const telemetryOnly=')) { throw 'Already modified; inspect before staging' }
$source = $source.Replace("const origin=", "const telemetryOnly=process.argv.includes('--telemetry-only');`nconst origin=")
$source = $source.Replace('async function request(route,body){', "async function request(route,body){if(telemetryOnly&&!((route==='/api/live/demand'&&body===undefined)||(route==='/api/live/ingest'&&body!==undefined)))throw Error('Route blocked in telemetry-only mode');")
$source = $source.Replace('if(active){await processWolf();', 'if(active){if(!telemetryOnly)await processWolf();')
$source = $source.Replace('last=key;failed=Date.now()', "last=key;failed=Date.now();if(telemetryOnly)fs.writeFileSync(path.join(home,'status.json'),JSON.stringify({at:new Date().toISOString(),status:'Connected',mode:'telemetry-only',sessionId:sample?.sessionId??null,sequence:sample?.sequence??null,publisherConnected:frame.publisherConnected===true}))")
$source = $source.Replace('reason:e.message', "reason:telemetryOnly?'Telemetry request failed; retrying':e.message")
$source = $source.Replace('function wolfPipe(request){return', "function wolfPipe(request){if(telemetryOnly)return Promise.reject(Error('WOLF disabled in telemetry-only mode'));return")
$source = $source.Replace("async function processWolf(){", "async function processWolf(){`n if(telemetryOnly)return;")
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'relay-telemetry.cjs'),$source,[Text.UTF8Encoding]::new($false))
$launcher = @'
Set relayShell = CreateObject("WScript.Shell")
relayShell.Run """C:\Users\griff\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"" ""C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay\relay.cjs"" --telemetry-only", 0, False
'@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'start-telemetry-only.vbs'),$launcher,[Text.UTF8Encoding]::new($false))
