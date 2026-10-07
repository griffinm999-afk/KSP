// Optional telemetry is never evidence of complete colony production coverage.
const productionNumber=x=>Number.isFinite(x)&&x>=0;
const productionText=x=>typeof x==='string'&&x.length<=512;
const productionResource=x=>typeof x==='string'&&x.trim().length>0&&x.length<=160;
const productionIndex=x=>Number.isSafeInteger(x)&&x>=0;
const productionUint=x=>productionIndex(x)&&x<=4294967295;
const productionNullableBool=x=>x===null||typeof x==='boolean';
function productionEpoch(frame){const p=[frame?.worldId,frame?.sample?.sessionId,frame?.sample?.loadEpoch];return p.every(x=>typeof x==='string'&&x.trim())?JSON.stringify(p):null}
function productionRates(rows,requirements=false){
 if(!Array.isArray(rows)||rows.length>16)return null;
 if(rows.some(r=>!r||!productionResource(r.resource)||!productionNumber(requirements?r.amount:r.unitsPerSecond)||!requirements&&(!productionText(r.flowMode)||typeof r.dumpExcess!=='boolean')))return null;
 return rows.map(r=>requirements?{resource:r.resource,amount:r.amount}:{resource:r.resource,unitsPerSecond:r.unitsPerSecond,flowMode:r.flowMode,dumpExcess:r.dumpExcess});
}
function productionVector(v){if(!v)return null;const inputs=productionRates(v.inputs),outputs=productionRates(v.outputs),requirements=productionRates(v.requirements,true);return inputs&&outputs&&requirements?{inputs,outputs,requirements}:null}
function inspectProduction(raw){
 if(!raw||!['partial','unavailable','truncated'].includes(raw.status)||!['complete-supported','partial'].includes(raw.inventoryStatus)||!productionText(raw.reason)||!productionNumber(raw.observedUt)||!Array.isArray(raw.modules)||raw.modules.length>32)return null;
 if(raw.status==='unavailable'&&raw.modules.length)return null;
 const omitted=raw.budgetOmittedModuleCount===undefined?0:raw.budgetOmittedModuleCount,selection=raw.budgetSelectionSequence===undefined?null:raw.budgetSelectionSequence;
 if(!Number.isSafeInteger(omitted)||omitted<0||omitted>32||raw.modules.length+omitted>32||selection!==null&&(!Number.isSafeInteger(selection)||selection<0)||omitted>0&&(raw.status!=='truncated'||raw.inventoryStatus!=='partial'||selection===null)||omitted===0&&selection!==null)return null;
 const modules=[],seen=new Set();
 for(const m of raw.modules){
  if(!m||!productionUint(m.partId)||m.moduleId!==null&&!productionUint(m.moduleId)||!productionIndex(m.moduleIndex)||m.bayIndex!==null&&!productionIndex(m.bayIndex)||m.selectedLoadout!==null&&!productionIndex(m.selectedLoadout)||!productionText(m.moduleType)||!m.moduleType||!productionText(m.partName)||!productionText(m.recipe)||typeof m.recipeHash!=='string'||!/^[a-f0-9]{64}$/i.test(m.recipeHash)||!productionNullableBool(m.enabled)||!productionNullableBool(m.activated)||!['loaded-broker','background-model','proto-config'].includes(m.basis)||!productionText(m.reason)||m.nativeStatus!==null&&!productionText(m.nativeStatus))return null;
  const key=m.partId+'/'+m.moduleIndex,configured=productionVector(m.configured);if(seen.has(key)||!configured)return null;seen.add(key);
  let prepared=null,achieved=null,background=null,harvester=null,hopper=null;
  if(m.prepared!==null){const p=m.prepared,rates=productionVector(p?.rates);if(!p||!productionNumber(p.sampleUt)||!productionNumber(p.efficiencyMultiplier)||!productionNumber(p.requirementMultiplier)||!rates)return null;prepared={sampleUt:p.sampleUt,efficiencyMultiplier:p.efficiencyMultiplier,requirementMultiplier:p.requirementMultiplier,rates}}
  if(m.achieved!==null){const a=m.achieved,inputs=productionRates(a?.inputs),outputs=productionRates(a?.outputs);if(!a||!Number.isSafeInteger(a.captureSequence)||a.captureSequence<=0||!productionNumber(a.sampleUt)||!Number.isFinite(a.intervalGameSeconds)||a.intervalGameSeconds<=0||!productionNumber(a.timeFactor)||!inputs||!outputs)return null;achieved={captureSequence:a.captureSequence,sampleUt:a.sampleUt,intervalGameSeconds:a.intervalGameSeconds,timeFactor:a.timeFactor,inputs,outputs}}
  if(m.background!==null){const b=m.background,inputs=productionRates(b?.inputs),outputs=productionRates(b?.outputs);if(!b||!productionNumber(b.sampleUt)||!productionText(b.constraintState)||!inputs||!outputs)return null;background={sampleUt:b.sampleUt,constraintState:b.constraintState,inputs,outputs}}
  if(m.harvester!==null){const h=m.harvester;if(!h||!productionResource(h.resource)||!productionNumber(h.efficiency)||!productionNumber(h.harvestThreshold)||!productionIndex(h.harvesterType))return null;harvester={resource:h.resource,efficiency:h.efficiency,harvestThreshold:h.harvestThreshold,harvesterType:h.harvesterType}}
  if(m.hopper!==null){const h=m.hopper;if(!h||!productionText(h.hopperId)||!productionNullableBool(h.connected)||!productionText(h.body)||!productionText(h.biome)||!Array.isArray(h.allocationPoints)||h.allocationPoints.length>16||h.allocationPoints.some(r=>!productionResource(r.resource)||!productionNumber(r.points)))return null;hopper={hopperId:h.hopperId,connected:h.connected,body:h.body,biome:h.biome,allocationPoints:h.allocationPoints.map(r=>({resource:r.resource,points:r.points}))}}
  modules.push({key,partId:m.partId,moduleId:m.moduleId,moduleIndex:m.moduleIndex,moduleType:m.moduleType,partName:m.partName,bayIndex:m.bayIndex,selectedLoadout:m.selectedLoadout,recipe:m.recipe,recipeHash:m.recipeHash,enabled:m.enabled,activated:m.activated,basis:m.basis,configured,prepared,achieved,background,nativeStatus:m.nativeStatus,reason:m.reason,harvester,hopper});
 }
 return {status:raw.status,inventoryStatus:raw.status==='partial'?raw.inventoryStatus:'partial',reason:raw.reason,observedUt:raw.observedUt,budgetOmittedModuleCount:omitted,budgetSelectionSequence:selection,modules};
}
function productionAchievedCurrent(m,observation,frame){const ut=frame?.sample?.utSeconds,a=m.achieved;return !!(a&&frame.status==='live'&&frame.publisherConnected===true&&productionNumber(frame.ageSeconds)&&frame.ageSeconds<=6&&frame.sample?.activeWorld===true&&observation.observationBasis==='loaded'&&m.basis==='loaded-broker'&&m.enabled===true&&m.activated===true&&productionNumber(ut)&&ut>=a.sampleUt&&ut-a.sampleUt<=10)}
function applyProductionObservation(previous,observation,frame,receivedAt){
 if(typeof applyDemandObservation==='function')previous=applyDemandObservation(previous,observation,frame,receivedAt);
 const epoch=productionEpoch(frame),same=!!epoch&&previous.productionEpoch===epoch;
 const record={...previous,productionEpoch:epoch,productionCurrent:false,productionObservation:null,productionCaptureState:same?{...previous.productionCaptureState}:{},productionBudgetSequence:same?previous.productionBudgetSequence??null:null};
 record.productionConfiguration=same?(previous.productionConfiguration||mergeProductionConfiguration(null,previous.productionObservation,epoch,previous.productionObservedAt)):null;
 const priorState=record.productionCaptureState;record.productionCaptureState=Object.fromEntries(Object.entries(priorState).map(([k,v])=>[k,{...v,eligible:false,resumeAfterBudget:false}]));
 if(!epoch||!Number.isSafeInteger(frame.sample?.sequence)||frame.sample.sequence<0||!['live','paused'].includes(frame.status)||frame.sample?.activeWorld!==true)return record;
 const ut=frame.sample.utSeconds,utReset=same&&productionNumber(previous.productionClockUt)&&ut<previous.productionClockUt;
 if(productionNumber(ut))record.productionClockUt=ut;
 const p=inspectProduction(observation.production);if(!p||!productionNumber(ut)||p.observedUt>ut)return record;
 if(p.budgetSelectionSequence!==null){if(record.productionBudgetSequence!==null&&p.budgetSelectionSequence<record.productionBudgetSequence)return record;record.productionBudgetSequence=p.budgetSelectionSequence}
 const omitted=p.budgetOmittedModuleCount>0,reported=new Set(p.modules.map(m=>m.key));
 // The omission marker identifies payload loss only. No cached measurement is
 // current during that loss. A later explicit, valid re-observation can reuse
 // the callback only if budget omission was the sole reason it was excluded.
 const knownMissing=(record.productionConfiguration?.modules||[]).filter(m=>!reported.has(m.key)).length;
 const mayResume=omitted&&p.budgetOmittedModuleCount>=knownMissing&&!utReset&&frame.status==='live'&&frame.publisherConnected===true&&productionNumber(frame.ageSeconds)&&frame.ageSeconds<=6&&observation.observationBasis==='loaded';
 if(mayResume)for(const [key,old] of Object.entries(priorState))if(!reported.has(key))record.productionCaptureState[key]={...old,eligible:false,resumeAfterBudget:old.eligible===true||old.resumeAfterBudget===true};
 for(const m of p.modules){const a=m.achieved,old=priorState[m.key],signature=JSON.stringify([m.moduleId,m.moduleType,m.recipeHash,m.bayIndex,m.selectedLoadout,m.enabled,m.activated,m.basis,m.configured,m.harvester,m.hopper,m.prepared?.efficiencyMultiplier,m.prepared?.requirementMultiplier,a]);let valid=!utReset&&productionAchievedCurrent(m,observation,frame);
  if(a&&old&&(a.captureSequence<old.sequence||a.captureSequence===old.sequence&&(!(old.eligible||old.resumeAfterBudget)||signature!==old.signature)))valid=false;
  if(a&&(!old||a.captureSequence>=old.sequence))record.productionCaptureState[m.key]={sequence:a.captureSequence,signature,eligible:valid,resumeAfterBudget:false};
  if(!valid)m.achieved=null;
 }
 record.productionConfiguration=mergeProductionConfiguration(record.productionConfiguration,p,epoch,receivedAt);
 record.productionObservation=p;record.productionCurrent=true;record.productionObservedAt=receivedAt;record.productionFrameSequence=frame.sample.sequence;
 return record;
}
// Ignore obsolete transport frames before they can roll back any building data.
function productionFrameOrder(frame,previous){
 const epoch=productionEpoch(frame),prior=previous?.productionTransport;
 if(!epoch||!Number.isSafeInteger(frame.sample?.sequence)||frame.sample.sequence<0)return {accepted:true,state:prior||null};
 const sequence=frame.sample.sequence,superseded=prior?.superseded||[];
 if(prior&&(superseded.includes(epoch)||(prior.epoch===epoch||JSON.parse(prior.epoch)[1]===frame.sample.sessionId)&&sequence<prior.sequence))return {accepted:false,state:prior};
 return {accepted:true,state:{epoch,sequence,superseded:prior&&prior.epoch!==epoch?[...superseded,prior.epoch].slice(-128):superseded}};
}
// Configuration history is separate from this frame's measurement eligibility.
// Retained entries keep their original inspection/capture times, never freshen
// on an omitted payload, and contribute no current rate by themselves.
function mergeProductionConfiguration(previous,production,epoch,receivedAt){
 const same=previous?.epoch===epoch,prior=same?previous:null;
 if(!production)return prior||null;
 const complete=production.inventoryStatus==='complete-supported'&&production.status==='partial';
 const before=new Map((prior?.modules||[]).map(m=>[m.key,m])),merged=new Map(complete?[]:before);
 for(const module of production.modules){const old=before.get(module.key),sameRecipe=old&&JSON.stringify([old.moduleId,old.moduleType,old.recipeHash,old.bayIndex,old.selectedLoadout,old.configured,old.harvester,old.hopper])===JSON.stringify([module.moduleId,module.moduleType,module.recipeHash,module.bayIndex,module.selectedLoadout,module.configured,module.harvester,module.hopper]);
  const references=direction=>[...new Set([...(sameRecipe?old.resourceReferences?.[direction]||[]:[]).map(resource=>({resource})),...module.configured[direction],...(module.prepared?.rates[direction]||[]),...(module.achieved?.[direction]||[]),...(module.background?.[direction]||[]),...(direction==='outputs'&&module.harvester?[{resource:module.harvester.resource}]:[])].map(r=>r.resource))].sort();
  merged.set(module.key,{...module,prepared:null,achieved:null,background:null,resourceReferences:{inputs:references('inputs'),outputs:references('outputs')},configurationObservedUt:production.observedUt,configurationReceivedAt:receivedAt,lastAchieved:module.achieved||sameRecipe&&old.lastAchieved||null,lastPrepared:module.prepared||sameRecipe&&old.lastPrepared||null,lastBackground:module.background||sameRecipe&&old.lastBackground||null});
 }
 // The native per-vessel inventory is bounded at 32; the larger historical
 // ceiling covers partial inspections during changes without growing forever.
 const modules=[...merged.values()].sort((a,b)=>(b.configurationObservedUt||0)-(a.configurationObservedUt||0)||a.key.localeCompare(b.key)).slice(0,128).sort((a,b)=>a.key.localeCompare(b.key));
 return {epoch,modules,completeSupported:complete||!!prior?.completeSupported,completeObservedUt:complete?production.observedUt:prior?.completeObservedUt??null};
}
function productionConfigurationSignature(record){
 const c=record?.productionConfiguration;if(!c)return null;
 return JSON.stringify([c.epoch,c.completeSupported,c.modules.map(m=>[m.key,m.moduleId,m.moduleType,m.partName,m.bayIndex,m.selectedLoadout,m.recipe,m.recipeHash,m.enabled,m.activated,m.basis,m.configured,m.harvester,m.hopper,m.resourceReferences])]);
}
