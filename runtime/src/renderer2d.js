// renderer2d.js — Canvas 2D 后端
// 只依赖 Doc 的只读视图；不修改文档状态

import { buildPath2D, flatCoords, geomLength, truncatePath2D } from './geom.js';
import { resolveMatrix, resolveTrackMatrix, resolveTrackReveal } from './xform.js';

const DPR_CAP = 2;

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

      const path = geom.kind === 'points' ? null : this._pathFor(cmd.geom, geom);
      if (geom.kind !== 'points' && !path) { skipped++; continue; }

      g.save();
      g.globalAlpha = clamp01((layer.opacity ?? 1) * (cmd.opacity ?? 1));
      g.globalCompositeOperation = layer.blend || 'source-over';

      const t = cmd.transform;
      // 有 track 就用 track：它自己就是时间轴，按 doc.time 取帧。
      // 没有 track 才走 tween：progress < 1 时在 identity 与终态之间插值 —— "让 PSR 演出来"。
      const m = cmd.track ? resolveTrackMatrix(cmd.track, doc.time || 0) : resolveMatrix(t);
      if (m) g.transform(m[0], m[1], m[2], m[3], m[4], m[5]);
      if (t.translate[0] || t.translate[1]) g.translate(t.translate[0], t.translate[1]);
      if (t.rotate) {
        // 协议里 rotate 用「度」（人读得懂），这里换算成弧度
        const rad = (t.rotate * Math.PI) / 180;
        g.translate(t.pivot[0], t.pivot[1]);
        g.rotate(rad);
        g.translate(-t.pivot[0], -t.pivot[1]);
      }
      if (t.scale[0] !== 1 || t.scale[1] !== 1) {
        g.translate(t.pivot[0], t.pivot[1]);
        g.scale(t.scale[0], t.scale[1]);
        g.translate(-t.pivot[0], -t.pivot[1]);
      }

      const st = cmd.style.stroke;
      const fi = cmd.style.fill;

      if (geom.kind === 'points') {
        const f = flatCoords(geom);
        const col = parseColor(st.color || fi.color || '#fff', st.alpha);
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
        const trackReveal = cmd.track ? resolveTrackReveal(cmd.track, doc.time || 0) : null;
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
        const strokeCol = parseColor(st.color, st.alpha);
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
