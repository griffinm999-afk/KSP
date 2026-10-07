// The Site access boundary remains owner-private. Ingestion also requires a
// relay-only secret, never included in PAGE or CATALOG.
async function liveRequest(req, env, path) {
 const db=env.DB,now=Date.now();
 const row=await db.prepare("SELECT * FROM live_feed WHERE id='current'").first();
 if(path==='/api/live'&&req.method==='GET') {
  if(!row||Number(row.demand_until)<now+4000)await db.prepare("INSERT INTO live_feed (id,frame,received_at,demand_until) VALUES ('current','null','0',?) ON CONFLICT(id) DO UPDATE SET demand_until=excluded.demand_until").bind(String(now+12000)).run();
  const receivedAt=Number(row?.received_at||0);return json({receivedAt,serverNow:now,serverAgeMs:receivedAt>0?Math.max(0,now-receivedAt):null,frame:row?JSON.parse(row.frame):null});
 }
 if(!env.LIVE_RELAY_KEY||req.headers.get('X-Expanse-Relay')!==env.LIVE_RELAY_KEY)return json({error:'Unauthorized'},403);
 if(path==='/api/live/demand'&&req.method==='GET')return json({active:Number(row?.demand_until||0)>now});
 if(path!=='/api/live/ingest'||req.method!=='POST')return json({error:'Not found'},404);
 if(Number(req.headers.get('Content-Length')||0)>262144)return json({error:'Too large'},413);
 const raw=await req.text();if(new TextEncoder().encode(raw).length>262144)return json({error:'Too large'},413);
 let frame;try{frame=JSON.parse(raw)}catch{return json({error:'Invalid JSON'},400)}
 if(frame.protocolVersion!==1||frame.messageType!=='clockView'||!['live','paused','stale','noWorld','waitingForKsp'].includes(frame.status))return json({error:'Invalid feed'},400);
 // Never mix a different save into this colony catalogue.
 if(frame.sample?.activeWorld&&(frame.sample.saveFolder!=='The Expanse'||frame.sample.installNamespace?.replaceAll('\\','/').replace(/\/$/,'').toLowerCase()!=='c:/kerbal space program'))return json({error:'Wrong game/save'},409);
 if(frame.colony?.vessels!==undefined&&(!Array.isArray(frame.colony.vessels)||frame.colony.vessels.some(v=>!v||typeof v.vesselId!=='string'))||frame.colony?.vessels?.length>24)return json({error:'Invalid vessels'},400);
 const previous=row?JSON.parse(row.frame):null,order=productionFrameOrder(frame,previous);if(!order.accepted)return json({accepted:false,reason:'Obsolete game-context frame'});
 // The native clock protocol bounds the entire UTF-8 frame. Production
 // rotation is explicit per vessel; do not silently erase an accepted frame.
 const sourceHash=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(JSON.stringify(frame.colony||null))))).map(b=>b.toString(16).padStart(2,'0')).join('');
 const clean={productionTransport:order.state,colonySourceHash:sourceHash,worldId:frame.worldId||null,runId:frame.runId||null,status:frame.status,publisherConnected:frame.publisherConnected,ageSeconds:frame.ageSeconds,sample:frame.sample?{activeWorld:frame.sample.activeWorld,sequence:frame.sample.sequence,sessionId:frame.sample.sessionId,loadEpoch:frame.sample.loadEpoch,utSeconds:frame.sample.utSeconds,formattedDate:frame.sample.formattedDate,scene:frame.sample.scene}:null,colony:frame.colony||null};
 clean.crewEpoch=crewEpoch(frame)||previous?.crewEpoch||null;const sameObservation=previous&&previous.worldId===clean.worldId&&previous.sample?.activeWorld===clean.sample?.activeWorld&&previous.sample?.sessionId===clean.sample?.sessionId&&previous.sample?.loadEpoch===clean.sample?.loadEpoch&&previous.status===clean.status&&previous.publisherConnected===clean.publisherConnected&&previous.ageSeconds===clean.ageSeconds&&previous.colonySourceHash===sourceHash&&(!frame.colony?.vessels?.some(v=>v.production||v.lifeSupport||v.powerAverage)||previous.sample?.utSeconds===clean.sample?.utSeconds);
 // Normalize before returning telemetry; invalid records cannot reach the client.
 clean.buildingDiscovery=sameObservation&&previous.buildingDiscovery?previous.buildingDiscovery:await registerBuildingObservations(env,frame,now);
 if(clean.colony?.vessels){const registry=await readBuildingRegistry(db),byId=new Map(registry.buildings.map(b=>[b.id,b]));clean.colony={...clean.colony,vessels:clean.colony.vessels.map(v=>{const b=byId.get(v.vesselId.toLowerCase());return {...v,lifeSupport:b?.demandEpoch===productionEpoch(frame)&&b.lifeSupportObservation?{...b.lifeSupportObservation,supply:b.lifeSupportEligible?.supply?b.lifeSupportObservation.supply:null,crewElectricity:b.lifeSupportEligible?.crewElectricity?b.lifeSupportObservation.crewElectricity:null}:null,powerAverage:b?.demandEpoch===productionEpoch(frame)&&b.powerAverageCurrent?b.powerAverageObservation:null,production:b?.productionCurrent&&b.productionEpoch===productionEpoch(frame)?b.productionObservation:null}})}}
 clean.vesselLifecycle=sameObservation&&previous.vesselLifecycle?previous.vesselLifecycle:await applyVesselLifecycle(env,frame,previous,now);
 clean.buildingDiscovery.revision=String(Math.max(Number(clean.buildingDiscovery.revision)||0,Number(clean.vesselLifecycle.revision)||0));
 await db.prepare("INSERT INTO live_feed (id,frame,received_at,demand_until) VALUES ('current',?,?,'0') ON CONFLICT(id) DO UPDATE SET frame=excluded.frame,received_at=excluded.received_at").bind(JSON.stringify(clean),String(now)).run();
 return json({accepted:true});
}
