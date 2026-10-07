function vesselCensusContext(frame){
 const sample=frame?.sample;
 if(typeof frame?.worldId!=='string'||!frame.worldId||typeof sample?.sessionId!=='string'||!sample.sessionId||typeof sample.loadEpoch!=='string'||!sample.loadEpoch.trim()||typeof sample.scene!=='string'||!sample.scene)return null;
 return {key:JSON.stringify([frame.worldId,sample.sessionId,sample.loadEpoch]),worldId:frame.worldId,sessionId:sample.sessionId,loadEpoch:sample.loadEpoch,scene:sample.scene};
}
function inspectVesselCensus(frame,previousFrame,pinnedWorldId=null){
 const census=frame?.colony?.vesselCensus;
 if(!census)return {attempt:false,status:'unavailable',reason:'The game feed does not include a complete-world vessel census yet.'};
 const context=vesselCensusContext(frame),sample=frame?.sample;
 if(!context||sample.activeWorld!==true||frame.publisherConnected!==true||!['live','paused'].includes(frame.status)||!Number.isFinite(frame.ageSeconds)||frame.ageSeconds<0||frame.ageSeconds>6||!Number.isFinite(frame.colony?.observedUt)||frame.colony.observedUt<0)return {attempt:false,status:'ignored',reason:'Census is not from a ready, fresh game context.'};
 if(pinnedWorldId&&context.worldId!==pinnedWorldId)return {attempt:false,status:'ignored',reason:'Census belongs to a different managed world.'};
 const previousContext=vesselCensusContext(previousFrame);
 // Clock sequence is used only to reject older transport frames within the
 // same game load. Census freshness is exclusively observationSequence.
 if(previousContext&&previousContext.worldId===context.worldId&&previousContext.sessionId===context.sessionId&&previousContext.loadEpoch===context.loadEpoch&&Number.isSafeInteger(sample.sequence)&&Number.isSafeInteger(previousFrame.sample.sequence)&&sample.sequence<previousFrame.sample.sequence)return {attempt:false,status:'ignored',reason:'Census arrived with an older game-context frame.'};
 if(!Number.isSafeInteger(census.observationSequence)||census.observationSequence<0)return {attempt:false,status:'ignored',reason:'Census observation sequence is missing or invalid.'};
 const result={attempt:true,context,sequence:census.observationSequence,observedUt:frame.colony.observedUt,status:census.status,complete:false,ids:[],reason:null};
 if(!['complete','unavailable','truncated'].includes(census.status)||!Array.isArray(census.vesselIds)||census.vesselIds.length>512)return {...result,status:'invalid',reason:'Census status or ID list is invalid.'};
 if(census.status!=='complete')return census.vesselIds.length?{...result,status:'invalid',reason:'An incomplete census must not contain IDs.'}:{...result,reason:'Waiting for a complete current-world vessel census.'};
 const guid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
 if(census.vesselIds.some(id=>typeof id!=='string'||!guid.test(id)||/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(id)))return {...result,status:'invalid',reason:'Census contains an invalid vessel ID.'};
 const ids=census.vesselIds.map(id=>id.toLowerCase()),unique=new Set(ids);
 if(unique.size!==ids.length)return {...result,status:'invalid',reason:'Census contains duplicate vessel IDs.'};
 if((frame.colony.vessels||[]).some(v=>typeof v.vesselId!=='string'||!unique.has(v.vesselId.toLowerCase())))return {...result,status:'invalid',reason:'Census omits a vessel reported by the same observation.'};
 return {...result,complete:true,ids};
}
function planVesselLifecycle(records,inspection){
 if(!inspection.complete)return [];
 const alive=new Set(inspection.ids),transitions=[];
 for(const record of records){const state=record.lifecycleState||'active',next=alive.has(record.id)?'active':'retired';if(state!==next)transitions.push({record,from:state,to:next,reason:next==='retired'?'Absent from complete current-world vessel census':'Present again in complete current-world vessel census'})}
 return transitions;
}
