// xform.js — 2D 仿射的"分解 → 插值 → 合成"
//
// 为什么需要它：Transform 电池给出的 matrix 是**终态**（旋转 45° 之后的矩阵）。
// 直接应用就只有终态，画面看起来"卡在最终位置"，没有从原状变过去的过程。
// 要让一个变换"演"出来，必须能在"上一次的矩阵"与"这一次的矩阵"之间取值 ——
// 而线性插值六个矩阵分量是错的（旋转 180° 时中点会塌成零缩放），
// 所以先用 TRS 分解，再对 rot/scale/translate 分别插值。
//
// from 的来源是 tween.js：它记着这条指令上一次的矩阵。没有 from（第一次出现）时
// 从 identity 出发，等价于"从原位长过去"。
//
// 只处理 TRS（Rhino 的 Transform 电池经 P·M·P⁻¹ 共轭后不含 shear）。
// p ≥ 1 时原样返回传入的矩阵，不做往返换算，避免浮点漂移。

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
const TAU = Math.PI * 2;

/** [a,b,c,d,e,f] → { tx, ty, rot, sx, sy }。rot 单位弧度 */
export function decompose(m) {
  const [a, b, c, d, e, f] = m;
  const sx = Math.hypot(a, b);
  const det = a * d - b * c;
  const sy = sx === 0 ? 1 : det / sx;
  return { tx: e, ty: f, rot: Math.atan2(b, a), sx, sy };
}

/** { tx, ty, rot, sx, sy } → [a,b,c,d,e,f]，与 ctx.transform 同序 */
export function compose({ tx = 0, ty = 0, rot = 0, sx = 1, sy = 1 }) {
  const cos = Math.cos(rot), sin = Math.sin(rot);
  return [cos * sx, sin * sx, -sin * sy, cos * sy, tx, ty];
}

export const IDENTITY = [1, 0, 0, 1, 0, 0];

/** 角度差取最短弧：170° → −170° 应该走 20°，不是 340° */
export function shortestArc(from, to) {
  let d = (to - from) % TAU;
  if (d > Math.PI) d -= TAU;
  if (d < -Math.PI) d += TAU;
  return d;
}

/**
 * 把 cmd.transform 解析成"当前该用的矩阵"。
 * progress 缺省 1（= 终态）；0 = from（没有 from 就是 identity）；0..1 = 从 from 演到 matrix。
 *
 * 关键：**不能**只分解成 rot/scale/translate 再各自插值。
 * 一个"绕自身中心转 45°"的矩阵，其平移分量里藏着的正是锚点（= 这个仿射映射的不动点）。
 * 只插值 rot 和 translate，等于让物体一边绕页面原点转、一边沿直线平移 ——
 * 看起来就是"锚点不对"和"中间在抖"。所以这里先解出不动点 C，把插值写成
 *
 *     M(p) = T(t_p) · T(C_p) · R(θ_p) · S(s_p) · T(−C_p)
 *
 * C 不动，θ/s 变 —— 这才是绕同一根轴转。
 */
export function resolveMatrix(t) {
  const m = t && t.matrix;
  if (!m) return null;
  const p = t.progress == null ? 1 : clamp01(t.progress);
  if (p >= 1) return m;

  const hasFrom = t.from && t.from.length === 6;
  if (p <= 0) return hasFrom ? t.from : IDENTITY;

  const to = decomposeAffine(m);
  // 起点是 identity 时，锚点直接取终态的锚点：这样"从原始状态转过去"是绕自己的中心转，
  // 而不是绕页面原点转半圈再飞过来。
  const from = hasFrom
    ? decomposeAffine(t.from)
    : { rot: 0, sx: 1, sy: 1, cx: to.cx, cy: to.cy, tx: 0, ty: 0 };

  return composeAffine({
    rot: from.rot + shortestArc(from.rot, to.rot) * p,
    sx: from.sx + (to.sx - from.sx) * p,
    sy: from.sy + (to.sy - from.sy) * p,
    cx: from.cx + (to.cx - from.cx) * p,
    cy: from.cy + (to.cy - from.cy) * p,
    tx: from.tx + (to.tx - from.tx) * p,
    ty: from.ty + (to.ty - from.ty) * p,
  });
}

/**
 * 仿射 → { rot, sx, sy, cx, cy, tx, ty }，其中 (cx,cy) 是不动点（锚点）。
 *
 * 记线性部分 A、平移 τ，则 M(p) = A·p + τ。不动点满足 C = A·C + τ，即 (I−A)C = τ。
 *   · (I−A) 可逆（有任何旋转或缩放）→ 解出唯一的 C，残余平移 t = 0
 *   · (I−A) 奇异（纯平移，A = I）→ 没有唯一不动点，取 C = 0，平移全放进 t
 * 纯平移时"锚点"本来就没有意义，所以这个分支不影响观感。
 */
export function decomposeAffine(m) {
  const [a, b, c, d, e, f] = m;
  const sx = Math.hypot(a, b);
  const det = a * d - b * c;
  const sy = sx === 0 ? 1 : det / sx;
  const rot = Math.atan2(b, a);

  const i11 = 1 - a, i12 = -c, i21 = -b, i22 = 1 - d;
  const detI = i11 * i22 - i12 * i21;
  if (Math.abs(detI) > 1e-9) {
    return {
      rot, sx, sy,
      cx: (i22 * e - i12 * f) / detI,
      cy: (-i21 * e + i11 * f) / detI,
      tx: 0, ty: 0,
    };
  }
  return { rot, sx, sy, cx: 0, cy: 0, tx: e, ty: f };
}

/** decomposeAffine 的逆 */
export function composeAffine({ rot = 0, sx = 1, sy = 1, cx = 0, cy = 0, tx = 0, ty = 0 }) {
  const l = compose({ rot, sx, sy });
  const [a, b, c, d] = l;
  return [
    a, b, c, d,
    cx - (a * cx + c * cy) + tx,
    cy - (b * cx + d * cy) + ty,
  ];
}

/** 给测试/自检用：矩阵在 progress 下的等价写法 */
export function matrixAt(m, p, from = null) {
  return resolveMatrix({ matrix: m, progress: p, from });
}

/* ---------------- 时间轨（v0.16） ----------------
 *
 * Track 收的是"一串矩阵"而不是"关键帧 + 字段"。区别：
 *   关键帧 + Field  → 我预设了能驱动哪些属性（reveal/rotate/move），你的动作被塞进我的枚举
 *   一串矩阵        → 这 N 帧是你在 GH 里算的，我只是按时间播
 *
 * 播放参数只有三个，都是时间本身的属性，不是效果：Duration / Loop / Ease。
 */

/** 缓动表：x/y 都已归一化到 0..1，按 x 二分查找后线性插值 */
export function sampleEase(ease, u) {
  if (!ease || !ease.x || !ease.x.length || !ease.y || ease.y.length !== ease.x.length) return u;
  const xs = ease.x, ys = ease.y;
  const k = clamp01(u);
  if (k <= xs[0]) return ys[0];
  if (k >= xs[xs.length - 1]) return ys[ys.length - 1];
  let lo = 0, hi = xs.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (xs[mid] <= k) lo = mid; else hi = mid;
  }
  const span = xs[hi] - xs[lo] || 1;
  return ys[lo] + (ys[hi] - ys[lo]) * ((k - xs[lo]) / span);
}

/** 时间轨 → 归一化进度 u（已过缓动）。矩阵和 reveal 共用同一个 u，两者才不会不同步 */
export function trackU(track, time) {
  if (!track) return 0;
  const d = track.duration > 0 ? track.duration : 1;
  const raw = time / d;
  const p = track.loop ? ((raw % 1) + 1) % 1 : clamp01(raw);
  return clamp01(sampleEase(track.ease, p));
}

/** 时间轨 → 此刻该用的矩阵。time 单位秒 */
export function resolveTrackMatrix(track, time) {
  if (!track || !track.frames || track.n < 2) return null;
  const u = trackU(track, time);
  const f = track.frames;
  const pos = u * (track.n - 1);
  const i0 = Math.floor(pos);
  const i1 = Math.min(track.n - 1, i0 + 1);
  const frac = pos - i0;

  const A = [f[i0 * 6], f[i0 * 6 + 1], f[i0 * 6 + 2], f[i0 * 6 + 3], f[i0 * 6 + 4], f[i0 * 6 + 5]];
  if (frac <= 1e-6 || i1 === i0) return A;
  const B = [f[i1 * 6], f[i1 * 6 + 1], f[i1 * 6 + 2], f[i1 * 6 + 3], f[i1 * 6 + 4], f[i1 * 6 + 5]];

  // 帧间插值仍走不动点分解：相邻帧的旋转中心不会漂，中点角度也是真中点
  return resolveMatrix({ matrix: B, from: A, progress: frac });
}

/**
 * 时间轨 → 此刻的 reveal（0..1）。没有 reveal 序列就返回 null（表示"这条轨道不管生长"）。
 * 生长必须走这里而不是 dash：dash 只能截断描边，填充永远是完整的。
 */
export function resolveTrackReveal(track, time) {
  const r = track && track.reveal;
  if (!r || r.length < 2) return null;
  const u = trackU(track, time);
  const pos = u * (r.length - 1);
  const i0 = Math.floor(pos);
  const i1 = Math.min(r.length - 1, i0 + 1);
  const frac = pos - i0;
  return clamp01(r[i0] + (r[i1] - r[i0]) * frac);
}
