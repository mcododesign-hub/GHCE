// doc.js — 文档状态模型（只做状态，不做绘制）
// 一次 patch 的 op 全部应用完毕后才算一帧提交；中途出错按 op 粒度记录，不整体回滚（op 幂等，重放即可恢复）

import { decodeF32 } from './geom.js';
import { exprUsesTime } from './expr.js';

const DEFAULT_CANVAS = { w: 1200, h: 800, bg: '#0b0b0f', dpr: 'auto', clearMode: 'clear', fadeAlpha: 0.08 };
// 没接 Timeline 电池时的兜底：给一段足够长的循环，旧定义不会因为缺少时间轴而停住
const DEFAULT_TIMELINE = { duration: 8, loop: true };
const DEFAULT_LAYER = { id: '', name: '', order: 0, opacity: 1, blend: 'source-over', visible: true, mask: null };
const DEFAULT_STROKE = { color: null, width: 1, cap: 'butt', join: 'miter', dash: null, alpha: 1 };
const DEFAULT_FILL = { color: null, alpha: 1, rule: 'nonzero' };
export const CLEAR_MODES = ['clear', 'fade', 'none'];

function normalizeGeom(v) {
  if (!v || typeof v !== 'object') return null;
  const kind = v.kind;
  if (!['polyline', 'polygon', 'points', 'path'].includes(kind)) return null;
  if (kind === 'path') {
    if (!Array.isArray(v.cmds) || !v.cmds.length) return null;
    return { kind, cmds: v.cmds };
  }
  let coords = null;
  if (v.enc === 'f32') coords = decodeF32(v.coords);
  else if (Array.isArray(v.coords)) coords = Float32Array.from(v.coords);
  if (!coords || coords.length < 2) return null;
  return { kind, coords };
}

function normalizeStyle(s) {
  const st = { stroke: { ...DEFAULT_STROKE }, fill: { ...DEFAULT_FILL }, pointSize: 3, reveal: 1 };
  if (!s || typeof s !== 'object') return st;
  if (s.stroke) Object.assign(st.stroke, s.stroke);
  if (s.fill) Object.assign(st.fill, s.fill);
  if (typeof s.pointSize === 'number') st.pointSize = s.pointSize;
  if (typeof s.reveal === 'number') st.reveal = Math.max(0, Math.min(1, s.reveal));
  return st;
}

/**
 * 属性绑定：{ width: ast, opacity: ast }。
 * 键就是可绑定的属性名，值是一棵表达式树。空对象按"没有绑定"处理，
 * 免得一条空 bind 让 cmd 的 JSON 每帧都不同、把增量 diff 打破。
 */
function normalizeBind(v) {
  if (!v || typeof v !== 'object') return null;
  const out = {};
  for (const key of ['width', 'opacity', 'hue', 'scale']) {
    const e = v[key];
    if (e && typeof e === 'object' && typeof e.o === 'string') out[key] = e;
  }
  return Object.keys(out).length ? out : null;
}

function normalizeCmd(v) {
  if (!v || typeof v !== 'object' || !v.id || !v.geom) return null;
  const t = v.transform || {};
  return {
    id: String(v.id),
    geom: String(v.geom),
    layer: v.layer == null ? null : String(v.layer),
    order: Number.isFinite(v.order) ? v.order : 0,
    visible: v.visible !== false,
    opacity: Number.isFinite(v.opacity) ? v.opacity : 1,
    style: normalizeStyle(v.style),
    transform: {
      // 2D 仿射矩阵 [a,b,c,d,e,f]（GH 的 Transform 投影下来的）。null 表示没有
      matrix: Array.isArray(t.matrix) && t.matrix.length === 6 ? t.matrix.map(Number) : null,
      // 变换进度：0 = 起点，1 = 矩阵终态。GH 永远发 1（终态），
      // 中间值由 tween.js 每帧写回 —— 这就是"变换自己演出来"的那段。
      progress: Number.isFinite(t.progress) ? Math.max(0, Math.min(1, t.progress)) : 1,
      // 过渡起点矩阵（tween 写回；null = 从 identity 出发）
      from: Array.isArray(t.from) && t.from.length === 6 ? t.from.map(Number) : null,
      translate: Array.isArray(t.translate) ? t.translate : [0, 0],
      rotate: Number.isFinite(t.rotate) ? t.rotate : 0,
      scale: Array.isArray(t.scale) ? t.scale : [1, 1],
      pivot: Array.isArray(t.pivot) ? t.pivot : [0, 0],
    },
    // 时间轨（v0.18）：轨道只带区间 + 缓动；时长与循环在 doc.timeline。
    // 有它就是"这条指令自己会动"，没有就走 transform 的 tween。
    track: normalizeTrack(v.track),
    // 几何 morph（v0.19）：N 组同拓扑点缓冲，按时间在相邻帧间插值顶点
    morph: normalizeMorph(v.morph),
    // 运行时表达式绑定（v0.30）：属性名 → AST。绘制时求值，不写回 cmd。
    bind: normalizeBind(v.bind),
  };
}


/**
 * 时间轨。帧序列走 f32+base64，因为 25 条 × 60 帧 × 6 分量 = 9000 个数。
 * 这里只做解码和形状检查；"播到第几帧"由 xform.js 按 clock 算。
 */
function normalizeTrack(t) {
  if (!t || typeof t !== 'object') return null;
  const n = t.n | 0;
  if (n < 2) return null;
  const frames = decodeF32(t.frames);
  const hasFrames = !!frames && frames.length >= n * 6;

  const ex = Array.isArray(t.easeX) ? t.easeX.map(Number) : null;
  const ey = Array.isArray(t.easeY) ? t.easeY.map(Number) : null;
  const ease = ex && ey && ex.length === ey.length && ex.length > 1 ? { x: ex, y: ey } : null;

  const reveal = Array.isArray(t.reveal) && t.reveal.length > 1
    ? Float32Array.from(t.reveal, (x) => Math.max(0, Math.min(1, Number(x) || 0)))
    : null;

  // 只做生长时没有 frames；morph 模式下两者都没有（帧在 cmd.morph 里）。
  // 所以这里不做"必须有内容"的判断 —— 空轨也是合法的，它只提供区间与缓动。

  return {
    n,
    frames: hasFrames ? frames : null,
    reveal,
    // 轨道在全局时间轴上的位置。end <= start 表示"占满整段"，由 trackU 兜底。
    // 时长与循环不在这里 —— 那是 doc.timeline 的事。
    start: Number.isFinite(t.start) ? Math.max(0, t.start) : 0,
    end: Number.isFinite(t.end) ? t.end : -1,
    hidden: t.hidden === true,
    ease,
  };
}

/**
 * 几何 morph：N 组**同拓扑**点缓冲，运行时在相邻帧之间逐顶点插值。
 *
 * 这正是"任意 slider 都能动画"的落地方式 —— 运行时不认识 fillet / offset / 布尔，
 * 只知道"第 k 帧的点在这儿、第 k+1 帧在那儿"。和矩阵插值同构，只是插的对象
 * 从 6 个数变成 2·count 个数。
 *
 * 唯一硬约束：所有帧点数相同。不合格就整条丢掉，宁可不动也不画错。
 */
function normalizeMorph(m) {
  if (!m || typeof m !== 'object') return null;
  const n = m.n | 0;
  const count = m.count | 0;
  if (n < 2 || count < 2) return null;

  const frames = decodeF32(m.frames);
  if (!frames || frames.length < n * count * 2) return null;

  return {
    n,
    count,
    closed: m.closed === true,
    // 每秒帧数：这段素材的自然时长 = (n-1)/step。Track 没给区间时拿它兜底。
    step: Number.isFinite(m.step) && m.step > 0 ? m.step : 12,
    frames,
  };
}

export class Doc {
  constructor() {
    this.canvas = { ...DEFAULT_CANVAS };
    this.layers = new Map();
    this.geoms = new Map();
    this.cmds = new Map();
    // 全局时间轴：只有"整段多长、要不要循环"。具体动画在各条 cmd.track 上。
    this.timeline = { ...DEFAULT_TIMELINE };
    // 运行时变量：事件写、表达式读。跨帧存在，但在 doc.reset 时清空回初值。
    this.vars = new Map();
    this.varsDirty = false;     // 有变量被事件改过 → 下一帧必须重绘
    this._bindingFlag = false;  // cmd 集合变动过 → 重算 timeBinding
    this.timeBinding = false;   // 存在依赖时间的绑定 → 每帧都要重绘
    this.rev = -1;
    this.cursor = 0;          // 已消费到哪一帧
    this.errors = [];
    this._orderDirty = true;
    this._sorted = [];
  }

  /** 应用一帧 patch。返回 { applied, rejected, changedGeom, structural, entityOps } */
  apply(patch) {
    const res = { applied: 0, rejected: 0, changedGeom: new Set(), structural: false, reset: false, entityOps: [], eventOps: [] };
    if (!patch || patch.t !== 'patch') return res;
    if (patch.v !== '0.1') {
      this.errors.push({ code: 'VERSION_MISMATCH', msg: `patch v=${patch.v}` });
      return res;
    }
    // 整帧快照（含 doc.reset）不受 rev 单调性约束：
    // GH 插件重载、场景重建、重连补发都会让 rev 回到低值，此时必须接受而不是丢弃。
    const isSnapshot = patch.snapshot === true || (patch.ops || []).some((o) => o && o.op === 'doc.reset');
    if (!isSnapshot) {
      if (typeof patch.rev === 'number' && patch.rev <= this.rev) return res; // 乱序/重复帧丢弃
    }
    if (typeof patch.rev === 'number') this.rev = patch.rev;

    for (const op of patch.ops || []) {
      const r = this._applyOne(op, res);
      if (r) res.applied++; else res.rejected++;
    }
    if (res.applied) this._orderDirty = true;
    // 绑定是否依赖时间决定"要不要每帧重画"。只在 cmd 集合变动时重算，不逐帧扫。
    if (this._bindingFlag) {
      this._bindingFlag = false;
      this.timeBinding = false;
      for (const c of this.cmds.values()) {
        if (!c.bind) continue;
        for (const k in c.bind) {
          if (exprUsesTime(c.bind[k])) { this.timeBinding = true; break; }
        }
        if (this.timeBinding) break;
      }
    }
    return res;
  }

  _applyOne(op, res) {
    if (!op || typeof op !== 'object') return false;
    switch (op.op) {
      case 'doc.reset': {
        this.layers.clear(); this.geoms.clear(); this.cmds.clear();
        // 换作品必须从默认值重建，再覆盖发来的字段。
        // 早先这里是 Object.assign(this.canvas, op.canvas) —— 没带的字段会保留上一件作品的：
        // 从 fade 场景切到普通场景，clearMode 会一直是 fade，画面开始叠影，
        // 而且 GH 的 Canvas 电池根本不发 clearMode，所以接真 GH 时必现。
        this.canvas = { ...DEFAULT_CANVAS, ...(op.canvas || {}) };
        this.timeline = { ...DEFAULT_TIMELINE };
        this.vars.clear();
        this.varsDirty = false;
        this.timeBinding = false;
        res.structural = true;
        res.reset = true;                          // 换作品 = 时间也该归零
        res.entityOps.push({ op: 'doc.reset' });   // 实体系统也要清空，顺序必须一致
        res.eventOps.push({ op: 'doc.reset' });
        return true;
      }
      // 全局时间轴：整段多长、要不要循环。没有它，各条 Track 的 start/end 无从比较。
      case 'timeline.set': {
        const d = Number(op.duration);
        this.timeline = {
          duration: Number.isFinite(d) && d > 1e-6 ? d : DEFAULT_TIMELINE.duration,
          loop: op.loop !== false,
        };
        return true;
      }
      // 事件触发（阶段 3 D）也是运行时状态，交给 EventTriggers
      case 'event.upsert':
      case 'event.remove':
        res.eventOps.push(op);
        return true;
      // 实体是"运行时状态"，不进 Doc 的静态表；转交 EntitySystem 处理
      case 'entity.upsert':
        if (!op.id || !op.value) return false;
        res.entityOps.push(op);
        return true;
      case 'entity.remove':
        if (!op.id) return false;
        res.entityOps.push(op);
        return true;
      // v0.16：anim.* 已删除。时间不再是一条独立的轨道对象，
      // 而是挂在指令上的 track（见 normalizeTrack）—— 一条指令一份，跟着指令一起增删。
      case 'canvas.set':
        if (!op.value) return false;
        Object.assign(this.canvas, op.value);
        res.structural = true;
        return true;
      case 'layer.upsert': {
        if (!op.value || !op.value.id) return false;
        this.layers.set(String(op.value.id), { ...DEFAULT_LAYER, ...op.value });
        return true;
      }
      case 'layer.remove':
        return this.layers.delete(String(op.id));
      case 'geom.upsert': {
        const g = normalizeGeom(op.value);
        if (!g || !op.id) return false;
        this.geoms.set(String(op.id), g);
        res.changedGeom.add(String(op.id));
        return true;
      }
      case 'geom.remove':
        return this.geoms.delete(String(op.id));
      case 'cmd.upsert': {
        const c = normalizeCmd(op.value);
        if (!c) return false;
        this.cmds.set(c.id, c);
        this._bindingFlag = true;
        return true;
      }
      case 'cmd.remove': {
        const hit = this.cmds.delete(String(op.id));
        this._bindingFlag = true;
        return hit;
      }
      // 运行时变量（v0.30）：事件写、表达式读。
      // 只存数值；语义（哪个事件写它、谁在读它）都在定义里，运行时不需要知道。
      case 'var.upsert': {
        const v = Number(op.value);
        if (!op.id || !Number.isFinite(v)) return false;
        this.vars.set(String(op.id), v);
        return true;
      }
      case 'var.remove':
        return this.vars.delete(String(op.id));
      default:
        this.errors.push({ code: 'OP_UNKNOWN', msg: op.op });
        return false;
    }
  }

  /** 排序后的绘制列表。排序键 (layer.order, cmd.order, cmd.id) —— 保证可重复 */
  drawList() {
    if (!this._orderDirty) return this._sorted;
    const layerOrder = (id) => {
      const l = id == null ? null : this.layers.get(id);
      return l ? l.order : 0;
    };
    const arr = [...this.cmds.values()];
    arr.sort((a, b) => {
      const la = layerOrder(a.layer), lb = layerOrder(b.layer);
      if (la !== lb) return la - lb;
      if (a.order !== b.order) return a.order - b.order;
      return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
    });
    this._sorted = arr;
    this._orderDirty = false;
    return arr;
  }

  stats() {
    return { geoms: this.geoms.size, cmds: this.cmds.size, layers: this.layers.size, rev: this.rev };
  }
}
