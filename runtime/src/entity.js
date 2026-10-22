// entity.js — 动态对象系统
//
// 设计要点：
//   1. 身份 = (blueprintId, index)，不依赖数组位置。删掉中间一个不会让别人换人。
//   2. 定义与实例分离：GH 发的是 blueprint，浏览器持有实例状态（age/u/trail）。
//   3. 几何变化不重启实例：曲线被 GH 改了，粒子继续跑，只是路径换了 —— 靠 fingerprint 里不含几何内容。
//   4. 固定步长：模拟用 1/60 累加，不受显示器刷新率和 GH 求解节奏影响。
//   5. 生命周期：create / preserve / restart / remove，由 reconcile 明确区分。
//
// v0.15：**没有随机，没有行为预设。**
//   - 分布是确定的：第 i 个粒子 u = (i / count + phase) % 1
//   - 只有一种运动：沿蓝图里那条曲线跑。drift / orbit / attract / repel / follow-pointer 全部删除，
//     它们是"看起来能玩"的演示，而不是能复用的工具。
//   - 粒子形状由 GH 给的一条曲线决定（局部坐标，最长边归一化成 1）

import { flatCoords, decodeF32 } from './geom.js';

export const FIXED_DT = 1 / 60;

// v0.15：mulberry32 随机数生成器已删除。粒子的分布、速度、寿命全部是确定式，
// 没有种子、没有抖动 —— 要变化就在 GH 里算，而不是让引擎替你掷骰子。

const lerp = (a, b, t) => a + (b - a) * t;

/** 把折线几何变成按弧长参数化的采样器（u ∈ [0,1]） */
class Sampler {
  constructor(coords, closed) {
    const n = coords.length / 2;
    this.pts = new Float32Array(closed ? (n + 1) * 2 : coords.length);
    this.pts.set(coords);
    if (closed) {
      this.pts[n * 2] = coords[0];
      this.pts[n * 2 + 1] = coords[1];
    }
    const count = this.pts.length / 2;
    this.cum = new Float32Array(count);
    let total = 0;
    for (let i = 1; i < count; i++) {
      const dx = this.pts[i * 2] - this.pts[(i - 1) * 2];
      const dy = this.pts[i * 2 + 1] - this.pts[(i - 1) * 2 + 1];
      total += Math.hypot(dx, dy);
      this.cum[i] = total;
    }
    this.total = total || 1;
  }

  pointAt(u) {
    const target = ((u % 1) + 1) % 1 * this.total;
    // 二分找到区间
    let lo = 0, hi = this.cum.length - 1;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (this.cum[mid] < target) lo = mid + 1; else hi = mid;
    }
    const i = Math.max(1, lo);
    const prev = this.cum[i - 1];
    const seg = this.cum[i] - prev || 1;
    const t = (target - prev) / seg;
    return {
      x: lerp(this.pts[(i - 1) * 2], this.pts[i * 2], t),
      y: lerp(this.pts[(i - 1) * 2 + 1], this.pts[i * 2 + 1], t),
    };
  }
}

/** blueprint 的"身份指纹"：刻意不包含几何内容，只描述实例的生存规则 */
function blueprintKey(v) {
  return [v.count, v.speed, v.lifetime, v.phase, v.trail || 0].join('|');
}

/**
 * 唯一保留的运动语义：沿蓝图里那条曲线跑。
 * v0.15 删掉了 drift / orbit / attract / repel / follow-pointer —— 那五个是"下拉里看起来能玩"的
 * 演示预设，实测除了 follow-curve 都没有可复用性。
 */
export const BEHAVIORS = ['follow-curve'];

/**
 * 粒子形状：GH 发来的是"局部坐标 + 最长边归一化成 1"的折线。
 * 归一化在 GH 侧做，所以 Size 一个端口就能表示"相对原曲线多大"，跟画布缩放无关。
 */
function shapeOf(s) {
  if (!s) return null;
  const coords = s.coords instanceof Float32Array ? s.coords
    : Array.isArray(s.coords) ? Float32Array.from(s.coords)
      : decodeF32(s.coords);
  if (!coords || coords.length < 4) return null;
  return { coords, closed: s.kind === 'polygon' || s.closed === true };
}

export class EntitySystem {
  constructor() {
    this.blueprints = new Map();   // id -> { def, key, list, samplerKey }
    this.samplers = new Map();     // geomKey -> Sampler
    this.time = 0;
    this.acc = 0;
    this.frame = 0;
    this.respawns = 0;
    this.stats = { blueprints: 0, entities: 0, alive: 0, steps: 0, stepMs: 0, respawns: 0 };
  }

  clear() {
    this.blueprints.clear();
    this.samplers.clear();
    this.time = 0;
    this.acc = 0;
    this.frame = 0;
  }

  /** 重启全部实例（时间归零 + 按种子重新生成），画布内容不动 */
  restart() {
    this.time = 0;
    this.acc = 0;
    this.frame = 0;
    this.respawns = 0;
    for (const bp of this.blueprints.values()) {
      bp.list.length = 0;
      this._resize(bp);
    }
  }

  /** 应用一帧里的 entity.* / doc.reset */
  applyOps(ops, doc) {
    if (!ops || !ops.length) return;
    for (const op of ops) {
      if (!op || typeof op !== 'object') continue;
      if (op.op === 'doc.reset') { this.clear(); continue; }
      if (op.op === 'entity.upsert') this._upsert(op.id, op.value, doc);
      else if (op.op === 'entity.remove') this.blueprints.delete(String(op.id));
    }
  }

  _upsert(id, v, doc) {
    if (!id || !v) return;
    id = String(id);
    const key = blueprintKey(v);
    const def = {
      id,
      geom: v.geom || null,
      layer: v.layer || null,
      order: v.order || 0,
      count: Math.max(0, Math.min(20000, v.count | 0)),
      speed: typeof v.speed === 'number' ? v.speed : 0.06,
      lifetime: typeof v.lifetime === 'number' ? v.lifetime : 7,
      phase: typeof v.phase === 'number' ? v.phase : 0,
      trail: Math.max(0, Math.min(32, v.trail | 0)),
      size: typeof v.size === 'number' ? v.size : 1.8,
      width: typeof v.width === 'number' ? v.width : 1,
      shape: shapeOf(v.shape),
      color: v.color || '#FD7AB4',
      fade: v.fade !== false,
      visible: v.visible !== false,
      opacity: typeof v.opacity === 'number' ? v.opacity : 1,
    };

    const existing = this.blueprints.get(id);
    if (existing && existing.key === key) {
      // 同一份定义：只更新非身份字段，实例状态全部保留
      existing.def = { ...existing.def, ...def };
      this._resize(existing);
      return;
    }
    // 定义变了：重建（Restart 语义），几何变了不算定义变
    const bp = { def, key, list: [] };
    this.blueprints.set(id, bp);
    this._resize(bp);
  }

  _resize(bp) {
    const want = bp.def.count;
    const list = bp.list;
    while (list.length > want) list.pop();          // remove（从尾部，不影响前面的身份）
    while (list.length < want) list.push(this._makeEntity(bp, list.length));
  }

  /**
   * 生成第 index 个实例。**全程无随机**：
   *   - u     = (index / count + phase) % 1    均匀铺在曲线上
   *   - age   = (index / count) * life        错开重生时刻（否则所有粒子同时消失同时出现）
   * 想每个粒子不一样，就在 GH 里给不同参数/多条蓝图，而不是让引擎掷骰子。
   */
  _makeEntity(bp, index) {
    const d = bp.def;
    const n = Math.max(1, d.count);
    const life = d.lifetime > 0 ? d.lifetime : Infinity;
    return {
      index,
      u: ((index / n) + (d.phase || 0)) % 1,
      speed: d.speed,
      age: life === Infinity ? 0 : (index / n) * life,
      life,
      size: d.size,
      x: 0, y: 0,
      trail: d.trail > 0 ? new Float32Array(d.trail * 2) : null,
      trailCount: 0,
      trailHead: 0,
    };
  }

  _samplerFor(def, doc) {
    if (!def.geom) return null;
    const geom = doc.geoms.get(def.geom);
    if (!geom) return null;
    const cached = this.samplers.get(def.geom);
    if (cached && cached.geom === geom) return cached.sampler;
    const coords = flatCoords(geom);
    if (!coords || coords.length < 4) return null;
    const sampler = new Sampler(coords, geom.kind === 'polygon' || geom.closed === true);
    this.samplers.set(def.geom, { geom, sampler });
    return sampler;
  }

  /**
   * 整组相位（不动身份指纹，因此不会重启实例）。
   * 这是给"时间"用的唯一入口：轨道把时间写进 phase，整组粒子就沿曲线往前挪。
   * 不是"行为预设"，就是 Blueprint 上那个 Phase 端口的运行时写入。
   */
  setPhase(id, v) {
    const bp = this.blueprints.get(String(id));
    if (!bp) return;
    bp.def.phase = v;
    const list = bp.list;
    const n = Math.max(1, bp.def.count);
    for (let i = 0; i < list.length; i++) list[i].u = ((i / n) + v) % 1;
  }

  /** 交互指针（阶段 3 D）。行为里用到 pointer 的会读它 */
  setPointer(p) {
    this.pointer = p;
  }

  /**
   * 真实时间 → 固定步长推进。返回本帧推进了多少步 */
  update(doc, realDtSec) {
    const t0 = performance.now();
    this.acc += Math.min(0.25, Math.max(0, realDtSec));
    let steps = 0;
    while (this.acc >= FIXED_DT && steps < 8) {
      this._step(doc, FIXED_DT);
      this.acc -= FIXED_DT;
      steps++;
    }
    this.stats.stepMs = performance.now() - t0;
    this.stats.steps = steps;
    return steps;
  }

  _step(doc, dt) {
    this.time += dt;
    this.frame++;
    let alive = 0, total = 0;

    for (const bp of this.blueprints.values()) {
      const d = bp.def;
      total += bp.list.length;          // 先计数：重生分支会 continue，不能漏计

      const sampler = this._samplerFor(d, doc);
      if (!sampler) continue;           // 没有曲线就没有运动 —— 这是唯一语义

      for (let i = 0; i < bp.list.length; i++) {
        const e = bp.list[i];
        e.age += dt;

        if (e.age >= e.life) {
          // Restart：身份不变，状态按同一个确定性公式重建（不是重新掷骰子）
          const fresh = this._makeEntity(bp, i);
          fresh.age = 0;
          bp.list[i] = fresh;
          this.respawns++;
          continue;
        }

        e.u = (e.u + e.speed * dt) % 1;
        const p = sampler.pointAt(e.u);
        e.x = p.x;
        e.y = p.y;

        if (e.trail) {
          // 每 3 个物理步记一个采样点：同样的拖尾长度，视觉上是 3 倍时间跨度，
          // 否则 60Hz 下 10 个采样点只有 1/6 秒，看起来就是散落的短线。
          e.trailTick = (e.trailTick || 0) + 1;
          if (e.trailTick % 3 === 0) {
            e.trail[e.trailHead * 2] = e.x;
            e.trail[e.trailHead * 2 + 1] = e.y;
            e.trailHead = (e.trailHead + 1) % (e.trail.length / 2);
            if (e.trailCount < e.trail.length / 2) e.trailCount++;
          }
        }

        if (e.age < e.life) alive++;
      }
    }

    this.stats.blueprints = this.blueprints.size;
    this.stats.entities = total;
    this.stats.alive = alive;
    this.stats.respawns = this.respawns;
  }
  draw(ctx, doc) {
    for (const bp of this.blueprints.values()) {
      const d = bp.def;
      if (!d.visible || !bp.list.length) continue;

      const layer = d.layer ? doc.layers.get(d.layer) : null;
      if (layer && layer.visible === false) continue;

      ctx.save();
      ctx.globalAlpha = (layer ? (layer.opacity ?? 1) : 1) * d.opacity;
      if (layer && layer.blend) ctx.globalCompositeOperation = layer.blend;
      ctx.fillStyle = d.color;
      ctx.strokeStyle = d.color;

      for (const e of bp.list) {
        const fade = d.fade ? Math.max(0, 1 - e.age / e.life) : 1;

        if (e.trail && e.trailCount > 1) {
          const n = e.trailCount;
          const cap = e.trail.length / 2;
          // 从旧到新画：越旧越透明
          ctx.globalAlpha = (layer ? (layer.opacity ?? 1) : 1) * d.opacity * 0.5;
          ctx.beginPath();
          for (let k = 0; k < n; k++) {
            const idx = (e.trailHead - n + k + cap) % cap;
            const x = e.trail[idx * 2], y = e.trail[idx * 2 + 1];
            if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.lineWidth = Math.max(0.4, e.size * 0.5);
          ctx.stroke();
        }

        ctx.globalAlpha = (layer ? (layer.opacity ?? 1) : 1) * d.opacity * fade;

        if (d.shape) {
          // 形状是"最长边归一化成 1"的局部折线，所以这里乘 Size 就是最终像素大小。
          // 不 ctx.scale()：那样 lineWidth 也会被缩放，Width 就不是像素了。
          const sc = e.size;
          const pts = d.shape.coords;
          const n = pts.length / 2;
          ctx.beginPath();
          for (let k = 0; k < n; k++) {
            const x = e.x + pts[k * 2] * sc;
            const y = e.y + pts[k * 2 + 1] * sc;
            if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          if (d.shape.closed) { ctx.closePath(); ctx.fill(); }
          ctx.lineWidth = Math.max(0.2, d.width);
          ctx.stroke();
        } else {
          const r = e.size;
          ctx.fillRect(e.x - r, e.y - r, r * 2, r * 2);
        }
      }
      ctx.restore();
    }
  }

  /** 给状态面板 / 测试用 */
  snapshot(id) {
    const bp = this.blueprints.get(id);
    if (!bp) return null;
    return {
      id,
      count: bp.list.length,
      ages: bp.list.slice(0, 5).map((e) => +e.age.toFixed(2)),
      us: bp.list.slice(0, 5).map((e) => +e.u.toFixed(3)),
      xs: bp.list.slice(0, 5).map((e) => Math.round(e.x)),
      pts: bp.list.slice(0, 8).map((e) => [Math.round(e.x), Math.round(e.y)]),
    };
  }
}
