// verify-plugin-serve.mjs — 验证"插件自己发页面"这条链路。
// 前置：在另一个终端跑
//   cd gh/probe && DOTNET_ROOT="C:/Program Files/dotnet" "/c/Program Files/dotnet/dotnet.exe" ./bin/Release/net7.0-windows/probe.dll --serve
// 它用插件里的 BridgeServer（同一个 TcpListener）在 8799 上既发内嵌页面、又推 WS patch。
// 这个脚本不依赖 node 的静态服务器，也不依赖 5173。

import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { writeFile } from 'node:fs/promises';
import WebSocket from 'ws';

const BASE = process.env.BASE || 'http://127.0.0.1:8799';
const PAGE = `${BASE}/?ws=${BASE.replace('http', 'ws')}/colugo`;
const CDP_PORT = Number(process.env.CDP_PORT || 9335);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const profile = mkdtempSync(join(tmpdir(), 'colugo-serve-'));
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

const ws = new WebSocket(wsUrl);
await new Promise((r) => ws.on('open', r));
let id = 0;
const pending = new Map();
const events = [];
ws.on('message', (raw) => {
  const m = JSON.parse(raw.toString());
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  else if (m.method) events.push(m);
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
  return r.result.value;
};

await send('Runtime.enable');
await send('Log.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(3000);

const first = await ev(`(() => {
  const G = window.Colugo;
  return { title: document.title, conn: G.client.state, rev: G.doc.rev, ...G.doc.stats(),
           drawn: G.renderer.lastDraw.drawn, skipped: G.renderer.lastDraw.skipped,
           canvasW: G.renderer.canvas.width,
           devHidden: document.getElementById('dev').hidden,
           hudGone: !document.getElementById('hud') };
})()`);
await sleep(1600);
const second = await ev('({ rev: window.Colugo.doc.rev })');

const results = [];
const check = (n, p, d) => { results.push(p); console.log(`${p ? 'PASS' : 'FAIL'}  ${n}  → ${JSON.stringify(d)}`); };

check('页面由插件内嵌资源提供', first.title === 'Colugo Runtime · V0.1', first.title);
check('WS 连上插件自己的桥', first.conn === 'open', first.conn);
check('60 条路径全部入库', first.geoms === 60 && first.cmds === 60, { geoms: first.geoms, cmds: first.cmds });
check('全部被绘制，无跳过', first.drawn === 60 && first.skipped === 0, { drawn: first.drawn, skipped: first.skipped });
check('收到的帧在持续变化', second.rev > first.rev, { from: first.rev, to: second.rev });
check('接真 GH 时默认不出现任何面板', first.hudGone === true && first.devHidden === true, { hudGone: first.hudGone, devHidden: first.devHidden });

const shot = await send('Page.captureScreenshot', {
  format: 'png',
  clip: { x: 0, y: 0, width: 1280, height: 800, scale: 1 },
});
const out = join(tmpdir(), 'colugo-plugin-serve.png');
await writeFile(out, Buffer.from(shot.data, 'base64'));
console.log('  screenshot:', out);

const errs = events.filter((e) =>
  (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error') ||
  e.method === 'Runtime.exceptionThrown' ||
  (e.method === 'Log.entryAdded' && e.params.entry.level === 'error'));
check('无控制台错误', errs.length === 0, errs.slice(0, 2).map((e) => e.params.entry?.text || e.params.exceptionDetails?.text));

ws.close();
chrome.kill();
const pass = results.filter(Boolean).length;
console.log(`\n${pass}/${results.length} passed`);
process.exit(pass === results.length ? 0 : 1);
