// main.js — 运行时装配：文档状态 → 渲染器 → 桥
import { Doc } from './doc.js';
import { Renderer2D } from './renderer2d.js';
import { BridgeClient } from './bridge-client.js';
import { EntitySystem } from './entity.js';
import { Clock } from './clock.js';
import { Pointer } from './pointer.js';
import { EventTriggers } from './events.js';
import { Tweens } from './tween.js';
import { toSVG } from './svg.js';
import { resolveTrackMatrix, resolveTrackReveal, trackU } from './xform.js';
import {
  exportPngSequence, exportStandalone, exportVideo,
  trackDuration, pickVideoMime, videoExt,
} from './exporter.js';

const WS_URL = new URLSearchParams(location.search).get('ws') || 'ws://127.0.0.1:8777/ghce';

const doc = new Doc();
const entities = new EntitySystem();
const clock = new Clock();
const pointer = new Pointer();
// 变换过渡：GH 只发"每条指令现在该在哪"，中间那些帧由这里补（变换本身就是动画）
const tweens = new Tweens();
const stage = document.getElementById('stage-inner');
const renderer = new Renderer2D(stage);
renderer.setDoc(doc);
renderer.setEntitySystem(entities);
entities.setPointer(pointer);

// 事件触发与选择性清屏（阶段 3 C/D）：规则由 GH 发一次，命中后浏览器直接执行
const events = new EventTriggers({ clock, doc, tweens, entities, renderer, requestDraw: () => requestDraw() });
events.bind(renderer.canvas);

const el = {
  dot: document.getElementById('dot'),
  conn: document.getElementById('k-conn'),
  rev: document.getElementById('k-rev'),
  count: document.getElementById('k-count'),
  patch: document.getElementById('k-patch'),
  ops: document.getElementById('k-ops'),
  draw: document.getElementById('k-draw'),
  ent: document.getElementById('k-ent'),
  time: document.getElementById('k-time'),
  ptr: document.getElementById('k-ptr'),
  play: document.getElementById('btn-play'),
  fps: document.getElementById('k-fps'),
  errs: document.getElementById('errs'),
  hud: document.getElementById('hud'),
};

let dirty = true;
let currentScene = null;
const requestDraw = () => { dirty = true; };

// ---------- 渲染循环 ----------
// 动态对象必须自己走时钟：GH 不再发包时（rev 不变）画面照样要动。
let drawCount = 0, fps = 0, fpsT0 = performance.now();
let lastFrame = performance.now();
// 循环必须比绘制活得更久：任何一帧抛异常都不能让 requestAnimationFrame 断掉，
// 否则画面永久冻结、时钟停在 0，看起来像"动画没实现"，实际是异常把循环打死了。
const loopErrors = new Set();
let drawFails = 0, drawSuspended = false;
function loop(now) {
  try {
    tick(now);
  } catch (e) {
    const msg = `${e && (e.message || e)}`;
    drawFails++;
    if (!loopErrors.has(msg)) {
      loopErrors.add(msg);
      pushErr({ code: 'LOOP', msg });
    }
    if (drawFails >= 20 && !drawSuspended) {
      drawSuspended = true;   // 一直画不出来就先停绘制，但时钟与循环继续跑，便于在 HUD 上看到状态
      pushErr({ code: 'DRAW', msg: '绘制连续失败，已暂停重绘（时钟仍在走）' });
    }
  }
  requestAnimationFrame(loop);
}

function tick(now) {
  const dt = (now - lastFrame) / 1000;
  lastFrame = now;

  const scaled = clock.step(dt);
  const steps = entities.update(doc, scaled);
  // 时间轨要的那个时间就是它：暂停就冻住，播放就走。渲染器只读 doc.time，不自己取时钟。
  doc.time = clock.time;
  // 变换过渡最后写：它是"数据变了 → 演过去"，不受暂停影响（暂停只停时钟，不该让变换停在半路）
  const tweenTouched = tweens.update(doc);
  if (steps > 0 || tweenTouched) dirty = true;
  if (doc.time !== undefined && hasTracks(doc)) dirty = true;   // 有轨道就每帧都要重画
  // 残影 / 累积模式下画面永远在变，必须逐帧重绘
  if ((doc.canvas.clearMode || 'clear') !== 'clear') dirty = true;

  if (dirty) {
    dirty = false;
    if (!drawSuspended) {
      renderer.draw();
      drawFails = 0;
    }
    clock.frame++;
    drawCount++;
  }
  if (now - fpsT0 >= 500) {
    fps = (drawCount * 1000) / (now - fpsT0);
    drawCount = 0; fpsT0 = now;
    updateHud();
  }
  sendStat(dt);
}

// 状态回传（阶段 7 的双向闭环 / GH 侧自检面板的数据源）
let statAcc = 0;
function sendStat(dt) {
  statAcc += dt;
  if (statAcc < 0.2) return;
  statAcc = 0;
  if (client.state !== 'open') return;
  client.metrics.statSent++;
  const es = entities.stats;
  client.send({
    v: '0.1',
    t: 'stat',
    ptr: [Math.round(pointer.x), Math.round(pointer.y)],
    inside: pointer.inside,
    down: pointer.down,
    clicks: pointer.clicks,
    keys: [...pointer.keys].slice(0, 8).join(''),
    fps: +fps.toFixed(1),
    entities: es.entities,
    alive: es.alive,
    rev: doc.rev,
    drawMs: +renderer.lastDraw.ms.toFixed(2),
    stepMs: +es.stepMs.toFixed(2),
    time: +clock.time.toFixed(2),
  });
}

// ---------- HUD ----------
function fmtBytes(n) {
  if (!n) return '—';
  return n > 1024 ? (n / 1024).toFixed(1) + ' KB' : n + ' B';
}
function updateHud() {
  const c = client;
  el.conn.textContent = c.state;
  el.dot.className = 'dot ' + c.state;
  el.rev.textContent = doc.rev < 0 ? '—' : doc.rev;
  const s = doc.stats();
  el.count.textContent = `${s.geoms} / ${s.cmds}`;
  el.patch.textContent = c.metrics.patches ? `${c.metrics.lastPatchMs.toFixed(2)} ms` : '—';
  el.ops.textContent = c.metrics.patches ? `${c.metrics.lastOps} / ${fmtBytes(c.metrics.lastBytes)}` : '—';
  const d = renderer.lastDraw;
  el.draw.textContent = `${d.ms.toFixed(2)} ms · ${d.drawn}${d.skipped ? ' / 跳过' + d.skipped : ''}`;
  el.fps.textContent = fps ? fps.toFixed(1) + ' fps' : '—';
  const es = entities.stats;
  if (el.ent) {
    el.ent.textContent = es.entities
      ? `${es.entities} · ${es.stepMs.toFixed(2)} ms`
      : '—';
  }
  if (el.time) {
    el.time.textContent = `${clock.time.toFixed(2)}s${clock.playing ? '' : ' · 暂停'}`;
  }
  if (el.ptr) {
    const ps = pointer.stats();
    el.ptr.textContent = ps.inside ? `${ps.x}, ${ps.y}${ps.down ? ' ↓' : ''}` : '—';
  }
}

// 三种"重置"是不同的事（任务书 3E）：时间、对象状态、画布内容，不能混成一个按钮
function syncPlayButton() {
  if (el.play) el.play.textContent = clock.playing ? '暂停' : '播放';
}

const errQueue = [];
function pushErr(e) {
  errQueue.push(e);
  while (errQueue.length > 6) errQueue.shift();
  el.errs.textContent = errQueue.map((x) => `[${x.code || 'ERR'}] ${x.msg || ''}`).join('\n');
}

// ---------- 桥 ----------
const client = new BridgeClient(WS_URL, {
  onPatch(patch) {
    const res = doc.apply(patch);
    if (res.entityOps.length) entities.applyOps(res.entityOps, doc);
    if (res.eventOps.length) events.applyOps(res.eventOps);
    if (res.reset) {
      clock.reset();          // 新作品从 0 秒开始，否则非循环时间线一上来就在终点
      entities.restart();
      tweens.clear();         // 换作品了，旧的过渡记录作废
    }
    if (res.changedGeom.size) {
      renderer.invalidate(res.changedGeom);
      // 几何变了：采样器要跟着重建，但实体状态保留（阶段 2 的关键语义）
      for (const id of res.changedGeom) entities.samplers.delete(id);
    }
    if (res.structural) renderer.invalidate(null);
    // 矩阵变了就起一段过渡（变换本身就是动画）。必须放在 doc 更新之后、绘制之前
    if (tweens.observe(doc)) dirty = true;
    if (res.applied) requestDraw();
    if (res.rejected) pushErr({ code: 'PARTIAL', msg: `rev ${patch.rev}: ${res.rejected} op 被拒绝` });
    for (const e of doc.errors.splice(0)) pushErr(e);
    syncExpDefault();       // 换了作品就刷新"导出时长"的默认值
    return res;
  },
  onStatus(s, extra) {
    if (s === 'retry') pushErr({ code: 'RETRY', msg: `${extra?.wait | 0}ms 后重连 ${WS_URL}` });
    if (s === 'open') applyHarnessMode(!!(extra && extra.mock));
    updateHud();
  },
  onError(e) { pushErr(e); },
});

// ---------- 交互 ----------
document.querySelectorAll('#scenes button').forEach((b) => {
  b.addEventListener('click', () => selectScene(b.dataset.scene));
});
function selectScene(name) {
  currentScene = name;
  document.querySelectorAll('#scenes button').forEach((b) => b.classList.toggle('on', b.dataset.scene === name));
  client.switchScenario(name);
  requestDraw();
}

// 演示场景按钮只在模拟桥下有意义；接真 GH 时藏起来，免得点了没反应
function applyHarnessMode(isMock) {
  const scenes = document.getElementById('scenes');
  const label = document.getElementById('scenes-label');
  if (scenes) scenes.classList.toggle('hidden', !isMock);
  if (label) label.classList.toggle('hidden', !isMock);
}

function download(blob, name) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
}

document.getElementById('btn-png').addEventListener('click', async () => {
  const blob = await renderer.exportPNG(2);
  if (blob) download(blob, `ghce-rev${doc.rev}.png`);
});
document.getElementById('btn-png-alpha').addEventListener('click', async () => {
  // 不填背景色：只有几何本身，alpha 通道保留，可以直接叠到别的画面上
  const blob = await renderer.exportPNG(2, { transparent: true });
  if (blob) download(blob, `ghce-rev${doc.rev}-alpha.png`);
});
document.getElementById('btn-svg').addEventListener('click', () => {
  const { svg, report } = toSVG(doc);
  if (report.warnings.length) pushErr({ code: 'SVG_DEGRADED', msg: report.warnings.slice(0, 3).join(' | ') });
  download(new Blob([svg], { type: 'image/svg+xml' }), `ghce-rev${doc.rev}.svg`);
});

/* ---------- v0.17 交付：PNG 序列 / 独立 HTML / 视频 ---------- */
const expStat = document.getElementById('exp-stat');
const expFps = document.getElementById('exp-fps');
const expDur = document.getElementById('exp-dur');
let expBusy = false;

const expProgress = (text) => { if (expStat) expStat.textContent = text; };
const expDone = (text, bytes) => expProgress(`${text} · ${fmtBytes(bytes)}`);

function expOpts() {
  const fps = Math.max(1, Math.min(60, Number(expFps && expFps.value) || 30));
  const dur = Math.max(0.2, Math.min(120, Number(expDur && expDur.value) || trackDuration(doc)));
  return { fps, dur };
}

/** 时长默认跟着轨道走；用户手填过就不再覆盖 */
function syncExpDefault() {
  if (!expDur) return;
  if (expDur.dataset.touched === '1') return;
  expDur.value = trackDuration(doc).toFixed(1);
}

async function withExport(label, fn) {
  if (expBusy) return null;
  expBusy = true;
  expProgress(`${label}…`);
  try {
    return await fn();
  } catch (e) {
    pushErr({ code: 'EXPORT', msg: `${label}失败：${e && e.message}` });
    expProgress('');
    return null;
  } finally {
    expBusy = false;
  }
}

expDur?.addEventListener('input', () => { expDur.dataset.touched = '1'; });

document.getElementById('btn-seq')?.addEventListener('click', () => withExport('PNG 序列', async () => {
  const { fps, dur } = expOpts();
  const r = await exportPngSequence({
    doc, renderer, entities, fps, duration: dur,
    onProgress: (i, n) => expProgress(`PNG 序列 ${i}/${n} · ${fps}fps · ${dur}s`),
  });
  download(r.blob, `ghce-seq-${fps}fps-${r.frames}f.zip`);
  expDone(`PNG 序列完成（${r.frames} 帧）`, r.blob.size);
  return r;
}));

document.getElementById('btn-html')?.addEventListener('click', () => withExport('独立 HTML', async () => {
  const blob = await exportStandalone({ doc, entities, title: `GHCE rev${doc.rev}` });
  download(blob, `ghce-rev${doc.rev}.html`);
  expDone('独立 HTML 完成（离线可看）', blob.size);
  return blob;
}));

document.getElementById('btn-video')?.addEventListener('click', () => withExport('视频', async () => {
  const { fps, dur } = expOpts();
  const mime = pickVideoMime();
  if (!mime) throw new Error('这个浏览器不支持 MediaRecorder');
  const r = await exportVideo({
    doc, renderer, clock, entities, fps, duration: dur,
    onProgress: (t, d) => expProgress(`录制 ${t.toFixed(1)}/${d}s · ${videoExt(mime)}`),
  });
  download(r.blob, `ghce-rev${doc.rev}.${r.ext}`);
  expDone(`视频完成（${r.ext} · ${dur}s）`, r.blob.size);
  return r;
}));

addEventListener('resize', requestDraw);

// ---------- 播放控制（阶段 3 A / E）----------
el.play?.addEventListener('click', () => { clock.toggle(); syncPlayButton(); requestDraw(); });
document.getElementById('btn-reset-time')?.addEventListener('click', () => { clock.reset(); requestDraw(); });
document.getElementById('btn-restart')?.addEventListener('click', () => { entities.restart(); requestDraw(); });
document.getElementById('btn-clear')?.addEventListener('click', () => { renderer.clear(); requestDraw(); });
syncPlayButton();

addEventListener('keydown', (e) => {
  if (e.key === 'h' || e.key === 'H') {
    el.hud.classList.toggle('hidden');
    document.body.classList.toggle('hud-hidden', el.hud.classList.contains('hidden'));
    requestDraw();
  }
  if (e.key === 'r' || e.key === 'R') { client.close(); client.connect(); }
  if (e.key === ' ') { e.preventDefault(); clock.toggle(); syncPlayButton(); requestDraw(); }
  if (e.key === '0') { clock.reset(); requestDraw(); }
  if (e.key === '1') selectScene('static');
  if (e.key === '2') selectScene('mutate');
  if (e.key === '3') selectScene('stress');
  if (e.key === '4') selectScene('entities');
  if (e.key === '5') selectScene('living');
});

client.connect();
requestDraw();
pointer.attach(renderer.canvas, (cx, cy) => renderer.clientToPage(cx, cy));
applyHarnessMode(false);   // 默认按"接真 GH"处理，收到 mock 的 welcome 才显示演示场景
requestAnimationFrame(loop);

// 调试与自动化验收入口
window.GHCE = {
  doc, renderer, client, entities, clock, pointer, tweens, events, toSVG, selectScene, requestDraw,

  // v0.17 导出（验收直接调这几个，不用去点按钮）
  exportPngSequence, exportStandalone, exportVideo, trackDuration, pickVideoMime, videoExt,

  /** 自检用：某条指令在某一时刻的时间轨取值（不传 time 就用当前时钟） */
  trackAt(id, time) {
    const c = doc.cmds.get(String(id));
    if (!c || !c.track) return null;
    const t = time == null ? clock.time : time;
    return {
      u: trackU(c.track, t),
      reveal: resolveTrackReveal(c.track, t),
      matrix: resolveTrackMatrix(c.track, t),
    };
  },
};

/** 有没有任何指令带时间轨 —— 有就说明画面每帧都在变，不能靠 dirty 标记省绘制 */
function hasTracks(d) {
  for (const c of d.cmds.values()) if (c.track) return true;
  return false;
}
