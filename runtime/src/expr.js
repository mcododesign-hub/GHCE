// expr.js — Runtime Expression：运行时求值的表达式树
//
// 为什么是 AST 而不是 JS 字符串：GH 传来的东西一律当数据看，
// 不 eval、不 new Function。白名单之外的操作一律返回 NaN，由调用方回退到静态值。
//
// 节点形状（尽量短，定义只发一次，不走每帧）：
//   { o:'const', v }                       常量
//   { o:'var',   n, i? }                   读变量；i 是声明时的初值（可省）
//   { o:'time' }                           运行时时钟（秒）
//   { o:'state', n }                       实例状态（只在实体行为里有效）
//   { o:'dt' }                             固定步长（秒）
//   { o:'add'|'sub'|'mul'|'div'|'min'|'max'|'gt'|'lt', a, b }   二元（gt/lt 输出 1 或 0）
//   { o:'select', c, a, b }                                         条件选择（c 非 0 取 a）
//   { o:'sin'|'abs'|'clamp01'|'noise', a }                          一元
//
// 求值上下文：{ t, dt, vars, state }。vars 是 Map(name -> number)，
// state 是实体实例（x/y/vx/vy/u/age/index/size/alpha/aux/gen）。

const MAX_DEPTH = 32;

const num = (x, fallback) => (Number.isFinite(x) ? x : fallback);

/** 求值。非法节点 / 未知算子返回 NaN —— 调用方据此保留静态值，不写脏数据。 */
export function evalExpr(node, ctx, depth = 0) {
  if (!node || typeof node !== 'object' || depth > MAX_DEPTH) return NaN;
  const t = ctx && Number.isFinite(ctx.t) ? ctx.t : 0;
  const vars = ctx && ctx.vars ? ctx.vars : null;

  switch (node.o) {
    case 'const':
      return num(Number(node.v), NaN);

    case 'time':
      return t;

    // 固定步长：行为里做积分必须用它。用 time 差分等于把帧率写进物理，
    // 掉一帧就永久多走/少走一段，导出的作品在慢机器上会不一样。
    case 'dt':
      return ctx && Number.isFinite(ctx.dt) ? ctx.dt : 0;

    case 'state': {
      const st = ctx && ctx.state;
      if (!st) return NaN;
      const v = st[String(node.n || '')];
      return Number.isFinite(v) ? v : NaN;
    }

    case 'var': {
      const name = String(node.n || '');
      if (vars && vars.has(name)) return num(Number(vars.get(name)), 0);
      // 没被声明过就用节点自带的初值；再退到 0。
      return num(Number(node.i), 0);
    }

    case 'add': return evalExpr(node.a, ctx, depth + 1) + evalExpr(node.b, ctx, depth + 1);
    case 'sub': return evalExpr(node.a, ctx, depth + 1) - evalExpr(node.b, ctx, depth + 1);
    case 'mul': return evalExpr(node.a, ctx, depth + 1) * evalExpr(node.b, ctx, depth + 1);
    case 'div': {
      const d = evalExpr(node.b, ctx, depth + 1);
      return Math.abs(d) < 1e-9 ? NaN : evalExpr(node.a, ctx, depth + 1) / d;
    }
    case 'min': return Math.min(evalExpr(node.a, ctx, depth + 1), evalExpr(node.b, ctx, depth + 1));
    case 'max': return Math.max(evalExpr(node.a, ctx, depth + 1), evalExpr(node.b, ctx, depth + 1));

    // 比较输出 1/0 而不是布尔：整棵树只处理数值，省得在运行时里做类型分支
    case 'gt': return evalExpr(node.a, ctx, depth + 1) > evalExpr(node.b, ctx, depth + 1) ? 1 : 0;
    case 'lt': return evalExpr(node.a, ctx, depth + 1) < evalExpr(node.b, ctx, depth + 1) ? 1 : 0;

    case 'select': {
      const c = evalExpr(node.c, ctx, depth + 1);
      return Number.isFinite(c) && c !== 0
        ? evalExpr(node.a, ctx, depth + 1)
        : evalExpr(node.b, ctx, depth + 1);
    }

    case 'sin': return Math.sin(evalExpr(node.a, ctx, depth + 1));
    case 'abs': return Math.abs(evalExpr(node.a, ctx, depth + 1));
    case 'clamp01': {
      const v = evalExpr(node.a, ctx, depth + 1);
      return Number.isFinite(v) ? Math.max(0, Math.min(1, v)) : NaN;
    }

    // 一维值噪声：确定性、无状态，和 signal.js 的 Perlin 同源但更便宜。
    // 输出 0..1，够做"随位置粗细变化"这类静态扰动，不承担每帧动画。
    case 'noise': return noise01(evalExpr(node.a, ctx, depth + 1));

    default:
      return NaN;
  }
}

/** 整数 → 0..1，同一输入永远同一输出（value noise 的骨架） */
function hash01(i) {
  let h = Math.imul(i ^ 0x9e3779b9, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

/** 一维值噪声：格点用 hash，格内用 smoothstep 过渡，输出 0..1 */
export function noise01(x) {
  if (!Number.isFinite(x)) return NaN;
  const i = Math.floor(x);
  const f = x - i;
  const u = f * f * (3 - 2 * f);
  return hash01(i) * (1 - u) + hash01(i + 1) * u;
}

/** 这棵表达式是否依赖时间 —— 只有依赖时间的绑定才需要每帧重绘。 */
export function exprUsesTime(node, depth = 0) {
  if (!node || typeof node !== 'object' || depth > MAX_DEPTH) return false;
  if (node.o === 'time') return true;
  for (const k of ['a', 'b', 'c']) {
    if (exprUsesTime(node[k], depth + 1)) return true;
  }
  return false;
}

/**
 * 收集表达式里声明过的变量（name -> init）。
 * PatchBuilder 用它把 `var.upsert` 发出来 —— 变量只有真正被用到才进文档。
 */
export function exprVars(node, out = new Map(), depth = 0) {
  if (!node || typeof node !== 'object' || depth > MAX_DEPTH) return out;
  if (node.o === 'var' && node.n) {
    const name = String(node.n);
    const init = Number(node.i);
    if (!out.has(name)) out.set(name, Number.isFinite(init) ? init : 0);
  }
  for (const k of ['a', 'b', 'c']) exprVars(node[k], out, depth + 1);
  return out;
}
