// replay-shader.mjs — 把 C# PatchBuilder 真实产出的 shader patch 灌进运行时
//
// 这个脚本存在的唯一理由：C# 内部结构对、运行时内部逻辑对，不代表中间那段 JSON 对。
// 字段名差一个字母、数字被序列化成字符串、数组被包了一层，两边的断言都是绿的。
// 只有真把 C# 的字节喂给运行时，才算证明"两端字段一致"。
import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const JSON_PATH = process.env.PATCH || join(tmpdir(), 'colugo-csharp-patch-shader.json');
const PORT = Number(process.env.REPLAY_PORT || 8789);
const CDP_PORT = Number(process.env.CDP_PORT || 9335);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const PAGE = `${process.env.RUNTIME || 'http://127.0.0.1:5173/'}?ws=ws://127.0.0.1:${PORT}/colugo`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let pass = 0;
let fail = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; console.log(`  FAIL  ${name}  ${JSON.stringify(detail)}`); }
};

const patch = await readFile(JSON_PATH, 'utf8');

/* ---------- 极简桥：连接即发一帧 C# 产出的 patch ---------- */
const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: '/colugo' });
wss.on('connection', (ws) => {
  ws.send(JSON.stringify({ v: '0.1', t: 'welcome', doc: 'csharp-shader' }));
  ws.send(patch);
  ws.on('message', (raw) => {
    const m = JSON.parse(raw.toString());
    if (m.t === 'ping') ws.send(JSON.stringify({ v: '0.1', t: 'pong' }));
  });
});

/* ---------- CDP ---------- */
const profile = mkdtempSync(join(tmpdir(), 'colugo-shader-replay-'));
const chrome = spawn(CHROME, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
  '--enable-unsafe-swiftshader',
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
if (!wsUrl) { console.log('FAIL: chrome 没起来'); process.exit(1); }

const cdpWs = new WebSocket(wsUrl);
await new Promise((r) => cdpWs.on('open', r));
let id = 0;
const pending = new Map();
const logs = [];
cdpWs.on('message', (raw) => {
  const m = JSON.parse(raw.toString());
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  else if (m.method === 'Runtime.consoleAPICalled') {
    logs.push(m.params.type + ': ' + (m.params.args || []).map((a) => a.value ?? a.description).join(' '));
  } else if (m.method === 'Runtime.exceptionThrown') {
    logs.push('EXC: ' + (m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text));
  }
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdpWs.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) return { __error: r.exceptionDetails.text };
  return r.result.value;
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(2600);

/* ---------- 等 three 加载 + IR 编译 ---------- */
const r = await ev(`(async () => {
  const G = window.Colugo;
  if (!G) return { noApp: true };
  const t0 = performance.now();
  while (performance.now() - t0 < 20000) {
    const S = G.scene3d;
    if (S && S.backend && S.shader && S.shader.stats.builds >= 1) break;
    await new Promise(r => setTimeout(r, 100));
  }
  await new Promise(r => setTimeout(r, 600));
  const S = G.scene3d;
  const keys = [...S.materials.keys()];
  const mat = S.materials.get(keys[0]);
  return {
    ready: !!S.backend,
    backend: S.backend,
    objects: S.stats().objects,
    tris: S.stats().tris,
    builds: S.shader.stats.builds,
    failures: S.shader.stats.failures,
    ops: S.shader.lastOps || [],
    outType: S.shader.lastOutType,
    animated: !!S._shaderAnimated,
    matKey: keys[0] || '',
    matCount: keys.length,
    hasColorNode: !!(mat && mat.colorNode),
    err: S.lastShaderError || '',
    ms: Math.round(performance.now() - t0),
  };
})()`);

check('运行时起得来（three 已加载、后端已确定）', r.ready === true, r);
check('C# 发来的 mesh 进了场景', r.objects >= 1, r);
check('C# 发来的 IR 被编译（builds ≥ 1）', r.builds >= 1, { builds: r.builds });
check('编译没有报错', r.failures === 0 && !r.err, r);
check('材质的 colorNode 来自 IR，不是常量色', r.hasColorNode === true, r);
check('输出类型是 v3（颜色域）', r.outType === 'v3', r.outType);
check('C# 图里的 time 节点让场景动起来', r.animated === true, r.animated);
check('材质缓存 key 带 C# 算出的结构哈希', /[0-9a-f]{8}/.test(r.matKey), r.matKey);

// C# 那张图用了 9 种 op：position / swizzle / const / mul / time / add / sin / remap / mix
const want = ['position', 'swizzle', 'const', 'mul', 'time', 'add', 'sin', 'remap', 'mix'];
const missing = want.filter((o) => !r.ops.includes(o));
check('C# 图用到的 9 种 op 全部被运行时认出来', missing.length === 0, { ops: r.ops, missing });
check('只用了一个材质（一张图一份材质）', r.matCount === 1, { matCount: r.matCount });

/* ---------- 截图：像素证据 ---------- */
const shot = await send('Page.captureScreenshot', { format: 'png' });
if (shot.data) {
  const p = join(tmpdir(), 'colugo-shader-replay.png');
  writeFileSync(p, Buffer.from(shot.data, 'base64'));
  console.log('  screenshot: ' + p);
}

const bad = logs.filter((l) => /^EXC:/.test(l) || /^error:/.test(l));
check('无控制台错误', bad.length === 0, bad);

console.log(`\n${pass}/${pass + fail} passed`);
wss.close();
process.exit(fail ? 1 : 0);
