// verify-v011.mjs — v0.11 三件事的浏览器侧验收
//
//   1. transform.progress：PSR 能"从原位演到终态"（旧行为是直接卡在终态）
//   2. Timeline 的 Field 写 "xform" 就能在浏览器里驱动变换进度（不发包）
//   3. Oscillator 每帧摆动；Event Trigger 命中后执行动作；clear-region 真的擦掉像素
//
// 用法（需要运行时页面在线）：
//   终端 1：cd mock && RUNTIME_PORT=5273 PORT=8877 node dev.mjs
//   终端 2：node mock/verify-v011.mjs

import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const PORT = Number(process.env.V011_PORT || 8899);
const CDP_PORT = Number(process.env.CDP_PORT || 9336);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PAGE = `${process.env.RUNTIME || 'http://127.0.0.1:5273/'}?ws=ws://127.0.0.1:${PORT}/ghce`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------- patch 载荷 ---------- */
function b64(...xs) {
  const f = Float32Array.from(xs);
  return Buffer.from(f.buffer).toString('base64');
}

const PATCH = {
  v: '0.1', t: 'patch', seq: 1, rev: 1, snapshot: true,
  ops: [
    { op: 'doc.reset', canvas: { w: 400, h: 300, bg: '#0b0b0f', dpr: 'auto' } },
    { op: 'layer.upsert', value: { id: 'main', name: 'main', order: 0, opacity: 1, blend: 'source-over', visible: true } },
    {
      op: 'geom.upsert', id: 'g_box',
      value: { kind: 'polygon', enc: 'f32', coords: b64(0, 0, 400, 0, 400, 300, 0, 300) },
    },
    {
      // 这一条用来看 PSR：矩阵是"绕原点 90°"，progress 由时间线驱动
      op: 'cmd.upsert',
      value: {
        id: 'c_rot', geom: 'g_box', layer: 'main', order: 0, visible: true, opacity: 1,
        style: { stroke: { color: '#FD7AB4', width: 2, alpha: 1 }, fill: { color: '#E8E6DF', alpha: 1 } },
        transform: { matrix: [0, 1, -1, 0, 0, 0], progress: 0, translate: [0, 0], rotate: 0, scale: [1, 1], pivot: [0, 0] },
      },
    },
    {
      // 这一条用来看振荡器：rotate 每帧按 sin 摆 ±20°
      op: 'cmd.upsert',
      value: {
        id: 'c_osc', geom: 'g_box', layer: 'main', order: 5, visible: true, opacity: 1,
        style: { stroke: { color: '#07A954', width: 1, alpha: 1 }, fill: { color: null } },
        transform: { matrix: null, progress: 1, translate: [0, 0], rotate: 0, scale: [1, 1], pivot: [0, 0] },
        osc: { field: 'rotate', wave: 'sin', freq: 0.5, amp: 20, offset: 0, phase: 0 },
      },
    },
    {
      op: 'anim.upsert', id: 'a_rot',
      value: {
        field: 'xform',
        targets: [{ kind: 'cmd', id: 'c_rot' }],
        keys: [[0, 0], [2, 1]],
        easing: 'linear', loop: false, offset: 0,
      },
    },
    { op: 'event.upsert', id: 'ev_time', value: { event: 'click', action: 'reset-time', detail: '' } },
    { op: 'event.upsert', id: 'ev_region', value: { event: 'click', action: 'clear-region', detail: '0,0,100,100' } },
  ],
};

/* ---------- 桥：连接即发这一帧 ---------- */
const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: '/ghce' });
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ v: '0.1', t: 'welcome', doc: 'v011' }));
  ws.send(JSON.stringify(PATCH));
  ws.on('message', (raw) => {
    const m = JSON.parse(raw.toString());
    if (m.t === 'ping') ws.send(JSON.stringify({ v: '0.1', t: 'pong' }));
  });
});

/* ---------- Chrome ---------- */
const profile = mkdtempSync(join(tmpdir(), 'ghce-v011-'));
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
const consoleEvents = [];
cdpWs.on('message', (raw) => {
  const m = JSON.parse(raw.toString());
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  else if (m.method) consoleEvents.push(m);
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdpWs.send(JSON.stringify({ id: i, method, params }));
});
const evaluate = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
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

const s1 = await evaluate(`(() => {
  const G = window.GHCE;
  return {
    conn: G.client.state,
    progress: G.doc.cmds.get('c_rot')?.transform.progress,
    osc: G.doc.cmds.get('c_osc')?.osc || null,
    rotate: G.doc.cmds.get('c_osc')?.transform.rotate,
    trackFields: [...G.timeline.tracks.values()].map((t) => t.field),
    triggers: G.events.stats.triggers,
    time: G.clock.time,
  };
})()`);

check('运行时连上桥', s1.conn === 'open', s1.conn);
check('transform.progress 被规范化（缺省 patch 里写 0）',
  typeof s1.progress === 'number' && s1.progress >= 0 && s1.progress <= 1, s1.progress);
check('Timeline 的 Field = "xform" 被接受（文本字段名，不是序号）',
  s1.trackFields.includes('xform'), s1.trackFields);
check('振荡器被归一化到指令上', s1.osc && s1.osc.field === 'rotate' && s1.osc.wave === 'sin', s1.osc);
check('事件触发器入库两条', s1.triggers === 2, s1.triggers);

// 等时钟推进，看 progress 是否真的在动（旧版本这里会一直是 0 或直接卡在终态）
// 注意：轨道只有 2 秒，页面已跑过 1.2 秒，直接睡 1 秒会自然跑满到 1 —— 所以先把时钟
// 归零再采样，测的是"同一段轨道内 progress 跟着时间走"，而不是"页面开了多久"。
await evaluate(`(() => { const G = window.GHCE; G.clock.time = 0; })()`);
await sleep(150); // 留两帧让字段重新落到指令上
const p0 = await evaluate(`window.GHCE.doc.cmds.get('c_rot').transform.progress`);
await sleep(500);
const s2 = await evaluate(`(() => {
  const G = window.GHCE;
  const c = G.doc.cmds.get('c_rot');
  const svg = G.toSVG(G.doc).svg;
  const i = svg.indexOf('matrix(');
  return {
    progress: c.transform.progress,
    rotate: G.doc.cmds.get('c_osc')?.transform.rotate,
    time: G.clock.time,
    svgMatrix: i < 0 ? null : svg.slice(i, svg.indexOf(')', i) + 1),
  };
})()`);

check('progress 随时间推进（时间线驱动变换，不是 GH 每帧发包）',
  s2.progress > p0 + 0.05 && s2.progress > 0.1 && s2.progress < 0.95, { from: p0, to: s2.progress, t: s2.time });
check('振荡器让 rotate 在 ±20° 内摆动',
  typeof s2.rotate === 'number' && Math.abs(s2.rotate) <= 20.01 && Math.abs(s2.rotate) > 0.5, s2.rotate);
check('SVG 导出用的是同一个插值矩阵（屏幕与矢量一致）',
  typeof s2.svgMatrix === 'string' && s2.svgMatrix.startsWith('matrix(')
  && Math.abs(parseFloat(s2.svgMatrix.slice(7).split(',')[0]) - Math.cos((s2.progress * Math.PI) / 2)) < 0.02,
  s2.svgMatrix);

// 事件：点击 → 时间归零 + 擦掉左上角
const s3 = await evaluate(`(() => {
  const G = window.GHCE;
  G.clock.time = 5;
  return { before: G.clock.time };
})()`);

const s4 = await evaluate(`(() => {
  const G = window.GHCE;
  const fired = G.events.fire('click', {});
  const g = G.renderer.ctx, dpr = G.renderer._dpr || 1;
  const px = (x, y) => g.getImageData(Math.round(x * dpr), Math.round(y * dpr), 1, 1).data[3];
  const cleared = px(50, 50);
  const kept = px(300, 250);
  return { fired, time: G.clock.time, cleared, kept, errs: document.getElementById('errs').textContent };
})()`);

check('点击命中两条规则', s4.fired === 2, { fired: s4.fired, before: s3.before });
check('restart-time 真的把时间归零', s4.time < 0.2, s4.time);
check('clear-region 只擦掉指定区域（左上透明、右下保留）',
  s4.cleared === 0 && s4.kept > 0, { cleared: s4.cleared, kept: s4.kept });
check('面板无错误', !s4.errs || s4.errs.trim() === '' || s4.errs.trim() === '无错误', s4.errs);

const errs = consoleEvents.filter((e) =>
  (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error') ||
  e.method === 'Runtime.exceptionThrown' ||
  (e.method === 'Log.entryAdded' && e.params.entry.level === 'error'));
check('无控制台错误', errs.length === 0, errs.slice(0, 2).map((e) => e.params.entry?.text || e.params.exceptionDetails?.text));

cdpWs.close();
chrome.kill();
wss.close();
const pass = results.filter(Boolean).length;
console.log(`\n${pass}/${results.length} passed`);
process.exit(pass === results.length ? 0 : 1);
