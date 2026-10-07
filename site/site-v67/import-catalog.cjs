const fs=require('fs');
function parse(path){const root={type:'ROOT',v:{},c:[]},stack=[root];let pending='';for(let l of fs.readFileSync(path,'utf8').split(/\r?\n/)){l=l.trim();if(!l||l.startsWith('//'))continue;if(l==='{'){const n={type:pending,v:{},c:[]};stack.at(-1).c.push(n);stack.push(n)}else if(l==='}')stack.pop();else if(l.includes('=')){const i=l.indexOf('=');stack.at(-1).v[l.slice(0,i).trim()]=l.slice(i+1).trim()}else pending=l}return root}
function all(n){return [n,...n.c.flatMap(all)]}
const config=all(parse('C:/Kerbal Space Program/GameData/ModuleManager.ConfigCache'));
const bodies=config.filter(n=>n.type==='Body').map(n=>({name:n.v.name,biomes:all(n).filter(x=>x.type==='Biome').map(x=>x.v.name)})).filter(x=>x.name&&x.biomes.length);
fs.writeFileSync('catalog-bodies.json',JSON.stringify(bodies,null,2));
console.log(bodies.map(b=>b.name+': '+b.biomes.length).join('\n'));
const save=all(parse('C:/Kerbal Space Program/saves/The Expanse/persistent.sfs'));
console.log('Metadata',save.filter(n=>n.v.BodyName||n.v.Biome||n.v.biome||n.v.BiomeName).slice(0,12).map(n=>({type:n.type,values:n.v})));
const vessels=save.filter(n=>n.type==='VESSEL'&&n.v.sit==='LANDED');
console.log('Landed',vessels.map(n=>({id:n.v.pid,name:n.v.name,type:n.v.type,body:n.c.find(x=>x.type==='ORBIT')?.v.REF,lat:n.v.lat,lon:n.v.lon})));
