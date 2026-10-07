const fs=require('fs'),vm=require('vm'),assert=require('assert');
const source=fs.readFileSync(process.argv[2]||__dirname+'/relay-telemetry.cjs','utf8');
async function scenario(telemetry,receipt,fail){
 const calls=[],writes=[],deletes=[],pipeCalls=[];let sleeps=0;
 const fakeFs={existsSync:()=>receipt,readFileSync:()=>JSON.stringify({id:'old-receipt',result:{status:'applied'}}),unlinkSync:p=>deletes.push(p),writeFileSync:(p,v)=>writes.push(JSON.parse(v))};
 const sandbox={require:n=>({net:{createServer:()=>({on(){},listen(){}}),connect:p=>{pipeCalls.push(p);throw Error('Unexpected native pipe')}},fs:fakeFs,path:require('path'),child_process:{execFileSync:()=>JSON.stringify({serviceToken:'test-only',relayKey:'test-only'})}}[n]),process:{argv:telemetry?['node','relay','--telemetry-only']:['node','relay'],env:{LOCALAPPDATA:'test',USERNAME:'test'},exit:()=>{}},Buffer,Promise,Date,JSON,Error,AbortSignal,setTimeout:()=>{throw Error('test stop')},fetch:async(url,opts)=>{calls.push({url,method:opts.method});assert(opts.signal);if(fail)throw Error('sensitive error details');return {ok:true,json:async()=>url.endsWith('/api/live/demand')?{active:true}:url.endsWith('/api/wolf/next')?null:{ok:true}}}};
 vm.createContext(sandbox);vm.runInContext(source,sandbox);
 sandbox.stop=()=>{if(++sleeps>=2)throw Error('test stop');return Promise.reject(Error('test stop'));};
 vm.runInContext("snapshot=async()=>({protocolVersion:1,messageType:'clockView',status:'live',publisherConnected:true,sample:{sessionId:'new-session',sequence:123}});",sandbox);
 await vm.runInContext('run()',sandbox).catch(e=>assert.equal(e.message,'test stop'));
 if(telemetry){
  await vm.runInContext('processWolf()',sandbox);
  await vm.runInContext('wolfPipe({})',sandbox).then(()=>assert.fail('WOLF pipe allowed'),e=>assert.match(e.message,/disabled/));
  for(const route of ['/api/wolf/next','/api/wolf/result','/api/other'])await vm.runInContext(`request('${route}')`,sandbox).then(()=>assert.fail('WOLF route allowed'),e=>assert.match(e.message,/blocked/));
  await vm.runInContext("request('/api/live/demand',{})",sandbox).then(()=>assert.fail('Wrong method allowed'),e=>assert.match(e.message,/blocked/));
  await vm.runInContext("request('/api/live/ingest')",sandbox).then(()=>assert.fail('Wrong method allowed'),e=>assert.match(e.message,/blocked/));
  assert(calls.every(c=>/\/api\/live\/(demand|ingest)$/.test(c.url)));
  assert.equal(deletes.length,0);assert.equal(pipeCalls.length,0);
  assert(!JSON.stringify(writes).includes('sensitive error details'));
  if(!fail)assert(writes.some(w=>w.status==='Connected'&&w.sessionId==='new-session'));
 }else assert(calls.some(c=>c.url.endsWith(receipt?'/api/wolf/result':'/api/wolf/next')));
 return {telemetry,receipt,fail,requests:calls.length};
}
(async()=>{const results=[];for(const receipt of [false,true])for(const fail of [false,true])results.push(await scenario(true,receipt,fail));results.push(await scenario(false,false,false));results.push(await scenario(false,true,false));console.log(JSON.stringify({passed:true,scenarios:results},null,2));})().catch(e=>{console.error(e);process.exitCode=1});
