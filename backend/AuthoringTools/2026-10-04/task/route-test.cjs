const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const source = fs.readFileSync(process.argv[2], 'utf8');
const start = source.indexOf('function renderDeliveries(root,hubOnly=false){');
const end = source.indexOf('function renderColonySettings(', start);
assert(start >= 0 && end > start);
class Element {
  constructor(tag, text = '') { this.tag = tag; this.textContent = text; this.children = []; this.attrs = {}; this.hidden = false; }
  append(...children) { this.children.push(...children); }
  setAttribute(name, value) { this.attrs[name] = String(value); }
  getAttribute(name) { return this.attrs[name]; }
}
const el = (tag, text) => new Element(tag, text);
const walk = node => [node, ...(node.children || []).flatMap(walk)];
const words = node => walk(node).map(x => x.textContent || '').join(' ');
let data = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const economics = JSON.parse(fs.readFileSync(process.argv[4], 'utf8'));
const context = {el, detailPanel: title => el('section', title), api: async () => data, document: {createTextNode: text => el('#text', text)}, SHIPPING_ECONOMICS:economics, window: {}, console};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);
const root = el('root');
context.renderDeliveries(root);
setImmediate(() => {
  const nodes = walk(root);
  const summaries = nodes.filter(x => x.className === 'current-route-summary');
  const details = nodes.filter(x => x.className === 'current-route-details');
  assert.equal(summaries.length, 3);
  assert.equal(details.length, 3);
  const buttons = nodes.filter(x => x.className === 'route-toggle');
  assert.equal(buttons.length, 3);
  assert.equal(buttons[0].getAttribute('aria-expanded'), 'false');
  assert(details.every(x => x.hidden));
  buttons[0].onclick();
  assert.equal(buttons[0].getAttribute('aria-expanded'), 'true');
  assert.equal(details[0].hidden, false);
  assert.equal(details[1].hidden, true);
  buttons[0].onclick();
  assert.equal(details[0].hidden, true);
  const fuel = details.find(x => words(x).includes('MM Route') === false && words(x).includes('LiquidFuel'));
  assert(fuel && words(fuel).includes('MonoPropellant') && words(fuel).includes('Oxidizer'));
  assert(words(root).includes('Every 6 game hours'));
  assert(words(root).includes('100,000 funds per full load'));
  assert(words(root).includes('2,700,000 funds across 27 saved shipments'));
  assert(words(root).includes('4,060 funds per configured full load'));
  assert(words(root).includes('8,120 funds across 2 saved shipments'));
  assert(words(root).includes('2,000 funds per configured full load'));
  assert(words(root).includes('MonoPropellant 3 funds/unit'));
  assert(words(root).includes('LiquidFuel 2 funds/unit'));
  assert(words(root).includes('Oxidizer 0.4 funds/unit'));
  assert(words(root).includes('No recovery receipt recorded in this snapshot'));
  assert.equal(nodes.filter(x => x.tag === 'tr' && x.children.length === 7).length, 30);
  const clocks = nodes.filter(x => x.className === 'route-clock');
  assert(clocks.length >= 6);
  assert(clocks.every(x => x.textContent.includes('countdown unavailable')));
  const feed = (ut, status = 'live', receivedAt = Date.now()) => ({receivedAt,frame:{status,sample:{activeWorld:true,utSeconds:ut}}});
  context.window.colonyDeliveryClockRefresh(feed(data.observedUt));
  const baseline = clocks.map(x => x.textContent);
  assert(baseline.every(x => x.includes('Year 1, Day') && x.includes(' in ')));
  context.window.colonyDeliveryClockRefresh(feed(data.observedUt, 'paused'));
  assert.deepEqual(clocks.map(x => x.textContent), baseline);
  context.window.colonyDeliveryClockRefresh(feed(data.observedUt + 7200));
  assert.notDeepEqual(clocks.map(x => x.textContent), baseline);
  context.window.colonyDeliveryClockRefresh(feed(data.observedUt - 600));
  assert.notDeepEqual(clocks.map(x => x.textContent), baseline);
  context.window.colonyDeliveryClockRefresh(feed(999999999));
  assert(clocks.every(x => x.textContent.includes('Due as of live game time')));
  context.window.colonyDeliveryClockRefresh(feed(data.observedUt, 'live', Date.now() - 7000));
  assert(clocks.every(x => x.textContent.includes('countdown unavailable')));
  context.window.colonyDeliveryClockRefresh(null);
  assert(clocks.every(x => x.textContent.includes('countdown unavailable')));
  const dated = structuredClone(data);
  dated.shipments[0].dueUt = 426 * 21600;
  dated.shipments = [dated.shipments[0]];
  dated.routes = [dated.routes[0]];
  dated.rules = [];
  data = dated;
  const second = el('root');
  context.renderDeliveries(second);
  setImmediate(() => {
    assert(words(second).includes('Year 2, Day 1 - 0h, 0m, 0s'));
    const unknown = structuredClone(dated);
    unknown.routes[0].destination = 'Fuel Depot';
    unknown.routes[0].cargo = [{resource:'UnknownFuel',amount:1000}];
    unknown.shipments[0].destination = 'Fuel Depot';
    unknown.shipments[0].cargo = [{resource:'UnknownFuel',amount:1000}];
    data = unknown;
    const third = el('root');
    context.renderDeliveries(third);
    setImmediate(() => {
      assert(words(third).includes('No verified route price in this delivery snapshot'));
      assert(!words(third).includes('0 funds per configured full load'));
      console.log('Route DOM, amounts, unknown prices, live clock, pause, warp, rewind, stale feed, and Kerbin calendar checks passed');
    });
  });
});
