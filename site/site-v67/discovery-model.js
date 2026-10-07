// Partial observations may add or rename known assets; absence never deletes one.
function planBuildingDiscovery({frame,catalog,colonies,assignments,registry=[],receivedAt,now=Date.now()}){
 const result={upserts:[],assignments:[],renames:[],skipped:[]};
 if(!frame||frame.sample?.activeWorld!==true||!['live','paused'].includes(frame.status)||!Number.isFinite(receivedAt)||now-receivedAt>6000||frame.colony?.status!=='observed')return result;
 const seen=new Set(),existing=new Map([...catalog.buildings,...registry].map(v=>[v.id,v])),owners=new Map(assignments.map(a=>[a.id,a]));
 for(const observation of frame.colony.vessels||[]){
  const id=typeof observation.vesselId==='string'?observation.vesselId.toLowerCase():observation.vesselId,name=observation.name??observation.vesselName,body=observation.body,biome=observation.biome;
  if(typeof id!=='string'||!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(id)||seen.has(id))continue;seen.add(id);
  // Colony protocol v1 already admits only landed/splashed USI/WOLF/MKS
  // settlement vessels. It does not emit situation; reject contrary fields
  // if a future publisher does include one.
  const situation=String(observation.situation||'').toUpperCase();
  const validLocation=catalog.bodies.some(b=>b.name===body&&b.biomes.includes(biome));
  if(typeof name!=='string'||!name.trim()||name.length>200||!validLocation||(situation&&!['LANDED','SPLASHED'].includes(situation))){result.skipped.push({id,reason:'Incomplete or unsupported vessel identity/location/situation'});continue}
  if(['EVA','FLAG','DEBRIS','SPACEOBJECT'].includes(String(observation.type||'').toUpperCase())){result.skipped.push({id,reason:'Not a building vessel'});continue}
  // Kerbin is a special hub. Ground-vessel discovery must not create a second
  // recovery sink or attach orbital ships to a surface-colony charter.
  if(body==='Kerbin'){result.skipped.push({id,reason:'Kerbin infrastructure uses its separate hub'});continue}
  const previous=existing.get(id);let record={...(previous||{}),id,name:name.trim(),body,biome,admissionBasis:'publisher-settlement-v1',discoveredAt:previous?.discoveredAt||receivedAt,lastObservedAt:receivedAt};
  const crew=observation.crew??observation.crewCount;if(Number.isInteger(crew)&&crew>=0)record.crew=crew;
  for(const key of ['latitude','longitude'])if(Number.isFinite(observation[key]))record[key]=observation[key];
  if(Array.isArray(observation.tanks)){const resources=new Set(observation.tanks.map(t=>t.resource));record.tanks=[...(previous?.tanks||[]).filter(t=>!resources.has(t.resource)),...observation.tanks]}
  record=applyCrewObservation(record,observation,frame,receivedAt);
  if(typeof applyProductionObservation==='function')record=applyProductionObservation(record,observation,frame,receivedAt);
  record.observationBasis=observation.observationBasis||record.observationBasis;
  result.upserts.push(record);
  const owner=owners.get(id);if(owner){if(owner.name!==record.name)result.renames.push({id,name:record.name,colonyId:owner.colony_id});continue}
  const matches=colonies.filter(c=>c.body===body&&c.location===biome);
  if(matches.length===1){const assignment={id,name:record.name,colonyId:matches[0].id};result.assignments.push(assignment);owners.set(id,{...assignment,colony_id:assignment.colonyId})}
 }
 return result;
}
