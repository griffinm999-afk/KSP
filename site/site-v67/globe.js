function mountGlobe(panel,c,buildings){
 const points=buildings.filter(b=>Number.isFinite(b.latitude)&&Number.isFinite(b.longitude));
 const homeLat=points.length?points.reduce((s,b)=>s+b.latitude,0)/points.length:0;
 const homeLon=points.length?Math.atan2(points.reduce((s,b)=>s+Math.sin(b.longitude*Math.PI/180),0),points.reduce((s,b)=>s+Math.cos(b.longitude*Math.PI/180),0))*180/Math.PI:0;
 let lat=homeLat,lon=homeLon,zoom=1,pixels=null,tw=0,th=0,groups=[],frame=0,selected=null;
 const controls=el('div','');controls.className='globe-controls';
 const stage=el('div','');stage.className='globe-stage';const canvas=el('canvas','');canvas.setAttribute('role','img');canvas.setAttribute('aria-label',c.body+' surface and facility locations');canvas.tabIndex=0;
 const overlay=el('div','');overlay.className='globe-markers';stage.append(canvas,overlay);
 const status=el('p','Loading '+c.body+' surface…');status.className='globe-status';
 const selection=el('div','');selection.className='globe-selection';
 const select=el('select','');select.setAttribute('aria-label','Locate building');select.append(new Option('Locate a building…',''));points.forEach(b=>select.append(new Option(b.name,b.id)));
 select.onchange=()=>{const b=points.find(b=>b.id===select.value);if(b){lat=b.latitude;lon=b.longitude;selected=b.id;zoom=Math.max(zoom,2.4);show([b]);schedule()}};
 const button=(name,fn)=>{const b=el('button',name);b.className='btn';b.onclick=fn;controls.append(b)};
 button('−',()=>{zoom=Math.max(.8,zoom/1.6);schedule()});controls.lastChild.setAttribute('aria-label','Zoom out');button('+',()=>{zoom=Math.min(20,zoom*1.6);schedule()});controls.lastChild.setAttribute('aria-label','Zoom in');
 button('Colony',()=>{lat=homeLat;lon=homeLon;zoom=1;selected=null;select.value='';selection.replaceChildren();schedule()});controls.append(select);
 panel.append(controls,stage,status,selection);
 function show(list){selection.replaceChildren();selection.append(el('strong',list.length===1?list[0].name:list.length+' facilities at this location'));for(const b of list){const row=el('button',b.name+' · '+coord(b));row.className='facility-location';row.onclick=()=>{selected=b.id;select.value=b.id;lat=b.latitude;lon=b.longitude;zoom=Math.max(zoom,2.4);show([b]);schedule()};selection.append(row)}}
 function schedule(){if(!frame)frame=requestAnimationFrame(()=>{frame=0;draw()})}
 function draw(){
  if(!pixels||!canvas.isConnected)return;
  if(!stage.clientWidth)return;
  const displayWidth=stage.clientWidth,displayHeight=Math.max(180,Math.min(640,window.innerHeight-stage.getBoundingClientRect().top-78));
  stage.style.height=displayHeight+'px';
  const scale=Math.min(1,1100/displayWidth),W=Math.round(displayWidth*scale),H=Math.round(displayHeight*scale);canvas.width=W;canvas.height=H;
  const ctx=canvas.getContext('2d'),img=ctx.createImageData(W,H),dest=img.data,R=Math.min(W,H)*.44*zoom,cx=W/2,cy=H/2;
  const a=lat*Math.PI/180,o=lon*Math.PI/180,sa=Math.sin(a),ca=Math.cos(a);
  for(let y=0;y<H;y++){const ny=(cy-y)/R;for(let x=0;x<W;x++){const nx=(x-cx)/R,rr=nx*nx+ny*ny,i=(y*W+x)*4;if(rr>1){dest[i]=5;dest[i+1]=12;dest[i+2]=23;dest[i+3]=255;continue}
   const z=Math.sqrt(1-rr),phi=Math.asin(Math.max(-1,Math.min(1,ny*ca+z*sa))),lambda=o+Math.atan2(nx,z*ca-ny*sa);
   const u=((lambda/(2*Math.PI)+.5)%1+1)%1,v=.5-phi/Math.PI;
   const ti=(Math.min(th-1,Math.floor(v*th))*tw+Math.floor(u*tw))*4;
   const light=.3+.76*Math.max(0,-.34*nx+.38*ny+.86*z);
   dest[i]=Math.min(255,pixels[ti]*light);dest[i+1]=Math.min(255,pixels[ti+1]*light);dest[i+2]=Math.min(255,pixels[ti+2]*light);dest[i+3]=255;
  }}ctx.putImageData(img,0,0);
  groups=[];for(const b of points){const p=b.latitude*Math.PI/180,d=b.longitude*Math.PI/180-o;const z=sa*Math.sin(p)+ca*Math.cos(p)*Math.cos(d);if(z<=0)continue;const x=cx+R*Math.cos(p)*Math.sin(d),y=cy-R*(ca*Math.sin(p)-sa*Math.cos(p)*Math.cos(d));if(x<0||y<0||x>W||y>H)continue;let g=groups.find(g=>Math.hypot(g.x-x,g.y-y)<28);if(g)g.items.push(b);else groups.push({x,y,items:[b]})}
  overlay.replaceChildren();groups.forEach(g=>{const b=el('button',g.items.length>1?String(g.items.length):'');b.className='globe-pin'+(g.items.some(b=>b.id===selected)?' selected':'');b.style.left=(g.x/W*100)+'%';b.style.top=(g.y/H*100)+'%';b.title=g.items.map(b=>b.name).join('\n');b.setAttribute('aria-label',g.items.length>1?'Show '+g.items.length+' nearby facilities':g.items[0].name+' location');b.onclick=()=>show(g.items);overlay.append(b)});
  status.textContent=c.body+' · '+c.location+' · Drag to rotate. Use + / − to zoom.';canvas.dataset.ready='true';
 }
 let drag=null;canvas.onpointerdown=e=>{drag={x:e.clientX,y:e.clientY,lat,lon};canvas.setPointerCapture(e.pointerId)};
 canvas.onpointermove=e=>{if(!drag)return;lon=drag.lon-(e.clientX-drag.x)*.18/zoom;lat=Math.max(-89.5,Math.min(89.5,drag.lat+(e.clientY-drag.y)*.18/zoom));schedule()};canvas.onpointerup=canvas.onpointercancel=()=>drag=null;
 canvas.onkeydown=e=>{const step=8/zoom;if(e.key==='ArrowLeft')lon-=step;else if(e.key==='ArrowRight')lon+=step;else if(e.key==='ArrowUp')lat=Math.min(89.5,lat+step);else if(e.key==='ArrowDown')lat=Math.max(-89.5,lat-step);else return;e.preventDefault();schedule()};
 const observer=new ResizeObserver(schedule);observer.observe(stage);window.addEventListener('resize',schedule);window.colonyGlobeCleanup=()=>{observer.disconnect();window.removeEventListener('resize',schedule);cancelAnimationFrame(frame)};
 const texture=new Image();texture.onload=()=>{tw=texture.width;th=texture.height;const off=document.createElement('canvas');off.width=tw;off.height=th;const ctx=off.getContext('2d');ctx.drawImage(texture,0,0);pixels=ctx.getImageData(0,0,tw,th).data;schedule()};texture.onerror=()=>{status.textContent='No installed surface map is available for '+c.body+'.';stage.hidden=true};texture.src='/assets/bodies/'+encodeURIComponent(c.body)+'.jpg';
}
