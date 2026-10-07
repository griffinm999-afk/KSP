import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const path=new URL('../supply-chain-model.js',import.meta.url);
const ctx=vm.createContext({console});
vm.runInContext(fs.readFileSync(path,'utf8')+'\nthis.model={supplySnapshot,createSupplySampler,supplyPlanning,supplyEpoch,supplyTankTotals,SUPPLY_DAY_SECONDS,SUPPLY_PLANNING_PER_PERSON}',ctx);
const {supplySnapshot:snap,createSupplySampler:sampler,supplyPlanning:planning,supplyEpoch:epoch}=ctx.model;
const colony={body:'Mun',location:'Lowlands'};
const tank=(resource='Supplies',amount=100,capacity=200)=>({resource,amount,capacity});
const building=(id='v1',extra={})=>({id,name:id,body:'Mun',biome:'Lowlands',crew:3,crewCapacity:20,tanks:[tank()],...extra});
const catalog=(bs=[building()])=>({buildings:bs});
const vessel=(id='v1',extra={})=>({vesselId:id,name:id,body:'Mun',biome:'Lowlands',crew:3,crewCapacity:20,observationBasis:'loaded',tanks:[tank()],...extra});
const result=(ut=100,amount=100,extra={})=>({receivedAt:1000,frame:{worldId:'W',status:'live',sample:{sessionId:'S',loadEpoch:'E',activeWorld:true,utSeconds:999999},colony:{status:'observed',observedUt:ut,vessels:[vessel('v1',{tanks:[tank('Supplies',amount)]})]},...extra}});
const snapshot=(ut=100,amount=100,extra={},cat=catalog(),now=1000)=>snap(cat,colony,result(ut,amount,extra),now);
const tests=[]; const test=(name,run)=>tests.push({name,run});
const rate=(s)=>s.get('Supplies')?.perDay;
function established(){const s=sampler();s.update(snapshot(100,100),1000);assert.equal(rate(s.update(snapshot(316,90),2000)),-1000);return s}
function frameChange(change,ut=532,amount=80){const r=result(ut,amount);change(r.frame);return snap(catalog(),colony,r,3000)}
function resetCase(name,change){test(name,()=>{const s=established();assert.equal(s.update(frameChange(change),3000).size,0);assert.equal(s.update(snapshot(748,70),4000).size,0)})}
test('resources without evidence are not invented by a whitelist',()=>{const s=snapshot();assert.equal(s.resources.has('Water'),false);const missing=snap({buildings:[]},colony,null);assert.equal(missing.resources.get('Supplies').amount,null)});
test('explicit reported zero remains known current zero',()=>{const r=snapshot(100,0).resources.get('Supplies');assert.equal(r.amount,0);assert.equal(r.capacity,200);assert.equal(r.stores[0].current,true)});
test('retained omitted resource is not inferred zero',()=>{const cat=catalog([building('v1',{tanks:[tank('Water',55,100),tank()]})]);const r=snapshot(100,90,{},cat).resources.get('Water');assert.equal(r.amount,55);assert.equal(r.retained,55);assert.equal(r.current,0);assert.equal(r.stores[0].current,false)});
test('reported resources aggregate tanks and override retained totals',()=>{const f=result().frame;f.colony.vessels[0].tanks=[tank('Supplies',12,20),tank('Supplies',8,30)];const r=snapshot(100,100,f).resources.get('Supplies');assert.equal(r.amount,20);assert.equal(r.capacity,50);assert.equal(r.retained,0)});
test('missing vessels preserve retained stock while others remain current',()=>{const cat=catalog([building(),building('v2',{tanks:[tank('Supplies',25,40)]})]);const r=snapshot(100,90,{},cat).resources.get('Supplies');assert.equal(r.amount,115);assert.equal(r.current,90);assert.equal(r.retained,25)});
test('retired and other-location buildings excluded',()=>{const cat=catalog([building(),building('v2',{lifecycleState:'retired'}),building('v3',{biome:'Highlands'})]);assert.equal(snapshot(100,90,{},cat).resources.get('Supplies').amount,90)});
test('only observed people drive population, not seats',()=>{const s=snapshot();assert.equal(s.crewTotal,3);assert.equal(s.observedCrew,3);assert.equal(planning(s.crewTotal,108).demand,32.400000000000006)});
test('planning uses 10.8 per person per 21600-second game day',()=>{assert.equal(ctx.model.SUPPLY_DAY_SECONDS,21600);assert.equal(ctx.model.SUPPLY_PLANNING_PER_PERSON,10.8);assert.equal(planning(10,216).demand,108);assert.equal(planning(10,216).days,2);assert.equal(planning(0,216).days,null);assert.equal(planning(10,null).days,null);assert.equal(planning(10,0).days,0)});
test('invalid populations rejected',()=>{for(const n of [-1,0.5,Infinity,'abc',1000001])assert.equal(planning(n,10),null)});
test('rates use positive colony observedUt, not sample utSeconds',()=>{const s=established();assert.equal(rate(s.update(snapshot(532,80),3000)),-1000)});
test('zero observedUt cannot seed a sampled rate',()=>{const s=sampler();s.update(snapshot(0,100),1000);assert.equal(s.update(snapshot(216,90),2000).size,0)});
test('missing or numeric-string observedUt cannot seed rates',()=>{for(const value of [undefined,null,'100',NaN,-1]){const s=sampler();s.update(snapshot(value,100,{colony:{status:'observed',observedUt:value,vessels:[vessel()]}}),1000);assert.equal(s.update(snapshot(316,90),2000).size,0)}});
test('epoch requires all exact nonempty string components',()=>{for(const key of ['worldId','sessionId','loadEpoch'])for(const value of [undefined,null,'',1]){const f=result().frame;if(key==='worldId')f.worldId=value;else f.sample[key]=value;assert.equal(epoch(f),null)}});
for(const key of ['worldId','sessionId','loadEpoch'])resetCase(`${key} change resets sampling`,f=>{if(key==='worldId')f.worldId='other';else f.sample[key]='other'});
resetCase('paused resets sampling',f=>f.status='paused');
resetCase('truncated observation resets sampling',f=>f.colony.status='truncated');
resetCase('inactive world resets sampling',f=>f.sample.activeWorld=false);
test('stale feed resets sampling',()=>{const s=established();assert.equal(s.update(snapshot(532,80,{},catalog(),10000),10000).size,0);assert.equal(s.update(snapshot(748,70),11000).size,0)});
test('rewind resets baseline and can resume safely',()=>{const s=established();assert.equal(s.update(snapshot(50,200),3000).size,0);assert.equal(rate(s.update(snapshot(266,190),4000)),-1000)});
test('cohort addition resets sampling',()=>{const s=established();assert.equal(s.update(snapshot(532,80,{},catalog([building(),building('v2')])),3000).size,0)});
test('cohort reordering does not reset sampling',()=>{const s=sampler(),b2=building('v2');s.update(snapshot(100,100,{},catalog([building(),b2])),1000);assert.equal(rate(s.update(snapshot(316,90,{},catalog([b2,building()])),2000)),-1000)});
for(const [label,change] of [['capacity',v=>v.tanks[0].capacity=300],['basis',v=>v.observationBasis='snapshot'],['current cohort',v=>v.tanks=[]]]){
 test(`${label} change at advanced UT resets rate`,()=>{const s=established();assert.equal(s.update(frameChange(f=>change(f.colony.vessels[0])),3000).size,0)});
 test(`${label} change at same UT immediately resets rate`,()=>{const s=established();assert.equal(s.update(frameChange(f=>change(f.colony.vessels[0]),316,90),3000).size,0)});
}
test('unchanged time does not generate new rate and times out after 15 seconds',()=>{const s=established();assert.equal(rate(s.update(snapshot(316,90),3000)),-1000);assert.equal(s.update(snapshot(316,90),17001).size,0);assert.equal(s.update(snapshot(532,80),18000).size,0)});
test('large wall-time gap cannot form a rate',()=>{const s=established();assert.equal(s.update(snapshot(532,80),18000).size,0)});
test('recipe values do not contribute gross production to stock rate',()=>{const cat=catalog([building('v1',{production:[{inputs:[{resource:'Water',rate:999}],outputs:[{resource:'Supplies',rate:999}]}]})]);const s=sampler();s.update(snapshot(100,100,{},cat),1000);assert.equal(rate(s.update(snapshot(316,100,{},cat),2000)),0);assert.equal(snapshot(100,100,{},cat).resources.get('Water').amount,null)});
let passed=0;for(const {name,run}of tests){try{run();passed++;console.log('PASS '+name)}catch(err){console.log('FAIL '+name+'\n  '+err.message)}}console.log(`\n${passed}/${tests.length} passed`);process.exitCode=passed===tests.length?0:1;

const flowCtx=vm.createContext({});vm.runInContext(fs.readFileSync(path,'utf8')+'\nthis.flows={supplyConfiguredFlow,supplyFlowComparison}',flowCtx);const {supplyConfiguredFlow:flow,supplyFlowComparison:comparison}=flowCtx.flows;
const config={recipes:[{building:'Agriculture',enabled:true,inputs:[{resource:'Substrate',baseRate:.0026}],outputs:[{resource:'Supplies',baseRate:.00026}]},{building:'Polymers',enabled:true,inputs:[{resource:'Substrate',baseRate:.0305}],outputs:[{resource:'Polymers',baseRate:.0061}]},{building:'Disabled',enabled:false,inputs:[{resource:'Substrate',baseRate:3}],outputs:[]}]};
const substrate=flow(config,'Substrate');assert.equal(substrate.production.total,null);assert.ok(Math.abs(substrate.demand.total-714.96)<1e-9);assert.equal(comparison(substrate,'Substrate',45).state,'unknown');assert.equal(comparison(substrate,'Substrate',45,'actual').state,'unknown');
const supplies=flow(config,'Supplies');assert.equal(supplies.production.total,5.616);assert.equal(comparison(supplies,'Supplies',45).state,'shortfall');assert.ok(Math.abs(comparison(supplies,'Supplies',45).demand-486)<1e-9);assert.equal(comparison(supplies,'Supplies',0).state,'covered');assert.equal(comparison(supplies,'Supplies',45,'actual').balance,null);
const unknown=flow({recipes:[{enabled:true,outputs:[{resource:'Ore',baseRate:null}]}]},'Ore');assert.equal(unknown.production.total,null);const zero=flow({recipes:[{enabled:false,outputs:[{resource:'Ore',baseRate:2}]}]},'Ore');assert.equal(zero.production.total,0);const state=flow({recipes:[{outputs:[{resource:'Ore',baseRate:2}]}]},'Ore');assert.equal(state.production.total,null);
const hopper=flow(config,'Substrate',[{resource:'Substrate',building:'Hopper',enabled:true,baseRate:.1}]);assert.equal(hopper.production.total,2160);assert.equal(comparison(hopper,'Substrate',45).state,'covered');assert.ok(Math.abs(comparison(hopper,'Substrate',45).balance-1445.04)<1e-9);
console.log('Configured production tests passed: named consumers, nominal rates, disabled/unknown distinctions, signed red/green planning balance, neutral actual mode and separate USI crew demand.');
const savedCatalog=JSON.parse(fs.readFileSync(new URL('../catalog.json',import.meta.url)));const verifiedHoppers=JSON.parse(fs.readFileSync(new URL('../production-sources.json',import.meta.url)));savedCatalog.productionSources=verifiedHoppers;const saved=ctx.model.supplySnapshot(savedCatalog,{body:'Minmus',location:'Greater Flats'},null,1000);const mapped=flow(saved,'Substrate',saved.sourceRefs);assert.equal(mapped.producers[0].building,'Hoppers');assert.equal(mapped.producers[0].perDay,1562.5008);assert.ok(Math.abs(comparison(mapped,'Substrate',45).documentedBalance-847.5408)<1e-9);assert.equal(comparison(flow(saved,'Gypsum',saved.sourceRefs),'Gypsum',45).state,'unknown');assert.equal(comparison(flow(saved,'Water',saved.sourceRefs),'Water',45).state,'unknown');assert.ok(saved.sourceRefs.every(h=>saved.cohort.includes(h.buildingId)));assert.ok(saved.sourceRefs.every(h=>h.wolfPoints===5&&h.observedAt==='2026-10-06T03:38:46Z'));assert.equal(comparison(supplies,'Supplies',null).state,'unknown');assert.equal(comparison(supplies,'Supplies','').state,'unknown');
console.log('Verified hopper reference tests passed: stable vessel mapping, physical rates rather than WOLF points, Substrate/Water planning coverage and Gypsum planning shortfall.');
