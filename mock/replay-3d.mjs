// replay-3d.mjs — 把 C# 产出的三维 patch 灌进运行时，验证两端协议一致
// 与 replay-csharp.mjs 同一套路，只是断言换成三维的字段。
import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const JSON_PATH = process.env.PATCH3D || join(tmpdir(), 'colugo-csharp-patch3d.json');
const PORT = Number(process.env.REPLAY_PORT || 8791);
const CDP_PORT = Number(process.env.CDP_PORT || 9336);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PAGE = `${process.env.RUNTIME || 'http://127.0.0.1:5293/'}?ws=ws://127.0.0.1:${PORT}/colugo`;
const OUT = process.env.OUT || join(tmpdir(), 'colugo-replay3d.png');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const patch = await readFile(JSON_PATH, 'utf8');

let pass = 0, fail = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; console.log(`  FAIL  ${name}  ${JSON.stringify(detail)}`); }
};

/* 极简桥：连接即发一帧 */
const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: '/colugo' });
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ v: '0.1', t: 'welcome', doc: 'csharp-replay-3d' }));
  ws.send(patch);
  ws.on('message', (raw) => {
    const m = JSON.parse(raw.toString());
    if (m.t === 'ping') ws.send(JSON.stringify({ v: '0.1', t: 'pong' }));
  });
});

/* CDP */
const profile = mkdtempSync(join(tmpdir(), 'colugo-r3-'));
const chrome = spawn(CHROME, [
  '--headless=new', '--hide-scrollbars', '--no-first-run', '--no-proxy-server',
  '--enable-unsafe-swiftshader', '--window-size=900,700',
  `--user-data-dir=${profile}`, `--remote-debugging-port=${CDP_PORT}`, 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 80; i++) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`)).json();
    const page = list.find((t) => t.type === 'page');
    if (page) { wsUrl = page.webSocketDebuggerUrl; break; }
  } catch { /* 等 */ }
  await sleep(200);
}
const cdp = new WebSocket(wsUrl);
await new Promise((r) => cdp.on('open', r));
let id = 0;
const pending = new Map();
const logs = [];
cdp.on('message', (raw) => {
  const m = JSON.parse(raw);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  if (m.method === 'Runtime.consoleAPICalled') logs.push((m.params.args || []).map((a) => a.value ?? a.description).join(' '));
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdp.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  return r.exceptionDetails ? { __error: r.exceptionDetails.exception?.description || r.exceptionDetails.text } : r.result.value;
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(1800);   // 等 main.js 把 window.Colugo 挂上

const got = await ev(`(async () => {
  const G = window.Colugo;
  if (!G) return { noApp: true };
  const t0 = performance.now();
  while (performance.now() - t0 < 15000) {
    const s = G.scene3d.stats();
    if (s.meshes > 0 && s.objects > 0 && s.backend) break;
    await new Promise(r => setTimeout(r, 100));
  }
  const S = G.scene3d;
  const obj = [...S.objects.values()][0];
  const cmd = [...S.cmds.values()][0];
  const mesh = [...S.meshes.values()][0];
  const e = obj ? obj.matrix.elements : null;
  return {
    ...S.stats(),
    meshCount: mesh ? mesh.count : 0,
    tris: mesh ? mesh.tris : 0,
    mode: cmd ? cmd.material.mode : null,
    color: cmd ? cmd.material.color : null,
    size: cmd ? cmd.material.size : null,
    translate: e ? [e[12], e[13], e[14]] : null,
    objectCount: S.objects.size,
  };
})()`);

check('C# 的 mesh 被运行时解出来', got.meshCount === 3 && got.tris === 1, got);
check('材质 mode 与颜色被保留', got.mode === 3 && got.color === '#07a954', { mode: got.mode, color: got.color });
check('点大小到达运行时', got.size === 4, { size: got.size });
check('矩阵平移落在 (1,2,3)', Array.isArray(got.translate)
  && Math.abs(got.translate[0] - 1) < 1e-6
  && Math.abs(got.translate[1] - 2) < 1e-6
  && Math.abs(got.translate[2] - 3) < 1e-6, { translate: got.translate });
check('场景里有一个对象', got.objectCount === 1, { objects: got.objectCount });

await sleep(600);
const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));
console.log('  screenshot:', OUT);

const errs = logs.filter((l) => /error|undefined|failed/i.test(l) && !/favicon/i.test(l));
check('无控制台错误', errs.length === 0, errs.slice(0, 3));

console.log(`\n${pass}/${pass + fail} passed`);
wss.close();
cdp.close();
chrome.kill();
process.exit(fail ? 1 : 0);
