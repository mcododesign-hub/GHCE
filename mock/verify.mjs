// verify.mjs — 真浏览器验收（CDP 驱动无头 Chrome）
// 用法：node verify.mjs            （需先 npm run dev 起服务）
// 退出码 0 = 全部通过；1 = 有断言失败
//
// 为什么不用 --screenshot + --virtual-time-budget：
// 虚拟时间下 WebSocket 回调不会被真实调度，会得到假的「未连接」画面。

import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const URL_PAGE = process.env.PAGE || 'http://127.0.0.1:5173/';
const CDP_PORT = Number(process.env.CDP_PORT || 9333);
const CHROME = process.env.CHROME || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const OUT_DIR = process.env.OUT || tmpdir();

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const LIVING_FRAMES = 25;

/* ---------- 启动 Chrome ---------- */
const profile = mkdtempSync(join(tmpdir(), 'ghce-verify-'));
const chrome = spawn(CHROME, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--mute-audio',
  '--no-first-run', '--no-default-browser-check',
  '--window-size=1440,900',
  `--user-data-dir=${profile}`,
  `--remote-debugging-port=${CDP_PORT}`,
  'about:blank',
], { stdio: 'ignore' });

async function cdpTarget() {
  for (let i = 0; i < 60; i++) {
    try {
      const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`);
      const list = await r.json();
      const page = list.find((t) => t.type === 'page');
      if (page) return page.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(200);
  }
  throw new Error('CDP 端口未就绪');
}

/* ---------- CDP 客户端 ---------- */
class CDP {
  constructor(ws) {
    this.ws = ws;
    this.id = 0;
    this.pending = new Map();
    this.events = [];
    ws.on('message', (raw) => {
      const m = JSON.parse(raw.toString());
      if (m.id && this.pending.has(m.id)) {
        const { res, rej } = this.pending.get(m.id);
        this.pending.delete(m.id);
        m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result);
      } else if (m.method) {
        this.events.push(m);
      }
    });
  }
  send(method, params = {}) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
  async eval(expression) {
    const r = await this.send('Runtime.evaluate', {
      expression, returnByValue: true, awaitPromise: true, userGesture: true,
    });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ' ' + JSON.stringify(r.exceptionDetails.exception?.description || ''));
    return r.result.value;
  }
}

/* ---------- 断言 ---------- */
const results = [];
function check(name, pass, detail) {
  results.push({ name, pass: !!pass, detail });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail !== undefined ? '  → ' + JSON.stringify(detail) : ''}`);
}

/* ---------- 主流程 ---------- */
const wsUrl = await cdpTarget();
const ws = new WebSocket(wsUrl, { maxPayload: 64 * 1024 * 1024 });
await new Promise((res, rej) => { ws.on('open', res); ws.on('error', rej); });
const cdp = new CDP(ws);

await cdp.send('Runtime.enable');
await cdp.send('Log.enable');
await cdp.send('Page.enable');
await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 2, mobile: false });

await cdp.send('Page.navigate', { url: URL_PAGE });
await sleep(2500);   // 等 WS 握手 + 首帧

const shot = async (name) => {
  const vp = await cdp.eval('({ w: innerWidth, h: innerHeight })');
  const r = await cdp.send('Page.captureScreenshot', {
    format: 'png',
    clip: { x: 0, y: 0, width: vp.w, height: vp.h, scale: 1 },
  });
  const f = join(OUT_DIR, `ghce-${name}.png`);
  await writeFile(f, Buffer.from(r.data, 'base64'));
  return f;
};

/* 1. 连接与文档状态 */
const s1 = await cdp.eval(`(() => {
  const G = window.GHCE;
  return { conn: G.client.state, rev: G.doc.rev, ...G.doc.stats(), dpr: G.renderer._dpr,
           canvasW: G.renderer.canvas.width, errs: document.getElementById('errs').textContent,
           hudGone: !document.getElementById('hud'),
           devHidden: document.getElementById('dev').hidden,
           stagePad: getComputedStyle(document.getElementById('stage')).paddingLeft };
})()`);
check('WS 已连接', s1.conn === 'open', s1.conn);
// v0.29：运行时默认只有画面 —— 没有常驻 HUD，也不给画布留面板边距
check('默认没有常驻面板（画面不被挤压）', s1.hudGone === true && s1.devHidden === true && s1.stagePad === '0px', { hudGone: s1.hudGone, devHidden: s1.devHidden, pad: s1.stagePad });
check('收到 patch（rev>0）', s1.rev > 0, { rev: s1.rev });
check('几何与指令已入库', s1.geoms > 0 && s1.cmds > 0, { geoms: s1.geoms, cmds: s1.cmds });
check('backing store 按 DPR 放大', s1.canvasW >= 1440 * 2 - 4, { canvasW: s1.canvasW, dpr: s1.dpr });

/* 2. 真像素测量：画布不是空白 */
const px = await cdp.eval(`(() => {
  const c = window.GHCE.renderer.canvas;
  const g = c.getContext('2d');
  const d = g.getImageData(0, 0, c.width, c.height).data;
  const seen = new Map();
  let total = 0;
  for (let i = 0; i < d.length; i += 4 * 37) {           // 稀疏采样
    total++;
    const k = (d[i] << 16) | (d[i+1] << 8) | d[i+2];
    seen.set(k, (seen.get(k) || 0) + 1);
  }
  let maxKey = 0, maxCnt = 0;
  for (const [k, v] of seen) if (v > maxCnt) { maxCnt = v; maxKey = k; }
  return { total, samples: seen.size, dominant: '#' + maxKey.toString(16).padStart(6, '0'),
           dominantShare: +(maxCnt / total).toFixed(3), inkRatio: +(1 - maxCnt / total).toFixed(3) };
})()`);
check('画布有实际绘制内容', px.inkRatio > 0.02 && px.samples > 100, px);

const f1 = await shot('static');
console.log('  screenshot:', f1);

/* 3. 增量更新：只有几何变，指令不重传 */
const s2 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('mutate');
  await new Promise(r => setTimeout(r, 1600));
  return { rev: G.doc.rev, patches: G.client.metrics.patches, patchMs: +G.client.metrics.lastPatchMs.toFixed(2),
           ops: G.client.metrics.lastOps, bytes: G.client.metrics.lastBytes, ...G.doc.stats() };
})()`);
check('持续场景仍在收 patch', s2.patches > 1 && s2.rev > s1.rev, { patches: s2.patches, rev: s2.rev });
check('单帧 op 数与带宽可控', s2.ops <= 40 && s2.bytes < 200 * 1024, { ops: s2.ops, bytes: s2.bytes });
check('patch 应用耗时可测', s2.patchMs > 0, { ms: s2.patchMs });

/* 4. 500 路径压力 */
const s3 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('stress');
  await new Promise(r => setTimeout(r, 2200));
  const t0 = performance.now();
  G.renderer.draw();
  const oneFrame = performance.now() - t0;
  return { ...G.doc.stats(), drawMs: +G.renderer.lastDraw.ms.toFixed(2),
           drawn: G.renderer.lastDraw.drawn, skipped: G.renderer.lastDraw.skipped,
           manualFrameMs: +oneFrame.toFixed(2) };
})()`);
check('500 条路径全部进入绘制', s3.drawn >= 500 && s3.skipped === 0, { drawn: s3.drawn, skipped: s3.skipped });
check('快照帧可越过 rev 单调性保护', s3.geoms === 500 && s3.cmds === 500, { geoms: s3.geoms, cmds: s3.cmds, rev: s3.rev });
check('单帧绘制 < 16.7ms', s3.manualFrameMs < 16.7, { ms: s3.manualFrameMs });

const f2 = await shot('stress');
console.log('  screenshot:', f2);

/* 5. SVG 导出 */
const svg = await cdp.eval(`(() => {
  const r = window.GHCE.toSVG(window.GHCE.doc);
  return { paths: (r.svg.match(/<path/g) || []).length, bytes: r.svg.length, warnings: r.report.warnings.length };
})()`);
check('SVG 输出保留矢量路径', svg.paths >= 500, { paths: svg.paths, bytes: svg.bytes });

/* 5.5 两种 PNG 导出模式：带底色 / 透明底 */
const exp = await cdp.eval(`(async () => {
  const G = window.GHCE;
  async function sample(blob) {
    const bmp = await createImageBitmap(blob);
    const c = document.createElement('canvas');
    c.width = bmp.width; c.height = bmp.height;
    const g = c.getContext('2d');
    g.drawImage(bmp, 0, 0);
    const d = g.getImageData(0, 0, 1, 1).data;
    return { corner: [d[0], d[1], d[2], d[3]], w: bmp.width, h: bmp.height };
  }
  return {
    alpha: await sample(await G.renderer.exportPNG(1, { transparent: true })),
    opaque: await sample(await G.renderer.exportPNG(1)),
  };
})()`);
check('透明导出：角落 alpha=0', exp.alpha.corner[3] === 0, exp.alpha.corner);
check('普通导出：角落是实底背景', exp.opaque.corner[3] === 255, exp.opaque.corner);
check('两种导出尺寸一致', exp.alpha.w === exp.opaque.w && exp.alpha.h === exp.opaque.h,
  { alpha: [exp.alpha.w, exp.alpha.h], opaque: [exp.opaque.w, exp.opaque.h] });

/* 6. 控制台无错误 */
// WebSocket 的失败重连是浏览器自己写的网络日志，不是我们的代码在报错：
// 客户端本来就会重试（bridge-client 有退避），而"到底连上没有"由前面
// 「WS 已连接」那条断言负责。把它算进"控制台错误"会让套件偶发失败。
const wsRetry = (e) => e.method === 'Log.entryAdded'
  && /WebSocket connection to .* failed/.test(e.params.entry?.text || '');

const errs = cdp.events.filter((e) =>
  !wsRetry(e) && (
    (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error') ||
    e.method === 'Runtime.exceptionThrown' ||
    (e.method === 'Log.entryAdded' && e.params.entry.level === 'error')
  )
);
if (errs.length) console.log('  errs:', JSON.stringify(errs.map((e) => ({
  m: e.method, ts: e.params && e.params.timestamp,
  t: (e.params && (e.params.entry?.text || e.params.exceptionDetails?.exception?.description || e.params.type)) || '',
})).slice(0, 6)));
check('无控制台错误 / 未捕获异常', errs.length === 0,
  errs.slice(0, 3).map((e) => e.params.entry?.text || e.params.exceptionDetails?.text || e.params.args?.[0]?.value));

/* 7. 动态对象（阶段 2）：无新 patch 时画面照走；几何更新后状态保留 */
const ent1 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('entities');
  await new Promise(r => setTimeout(r, 1800));
  window.__e0 = { rev: G.doc.rev, snap: G.entities.snapshot('e_loop0'), stats: { ...G.entities.stats } };
  return window.__e0;
})()`);
await sleep(1200);
const ent2 = await cdp.eval(`(() => {
  const G = window.GHCE;
  return { rev: G.doc.rev, snap: G.entities.snapshot('e_loop0'), draw: { ...G.renderer.lastDraw }, stats: { ...G.entities.stats } };
})()`);

check('实体系统载入 1920 个实例', ent1.stats.entities === 1920, { entities: ent1.stats.entities, blueprints: ent1.stats.blueprints });
check('GH 不再发包时画面仍在推进（rev 不变、age 增长）',
  ent2.rev === ent1.rev && ent2.snap.ages[0] > ent1.snap.ages[0],
  { rev: [ent1.rev, ent2.rev], age: [ent1.snap.ages[0], ent2.snap.ages[0]] });
check('实例沿曲线位移（u 变化）', ent2.snap.us[0] !== ent1.snap.us[0], { before: ent1.snap.us[0], after: ent2.snap.us[0] });

const ent3 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  const before = { rev: G.doc.rev, snap: G.entities.snapshot('e_loop0') };
  G.client.send({ v: '0.1', t: 'cmd', action: 'drift', on: true });
  await new Promise(r => setTimeout(r, 1800));
  return { before, after: G.entities.snapshot('e_loop0'), rev: G.doc.rev };
})()`);
check('几何被改（rev 增加）后实例状态未被重置',
  ent3.rev > ent3.before.rev && ent3.after.ages[0] > ent3.before.snap.ages[0],
  { rev: [ent3.before.rev, ent3.rev], age: [ent3.before.snap.ages[0], ent3.after.ages[0]] });

check('实体场景单帧绘制 < 16.7ms', ent2.draw.ms < 16.7, { ms: ent2.draw.ms, entities: ent2.stats.entities });

// 形状：GH 发来的是归一化局部坐标的折线。偶数号蓝图带三角形，奇数的没形状（回退方块）。
// 必须在这里断言 —— 后面切到 living 场景后 entities 里就没有 e_loop* 了。
const shapes = await cdp.eval(`(() => {
  const G = window.GHCE;
  const out = {};
  for (const [id, bp] of G.entities.blueprints) {
    out[id] = bp.def.shape
      ? { pts: bp.def.shape.coords.length / 2, closed: !!bp.def.shape.closed }
      : null;
  }
  return out;
})()`);
check('带 Shape 的蓝图拿到三角形形状（3 点、闭合）',
  !!shapes.e_loop0 && shapes.e_loop0.pts === 3 && shapes.e_loop0.closed === true, shapes.e_loop0);
check('不带 Shape 的蓝图回退成方块（shape = null）', shapes.e_loop1 === null, shapes.e_loop1);

const f3 = await shot('entities');
console.log('  screenshot:', f3);

await cdp.eval(`window.GHCE.client.send({ v: '0.1', t: 'cmd', action: 'drift', on: false })`);

/* 8. 阶段 3：时钟 / 逐步绘制 / 残影 / 多行为并存 */
const lv1 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('living');
  // 等新场景真的到达（rev 归 1），否则读到的是上一轮的残留
  const t0 = performance.now();
  while (G.doc.rev !== 1 && performance.now() - t0 < 4000) {
    await new Promise(r => setTimeout(r, 50));
  }
  // 把自变量按住：时钟归零 + 留两帧，再采样
  G.clock.reset();
  await new Promise(r => requestAnimationFrame(r));
  await new Promise(r => requestAnimationFrame(r));
  return {
    mode: G.doc.canvas.clearMode,
    // v0.16 起 reveal 由时间轨算，不再读 style.reveal
    reveal: +G.trackAt('c_ln0').reveal.toFixed(3),
    time: +G.clock.time.toFixed(2),
    behaviors: [...G.entities.blueprints.keys()],
    entities: G.entities.stats.entities,
  };
})()`);
await sleep(700);
const lv2 = await cdp.eval(`(() => {
  const G = window.GHCE;
  return { reveal: +G.trackAt('c_ln0').reveal.toFixed(3), time: +G.clock.time.toFixed(2) };
})()`);

check('残影模式生效（clearMode=fade）', lv1.mode === 'fade', lv1.mode);
check('Stroke Reveal 随时间推进', lv2.reveal > lv1.reveal, { from: lv1.reveal, to: lv2.reveal });
check('三条蓝图并存（同一种语义，只差参数）',
  lv1.behaviors.includes('e_slow') && lv1.behaviors.includes('e_fast') && lv1.behaviors.includes('e_life'),
  lv1.behaviors);

/* 8.5 时间轴：全局时长/循环在 Timeline，轨道只声明自己的区间 */
const tlq = await cdp.eval(`(() => {
  const G = window.GHCE;
  const hiddenProbe = (() => {
    const c = G.doc.cmds.get('c_ln1');
    const was = c.track.hidden;
    c.track.hidden = true;
    const r = G.trackAt('c_ln1', 3.0);
    c.track.hidden = was;
    return r.matrix;
  })();
  return {
    duration: G.doc.timeline.duration,
    loop: G.doc.timeline.loop,
    rotBefore: +G.trackAt('c_ln1', 0.5).u.toFixed(3),    // 入点 1.5 之前
    rotMid: +G.trackAt('c_ln1', 3.0).u.toFixed(3),       // 1.5–4.5 的正中
    rotAfter: +G.trackAt('c_ln1', 5.5).u.toFixed(3),     // 出点 4.5 之后
    swingAt3: +G.trackAt('c_ln2', 3.0).u.toFixed(3),     // 另一条轨 3.5 才开始
    swingMid: +G.trackAt('c_ln2', 4.75).u.toFixed(3),    // 3.5–6 的正中
    hiddenMatrix: hiddenProbe,
  };
})()`);
check('全局时间轴被接收（6s · loop）',
  tlq.duration === 6 && tlq.loop === true, { duration: tlq.duration, loop: tlq.loop });
check('入点之前停在首帧', tlq.rotBefore === 0, tlq.rotBefore);
check('区间正中走到一半', Math.abs(tlq.rotMid - 0.5) < 0.03, tlq.rotMid);
check('出点之后停在末帧', tlq.rotAfter === 1, tlq.rotAfter);
check('同一时刻另一条轨还没开始（多轨错开）',
  tlq.swingAt3 === 0 && Math.abs(tlq.swingMid - 0.5) < 0.03,
  { at3s: tlq.swingAt3, at4_75s: tlq.swingMid });
check('静默轨道不驱动（矩阵返回 null）', tlq.hiddenMatrix === null, tlq.hiddenMatrix);

const paused = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.clock.pause();
  const t0 = G.clock.time;
  const a0 = G.entities.snapshot('e_slow').ages[0];
  await new Promise(r => setTimeout(r, 600));
  return {
    dt: Math.abs(G.clock.time - t0),
    dAge: Math.abs(G.entities.snapshot('e_slow').ages[0] - a0),
  };
})()`);
check('暂停后时钟与对象同时停住', paused.dt < 1e-6 && paused.dAge < 1e-6, paused);

const resumed = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.clock.play();
  const t0 = G.clock.time;
  await new Promise(r => setTimeout(r, 400));
  return { dt: +(G.clock.time - t0).toFixed(2) };
})()`);
check('恢复播放后时钟继续走', resumed.dt > 0.2, resumed);

const f4 = await shot('living');
console.log('  screenshot:', f4);

/* 9. 时间轨（v0.16）：帧序列由 GH 算一次，浏览器按 Duration/Loop/Ease 播 */
const tl1 = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('living');
  // 等新场景的快照真的到达（rev 归到 1），否则会读到上一轮的残留值
  const t0 = performance.now();
  while (G.doc.rev !== 1 && performance.now() - t0 < 4000) {
    await new Promise(r => setTimeout(r, 50));
  }
  // 把自变量按住：时钟归零 + 留两帧，再采样（不要拿墙钟当自变量）
  G.clock.reset();
  await new Promise(r => requestAnimationFrame(r));
  await new Promise(r => requestAnimationFrame(r));
  const g = G.doc.cmds.get('c_ln0');   // 纯生长轨
  const r = G.doc.cmds.get('c_ln1');   // 旋转轨
  return {
    rev: G.doc.rev,
    n: g.track ? g.track.n : null,
    hasReveal: !!(g.track && g.track.reveal),
    hasFrames: !!(g.track && g.track.frames),
    rotHasFrames: !!(r.track && r.track.frames),
    rotHasReveal: !!(r.track && r.track.reveal),
    easeLen: g.track && g.track.ease ? g.track.ease.y.length : 0,
    // 时间当自变量显式给：t=0 就应当精确是 0，不靠"reset 后马上读"这种跟帧赛跑的写法
    reveal0: G.trackAt('c_ln0', 0).reveal,
    rot0: G.trackAt('c_ln1', 0).matrix,
  };
})()`);
await sleep(900);
const tl2 = await cdp.eval(`(() => {
  const G = window.GHCE;
  return {
    rev: G.doc.rev,
    t: +G.clock.time.toFixed(2),
    reveal: +G.trackAt('c_ln0').reveal.toFixed(3),
    // 显式取区间中点：这条轨的入点是 1.5s，靠"时钟走到哪"采样会在区间外读到首帧
    rot: G.trackAt('c_ln1', 3.0).matrix,
  };
})()`);

check('时间轨已载入（25 帧）', tl1.n === LIVING_FRAMES, { n: tl1.n });
// c_ln0 纯生长（只有 reveal，没有矩阵帧）；c_ln1 才是旋转轨（有矩阵帧、没有 reveal）
check('纯生长轨只有 reveal 序列、没有矩阵帧',
  tl1.hasReveal === true && tl1.hasFrames === false, { hasReveal: tl1.hasReveal, hasFrames: tl1.hasFrames });
check('纯旋转轨有矩阵帧、没有 reveal 序列',
  tl1.rotHasFrames === true && tl1.rotHasReveal === false,
  { hasFrames: tl1.rotHasFrames, hasReveal: tl1.rotHasReveal });
check('缓动曲线被接收（33 点）', tl1.easeLen === 33, { len: tl1.easeLen });
check('时钟归零时 reveal 恰好是 0', tl1.reveal0 === 0, { reveal0: tl1.reveal0 });
check('无新 patch 时 reveal 仍在推进（时间轨在浏览器里跑）',
  tl2.rev === tl1.rev && tl2.reveal > tl1.reveal0 + 0.3,
  { rev: [tl1.rev, tl2.rev], reveal: [tl1.reveal0, tl2.reveal], t: tl2.t });
check('旋转轨的矩阵随时间变化', JSON.stringify(tl2.rot) !== JSON.stringify(tl1.rot0),
  { from: tl1.rot0 && tl1.rot0.map((x) => +x.toFixed(3)), to: tl2.rot && tl2.rot.map((x) => +x.toFixed(3)) });

/* 9.1 曲线生长 = 几何截断（不是虚线）：走真实 _paint，按时间量右边界 */
// 只在临时画布上画这一条指令、并临时摘掉粒子系统 —— 否则粒子和别的行会污染"最右墨迹"。
const grow = await cdp.eval(`(() => {
  const G = window.GHCE;
  const W = G.doc.canvas.w, H = G.doc.canvas.h;
  const c = document.createElement('canvas');
  c.width = W; c.height = H;
  const g = c.getContext('2d');
  const savedVis = [...G.doc.cmds.values()].map((x) => [x, x.visible]);
  for (const x of G.doc.cmds.values()) x.visible = (x.id === 'c_ln0');
  const savedEnt = G.renderer.entitySystem;
  G.renderer.setEntitySystem(null);
  const savedTime = G.doc.time;
  const rightEdge = (t) => {
    G.doc.time = t;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.clearRect(0, 0, W, H);
    G.renderer._paint(g);
    const d = g.getImageData(0, 0, W, H).data;
    let maxX = -1;
    for (let y = 0; y < H; y++) {
      for (let x = W - 1; x >= 0; x--) {
        if (d[(y * W + x) * 4 + 3] > 24) { if (x > maxX) maxX = x; break; }
      }
    }
    return maxX;
  };
  const out = {
    reveal: [0.55, 1.1, 2.4].map((t) => +G.trackAt('c_ln0', t).reveal.toFixed(2)),
    x: [rightEdge(0.55), rightEdge(1.1), rightEdge(2.4)],
  };
  G.doc.time = savedTime;
  G.renderer.setEntitySystem(savedEnt);
  for (const [x, v] of savedVis) x.visible = v;
  return out;
})()`);
check('生长按弧长截断（真实绘制里右边界随 reveal 单调推进）',
  grow.x[0] > 100 && grow.x[1] - grow.x[0] > 200 && grow.x[2] - grow.x[1] > 100, grow);

/* 10. 分布是确定性的（v0.15 去掉了 seed / vary / 行为预设） */
const dist = await cdp.eval(`(() => {
  const G = window.GHCE;
  const s = G.entities.snapshot('e_slow');
  const step = ((s.us[1] - s.us[0]) + 1) % 1;
  return { step: +step.toFixed(4), expect: +(1 / 30).toFixed(4) };
})()`);
check('同一蓝图内粒子沿曲线均匀铺开（步长 = 1/Count）',
  Math.abs(dist.step - dist.expect) < 2e-3, dist);

const phase = await cdp.eval(`(() => {
  const G = window.GHCE;
  const before = G.entities.snapshot('e_slow').us[0];
  G.entities.setPhase('e_slow', 0.5);
  const after = G.entities.snapshot('e_slow').us[0];
  return { before, after };
})()`);
// setPhase 直接把第 i 个粒子的 u 设成 (i/Count + phase)：index 0 就应当**正好**落在 phase 上
check('写 phase 让整组沿曲线前移（时间就是这么进来的）',
  Math.abs(phase.after - 0.5) < 1e-6, phase);

/* 10.1 指针仍然进运行时（交互没坏） */
const geo = await cdp.eval(`(() => {
  const r = window.GHCE.renderer.canvas.getBoundingClientRect();
  return { left: Math.round(r.left), top: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
})()`);
const targetX = geo.left + geo.w * 0.75;
const targetY = geo.top + geo.h * 0.25;

for (let i = 0; i < 4; i++) {
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: targetX, y: targetY, button: 'none' });
  await sleep(120);
}
await sleep(300);

const ptr = await cdp.eval(`(() => window.GHCE.pointer.stats())()`);
check('鼠标位置进入运行时（页面坐标）', ptr.inside === true, ptr);

const f5 = await shot('living-pointer');
console.log('  screenshot:', f5);

/* 10.4 按区域擦除（v0.11 的 clear-region，从已废弃的 verify-v011 套件搬过来） */
const clearReg = await cdp.eval(`(() => {
  const G = window.GHCE;
  const cv = G.renderer.canvas, ctx = cv.getContext('2d');
  const dpr = cv.width / G.doc.canvas.w;
  G.renderer.draw();
  G.renderer.clearRegion(0, 0, 200, 200);      // 页面坐标的左上角 200×200
  const inside = [...ctx.getImageData(Math.round(20 * dpr), Math.round(20 * dpr), 1, 1).data];
  const outside = [...ctx.getImageData(Math.round(700 * dpr), Math.round(500 * dpr), 1, 1).data];
  G.renderer.draw();                            // 还原，后面的导出还用这张画布
  return { inside, outside };
})()`);
check('clear-region 只擦指定区域（框内透明、框外保留底色）',
  clearReg.inside[3] === 0 && clearReg.outside[3] > 0, clearReg);

/* 10.5 位移轨 + 状态回传 */
// 用"时间当自变量"扫一个完整周期，而不是隔 700ms 采两次 —— 后者会撞上正弦谷底，
// 数值几乎不动，把正确的实现判成 FAIL（v0.16 踩过）。
const psr = await cdp.eval(`(() => {
  const G = window.GHCE;
  const tr = G.doc.cmds.get('c_ln2').track;
  // v0.18：一个周期 = 这条轨自己的 [start, end]，不再是轨道自带的 duration
  const s = tr.start, e = tr.end;
  const fs = [];
  for (let k = 0; k <= 8; k++) fs.push(+G.trackAt('c_ln2', s + ((e - s) * k) / 8).matrix[5].toFixed(2));
  return {
    span: +(e - s).toFixed(2),
    fs,
    max: Math.max(...fs),
    min: Math.min(...fs),
    statSent: G.client.metrics.statSent,
  };
})()`);
check('时间轨能驱动位移（一个周期走完 0 → +48 → 0 → −48）',
  Math.abs(psr.max - 48) < 0.5 && Math.abs(psr.min + 48) < 0.5,
  { span: psr.span, max: psr.max, min: psr.min, fs: psr.fs });
check('运行时状态已回传给 GH（stat）', psr.statSent > 3, { statSent: psr.statSent });

/* 10.6 变换锚点：画布真的绕"指定点"转，而不是绕原点 */
// 世界里的 M 要落到页面上，必须是 P·M·P⁻¹。这条用像素证明，不看矩阵数字。
const piv = await cdp.eval(`(() => {
  const c = document.createElement('canvas');
  c.width = 400; c.height = 400;
  const g = c.getContext('2d');
  g.setTransform(0, 1, -1, 0, 400, 0);     // 绕 (200,200) 视觉逆时针 90°：x'=400-y, y'=x
  g.fillStyle = '#fff';
  g.fillRect(200, 190, 100, 20);           // 水平长条，中心 (250,200)
  const ink = (x, y) => g.getImageData(x, y, 1, 1).data[3] > 32;
  const t = g.getTransform();
  return {
    order: [t.a, t.b, t.c, t.d, t.e, t.f],
    vertical: ink(200, 250),                 // 转后应变成竖条
    horizontal: ink(250, 200),               // 原水平位置应已空出
  };
})()`);
check('Canvas 矩阵字段序 = [a,b,c,d,e,f]',
  JSON.stringify(piv.order) === JSON.stringify([0, 1, -1, 0, 400, 0]), piv.order);
check('矩阵绕指定锚点旋转（竖条出现 / 横条消失）',
  piv.vertical === true && piv.horizontal === false,
  { vertical: piv.vertical, horizontal: piv.horizontal });

/* 10.7 v0.17 交付：PNG 序列 / 独立 HTML / 视频 */
const seq = await cdp.eval(`(async () => {
  const G = window.GHCE;
  const r = await G.exportPngSequence({
    doc: G.doc, renderer: G.renderer, entities: G.entities, fps: 6, duration: 0.5,
  });
  const head = new Uint8Array(await r.blob.slice(0, 4).arrayBuffer());
  return {
    frames: r.frames,
    size: r.blob.size,
    sig: [...head].map((b) => b.toString(16).padStart(2, '0')).join(''),
  };
})()`);
check('PNG 序列按 duration×fps 出帧并打成合法 zip',
  seq.frames === 3 && seq.sig === '504b0304' && seq.size > 3000, seq);

// 独立 HTML 不能只看它"有内容"——真的把它当页面加载，看里面那张画布画不画得出来
const solo = await cdp.eval(`(async () => {
  const G = window.GHCE;
  const blob = await G.exportStandalone({ doc: G.doc, entities: G.entities, title: 'standalone' });
  const text = await blob.text();
  const url = URL.createObjectURL(blob);
  const f = document.createElement('iframe');
  f.style.cssText = 'position:fixed;left:-9999px;top:0;width:900px;height:700px';
  f.src = url;
  document.body.appendChild(f);
  await new Promise((r) => setTimeout(r, 2000));
  const cv = f.contentDocument && f.contentDocument.querySelector('canvas');
  let ink = 0, err = null;
  try {
    if (cv) {
      const px = cv.getContext('2d').getImageData(0, 0, cv.width, cv.height).data;
      const bg = [px[0], px[1], px[2]];
      for (let i = 0; i < px.length; i += 4 * 97) {
        if (Math.abs(px[i] - bg[0]) + Math.abs(px[i + 1] - bg[1]) + Math.abs(px[i + 2] - bg[2]) > 24) ink++;
      }
    }
  } catch (e) { err = String(e); }
  const out = {
    size: blob.size,
    hasModule: text.indexOf('type="module"') >= 0,
    hasDataModules: text.indexOf('data:text/javascript;base64,') >= 0,
    hasPatch: text.indexOf('"ops"') >= 0,
    canvas: !!cv, ink, err,
    bootError: (f.contentWindow && f.contentWindow.__bootError) || null,
    docStats: (f.contentWindow && f.contentWindow.__GHCE)
      ? { geoms: f.contentWindow.__GHCE.doc.geoms.size, cmds: f.contentWindow.__GHCE.doc.cmds.size }
      : null,
    cv: cv ? [cv.width, cv.height] : null,
  };
  f.remove();
  URL.revokeObjectURL(url);
  return out;
})()`);
check('独立 HTML 能离线自己跑起来（iframe 里有画布并画出内容）',
  solo.canvas && solo.ink > 20 && solo.hasPatch && solo.hasDataModules, solo);

const vid = await cdp.eval(`(async () => {
  const G = window.GHCE;
  const mime = G.pickVideoMime();
  if (!mime) return { mime: null, size: 0, type: '' };
  const r = await G.exportVideo({
    doc: G.doc, renderer: G.renderer, clock: G.clock, entities: G.entities, fps: 24, duration: 0.8,
  });
  return { mime, ext: r.ext, size: r.blob.size, type: r.blob.type };
})()`);
check('视频导出产出可用的 WebM/MP4',
  !!vid.mime && vid.size > 1000 && /webm|mp4/.test(vid.type || ''), vid);

/* 12. v0.19 几何 morph：顶点插值（方 → 圆，非仿射变化） */
const mo = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('morph');
  // 判据不能用 rev===1：mock 每次切场景都从 1 重新计数，连续切换时条件立刻成立，等于没等。
  // 要等"只有新场景才会有的内容"出现。
  const t0 = performance.now();
  while (!G.doc.cmds.has('c_morph') && performance.now() - t0 < 4000) {
    await new Promise(r => setTimeout(r, 50));
  }
  G.clock.reset();

  const c = G.doc.cmds.get('c_morph');
  if (!c) return { noCmd: true, ids: [...G.doc.cmds.keys()].slice(0, 6), rev: G.doc.rev };
  const m = c.morph;
  if (!m) return { noMorph: true, keys: Object.keys(c), trackKeys: c.track ? Object.keys(c.track) : null };

  const cx = G.doc.canvas.w / 2, cy = G.doc.canvas.h / 2;
  const rad = (pts, i) => Math.hypot(pts[i * 2] - cx, pts[i * 2 + 1] - cy);

  const p0 = G.resolveMorph(m, 0);
  const pm = G.resolveMorph(m, 0.5);
  const p1 = G.resolveMorph(m, 1);

  return {
    n: m.n, count: m.count,
    // 45° 方向：方形半径 = √2·R，圆半径 = R。插值中点应当落在两者之间
    r0: +rad(p0, 12).toFixed(1),
    rm: +rad(pm, 12).toFixed(1),
    r1: +rad(p1, 12).toFixed(1),
    len: [p0.length, pm.length, p1.length],
    closed: m.closed,
  };
})()`);
check('morph 被接收（24 帧 × 96 点）', mo.n === 24 && mo.count === 96, mo);
check('三帧点数一致（可一一对应）',
  Array.isArray(mo.len) && mo.len.every((v) => v === mo.count * 2), mo.len);
check('首帧是方形（45° 半径明显大于圆）', mo.r0 > mo.r1 * 1.3, { square: mo.r0, circle: mo.r1 });
check('中点落在两帧之间（真的在插值，不是切帧）',
  mo.rm > mo.r1 + 1 && mo.rm < mo.r0 - 1, { r0: mo.r0, mid: mo.rm, r1: mo.r1 });

const f6 = await shot('morph');
console.log('  screenshot:', f6);

/* 13. v0.23 逐帧驱动：GH 每帧发新矩阵时的直通行为 */
const lv = await cdp.eval(`(async () => {
  const G = window.GHCE;
  G.selectScene('random');
  const t0 = performance.now();
  while (!G.doc.cmds.has('c_tri') && performance.now() - t0 < 4000) {
    await new Promise(r => setTimeout(r, 50));
  }
  const a = G.doc.cmds.get('c_tri').transform.matrix.slice();
  await new Promise(r => setTimeout(r, 450));
  const c = G.doc.cmds.get('c_tri');
  return {
    changed: JSON.stringify(a) !== JSON.stringify(c.transform.matrix),
    progress: c.transform.progress,
    rev: G.doc.rev,
    frames: G.client.metrics.patches,
  };
})()`);
check('逐帧矩阵更新（模拟 GH 每帧求解）', lv.changed === true, { changed: lv.changed, rev: lv.rev });
check('逐帧驱动时直通，不叠过渡缓动', lv.progress === 1, { progress: lv.progress });

// 信号：GH 推定义 → 浏览器求值 → stat 回传。这里验证前半段（定义到达 + 求值随时间变）
const sig = await cdp.eval(`(() => {
  const G = window.GHCE;
  const def = G.signals.get('sig_demo');
  if (!def) return { missing: true, ids: [...G.signals.keys()] };
  return {
    kind: def.kind,
    v0: +G.signalValue(def, 0).toFixed(4),
    v2: +G.signalValue(def, 2).toFixed(4),
    v5: +G.signalValue(def, 5).toFixed(4),
    stable: G.signalValue(def, 2) === G.signalValue(def, 2),
  };
})()`);
check('信号定义由 GH 推送并到达运行时', sig.missing !== true, sig);
check('信号值随时间变化', sig.v0 !== sig.v2 && sig.v2 !== sig.v5,
  { t0: sig.v0, t2: sig.v2, t5: sig.v5 });
check('同一时刻求值稳定（可回传、可复现）', sig.stable === true, sig.stable);

const f7 = await shot('random');
console.log('  screenshot:', f7);

/* 10.5 表达式绑定 + 事件写变量（v0.30）
 *
 * 这一节要证明的是"不重解 GH 也能变画面"：
 *   点击 → 事件写变量 → 表达式重算属性 → 重绘
 * 全程 doc.rev 不变 —— 一旦 rev 变了，就说明还是 GH 在出图，这条路没成立。 */
await cdp.eval("window.GHCE.selectScene('binding')");
await sleep(700);

const b0 = await cdp.eval(`(() => {
  const G = window.GHCE;
  const cmd = G.doc.cmds.get('c_beat');
  const ctx = { t: G.doc.time || 0, vars: G.doc.vars };
  return {
    hasBind: !!(cmd && cmd.bind && cmd.bind.width),
    v: G.doc.vars.get('v_w'),
    w: G.evalExpr(cmd.bind.width, ctx),
    rev: G.doc.rev,
    timeBinding: G.doc.timeBinding,
    breath0: G.evalExpr(G.doc.cmds.get('c_breath').bind.width, { t: 0, vars: G.doc.vars }),
    breath1: G.evalExpr(G.doc.cmds.get('c_breath').bind.width, { t: 0.4, vars: G.doc.vars }),
  };
})()`);
check('绑定随 patch 到达运行时', b0.hasBind === true, b0.hasBind);
check('变量初值来自定义', b0.v === 0, b0.v);
check('变量参与表达式求值', Math.abs(b0.w - 1.5) < 1e-6, b0.w);
check('时间绑定：同一表达式在不同 t 给出不同值',
  Math.abs(b0.breath0 - b0.breath1) > 0.3, { t0: +b0.breath0.toFixed(3), t04: +b0.breath1.toFixed(3) });
check('存在时间绑定 → 标记为逐帧重绘', b0.timeBinding === true, b0.timeBinding);

/** 数"近白"像素：c_beat 是唯一近白的线（另两条是绿/粉），所以它的粗细直接反映在数量上。
 *  不按坐标取样 —— 画布是等比缩放显示的，页面坐标到缓冲像素的映射不值得再赌一次。 */
const inkCount = () => cdp.eval(`(() => {
  const cv = window.GHCE.renderer.canvas;
  const g = cv.getContext('2d');
  const d = g.getImageData(0, 0, cv.width, cv.height).data;
  let n = 0;
  for (let i = 0; i < d.length; i += 4) {
    if (d[i + 3] > 20 && d[i] > 200 && d[i + 1] > 200 && d[i + 2] > 200) n++;
  }
  return n;
})()`);

const ink0 = await inkCount();
const rev0 = b0.rev;

await cdp.eval(`(() => {
  const cv = window.GHCE.renderer.canvas;
  cv.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: 10, clientY: 10 }));
})()`);
await sleep(260);

const b1 = await cdp.eval(`(() => {
  const G = window.GHCE;
  const cmd = G.doc.cmds.get('c_beat');
  return {
    v: G.doc.vars.get('v_w'),
    w: G.evalExpr(cmd.bind.width, { t: G.doc.time || 0, vars: G.doc.vars }),
    rev: G.doc.rev,
    stored: cmd.bind.width ? 'expr' : 'none',
  };
})()`);
const ink1 = await inkCount();

check('点击写入变量', b1.v === 1, b1.v);
check('表达式随之重算出新宽度', Math.abs(b1.w - 7.5) < 1e-6, b1.w);
check('全程没有触发 GH 求解（rev 不变）', b1.rev === rev0, { before: rev0, after: b1.rev });
check('像素证据：线真的变粗了', ink1 > ink0 * 1.4, { before: ink0, after: ink1 });
check('绑定不污染指令本体（cmd.style.width 仍是静态值）',
  await cdp.eval("window.GHCE.doc.cmds.get('c_beat').style.stroke.width") === 1.5,
  await cdp.eval("window.GHCE.doc.cmds.get('c_beat').style.stroke.width"));

/* 10.6 色相 / 缩放 / 指针事件（v0.30.1） */
const hueFx = await cdp.eval(`(() => {
  const a = window.GHCE.recolor('#EDEBE4', 0.5);
  const b = window.GHCE.recolor('#EDEBE4', 0.0);
  return { a, b, differs: a !== b, hue180: a.indexOf('180') >= 0 };
})()`);
check('色相函数：只换色相、保留明度', hueFx.differs && hueFx.hue180, hueFx);

const ringB = await cdp.eval(`(() => {
  const G = window.GHCE;
  const c = G.doc.cmds.get('c_ring');
  const at = (t, k) => +G.evalExpr(c.bind[k], { t, vars: G.doc.vars }).toFixed(3);
  return { s0: at(0, 'scale'), s1: at(1, 'scale'), h0: at(0, 'hue'), h1: at(1, 'hue') };
})()`);
check('缩放绑定随时间呼吸', Math.abs(ringB.s0 - ringB.s1) > 0.1, ringB);
check('色相绑定随时间循环', Math.abs(ringB.h0 - ringB.h1) > 0.1, ringB);

/** 数绿方块（#10B363）的像素：按下放大最直接的证据 */
const greenCount = () => cdp.eval(`(() => {
  const cv = window.GHCE.renderer.canvas;
  const d = cv.getContext('2d').getImageData(0, 0, cv.width, cv.height).data;
  let n = 0;
  for (let i = 0; i < d.length; i += 4) {
    if (d[i + 3] > 20 && d[i + 1] > 120 && d[i + 1] > d[i] * 1.6 && d[i + 1] > d[i + 2] * 1.3) n++;
  }
  return n;
})()`);

const revHold = await cdp.eval('window.GHCE.doc.rev');
const g0 = await greenCount();
await cdp.eval(`window.GHCE.renderer.canvas.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))`);
await sleep(240);
const holdDown = await cdp.eval(`(() => {
  const G = window.GHCE;
  const c = G.doc.cmds.get('c_hold');
  return { v: G.doc.vars.get('v_hold'), scale: G.evalExpr(c.bind.scale, { t: 0, vars: G.doc.vars }), rev: G.doc.rev };
})()`);
const g1 = await greenCount();
check('pointerdown 写变量', holdDown.v === 1, holdDown.v);
check('pointerdown 后缩放表达式为 4', Math.abs(holdDown.scale - 4) < 1e-6, holdDown.scale);
check('指针事件不触发 GH 求解（rev 不变）', holdDown.rev === revHold, { before: revHold, after: holdDown.rev });
check('像素证据：方块被放大', g1 > g0 * 1.6, { before: g0, after: g1 });

await cdp.eval(`window.GHCE.renderer.canvas.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }))`);
await sleep(240);
const holdUp = await cdp.eval(`(() => {
  const G = window.GHCE;
  const c = G.doc.cmds.get('c_hold');
  return { v: G.doc.vars.get('v_hold'), scale: G.evalExpr(c.bind.scale, { t: 0, vars: G.doc.vars }) };
})()`);
check('pointerup 复位变量', holdUp.v === 0 && Math.abs(holdUp.scale - 1) < 1e-6, holdUp);

const f85 = await shot('binding');
console.log('  screenshot:', f85);

/* 10.7 Mark：同一曲线的五种静态画法（v0.31）
 *
 * 这一节要证明三件事：
 *   1. 500+ 条标记只引用 3 份印章几何 —— patch 体积靠这个守住；
 *   2. 位置与朝向全在 matrix 里，没有把印章坐标烘进几何；
 *   3. 五种画法真的画出五种墨量，而不是"参数不同、画面一样"。 */
await cdp.eval("window.GHCE.selectScene('marks')");
await sleep(900);

const mk = await cdp.eval(`(() => {
  const G = window.GHCE;
  const cmds = [...G.doc.cmds.values()];
  const isMark = (c) => /^c_(stipple|nib|hatch|scatter)_/.test(c.id);
  const marks = cmds.filter(isMark);
  const stipple = marks.filter((c) => c.id.startsWith('c_stipple_')).sort((a, b) => a.order - b.order);
  const scaleOf = (c) => Math.hypot(c.transform.matrix[0], c.transform.matrix[1]);
  const xs = stipple.map((c) => c.transform.matrix[4]);
  const ys = stipple.map((c) => c.transform.matrix[5]);
  return {
    cmds: cmds.length,
    marks: marks.length,
    geoms: G.doc.stats().geoms,
    brushes: [...G.doc.geoms.keys()].filter((k) => k.indexOf('g_brush') === 0).length,
    stippleN: stipple.length,
    firstScale: stipple.length ? +scaleOf(stipple[0]).toFixed(3) : 0,
    lastScale: stipple.length ? +scaleOf(stipple[stipple.length - 1]).toFixed(3) : 0,
    firstX: stipple.length ? +xs[0].toFixed(1) : 0,
    lastX: stipple.length ? +xs[xs.length - 1].toFixed(1) : 0,
    ySpread: stipple.length ? +(Math.max(...ys) - Math.min(...ys)).toFixed(1) : 0,
    bytes: G.client.metrics.lastBytes,
  };
})()`);

check('300+ 条标记只引用 3 份印章几何',
  mk.brushes === 3 && mk.marks > 300, { brushes: mk.brushes, marks: mk.marks, cmds: mk.cmds, geoms: mk.geoms });
check('标记矩阵沿路径分布（水平 900+，纵向随波形 60+）',
  mk.lastX - mk.firstX > 900 && mk.ySpread > 60, { firstX: mk.firstX, lastX: mk.lastX, ySpread: mk.ySpread });
check('Style Map：点描沿路径由小到大',
  mk.lastScale > mk.firstScale * 3, { first: mk.firstScale, last: mk.lastScale });
check('整帧体积守住 400KB', mk.bytes > 0 && mk.bytes < 400000, mk.bytes);

/** 五条带各数一次墨量：不是"有没有画"，是"五种画法真的不一样" */
const bands = await cdp.eval(`(() => {
  const G = window.GHCE, cv = G.renderer.canvas, g = cv.getContext('2d');
  const k = cv.width / G.doc.canvas.w;
  const out = [];
  for (const cy of [130, 300, 470, 640, 810]) {
    const y0 = Math.max(0, Math.round((cy - 85) * k));
    const h = Math.min(cv.height - y0, Math.round(170 * k));
    const d = g.getImageData(0, y0, cv.width, h).data;
    let n = 0;
    for (let i = 0; i < d.length; i += 12) {
      const lum = (d[i] * 299 + d[i + 1] * 587 + d[i + 2] * 114) / 1000;
      if (d[i + 3] > 20 && lum > 55) n++;
    }
    out.push(n);
  }
  return out;
})()`);
check('五条带都有墨（五种画法都画出来了）', bands.every((n) => n > 150), bands);
check('五种画法的墨量至少三档（不是同一张图换个参数）',
  new Set(bands.map((n) => Math.round(n / 150))).size >= 3, bands);

// 回归：从 fade 场景切到普通场景，clearMode 不能残留。
// 这条是 v0.31 做五种画法时抓到的真 bug —— doc.reset 早先用 Object.assign，
// 没带的字段会留着上一件作品的设置，而 GH 的 Canvas 电池根本不发 clearMode。
await cdp.eval("window.GHCE.selectScene('living')");
await sleep(500);
await cdp.eval("window.GHCE.selectScene('marks')");
await sleep(600);
const clearMode = await cdp.eval("window.GHCE.doc.canvas.clearMode");
check('换作品时画布设置回到默认（fade 不会残留）', clearMode === 'clear', clearMode);

const f86 = await shot('marks');
console.log('  screenshot:', f86);

/* 10.8 Behavior Graph：状态读写 + 条件 + 生成/销毁（v0.32）
 *
 * 这一节要证明的不是"粒子动了"，而是：
 *   1. 程序到达运行时，且**没有**任何行为预设参与；
 *   2. 固定步长下人口按代数增长、到代数上限停住；
 *   3. 销毁按条件发生，残影（留痕）被保留且有上限；
 *   4. 同样的步数重跑，位置逐点相同（确定性）；
 *   5. 全程没有触发 GH 求解（rev 不变）。 */
await cdp.eval("window.GHCE.selectScene('behavior')");
await sleep(800);

const sim = await cdp.eval(`(() => {
  const G = window.GHCE, E = G.entities;
  const id = 'e_swarm';
  const snap = () => {
    const s = E.snapshot(id);
    return { n: s.count, pts: s.pts, gens: s.gens, ghosts: s.ghosts };
  };
  const bp = E.blueprints.get(id);
  const prog = bp && bp.def.program;
  const meta = {
    hasProgram: !!prog,
    steps: prog ? prog.steps.length : 0,
    spawn: !!(prog && prog.spawn),
    destroy: !!(prog && prog.destroy),
    ownsXY: !!(prog && prog.ownsXY),
    ownsU: !!(prog && prog.ownsU),
  };

  const rev0 = G.doc.rev;
  E.restart();
  E.step(G.doc, 90);
  const a = snap();
  E.step(G.doc, 90);
  const b = snap();
  E.step(G.doc, 90);
  const c = snap();
  E.step(G.doc, 90);
  const d = snap();
  E.step(G.doc, 60);
  const e = snap();

  E.restart();
  E.step(G.doc, 90);
  const again = snap();

  return { meta, a, b, c, d, e, again, rev0, rev1: G.doc.rev };
})()`);

check('状态程序到达运行时（5 条写入 + 生成 + 销毁）',
  sim.meta.hasProgram && sim.meta.steps === 5 && sim.meta.spawn && sim.meta.destroy, sim.meta);
check('写了 x/y 就接管运动（不再沿曲线跑，也不自增 u）',
  sim.meta.ownsXY === true && sim.meta.ownsU === false, sim.meta);

check('1.5s：24 个初代各分裂一次 → 72',
  sim.a.n === 72, { n: sim.a.n });
check('3.0s：子代再分裂一代 → 168（代数上限 2 挡住第三代）',
  sim.b.n === 168, { n: sim.b.n });
check('4.5s：还没到销毁年龄 → 168',
  sim.c.n === 168, { n: sim.c.n });
check('6.0s：子代到龄销毁 → 120，残影 48',
  sim.d.n === 120 && sim.d.ghosts === 48, { n: sim.d.n, ghosts: sim.d.ghosts });
check('7.0s：孙代也销毁 → 只剩 24 个初代，残影累计 144',
  sim.e.n === 24 && sim.e.ghosts === 144, { n: sim.e.n, ghosts: sim.e.ghosts });
check('代数不超过上限（没有无限分裂）',
  Math.max(...sim.e.gens) <= 2, sim.e.gens);

check('固定步长可复现：重跑同样的步数，位置逐点相同',
  JSON.stringify(sim.a.pts) === JSON.stringify(sim.again.pts),
  { first: sim.a.pts[0], again: sim.again.pts[0] });

check('整个模拟过程没有触发 GH 求解（rev 不变）',
  sim.rev0 === sim.rev1, { before: sim.rev0, after: sim.rev1 });

// 截图前跑 4 秒（轨道已经建立）并显式画一帧：
// 前面的模拟是同步跑的、不经过 RAF，不主动画就可能截到上一帧。
await cdp.eval(`(() => {
  const G = window.GHCE, E = G.entities;
  E.restart();
  E.step(G.doc, 240);
  G.renderer.draw();
  return true;
})()`);
await sleep(150);
const f87 = await shot('behavior');
console.log('  screenshot:', f87);

/* 10.9 图片 + 裁剪（v0.33）
 *
 * 三件事：
 *   1. 图片几何真的到了运行时，并且**三条指令共用一份 base64**；
 *   2. 解码完成后画得出来（这是异步的，所以第一帧空白不是 bug）；
 *   3. 裁剪在指令变换之前生效：圆内是图、圆外是底，反相则相反。 */
await cdp.eval("window.GHCE.selectScene('image')");
await sleep(1500);   // 等解码：Image.onload 之后会自己 requestDraw

const imgState = await cdp.eval(`(() => {
  const G = window.GHCE;
  const g = G.doc.geoms.get('g_checker');
  const cmds = [...G.doc.cmds.values()].filter((c) => c.geom === 'g_checker');
  return {
    kind: g && g.kind,
    srcHead: g ? g.src.slice(0, 22) : null,
    bytes: g ? g.src.length : 0,
    nat: g ? [g.w, g.h] : null,
    cmds: cmds.length,
    clips: cmds.map((c) => (c.clip ? c.clip.geom + (c.clip.invert ? '|inv' : '') : 'none')),
    imageGeoms: [...G.doc.geoms.values()].filter((x) => x.kind === 'image').length,
    lastBytes: G.client.metrics.lastBytes,
  };
})()`);

check('图片几何到达运行时（data URL + 原始尺寸）',
  imgState.kind === 'image' && imgState.srcHead.startsWith('data:image/png') && imgState.nat[0] === 64,
  { kind: imgState.kind, head: imgState.srcHead, nat: imgState.nat });
check('三条指令共用一份图片几何（base64 不重复进 patch）',
  imgState.cmds === 3 && imgState.imageGeoms === 1, { cmds: imgState.cmds, geoms: imgState.imageGeoms });
check('裁剪随指令到达，含反相标记',
  imgState.clips.includes('g_clip_circle') && imgState.clips.includes('g_clip_hole|inv'),
  imgState.clips);

/** 取页面坐标上的一点，返回像素亮度与最大通道 */
const pixelAt = (x, y) => cdp.eval(`(() => {
  const G = window.GHCE, cv = G.renderer.canvas;
  const k = cv.width / G.doc.canvas.w;
  const d = cv.getContext('2d').getImageData(Math.round(${x} * k), Math.round(${y} * k), 1, 1).data;
  return { r: d[0], g: d[1], b: d[2], a: d[3] };
})()`);

const warm = (p) => p.r > 100 && p.r > p.b + 30;   // 棋盘暖色 (240,120,40)
const dark = (p) => Math.max(p.r, p.g, p.b) < 60;  // 底色 #0b0b10

const plain = await pixelAt(300, 300);
check('图片解码后真的画出来了（不是一张空图）', warm(plain), plain);

const inCircle = await pixelAt(930, 270);
const outCircle = await pixelAt(830, 170);
check('圆内裁剪：模具内部保留', warm(inCircle), inCircle);
check('圆内裁剪：模具外部被切掉', dark(outCircle), outCircle);

// 反相裁剪的取样点必须落在棋格中心。64px 的棋盘铺到 260px 上，8px 格变成 32.5px，
// 原先取的 (150,560) 落在图素 (7.4,7.4) —— 离格边只有 0.6 个图素，双线性插值把
// 邻格深色 (40,40,40) 按 85% 混进来，240,120,40 被稀释成 69,46,40，看着像裁剪漏了。
// (234,773) = 图素 (28.1,59.8)，格心，距圆心 114 > r=90；(136,579) 是另一侧的格心。
const inHole = await pixelAt(250, 660);
const outHoleA = await pixelAt(234, 773);
const outHoleB = await pixelAt(136, 579);
const outHole = outHoleA.r >= outHoleB.r ? outHoleA : outHoleB;
check('反相裁剪：模具内部被挖空', dark(inHole), inHole);
check('反相裁剪：模具外部保留', warm(outHole), { a: outHoleA, b: outHoleB });

// SVG 导出：图片走 <image>，裁剪走 <clipPath>
const svgOut = await cdp.eval(`(() => {
  const r = window.GHCE.toSVG(window.GHCE.doc);
  return { ok: r.svg.indexOf('<image') >= 0, clip: r.svg.indexOf('<clipPath') >= 0,
           warned: r.report.warnings.length, len: r.svg.length };
})()`);
check('SVG 导出：图片内嵌、裁剪转 clipPath',
  svgOut.ok && svgOut.clip, svgOut);

// 独立 HTML 导出：图片必须跟着 doc 一起走，否则导出的作品是空白的
const docOps = await cdp.eval(`(() => {
  const snap = window.GHCE.serializeDoc(window.GHCE.doc, window.GHCE.entities);
  const ops = snap.patch.ops;
  const img = ops.find((o) => o.op === 'geom.upsert' && o.value && o.value.kind === 'image');
  const cmd = ops.find((o) => o.op === 'cmd.upsert' && o.value && o.value.clip);
  return { image: !!img, src: img ? img.value.src.slice(0, 22) : null, clip: !!cmd };
})()`);
check('独立 HTML 导出带上图片与裁剪', docOps.image && docOps.clip, docOps);

const f88 = await shot('image');
console.log('  screenshot:', f88);

/* 11. 四档视口：量横向溢出与是否被裁 */
const VIEWPORTS = [
  [390, 844], [700, 700], [800, 600], [1400, 900],
];
const layoutIssues = [];
for (const [w, h] of VIEWPORTS) {
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: false });
  await sleep(320);
  const L = await cdp.eval(`(() => {
    const c = document.querySelector('.art').getBoundingClientRect();
    const dev = document.getElementById('dev');
    return {
      inner: [innerWidth, innerHeight],
      scroll: [document.documentElement.scrollWidth, document.documentElement.scrollHeight],
      art: [Math.round(c.x), Math.round(c.y), Math.round(c.width), Math.round(c.height)],
      devVisible: !dev.hidden,
      stagePad: parseFloat(getComputedStyle(document.getElementById('stage')).paddingLeft) || 0
    };
  })()`);
  const overflowX = L.scroll[0] - L.inner[0];
  const clipped = L.art[0] < -1 || L.art[1] < -1
    || L.art[0] + L.art[2] > L.inner[0] + 1 || L.art[1] + L.art[3] > L.inner[1] + 1;
  if (overflowX > 0 || clipped || L.devVisible || L.stagePad > 0) {
    layoutIssues.push({ vp: `${w}x${h}`, overflowX, clipped, devVisible: L.devVisible, stagePad: L.stagePad, art: L.art });
  }
}
check('四档视口无横向溢出 / 无裁切 / 无面板遮挡', layoutIssues.length === 0, layoutIssues);

await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 2, mobile: false });
await sleep(300);

/* ---------- 收尾 ---------- */
ws.close();
chrome.kill();
const failed = results.filter((r) => !r.pass);
console.log(`\n${results.length - failed.length}/${results.length} passed`);
process.exit(failed.length ? 1 : 0);
