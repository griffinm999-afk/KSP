function supplyTelemetryRecipe(module,building,observedUt){
 const rows=items=>items.map(r=>({resource:r.resource,baseRate:r.unitsPerSecond}));
 const inputs=rows(module.configured.inputs),outputs=rows(module.configured.outputs);for(const [direction,target] of [['inputs',inputs],['outputs',outputs]])for(const resource of module.resourceReferences?.[direction]||[])if(!target.some(r=>r.resource===resource))target.push({resource,baseRate:null});if(module.harvester&&!outputs.some(r=>r.resource===module.harvester.resource))outputs.push({resource:module.harvester.resource,baseRate:null});
 return {buildingId:building.id,building:building.name,part:module.partName,bay:module.bayIndex===null?null:module.bayIndex+1,recipe:module.recipe,enabled:module.enabled,inputs,outputs,required:module.configured.requirements.map(r=>({resource:r.resource,baseRate:r.amount})),source:module.retained?'Last known configuration':'Reported configuration',observedUt:module.configurationObservedUt??observedUt,module};
}
function supplyTelemetryFlow(snapshot,resource,basis){
 if(resource==='ElectricCharge'&&basis==='actual'&&typeof supplyPowerFlow==='function')return supplyPowerFlow(snapshot);
 const saved=supplyConfiguredFlow(snapshot,resource,snapshot.sourceRefs),producers=[],consumers=[];
 const has=(rates,name)=>rates.some(r=>r.resource===name);
 for(const t of snapshot.telemetry||[])for(const m of t.production.modules){
  const candidates=d=>[...(m.resourceReferences?.[d]||[]).map(resource=>({resource})),...m.configured[d],...(m.prepared?.rates[d]||[]),...(m.achieved?.[d]||[]),...(m.background?.[d]||[])];
  const measurement=basis==='actual'?m.achieved:basis==='potential'?m.prepared?.rates:basis==='background'?m.background:null;
  const sample=basis==='actual'?m.achieved:basis==='potential'?m.prepared:basis==='background'?m.background:null;
  for(const [direction,target] of [['outputs',producers],['inputs',consumers]])if(has(candidates(direction),resource)||direction==='outputs'&&m.harvester?.resource===resource){
   const entries=measurement?.[direction]?.filter(r=>r.resource===resource),valid=!m.retained&&!!sample&&sample.sampleUt<=snapshot.clockUt&&snapshot.clockUt-sample.sampleUt<=10;
   const measured=valid&&entries?.length?entries.reduce((n,r)=>n+r.unitsPerSecond,0)*SUPPLY_DAY_SECONDS:null;
   target.push({buildingId:t.building.id,building:t.building.name,recipe:m.recipe,bay:m.bayIndex===null?null:m.bayIndex+1,enabled:m.enabled,source:basis==='actual'?'Measured broker subtotal':basis==='potential'?'Prepared native potential':'Background model',inputs:candidates('inputs').map(r=>({resource:r.resource})),outputs:candidates('outputs').map(r=>({resource:r.resource})),perDay:measured,module:m,retained:m.retained,configurationObservedUt:m.configurationObservedUt,lastMeasured:basis==='actual'?m.lastAchieved:null,sampleUt:valid?sample.sampleUt:null,intervalGameSeconds:valid?sample.intervalGameSeconds??null:null,nativeStatus:m.nativeStatus,reason:m.reason});
  }
 }
 // Preserve useful named relationships when no telemetry for that vessel is available.
 const observed=new Set((snapshot.telemetry||[]).filter(t=>t.production.modules.length||t.configurationComplete||t.currentInventoryComplete).map(t=>t.building.id));
 for(const [source,target] of [[saved.producers,producers],[saved.consumers,consumers]])for(const r of source)if(!observed.has(r.buildingId)||r.telemetryUnmatched)target.push({...r,perDay:null,source:'Saved relationship · rate unavailable'});
 const crewRows=basis==='actual'&&typeof supplyCrewRows==='function'?supplyCrewRows(snapshot,resource):[];if(resource==='Mulch')producers.push(...crewRows);else consumers.push(...crewRows);
 const sum=rows=>{const known=rows.filter(r=>Number.isFinite(r.perDay));return {total:known.length?known.reduce((n,r)=>n+r.perDay,0):null,knownTotal:known.length?known.reduce((n,r)=>n+r.perDay,0):null,known:known.length,configured:rows.length,unknown:rows.length-known.length}};
 return {basis,producers,consumers,crewIncluded:crewRows.length>0,production:sum(producers),demand:sum(consumers),unreportedProductionBuildings:snapshot.unreportedProductionBuildings,partial:true};
}
function supplyProductionView(building,record){
 const cache=record.productionConfiguration,current=record.productionCurrent?record.productionObservation:null;
 if(!cache&&!current)return null;
 const now=new Map((current?.modules||[]).map(m=>[m.key,m]));
 const modules=(cache?.modules||current?.modules||[]).map(c=>{const m=now.get(c.key);return {...c,...(m||{}),retained:!m,configurationObservedUt:c.configurationObservedUt??current?.observedUt,lastAchieved:c.lastAchieved||null,lastPrepared:c.lastPrepared||null,lastBackground:c.lastBackground||null}});
 return {building,reportedThisFrame:!!current&&(current.modules.length>0||current.inventoryStatus==='complete-supported'),reportedModuleCount:current?.modules.length||0,retainedModuleCount:modules.filter(m=>m.retained).length,configurationComplete:!!cache?.completeSupported,currentInventoryComplete:current?.inventoryStatus==='complete-supported',budgetOmittedModuleCount:current?.budgetOmittedModuleCount||0,budgetSelectionSequence:current?.budgetSelectionSequence??null,production:{status:current?.status||'unavailable',budgetOmittedModuleCount:current?.budgetOmittedModuleCount||0,budgetSelectionSequence:current?.budgetSelectionSequence??null,inventoryStatus:current?.inventoryStatus||'partial',observedUt:current?.observedUt??null,reason:current?.reason||'Configuration retained; current module reading unavailable',modules}};
}
