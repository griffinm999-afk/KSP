import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import {frameFixture,rate} from './production-fixture.mjs';

class Node{constructor(tag,text=''){Object.assign(this,{tag,textContent:text,children:[],attributes:{},dataset:{},style:{setProperty(){}},classList:{add(){}}})}append(...n){this.children.push(...n)}replaceChildren(...n){this.children=n}setAttribute(k,v){this.attributes[k]=v}focus(){}}
const walk=n=>[n,...n.children.flatMap(walk)];
const context=()=>{const c={console,Date,window:{},el:(tag,text)=>new Node(tag,text)};vm.createContext(c);for(const file of ['crew-observation.js','production-observation.js','supply-chain-model.js','production-flow.js','production-sankey.js','supply-chain-ui.js'])vm.runInContext(fs.readFileSync(new URL('../'+file,import.meta.url),'utf8'),c);return c};
// UI replay using the separately verified 20:29 native rate, not a captured
// full frame or a claim that this remains the game's current rate.
const frame=frameFixture(),v=frame.colony.vessels[0],m=v.production.modules[0];v.vesselId='602524b9-f5c6-46b5-bd7e-879357299f5e';v.name='Atlas Harvester 1';m.achieved.outputs=[rate('Gypsum',2.194179)];m.prepared.rates.outputs=[rate('Gypsum',2.194179)];m.configured.outputs=[];
const setup=c=>{c.catalog={worldId:frame.worldId,buildings:[{...v,id:v.vesselId,production:[]}]};c.window.lastColonyLive={frame,receivedAt:Date.now()}};
const colony={id:'default-live',body:v.body,location:v.biome};
const render=c=>{const root=new Node('div');c.renderSupplyChain(root,colony);const nodes=()=>walk(root);return {root,nodes,picker:()=>nodes().find(n=>n.attributes['aria-label']==='Production comparison basis'),resource:()=>nodes().find(n=>n.attributes['aria-label']==='Select supply chain resource'),diagram:()=>nodes().find(n=>n.className==='sc-sankey-desktop').innerHTML,text:()=>nodes().map(n=>n.textContent).join(' ')}};
const choose=(n,value)=>{n.value=value;n.onchange()};
let c=context();setup(c);let ui=render(c);assert.equal(ui.picker().value,'actual');assert.equal(ui.picker().children[0].textContent,'Live production · measured subtotals');choose(ui.resource(),'Gypsum');assert.match(ui.diagram(),/LIVE PRODUCTION \/ MEASURED SUBTOTALS/);assert.match(ui.diagram(),/Atlas Harvester 1/);assert.match(ui.diagram(),/47,394.27/);assert.doesNotMatch(ui.diagram(),/CONFIGURED \/ USI PLANNING/);
// Explicit plan survives live refresh, resource/search/population edits and a
// route remount. Default-selection migration never overwrites that marker.
choose(ui.picker(),'plan');assert.equal(c.window.colonySupplyStates[colony.id].basisExplicit,true);assert.match(ui.diagram(),/CONFIGURED \/ USI PLANNING/);assert.doesNotMatch(ui.diagram(),/47,394.27/);choose(ui.resource(),'ElectricCharge');const search=ui.nodes().find(n=>n.attributes['aria-label']==='Search supply chain resources');search.value='gypsum';search.oninput();const population=ui.nodes().find(n=>n.attributes['aria-label']==='Planning population');population.value='51';population.oninput();ui.root.liveRefresh();ui=render(c);assert.equal(ui.picker().value,'plan');assert.equal(c.window.colonySupplyStates[colony.id].basisExplicit,true);assert.equal(ui.nodes().find(n=>n.attributes['aria-label']==='Search supply chain resources').value,'gypsum');
// Old in-memory nominal defaults have no explicit marker. Keep their selected
// resource and scenario inputs while returning the diagram to live readings.
c.window.colonySupplyStates[colony.id]={selected:'Gypsum',population:'51',flowBasis:'plan',searchText:'gypsum'};ui=render(c);assert.equal(ui.picker().value,'actual');assert.equal(ui.resource().value,'Gypsum');assert.match(ui.diagram(),/47,394.27/);
// A browser reload creates a fresh window and selects live. Other explicit
// modes remain available; changing production data cannot choose a mode.
c=context();setup(c);ui=render(c);assert.equal(ui.picker().value,'actual');choose(ui.picker(),'background');ui=render(c);assert.equal(ui.picker().value,'background');c.window.colonySupplyStates={};ui=render(c);assert.equal(ui.picker().value,'actual');choose(ui.resource(),'Gypsum');
m.achieved.outputs[0].unitsPerSecond=0;ui.root.liveRefresh();assert.match(ui.diagram(),/0 \/ day/);assert.doesNotMatch(ui.diagram(),/47,394.27/);m.achieved=null;ui.root.liveRefresh();assert.match(ui.diagram(),/Unavailable/);assert.doesNotMatch(ui.diagram(),/47,394.27|2.194179/);assert.equal(ui.picker().value,'actual');assert.match(ui.text(),/Gross production telemetry unavailable/);
console.log('PASS live default, legacy default migration, explicit choice retention, reload/reset, verified-rate UI replay, zero vs unknown and no nominal fallback.');
