// exporter.js — 导出：PNG 序列（zip）/ 独立 HTML / WebM-MP4
//
// 三条导出的共同点：都不依赖 GH 再发一次数据，用的全是浏览器里已经有的文档状态。
// 这也是 v0.16 把时间交给运行时之后的直接收益 —— GH 只需要把"帧序列"发一次，
// 浏览器就能自己把整段时间渲染成 300 张图或一段视频。

import { encodeF32 } from './geom.js';
import { zipStore } from './zip.js';

/** 整份文档里最长的那条时间轨（秒）。没有任何轨道时给 4 秒。 */
export function trackDuration(doc) {
  let d = 0;
  for (const c of doc.cmds.values()) {
    if (c.track && c.track.duration > d) d = c.track.duration;
  }
  return d > 0 ? d : 4;
}

/* ---------------- 文档 → 可重放的 op 列表 ---------------- */

function serializeCmd(c) {
  const out = {
    id: c.id,
    geom: c.geom,
    layer: c.layer,
    order: c.order,
    visible: c.visible,
    opacity: c.opacity,
    style: c.style,
    // progress 写 1：导出的是"终态 + 轨道"，tween 的中间态不属于作品本身
    transform: c.transform && c.transform.matrix
      ? { matrix: c.transform.matrix, progress: 1 }
      : { progress: 1 },
    track: null,
    // 裁剪跟着指令走：它描述"这条指令被裁成什么样"，不是图层属性
    clip: c.clip ? { geom: c.clip.geom, invert: c.clip.invert } : null,
  };
  if (c.track) {
    out.track = {
      n: c.track.n,
      frames: c.track.frames ? encodeF32(c.track.frames) : null,
      reveal: c.track.reveal ? Array.from(c.track.reveal) : null,
      duration: c.track.duration,
      loop: c.track.loop,
      easeX: c.track.ease ? c.track.ease.x : null,
      easeY: c.track.ease ? c.track.ease.y : null,
    };
  }
  return out;
}

function serializeEntity(d) {
  return {
    geom: d.geom, layer: d.layer, order: d.order, count: d.count,
    speed: d.speed, lifetime: d.lifetime, phase: d.phase, trail: d.trail,
    size: d.size, width: d.width, color: d.color, fade: d.fade,
    visible: d.visible, opacity: d.opacity,
    shape: d.shape ? { enc: 'f32', coords: encodeF32(d.shape.coords), closed: d.shape.closed } : null,
  };
}

/** 当前文档状态 → 一次 doc.reset 全量 op（独立 HTML 用它冷启动） */
export function serializeDoc(doc, entities) {
  const ops = [{ op: 'doc.reset', canvas: { ...doc.canvas } }];
  for (const l of doc.layers.values()) ops.push({ op: 'layer.upsert', value: { ...l } });
  for (const [id, g] of doc.geoms) {
    ops.push({
      op: 'geom.upsert', id,
      value: g.kind === 'path'
        ? { kind: 'path', cmds: g.cmds }
        : g.kind === 'image'
          ? { kind: 'image', src: g.src, w: g.w, h: g.h }
          : { kind: g.kind, enc: 'f32', coords: encodeF32(g.coords) },
    });
  }
  for (const c of doc.cmds.values()) ops.push({ op: 'cmd.upsert', value: serializeCmd(c) });

  const entOps = [];
  if (entities) {
    for (const [id, bp] of entities.blueprints) {
      entOps.push({ op: 'entity.upsert', id, value: serializeEntity(bp.def) });
    }
  }
  // t:'patch' 是 Doc.apply 的入场券（不是随便一个 {ops} 对象都能进）
  return { patch: { v: '0.1', t: 'patch', rev: 1, snapshot: true, ops }, entities: entOps };
}

/* ---------------- 小工具 ---------------- */

const canvasBlob = (cv, type = 'image/png') =>
  new Promise((res) => cv.toBlob((b) => res(b), type, 0.95));

const blobBytes = async (b) => new Uint8Array(await b.arrayBuffer());

const yieldToUi = () => new Promise((r) => setTimeout(r, 0));

/** 页面尺寸的离屏画布（导出不能动屏幕上那张，否则会闪） */
function scratch(renderer, scale) {
  const w = renderer.doc.canvas.w;
  const h = renderer.doc.canvas.h;
  const cv = document.createElement('canvas');
  cv.width = Math.max(1, Math.round(w * scale));
  cv.height = Math.max(1, Math.round(h * scale));
  const ctx = cv.getContext('2d');
  return { cv, ctx, w, h, scale };
}

/* ---------------- 1. PNG 序列 ---------------- */

/**
 * 按 duration × fps 逐帧渲染成 PNG，打成一个 zip。
 * 时间是自己给的（不跟墙钟赛跑），所以同样的参数永远得到同样的 300 张图。
 */
export async function exportPngSequence(opts) {
  const {
    doc, renderer, entities, fps = 30, duration = trackDuration(doc),
    scale = 1, transparent = false, onProgress,
  } = opts;

  const { cv, ctx } = scratch(renderer, scale);
  const total = Math.max(1, Math.round(duration * fps));
  const savedTime = doc.time;
  const bg = doc.canvas.bg;
  const files = [];

  // 粒子要按导出时间轴重新播一遍，否则会拿屏幕上此刻的随机状态
  if (entities) entities.restart();

  for (let i = 0; i < total; i++) {
    doc.time = i / fps;
    if (entities) entities.update(doc, 1 / fps);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, cv.width, cv.height);
    // 离屏画布是透明的：要背景就得自己填（透明导出则留空，方便叠到别的画面上）
    if (!transparent && bg) {
      ctx.fillStyle = bg;
      ctx.fillRect(0, 0, cv.width, cv.height);
    }
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    renderer._paint(ctx);
    const blob = await canvasBlob(cv);
    files.push({ name: `frame_${String(i).padStart(4, '0')}.png`, data: await blobBytes(blob) });
    if (onProgress) onProgress(i + 1, total);
    if (i % 4 === 3) await yieldToUi();     // 别把主线程焊死
  }

  doc.time = savedTime;
  if (entities) entities.restart();
  return { blob: new Blob([zipStore(files)], { type: 'application/zip' }), frames: total };
}

/* ---------------- 2. 独立 HTML ---------------- */

const DEP_RE = /(?:from\s*|import\s*)['"](\.[^'"]+)['"]/g;

/** UTF-8 安全的 data: URL。不能用 blob: —— blob 只活在当前文档里，存成文件再打开就是死链 */
function jsDataUrl(src) {
  const bytes = new TextEncoder().encode(src);
  let bin = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return 'data:text/javascript;base64,' + btoa(bin);
}

/** 递归抓取若干入口的整棵依赖树，按"依赖先于使用"的顺序内联成 data: URL */
async function bundleAll(entryUrls) {
  const src = new Map();          // url -> 源码
  const order = [];               // 依赖在前的顺序
  const state = new Map();        // url -> 1 访问中 / 2 完成

  async function visit(url) {
    if (state.get(url) === 2) return;
    if (state.get(url) === 1) throw new Error(`模块循环依赖，无法打包：${url}`);
    state.set(url, 1);
    const text = await (await fetch(url)).text();
    src.set(url, text);
    for (const m of text.matchAll(DEP_RE)) await visit(new URL(m[1], url).href);
    state.set(url, 2);
    order.push(url);
  }
  for (const e of entryUrls) await visit(e);

  const urls = new Map();
  for (const url of order) {          // 依赖已在前，改引用时一定拿得到 URL
    const rewritten = src.get(url).replace(DEP_RE, (m, spec) => {
      const target = urls.get(new URL(spec, url).href);
      if (!target) throw new Error(`打包顺序错误：${spec}`);
      return m.replace(spec, target);
    });
    urls.set(url, jsDataUrl(rewritten));
  }
  return { urls };
}

const BOOT = `
import { Doc } from '__DOC__';
import { Renderer2D } from '__RENDERER__';
import { EntitySystem } from '__ENTITY__';
import { Clock } from '__CLOCK__';

const P = __PATCH__;

try {
  const doc = new Doc();
  const clock = new Clock();
  const entities = new EntitySystem();
  const renderer = new Renderer2D(document.getElementById('host'));
  renderer.setDoc(doc);
  renderer.setEntitySystem(entities);

  const res = doc.apply(P.patch);
  entities.applyOps(res.entityOps, doc);

  let last = performance.now();
  function loop(now) {
    const dt = (now - last) / 1000;
    last = now;
    const scaled = clock.step(dt);
    entities.update(doc, scaled);
    // 时间轨要的只有这一个数：pause 就冻住，play 就走
    doc.time = clock.time;
    renderer.draw();
    requestAnimationFrame(loop);
  }

  addEventListener('resize', () => renderer.draw());
  addEventListener('keydown', (e) => {
    if (e.key === ' ') { e.preventDefault(); clock.playing ? clock.pause() : clock.play(); }
    if (e.key === 'r' || e.key === 'R') { clock.reset(); entities.restart(); }
    if (e.key === 'h' || e.key === 'H') document.body.classList.toggle('bare');
  });
  document.getElementById('stage').addEventListener('click', () => {
    clock.playing ? clock.pause() : clock.play();
  });

  // 给"这个文件到底跑起来没有"留一个可断言的点（也方便用户在控制台里玩）
  window.__GHCE = { doc, renderer, clock, entities, res };
  requestAnimationFrame(loop);
} catch (e) {
  window.__bootError = (e && e.stack) || String(e);
  const pre = document.createElement('pre');
  pre.style.cssText = 'position:fixed;inset:16px;color:#FF5252;font-size:12px;white-space:pre-wrap;z-index:9';
  pre.textContent = 'GHCE 播放器启动失败：\\n' + window.__bootError;
  document.body.appendChild(pre);
}
`;

const STANDALONE_CSS = `
:root{--bg:#06060a;--fg:#F2F2F2;--muted:#7d7d88}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);
  font-family:ui-monospace,"Fira Code","Cascadia Mono",Consolas,monospace;overflow:hidden}
#stage{position:fixed;inset:0;display:grid;place-items:center}
#host{width:100%;height:100%;display:grid;place-items:center}
#host canvas{display:block;box-shadow:0 24px 80px rgba(0,0,0,.7)}
#hint{position:fixed;right:16px;bottom:14px;color:var(--muted);font-size:10.5px;
  letter-spacing:.08em;user-select:none;transition:opacity .2s}
body.bare #hint{opacity:0}
`;

/** 把当前作品压成一个单文件 HTML（运行时内联 + 文档内联，离线可看） */
export async function exportStandalone(opts) {
  const { doc, entities, title = 'GHCE' } = opts;
  const here = new URL('./', import.meta.url).href;
  const E = {
    doc: new URL('./doc.js', here).href,
    renderer: new URL('./renderer2d.js', here).href,
    entity: new URL('./entity.js', here).href,
    clock: new URL('./clock.js', here).href,
  };

  const { urls } = await bundleAll([E.doc, E.renderer, E.entity, E.clock]);

  const payload = JSON.stringify(serializeDoc(doc, entities))
    .replace(/<\/script/gi, '<\\/script');

  // 用函数式替换：文档里万一出现 "$&" 这类序列，字符串替换会把它当捕获组
  const boot = BOOT
    .replace('__DOC__', () => urls.get(E.doc))
    .replace('__RENDERER__', () => urls.get(E.renderer))
    .replace('__ENTITY__', () => urls.get(E.entity))
    .replace('__CLOCK__', () => urls.get(E.clock))
    .replace('__PATCH__', () => payload);

  const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${title.replace(/[<&>]/g, '')}</title>
<style>${STANDALONE_CSS}</style>
</head>
<body>
<div id="stage"><div id="host"></div></div>
<div id="hint">空格 播放/暂停 · R 重播 · H 隐藏</div>
<script type="module">${boot}</script>
</body>
</html>`;

  return new Blob([html], { type: 'text/html' });
}

/* ---------------- 3. 视频（WebM / MP4） ---------------- */

export function pickVideoMime() {
  const cands = [
    'video/webm;codecs=vp9',
    'video/webm;codecs=vp8',
    'video/webm',
    'video/mp4;codecs=avc1.42E01E',
    'video/mp4',
  ];
  if (typeof MediaRecorder === 'undefined') return null;
  for (const m of cands) {
    try { if (MediaRecorder.isTypeSupported(m)) return m; } catch { /* 老浏览器 */ }
  }
  return null;
}

export const videoExt = (mime) => (mime && mime.includes('mp4') ? 'mp4' : 'webm');

/**
 * 录屏式导出：按真实速度播一遍 duration 秒。
 * 为什么不用逐帧 requestFrame：那条路只对 PNG 有效，MediaRecorder 的时间戳来自墙钟，
 * 硬塞帧会得到一段被"快进"的视频。按 1× 播一遍虽然要等，但时长是对的。
 */
export async function exportVideo(opts) {
  const {
    doc, renderer, clock, entities, fps = 30,
    duration = trackDuration(doc), onProgress,
  } = opts;

  const mime = pickVideoMime();
  if (!mime) throw new Error('这个浏览器不支持 MediaRecorder 编码');
  if (!renderer.canvas.captureStream) throw new Error('这个浏览器不支持 canvas.captureStream');

  const stream = renderer.canvas.captureStream(fps);
  const rec = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 12_000_000 });
  const chunks = [];
  rec.ondataavailable = (e) => { if (e.data && e.data.size) chunks.push(e.data); };
  const stopped = new Promise((r) => { rec.onstop = r; });

  clock.reset();
  if (entities) entities.restart();
  clock.play();
  rec.start();

  await new Promise((resolve) => {
    const t0 = performance.now();
    const tick = () => {
      const t = (performance.now() - t0) / 1000;
      if (onProgress) onProgress(Math.min(t, duration), duration);
      if (t >= duration) { resolve(); return; }
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });

  rec.stop();
  await stopped;
  for (const tr of stream.getTracks()) tr.stop();
  clock.pause();

  return { blob: new Blob(chunks, { type: mime }), mime, ext: videoExt(mime), duration };
}
