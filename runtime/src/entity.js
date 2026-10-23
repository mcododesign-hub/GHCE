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
import { evalExpr } from './expr.js';

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

const WRITABLE = new Set(['x', 'y', 'vx', 'vy', 'u', 'size', 'alpha', 'aux']);

/**
 * 状态程序（v0.32）。三个部分都可选：
 *   steps[] 每步执行一次的写入（顺序即执行顺序）
 *   spawn   条件上升沿生成子代
 *   destroy 条件非 0 时移除，可留 N 个残影
 *
 * `ownsU` / `ownsXY` 是"写谁谁归你"的开关：写了 u 就不再自增，
 * 写了 x/y 就不再从曲线取样。否则自定义运动会被沿曲线跑的那一步覆盖掉。
 */
function normalizeProgram(p) {
  if (!p || typeof p !== 'object') return null;

  const steps = Array.isArray(p.steps)
    ? p.steps
        .filter((s) => s && s.slot && s.expr)
        .map((s) => ({
          slot: String(s.slot),
          op: s.op === 'add' ? 'add' : 'set',
          expr: s.expr,
          when: s.when || null,
        }))
    : [];

  const sp = p.spawn;
  const spawn = sp && sp.when ? {
    when: sp.when,
    count: Math.max(1, Math.min(64, sp.count | 0 || 1)),
    spread: Number.isFinite(sp.spread) ? sp.spread : 0,
    maxGen: Math.max(0, Math.min(64, sp.maxGen | 0)),
    maxPop: Math.max(0, Math.min(20000, sp.maxPop | 0)),
  } : null;

  const de = p.destroy;
  const destroy = de && de.when ? {
    when: de.when,
    keep: Math.max(0, Math.min(2000, de.keep | 0)),
  } : null;

  if (!steps.length && !spawn && !destroy) return null;

  return {
    steps,
    spawn,
    destroy,
    ownsU: steps.some((s) => s.slot === 'u'),
    ownsXY: steps.some((s) => s.slot === 'x' || s.slot === 'y'),
  };
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
      bp.ghosts.length = 0;
      bp.nextIndex = 0;
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
      program: normalizeProgram(v.program),
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
      // 同一份定义：只更新非身份字段，实例状态全部保留。
      // program 不在 key 里 —— 拖一根滑条改系数不该把粒子群清空重来。
      existing.def = { ...existing.def, ...def };
      this._resize(existing);
      return;
    }
    // 定义变了：重建（Restart 语义），几何变了不算定义变
    const bp = { def, key, list: [], ghosts: [], nextIndex: 0 };
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
    if ((bp.nextIndex | 0) <= index) bp.nextIndex = index + 1;
    const n = Math.max(1, d.count);
    const life = d.lifetime > 0 ? d.lifetime : Infinity;
    return {
      index,
      u: ((index / n) + (d.phase || 0)) % 1,
      speed: d.speed,
      age: life === Infinity ? 0 : (index / n) * life,
      life,
      size: d.size,
      // 状态程序的槽位。没有程序时它们只是闲置字段，不影响旧行为。
      vx: 0, vy: 0, alpha: 1, aux: 0, gen: 0, spawned: false,
      placed: false,
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

  /** 固定步长手动推进：不经过累加器，所以结果与帧率无关（测试与离屏导出用）。 */
  step(doc, steps = 1) {
    for (let i = 0; i < steps; i++) this._step(doc, FIXED_DT);
  }

  /**
   * 一个固定步。
   *
   * 有两种运动：
   *   1. 没有 program —— v0.15 的"沿曲线跑"，行为完全不变；
   *   2. 有 program —— 先跑内置的那一步（除非程序自己写了 u / x / y），
   *      再按顺序执行写入，最后判定销毁与分裂。
   */
  _step(doc, dt) {
    this.time += dt;
    this.frame++;
    let alive = 0, total = 0;

    for (const bp of this.blueprints.values()) {
      const d = bp.def;
      const prog = d.program;
      const sampler = this._samplerFor(d, doc);
      // 没有程序就必须有曲线；程序自己写了 x/y 的话，曲线可以没有
      if (!sampler && !(prog && prog.ownsXY)) continue;

      const list = bp.list;
      total += list.length;
      const next = [];
      const pop = { n: list.length };

      for (let i = 0; i < list.length; i++) {
        const e = list[i];
        e.age += dt;

        if (e.age >= e.life) {
          // 寿命到了：身份不变，按确定性公式重建（与 v0.15 一致）
          const fresh = this._makeEntity(bp, i);
          fresh.age = 0;
          next.push(fresh);
          this.respawns++;
          continue;
        }

        if (sampler && !e.placed) {
          const p0 = sampler.pointAt(e.u);
          e.x = p0.x;
          e.y = p0.y;
          e.placed = true;
        }

        if (prog) {
          if (!prog.ownsU) e.u = (e.u + e.speed * dt) % 1;
          if (!prog.ownsXY && sampler) {
            const p = sampler.pointAt(e.u);
            e.x = p.x;
            e.y = p.y;
          }

          const { ctx, killed } = this._runSteps(e, prog, doc, dt);

          if (killed) {
            if (prog.destroy.keep > 0) {
              bp.ghosts.push({ x: e.x, y: e.y, size: e.size, alpha: e.alpha });
              while (bp.ghosts.length > prog.destroy.keep) bp.ghosts.shift();
            }
            pop.n--;
            continue;
          }

          if (prog.spawn) this._spawn(bp, e, prog, ctx, next, pop);

          if (e.age < e.life) alive++;
        } else {
          e.u = (e.u + e.speed * dt) % 1;
          const p = sampler.pointAt(e.u);
          e.x = p.x;
          e.y = p.y;
          if (e.age < e.life) alive++;
        }

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

        next.push(e);
      }

      bp.list = next;
    }

    this.stats.blueprints = this.blueprints.size;
    this.stats.entities = total;
    this.stats.alive = alive;
    this.stats.respawns = this.respawns;
  }

  /**
   * 执行状态程序里的写入，返回本步的求值上下文与"是否该销毁"。
   *
   * 上下文只建一次，spawn / destroy 复用同一份 —— 否则同一步里
   * `when` 与写入读到的 astate 可能不是同一个时刻的值。
   */
  _runSteps(e, prog, doc, dt) {
    const ctx = { t: this.time, dt, vars: doc.vars, state: e };

    for (const st of prog.steps) {
      if (st.when) {
        const c = evalExpr(st.when, ctx);
        if (!Number.isFinite(c) || c === 0) continue;
      }
      const v = evalExpr(st.expr, ctx);
      if (!Number.isFinite(v) || !WRITABLE.has(st.slot)) continue;
      e[st.slot] = st.op === 'add' ? e[st.slot] + v : v;
    }

    if (e.size < 0) e.size = 0;
    if (e.alpha < 0) e.alpha = 0;
    else if (e.alpha > 1) e.alpha = 1;

    let killed = false;
    if (prog.destroy) {
      const c = evalExpr(prog.destroy.when, ctx);
      killed = Number.isFinite(c) && c !== 0;
    }
    return { ctx, killed };
  }

  /**
   * 上升沿生成子代。子代**默认继承父代全部状态**（速度/尺寸/透明度/aux），
   * 只有 age 归零、gen+1、index 递增 —— "变得不一样"由表达式读 index / gen 自己算，
   * 引擎不掷骰子。
   */
  _spawn(bp, e, prog, ctx, next, pop) {
    const sp = prog.spawn;
    const c = evalExpr(sp.when, ctx);
    const on = Number.isFinite(c) && c !== 0;

    if (!on) { e.spawned = false; return 0; }
    if (e.spawned) return 0;          // 条件持续为真时只生成一次
    e.spawned = true;

    if (sp.maxGen > 0 && e.gen >= sp.maxGen) return 0;
    if (sp.maxPop > 0 && pop.n + sp.count > sp.maxPop) return 0;
    if (pop.n + sp.count > 20000) return 0;

    const d = bp.def;
    for (let k = 0; k < sp.count; k++) {
      const a = (k / sp.count) * Math.PI * 2 + e.gen * 0.7;
      next.push({
        index: bp.nextIndex++,
        u: e.u, speed: e.speed, life: e.life,
        age: 0,
        size: e.size,
        vx: e.vx, vy: e.vy, alpha: e.alpha, aux: e.aux,
        gen: e.gen + 1, spawned: false, placed: true,
        x: e.x + Math.cos(a) * sp.spread,
        y: e.y + Math.sin(a) * sp.spread,
        trail: d.trail > 0 ? new Float32Array(d.trail * 2) : null,
        trailCount: 0, trailHead: 0,
      });
      pop.n++;
    }
    return sp.count;
  }

  draw(ctx, doc) {
    for (const bp of this.blueprints.values()) {
      const d = bp.def;
      if (!d.visible || (!bp.list.length && !bp.ghosts.length)) continue;

      const layer = d.layer ? doc.layers.get(d.layer) : null;
      if (layer && layer.visible === false) continue;

      ctx.save();
      const base = (layer ? (layer.opacity ?? 1) : 1) * d.opacity;
      ctx.globalAlpha = base;
      if (layer && layer.blend) ctx.globalCompositeOperation = layer.blend;
      ctx.fillStyle = d.color;
      ctx.strokeStyle = d.color;

      for (const e of bp.list) {
        const fade = d.fade ? Math.max(0, 1 - e.age / e.life) : 1;

        if (e.trail && e.trailCount > 1) {
          const n = e.trailCount;
          const cap = e.trail.length / 2;
          // 从旧到新画：越旧越透明
          ctx.globalAlpha = base * 0.5;
          ctx.beginPath();
          for (let k = 0; k < n; k++) {
            const idx = (e.trailHead - n + k + cap) % cap;
            const x = e.trail[idx * 2], y = e.trail[idx * 2 + 1];
            if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.lineWidth = Math.max(0.4, e.size * 0.5);
          ctx.stroke();
        }

        ctx.globalAlpha = base * fade * (e.alpha == null ? 1 : e.alpha);
        this._paintInstance(ctx, d, e.x, e.y, e.size);
      }

      // 残影：被销毁实例的最后状态，不参与步进、不进 patch
      if (bp.ghosts.length) {
        for (const g of bp.ghosts) {
          ctx.globalAlpha = base * 0.35 * (g.alpha == null ? 1 : g.alpha);
          this._paintInstance(ctx, d, g.x, g.y, g.size);
        }
      }

      ctx.restore();
    }
  }

  /** 画一个实例（形状或方块点）。残影与实体共用，避免两份绘制代码走形。 */
  _paintInstance(ctx, d, x, y, size) {
    if (d.shape) {
      // 形状是"最长边归一化成 1"的局部折线，所以这里乘 Size 就是最终像素大小。
      // 不 ctx.scale()：那样 lineWidth 也会被缩放，Width 就不是像素了。
      const pts = d.shape.coords;
      const n = pts.length / 2;
      ctx.beginPath();
      for (let k = 0; k < n; k++) {
        const px = x + pts[k * 2] * size;
        const py = y + pts[k * 2 + 1] * size;
        if (k === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
      }
      if (d.shape.closed) { ctx.closePath(); ctx.fill(); }
      ctx.lineWidth = Math.max(0.2, d.width);
      ctx.stroke();
    } else {
      const r = size;
      ctx.fillRect(x - r, y - r, r * 2, r * 2);
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
      gens: bp.list.slice(0, 8).map((e) => e.gen || 0),
      ghosts: bp.ghosts.length,
      hasProgram: !!bp.def.program,
    };
  }
}
