async function applyVesselLifecycle(env,frame,previousFrame,receivedAt){
 const db=env.DB,registry=await readBuildingRegistry(db);
 const contexts=(await db.prepare('SELECT * FROM vessel_census_contexts ORDER BY rowid DESC').all()).results;
 const pinnedWorldId=contexts[0]?.world_id||previousFrame?.worldId||null;
 const inspection=inspectVesselCensus(frame,previousFrame,pinnedWorldId);
 const base={status:inspection.status,reason:inspection.reason,revision:registry.revision,retired:0,reactivated:0};
 if(!inspection.attempt)return base;
 const context=inspection.context,prior=contexts.find(c=>c.context_key===context.key),latest=contexts[0];
 // A previously seen load/session cannot become current again. A save rewind
 // gets a new opaque loadEpoch and is evaluated on its own complete census.
 if(prior&&latest&&latest.context_key!==context.key&&(latest.session_id!==context.sessionId||latest.load_epoch!==context.loadEpoch))return {...base,status:'ignored',reason:'Census belongs to a superseded game-load context.'};
 if(prior&&inspection.sequence<=prior.highest_sequence)return {...base,status:inspection.sequence===prior.highest_sequence?prior.status:'ignored',reason:inspection.sequence===prior.highest_sequence?'This census attempt was already processed.':'An older census attempt was ignored.',observationSequence:prior.highest_sequence};
 const statements=[db.prepare('INSERT INTO vessel_census_contexts (context_key,world_id,session_id,load_epoch,scene,highest_sequence,status,received_at,observed_ut) VALUES (?,?,?,?,?,?,?,?,?) ON CONFLICT(context_key) DO UPDATE SET highest_sequence=excluded.highest_sequence,status=excluded.status,scene=excluded.scene,received_at=excluded.received_at,observed_ut=excluded.observed_ut WHERE excluded.highest_sequence>vessel_census_contexts.highest_sequence AND NOT EXISTS (SELECT 1 FROM vessel_census_contexts newer WHERE newer.rowid>vessel_census_contexts.rowid AND (newer.session_id<>vessel_census_contexts.session_id OR newer.load_epoch<>vessel_census_contexts.load_epoch))').bind(context.key,context.worldId,context.sessionId,context.loadEpoch,context.scene,inspection.sequence,inspection.status,String(receivedAt),String(inspection.observedUt))];
 const guard="EXISTS (SELECT 1 FROM vessel_census_contexts WHERE context_key=? AND highest_sequence=?) AND ?=(SELECT context_key FROM vessel_census_contexts ORDER BY rowid DESC LIMIT 1)";
 const records=new Map(CATALOG.buildings.map(b=>[b.id,{...b,lifecycleState:'active'}]));for(const b of registry.buildings)records.set(b.id,{...(records.get(b.id)||{}),...b});
 // The imported catalog and registry both belong to this Site's fixed,
 // authenticated The Expanse source. Seed missing baseline records here,
 // never in schema migrations, before applying authoritative retirement.
 const existing=new Set(registry.buildings.map(b=>b.id)),transitions=planVesselLifecycle([...records.values()],inspection);
 for(const t of transitions){const b=t.record;if(!existing.has(b.id))statements.push(db.prepare('INSERT INTO observed_buildings (id,record,updated_at,revision) VALUES (?,?,?,?) ON CONFLICT(id) DO NOTHING').bind(b.id,JSON.stringify(b),String(Date.parse(CATALOG.observedAt)||0),'0'));
  const eventId=JSON.stringify([context.key,inspection.sequence,b.id,t.to]);const event={id:eventId,vesselId:b.id,name:b.name,body:b.body,biome:b.biome,from:t.from,to:t.to,reason:t.reason,observedAt:receivedAt,observedUt:inspection.observedUt,context:context.key,scene:context.scene,observationSequence:inspection.sequence,lastKnownCrew:Number.isInteger(b.crew)?b.crew:null,lastKnownSeats:Number.isInteger(b.physicalCrewCapacity)?b.physicalCrewCapacity:null,lastKnownRoster:Array.isArray(b.crewRoster)?b.crewRoster:null,rosterObservedAt:b.crewRosterObservedAt||null,capacityObservedAt:b.crewCapacityObservedAt||null,censusVesselCount:inspection.ids.length};
  statements.push(db.prepare('INSERT INTO building_lifecycle_events (id,vessel_id,record,created_at) SELECT ?,?,?,? WHERE '+guard+' AND EXISTS (SELECT 1 FROM observed_buildings WHERE id=? AND lifecycle_state=?) ON CONFLICT(id) DO NOTHING').bind(eventId,b.id,JSON.stringify(event),String(receivedAt),context.key,inspection.sequence,context.key,b.id,t.from));
  statements.push(db.prepare('UPDATE observed_buildings SET lifecycle_state=?,lifecycle_changed_at=?,lifecycle_reason=?,lifecycle_context=? WHERE id=? AND lifecycle_state=? AND '+guard).bind(t.to,String(receivedAt),t.reason,context.key,b.id,t.from,context.key,inspection.sequence,context.key));
 }
 await db.batch(statements);
 const after=await readBuildingRegistry(db);
 const applied=transitions.filter(t=>after.buildings.some(b=>b.id===t.record.id&&b.lifecycleState===t.to&&b.lifecycleChangedAt===receivedAt&&b.lifecycleContext===context.key));
 return {...base,status:inspection.status,reason:inspection.reason,observationSequence:inspection.sequence,revision:after.revision,retired:applied.filter(t=>t.to==='retired').length,reactivated:applied.filter(t=>t.to==='active').length};
}
