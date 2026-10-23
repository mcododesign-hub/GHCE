// 读 __probe + 截图 + 收集 console
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const URL_ = process.env.URL || 'http://127.0.0.1:8891/';
const OUT = process.env.OUT || join(tmpdir(), 'tsl-probe.png');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const profile = mkdtempSync(join(tmpdir(), 'tsl-'));
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--window-size=680,440', '--no-first-run',
  '--enable-unsafe-swiftshader', '--no-proxy-server', '--disable-features=CalculateNativeWinOcclusion',
  `--user-data-dir=${profile}`, '--remote-debugging-port=9666', 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 80; i++) {
  try {
    const list = await (await fetch('http://127.0.0.1:9666/json/list')).json();
    const page = list.find((t) => t.type === 'page');
    if (page) { wsUrl = page.webSocketDebuggerUrl; break; }
  } catch { /* 等 */ }
  await sleep(200);
}
if (!wsUrl) { console.log('FAIL: chrome'); process.exit(1); }

const cdp = new WebSocket(wsUrl);
await new Promise((r) => cdp.on('open', r));
let id = 0;
const pending = new Map();
const console_ = [];
cdp.on('message', (raw) => {
  const m = JSON.parse(raw);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  if (m.method === 'Runtime.consoleAPICalled') {
    console_.push(m.params.type + ': ' + (m.params.args || []).map((a) => a.value ?? a.description).join(' '));
  }
  if (m.method === 'Network.responseReceived') {
    const r = m.params.response;
    if (r.status >= 400) console_.push('HTTP ' + r.status + ' ' + r.url);
  }
  if (m.method === 'Network.loadingFailed') {
    console_.push('NETFAIL ' + m.params.errorText + ' ' + (m.params.type || ''));
  }
  if (m.method === 'Runtime.exceptionThrown') {
    console_.push('EXCEPTION: ' + (m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text));
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
await send('Network.enable');
await send('Page.navigate', { url: URL_ });
await sleep(6000);

const probe = await ev('JSON.stringify(window.__probe || {missing:true})');
console.log('probe:', probe);
console.log('console:', console_.slice(0, 8).length ? console_.slice(0, 8) : 'none');

const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));
console.log('screenshot:', OUT);

// 像素统计：确认画布不是全黑
const px = await ev(`(() => {
  const c = document.querySelector('canvas');
  if (!c) return { noCanvas: true };
  const t = document.createElement('canvas');
  t.width = c.width; t.height = c.height;
  t.getContext('2d').drawImage(c, 0, 0);
  const d = t.getContext('2d').getImageData(0, 0, t.width, t.height).data;
  const seen = new Set();
  let lit = 0, n = 0;
  for (let i = 0; i < d.length; i += 4 * 17) {
    n++;
    const k = (d[i] << 16) | (d[i+1] << 8) | d[i+2];
    seen.add(k);
    if (k > 0x151515) lit++;
  }
  return { w: t.width, h: t.height, palette: seen.size, litRatio: +(lit / n).toFixed(3) };
})()`);
console.log('pixels:', JSON.stringify(px));

cdp.close();
chrome.kill();
process.exit(0);
