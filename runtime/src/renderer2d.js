// renderer2d.js — Canvas 2D 后端
// 只依赖 Doc 的只读视图；不修改文档状态

import { buildPath2D, flatCoords, geomLength, truncatePath2D } from './geom.js';
import { evalExpr } from './expr.js';
import {
  resolveMatrix, resolveTrackMatrix, resolveTrackReveal,
  resolveMorph, morphFallbackU, trackU,
} from './xform.js';

const DPR_CAP = 2;

/**
 * 图片解码缓存。
 *
 * 为什么按 src 缓存而不是按 geom id：同一张图可以被两条指令引用（比如原图 + 缩小版），
 * 按 id 缓存会解码两次。数据 URL 本身就是身份。
 *
 * 解码是异步的，所以首帧一定画不出来 —— 这不是缺陷，是浏览器的事实。
 * 加载完成后回调一次 onImageReady，由 main.js 请求重绘；否则第一次渲染就永远停在空白，
 * 而 patch 早就到了（doc.rev 不变，没人知道该重画）。
 */
const imageCache = new Map();
let onImageReady = null;

export function setImageReadyHandler(fn) {
  onImageReady = fn;
}

function imageFor(src) {
  let entry = imageCache.get(src);
  if (entry) return entry;

  const img = new Image();
  entry = { img, ready: false, failed: false };
  img.onload = () => {
    entry.ready = true;
    if (onImageReady) onImageReady();
  };
  // 解码失败不抛异常：一个坏图片不该让整个作品停摆，画别的就行
  img.onerror = () => { entry.failed = true; };
  img.src = src;
  imageCache.set(src, entry);
  return entry;
}

/** 颜色解析：#RGB / #RRGGBB / #RRGGBBAA / rgb() / rgba() */
export function parseColor(str, alphaMul = 1) {
  if (!str) return null;
  const s = String(str).trim();
  if (s[0] === '#') {
    let hex = s.slice(1);
    if (hex.length === 3) hex = hex.split('').map((c) => c + c).join('');
    if (hex.length !== 6 && hex.length !== 8) return null;
    const r = parseInt(hex.slice(0, 2), 16);
    const g = parseInt(hex.slice(2, 4), 16);
    const b = parseInt(hex.slice(4, 6), 16);
    const a = hex.length === 8 ? parseInt(hex.slice(6, 8), 16) / 255 : 1;
    const aa = Math.max(0, Math.min(1, a * alphaMul));
    return `rgba(${r},${g},${b},${aa.toFixed(4)})`;
  }
  if (s.startsWith('rgb')) {
    if (alphaMul === 1) return s;
    const m = s.match(/[\d.]+/g);
    if (!m) return s;
    const [r, g, b, a = 1] = m.map(Number);
    return `rgba(${r},${g},${b},${(a * alphaMul).toFixed(4)})`;
  }
  return s;
}

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);

/**
 * 只换色相，保留底色的饱和度与明度：hue 0..1 → CSS 颜色。
 * 为什么不是 RGB 三通道：色相循环是这类作品最常见的写法，一个表达式就够；
 * 底色若是灰/白（饱和度≈0），自动补一档饱和度，否则换色相根本看不出来。
 */
export function recolor(base, hue01, alphaMul = 1) {
  const s = String(base || '#EDEBE4').trim();
  let r = 237, g = 235, b = 228;
  if (s[0] === '#') {
    let hex = s.slice(1);
    if (hex.length === 3) hex = hex.split('').map((c) => c + c).join('');
    if (hex.length >= 6) {
      r = parseInt(hex.slice(0, 2), 16);
      g = parseInt(hex.slice(2, 4), 16);
      b = parseInt(hex.slice(4, 6), 16);
    }
  }

  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  let h = 0, sat = 0;
  const l = (max + min) / 2;
  const d = max - min;
  if (d > 1e-6) {
    sat = d / (1 - Math.abs(2 * l - 1));
    if (max === r) h = ((g - b) / d + 6) % 6;
    else if (max === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h /= 6;
  }
  if (sat < 0.12) sat = 0.75;      // 灰/白底直接换色相会看不出变化

  const hue = ((Number.isFinite(hue01) ? hue01 : h) % 1 + 1) % 1;
  return `hsla(${(hue * 360).toFixed(2)},${(sat * 100).toFixed(1)}%,${(l * 100).toFixed(1)}%,${clamp01(alphaMul).toFixed(3)})`;
}

export class Renderer2D {
  constructor(host) {
    this.host = host;              // 容器元素，用于 CSS 缩放适配
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'art';
    host.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d', { alpha: true, desynchronized: true });
    this.doc = null;
    this._pathCache = new Map();   // geomId -> { geom, path }
    this._truncCache = new Map();  // geomId|reveal -> { geom, path }（生长截断）
    this._sizeKey = '';
    this.lastDraw = { ms: 0, drawn: 0, skipped: 0 };
  }

  setDoc(doc) {
    if (this.doc !== doc) {
      this.doc = doc;
      this._pathCache.clear();
    }
  }

  /** 实体系统在命令之后绘制（阶段 2）。传 null 关闭 */
  setEntitySystem(sys) {
    this.entitySystem = sys;
  }

  /** 几何变更后使缓存失效。ids 为 null 时全清 */
  invalidate(ids) {
    if (!ids) this._pathCache.clear();
    else for (const id of ids) this._pathCache.delete(id);
  }

  _pathFor(id, geom) {
    const hit = this._pathCache.get(id);
    if (hit && hit.geom === geom) return hit.path;
    const path = buildPath2D(geom);
    this._pathCache.set(id, { geom, path });
    return path;
  }

  /**
   * morph 帧 → Path2D。
   *
   * 缓存键是 (帧索引, 帧内小数)：同一帧内重复绘制不重复插值，跨帧才重建。
   * 96 点的逐顶点插值大约几十微秒，但 500 条指令 × 每帧重算就不划算了。
   */
  _morphPath(cmd, u) {
    const m = cmd.morph;
    if (!m || m.n < 2) return null;

    const pos = clamp01(u) * (m.n - 1);
    const i0 = Math.floor(pos);
    const stamp = i0 * 1000 + Math.round((pos - i0) * 500);
    if (cmd._morphStamp === stamp) return cmd._morphPath2d;

    const pts = resolveMorph(m, u);
    if (!pts || pts.length < 4) return null;

    const p = new Path2D();
    p.moveTo(pts[0], pts[1]);
    for (let i = 2; i < pts.length; i += 2) p.lineTo(pts[i], pts[i + 1]);
    if (m.closed) p.closePath();

    cmd._morphStamp = stamp;
    cmd._morphPath2d = p;
    return p;
  }

  /** 截断路径（生长）：同一几何的同一 reveal 只建一次 Path2D */
  _truncPath(id, geom, reveal) {
    const key = id + '|' + Math.round(reveal * 256);
    const hit = this._truncCache.get(key);
    if (hit && hit.geom === geom) return hit.path;
    const path = truncatePath2D(geom, reveal);
    if (!path) return null;
    if (this._truncCache.size > 4096) this._truncCache.clear();
    this._truncCache.set(key, { geom, path });
    return path;
  }

  /** 按 doc.canvas 调整 backing store 与 CSS 显示尺寸 */
  fit(viewportW, viewportH) {
    const doc = this.doc;
    if (!doc) return 1;
    const { w, h } = doc.canvas;
    const dpr = doc.canvas.dpr === 'auto'
      ? Math.min(window.devicePixelRatio || 1, DPR_CAP)
      : Math.min(doc.canvas.dpr || 1, DPR_CAP);

    const pad = Math.min(80, Math.min(viewportW, viewportH) * 0.12);
    const scale = Math.max(0.02, Math.min((viewportW - pad) / w, (viewportH - pad) / h));

    // 关键：不用 transform: scale()。transform 不参与布局，元素本身仍是 w×h，
    // 容器比它窄时会被挤出右边界并裁掉。直接给 CSS 尺寸，让 flex 正常居中。
    const cssW = Math.round(w * scale);
    const cssH = Math.round(h * scale);
    const key = `${w}x${h}@${dpr}→${cssW}x${cssH}`;
    if (key !== this._sizeKey) {
      this.canvas.width = Math.max(1, Math.round(w * dpr));
      this.canvas.height = Math.max(1, Math.round(h * dpr));
      this.canvas.style.width = cssW + 'px';
      this.canvas.style.height = cssH + 'px';
      this.canvas.style.transform = '';
      this._sizeKey = key;
      this._dpr = dpr;
    }
    return scale;
  }

  draw() {
    const doc = this.doc;
    if (!doc) return this.lastDraw;
    const t0 = performance.now();
    this.fit(this.host.clientWidth || window.innerWidth, this.host.clientHeight || window.innerHeight);
    const g = this.ctx;
    const dpr = this._dpr || 1;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const { w, h, bg } = doc.canvas;
    const mode = doc.canvas.clearMode || 'clear';

    if (mode === 'clear') {
      g.clearRect(0, 0, w, h);
      if (bg) {
        g.fillStyle = parseColor(bg);
        g.fillRect(0, 0, w, h);
      }
    } else if (mode === 'fade') {
      // 残影 / 累积（阶段 3 C）：不清屏，只盖一层半透明底色
      const a = Math.max(0.004, Math.min(1, doc.canvas.fadeAlpha ?? 0.08));
      if (bg) {
        g.globalAlpha = a;
        g.fillStyle = parseColor(bg);
        g.fillRect(0, 0, w, h);
        g.globalAlpha = 1;
      }
    }
    // mode === 'none'：画面永久累积，只有显式 reset 才清

    const r = this._paint(g);
    this.lastDraw = { ms: performance.now() - t0, ...r };
    return this.lastDraw;
  }

  /** 实际绘制。供 draw() 与 PNG 导出共用 */
  _paint(g) {
    const doc = this.doc;
    let drawn = 0, skipped = 0;
    const noLayer = { order: 0, opacity: 1, blend: 'source-over', visible: true };

    for (const cmd of doc.drawList()) {
      if (!cmd.visible) continue;
      const geom = doc.geoms.get(cmd.geom);
      if (!geom) { skipped++; continue; }
      const layer = (cmd.layer && doc.layers.get(cmd.layer)) || noLayer;
      if (layer.visible === false) continue;

      // morph 的进度要和矩阵/生长共用同一个 u，否则形状、位置、笔画会各走各的
      let u = 0;
      if (cmd.track) u = trackU(cmd.track, doc.time || 0, doc.timeline);
      else if (cmd.morph) u = morphFallbackU(cmd.morph, doc.time || 0);

      const isImage = geom.kind === 'image';
      const isPoints = geom.kind === 'points';
      const path = (isImage || isPoints)
        ? null
        : (cmd.morph ? this._morphPath(cmd, u) : this._pathFor(cmd.geom, geom));
      if (!isImage && !isPoints && !path) { skipped++; continue; }

      g.save();

      // 裁剪（v0.33）：模具用页面坐标，所以在 g.transform 之前落剪。
      // 反过来写会让裁剪框跟着指令旋转 —— "把图片裁进一个圆"会变成"裁进一个跟着图转的圆"。
      if (cmd.clip) {
        const clipGeom = doc.geoms.get(cmd.clip.geom);
        const clipPath = clipGeom && clipGeom.kind !== 'image'
          ? this._pathFor(cmd.clip.geom, clipGeom)
          : null;
        if (clipPath) {
          if (cmd.clip.invert) {
            // 反相 = 整幅画布减去模具，用 even-odd 一次成路径
            const inv = new Path2D();
            inv.rect(0, 0, doc.canvas.w, doc.canvas.h);
            inv.addPath(clipPath);
            g.clip(inv, 'evenodd');
          } else {
            g.clip(clipPath);
          }
        } else {
          skipped++;
          g.restore();
          continue;
        }
      }

      // 绑定求值：只在这一帧的局部变量里生效，不写回 cmd —— 写回去的话
      // 导出、SVG、下一帧的 diff 都会拿到被污染的脏值。
      const ex = cmd.bind ? { t: doc.time || 0, vars: doc.vars } : null;
      const bindNum = (ast, fallback) => {
        if (!ex || !ast) return fallback;
        const v = evalExpr(ast, ex);
        return Number.isFinite(v) ? v : fallback;
      };
      const opa = bindNum(cmd.bind && cmd.bind.opacity, cmd.opacity ?? 1);
      g.globalAlpha = clamp01((layer.opacity ?? 1) * opa);
      // 缩放绑定：半径呼吸用这一条，不必生成 morph 帧
      const boundScale = cmd.bind && cmd.bind.scale ? bindNum(cmd.bind.scale, NaN) : NaN;
      const boundHue = cmd.bind && cmd.bind.hue ? bindNum(cmd.bind.hue, NaN) : NaN;
      g.globalCompositeOperation = layer.blend || 'source-over';

      const t = cmd.transform;
      // 有 track 就用 track：它自己就是时间轴，按 doc.time 取帧。
      // 没有 track 才走 tween：progress < 1 时在 identity 与终态之间插值 —— "让 PSR 演出来"。
      const m = cmd.track ? resolveTrackMatrix(cmd.track, doc.time || 0, doc.timeline) : resolveMatrix(t);
      if (m) g.transform(m[0], m[1], m[2], m[3], m[4], m[5]);

      if (t.translate[0] || t.translate[1]) g.translate(t.translate[0], t.translate[1]);
      if (t.rotate) {
        // 协议里 rotate 用「度」（人读得懂），这里换算成弧度
        const rad = (t.rotate * Math.PI) / 180;
        g.translate(t.pivot[0], t.pivot[1]);
        g.rotate(rad);
        g.translate(-t.pivot[0], -t.pivot[1]);
      }
      const sx = Number.isFinite(boundScale) ? boundScale : t.scale[0];
      const sy = Number.isFinite(boundScale) ? boundScale : t.scale[1];
      if (sx !== 1 || sy !== 1) {
        g.translate(t.pivot[0], t.pivot[1]);
        g.scale(sx, sy);
        g.translate(-t.pivot[0], -t.pivot[1]);
      }

      const st = cmd.bind && cmd.bind.width
        ? { ...cmd.style.stroke, width: Math.max(0, bindNum(cmd.bind.width, cmd.style.stroke.width)) }
        : cmd.style.stroke;
      const fi = cmd.style.fill;

      if (isImage) {
        const entry = imageFor(geom.src);
        if (entry.ready) {
          // 几何是单位方块 (0,0)-(1,1)，尺寸与位置全在矩阵里 ——
          // 这样缩放就是矩阵的线性部分，和 Mark 的印章同一套心智模型。
          g.imageSmoothingEnabled = true;
          g.imageSmoothingQuality = 'high';
          g.drawImage(entry.img, 0, 0, 1, 1);
        } else {
          skipped++;
        }
      } else if (isPoints) {
        const f = flatCoords(geom);
        const baseCol = st.color || fi.color || '#fff';
        const col = Number.isFinite(boundHue)
          ? recolor(baseCol, boundHue, st.alpha)
          : parseColor(baseCol, st.alpha);
        if (f && col) {
          g.fillStyle = col;
          const rr = Math.max(0.5, (cmd.style.pointSize || 3) / 2);
          for (let i = 0; i < f.length; i += 2) {
            g.beginPath();
            g.arc(f[i], f[i + 1], rr, 0, Math.PI * 2);
            g.fill();
          }
        }
      } else {
        // 生长：几何截断，描边和填充一起长。
        // 旧做法（虚线 dash 截断）只有描边会变短，填充永远完整 —— 填充没法"生长"。
        // path 类型（贝塞尔）弧长未知，退化为完整绘制。
        // 轨道带 reveal 序列就按时间取，否则用静态的（Draw Progress 算出来的）
        const trackReveal = cmd.track ? resolveTrackReveal(cmd.track, doc.time || 0, doc.timeline) : null;
        const reveal = trackReveal == null ? cmd.style.reveal : trackReveal;
        let dp = path;
        if (reveal < 1 && (geom.kind === 'polyline' || geom.kind === 'polygon')) {
          dp = this._truncPath(cmd.geom, geom, reveal) || path;
        }

        const fillCol = parseColor(fi.color, fi.alpha);
        if (fillCol) {
          g.fillStyle = fillCol;
          g.fill(dp, fi.rule === 'evenodd' ? 'evenodd' : 'nonzero');
        }
        const strokeCol = Number.isFinite(boundHue)
          ? recolor(st.color, boundHue, st.alpha)
          : parseColor(st.color, st.alpha);
        if (strokeCol) {
          g.strokeStyle = strokeCol;
          g.lineWidth = st.width;
          g.lineCap = st.cap || 'butt';
          g.lineJoin = st.join || 'miter';
          g.setLineDash(Array.isArray(st.dash) && st.dash.length ? st.dash : []);
          g.stroke(dp);
        }
      }
      g.restore();
      drawn++;
    }
    g.setLineDash([]);

    // 动态对象画在静态命令之上（阶段 2；与图层的交错排序留到阶段 3）
    if (this.entitySystem) this.entitySystem.draw(g, doc);

    return { drawn, skipped };
  }

  /** 客户端坐标 → 页面坐标。画布是等比缩放显示的，必须走这一步 */
  clientToPage(clientX, clientY) {
    const r = this.canvas.getBoundingClientRect();
    const { w, h } = this.doc ? this.doc.canvas : { w: 1, h: 1 };
    const sx = w / (r.width || 1);
    const sy = h / (r.height || 1);
    return { x: (clientX - r.left) * sx, y: (clientY - r.top) * sy };
  }

  /** 强制清屏（累积 / 残影模式下由"重置"调用） */
  clear() {
    const doc = this.doc;
    if (!doc) return;
    const { w, h, bg } = doc.canvas;
    const g = this.ctx;
    const dpr = this._dpr || 1;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    if (bg) { g.fillStyle = parseColor(bg); g.fillRect(0, 0, w, h); }
  }

  /**
   * 按区域清屏（阶段 3 C 的 Selective Clear）。
   * 累积 / 残影模式下"擦掉一块再重画"是常见手法；它只作用于像素，不动文档状态，
   * 所以和「重置时间」「重启对象」是三个独立动作（任务书 3E）。
   * 坐标是页面坐标，内部按 DPR 换算。
   */
  clearRegion(x, y, w, h) {
    const g = this.ctx;
    if (!g) return;
    const dpr = this._dpr || 1;
    g.save();
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(x, y, Math.max(0, w), Math.max(0, h));
    g.restore();
  }

  /**
   * 高分辨率 PNG。scale 为相对页面像素的倍数。
   * transparent=true 时不填背景色，只出几何 —— 用于叠到别的画面上。
   */
  async exportPNG(scale = 2, opts = {}) {
    const transparent = opts.transparent === true;
    const mime = opts.mime || 'image/png';
    const doc = this.doc;
    if (!doc) return null;
    const { w, h, bg } = doc.canvas;
    const c = document.createElement('canvas');
    c.width = Math.round(w * scale);
    c.height = Math.round(h * scale);
    const g = c.getContext('2d');
    if (!transparent) {
      if (bg) { g.fillStyle = parseColor(bg); g.fillRect(0, 0, c.width, c.height); }
    }
    g.setTransform(scale, 0, 0, scale, 0, 0);
    this._paint(g);
    return await new Promise((res) => c.toBlob((b) => res(b), mime, 0.95));
  }
}
