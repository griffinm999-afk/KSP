function renderDetail(){
 window.colonyNavCleanup?.();window.colonyGlobeCleanup?.();window.colonyLiveRefresh=null;liveLastKey='';
 const match=location.pathname.match(/^\/colonies\/([^/]+)/);if(!match)return;
 $('colony-list').hidden=true;$('colony-detail').hidden=false;document.body.classList.add('detail-page');
 const c=colonies.find(c=>c.id===match[1]);const root=$('colony-detail');root.replaceChildren();
 const back=el('a','All colonies');back.href='/';back.className='back-link';root.append(back);
 if(!c){root.append(el('h1','Colony not found'),el('p','This charter may have been deleted.'));return}
 document.title=c.name+' · Expanse Foundations';
 const heading=el('div','');heading.className='heading';const title=el('div','');title.append(el('h1',c.name),el('p',c.body+' · '+c.location));const add=el('button','Add buildings');add.className='btn primary';add.onclick=()=>open(c);heading.append(title,add);root.append(heading);
 const buildings=c.buildings.map(b=>catalog.buildings.find(x=>x.id===b.id)||b);
 const crewKnown=buildings.length>0&&buildings.every(b=>Number.isInteger(b.crew));
 const layout=el('div','');layout.className='colony-layout';const sidebar=el('aside','');sidebar.className='colony-sidebar';sidebar.setAttribute('aria-label','Colony summary');const main=el('div','');main.className='colony-main';layout.append(sidebar,main);root.append(layout);const stats=el('div','');stats.className='detail-stats';
 for(const [label,value,caption] of [['Population',crewKnown?buildings.reduce((sum,b)=>sum+b.crew,0):'—','Kerbals aboard assigned buildings'],['Buildings',buildings.length,'Assigned to this colony'],['Location',c.body,c.location]]){const card=el('section','');card.className='stat';card.append(el('span',label),el('strong',String(value)),el('small',caption));stats.append(card)}sidebar.append(stats);const balances=el('section','');balances.className='balance-panel';balances.append(el('h2','Career balances'));for(const [label,key] of [['Funds','funds'],['Science','science']]){const card=el('div','');card.className='balance-value';card.append(el('span',label),el('strong',Number.isFinite(catalog.balances?.[key])?catalog.balances[key].toLocaleString(undefined,{maximumFractionDigits:key==='funds'?0:1}):'—'));balances.append(card)}balances.append(el('small',catalog.balances?'Last saved '+new Date(catalog.balances.observedAt).toLocaleString():'Not connected'));sidebar.append(balances);
 const liveStatus=el('p','Connecting live feed…');liveStatus.className='live-feed-status resource-note';main.append(liveStatus);
 const navigation=el('nav','');navigation.className='colony-nav';navigation.setAttribute('aria-label','Colony sections');main.append(navigation);
 const overview=el('div',''),buildingView=el('div',''),resourceView=el('div',''),accountingView=el('div',''),mapView=el('div',''),charterView=el('div',''),productionView=el('div',''),settingsView=el('div','');main.append(overview,buildingView,resourceView,accountingView,mapView,charterView,productionView,settingsView);
 const views={Overview:overview,Buildings:buildingView,Resources:resourceView,Production:productionView,Accounting:accountingView,Map:mapView,Charter:charterView,Settings:settingsView};
 const navButtons=new Map(),navLinks=new Map(),menus=[];
 const closeMenus=()=>{for(const {button,menu} of menus){menu.hidden=true;button.setAttribute('aria-expanded','false')}};
 const go=label=>{if(!views[label])label='Overview';for(const [name,v] of Object.entries(views))v.hidden=name!==label;for(const [name,button] of navButtons)button.setAttribute('aria-pressed',String(name===label||(name==='Operations'&&['Production'].includes(label))||(name==='Colony'&&['Buildings','Resources','Map','Charter','Settings'].includes(label))));for(const [name,link] of navLinks){if(name===label)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current')}closeMenus();history.replaceState(null,'','#'+label.toLowerCase())};
 for(const [name,items] of [['Overview',null],['Operations',['Production']],['Accounting',null],['Colony',['Buildings','Resources','Map','Charter','Settings']]]){const button=el('button',name);button.className='section-button';navButtons.set(name,button);if(!items){button.onclick=()=>go(name);navigation.append(button);continue}
 const group=el('div','');group.className='nav-group';const menu=el('div','');menu.className='nav-dropdown';menu.hidden=true;menu.id='colony-menu-'+name.toLowerCase();button.setAttribute('aria-expanded','false');button.setAttribute('aria-controls',menu.id);const chevron=el('span',' ▾');chevron.setAttribute('aria-hidden','true');button.append(chevron);button.onclick=()=>{const opening=menu.hidden;closeMenus();menu.hidden=!opening;button.setAttribute('aria-expanded',String(opening))};
 for(const label of items){const link=el('a',label);link.href='#'+label.toLowerCase();link.onclick=e=>{e.preventDefault();go(label);button.focus()};navLinks.set(label,link);menu.append(link)}group.append(button,menu);navigation.append(group);menus.push({button,menu});group.onkeydown=e=>{if(e.key==='Escape'){closeMenus();button.focus();e.preventDefault()}else if(e.target===button&&e.key==='ArrowDown'){closeMenus();menu.hidden=false;button.setAttribute('aria-expanded','true');menu.firstElementChild.focus();e.preventDefault()}};
 }
 const outside=e=>{if(!navigation.contains(e.target))closeMenus()};document.addEventListener('click',outside);window.colonyNavCleanup=()=>document.removeEventListener('click',outside);
 const body=catalog.systems.find(x=>x.name===c.body),parent=catalog.systems.find(x=>x.name===body?.parent);
 const primary=parent&&parent.parent?parent:body;
 const moons=catalog.systems.filter(x=>x.parent===primary?.name);
 const ancestors=[];let ancestor=catalog.systems.find(x=>x.name===primary?.parent);const visited=new Set();while(ancestor&&!visited.has(ancestor.name)){visited.add(ancestor.name);ancestors.unshift(ancestor);ancestor=catalog.systems.find(x=>x.name===ancestor.parent)}
 const members=primary?[...ancestors,primary,...moons]:[{name:c.body}];
 const diagram=el('section','');diagram.className='colony-system';diagram.setAttribute('aria-label','Planetary system');
 const diagramWidth=Math.max(420,members.length*110);const systemCanvas=svg('svg',{viewBox:'0 0 '+diagramWidth+' 70',role:'img','aria-label':c.body+' orbital hierarchy'});
 members.forEach((member,i)=>{const x=55+i*(diagramWidth-110)/Math.max(1,members.length-1),selected=member.name===c.body;
 if(i)systemCanvas.append(svg('line',{x1:72,y1:25,x2:x-20,y2:25,stroke:'#466379','stroke-dasharray':'4 6'}));
 systemCanvas.append(svg('circle',{cx:x,cy:25,r:i===0?17:12,fill:member.name==='Sun'?'#eda75e':selected?'#729f9f':'#477a9f',stroke:selected?'#ff9552':'#82a9c3','stroke-width':selected?3:1}));
 const label=svg('text',{x,y:59,'text-anchor':'middle',fill:selected?'#ffb47f':'#e5eff6','font-size':14,'font-weight':selected?600:400});label.textContent=member.name;systemCanvas.append(label)});
 diagram.append(systemCanvas);main.insertBefore(diagram,navigation);

 const site=detailPanel(c.body+' · Colony location');site.classList.add('surface-panel');mapView.append(site);mountGlobe(site,c,buildings);

 const panel=detailPanel('Buildings');const wrap=el('div','');wrap.className='scroll';const table=el('table','');const head=el('thead',''),hr=el('tr','');for(const label of ['Building','Kerbals','Coordinates'])hr.append(el('th',label));head.append(hr);table.append(head);const rows=el('tbody','');for(const b of buildings){const row=el('tr','');row.dataset.vessel=b.id;row.append(el('td',b.name),el('td',Number.isInteger(b.crew)?String(b.crew):'—'),el('td',Number.isFinite(b.latitude)&&Number.isFinite(b.longitude)?coord(b):'Unavailable'));rows.append(row)}table.append(rows);wrap.append(table);panel.append(buildings.length?wrap:el('p','No buildings assigned yet.'));buildingView.append(panel);
 renderResources(resourceView,c,buildings);
 renderAccounting(accountingView);
 renderProduction(productionView,c,buildings);
 renderCharter(charterView,c);
 renderColonySettings(settingsView,c);
 renderColonyOverview(overview,c,buildings,go);
 const viewCharter=el('button','View charter');viewCharter.className='btn';viewCharter.onclick=()=>go('Charter');const actions=el('div','');actions.className='heading-actions';actions.append(viewCharter,add);heading.append(actions);
 const active=location.hash.slice(1);go(Object.keys(views).find(name=>name.toLowerCase()===active)||'Overview');
 window.colonyLiveRefresh=()=>{resourceView.liveRefresh?.();productionView.liveRefresh?.();const population=stats.querySelector('.stat strong');if(population)population.textContent=buildings.every(b=>Number.isInteger(b.crew))?String(buildings.reduce((n,b)=>n+b.crew,0)):'—';for(const row of rows.children){const b=buildings.find(b=>b.id===row.dataset.vessel);if(b){const values=[b.name,String(b.crew),coord(b)];values.forEach((v,i)=>{if(row.children[i].textContent!==v)row.children[i].textContent=v})}}};
 const observation=el('p','Crew and positions: last recorded '+new Date(catalog.observedAt).toLocaleString()+'.');observation.className='observation';root.append(observation);
}
function detailPanel(title){const panel=el('section','');panel.className='panel detail-panel';const heading=el('h2',title);heading.className='panel-title';panel.append(heading);return panel}
function svg(tag,attributes){const node=document.createElementNS('http://www.w3.org/2000/svg',tag);for(const [key,value] of Object.entries(attributes))node.setAttribute(key,String(value));return node}
function coord(b){return Math.abs(b.latitude).toFixed(3)+'° '+(b.latitude<0?'S':'N')+', '+Math.abs(b.longitude).toFixed(3)+'° '+(b.longitude<0?'W':'E')}
