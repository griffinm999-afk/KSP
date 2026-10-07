function json(v,status=200){return Response.json(v,{status,headers:{'Cache-Control':'no-store'}})}
export default {async fetch(req,env){const u=new URL(req.url);
 if(u.pathname==='/assets/expanse-kerbal-panorama.png')return new Response(Uint8Array.from(atob(MASTHEAD_ASSET),c=>c.charCodeAt(0)),{headers:{'Content-Type':'image/png','Cache-Control':'public, max-age=86400','X-Content-Type-Options':'nosniff'}});
 if(u.pathname.startsWith('/assets/bodies/')){const data=BODY_ASSETS[decodeURIComponent(u.pathname.split('/').pop())];if(!data)return new Response('Not found',{status:404});return new Response(Uint8Array.from(atob(data),c=>c.charCodeAt(0)),{headers:{'Content-Type':'image/jpeg','Cache-Control':'public, max-age=86400'}})}
 if(u.pathname==='/api/catalog'){try{return json(await currentCatalog(env))}catch(error){console.error(error);return json({error:'Building catalog is temporarily unavailable. Please try again.'},503)}}
 if(req.method==='GET'&&u.pathname==='/api/deliveries')return json(DELIVERIES);
 if(u.pathname.startsWith('/api/')){try{
 const db=env.DB;if(!db)throw Error('Database unavailable');
 if(u.pathname.startsWith('/api/wolf/'))return await wolfRequest(req,env,u);
 if(u.pathname==='/api/live'||u.pathname.startsWith('/api/live/'))return await liveRequest(req,env,u.pathname);
 if(req.method==='GET'&&u.pathname==='/api/colonies'){
 const colonies=(await db.prepare('SELECT * FROM colonies ORDER BY created_at DESC,id').all()).results;
 const buildings=(await db.prepare('SELECT * FROM colony_buildings').all()).results;
 const registry=await readBuildingRegistry(db),retired=new Set(registry.buildings.filter(b=>b.lifecycleState==='retired').map(b=>b.id));
 return json(colonies.map(c=>({...c,buildings:buildings.filter(b=>b.colony_id===c.id&&!retired.has(b.id)),retiredBuildings:buildings.filter(b=>b.colony_id===c.id&&retired.has(b.id))})));
 }
 if(req.method!=='POST')return json({error:'Not found'},404);
 if(req.headers.get('Origin')!==u.origin)return json({error:'Invalid origin'},403);
 if(!req.headers.get('Content-Type')?.includes('application/json'))return json({error:'Expected JSON'},415);
 const raw=await req.text();if(raw.length>16000)return json({error:'Request too large'},413);
 let d;try{d=JSON.parse(raw)}catch{return json({error:'Invalid request'},400)}
 if(!d||typeof d.id!=='string'||!/^[a-f0-9-]{36}$/i.test(d.id))return json({error:'Invalid colony ID'},400);
 if(u.pathname==='/api/colonies/rename'){
 if(typeof d.name!=='string'||!d.name.trim()||d.name.trim().length>100)return json({error:'Enter a colony name'},400);
 const current=await db.prepare('SELECT * FROM colonies WHERE id=?').bind(d.id).first();if(!current)return json({error:'Colony not found'},404);
 const name=d.name.trim();await db.prepare('UPDATE colonies SET name=? WHERE id=?').bind(name,d.id).run();return json({id:d.id,name});
 }
 if(u.pathname==='/api/colonies/delete'){
 await db.batch([
 db.prepare('DELETE FROM colony_buildings WHERE colony_id=?').bind(d.id),
 db.prepare('DELETE FROM colonies WHERE id=?').bind(d.id)
 ]);
 return json({id:d.id,deleted:true});
 }
 let colony;
 if(u.pathname==='/api/colonies'){
 if(typeof d.name!=='string'||!d.name.trim()||d.name.trim().length>100)return json({error:'Enter a colony name'},400);
 if(!CATALOG.bodies.some(b=>b.name===d.body&&b.biomes.includes(d.biome)))return json({error:'Select a body and biome'},400);
 colony={id:d.id,name:d.name.trim(),body:d.body,location:d.biome};
 }else if(u.pathname==='/api/buildings'){
 colony=await db.prepare('SELECT * FROM colonies WHERE id=?').bind(d.id).first();if(!colony)return json({error:'Colony not found'},404);
 }else return json({error:'Not found'},404);
 if(!Array.isArray(d.buildings)||d.buildings.length>200||new Set(d.buildings).size!==d.buildings.length)return json({error:'Invalid building selection'},400);
 const current=await currentCatalog(env);
 const selected=d.buildings.map(id=>current.buildings.find(b=>b.id===id&&b.body===colony.body&&b.biome===colony.location));
 if(selected.some(b=>!b))return json({error:'Select buildings at this colony location'},400);
 const existing=await db.prepare('SELECT * FROM colonies WHERE id=?').bind(colony.id).first();if(existing&&(existing.body!==colony.body||existing.location!==colony.location||existing.name!==colony.name))return json({error:'This request was already saved with different details. Reopen the form.'},409);
 const statements=[];
 if(u.pathname==='/api/colonies')statements.push(db.prepare('INSERT INTO colonies (id,name,body,location,created_at) VALUES (?,?,?,?,?) ON CONFLICT(id) DO NOTHING').bind(colony.id,colony.name,colony.body,colony.location,new Date().toISOString()));
 for(const b of selected)statements.push(db.prepare('INSERT INTO colony_buildings (id,colony_id,name) SELECT ?,?,? WHERE NOT EXISTS (SELECT 1 FROM colony_buildings WHERE id=? AND colony_id=?)').bind(b.id,colony.id,b.name,b.id,colony.id));
 const assigned=(await db.prepare('SELECT id,colony_id FROM colony_buildings').all()).results;
 if(selected.some(b=>assigned.some(a=>a.id===b.id&&a.colony_id!==colony.id)))return json({error:'A selected building already belongs to another colony'},409);
 if(statements.length)await db.batch(statements);
 return json({id:colony.id},201);
 }catch(e){console.error(e);return json({error:'Unable to save or load colony records. Please try again.'},503)}}
 if(u.pathname!=='/'&&u.pathname!=='/index.html'&&u.pathname!=='/kerbin'&&!/^\/colonies\/[a-f0-9-]{36}\/?$/i.test(u.pathname))return new Response('Not found',{status:404});
 return new Response(PAGE,{headers:{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-cache','X-Content-Type-Options':'nosniff'}});
}};
