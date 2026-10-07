// Physical tank inventories only. WOLF points and configured recipe rates are not stock flows.
const SUPPLY_DAY_SECONDS=21600;
const SUPPLY_PLANNING_PER_PERSON=10.8;
const supplyResourceName=name=>typeof name==='string'&&name.length<=160&&name.trim().length>0;
function supplyEpoch(frame){const p=[frame?.worldId,frame?.sample?.sessionId,frame?.sample?.loadEpoch];return p.every(x=>typeof x==='string'&&x.length>0)?JSON.stringify(p):null}
function supplyTankTotals(tanks){const out=new Map();for(const t of Array.isArray(tanks)?tanks:[]){if(typeof t.resource!=='string'||!t.resource||!Number.isFinite(t.amount)||t.amount<0||!Number.isFinite(t.capacity)||t.capacity<0)continue;const r=out.get(t.resource)||{amount:0,capacity:0};r.amount+=t.amount;r.capacity+=t.capacity;out.set(t.resource,r)}return out}
// Old saved recipes lack native part IDs. Suppress their duplicate relationship
// only when one saved row and one currently inspected module agree unambiguously.
function supplyRecipeIdentity(part,recipe,bay,outputs){
 const text=x=>typeof x==='string'?x.trim().replace(/\s+/g,' '):'';part=text(part);recipe=text(recipe);if(!part||!recipe||!Array.isArray(outputs)||!outputs.length||outputs.some(x=>!supplyResourceName(x?.resource)))return null;
 if(bay!==null){if(typeof bay==='string'){const match=/^(?:Bay\s*)?([1-9]\d*)$/i.exec(bay.trim());bay=match?Number(match[1]):NaN}if(!Number.isSafeInteger(bay)||bay<1)return null}
 return JSON.stringify([part,recipe,bay,[...new Set(outputs.map(x=>x.resource))].sort()]);
}
function supplyRepresentedSavedRecipes(saved,modules){
 const savedKeys=saved.map(p=>supplyRecipeIdentity(p.part,p.recipe,p.bay,p.outputs));
 const moduleKeys=modules.map(m=>supplyRecipeIdentity(m.partName,m.recipe,m.bayIndex===null?null:m.bayIndex+1,m.configured.outputs));
 return new Set(saved.filter((p,i)=>savedKeys[i]!==null&&savedKeys.filter(k=>k===savedKeys[i]).length===1&&moduleKeys.filter(k=>k===savedKeys[i]).length===1&&modules[moduleKeys.indexOf(savedKeys[i])]?.retained===false));
}
function supplySnapshot(catalog,colony,result,now=Date.now(),context={}){
 const frame=result?.frame,epoch=supplyEpoch(frame),received=Number(result?.receivedAt),age=typeof liveObservationAge==='function'?liveObservationAge(result,now):now-received;
 const fresh=!!(!result?._liveError&&epoch&&frame.sample?.activeWorld&&['live','paused'].includes(frame.status)&&Number.isFinite(received)&&age>=0&&age<=6000&&['observed','truncated'].includes(frame.colony?.status));
 const buildings=(catalog.buildings||[]).filter(b=>b.body===colony.body&&b.biome===colony.location&&b.lifecycleState!=='retired');
 const raw=new Map();if(fresh)for(const v of frame.colony?.vessels||[])if(v.body===colony.body&&v.biome===colony.location&&typeof v.vesselId==='string')raw.set(v.vesselId,v);
 const resources=new Map();const include=(name,origin)=>{if(!supplyResourceName(name))return null;let r=resources.get(name);if(!r){r={name,amount:null,capacity:null,current:0,retained:0,stores:[],origins:[],deliveries:[]};resources.set(name,r)}if(origin&&!r.origins.includes(origin))r.origins.push(origin);return r};include('Supplies','crew planning');
 const recipes=[],telemetry=[],demandTelemetry=[];const telemetryById=new Map();
 if(typeof applyProductionObservation==='function'&&typeof supplyProductionView==='function')for(const b of buildings){const v=raw.get(b.id),record=applyProductionObservation(b,v||{},frame||{},received);if(typeof supplyDemandView==='function')demandTelemetry.push(supplyDemandView({...b,crew:v?.crew??v?.crewCount??b.crew},record,Math.max(0,age)/1000));const t=supplyProductionView(b,record);if(t){telemetry.push(t);telemetryById.set(b.id,t)}}
 for(const t of demandTelemetry){if(t.lifeSupport){include('Supplies','USI life support');include('Mulch','USI life support');include('ElectricCharge','USI life support')}if(t.powerAverage)include('ElectricCharge','completed power window')}
 const reportedRecipe=typeof supplyTelemetryRecipe==='function'?supplyTelemetryRecipe:null;
 let observedCrew=0,retainedCrew=0,crewKnown=0;
 for(const b of buildings){const v=raw.get(b.id);for(const t of [...(Array.isArray(b.tanks)?b.tanks:[]),...(Array.isArray(v?.tanks)?v.tanks:[])])include(t?.resource,'inventory');const saved=supplyTankTotals(b.tanks),reported=supplyTankTotals(v?.tanks);const names=new Set([...saved.keys(),...reported.keys()]);
  for(const name of names){const live=reported.has(name),t=reported.get(name)||saved.get(name),r=include(name,'inventory');if(!r)continue;r.amount=(r.amount??0)+t.amount;r.capacity=(r.capacity??0)+t.capacity;r[live?'current':'retained']+=t.amount;r.stores.push({id:b.id,name:v?.name||b.name,amount:t.amount,capacity:t.capacity,current:live,basis:live?v.observationBasis:'retained'});resources.set(name,r)}
  const count=v?.crew??v?.crewCount??b.crew;if(Number.isInteger(count)&&count>=0){crewKnown++;if(v&&Number.isInteger(v.crew??v.crewCount))observedCrew+=count;else retainedCrew+=count}
  const t=telemetryById.get(b.id),savedRecipes=b.production||[],represented=supplyRepresentedSavedRecipes(savedRecipes,t?.production.modules||[]);for(const p of savedRecipes){for(const x of [...(p.inputs||[]),...(p.outputs||[]),...(p.required||[])])include(x.resource,'saved recipe reference');if((!t||!reportedRecipe||!t.configurationComplete)&&!represented.has(p))recipes.push({...p,...(t?.production.modules.length?{telemetryUnmatched:true,source:'Saved relationship · partial module inventory',inputs:(p.inputs||[]).map(x=>({...x,baseRate:null})),outputs:(p.outputs||[]).map(x=>({...x,baseRate:null}))}:{}),buildingId:b.id,building:b.name})}
  if(t&&reportedRecipe)for(const m of t.production.modules){recipes.push(reportedRecipe(m,b,t.production.observedUt));for(const x of [...m.configured.inputs,...m.configured.outputs,...m.configured.requirements,...(m.prepared?.rates.inputs||[]),...(m.prepared?.rates.outputs||[]),...(m.achieved?.inputs||[]),...(m.achieved?.outputs||[]),...(m.background?.inputs||[]),...(m.background?.outputs||[])])include(x.resource,m.retained?'last known module configuration':'module telemetry');for(const name of [...(m.resourceReferences?.inputs||[]),...(m.resourceReferences?.outputs||[])])include(name,m.retained?'last known module configuration':'module telemetry');if(m.harvester)include(m.harvester.resource,'harvester output');for(const x of m.hopper?.allocationPoints||[])include(x.resource,'WOLF allocation')}
 }
 for(const p of recipes){for(const t of p.inputs||[])include(t.resource,'recipe demand');for(const t of p.outputs||[])include(t.resource,'recipe output');for(const t of p.required||[])include(t.resource,'required inventory')}
 const worldId=frame?.worldId||context.worldId||catalog.worldId;const sourceRefs=(catalog.productionSources||[]).filter(x=>x.body===colony.body&&x.biome===colony.location&&(!x.worldId||x.worldId===worldId)&&(!x.buildingId||buildings.some(b=>b.id===x.buildingId))&&(!telemetryById.has(x.buildingId)||!telemetryById.get(x.buildingId).configurationComplete&&!telemetryById.get(x.buildingId).production.modules.some(m=>m.partId===Number(x.partId))));for(const ref of sourceRefs)include(ref.resource,'configured source');
 // Delivery IDs are depot endpoints, not vessel IDs. Discover names at world
 // scope without attributing a shipment or converting cargo into a daily rate.
 const delivery=context.deliveries;
 const deliveryAvailable=!!(delivery&&typeof worldId==='string'&&delivery.worldId===worldId);
 if(deliveryAvailable){for(const [kind,records] of [['shipment',delivery.shipments],['route',delivery.routes],['history',delivery.history],['rule',delivery.rules]])for(const record of Array.isArray(records)?records:[]){const items=Array.isArray(record.cargo)?record.cargo:record.cargo&&typeof record.cargo==='object'?[record.cargo]:[];for(const item of [...items,...(kind==='rule'&&record.resource?[{resource:record.resource,amount:record.batchAmount}]:[])]){const r=include(item?.resource,'delivery snapshot');if(r)r.deliveries.push({kind,id:record.id??null,route:record.route||record.id||'Unnamed saved record',amount:Number.isFinite(item.amount)?item.amount:null,savedAt:delivery.savedAt??null,scope:'world'})}}}

 const productionClassifications=buildings.map(b=>{const t=telemetryById.get(b.id),refs=sourceRefs.filter(r=>r.buildingId===b.id);if(t&&t.currentInventoryComplete)return {id:b.id,name:b.name,resources:[],kind:'supported-inspected'};const resources=[...new Set(refs.filter(r=>!Number.isFinite(r.baseRate)).map(r=>r.resource))],knownProducer=!!t?.production.modules.length||refs.length>0||Array.isArray(b.production)&&b.production.length>0;const verifiedNonproducer=!knownProducer&&b.productionClassification==='nonproducer'&&b.productionInventoryComplete===true;return {id:b.id,name:b.name,discoveredAt:b.discoveredAt??null,lastObservedAt:b.lastObservedAt??null,resources,kind:resources.length?'missing-rates':knownProducer?'producer':verifiedNonproducer?'nonproducer':'unclassified'}}).sort((a,b)=>(b.discoveredAt||0)-(a.discoveredAt||0));
 const missingProductionRates=productionClassifications.filter(b=>b.kind==='missing-rates'),unclassifiedProductionBuildings=productionClassifications.filter(b=>b.kind==='unclassified'),nonproducingBuildings=productionClassifications.filter(b=>b.kind==='nonproducer'),unreportedProductionBuildings=[...missingProductionRates,...unclassifiedProductionBuildings];
 const lastReported=Array.isArray(frame?.colony?.vessels)?buildings.filter(b=>frame.colony.vessels.some(v=>v.vesselId===b.id&&v.body===colony.body&&v.biome===colony.location)).length:null;
 const reason=result?._liveError?'Site feed request failed':!frame?'No game observation received':!['live','paused'].includes(frame.status)?'Game source: '+(frame.status||'unavailable'):!Number.isFinite(age)?'Feed freshness unavailable':age>6000?'Game feed stale':!epoch||!frame.sample?.activeWorld?'No active world observation':!['observed','truncated'].includes(frame.colony?.status)?'Building readings unavailable':frame.status==='paused'?'Paused':frame.colony.status==='truncated'?'Partial observation':'Observing';
 const complete=fresh&&frame.status==='live'&&frame.colony.status==='observed';
 const cohort=buildings.map(b=>b.id).sort();
 return {resources,recipes,sourceRefs,telemetry,demandTelemetry,clockUt:frame?.sample?.utSeconds,unreportedProductionBuildings,missingProductionRates,unclassifiedProductionBuildings,nonproducingBuildings,transportAgeMs:age,lastReported,deliveryAvailable,deliveryDate:deliveryAvailable?delivery.savedAt:null,buildings:buildings.length,observed:buildings.filter(b=>raw.has(b.id)).length,observedCrew,retainedCrew,crewKnown,crewTotal:observedCrew+retainedCrew,epoch,observedIds:[...raw.keys()].sort(),ut:frame?.colony?.observedUt,receivedAt:received,fresh,complete,paused:frame?.status==='paused',cohort,recipeDate:catalog.productionObservedAt,stockDate:catalog.observedAt,gameDate:frame?.sample?.formattedDate||'',reason};
}
function createSupplySampler(){let previous=null,lastProgress=0;const rates=new Map();return {reset(){previous=null;rates.clear();lastProgress=0},update(s,now=Date.now()){
 if(!s.complete||!s.epoch||!Number.isFinite(s.ut)||s.ut<=0){this.reset();return rates}
 const key=JSON.stringify([s.epoch,s.cohort,s.observedIds]);if(previous&&(previous.key!==key||s.ut<previous.ut)){this.reset()}
 const current=new Map();for(const [name,r] of s.resources){const stores=r.stores.filter(x=>x.current).sort((a,b)=>a.id.localeCompare(b.id));if(stores.length)current.set(name,{amount:stores.reduce((n,x)=>n+x.amount,0),signature:JSON.stringify(stores.map(x=>[x.id,x.capacity,x.basis]))})}
 if(previous&&s.ut===previous.ut){const signatures=JSON.stringify([...current].map(([n,r])=>[n,r.signature,r.amount]).sort());const prior=JSON.stringify([...previous.resources].map(([n,r])=>[n,r.signature,r.amount]).sort());if(signatures!==prior){rates.clear();previous={key,ut:s.ut,resources:current};lastProgress=now}else if(now-lastProgress>15000)this.reset();return rates}
 rates.clear();if(previous){const dt=s.ut-previous.ut;if(dt>0&&now-lastProgress<=15000)for(const [name,r] of current){const p=previous.resources.get(name);if(p&&p.signature===r.signature)rates.set(name,{perDay:(r.amount-p.amount)*SUPPLY_DAY_SECONDS/dt,seconds:dt,stores:s.resources.get(name).stores.filter(x=>x.current).length})}}
 previous={key,ut:s.ut,resources:current};lastProgress=now;return rates;
 }}}
function supplyPlanning(population,stock){const n=Number(population);if(population===null||population===undefined||String(population).trim()===''||!Number.isInteger(n)||n<0||n>1000000)return null;const demand=n*SUPPLY_PLANNING_PER_PERSON;return {population:n,demand,days:demand>0&&Number.isFinite(stock)&&stock>=0?stock/demand:null}}
function supplyStorageBands(resource,max=5){if(!resource||!(resource.amount>0))return [];const stores=resource.stores.filter(s=>s.amount>0).sort((a,b)=>b.amount-a.amount);const rows=stores.slice(0,max);if(stores.length>max)rows.push({id:'other',name:'Other storage ('+(stores.length-max)+')',amount:stores.slice(max).reduce((n,s)=>n+s.amount,0),current:stores.slice(max).every(s=>s.current)});return rows.map(s=>({...s,share:s.amount/resource.amount}))}
// Saved converter rows describe selected bays, not every possible recipe option.
// Totals are documented nominal rates only; missing producers never mean zero.
function supplyConfiguredFlow(snapshot,resource,references=[]){
 const rows=direction=>snapshot.recipes.filter(p=>(p[direction]||[]).some(x=>x.resource===resource)).map(p=>({buildingId:p.buildingId,building:p.building,recipe:p.recipe,bay:p.bay,enabled:p.enabled,telemetryUnmatched:p.telemetryUnmatched,module:p.module,source:p.source||'Saved recipe',inputs:p.inputs||[],outputs:p.outputs||[],perDay:(p[direction]||[]).filter(x=>x.resource===resource).every(x=>Number.isFinite(x.baseRate)&&x.baseRate>=0)?(p[direction]||[]).filter(x=>x.resource===resource).reduce((n,x)=>n+x.baseRate*SUPPLY_DAY_SECONDS,0):null}));
 const producers=rows('outputs'),consumers=rows('inputs');for(const ref of references)if(ref.resource===resource)producers.push({...ref,source:ref.source||'Verified hopper configuration',inputs:[],outputs:[{resource}],perDay:Number.isFinite(ref.baseRate)&&ref.baseRate>=0?ref.baseRate*SUPPLY_DAY_SECONDS:null});
 const summarize=list=>{const enabled=list.filter(x=>x.enabled===true),known=enabled.filter(x=>Number.isFinite(x.perDay));return {total:list.length&&list.every(x=>typeof x.enabled==='boolean'&&(x.enabled===false||Number.isFinite(x.perDay)))?known.reduce((n,x)=>n+x.perDay,0):null,knownTotal:known.length?known.reduce((n,x)=>n+x.perDay,0):null,enabled:enabled.length,unknown:list.filter(x=>typeof x.enabled!=='boolean'||x.enabled&&!Number.isFinite(x.perDay)).length,configured:list.length}};
 return {producers,consumers,partial:!!snapshot.telemetry?.length,unreportedProductionBuildings:snapshot.unreportedProductionBuildings||[],production:summarize(producers),demand:summarize(consumers),actualProduction:null,actualDemand:null,actualBalance:null};
}
function supplyFlowComparisonBase(flow,resource,population,basis='plan'){
 if(basis!=='plan'){const supported=flow.basis===basis;return {basis,production:supported?flow.production.total:null,demand:supported?flow.demand.total:null,balance:null,state:'unknown',label:basis==='actual'?'Actual balance unavailable':basis==='potential'?'Potential only · balance unknown':'Modeled subtotal · balance unknown'}};
 const production=flow.production.total,facilityDemand=flow.demand.total;const crew=resource==='Supplies'?supplyPlanning(population,null):null;
 // No listed recipe consumers means unknown, except Supplies' explicit crew-only scenario.
 const demand=resource==='Supplies'?(crew?crew.demand+(flow.consumers.length?facilityDemand??NaN:0):null):facilityDemand;
 const known=Number.isFinite(production)&&Number.isFinite(demand);const raw=known?production-demand:null;const balance=known&&Math.abs(raw)<=Number.EPSILON*8*Math.max(1,production,demand)?0:raw;
 if(flow.partial)return {basis:'plan',production,demand:Number.isFinite(demand)?demand:null,documentedBalance:balance,balance:null,state:'unknown',label:'Supported configuration · balance unknown'};
 if(flow.unreportedProductionBuildings?.length)return {basis:'plan',production,demand:Number.isFinite(demand)?demand:null,documentedBalance:balance,balance:null,state:'unknown',label:flow.unreportedProductionBuildings.some(b=>b.kind==='missing-rates')?'Balance unknown · producer rates missing':'Balance unverified · module roles unclassified',unreportedProductionBuildings:flow.unreportedProductionBuildings};
 return {basis:'plan',production,demand:Number.isFinite(demand)?demand:null,balance,state:!known?'unknown':production===0&&demand===0?'idle':balance>=0?'covered':'shortfall',label:!known?'Planning balance unknown':production===0&&demand===0?'No saved-enabled flow':balance>=0?'Planning demand covered':'Planning shortfall'};
}

// Compare the known flows without claiming that missing flows are zero.
function supplyFlowComparison(flow,resource,population,basis='plan'){
 const overall=supplyFlowComparisonBase(flow,resource,population,basis),supported=!flow.meanUnavailable&&!flow.historical&&(basis==='plan'||flow.basis===basis);if(flow.historical)overall.label='Paused · historical power window';else if(flow.meanUnavailable)overall.label='Power mean unavailable · coverage gaps';
 const finite=x=>Number.isFinite(x)?x:null;
 const knownProduction=supported&&flow.producers.length?finite(flow.production.knownTotal??flow.production.total):null;
 let knownDemand=supported&&flow.consumers.length?finite(flow.demand.knownTotal??flow.demand.total):null;
 if(basis==='plan'&&resource==='Supplies'){const crew=supplyPlanning(population,null);if(crew)knownDemand=crew.demand+(knownDemand??0)}
 const raw=Number.isFinite(knownProduction)&&Number.isFinite(knownDemand)?knownProduction-knownDemand:null;
 const knownBalance=raw!==null&&Math.abs(raw)<=Number.EPSILON*8*Math.max(1,knownProduction,knownDemand)?0:raw;
 const knownState=knownBalance===null?'unknown':knownProduction===0&&knownDemand===0?'idle':knownBalance>=0?'covered':'shortfall';
 const prefix=basis==='plan'?'Known planning':basis==='actual'?'Measured subtotal':basis==='potential'?'Potential subtotal':'Modeled subtotal';
 if(!supported){overall.production=null;overall.demand=null}return {...overall,knownProduction,knownDemand,knownBalance,knownState,partial:overall.balance===null,knownLabel:knownBalance===null?overall.label:prefix+(knownBalance<0?' shortfall':knownBalance>0?' surplus':' balanced')};
}
function supplyVisibleComparison(comparison){
 return {...comparison,production:comparison.production??comparison.knownProduction,demand:comparison.demand??comparison.knownDemand,balance:comparison.balance??comparison.knownBalance,state:comparison.state==='unknown'?comparison.knownState:comparison.state,label:comparison.partial&&comparison.knownBalance!==null?comparison.knownLabel+' · partial':comparison.label};
}
