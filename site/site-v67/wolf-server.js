function wolfPublic(row){return row?{id:row.id,status:row.status,result:row.result?JSON.parse(row.result):null}:null}
async function wolfRequest(req,env,u){
 const db=env.DB,now=Date.now(),path=u.pathname;
 const relay=!!env.LIVE_RELAY_KEY&&req.headers.get('X-Expanse-Relay')===env.LIVE_RELAY_KEY;
 if(path==='/api/wolf/command'&&req.method==='GET')return json(wolfPublic(await db.prepare('SELECT * FROM wolf_commands WHERE id=?').bind(u.searchParams.get('id')).first()));
 if(path==='/api/wolf/next'&&req.method==='GET'){
  if(!relay)return json({error:'Unauthorized'},403);
  await db.prepare("UPDATE wolf_commands SET status='expired' WHERE status='pending' AND CAST(created_at AS INTEGER)<?").bind(now-30000).run();
  const claimed=await db.prepare("UPDATE wolf_commands SET status='executing' WHERE id=(SELECT id FROM wolf_commands WHERE status='pending' ORDER BY created_at LIMIT 1) AND status='pending' RETURNING *").first();
  return json(claimed?{id:claimed.id,request:JSON.parse(claimed.request)}:null);
 }
 if(req.method!=='POST')return json({error:'Not found'},404);
 if(path==='/api/wolf/result'){if(!relay)return json({error:'Unauthorized'},403)}
 else if(req.headers.get('Origin')!==u.origin)return json({error:'Invalid origin'},403);
 const raw=await req.text();if(raw.length>8000)return json({error:'Too large'},413);
 let d;try{d=JSON.parse(raw)}catch{return json({error:'Invalid JSON'},400)}
 if(!d||typeof d.id!=='string'||!/^[a-f0-9-]{36}$/i.test(d.id))return json({error:'Invalid request ID'},400);
 if(path==='/api/wolf/result'){
  const row=await db.prepare('SELECT * FROM wolf_commands WHERE id=?').bind(d.id).first();if(!row)return json({error:'Unknown request'},404);
  const result=d.result,request=JSON.parse(row.request);
  if(!result||result.requestId!==d.id||!['applied','duplicate','rejected','uncertain'].includes(result.status))return json({error:'Invalid receipt'},400);
  if(['applied','duplicate'].includes(result.status)&&(!Number.isInteger(result.incoming)||result.incoming!==request.targetIncoming||result.outgoing!==request.expectedOutgoing||result.available!==result.incoming-result.outgoing))return json({error:'Receipt differs from request'},409);
  if(row.status==='executing')await db.prepare("UPDATE wolf_commands SET status=?,result=? WHERE id=? AND status='executing'").bind(result.status,JSON.stringify(result),d.id).run();
  return json({accepted:true});
 }
 if(path!=='/api/wolf/edit')return json({error:'Not found'},404);
 const prior=await db.prepare('SELECT * FROM wolf_commands WHERE id=?').bind(d.id).first();if(prior){const old=JSON.parse(prior.request);if(old.resource!==d.resource||old.sessionId!==d.sessionId||old.loadEpoch!==d.loadEpoch||old.expectedIncoming!==d.expectedIncoming||old.expectedOutgoing!==d.expectedOutgoing||old.targetIncoming!==d.targetIncoming)return json({error:'Request ID was already used for another change.'},409);return json(wolfPublic(prior));}
 const colony=await db.prepare('SELECT * FROM colonies WHERE id=?').bind(d.colonyId).first();if(!colony)return json({error:'Colony not found'},404);
 const feed=await db.prepare("SELECT * FROM live_feed WHERE id='current'").first(),f=feed?JSON.parse(feed.frame):null;
 if(!f||now-Number(feed.received_at)>6000||!['live','paused'].includes(f.status)||!f.sample?.activeWorld||f.colony?.wolf?.status!=='observed'||!f.worldId||!f.runId)return json({error:'A current KSP WOLF reading is required. Wait for the live feed.'},409);
 if(d.sessionId!==f.sample.sessionId||d.loadEpoch!==f.sample.loadEpoch)return json({error:'The loaded game changed. Reopen the editor.'},409);
 const depot=f.colony.wolf.depots.find(x=>x.body===colony.body&&x.biome===colony.location);
 if(!depot?.established)return json({error:'No established WOLF depot at this site.'},409);
 if(typeof d.resource!=='string'||!(f.colony.wolf.allowedResources||[]).includes(d.resource))return json({error:'Choose an installed WOLF resource.'},400);
 const row=depot.resources.find(x=>x.name===d.resource)||{incoming:0,outgoing:0};
 if(d.expectedIncoming!==row.incoming||d.expectedOutgoing!==row.outgoing)return json({error:'WOLF supply or allocation changed. Reopen the editor.'},409);
 if(!Number.isInteger(d.targetIncoming)||d.targetIncoming<row.outgoing||d.targetIncoming>1000000)return json({error:'Supply must be a whole number between allocated capacity and 1,000,000.'},400);
 const request={requestId:d.id,worldId:f.worldId,runId:f.runId,sessionId:f.sample.sessionId,loadEpoch:f.sample.loadEpoch,body:colony.body,biome:colony.location,resource:d.resource,expectedIncoming:row.incoming,expectedOutgoing:row.outgoing,targetIncoming:d.targetIncoming};
 await db.prepare("INSERT INTO wolf_commands (id,request,status,created_at) SELECT ?,?,'pending',? WHERE NOT EXISTS (SELECT 1 FROM wolf_commands WHERE status IN ('pending','executing') AND CAST(created_at AS INTEGER)>?)").bind(d.id,JSON.stringify(request),String(now),now-60000).run();
 const queued=await db.prepare('SELECT * FROM wolf_commands WHERE id=?').bind(d.id).first();
 return queued?json(wolfPublic(queued),202):json({error:'Another WOLF change is in progress.'},409);
}
