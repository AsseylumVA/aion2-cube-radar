const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function browser() {
  const nodes = new Map(), calls = [];
  const canvas = new Proxy({}, { get: (_, name) => (...args) => calls.push([name, ...args]),
    set: (_, name, value) => { calls.push(['set:' + name, value]); return true; } });
  const document = {
    documentElement: {}, activeElement: null,
    querySelectorAll: () => [],
    getElementById(id) {
      if (!nodes.has(id)) nodes.set(id, {
        innerHTML: '', textContent: '', hidden: false, value: '', clientWidth: 800, clientHeight: 500,
        classList: { toggle() {} }, listeners: {},
        addEventListener(type, fn) { this.listeners[type] = fn; }, getContext: () => canvas,
      });
      return nodes.get(id);
    },
  };
  const i18n = JSON.parse(fs.readFileSync(path.join(__dirname, '../src/CubeRadar.Cli/wwwroot/i18n.json'), 'utf8'));
  const context = vm.createContext({ document, console, I18N: i18n,
    localStorage: { getItem: () => null, setItem() {}, removeItem() {} },
    window: { addEventListener() {}, devicePixelRatio: 1 }, location: { host: '127.0.0.1' },
    getComputedStyle: () => ({ getPropertyValue: () => '#abcdef' }),
    WebSocket: class {}, setInterval() {}, setTimeout() {},
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../src/CubeRadar.Cli/wwwroot/app.js'), 'utf8'), context);
  const update = (players, ownFaction = 'Elyos') => {
    const state = { radar: { players, ownFaction, factionSetting: ownFaction, nowMs: 10000, selfId: 1,
      cubes: [], warnings: [], trail: [], zone: null, mapId: 1, fixFreshSeconds: 5,
      self: { x: 10000, y: 10000, z: 100, timeMs: 10000, ageMs: 0 }, ring: null,
      orientation: { north: '-y', northX: 0, northY: -1, eastX: 1, eastY: 0 } },
      capture: { mode: 'replay', streams: 1, packets: 10 }, wallMs: 10000 };
    context.input = state;
    vm.runInContext('msg = input; onState();', context);
  };
  return { document, calls, context, update };
}

const contact = (extra = {}) => ({ entityId: 11930, name: 'EnemyB', faction: 'Asmodian', isEnemy: true,
  rankCode: 17, rankName: 'Eighth-Class Soldier', rankVerified: true,
  x: 12000, y: 11000, z: 100, distanceM: 22, compass: 'SO', positionStale: false, positionAgeMs: 100, ...extra });

test('enemy list shows verified rank and excludes allies/unclassified players', () => {
  const b = browser();
  b.update([contact(), contact({ name: 'Ally', isEnemy: false }), contact({ name: 'Unknown', isEnemy: null })]);
  const html = b.document.getElementById('enemy-list').innerHTML;
  assert.match(html, /EnemyB/);
  assert.match(html, /Eighth-Class Soldier/);
  assert.doesNotMatch(html, /Ally|Unknown| \*/);
  assert.equal(b.document.getElementById('enemy-count').textContent, '(1)');
});

test('assumed ranks carry an asterisk and names are HTML-escaped', () => {
  const b = browser();
  b.update([contact({ name: '<script>x</script>', rankCode: 18, rankName: 'Ninth-Class Soldier', rankVerified: false })]);
  const html = b.document.getElementById('enemy-list').innerHTML;
  assert.match(html, /Ninth-Class Soldier \*/);
  assert.match(html, /&lt;script&gt;/);
  assert.doesNotMatch(html, /<script>/);
});

test('unknown own faction requests selection instead of showing alleged enemies', () => {
  const b = browser();
  b.update([contact({ isEnemy: null })], 'Unknown');
  assert.equal(b.document.getElementById('enemy-list').innerHTML, '');
  assert.match(b.document.getElementById('enemy-empty').textContent, /Своя фракция ещё неизвестна/);
});

test('stale contacts remain labelled in the list but get no live enemy map label', () => {
  const b = browser();
  b.update([contact({ positionStale: true, positionAgeMs: 12000 })]);
  assert.match(b.document.getElementById('enemy-list').innerHTML, /устарела/);
  assert.equal(b.calls.filter(c => c[0] === 'fillText' && String(c[1]).includes('EnemyB')).length, 0);
});

test('fresh enemy positions get a labelled map marker', () => {
  const b = browser();
  b.update([contact()]);
  assert.ok(b.calls.some(c => c[0] === 'fillText' && String(c[1]).includes('EnemyB')));
});

test('estimated distances and crowded labels are handled explicitly', () => {
  const b = browser();
  b.update([contact({ distanceEstimated: true }), contact({ name: 'SecondEnemy' })]);
  assert.match(b.document.getElementById('enemy-list').innerHTML, /≈ 22 m/);
  assert.equal(b.calls.filter(c => c[0] === 'fillText' && /EnemyB|SecondEnemy/.test(String(c[1]))).length, 1);
});

test('enemy display can be disabled without disabling cubes', () => {
  const b = browser();
  b.update([contact()]);
  b.document.getElementById('enemies').listeners.click();
  assert.equal(b.document.getElementById('enemy-list').innerHTML, '');
  assert.match(b.document.getElementById('enemy-empty').textContent, /выключен/);
  assert.equal(b.document.getElementById('count').textContent, '');
});

test('all soldier classes and trainee are drawn as centered single-character badges', () => {
  for (const [rankCode, symbol] of [[19, 'T'], [18, '9'], [17, '8'], [16, '7'], [15, '6'],
    [14, '5'], [13, '4'], [12, '3'], [11, '2'], [10, '1'], [null, '?'], [9, '?'], [1, '?'], [20, '?']]) {
    const b = browser();
    b.update([contact({ rankCode, rankName: null })]);
    assert.ok(b.calls.some(c => c[0] === 'fillText' && c[1] === symbol), `code ${rankCode} -> ${symbol}`);
    assert.ok(b.calls.some(c => c[0] === 'set:textAlign' && c[1] === 'center'));
    assert.ok(b.calls.some(c => c[0] === 'set:textBaseline' && c[1] === 'middle'));
    assert.ok(!b.calls.some(c => c[0] === 'fillText' && String(c[1]).includes(`R${rankCode}`)));
  }
});

test('badges stay visible when crowded names are suppressed and are painted over text labels', () => {
  const b = browser();
  b.update([contact(), contact({ name: 'Nearby', rankCode: 19, rankName: 'Trainee', rankSymbol: 'T' })]);
  assert.ok(b.calls.some(c => c[0] === 'fillText' && c[1] === '8'));
  assert.ok(b.calls.some(c => c[0] === 'fillText' && c[1] === 'T'));
  const nameIndex = b.calls.findIndex(c => c[0] === 'fillText' && String(c[1]).includes('EnemyB'));
  const badgeIndex = b.calls.findIndex(c => c[0] === 'fillText' && c[1] === '8');
  assert.ok(badgeIndex > nameIndex);
});

test('Verteron cube 70962,99276 lands on the calibrated map pixel', () => {
  const b = browser();
  const map = { scale: 0.009791754817436571, offsetX: 4035.4689879924563, offsetY: 4153.7253769234385 };
  const [px, py] = b.context.mapPixel(map, 70962, 99276);
  assert.ok(Math.abs(px - 4730.31) < 0.05, px);
  assert.ok(Math.abs(py - 5125.81) < 0.05, py);
});

test('a click on the map shifts that picture point onto the player', () => {
  const b = browser();
  const map = { scale: 0.01, offsetX: 100, offsetY: 200 };
  const player = { x: 50000, y: 60000 };
  const at = { x: 80000, y: 40000 };
  const d = b.context.mapNudgeDelta(map, player, at);
  const shifted = { ...map, offsetX: map.offsetX + d.x, offsetY: map.offsetY + d.y };
  const [px, py] = b.context.mapPixel(shifted, player.x, player.y);
  const [qx, qy] = b.context.mapPixel(map, at.x, at.y);
  assert.ok(Math.abs(px - qx) < 1e-6 && Math.abs(py - qy) < 1e-6);
});

test('a spawn pixel stays on that image point when converted to world centimeters', () => {
  const b = browser();
  const map = { scale: 0.01, offsetX: 100, offsetY: 200 };
  const world = b.context.spawnWorld(map, 4730, 5126);
  const [px, py] = b.context.mapPixel(map, world.x, world.y);
  assert.ok(Math.abs(px - 4730) < 1e-6 && Math.abs(py - 5126) < 1e-6);
});

test('every published respawn is listed, including the collected Verteron cube', () => {
  const data = JSON.parse(fs.readFileSync(path.join(__dirname, '../src/CubeRadar.Cli/wwwroot/maps/spawns.json'), 'utf8'));
  assert.equal(data.zones.verteron.length, 1440);
  assert.equal(data.zones.altgard.length, 1371);
  assert.ok(data.zones.verteron.some(([x, y]) => Math.hypot(x - 70962, y - 99276) < 2));
});

test('dragging the map frees it, and three position packets after a move bring it back', () => {
  const b = browser();
  b.update([]);
  const before = b.context.mapFrame().center.x;
  b.context.panTo({ x: before, y: b.context.mapFrame().center.y }, 80, 0);
  const v = b.context.mapView();
  assert.equal(v.follow, false);
  assert.ok(Math.abs(v.center.x - before) > 1);
  const step = (ms, x) => b.context.notePosition({ x, y: 10000, z: 100, timeMs: ms, ageMs: 0 });
  step(11000, 10000);
  assert.equal(v.moves, 0);
  step(12000, 10300);
  assert.equal(v.moves, 1);
  step(13000, 10600);
  step(14000, 10900);
  assert.ok(v.returning);
  assert.equal(v.returning.from.x, v.center.x);
});

test('a moved position counts even when the packet time does not change', () => {
  const b = browser();
  b.update([]);
  const before = b.context.mapFrame().center.x;
  b.context.panTo({ x: before, y: b.context.mapFrame().center.y }, 80, 0);
  b.context.notePosition({ x: 10300, y: 10000, timeMs: 10000 });
  assert.equal(b.context.mapView().moves, 1);
  b.context.notePosition({ x: 10600, y: 10000, timeMs: 10000 });
  b.context.notePosition({ x: 10900, y: 10000, timeMs: 10000 });
  assert.ok(b.context.mapView().returning);
});

test('wheel zoom stays bounded and does not free a following map', () => {
  const b = browser();
  b.update([]);
  b.context.zoomView(2, 100, 100);
  assert.equal(b.context.mapView().zoom, 2);
  assert.equal(b.context.mapView().follow, true);
  b.context.zoomView(100, 100, 100);
  assert.equal(b.context.mapView().zoom, 8);
});

test('invalid API badge strings fall back to the class mapping', () => {
  const b = browser();
  b.update([contact({ rankSymbol: 'R17' })]);
  assert.ok(b.calls.some(c => c[0] === 'fillText' && c[1] === '8'));
  assert.ok(!b.calls.some(c => c[0] === 'fillText' && c[1] === 'R17'));
});
