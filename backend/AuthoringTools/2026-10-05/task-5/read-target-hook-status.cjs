const net = require('net');
const targets = new Map([
 ['602524b9-f5c6-46b5-bd7e-879357299f5e','Atlas harvester'],
 ['a89d0ac9-cf1b-4d96-ab72-8643a2a22bde','Atlas agriculture'],
 ['ff54e526-3a87-46de-921a-6f6e292da861','Duna agriculture']
]);
function read() { return new Promise((resolve,reject)=>{
 const s=net.connect('\\\\.\\pipe\\ExpanseFoundations.Clock.View.v1.'+process.env.USERNAME);
 let b=Buffer.alloc(0),done=false;
 function fail(){if(done)return;done=true;s.destroy();reject(new Error('Local read-only Clock.View unavailable'));}
 s.setTimeout(3000,fail);s.on('error',fail);
 s.on('connect',()=>{const q=Buffer.from(JSON.stringify({protocolVersion:1,messageType:'getSnapshot'}));const h=Buffer.alloc(4);h.writeUInt32LE(q.length);s.write(Buffer.concat([h,q]));});
 s.on('data',chunk=>{b=Buffer.concat([b,chunk]);if(b.length<4)return;const n=b.readUInt32LE();if(n<1||n>262144)return fail();if(b.length<n+4)return;done=true;s.destroy();try{const f=JSON.parse(b.subarray(4,n+4));const p=f.sample??{};
 resolve({readUtc:new Date().toISOString(),frameBytes:n,status:f.status,publisherConnected:f.publisherConnected,ageSeconds:f.ageSeconds,sequence:p.sequence,ut:p.utSeconds,scene:p.scene,paused:p.paused,colonyStatus:f.colony?.status,colonyReason:f.colony?.reason,colonyUt:f.colony?.observedUt,targets:(f.colony?.vessels??[]).filter(v=>targets.has(v.vesselId)).map(v=>({target:targets.get(v.vesselId),basis:v.observationBasis,productionStatus:v.production?.status,productionInventoryStatus:v.production?.inventoryStatus,budgetOmittedModuleCount:v.production?.budgetOmittedModuleCount,productionReason:v.production?.reason,productionUt:v.production?.observedUt,omittedModuleCount:v.production?.omittedModuleCount,modules:(v.production?.modules??[]).map(m=>({moduleIndex:m.moduleIndex,bayIndex:m.bayIndex,recipe:m.recipe,enabled:m.enabled,activated:m.activated,basis:m.basis,moduleType:m.moduleType,harvester:m.harvester,nativeStatus:m.nativeStatus,configuredOutputs:m.configured?.outputs,prepared:m.prepared?{sampleUt:m.prepared.sampleUt,outputs:m.prepared.rates?.outputs}:null,achieved:m.achieved?{sampleUt:m.achieved.sampleUt,outputs:m.achieved.outputs,captureSequence:m.achieved.captureSequence,intervalGameSeconds:m.achieved.intervalGameSeconds}:null,captureSequence:m.captureSequence,reason:m.reason}))}))});
 }catch(e){reject(new Error('Local snapshot parsing failed'));}});
 s.on('end',()=>{if(!done)fail();});
 });}
(async()=>{for(let i=0;i<1;i++){console.log(JSON.stringify(await read()));}})().catch(e=>{console.error(e.message);process.exitCode=1;});




