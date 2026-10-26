// verify-v012.mjs — v0.12 两件事的浏览器侧验收
//
//   1. 每个 cmds 配自己的 Xform：25 个方块里改 5 个，只有那 5 个"演"过去，其余不动
//      （GH 只发终态矩阵，中间帧由 tween.js 补 —— 不是 GH 每帧发包）
//   2. 事件绑定在指令上：点击只影响喂进 Event Trigger 的那几条
//
// 用法：
//   终端 1：cd mock && npm run dev
//   终端 2：node mock/verify-v012.mjs

import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const PORT = Number(process.env.V012_PORT || 8901);
const CDP_PORT = Number(process.env.CDP_PORT || 9337);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const RUNTIME = process.env.RUNTIME || 'http://127.0.0.1:5173/';
const PAGE = `${RUNTIME}?ws=ws://127.0.0.1:${PORT}/colugo`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const N = 25;
const b64 = (...xs) => Buffer.from(Float32Array.from(xs).buffer).toString('base64');

/* ---------- 25 个方块：格子里排 5×5 ---------- */
const squares = [];
for (let i = 0; i < N; i++) {
  const x = 40 + (i % 5) * 70;
  const y = 40 + Math.floor(i / 5) * 70;
  squares.push({
    op: 'geom.upsert', id: `g_${i}`,
    value: { kind: 'polygon', enc: 'f32', coords: b64(x, y, x + 50, y, x + 50, y + 50, x, y + 50) },
  });
}

const cmdOps = (matrices) => squares.map((_, i) => ({
  op: 'cmd.upsert',
  value: {
    id: `c_${i}`, geom: `g_${i}`, layer: 'main', order: i, visible: true, opacity: 1,
    style: { stroke: { color: '#E8E6DF', width: 1.5, alpha: 1 }, fill: { color: null } },
    transform: { matrix: matrices ? matrices[i] : [1, 0, 0, 1, 0, 0], progress: 1, translate: [0, 0], rotate: 0, scale: [1, 1], pivot: [0, 0] },
  },
}));

const PATCH1 = {
  v: '0.1', t: 'patch', seq: 1, rev: 1, snapshot: true,
  ops: [
    { op: 'doc.reset', canvas: { w: 480, h: 480, bg: '#0b0b0f', dpr: 'auto' } },
    { op: 'layer.upsert', value: { id: 'main', name: 'main', order: 0, opacity: 1, blend: 'source-over', visible: true } },
    ...squares,
    ...cmdOps(null),
  ],
};

// 第二帧：只有 0..4 这 5 个转了 45°（绕自己中心，页面坐标里直接给矩阵）
const TARGETS = [0, 1, 2, 3, 4];
const rot45 = (() => {
  const c = Math.SQRT1_2, s = Math.SQRT1_2;
  return [c, s, -s, c, 0, 0];
})();
const matrices2 = Array.from({ length: N }, (_, i) => (TARGETS.includes(i) ? rot45 : [1, 0, 0, 1, 0, 0]));

const PATCH2 = {
  v: '0.1', t: 'patch', seq: 2, rev: 2,
  ops: cmdOps(matrices2),
};

// 第三帧：事件规则绑在 c_0/c_1/c_2 上；另一条绑在 c_10/c_11 上做 restart
const PATCH3 = {
  v: '0.1', t: 'patch', seq: 3, rev: 3,
  ops: [
    { op: 'event.upsert', id: 'ev_hide', value: { event: 'click', action: 'toggle', detail: '', targets: ['c_0', 'c_1', 'c_2'] } },
    { op: 'event.upsert', id: 'ev_again', value: { event: 'dblclick', action: 'restart', detail: '', targets: ['c_10', 'c_11'] } },
  ],
};

/* ---------- 桥 ---------- */
const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: '/colugo' });
let peer = null;
wss.on('connection', (ws) => {
  peer = ws;
  ws.send(JSON.stringify({ v: '0.1', t: 'welcome', doc: 'v012' }));
  ws.send(JSON.stringify(PATCH1));
  ws.on('message', (raw) => {
    const m = JSON.parse(raw.toString());
    if (m.t === 'ping') ws.send(JSON.stringify({ v: '0.1', t: 'pong' }));
  });
});

/* ---------- Chrome ---------- */
const profile = mkdtempSync(join(tmpdir(), 'colugo-v012-'));
const chrome = spawn(CHROME, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--window-size=1280,800', `--user-data-dir=${profile}`,
  `--remote-debugging-port=${CDP_PORT}`, 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 60 && !wsUrl; i++) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`)).json();
    wsUrl = list.find((t) => t.type === 'page')?.webSocketDebuggerUrl;
  } catch { /* not up */ }
  if (!wsUrl) await sleep(200);
}

const cdpWs = new WebSocket(wsUrl);
await new Promise((r) => cdpWs.on('open', r));
let id = 0;
const pending = new Map();
const events = [];
cdpWs.on('message', (raw) => {
  const m = JSON.parse(raw.toString());
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  else if (m.method) events.push(m);
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdpWs.send(JSON.stringify({ id: i, method, params }));
});
const evaluate = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ' ' + (r.exceptionDetails.exception?.description || ''));
  return r.result.value;
};

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(1200);

const results = [];
const check = (name, pass, detail) => {
  results.push(pass);
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}  → ${JSON.stringify(detail)}`);
};

/* ---------- 1. 首帧：25 个都停在终态 ---------- */
const s1 = await evaluate(`(() => {
  const G = window.Colugo;
  const ps = [...G.doc.cmds.values()].map((c) => c.transform.progress);
  return {
    cmds: G.doc.stats().cmds,
    min: Math.min(...ps), max: Math.max(...ps),
    active: G.tweens.stats.active,
    conn: G.client.state,
  };
})()`);
check('运行时连上桥', s1.conn === 'open', s1.conn);
check('25 条指令入库', s1.cmds === N, s1.cmds);
check('首帧入场演完后 25 条都落在终态', s1.min === 1 && s1.max === 1 && s1.active === 0, s1);

/* ---------- 2. 改 5 个：只有那 5 个在演 ---------- */
peer.send(JSON.stringify(PATCH2));
await sleep(180);
const s2 = await evaluate(`(() => {
  const G = window.Colugo;
  const t = ${JSON.stringify(TARGETS)};
  const moving = [], still = [];
  for (const c of G.doc.cmds.values()) {
    const i = Number(c.id.slice(2));
    (t.includes(i) ? moving : still).push(+c.transform.progress.toFixed(3));
  }
  const c0 = G.doc.cmds.get('c_0');
  const svg = G.toSVG(G.doc).svg;
  const i = svg.indexOf('matrix(');
  return {
    moving, still: still.slice(0, 3),
    active: G.tweens.stats.active,
    matrix: c0.transform.matrix.slice(0, 2).map((v) => +v.toFixed(3)),
    svgMatrix: i < 0 ? null : svg.slice(i, svg.indexOf(')', i) + 1),
  };
})()`);
check('只有被改的 5 个在演（0 < p < 1）',
  s2.moving.length === 5 && s2.moving.every((p) => p > 0 && p < 1), s2.moving);
check('其余 20 个完全不动（p 仍是 1）',
  s2.still.length === 3 && s2.still.every((p) => p === 1), s2.still);
check('过渡引擎同时在跑的正好 5 条', s2.active === 5, s2.active);

/* ---------- 3. 演完归位 ---------- */
await sleep(1100);
const s3 = await evaluate(`(() => {
  const G = window.Colugo;
  const t = ${JSON.stringify(TARGETS)};
  const moving = [];
  for (const c of G.doc.cmds.values()) if (t.includes(Number(c.id.slice(2)))) moving.push(c.transform.progress);
  return { moving, active: G.tweens.stats.active };
})()`);
check('演完之后 5 个都回到终态（p = 1，且不再重绘）',
  s3.moving.every((p) => p === 1) && s3.active === 0, s3);

/* ---------- 4. 屏幕与 SVG 用同一个插值 ---------- */
const s4 = await evaluate(`(() => {
  const G = window.Colugo;
  // 手动把 c_0 拨回半程，比较画布与 SVG 用的矩阵
  const c = G.doc.cmds.get('c_0');
  c.transform.from = [1, 0, 0, 1, 0, 0];
  c.transform.progress = 0.5;
  const svg = G.toSVG(G.doc).svg;
  const i = svg.indexOf('matrix(');
  return {
    progress: c.transform.progress,
    svgMatrix: i < 0 ? null : svg.slice(i, svg.indexOf(')', i) + 1),
  };
})()`).catch((e) => ({ err: String(e) }));
check('SVG 里写的是插值后的矩阵（45° 的一半 ≈ 22.5°）', (() => {
  if (!s4 || !s4.svgMatrix) return false;
  const n = s4.svgMatrix.match(/-?\d+(?:\.\d+)?/g).map(Number);
  return Math.abs(n[0] - Math.cos(Math.PI / 8)) < 0.02 && Math.abs(n[1] - Math.sin(Math.PI / 8)) < 0.02;
})(), s4);

/* ---------- 5. 事件绑定在指令上 ---------- */
peer.send(JSON.stringify(PATCH3));
await sleep(200);
const s5 = await evaluate(`(() => {
  const G = window.Colugo;
  const vis = (id) => G.doc.cmds.get(id).visible;
  G.events.fire('click', {});
  const after = { c0: vis('c_0'), c1: vis('c_1'), c2: vis('c_2'), c3: vis('c_3'), c24: vis('c_24') };
  G.events.fire('click', {});
  const back = { c0: vis('c_0'), c3: vis('c_3') };
  return { triggers: G.events.stats.triggers, after, back };
})()`);
check('两条规则入库（都带 targets）', s5.triggers === 2, s5.triggers);
check('点击只影响目标 3 条（c3 / c24 不受影响）',
  s5.after.c0 === false && s5.after.c1 === false && s5.after.c2 === false
  && s5.after.c3 === true && s5.after.c24 === true, s5.after);
check('再点一次恢复显示', s5.back.c0 === true && s5.back.c3 === true, s5.back);

/* ---------- 1b. 新指令第一次出现：从原始状态演到目标（黑线→绿线） ---------- */
peer.send(JSON.stringify({
  v: '0.1', t: 'patch', seq: 4, rev: 4,
  ops: [
    { op: 'geom.upsert', id: 'g_new', value: { kind: 'polygon', enc: 'f32', coords: b64(300, 300, 380, 300, 380, 380, 300, 380) } },
    {
      op: 'cmd.upsert',
      value: {
        id: 'c_new', geom: 'g_new', layer: 'main', order: 99, visible: true, opacity: 1,
        style: { stroke: { color: '#07A954', width: 2, alpha: 1 }, fill: { color: null } },
        transform: { matrix: [0, 1, -1, 0, 0, 0], progress: 1, translate: [0, 0], rotate: 0, scale: [1, 1], pivot: [0, 0] },
      },
    },
  ],
}));
await sleep(250);
const sIntro = await evaluate(`(() => {
  const G = window.Colugo;
  const c = G.doc.cmds.get('c_new');
  return { p: c ? c.transform.progress : null, matrix: c ? c.transform.matrix.slice(0, 2) : null };
})()`);
check('新指令第一次出现时从 0 开始演（不是直接跳到终态）',
  sIntro.p !== null && sIntro.p > 0 && sIntro.p < 0.9, sIntro);

/* ---------- 6. restart：让目标的变换从头再演 ---------- */
// 先把 c_10 摆成"演完了"的状态，再触发 restart；进度是下一帧才写回的，所以要等一帧
await evaluate(`(() => {
  const G = window.Colugo;
  G.doc.cmds.get('c_10').transform.from = [1, 0, 0, 1, 0, 0];
  G.doc.cmds.get('c_10').transform.matrix = [0.707, 0.707, -0.707, 0.707, 0, 0];
  G.doc.cmds.get('c_10').transform.progress = 1;
  G.events.fire('dblclick', {});
})()`);
await sleep(150);
const s6 = await evaluate(`(() => {
  const G = window.Colugo;
  return { p0: G.doc.cmds.get('c_10').transform.progress, other: G.doc.cmds.get('c_12').transform.progress };
})()`);
check('restart 让目标指令的变换重演（进度归零，非目标不动）', s6.p0 < 0.4 && s6.other === 1, s6);

/* ---------- 7. 控制台 ---------- */
const errs = events.filter((e) =>
  (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error') ||
  e.method === 'Runtime.exceptionThrown' ||
  (e.method === 'Log.entryAdded' && e.params.entry.level === 'error'));
check('无控制台错误', errs.length === 0, errs.slice(0, 2).map((e) => e.params.entry?.text || e.params.exceptionDetails?.text));

const passed = results.filter(Boolean).length;
console.log(`\n${passed}/${results.length} passed`);
chrome.kill();
process.exit(passed === results.length ? 0 : 1);
