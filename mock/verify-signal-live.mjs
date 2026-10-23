// 抓"真实题面"：插件桥 + 真浏览器页面，检查运行时是否收到 signal 定义
// 与 mock 的区别：这里没有 mock，消息由 C# 的 BridgeServer.PublishSignal 发出
import { spawn } from 'node:child_process';
import WebSocket from 'ws';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const BASE = process.env.BASE || 'http://127.0.0.1:8788/';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const profile = mkdtempSync(join(tmpdir(), 'ghce-sig-'));
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--disable-gpu', '--window-size=1200,800', '--no-first-run',
  `--user-data-dir=${profile}`, '--remote-debugging-port=9555', 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 60; i++) {
  try {
    const list = await (await fetch('http://127.0.0.1:9555/json/list')).json();
    const page = list.find((t) => t.type === 'page');
    if (page) { wsUrl = page.webSocketDebuggerUrl; break; }
  } catch { /* 还没起来 */ }
  await sleep(200);
}
if (!wsUrl) { console.log('FAIL: chrome 没起来'); process.exit(1); }

const cdp = new WebSocket(wsUrl);
await new Promise((r) => cdp.on('open', r));
let id = 0;
const pending = new Map();
const logs = [];
cdp.on('message', (raw) => {
  const m = JSON.parse(raw);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  if (m.method === 'Runtime.consoleAPICalled') {
    logs.push((m.params.args || []).map((a) => a.value ?? a.description).join(' '));
  }
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdp.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) return { __error: r.exceptionDetails.text };
  return r.result.value;
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url: BASE });
await sleep(2500);

const state = await ev(`(() => {
  const G = window.GHCE;
  if (!G) return { noApp: true, title: document.title };
  return {
    conn: G.client.state,
    statSent: G.client.metrics.statSent,
    signalIds: [...G.signals.keys()],
    defs: [...G.signals.values()],
    time: +G.clock.time.toFixed(2),
  };
})()`);

console.log(JSON.stringify(state, null, 1));

// stat 里到底带了什么：直接复算运行时用的同一个函数
const snap = await ev(`(() => {
  const G = window.GHCE;
  const t = 1.234;
  const out = {};
  for (const [id, d] of G.signals) out[id] = +G.signalValue(d, t).toFixed(4);
  return out;
})()`);
console.log('stat.signals 会是:', JSON.stringify(snap));

const errs = logs.filter((l) => /error|fail|undefined/i.test(l)).slice(0, 5);
console.log('console warnings:', errs.length ? errs : 'none');

cdp.close();
chrome.kill();
process.exit(0);
