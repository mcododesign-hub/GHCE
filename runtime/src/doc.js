// doc.js — 文档状态模型（只做状态，不做绘制）
// 一次 patch 的 op 全部应用完毕后才算一帧提交；中途出错按 op 粒度记录，不整体回滚（op 幂等，重放即可恢复）

import { decodeF32 } from './geom.js';

const DEFAULT_CANVAS = { w: 1200, h: 800, bg: '#0b0b0f', dpr: 'auto', clearMode: 'clear', fadeAlpha: 0.08 };
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
    // 时间轨（v0.16）：帧序列 + 时长 + 循环 + 缓动曲线。
    // 有它就是"这条指令自己会动"，没有就走 transform 的 tween。
    track: normalizeTrack(v.track),
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

  // 只做生长时没有 frames —— 这也是一条合法的时间轨
  if (!hasFrames && !reveal) return null;

  return {
    n,
    frames: hasFrames ? frames : null,
    reveal,
    duration: Number.isFinite(t.duration) && t.duration > 0 ? t.duration : 1,
    loop: t.loop !== false,
    ease,
  };
}

export class Doc {
  constructor() {
    this.canvas = { ...DEFAULT_CANVAS };
    this.layers = new Map();
    this.geoms = new Map();
    this.cmds = new Map();
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
    return res;
  }

  _applyOne(op, res) {
    if (!op || typeof op !== 'object') return false;
    switch (op.op) {
      case 'doc.reset': {
        this.layers.clear(); this.geoms.clear(); this.cmds.clear();
        if (op.canvas) Object.assign(this.canvas, op.canvas);
        else this.canvas = { ...DEFAULT_CANVAS };
        res.structural = true;
        res.reset = true;                          // 换作品 = 时间也该归零
        res.entityOps.push({ op: 'doc.reset' });   // 实体系统也要清空，顺序必须一致
        res.eventOps.push({ op: 'doc.reset' });
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
        return true;
      }
      case 'cmd.remove':
        return this.cmds.delete(String(op.id));
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
