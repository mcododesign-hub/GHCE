// geom.js — 几何解码与 Path2D 构建
// 坐标系约定：页面像素，Y 向下（GH 侧已完成投影）

const B64_RE = /^[A-Za-z0-9+/]*={0,2}$/;

/** base64 -> Float32Array（little-endian） */
export function decodeF32(b64) {
  if (typeof b64 !== 'string' || !B64_RE.test(b64)) return null;
  const bin = atob(b64);
  const len = bin.length;
  if (len % 4 !== 0) return null;
  const buf = new ArrayBuffer(len);
  const u8 = new Uint8Array(buf);
  for (let i = 0; i < len; i++) u8[i] = bin.charCodeAt(i);
  return new Float32Array(buf);
}

/**
 * Float32Array -> base64（decodeF32 的逆）。
 * 导出独立 HTML 时要靠它把已解码的几何/帧序列写回协议格式，
 * 否则 JSON.stringify 一个 Float32Array 会得到 {"0":..,"1":..} 这种废数据。
 */
export function encodeF32(arr) {
  if (!arr) return null;
  const f = arr instanceof Float32Array ? arr : Float32Array.from(arr);
  const bytes = new Uint8Array(f.buffer, f.byteOffset, f.byteLength);
  let bin = '';
  const CH = 0x8000;             // 分段拼，避免 apply 参数过多
  for (let i = 0; i < bytes.length; i += CH) {
    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CH));
  }
  return btoa(bin);
}

/** 统一取出几何的扁平坐标 [x0,y0,x1,y1,...]，用于计数与导出 */
export function flatCoords(g) {
  if (!g) return null;
  if (g.coords instanceof Float32Array) return g.coords;
  if (Array.isArray(g.coords)) return Float32Array.from(g.coords);
  return null;
}

export function pointCount(g) {
  if (!g) return 0;
  if (g.kind === 'path') {
    let n = 0;
    for (const c of g.cmds || []) {
      if (c[0] === 'M' || c[0] === 'L') n += 1;
      else if (c[0] === 'C') n += 1;
      else if (c[0] === 'Q') n += 1;
    }
    return n;
  }
  const f = flatCoords(g);
  return f ? (f.length / 2) | 0 : 0;
}

/** 构建 Path2D。返回 null 表示无法构建（调用方应计入 invalid） */
export function buildPath2D(g) {
  if (!g) return null;
  const p = new Path2D();

  if (g.kind === 'path') {
    const cmds = g.cmds || [];
    if (!cmds.length) return null;
    for (const c of cmds) {
      const op = c[0];
      if (op === 'M') p.moveTo(c[1], c[2]);
      else if (op === 'L') p.lineTo(c[1], c[2]);
      else if (op === 'C') p.bezierCurveTo(c[1], c[2], c[3], c[4], c[5], c[6]);
      else if (op === 'Q') p.quadraticCurveTo(c[1], c[2], c[3], c[4]);
      else if (op === 'Z') p.closePath();
    }
    return p;
  }

  const f = flatCoords(g);
  if (!f || f.length < 2) return null;

  if (g.kind === 'points') return null; // 点集走 fillRect/arc 路径，不进 Path2D

  p.moveTo(f[0], f[1]);
  for (let i = 2; i < f.length; i += 2) p.lineTo(f[i], f[i + 1]);
  if (g.kind === 'polygon') p.closePath();
  return p;
}

/** 几何指纹：与 GH 侧 FNV-1a 同算法，用于判断是否需要重传 */
export function fingerprint(g) {
  if (!g) return 0;
  let h = 0x811c9dc5;
  const mix = (n) => {
    h ^= n & 0xff; h = Math.imul(h, 0x01000193);
    h ^= (n >>> 8) & 0xff; h = Math.imul(h, 0x01000193);
    h ^= (n >>> 16) & 0xff; h = Math.imul(h, 0x01000193);
    h ^= (n >>> 24) & 0xff; h = Math.imul(h, 0x01000193);
  };
  if (g.kind === 'path') {
    for (const c of g.cmds || []) {
      for (let i = 0; i < c.length; i++) {
        if (i === 0) mix(c[0].charCodeAt(0));
        else mix(Math.round(c[i] * 1000));
      }
    }
  } else {
    mix(0); // kind 分隔
    const f = flatCoords(g);
    if (f) for (let i = 0; i < f.length; i++) mix(Math.round(f[i] * 1000));
  }
  return h >>> 0;
}

/** 折线总长（用于 Stroke Reveal 的虚线切分），结果缓存在几何对象上 */
export function geomLength(g) {
  if (g._len != null) return g._len;
  const f = flatCoords(g);
  let L = 0;
  if (f && f.length >= 4) {
    // 相邻两点的差：x 是 f[i]-f[i-2]，y 必须是 f[i+1]-f[i-1]。
    // 写成 f[i-3] 时 i=2 会取到 f[-1]（undefined），整条长度变 NaN 并被 _len 缓存住，
    // 之后 truncatePath2D 每次都判 total 不合法返回 null —— 生长静默失效（v0.16 踩过）。
    for (let i = 2; i < f.length; i += 2) L += Math.hypot(f[i] - f[i - 2], f[i + 1] - f[i - 1]);
    if (g.kind === 'polygon') L += Math.hypot(f[0] - f[f.length - 2], f[1] - f[f.length - 1]);
  }
  g._len = L;
  return L;
}

/** 包围盒（页面坐标），用于画布自适应与 SVG 导出 */
export function bounds(g) {
  const f = flatCoords(g);
  if (!f || f.length < 2) return null;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (let i = 0; i < f.length; i += 2) {
    const x = f[i], y = f[i + 1];
    if (x < x0) x0 = x; if (y < y0) y0 = y;
    if (x > x1) x1 = x; if (y > y1) y1 = y;
  }
  return { x0, y0, x1, y1 };
}

/**
 * 按弧长把折线截断到 u（0..1），返回 Path2D。
 *
 * 为什么不用虚线（setLineDash）做 reveal：虚线只影响描边，**填充永远是完整的**。
 * 几何截断对描边和填充一视同仁，这才是"曲线生长"。
 */
export function truncatePath2D(g, u) {
  const f = flatCoords(g);
  if (!f || f.length < 4) return null;

  const n = f.length / 2;
  const total = geomLength(g);
  if (!(total > 0)) return null;

  const target = Math.max(0, Math.min(1, u)) * total;
  const p = new Path2D();
  p.moveTo(f[0], f[1]);

  let acc = 0;
  for (let i = 1; i < n; i++) {
    const x0 = f[(i - 1) * 2], y0 = f[(i - 1) * 2 + 1];
    const x1 = f[i * 2], y1 = f[i * 2 + 1];
    const seg = Math.hypot(x1 - x0, y1 - y0);
    if (acc + seg >= target) {
      const t = seg <= 1e-9 ? 0 : (target - acc) / seg;
      p.lineTo(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
      return p;
    }
    p.lineTo(x1, y1);
    acc += seg;
  }

  // 多边形还有一条收口边，长度里算了它就必须截到它
  if (g.kind === 'polygon') {
    const x0 = f[(n - 1) * 2], y0 = f[(n - 1) * 2 + 1];
    const x1 = f[0], y1 = f[1];
    const seg = Math.hypot(x1 - x0, y1 - y0);
    const t = seg <= 1e-9 ? 1 : Math.min(1, (target - acc) / seg);
    p.lineTo(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
  }
  return p;
}
