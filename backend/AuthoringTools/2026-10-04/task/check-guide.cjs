const {chromium}=require('C:/Users/griff/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const path=require('node:path');
const fs=require('node:fs');
(async()=>{
const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
const page=await browser.newPage();
const errors=[];page.on('pageerror',e=>errors.push(e.message));
const base=path.join(__dirname,'site-existing-base-open','dist');
for(const width of [390,1440]){
await page.setViewportSize({width,height:1100});
await page.goto('file:///'+path.join(base,'use-existing-base.html').replaceAll('\\','/'));
await page.screenshot({path:path.join(__dirname,`existing-base-${width}.png`),fullPage:true});
const result=await page.evaluate(()=>({title:document.title,steps:document.querySelectorAll('.qs-step').length,details:document.querySelectorAll('details').length,mainOverflow:document.querySelector('main').scrollWidth>document.querySelector('main').clientWidth}));
if(result.mainOverflow||result.steps!==4||result.details!==4)throw Error(JSON.stringify(result));
console.log(JSON.stringify({width,...result}));
}
for(const name of ['use-existing-base.html','colony-build.html','colony-mvp-quick-start.html']){
const html=fs.readFileSync(path.join(base,name),'utf8');
for(const match of html.matchAll(/href="([^"#]+)"/g)){
const href=match[1].split('#')[0];
if(!href.startsWith('/')&&!href.includes(':')&&!fs.existsSync(path.join(base,href)))throw Error(`Missing ${name}: ${href}`);
}
}
if(errors.length)throw Error(errors.join('\n'));
await browser.close();console.log('Guide links and scripts checked.');
})().catch(e=>{console.error(e.message);process.exit(1)});
