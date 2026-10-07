const fs=require('fs');
const path='C:/Kerbal Space Program/saves/The Expanse/persistent.sfs';
const root={type:'ROOT',v:{},c:[]},stack=[root];let pending='';
for(let l of fs.readFileSync(path,'utf8').split(/\r?\n/)){l=l.trim();if(!l||l.startsWith('//'))continue;if(l==='{'){const n={type:pending,v:{},c:[]};stack.at(-1).c.push(n);stack.push(n)}else if(l==='}')stack.pop();else if(l.includes('=')){const i=l.indexOf('=');stack.at(-1).v[l.slice(0,i).trim()]=l.slice(i+1).trim()}else pending=l}
function all(n){return [n,...n.c.flatMap(all)]}
const vessels=all(root).filter(n=>n.type==='VESSEL');
console.log('vessels',vessels.length);
for(const v of vessels){const ref=v.c.find(x=>x.type==='ORBIT')?.v.REF;const labs=all(v).filter(x=>x.type==='MODULE'&&x.v.name==='ModuleScienceLab').length;if(ref==='1'||/hotel|depot|lab|station|kerbin/i.test(v.v.name||'')||labs)console.log(JSON.stringify({name:v.v.name,id:v.v.pid,sit:v.v.sit,type:v.v.type,ref,labs,parts:v.c.filter(x=>x.type==='PART').length}));}

console.log('scenarios',all(root).filter(n=>n.type==='SCENARIO').map(n=>({name:n.v.name,children:n.c.map(c=>c.type).slice(0,8)})));

for(const name of ['RecoveryCapsuleModule','ColonyRuntime','DepotRegistryModule']){const n=all(root).find(x=>x.type==='SCENARIO'&&x.v.name===name);console.log(name,JSON.stringify({keys:Object.keys(n?.v||{}),children:n?.c.map(c=>({type:c.type,keys:Object.keys(c.v),count:c.c.length,sample:c.c.slice(0,4).map(x=>({type:x.type,keys:Object.keys(x.v)}))}))}));}

for(const name of ['RecoveryCapsuleModule','ColonyRuntime']){const n=all(root).find(x=>x.type==='SCENARIO'&&x.v.name===name)?.c[0];for(const key of ['stateBytesBase64','payload'])if(n?.v[key]){const b=Buffer.from(n.v[key],'base64');console.log(name,key,'bytes',b.length,'prefix',b.subarray(0,50).toString());try{let j=JSON.parse(b.toString());console.log('JSON keys',Object.keys(j));}catch{}}}

const raw=all(root).find(x=>x.type==='SCENARIO'&&x.v.name==='ColonyRuntime')?.c[0]?.v.payload;const state=JSON.parse(Buffer.from(raw,'base64').toString());for(const key of ['Colonies','Receipts','Effects','Shipments','Journal','Suppliers']){let v=state[key];console.log('STATE',key,Array.isArray(v)?v.length:typeof v,Array.isArray(v)?v.slice(0,2):Object.keys(v||{}).slice(0,10));}console.log('COLONIES',state.Colonies?.map(x=>({keys:Object.keys(x),id:x.Id,name:x.Name,charter:x.Charter})));

console.log('COLONY_SUMMARY',JSON.stringify(state.Colonies?.map(x=>({Id:x.Id,Name:x.Name,keys:Object.keys(x),charter:x.Charter,facilities:x.Facilities?.length,logistics:x.Logistics,treasury:x.Treasury,position:x.Position}))));
