const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const savePath = 'C:\\Kerbal Space Program\\saves\\The Expanse\\persistent.sfs';
const sitePath = 'C:\\Users\\griff\\Documents\\Codex\\2026-10-04\\task\\colony-deliveries-site';
const outputPath = 'C:\\Users\\griff\\Documents\\Codex\\2026-10-04\\task\\colony-roster-current.json';
const before = fs.statSync(savePath);
const saveBytes = fs.readFileSync(savePath);
const lines = saveBytes.toString('utf8').split(/\r?\n/);
const after = fs.statSync(savePath);
if (before.mtimeMs !== after.mtimeMs || before.size !== after.size || saveBytes.length !== after.size) {
  throw Error('The game save changed during the read; rerun against a stable save.');
}

function parseConfig(lines) {
  const root = {name:'ROOT', properties:{}, children:[]};
  const stack = [root];
  let pending = '';
  for (const [index, raw] of lines.entries()) {
    const line = raw.trim();
    if (!line) continue;
    if (line === '{') {
      const node = {name:pending, properties:{}, children:[], line:index+1};
      stack.at(-1).children.push(node);
      stack.push(node);
      pending = '';
    } else if (line === '}') {
      if (stack.length < 2) throw Error('Unbalanced save node');
      stack.pop();
    } else {
      const equal = line.indexOf('=');
      if (equal < 0) pending = line;
      else (stack.at(-1).properties[line.slice(0,equal).trim()] ??= []).push(line.slice(equal+1).trim());
    }
  }
  if (stack.length !== 1) throw Error('Incomplete save node');
  return root;
}
const children = (node, name) => node?.children.filter(x => x.name === name) || [];
const value = (node, name) => node?.properties[name]?.[0] || '';
const normalizedId = id => String(id).replaceAll('-', '').toLowerCase();
const game = children(parseConfig(lines), 'GAME')[0];
const flight = children(game, 'FLIGHTSTATE')[0];
const vessels = children(flight, 'VESSEL');
const roster = children(children(game, 'ROSTER')[0], 'KERBAL');
const byName = new Map(roster.map(k => [value(k,'name'), {
  trait:value(k,'trait') || 'Unknown', type:value(k,'type') || 'Unknown',
  rosterState:value(k,'state') || 'Unknown'
}]));
if (byName.size !== roster.length) throw Error('Duplicate KSP roster names');

const stateNode = children(children(game, 'SCENARIO').find(x => value(x,'name') === 'ColonyRuntime'), 'COLONY_STATE')[0];
if (!stateNode) throw Error('Current colony state is missing');
const payload = Buffer.from(value(stateNode,'payload').replaceAll('-','+').replaceAll('_','/'),'base64');
if (crypto.createHash('sha256').update(payload).digest('hex') !== value(stateNode,'sha256')) throw Error('Colony state checksum mismatch');
const colonyState = JSON.parse(payload.toString('utf8'));
const ilus = colonyState.Colonies.find(c => c.Name === 'Ilus' && c.Site?.Body === 'Minmus' && c.Site?.Biome === 'Greater Flats');
if (!ilus) throw Error('Ilus colony identity not found');
const visitorNames = new Set(ilus.VisitorRosterIds);
const residentNames = new Set(ilus.Residents.map(x => x.RosterId));

const catalog = JSON.parse(fs.readFileSync(path.join(sitePath,'catalog.json'),'utf8'));
const shipping = JSON.parse(fs.readFileSync(path.join(sitePath,'kerbin-shipping.json'),'utf8'));
const assigned = catalog.buildings.filter(x => x.body === 'Minmus' && x.biome === 'Greater Flats');
if (assigned.length !== 12) throw Error('Assigned Greater Flats building set changed; refresh classification');
const vesselMap = new Map(vessels.map(v => [normalizedId(value(v,'pid')),v]));
const physicalCrew = new Set();

const usedPartNames = new Set();
for (const b of assigned) {
  const v = vesselMap.get(normalizedId(b.id));
  if (!v || value(v,'sit') !== 'LANDED' || value(children(v,'ORBIT')[0],'REF') !== '3') throw Error('Assigned building is missing from current Minmus save: '+b.name);
  if (Math.abs(Number(value(v,'lat'))-b.latitude)>0.01 || Math.abs(Number(value(v,'lon'))-b.longitude)>0.01) throw Error('Building location changed since biome classification: '+b.name);
  for (const p of children(v,'PART')) usedPartNames.add(value(p,'name'));
}
for (const b of shipping.vessels) {
  const v = vesselMap.get(normalizedId(b.id));
  if (!v || value(v,'sit') !== 'ORBITING' || value(children(v,'ORBIT')[0],'REF') !== '1') throw Error('Kerbin infrastructure identity changed: '+b.name);
  for (const p of children(v,'PART')) usedPartNames.add(value(p,'name'));
}

const definitions = new Map();
function walk(directory) {
  for (const entry of fs.readdirSync(directory,{withFileTypes:true})) {
    const full = path.join(directory,entry.name);
    if (entry.isDirectory()) walk(full);
    else if (entry.name.toLowerCase().endsWith('.cfg')) {
      let text;
      try { text=fs.readFileSync(full,'utf8'); } catch { continue; }
      if (!/^\s*PART\s*\{/m.test(text)) continue;
      const first = text.split(/\r?\n/).slice(0,220);
      let partName, capacity;
      for (const line of first) {
        const name = line.match(/^\s*name\s*=\s*([A-Za-z0-9_.-]+)/);
        const seats = line.match(/^\s*CrewCapacity\s*=\s*(\d+)/);
        if (!partName && name) partName=name[1].replaceAll('_','.');
        if (capacity === undefined && seats) capacity=Number(seats[1]);
      }
      if (partName && usedPartNames.has(partName) && !definitions.has(partName)) {
        definitions.set(partName,{capacity:capacity ?? 0,crewCapacityDeclared:capacity !== undefined,config:full});
      }
    }
  }
}
walk('C:\\Kerbal Space Program\\GameData');

function describe(v,biome,relation) {
  const parts = children(v,'PART');
  const crew = [];
  const seatParts = [];
  const unresolved = [];
  let seatCapacity = 0;
  for (const p of parts) {
    const partName = value(p,'name'), occupants = p.properties.crew || [];
    const definition = definitions.get(partName);
    if (occupants.length && (!definition || definition.capacity < occupants.length)) throw Error('Occupied part has no verified capacity: '+partName);
    if (definition?.capacity) {
      seatCapacity += definition.capacity;
      seatParts.push({partName,partPersistentId:value(p,'persistentId'),capacity:definition.capacity,occupied:occupants.length,config:definition.config});
    } else if (!definition) unresolved.push(partName);
    for (const name of occupants) {
      const identity = byName.get(name);
      if (!identity) throw Error('Crew member absent from game roster: '+name);
      if (physicalCrew.has(name)) throw Error('Crew member occupies multiple vessels: '+name);
      physicalCrew.add(name);
      crew.push({name,profession:identity.trait,rosterType:identity.type,rosterState:identity.rosterState,
        partName,partPersistentId:value(p,'persistentId'),
        colonyStatus:residentNames.has(name)?'resident':visitorNames.has(name)?'visitor':'notRegisteredInIlus'});
    }
  }
  return {vesselId:value(v,'pid'),name:value(v,'name'),type:value(v,'type'),body:relation==='assignedBiomeBuilding'?'Minmus':'Kerbin',
    biome,situation:value(v,'sit'),relation,latitude:Number(value(v,'lat')),longitude:Number(value(v,'lon')),
    occupied:crew.length,seatCapacity,emptySeats:seatCapacity-crew.length,
    qualifiedHousingCapacity:relation==='assignedBiomeBuilding'?0:null,
    capacity:{nominalCrewSeats:seatCapacity,occupied:crew.length,empty:seatCapacity-crew.length,
      seatParts,unresolvedPartNames:[...new Set(unresolved)].sort()},
    crew:crew.sort((a,b)=>a.name.localeCompare(b.name))};
}

const buildings = assigned.map(b => describe(vesselMap.get(normalizedId(b.id)),'Greater Flats','assignedBiomeBuilding'));
const kerbinOrbit = shipping.vessels.map(b => describe(vesselMap.get(normalizedId(b.id)),null,'kerbinOrbitInfrastructure'));
const allCrew = buildings.flatMap(x=>x.crew);
const byTrait = Object.fromEntries([...new Set(allCrew.map(x=>x.profession))].sort().map(t=>[t,allCrew.filter(x=>x.profession===t).length]));
const sum = key => buildings.reduce((n,x)=>n+x.capacity[key],0);
const habitatParts = buildings.flatMap(x=>x.capacity.seatParts.map(p=>({...p,vesselId:x.vesselId,vesselName:x.name}))).filter(x=>x.partName==='KKAOSS.Habitat.MK2.g');
const output = {
  schema:'expanse-roster-export-v1',
  source:{save:savePath,observedAt:value(game,'persistentTimestamp'),gameUt:Number(value(flight,'UT')),saveModifiedUtc:after.mtime.toISOString(),
    saveSha256:crypto.createHash('sha256').update(saveBytes).digest('hex'),
    colonyWorldId:colonyState.WorldId,colonyRevision:colonyState.Revision,colonyStateSha256:value(stateNode,'sha256'),
    biomeClassification:'catalog vessel ID and matching coordinates',catalogObservedAt:catalog.observedAt,
    capacityBasis:'Installed GameData PART CrewCapacity definitions; nominal crew seats, not certified homes',
    qualifiedHousingBasis:'Ilus saved colony facilities and certifications; no adopted facility or certified home exists',
    completeness:'All 12 catalog-assigned Greater Flats buildings resolved by stable vessel ID and current coordinates; all 39 occupants resolved to unique KSP roster names and traits. Kerbin list is the five site infrastructure IDs.'},
  colony:{id:ilus.Id,name:ilus.Name,site:ilus.Site,
    registeredResidents:ilus.Residents.length,registeredVisitors:ilus.VisitorRosterIds.length,
    registeredFacilities:ilus.Facilities.length,certifiedHomes:ilus.Facilities.reduce((n,f)=>n+(f.CertifiedHomes||0),0),
    supportStatus:ilus.SupportStatus},
  greaterFlats:{buildingCount:buildings.length,occupied:sum('occupied'),nominalCrewSeats:sum('nominalCrewSeats'),
    emptyCrewSeats:sum('empty'),byTrait,
    kpbsHabitatMk2Parts:habitatParts.length,potentialDedicatedHabitatSeats:habitatParts.reduce((n,x)=>n+x.capacity,0),
    buildings},
  kerbinOrbit:{vessels:kerbinOrbit}
};
if (output.greaterFlats.occupied !== 39 || allCrew.filter(x=>x.colonyStatus==='visitor').length !== ilus.VisitorRosterIds.length ||
    allCrew.filter(x=>x.colonyStatus==='resident').length !== ilus.Residents.length ||
    output.greaterFlats.nominalCrewSeats < output.greaterFlats.occupied) throw Error('Roster does not reconcile with colony state');
fs.writeFileSync(outputPath,JSON.stringify(output,null,2)+'\n');
console.log(JSON.stringify({outputPath,observedAt:output.source.observedAt,gameUt:output.source.gameUt,occupied:output.greaterFlats.occupied,
  seats:output.greaterFlats.nominalCrewSeats,empty:output.greaterFlats.emptyCrewSeats,byTrait,
  potentialHabitatSeats:output.greaterFlats.potentialDedicatedHabitatSeats,kerbinOrbit:kerbinOrbit.map(x=>({name:x.name,crew:x.crew.length,seats:x.capacity.nominalCrewSeats}))},null,2));
