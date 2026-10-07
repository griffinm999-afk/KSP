const fs=require('fs'),vm=require('vm');
const relayPath=process.env.LOCALAPPDATA+'\\ExpanseFoundations\\SiteRelay\\relay.cjs';
let source=fs.readFileSync(relayPath,'utf8');
// Verification creates no lock, worker loop or WOLF request.
source=source.replace("const lock=net.createServer(s=>s.destroy());lock.on('error',()=>process.exit(0));lock.listen('\\\\\\\\.\\\\pipe\\\\Expanse.SiteRelay.lock.'+process.env.USERNAME,()=>run());",'');
if(source.includes('const lock='))throw Error('Verification lock removal failed');
const context={require,process:{argv:['node','verify','--telemetry-only'],env:process.env},Buffer,fetch,AbortSignal,setTimeout};
vm.createContext(context);vm.runInContext(source,context);
(async()=>{const demand=await vm.runInContext("request('/api/live/demand')",context);const frame=await vm.runInContext('snapshot()',context);console.log(JSON.stringify({demandActive:demand.active===true,hostStatus:frame.status,publisherConnected:frame.publisherConnected,ageSeconds:frame.ageSeconds,sessionId:frame.sample?.sessionId,sequence:frame.sample?.sequence}));})().catch(()=>{console.error('Telemetry verification failed; no sensitive error details emitted');process.exitCode=1});
