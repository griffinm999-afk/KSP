// Freshness uses server-relative age and a local monotonic receipt clock.
// receivedAt remains provenance; it is never compared across device clocks
// when the current response protocol supplies serverAgeMs.
function liveMonotonicNow(){return typeof performance!=='undefined'&&typeof performance.now==='function'?performance.now():Date.now()}
function markLiveResponse(result,started=liveMonotonicNow(),mono=liveMonotonicNow(),wall=Date.now()){
 if(!result||typeof result!=='object')return result;
 const startMono=typeof started==='object'?started.mono:started,startWall=typeof started==='object'?started.wall:wall;const requestMs=Math.max(Number.isFinite(startMono)&&mono>=startMono?mono-startMono:0,Number.isFinite(startWall)&&wall>=startWall?wall-startWall:0);return {...result,_liveClock:{mono,wall,requestMs}};
}
function liveObservationAge(result,wallNow=Date.now(),monoNow=liveMonotonicNow()){
 if(!result)return Infinity;
 if(Object.hasOwn(result,'serverAgeMs')){const c=result._liveClock;if(!Number.isFinite(result.serverAgeMs)||result.serverAgeMs<0||!c||!Number.isFinite(c.mono)||!Number.isFinite(c.wall)||!Number.isFinite(c.requestMs)||c.requestMs<0)return Infinity;const monotonicElapsed=monoNow-c.mono,wallElapsed=wallNow-c.wall;if(monotonicElapsed<0)return Infinity;const elapsed=Math.max(monotonicElapsed,Number.isFinite(wallElapsed)?Math.max(0,wallElapsed):0);return result.serverAgeMs+c.requestMs+elapsed}
 const received=Number(result.receivedAt),age=wallNow-received;return Number.isFinite(received)&&received>0&&age>=0?age:Infinity;
}
function liveResponseFresh(result,wallNow=Date.now()){return !result?._liveError&&liveObservationAge(result,wallNow)<=6000}
function crewEpoch(frame){
 const sample=frame?.sample;if(sample?.activeWorld!==true||typeof sample.sessionId!=='string'||!sample.sessionId||typeof sample.loadEpoch!=='string'||!sample.loadEpoch.trim())return null;
 return JSON.stringify([frame.worldId??null,sample.sessionId,sample.loadEpoch]);
}
function clearCrewDetails(record,epoch){
 const clean={...record,crewEpoch:epoch,crewRosterComplete:false,crewRosterCurrent:false,physicalCrewCapacity:null,crewCapacityCurrent:false};
 for(const key of ['crewRoster','crewRosterObservedAt','crewRosterObservedUt','crewCapacityObservedAt','crewCapacityObservedUt'])delete clean[key];
 return clean;
}
function applyCrewObservation(previous,observation,frame,receivedAt){
 const epoch=crewEpoch(frame);if(!epoch)return clearCrewDetails(previous,null);
 let record=previous.crewEpoch===epoch?{...previous}:clearCrewDetails(previous,epoch);
 record.crewRosterCurrent=false;record.crewCapacityCurrent=false;record.crewEpoch=epoch;
 const count=observation.crew??observation.crewCount;if(Number.isInteger(count)&&count>=0){record.crew=count;record.crewCountObservedAt=receivedAt;record.crewCountObservedUt=frame.colony?.observedUt??null}
 const roster=observation.crewRoster;
 const valid=observation.crewRosterComplete===true&&Number.isInteger(count)&&Array.isArray(roster)&&roster.length===count&&roster.length<=256&&roster.every(k=>typeof k.name==='string'&&k.name.trim()&&k.name.length<=120&&typeof k.profession==='string'&&k.profession.trim()&&k.profession.length<=100)&&new Set(roster.map(k=>k.name)).size===roster.length;
 if(valid){record.crewRoster=roster.map(k=>({name:k.name,profession:k.profession}));record.crewRosterComplete=true;record.crewRosterCurrent=true;record.crewRosterObservedAt=receivedAt;record.crewRosterObservedUt=frame.colony?.observedUt??null}
 if(Number.isInteger(observation.physicalCrewCapacity)&&observation.physicalCrewCapacity>=0){record.physicalCrewCapacity=observation.physicalCrewCapacity;record.crewCapacityCurrent=true;record.crewCapacityObservedAt=receivedAt;record.crewCapacityObservedUt=frame.colony?.observedUt??null}
 return record;
}
function crewScopeSummary(buildings,colony,epoch){
 const vessels=buildings.filter(v=>v.lifecycleState!=='retired'&&v.body===colony.body&&v.biome===colony.location);
 const counts=vessels.filter(v=>Number.isInteger(v.crew)&&v.crew>=0),population=counts.reduce((n,v)=>n+v.crew,0);
 const currentEpoch=v=>!!epoch&&v.crewEpoch===epoch;
 const recordedMembers=vessels.flatMap(v=>currentEpoch(v)&&v.crewRosterComplete&&Array.isArray(v.crewRoster)?v.crewRoster.map(k=>({...k,vesselId:v.id,vesselName:v.name,observedAt:v.crewRosterObservedAt,current:v.crewRosterCurrent===true})):[]);
 const newestByName=new Map();for(const member of recordedMembers){const previous=newestByName.get(member.name);if(!previous||(member.observedAt||0)>(previous.observedAt||0))newestByName.set(member.name,member)}const members=[...newestByName.values()];
 const professions=new Map();for(const member of members)professions.set(member.profession,(professions.get(member.profession)||0)+1);
 const capacityVessels=vessels.filter(v=>currentEpoch(v)&&Number.isInteger(v.physicalCrewCapacity)&&v.physicalCrewCapacity>=0);
 const emptyVessels=capacityVessels.filter(v=>Number.isInteger(v.crew)&&v.crew>=0&&v.physicalCrewCapacity>=v.crew);
 const rosterComplete=vessels.length>0&&counts.length===vessels.length&&vessels.every(v=>currentEpoch(v)&&v.crewRosterComplete&&v.crewRoster?.length===v.crew)&&members.length===recordedMembers.length;
 return {vessels,members,professions,population,populationComplete:vessels.length>0&&counts.length===vessels.length,rosterComplete,seatTotal:capacityVessels.reduce((n,v)=>n+v.physicalCrewCapacity,0),seatKnown:capacityVessels.length,seatsComplete:vessels.length>0&&capacityVessels.length===vessels.length,emptyTotal:emptyVessels.reduce((n,v)=>n+v.physicalCrewCapacity-v.crew,0),emptyKnown:emptyVessels.length,emptyComplete:vessels.length>0&&emptyVessels.length===vessels.length};
}
function crewLiveCoverage(scope,live,epoch,now=Date.now()){
 const frame=live?.frame,fresh=!!frame&&liveResponseFresh(live,now)&&['live','paused'].includes(frame.status)&&frame.colony?.status==='observed'&&crewEpoch(frame)===epoch;
 const ids=new Set(scope.vessels.map(v=>v.id)),observed=fresh?(frame.colony.vessels||[]).filter(v=>ids.has(v.vesselId)&&Number.isInteger(v.crew??v.crewCount)):[];
 const seen=new Set(),unique=observed.filter(v=>{if(seen.has(v.vesselId))return false;seen.add(v.vesselId);return true});
 const observedPopulation=unique.reduce((n,v)=>n+(v.crew??v.crewCount),0);
 return {observedBuildings:unique.length,observedPopulation,retainedPopulation:Math.max(0,scope.population-observedPopulation),vesselIds:[...seen]};
}
