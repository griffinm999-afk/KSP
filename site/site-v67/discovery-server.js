async function readBuildingRegistry(db){
 const rows=(await db.prepare('SELECT id,record,updated_at,revision,lifecycle_state,lifecycle_changed_at,lifecycle_reason,lifecycle_context FROM observed_buildings').all()).results;
 return {rows,buildings:rows.map(row=>({...JSON.parse(row.record),lifecycleState:row.lifecycle_state,lifecycleChangedAt:row.lifecycle_changed_at===null?null:Number(row.lifecycle_changed_at),lifecycleReason:row.lifecycle_reason,lifecycleContext:row.lifecycle_context})),revision:String(rows.reduce((n,row)=>Math.max(n,Number(row.revision)||0,Number(row.lifecycle_changed_at)||0),0))};
}
async function currentCatalog(env){
 const registry=await readBuildingRegistry(env.DB),buildings=new Map(CATALOG.buildings.map(b=>[b.id,b]));
 for(const building of registry.buildings)buildings.set(building.id,{...(buildings.get(building.id)||{}),...building});
 const feed=await env.DB.prepare("SELECT frame FROM live_feed WHERE id='current'").first();const frame=feed?JSON.parse(feed.frame):null;
 const history=(await env.DB.prepare('SELECT record FROM building_lifecycle_events ORDER BY CAST(created_at AS INTEGER) DESC LIMIT 200').all()).results.map(row=>JSON.parse(row.record));
 return {...CATALOG,buildings:[...buildings.values()].filter(b=>b.lifecycleState!=='retired'),retiredBuildings:[...buildings.values()].filter(b=>b.lifecycleState==='retired'),lifecycleHistory:history,lifecycleStatus:frame?.vesselLifecycle||{status:'unavailable',reason:'The game feed does not include a complete-world vessel census yet.'},discoveryRevision:registry.revision,crewEpoch:frame?.crewEpoch||null};
}
async function registerBuildingObservations(env,frame,receivedAt){
 const db=env.DB,registry=await readBuildingRegistry(db);
 const colonies=(await db.prepare('SELECT id,body,location FROM colonies').all()).results;
 const assignments=(await db.prepare('SELECT id,colony_id,name FROM colony_buildings').all()).results;
 const plan=planBuildingDiscovery({frame,catalog:CATALOG,colonies,assignments,registry:registry.buildings,receivedAt,now:receivedAt});
 const epoch=crewEpoch(frame);if(epoch)for(const previous of registry.buildings)if(previous.crewEpoch&&previous.crewEpoch!==epoch&&!plan.upserts.some(v=>v.id===previous.id))plan.upserts.push(clearCrewDetails(previous,epoch));
 if(typeof applyProductionObservation==='function')for(const previous of registry.buildings)if(!plan.upserts.some(v=>v.id===previous.id)){const observed=frame.colony?.vessels?.find(v=>v.vesselId===previous.id&&v.body===previous.body&&v.biome===previous.biome);plan.upserts.push(applyProductionObservation(previous,observed||{},frame,receivedAt))}
 if(typeof applyProductionObservation==='function')for(let i=0;i<plan.upserts.length;i++)if(plan.upserts[i].productionEpoch!==productionEpoch(frame)){const record=plan.upserts[i],observed=frame.colony?.vessels?.find(v=>v.vesselId===record.id&&v.body===record.body&&v.biome===record.biome);plan.upserts[i]=applyProductionObservation(record,observed||{},frame,receivedAt)}
 const rowById=new Map(registry.rows.map(row=>[row.id,row])),records=new Map(registry.buildings.map(b=>[b.id,b]));
 const statements=[];let revision=registry.revision;
 for(const record of plan.upserts){const previous=records.get(record.id),oldRow=rowById.get(record.id);const changed=!previous||['name','body','biome','crewEpoch','crewRosterComplete','crewRosterCurrent','physicalCrewCapacity','crewCapacityCurrent'].some(k=>previous[k]!==record[k])||JSON.stringify(previous?.crewRoster)!==JSON.stringify(record.crewRoster)||productionConfigurationSignature(previous)!==productionConfigurationSignature(record)||previous?.productionEpoch!==record.productionEpoch||plan.assignments.some(a=>a.id===record.id)||plan.renames.some(a=>a.id===record.id);const recordRevision=changed?String(receivedAt):oldRow.revision;revision=String(Math.max(Number(revision),Number(recordRevision)));
  const stored={...record};for(const key of ['lifecycleState','lifecycleChangedAt','lifecycleReason','lifecycleContext'])delete stored[key];
  statements.push(db.prepare('INSERT INTO observed_buildings (id,record,updated_at,revision) VALUES (?,?,?,?) ON CONFLICT(id) DO UPDATE SET record=excluded.record,updated_at=excluded.updated_at,revision=excluded.revision').bind(record.id,JSON.stringify(stored),String(receivedAt),recordRevision));
 }
 for(const a of plan.assignments)statements.push(db.prepare('INSERT INTO colony_buildings (id,colony_id,name) VALUES (?,?,?) ON CONFLICT(id) DO NOTHING').bind(a.id,a.colonyId,a.name));
 for(const a of plan.renames)statements.push(db.prepare('UPDATE colony_buildings SET name=? WHERE id=? AND colony_id=?').bind(a.name,a.id,a.colonyId));
 if(statements.length)await db.batch(statements);
 return {revision,added:plan.assignments.length,renamed:plan.renames.length};
}
