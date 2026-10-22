// replay-csharp.mjs — 把 C# PatchBuilder 真实产出的 patch 灌进运行时，验证两端协议一致。
// 用法：
//   1) 跑 gh 探针导出 json：见 gh/probe/（README 里写了命令）
//   2) node replay-csharp.mjs
// 这一步取代不了 GH 里的联调，但它能保证"字段名 / 编码 / 类型"这三类错误不会留到 GH 现场才发现。

import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

// 缺省从系统临时目录取，不写死某个用户名下的路径（换机器就 ENOENT）
const JSON_PATH = process.env.PATCH || join(tmpdir(), 'ghce-csharp-patch.json');
const PORT = Number(process.env.REPLAY_PORT || 8788);
const CDP_PORT = Number(process.env.CDP_PORT || 9334);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PAGE = `${process.env.RUNTIME || 'http://127.0.0.1:5173/'}?ws=ws://127.0.0.1:${PORT}/ghce`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const patch = await readFile(JSON_PATH, 'utf8');

/* ---------- 极简桥：连接即发一帧 C# 产出的 patch ---------- */
const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: '/ghce' });
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ v: '0.1', t: 'welcome', doc: 'csharp-replay' }));
  ws.send(patch);
  ws.on('message', (raw) => {
    const m = JSON.parse(raw.toString());
    if (m.t === 'ping') ws.send(JSON.stringify({ v: '0.1', t: 'pong' }));
  });
});

/* ---------- CDP ---------- */
const profile = mkdtempSync(join(tmpdir(), 'ghce-replay-'));
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
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
  return r.result.value;
};

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(2200);

const got = await evaluate(`(() => {
  const G = window.GHCE;
  const c = G.doc.cmds.get('c_test');
  return {
    conn: G.client.state,
    canvas: G.doc.canvas,
    stats: G.doc.stats(),
    dash: c ? c.style.stroke.dash : null,
    fill: c ? c.style.fill.color : null,
    color: c ? c.style.stroke.color : null,
    kind: G.doc.geoms.get('g_test')?.kind,
    pts: G.doc.geoms.get('g_test')?.coords?.length,
    matrix: G.doc.cmds.get('c_test')?.transform.matrix || null,
    progress: G.doc.cmds.get('c_test')?.transform.progress ?? null,
    svgMatrix: (() => {
      const s = G.toSVG(G.doc).svg;
      const i = s.indexOf('matrix(');
      // 只取 matrix(...) 这一段：后面还会拼 rotate(...)/translate(...)，属于标量叠加层
      return i < 0 ? null : s.slice(i, s.indexOf(')', i) + 1);
    })(),
    entities: G.entities.stats.entities,
    // v0.16：时间轨挂在指令上，不再有全局 timeline
    tracks: [...G.doc.cmds.values()].filter((x) => x.track).length,
    reveal: G.doc.cmds.get('c_test') ? +G.doc.cmds.get('c_test').style.reveal.toFixed(3) : null,
    time: +G.clock.time.toFixed(2),
    drawn: G.renderer.lastDraw.drawn,
    skipped: G.renderer.lastDraw.skipped,
    layer: (() => {
      const l = G.doc.layers.get('main');
      return l ? { order: l.order, opacity: l.opacity, blend: l.blend, visible: l.visible } : null;
    })(),
    order: G.doc.drawList ? G.doc.drawList().map((c) => c.id) : null,
    errs: document.getElementById('errs').textContent
  };
})()`);

const results = [];
const check = (name, pass, detail) => {
  results.push(pass);
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}  → ${JSON.stringify(detail)}`);
};

check('运行时连上回放桥', got.conn === 'open', got.conn);
check('doc.reset 的 canvas 被接受', got.canvas.w === 1200 && got.canvas.h === 800 && got.canvas.bg === '#0b0b0f', got.canvas);
check('layer/geom/cmd 三个 op 全部入库', got.stats.layers === 2 && got.stats.geoms === 1 && got.stats.cmds === 1, got.stats);
check('实体蓝图展开 50 个实例', got.entities === 50, { entities: got.entities });
check('关键帧轨道载入并在浏览器里推进', got.tracks === 1 && got.reveal > 0 && got.reveal <= 1,
  { tracks: got.tracks, reveal: got.reveal, time: got.time });
check('f32 base64 解码成 2 个点', got.kind === 'polyline' && got.pts === 4, { kind: got.kind, floats: got.pts });
check('dash 数组原样保留', Array.isArray(got.dash) && got.dash[0] === 4 && got.dash[1] === 3, got.dash);
check('颜色大小写不影响解析', got.color === '#E8E6DF' && got.fill === '#112233', { stroke: got.color, fill: got.fill });
check('2D 仿射矩阵原样进入运行时', Array.isArray(got.matrix) && got.matrix.join(',') === '0,-1,1,0,10,-20', got.matrix);
// v0.13：progress 由浏览器接管（GH 只发终态 1，中间帧是 tween 写的），
// 所以这里验的是"入场演完之后落在终态"，而不是把 0.5 照搬过来。
check('入场演完后 progress = 1（过渡引擎接管）', got.progress === 1, got.progress);
// v0.10：显式图层定义（Layer 电池）必须原样落地，而不是被"首次出现顺序 × 10"覆盖
check('显式图层 order / opacity / blend 落地',
  got.layer && got.layer.order === 42 && got.layer.opacity === 0.5 && got.layer.blend === 'multiply',
  got.layer);
// 演完之后 SVG 里必须是 C# 发来的那个终态矩阵（90° + 平移 10,-20），一个数不差。
// 中间态的插值由 verify-tween.mjs / verify-v012.mjs 负责，这里只验两端一致。
const near = (a, b, tol = 1e-3) => Math.abs(a - b) <= tol;
const svgNums = (typeof got.svgMatrix === 'string' ? got.svgMatrix.match(/-?\d+(?:\.\d+)?/g) || [] : []).map(Number);
check('SVG 用的是 C# 发来的终态矩阵（90°）',
  svgNums.length === 6
  && near(svgNums[0], 0) && near(svgNums[1], -1) && near(svgNums[2], 1) && near(svgNums[3], 0)
  && near(svgNums[4], 10) && near(svgNums[5], -20),
  got.svgMatrix);
check('该指令被真正画出来', got.drawn === 1 && got.skipped === 0, { drawn: got.drawn, skipped: got.skipped });
check('面板无错误', got.errs.trim() === '' || got.errs.trim() === '无错误', got.errs);

const errs = events.filter((e) =>
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
