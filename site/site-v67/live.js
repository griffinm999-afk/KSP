// Compare rendered values and patch existing nodes: retain focus, scroll and
// button handlers. Nothing is painted when the observation has not changed.
function patchLiveNode(old,fresh){
 if(old.nodeType!==fresh.nodeType||old.nodeName!==fresh.nodeName){old.replaceWith(fresh);return}
 if(old.nodeType===3){if(old.data!==fresh.data)old.data=fresh.data;return}
 if(old.nodeType!==1)return;
 if(fresh.onclick)old.onclick=fresh.onclick;
 for(const a of [...old.attributes])if(!fresh.hasAttribute(a.name))old.removeAttribute(a.name);
 for(const a of [...fresh.attributes])if(old.getAttribute(a.name)!==a.value)old.setAttribute(a.name,a.value);
 const oc=[...old.childNodes],nc=[...fresh.childNodes];
 for(let i=0;i<nc.length;i++){if(oc[i])patchLiveNode(oc[i],nc[i]);else old.append(nc[i])}
 for(let i=nc.length;i<oc.length;i++)oc[i].remove();
}
function retainResourceExpansion(old,fresh){
 const open=new Set([...old.querySelectorAll('.resource-expand[aria-expanded="true"]')].map(b=>b.textContent.slice(2)));
 for(const b of fresh.querySelectorAll('.resource-expand'))if(open.has(b.textContent.slice(2)))b.click();
}

async function refreshDiscoveredBuildings(revision,signal){
 if(!revision||!catalog||saving||document.querySelector('dialog[open]'))return;
 const editingControl=document.activeElement?.matches('input,select,textarea'),supplyControl=editingControl&&document.activeElement?.closest('.supply-chain'),changed=String(catalog.discoveryRevision||'0')!==String(revision);
 if(editingControl&&!supplyControl||!changed&&!window.colonyDiscoveryRenderPending)return;
 if(changed){const [nextCatalog,nextColonies]=await Promise.all([api('catalog',undefined,{signal}),api('colonies',undefined,{signal})]);catalog=nextCatalog;colonies=reconcileColonyAssignments(catalog,nextColonies)}
 if(supplyControl){window.colonyDiscoveryRenderPending=true;window.colonySupplyChainRefresh?.(window.lastColonyLive);return}
 window.colonyDiscoveryRenderPending=false;const scrollY=window.scrollY;render();renderDetail();window.scrollTo(0,scrollY);
}

let liveLastKey='',liveBusy=false,liveTimer,liveReceivedAt=0;
function liveMessage(text){const n=document.querySelector('.live-feed-status');if(n&&n.textContent!==text)n.textContent=text}
async function pollColonyLive(){
 window.colonyDeliveryClockRefresh?.(window.lastColonyLive);window.colonyCrewRefresh?.();window.colonySupplyChainRefresh?.(window.lastColonyLive);if(window.lastColonyLive&&!liveResponseFresh(window.lastColonyLive))liveMessage('Live feed is not current · showing last recorded readings');
 if(liveBusy||document.hidden||!location.pathname.startsWith('/colonies/')&&location.pathname!=='/kerbin')return;
 liveBusy=true;const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),8000);try{
 const requestStarted={mono:liveMonotonicNow(),wall:Date.now()},result=markLiveResponse(await api('live',undefined,{signal:controller.signal}),requestStarted);window.lastColonyLive=result;liveReceivedAt=result.receivedAt;window.colonyDeliveryClockRefresh?.(result);
 const f=result.frame;
 await refreshDiscoveredBuildings(f?.buildingDiscovery?.revision,controller.signal);
 if(catalog&&f?.crewEpoch)catalog.crewEpoch=f.crewEpoch;
 if(catalog&&f?.vesselLifecycle){catalog.lifecycleStatus=f.vesselLifecycle;window.colonyLifecycleRefresh?.();}
 if(catalog&&f?.sample?.activeWorld&&['live','paused'].includes(f.status)&&f.colony?.status==='observed'&&liveResponseFresh(result)){for(const v of f.colony.vessels||[]){const b=catalog.buildings.find(x=>x.id===v.vesselId);if(b&&b.body===v.body&&b.biome===v.biome)Object.assign(b,applyCrewObservation(b,v,f,result.receivedAt))}}
 if(catalog&&typeof applyProductionObservation==='function'){for(const b of catalog.buildings){const v=f?.colony?.vessels?.find(v=>v.vesselId===b.id&&v.body===b.body&&v.biome===b.biome);Object.assign(b,applyProductionObservation(b,liveResponseFresh(result)?v||{}:{},f||{},result.receivedAt))}}
 window.colonyCrewRefresh?.();window.colonySupplyChainRefresh?.(result);
 if(!f||!liveResponseFresh(result)||!['live','paused'].includes(f.status)){liveMessage('Live feed disconnected · showing last recorded readings');return}
 const observation=f.colony;
 if(!observation||observation.status!=='observed'){liveMessage('Colony readings unavailable · showing last recorded readings');return}
 const key=JSON.stringify([f.sample?.sessionId,f.sample?.loadEpoch,observation.observedUt]);
 const match=colonies.find(c=>location.pathname.includes(c.id));
 const ids=new Set(match?.buildings.map(b=>b.id)||[]),vessels=observation.vessels||[];
 const covered=vessels.filter(v=>ids.has(v.vesselId));
 const summary=covered.length+' / '+ids.size+' buildings observed';
 liveMessage((f.status==='paused'?'Paused':'Live')+' · '+(f.sample?.formattedDate||'')+' · '+summary+' · unloaded buildings use snapshots');
 if(key===liveLastKey)return;liveLastKey=key;
 for(const v of covered){const b=catalog.buildings.find(b=>b.id===v.vesselId);if(!b||b.body!==v.body||b.biome!==v.biome)continue;
  // Preserve saved resources omitted from bounded observations. Replace only
  // explicitly observed totals, rather than treating absent readings as zero.
  const tanks=Array.isArray(v.tanks)?v.tanks:[];const seen=new Set(tanks.map(t=>t.resource));b.tanks=[...(b.tanks||[]).filter(t=>!seen.has(t.resource)),...tanks];
  const crew=v.crew??v.crewCount;if(Number.isInteger(crew)&&crew>=0)b.crew=crew;if(Number.isFinite(v.latitude))b.latitude=v.latitude;if(Number.isFinite(v.longitude))b.longitude=v.longitude;b.observationBasis=v.observationBasis;
 }
 if(observation.wolf?.status==='observed')catalog.wolf=observation.wolf;
 window.colonyLiveRefresh?.();
 }catch{if(window.lastColonyLive)window.lastColonyLive={...window.lastColonyLive,_liveError:true};window.colonyDeliveryClockRefresh?.(window.lastColonyLive);window.colonyCrewRefresh?.();window.colonySupplyChainRefresh?.(window.lastColonyLive);liveMessage('Live feed disconnected · showing last recorded readings')}
 finally{clearTimeout(timeout);liveBusy=false}
}
liveTimer=setInterval(pollColonyLive,500);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)pollColonyLive()});
window.addEventListener('pagehide',()=>clearInterval(liveTimer));
