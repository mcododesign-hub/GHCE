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
           scenesVisible: !!document.getElementById('scenes').offsetParent };
})()`);
check('WS 已连接', s1.conn === 'open', s1.conn);
check('模拟桥下显示演示场景按钮', s1.scenesVisible === true, s1.scenesVisible);
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
const errs = cdp.events.filter((e) =>
  (e.method === 'Runtime.consoleAPICalled' && e.params.type === 'error') ||
  e.method === 'Runtime.exceptionThrown' ||
  (e.method === 'Log.entryAdded' && e.params.entry.level === 'error')
);
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
    rot: G.trackAt('c_ln1').matrix,
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
  const fs = [];
  for (let k = 0; k <= 8; k++) fs.push(+G.trackAt('c_ln2', (tr.duration * k) / 8).matrix[5].toFixed(2));
  return {
    duration: tr.duration,
    fs,
    max: Math.max(...fs),
    min: Math.min(...fs),
    statSent: G.client.metrics.statSent,
  };
})()`);
check('时间轨能驱动位移（一个周期走完 0 → +48 → 0 → −48）',
  Math.abs(psr.max - 48) < 0.5 && Math.abs(psr.min + 48) < 0.5,
  { duration: psr.duration, max: psr.max, min: psr.min, fs: psr.fs });
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
    const hud = document.getElementById('hud').getBoundingClientRect();
    return {
      inner: [innerWidth, innerHeight],
      scroll: [document.documentElement.scrollWidth, document.documentElement.scrollHeight],
      art: [Math.round(c.x), Math.round(c.y), Math.round(c.width), Math.round(c.height)],
      hudRight: Math.round(hud.right),
      overlap: innerWidth >= 900 ? Math.max(0, hud.right - c.x) : 0
    };
  })()`);
  const overflowX = L.scroll[0] - L.inner[0];
  const clipped = L.art[0] < -1 || L.art[1] < -1
    || L.art[0] + L.art[2] > L.inner[0] + 1 || L.art[1] + L.art[3] > L.inner[1] + 1;
  if (overflowX > 0 || clipped || L.overlap > 0) {
    layoutIssues.push({ vp: `${w}x${h}`, overflowX, clipped, overlap: L.overlap, art: L.art });
  }
}
check('四档视口无横向溢出 / 无裁切 / HUD 不压画布', layoutIssues.length === 0, layoutIssues);

await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 2, mobile: false });
await sleep(300);

/* ---------- 收尾 ---------- */
ws.close();
chrome.kill();
const failed = results.filter((r) => !r.pass);
console.log(`\n${results.length - failed.length}/${results.length} passed`);
process.exit(failed.length ? 1 : 0);
