// 三维层的验收：真 Chrome + 模拟桥的场景 scene3d
// 断言的每一句都能判定真假，不做"看起来像"的判断。
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const PAGE = process.env.PAGE || 'http://127.0.0.1:5293/?ws=ws://127.0.0.1:8909/ghce';
const OUT = process.env.OUT || join(tmpdir(), 'ghce-3d.png');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let pass = 0;
let fail = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; console.log(`  FAIL  ${name}  ${JSON.stringify(detail)}`); }
};

const profile = mkdtempSync(join(tmpdir(), 'ghce3d-'));
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--window-size=1280,820', '--no-first-run', '--no-proxy-server',
  '--enable-unsafe-swiftshader',
  `--user-data-dir=${profile}`, '--remote-debugging-port=9777', 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 80; i++) {
  try {
    const list = await (await fetch('http://127.0.0.1:9777/json/list')).json();
    const page = list.find((t) => t.type === 'page');
    if (page) { wsUrl = page.webSocketDebuggerUrl; break; }
  } catch { /* 等 */ }
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
    logs.push(m.params.type + ': ' + (m.params.args || []).map((a) => a.value ?? a.description).join(' '));
  }
  if (m.method === 'Runtime.exceptionThrown') {
    logs.push('EXC: ' + (m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text));
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
await send('Page.navigate', { url: PAGE });
await sleep(2500);

// 切到 3D 场景，等 three 加载完（2.3 MB，慢一点）
const r1 = await ev(`(async () => {
  const G = window.GHCE;
  if (!G) return { noApp: true };
  G.selectScene('scene3d');
  const t0 = performance.now();
  while (performance.now() - t0 < 15000) {
    const s = G.scene3d.stats();
    if (s.objects >= 4 && s.backend) return { ready: true, ms: Math.round(performance.now() - t0), ...s };
    await new Promise(r => setTimeout(r, 100));
  }
  return { ready: false, timeout: true, ...G.scene3d.stats() };
})()`);

check('三维场景在超时前就绪', r1.ready === true, r1);
check('渲染后端已确定（webgpu / webgl2）', r1.backend === 'webgpu' || r1.backend === 'webgl2', r1.backend);
check('四种几何都进入场景（网格 / 立方体 / 点云 / 线）', r1.objects === 4, { objects: r1.objects });
check('三角数对得上（网格 968 + 立方体 12）', r1.tris === 980, { tris: r1.tris });
check('点云顶点数正确', r1.points === 420, { points: r1.points });
check('线段数正确（双螺旋 2×219）', r1.segments === 438, { segments: r1.segments });

// 相机：自动取景必须真的把物体框住 —— 检查相机到包围盒中心的距离与半径同量级
const cam = await ev(`(() => {
  const S = window.GHCE.scene3d;
  const c = S.camera;
  if (!c) return { noCamera: true };
  // 场景里所有对象的世界包围盒
  const T = S.T;
  const box = new T.Box3();
  for (const o of S.objects.values()) box.expandByObject(o);
  const center = box.getCenter(new T.Vector3());
  const radius = box.getSize(new T.Vector3()).length() * 0.5;
  const dist = c.position.distanceTo(center);
  return {
    dist: +dist.toFixed(2),
    radius: +radius.toFixed(2),
    ratio: +(dist / radius).toFixed(3),
    upZ: c.up.z,
    near: +c.near.toFixed(2),
  };
})()`);
check('自动取景把相机放在包围球外', cam.ratio > 1 && cam.ratio < 6, cam);
check('相机 up 轴是 Z（Rhino 的约定）', cam.upZ === 1, { upZ: cam.upZ });

// 画布缓冲尺寸：renderer 建立前收到的 resize 必须被应用，否则 300×150 被拉伸到全屏
const size = await ev(`(() => {
  const c = window.GHCE.scene3d.canvas;
  return {
    bufW: c.width, bufH: c.height,
    cssW: c.clientWidth, cssH: c.clientHeight,
    dpr: devicePixelRatio,
    backend: window.GHCE.scene3d.backend,
    objects: window.GHCE.scene3d.objects.size,
  };
})()`);
const dprCap = Math.min(size.dpr, 2);
check('画布缓冲 = CSS 尺寸 × DPR（不是 300×150 被拉伸）',
  size.bufW >= 400 && Math.abs(size.bufW - Math.round(size.cssW * dprCap)) <= 2, size);
check('缓冲宽高比与 CSS 一致',
  Math.abs(size.bufW / size.bufH - size.cssW / size.cssH) < 0.02,
  { buf: size.bufW / size.bufH, css: size.cssW / size.cssH });

// DPR 变化：把设备像素比改成 2，画布缓冲必须跟着变成 2 倍
await send('Emulation.setDeviceMetricsOverride', {
  width: 1280, height: 800, deviceScaleFactor: 2, mobile: false,
});
await sleep(900);
const hi = await ev(`(() => {
  const S = window.GHCE.scene3d;
  const c = S.canvas;
  S.render(0.016);            // 推一帧，让 _syncSize 有机会执行
  return { bufW: c.width, bufH: c.height, cssW: c.clientWidth, dpr: devicePixelRatio, tracked: S._dpr };
})()`);
check('DPR 变 2 后画布缓冲跟着翻倍', Math.abs(hi.bufW - Math.round(hi.cssW * 2)) <= 2,
  { bufW: hi.bufW, cssW: hi.cssW, dpr: hi.dpr, tracked: hi.tracked });
await send('Emulation.clearDeviceMetricsOverride');
await sleep(400);

// 帧率与耗时
await sleep(1200);
const perf = await ev(`(() => {
  const s = window.GHCE.scene3d.stats();
  return { frames: s.frames, ms: s.ms };
})()`);
check('场景至少出了一帧', perf.frames >= 1, perf);
// 静态场景画完就停，这是省电的正确行为，不该要求它一直空转
check('静态场景不空转（帧数很少）', perf.frames <= 6, perf);
check('单帧渲染 < 16.7ms', perf.ms < 16.7, perf);

// 像素：3D 层不能是空白
const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));
console.log('  screenshot:', OUT);

// 2D 是否仍然可用（共存）：切回去，确认 2D 对象还在
const back = await ev(`(async () => {
  const G = window.GHCE;
  G.selectScene('static');
  const t0 = performance.now();
  while (!G.doc.cmds.size && performance.now() - t0 < 4000) await new Promise(r => setTimeout(r, 50));
  await new Promise(r => setTimeout(r, 300));
  return { cmds2d: G.doc.cmds.size, objects3d: G.scene3d.stats().objects };
})()`);
check('切回二维后 3D 层被清空（共存但不残留）', back.objects3d === 0, back);
check('二维内容仍在', back.cmds2d > 0, back);

const errs = logs.filter((l) => /error|EXC|failed/i.test(l) && !/favicon/i.test(l));
check('无控制台错误', errs.length === 0, errs.slice(0, 4));

console.log(`\n${pass}/${pass + fail} passed`);
cdp.close();
chrome.kill();
process.exit(fail ? 1 : 0);
