import assert from 'node:assert/strict';import fs from 'node:fs';import vm from 'node:vm';import {EventEmitter} from 'node:events';
const source=fs.readFileSync(new URL('../relay.cjs',import.meta.url),'utf8'),clockSource=source.slice(source.indexOf('function snapshot()'),source.indexOf('async function run()')),wolfSource=source.slice(source.indexOf('function wolfPipe('),source.indexOf('async function processWolf()'));
// Run only the unchanged transport function bodies against in-memory sockets.
// Never evaluate the relay's credential loader, run loop or command processor.
async function readFrame(text,wolf=false){const bytes=Buffer.from(text),header=Buffer.alloc(4);header.writeUInt32LE(bytes.length);let connectedPath;
 class Socket extends EventEmitter{setTimeout(){return this}destroy(){this.destroyed=true}write(request){assert.equal(request.readUInt32LE(),request.length-4);queueMicrotask(()=>{const wire=Buffer.concat([header,bytes]);for(const cut of [wire.subarray(0,2),wire.subarray(2,7),wire.subarray(7,65540),wire.subarray(65540)])this.emit('data',cut);this.emit('end')})}}
 const net={connect(path){connectedPath=path;const s=new Socket();queueMicrotask(()=>s.emit('connect'));return s}},context={net,Buffer,process:{env:{USERNAME:'qa-fixture'}}};vm.createContext(context);vm.runInContext((wolf?wolfSource:clockSource)+(wolf?'\nthis.read=wolfPipe;':'\nthis.read=snapshot;'),context);const result=await context.read({requestId:'fixture'});assert.match(connectedPath,wolf?/WOLF\.Admin/:/Clock\.View/);return result;
}
function sized(bytes){const value={padding:''},base=Buffer.byteLength(JSON.stringify(value));value.padding='é'.repeat(Math.floor((bytes-base)/2))+((bytes-base)%2?'x':'');const text=JSON.stringify(value);assert.equal(Buffer.byteLength(text),bytes);return text}
for(const bytes of [65536,65537,139331,262144])assert.equal((await readFrame(sized(bytes))).padding.length,JSON.parse(sized(bytes)).padding.length);
await assert.rejects(readFrame(sized(262145)),/Invalid frame/);assert.ok(await readFrame(sized(65536),true));await assert.rejects(readFrame(sized(65537),true),/Invalid WOLF frame/);
console.log('Relay transport passed: fragmented UTF-8 clock frames through 262144 bytes; 262145 rejected; WOLF remains at 65536; no sockets, credentials or commands executed.');
