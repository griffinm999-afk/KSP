const demandEpsilon=1e-6;
const demandNumber=x=>Number.isFinite(x)&&x>=0;
const demandId=x=>Number.isSafeInteger(x)&&x>=1;
const demandReason=x=>typeof x==='string'&&x.length<=360;
function inspectLifeSupport(raw,crew){
 if(!raw||!['partial','unavailable'].includes(raw.status)||!demandReason(raw.reason)||!demandNumber(raw.observedUt))return null;
 const stream=(s,kind)=>{
  if(!s||!demandId(s.captureSequence)||!Number.isInteger(s.crewCount)||s.crewCount<1||s.crewCount>4096||s.crewCount!==crew||!demandNumber(s.sampleUt)||!demandNumber(s.processedEndUt)||!Number.isFinite(s.intervalGameSeconds)||s.intervalGameSeconds<=0||s.processedEndUt<s.intervalGameSeconds||s.processedEndUt>s.sampleUt+demandEpsilon||s.sampleUt>raw.observedUt||raw.observedUt-s.sampleUt>10||!demandNumber(s.timeFactor))return null;
  const fields=kind==='supply'?['recyclerMultiplier','grossSuppliesPerSecond','grossMulchPerSecond','configuredSuppliesPerSecond','configuredMulchPerSecond','suppliesConsumed','mulchProduced']:['configuredEcPerSecond','electricityConsumed'];if(fields.some(k=>!demandNumber(s[k]))||(kind==='supply'?['suppliesConsumed','mulchProduced']:['electricityConsumed']).some(k=>!Number.isFinite(s[k]/s.intervalGameSeconds)))return null;
  return Object.fromEntries(['sampleUt','processedEndUt','intervalGameSeconds','captureSequence','crewCount','timeFactor',...fields].map(k=>[k,s[k]]));
 };
 return {status:raw.status,reason:raw.reason,observedUt:raw.observedUt,supply:raw.status==='partial'?stream(raw.supply,'supply'):null,crewElectricity:raw.status==='partial'?stream(raw.crewElectricity,'crewElectricity'):null};
}
function inspectPowerAverage(p){
 if(!p||!demandId(p.windowId)||!['observed','partial'].includes(p.status)||!['fulfilled-part-requests','supported-native-callbacks'].includes(p.basis)||!demandReason(p.reason))return null;
 const fields=['windowStartUt','windowEndUt','windowGameSeconds','coveredGameSeconds','reportRealSeconds','ageRealSeconds','generationEc','consumptionEc','generationEcPerSecond','consumptionEcPerSecond'];if(fields.some(k=>!demandNumber(p[k]))||p.windowGameSeconds<=0||p.coveredGameSeconds<=0||p.coveredGameSeconds>p.windowGameSeconds+demandEpsilon||p.windowEndUt<=p.windowStartUt||Math.abs(p.windowEndUt-p.windowStartUt-p.windowGameSeconds)>demandEpsilon||p.reportRealSeconds<60||p.ageRealSeconds>120)return null;
 const near=(a,b)=>Number.isFinite(a)&&Number.isFinite(b)&&Math.abs(a-b)<=1e-6*Math.max(1,Math.abs(a),Math.abs(b));if(!near(p.generationEc/p.coveredGameSeconds,p.generationEcPerSecond)||!near(p.consumptionEc/p.coveredGameSeconds,p.consumptionEcPerSecond)||p.status==='observed'&&(p.basis!=='fulfilled-part-requests'||Math.abs(p.coveredGameSeconds-p.windowGameSeconds)>demandEpsilon))return null;
 return Object.fromEntries(['windowId',...fields,'status','basis','reason'].map(k=>[k,p[k]]));
}
function applyDemandObservation(previous,observation,frame,receivedAt){
 const epoch=productionEpoch(frame),same=!!epoch&&previous.demandEpoch===epoch,ut=frame.sample?.utSeconds;
 const oldStates=same?previous.demandCaptureState||{}:{},states=Object.fromEntries(Object.entries(oldStates).map(([k,s])=>[k,{...s,eligible:false}]));
 const result={...previous,demandEpoch:epoch,demandCaptureState:states,lifeSupportObservation:null,lifeSupportEligible:{supply:false,crewElectricity:false},powerAverageObservation:null,powerAverageCurrent:false};
 const rewind=same&&demandNumber(previous.demandClockUt)&&ut<previous.demandClockUt;if(demandNumber(ut))result.demandClockUt=ut;
 if(!epoch||!Number.isSafeInteger(frame.sample?.sequence)||frame.sample.sequence<0||!demandNumber(ut)||frame.sample?.activeWorld!==true)return result;
 const live=frame.status==='live'&&frame.publisherConnected===true&&demandNumber(frame.ageSeconds)&&frame.ageSeconds<=6&&observation.observationBasis==='loaded'&&!rewind;
 const life=inspectLifeSupport(observation.lifeSupport,observation.crew??observation.crewCount);
 if(life&&life.observedUt<=ut+demandEpsilon){result.lifeSupportObservation=life;for(const key of ['supply','crewElectricity']){const s=life[key];if(!s)continue;const old=oldStates[key],signature=JSON.stringify(s);let eligible=live&&s.sampleUt<=ut+demandEpsilon&&ut-s.sampleUt<=10&&Math.abs(s.processedEndUt-s.sampleUt)<=demandEpsilon;
  if(old&&(s.captureSequence<old.sequence||s.captureSequence===old.sequence&&(!old.eligible||signature!==old.signature)))eligible=false;
  if(!old||s.captureSequence>=old.sequence)states[key]={sequence:s.captureSequence,signature,eligible};result.lifeSupportEligible[key]=eligible;
 }}
 const p=inspectPowerAverage(observation.powerAverage),old=oldStates.power;
 if(p){const signature=JSON.stringify({...p,ageRealSeconds:0}),fresh=(live||frame.status==='paused'&&frame.publisherConnected===true&&demandNumber(frame.ageSeconds)&&frame.ageSeconds<=6&&observation.observationBasis==='loaded'&&!rewind)&&p.windowEndUt<=ut+demandEpsilon;let eligible=fresh;
  if(old&&(p.windowId<old.sequence||p.windowId===old.sequence&&(!old.eligible||signature!==old.signature||p.ageRealSeconds+demandEpsilon<old.ageRealSeconds)))eligible=false;
  if(!old||p.windowId>=old.sequence)states.power={sequence:p.windowId,signature,eligible,ageRealSeconds:p.ageRealSeconds};
  if(eligible){result.powerAverageObservation=p;result.powerAverageCurrent=true;result.powerAverageHistorical=frame.status==='paused'}
 }
 result.demandObservedAt=receivedAt;return result;
}
