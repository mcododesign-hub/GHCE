// mock-gh.mjs — 模拟 Grasshopper 侧的桥。
// 目的：在没有 Rhino 的机器上，用同一份协议驱动运行时，使 M0/M1 可在浏览器里验收。
// 它替换的是「GH 求解 + PatchBuilder」，因此这里刻意按 GH 的语义做事：
//   1. 先算完整文档状态，2. 与上次发出的状态做 diff，3. 只发送变化的 op。

import { WebSocketServer } from 'ws';

const PORT = Number(process.env.PORT || 8777);
const PATH = '/colugo';

/* ---------------- 几何工具 ---------------- */

function b64u32(arr) {
  const u = new Uint32Array(arr);
  const bytes = new Uint8Array(u.buffer);
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}

function b64f32(arr) {
  const f = Float32Array.from(arr);
  return Buffer.from(f.buffer, f.byteOffset, f.byteLength).toString('base64');
}

const lined = (id, kind, pts) => ({ id, value: { kind, enc: 'f32', coords: b64f32(pts) } });

function mulberry(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const W = 1440, H = 900;

/* ---------------- 场景：static（M0 的 GH Curves Viewer） ---------------- */

function sceneStatic() {
  const layers = [
    { id: 'plate', name: 'plate', order: 0 },
    { id: 'contour', name: 'contour', order: 10 },
    { id: 'accent', name: 'accent', order: 20 },
    { id: 'dots', name: 'dots', order: 30 },
  ];
  const geoms = new Map();
  const cmds = [];
  const rnd = mulberry(20261022);

  // 底板：一个满版 polygon
  geoms.set('g_plate', { kind: 'polygon', enc: 'f32', coords: b64f32([0, 0, W, 0, W, H, 0, H]) });
  cmds.push({
    id: 'c_plate', geom: 'g_plate', layer: 'plate', order: 0,
    style: { fill: { color: '#0e0e13', alpha: 1 }, stroke: { width: 0 } },
  });

  // 70 条同心扰动环 —— 干涉形成摩尔纹，是这版构图的骨架
  const CX = W / 2, CY = H / 2;
  for (let i = 0; i < 70; i++) {
    const r0 = 42 + i * 5.1;
    const pts = [];
    for (let s = 0; s < 288; s++) {
      const t = (s / 288) * Math.PI * 2;
      const rr = r0 * (1 + 0.055 * Math.sin(5 * t + i * 0.11) + 0.028 * Math.sin(11 * t - i * 0.23));
      pts.push(CX + rr * Math.cos(t), CY + rr * Math.sin(t) * 0.86);
    }
    geoms.set(`g_f${i}`, { kind: 'polygon', enc: 'f32', coords: b64f32(pts) });
    cmds.push({
      id: `c_f${i}`, geom: `g_f${i}`, layer: i % 9 === 4 ? 'accent' : 'contour', order: i,
      style: {
        stroke: {
          color: i % 9 === 4 ? '#FD7AB4' : '#E8E6DF',
          width: i % 9 === 4 ? 1.3 : 0.8,
          cap: 'round', join: 'round', alpha: i % 9 === 4 ? 0.55 : 0.30 + (i / 70) * 0.35,
        },
      },
    });
  }

  // 24 条放射弦线（plotter 的"刻线"感）
  for (let i = 0; i < 24; i++) {
    const a = (i / 24) * Math.PI * 2 + 0.11;
    const r = 470 + (i % 3) * 26;
    geoms.set(`g_l${i}`, { kind: 'polyline', enc: 'f32', coords: b64f32([CX, CY, CX + r * Math.cos(a), CY + r * Math.sin(a) * 0.86]) });
    cmds.push({
      id: `c_l${i}`, geom: `g_l${i}`, layer: 'accent', order: 100 + i,
      style: { stroke: { color: i % 4 === 0 ? '#07A954' : '#FD7AB4', width: 0.9, alpha: 0.28 } },
    });
  }

  // 一条外圈虚线边界
  const ring = [];
  for (let s = 0; s < 200; s++) {
    const t = (s / 200) * Math.PI * 2;
    ring.push(CX + 700 * Math.cos(t), CY + 700 * Math.sin(t) * 0.86);
  }
  geoms.set('g_ring', { kind: 'polygon', enc: 'f32', coords: b64f32(ring) });
  cmds.push({ id: 'c_ring', geom: 'g_ring', layer: 'accent', order: 200, style: { stroke: { color: '#07A954', width: 1.1, dash: [2, 9], alpha: 0.6 } } });

  // 点集
  const pts = [];
  for (let i = 0; i < 420; i++) pts.push(rnd() * W, rnd() * H);
  geoms.set('g_dots', { kind: 'points', enc: 'f32', coords: b64f32(pts) });
  cmds.push({ id: 'c_dots', geom: 'g_dots', layer: 'dots', order: 0, style: { stroke: { color: '#F2F2F2', alpha: 0.5 }, pointSize: 1.6 } });

  return { canvas: { w: W, h: H, bg: '#0b0b0f', dpr: 'auto' }, layers, geoms, cmds };
}

/* ---------------- 场景：mutate（GH 曲线参数持续变化） ---------------- */

function sceneMutate() {
  const layers = [
    { id: 'contour', name: 'contour', order: 10 },
    { id: 'accent', name: 'accent', order: 20 },
  ];
  const geoms = new Map();
  const cmds = [];
  const N = 24;
  for (let i = 0; i < N; i++) {
    const gid = `g_m${i}`;
    geoms.set(gid, { kind: 'polyline', enc: 'f32', coords: b64f32([0, 0, 1, 1]) });
    cmds.push({
      id: `c_m${i}`, geom: gid, layer: i % 6 === 0 ? 'accent' : 'contour', order: i,
      style: {
        stroke: {
          color: i % 6 === 0 ? '#07A954' : '#E8E6DF',
          width: 1 + (i / N) * 1.4, cap: 'round', join: 'round', alpha: 0.8,
        },
      },
    });
  }
  return { canvas: { w: W, h: H, bg: '#0b0b0f', dpr: 'auto' }, layers, geoms, cmds };
}

function mutateTick(state, tSec) {
  const N = 24;
  for (let i = 0; i < N; i++) {
    const k = i / N;
    const a = 190 + k * 460;
    const b = 90 + k * 330;
    const f = 1 + (i % 5);
    const pts = [];
    const steps = 180;
    for (let s = 0; s <= steps; s++) {
      const t = (s / steps) * Math.PI * 2;
      pts.push(
        W / 2 + a * Math.cos(f * t + tSec * (0.35 + k * 0.5)),
        H / 2 + b * Math.sin((f + 1) * t + tSec * 0.22) * 0.94
      );
    }
    state.geoms.set(`g_m${i}`, { kind: 'polyline', enc: 'f32', coords: b64f32(pts) });
  }
  // 只改样式、不动几何：验证「几何与绘图表达可独立修改」
  const idx = Math.floor(tSec * 2) % N;
  const cmd = state.cmds.find((c) => c.id === `c_m${idx}`);
  cmd.style.stroke.color = Math.floor(tSec * 2) % 4 === 0 ? '#FD7AB4' : '#E8E6DF';
}

/* ---------------- 场景：stress（500 路径） ---------------- */

function sceneStress() {
  const geoms = new Map();
  const cmds = [];
  const N = 500, PTS = 40;
  for (let i = 0; i < N; i++) {
    const pts = new Array(PTS * 2).fill(0);
    geoms.set(`g_s${i}`, { kind: 'polyline', enc: 'f32', coords: b64f32(pts) });
    cmds.push({
      id: `c_s${i}`, geom: `g_s${i}`, layer: 'contour', order: i,
      style: { stroke: { color: '#E8E6DF', width: 0.75, alpha: 0.55, cap: 'round' } },
    });
  }
  return {
    canvas: { w: W, h: H, bg: '#0b0b0f', dpr: 'auto' },
    layers: [{ id: 'contour', name: 'contour', order: 10 }],
    geoms, cmds,
  };
}

function stressTick(state, tSec) {
  const N = 500, PTS = 40;
  for (let i = 0; i < N; i++) {
    const k = i / N;
    const pts = [];
    const ox = 60 + (k * (W - 120));
    for (let s = 0; s <= PTS; s++) {
      const u = s / PTS;
      pts.push(ox + Math.sin(u * Math.PI * 2 + tSec * 1.4 + k * 9) * 26 * (0.3 + k), 80 + u * (H - 160));
    }
    state.geoms.set(`g_s${i}`, { kind: 'polyline', enc: 'f32', coords: b64f32(pts) });
  }
}

/* ---------------- 场景：entities（阶段 2 · 动态对象） ---------------- */

const ENTITY_PALETTE = ['#FD7AB4', '#07A954', '#E8E6DF', '#7DD3FC'];
const ENTITY_LOOPS = 6;
const ENTITY_PER_LOOP = 320;

function loopPoints(i, phase) {
  const cx = 260 + (i % 3) * 460;
  const cy = 250 + Math.floor(i / 3) * 400;
  const r = 130 + (i % 3) * 22;
  const pts = [];
  const N = 96;
  for (let s = 0; s < N; s++) {
    const t = (s / N) * Math.PI * 2;
    const rr = r * (1 + 0.16 * Math.sin(3 * t + phase + i) + 0.07 * Math.sin(5 * t - phase));
    pts.push(cx + rr * Math.cos(t), cy + rr * Math.sin(t) * 0.86);
  }
  return pts;
}

function sceneEntities() {
  const geoms = new Map();
  const entities = [];

  // 一个三角形，当作粒子形状：验证"形状由 GH 给的曲线决定"，坐标为归一化的局部坐标
  const triangle = {
    kind: 'polygon', enc: 'f32',
    coords: b64f32([0, -0.5, 0.5, 0.5, -0.5, 0.5]),
  };

  for (let i = 0; i < ENTITY_LOOPS; i++) {
    geoms.set(`g_loop${i}`, { kind: 'polygon', enc: 'f32', coords: b64f32(loopPoints(i, 0)) });
    entities.push({
      id: `e_loop${i}`,
      value: {
        geom: `g_loop${i}`,
        layer: 'particles',
        order: i,
        count: ENTITY_PER_LOOP,
        speed: 0.02 + i * 0.008,
        lifetime: i === 0 ? 0 : 6 + i,
        phase: i * 0.1,
        trail: 5,
        size: 7,
        width: 1,
        shape: i % 2 === 0 ? triangle : null,
        color: ENTITY_PALETTE[i % ENTITY_PALETTE.length],
        fade: true,
      },
    });
  }
  return {
    canvas: { w: W, h: H, bg: '#08080c', dpr: 'auto' },
    layers: [{ id: 'particles', name: 'particles', order: 10, opacity: 1, blend: 'lighter' }],
    geoms,
    cmds: [],
    entities,
  };
}

/** 只改几何、不动实体定义：用来看"GH 改曲线后粒子状态是否保留" */
function driftEntities(state, tSec) {
  for (let i = 0; i < ENTITY_LOOPS; i++) {
    state.geoms.set(`g_loop${i}`, { kind: 'polygon', enc: 'f32', coords: b64f32(loopPoints(i, tSec * 0.8)) });
  }
}

/* ---------------- 场景：living（阶段 3 · 逐步绘制 + 残影 + 多行为） ---------------- */

const LIVING_LINES = 8;

function livingPoints(i, t) {
  const pts = [];
  const N = 160;
  const y0 = 90 + i * ((H - 180) / (LIVING_LINES - 1));
  for (let s = 0; s <= N; s++) {
    const u = s / N;
    const x = 70 + u * (W - 140);
    const y = y0 + Math.sin(u * Math.PI * 2.2 + i * 0.8 + t * 0.15) * (10 + i * 3)
      + Math.sin(u * Math.PI * 7 + i) * 4;
    pts.push(x, y);
  }
  return pts;
}

function sceneLiving() {
  const geoms = new Map();
  const cmds = [];
  const entities = [];
  const NF = 25;                 // 时间轨的帧数（GH 里就是 Series 的长度）

  // 绕 (cx,cy) 转一个角度的页面矩阵
  const rotFrame = (deg, cx, cy) => {
    const a = (deg * Math.PI) / 180;
    const c = Math.cos(a), s2 = Math.sin(a);
    return [c, s2, -s2, c, cx - (c * cx - s2 * cy), cy - (s2 * cx + c * cy)];
  };
  const shiftFrame = (dy) => [1, 0, 0, 1, 0, dy];

  for (let i = 0; i < LIVING_LINES; i++) {
    const pts = livingPoints(i, 0);
    geoms.set(`g_ln${i}`, { kind: 'polyline', enc: 'f32', coords: b64f32(pts) });

    // 该行的中心（旋转锚点）
    let cx = 0, cy = 0;
    for (let k = 0; k < pts.length; k += 2) { cx += pts[k]; cy += pts[k + 1]; }
    cx /= pts.length / 2; cy /= pts.length / 2;

    // v0.18：轨道只声明"我占全局时间轴的哪一段"，时长与循环归 Timeline。
    // 所以三条轨可以在一条 6 秒轴上错开 —— 这是"每条轨自带 duration"做不到的。
    const track = { n: NF };

    if (i === 0) {
      // 0–2s：纯生长，只给 reveal 序列，不给任何矩阵
      track.start = 0; track.end = 2;
      track.reveal = Array.from({ length: NF }, (_, k) => k / (NF - 1));
      // 自定义缓动曲线（等价于 GH 里接一条 Curve 到 Ease）
      track.easeX = Array.from({ length: 33 }, (_, k) => k / 32);
      track.easeY = track.easeX.map((u) => Math.sin(u * Math.PI * 0.5) ** 0.7);
    } else if (i === 1) {
      // 1.5–4.5s：一圈旋转。帧序列自己转，浏览器只负责播
      track.start = 1.5; track.end = 4.5;
      track.frames = b64f32(Array.from({ length: NF }, (_, k) => rotFrame((360 * k) / (NF - 1), cx, cy)).flat());
    } else if (i === 2) {
      // 3.5–6s：上下摆动 0 → 48 → 0
      track.start = 3.5; track.end = 6;
      track.frames = b64f32(Array.from({ length: NF }, (_, k) => {
        const u = k / (NF - 1);
        return shiftFrame(Math.sin(u * Math.PI * 2) * 48);
      }).flat());
    }

    cmds.push({
      id: `c_ln${i}`, geom: `g_ln${i}`, layer: 'ink', order: i,
      style: {
        stroke: { color: i % 4 === 3 ? '#FD7AB4' : '#E8E6DF', width: 1.1, cap: 'round', join: 'round', alpha: 0.85 },
        reveal: i === 0 ? 0 : 1,
      },
      track,
    });
  }

  // 同一套语义（沿曲线跑）用不同参数表达三种观感：
  // 相位错开 / 速度不同 / 寿命无限。这替代了原来 drift+orbit+attract 三个行为预设。
  entities.push({
    id: 'e_slow',
    value: {
      geom: 'g_ln0', layer: 'dust', order: 0, count: 30,
      speed: 0.04, lifetime: 0, phase: 0,
      trail: 10, size: 6, width: 1.2, shape: null, color: '#07A954', fade: true,
    },
  });
  entities.push({
    id: 'e_fast',
    value: {
      geom: 'g_ln1', layer: 'dust', order: 1, count: 24,
      speed: 0.12, lifetime: 0, phase: 0.5,
      trail: 8, size: 5, width: 1.2, shape: null, color: '#FD7AB4', fade: true,
    },
  });
  entities.push({
    id: 'e_life',
    value: {
      geom: 'g_ln2', layer: 'dust', order: 2, count: 60,
      speed: 0.08, lifetime: 9, phase: 0.25,
      trail: 6, size: 4, width: 1.2, shape: null, color: '#7DD3FC', fade: true,
    },
  });

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto', clearMode: 'fade', fadeAlpha: 0.07 },
    // 全局时间轴：6 秒一循环。三条轨各自占其中一段，见上面的 start/end。
    timeline: { duration: 6, loop: true },
    layers: [
      { id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' },
      { id: 'dust', name: 'dust', order: 10, opacity: 1, blend: 'lighter' },
    ],
    geoms, cmds, entities,
  };
}

/* ---------------- v0.19 几何 morph：方 → 圆 ---------------- */

/**
 * 同一个"东西"的 N 个时刻，点数完全一致 —— 运行时在相邻两帧之间逐顶点插值。
 * 这里刻意用"方 → 圆"这种非仿射的变化：它没法用矩阵表达，但顶点仍能一一对应。
 */
function sceneMorph() {
  const N = 24, COUNT = 96, R = 260;
  const frames = [];

  for (let k = 0; k < N; k++) {
    const t = k / (N - 1);
    for (let i = 0; i < COUNT; i++) {
      const a = (i / COUNT) * Math.PI * 2 - Math.PI / 2;
      const ca = Math.cos(a), sa = Math.sin(a);

      const circleX = ca * R, circleY = sa * R;
      // 方形：把圆按 max(|cos|,|sin|) 归一化，等价于"外接方"
      const m = Math.max(Math.abs(ca), Math.abs(sa)) || 1e-9;
      const squareX = (ca / m) * R, squareY = (sa / m) * R;

      frames.push(
        W / 2 + circleX * t + squareX * (1 - t),
        H / 2 + circleY * t + squareY * (1 - t),
      );
    }
  }

  const geoms = new Map();
  // 第一帧当 fallback geom：morph 被判为不合格时仍有一条合法路径可画
  geoms.set('g_morph', { kind: 'polygon', enc: 'f32', coords: b64f32(frames.slice(0, COUNT * 2)) });

  const cmds = [{
    id: 'c_morph', geom: 'g_morph', layer: 'ink', order: 0,
    style: { stroke: { color: '#FD7AB4', width: 1.6, cap: 'round', join: 'round', alpha: 0.95 } },
    // 轨道只提供区间：0–3s 完成变形；帧序列由 morph 自带
    track: { n: 2, frames: null, reveal: null, start: 0, end: 3, hidden: false, easeX: null, easeY: null },
    morph: { n: N, count: COUNT, step: 12, closed: true, frames: b64f32(frames) },
  }];

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto', clearMode: 'clear' },
    timeline: { duration: 4, loop: true },
    layers: [{ id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' }],
    geoms, cmds, entities: [],
  };
}

/* ---------------- v0.23 逐帧驱动：模拟 GH 里 noise -> Rotate -> Transform ---------------- */

/** 页面坐标下绕 (cx,cy) 旋转 deg 度的 2D 仿射 [a,b,c,d,e,f] */
function rotMatrix(deg, cx, cy) {
  const r = (deg * Math.PI) / 180, c = Math.cos(r), s = Math.sin(r);
  return [c, s, -s, c, cx - c * cx + s * cy, cy - s * cx - c * cy];
}

/**
 * GH 侧的 noise 输出一个实时变化的数，接进 Rotate，Transform 每帧发一个新矩阵。
 * 这里模拟这个数据流：矩阵以约 30 Hz 更新。
 *
 * 关键行为：间隔小于 120ms 时运行时**直通**（这一帧给什么就画什么），
 * 不叠 0.9 秒的过渡缓动 —— 否则每帧都在重新缓动，看起来是抖而不是转。
 */
function sceneRandom() {
  const geoms = new Map();
  const cmds = [];

  // 不对称三角形：旋转时包围盒会变，肉眼能看出在动
  const tri = [0, -170, 150, 120, -150, 90];
  geoms.set('g_tri', {
    kind: 'polygon', enc: 'f32',
    coords: b64f32(tri.map((v, i) => (i % 2 === 0 ? W / 2 + v : H / 2 + v))),
  });

  cmds.push({
    id: 'c_tri', geom: 'g_tri', layer: 'ink', order: 0,
    style: { stroke: { color: '#FD7AB4', width: 2, cap: 'round', join: 'round', alpha: 1 } },
    transform: { matrix: rotMatrix(0, W / 2, H / 2), progress: 1 },
  });

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto', clearMode: 'fade', fadeAlpha: 0.12 },
    timeline: { duration: 8, loop: true },
    layers: [{ id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' }],
    geoms, cmds, entities: [],
  };
}

/** 每帧换一个矩阵 —— 等价于 GH 每次求解输出新的 Xform */
function randomTick(state, tSec) {
  const cmd = state.cmds[0];
  cmd.transform.matrix = rotMatrix(Math.sin(tSec * 1.6) * 32, W / 2, H / 2);
}


/* ---------------- v0.25 三维：网格 + 线框立方体 ---------------- */

/** 起伏平面：n×n 四边形，三角化后 2n² 个面 */
function buildGrid(n, size, amp) {
  const pos = [];
  for (let j = 0; j <= n; j++) {
    for (let i = 0; i <= n; i++) {
      const x = (i / n - 0.5) * size;
      const y = (j / n - 0.5) * size;
      const z = Math.sin(i * 0.55) * Math.cos(j * 0.42) * amp;
      pos.push(x, y, z);
    }
  }
  const idx = [];
  for (let j = 0; j < n; j++) {
    for (let i = 0; i < n; i++) {
      const a = j * (n + 1) + i;
      const b = a + 1;
      const c = a + n + 1;
      const d = c + 1;
      idx.push(a, b, d, a, d, c);
    }
  }
  return { pos, idx };
}

/** 单位立方体，12 个三角 */
function buildCube(size) {
  const h = size / 2;
  const pos = [];
  const idx = [];
  const faces = [
    [[-1, -1, -1], [1, -1, -1], [1, 1, -1], [-1, 1, -1]],
    [[-1, -1, 1], [1, -1, 1], [1, 1, 1], [-1, 1, 1]],
    [[-1, -1, -1], [1, -1, -1], [1, -1, 1], [-1, -1, 1]],
    [[-1, 1, -1], [1, 1, -1], [1, 1, 1], [-1, 1, 1]],
    [[-1, -1, -1], [-1, 1, -1], [-1, 1, 1], [-1, -1, 1]],
    [[1, -1, -1], [1, 1, -1], [1, 1, 1], [1, -1, 1]],
  ];
  for (const f of faces) {
    const base = pos.length / 3;
    for (const v of f) pos.push(v[0] * h, v[1] * h, v[2] * h);
    idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
  }
  return { pos, idx };
}

/** 球面散点：点云用的 */
function buildCloud(n, radius) {
  const pos = [];
  const golden = Math.PI * (3 - Math.sqrt(5));
  for (let i = 0; i < n; i++) {
    const y = 1 - (i / (n - 1)) * 2;
    const r = Math.sqrt(Math.max(0, 1 - y * y));
    const t = golden * i;
    pos.push(Math.cos(t) * r * radius, Math.sin(t) * r * radius, y * radius * 0.7);
  }
  return { pos, idx: null };
}

/** 双螺旋：线段用的 */
function buildHelix(turns, n, radius, height) {
  const pos = [];
  const idx = [];
  for (let k = 0; k < 2; k++) {
    const base = pos.length / 3;
    const phase = k * Math.PI;
    for (let i = 0; i < n; i++) {
      const u = i / (n - 1);
      const a = u * Math.PI * 2 * turns + phase;
      pos.push(Math.cos(a) * radius, Math.sin(a) * radius, (u - 0.5) * height);
    }
    for (let i = 0; i < n - 1; i++) {
      idx.push(base + i, base + i + 1);
    }
  }
  return { pos, idx };
}

function scene3D() {
  const grid = buildGrid(22, 160, 16);
  const cube = buildCube(52);
  const cloud = buildCloud(420, 78);
  const helix = buildHelix(2.5, 220, 46, 150);

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto', clearMode: 'clear' },
    layers: [],
    geoms: new Map(),
    cmds: [],
    entities: [],
    scene3d: {
      bg: '#07070a',
      lights: true,
      ambient: 1,
      camera: null,          // null = 自动取景，正好测这条路径
      meshes: [
        {
          id: 'm_grid',
          positions: b64f32(grid.pos),
          indices: b64u32(grid.idx),
          count: grid.pos.length / 3,
          tris: grid.idx.length / 3,
        },
        {
          id: 'm_cloud',
          kind: 'points',
          positions: b64f32(cloud.pos),
          indices: null,
          count: cloud.pos.length / 3,
          tris: 0,
        },
        {
          id: 'm_helix',
          kind: 'lines',
          positions: b64f32(helix.pos),
          indices: b64u32(helix.idx),
          count: helix.pos.length / 3,
          tris: 0,
        },
        {
          id: 'm_cube',
          positions: b64f32(cube.pos),
          indices: b64u32(cube.idx),
          count: cube.pos.length / 3,
          tris: cube.idx.length / 3,
        },
      ],
      cmds: [
        {
          id: 'c3_grid',
          mesh: 'm_grid',
          material: { mode: 1, color: '#07a954', size: 2, glow: 0.15, opacity: 1 },
          xform: { matrix: null, frames: null, n: 0 },
          visible: true,
          order: 0,
        },
        {
          id: 'c3_cube',
          mesh: 'm_cube',
          material: { mode: 2, color: '#fd7ab4', size: 1.4, glow: 0, opacity: 1 },
          xform: {
            matrix: b64f32(rotZ(24, 0, 0, 34)),
            frames: null,
            n: 0,
          },
          visible: true,
          order: 1,
        },
        {
          id: 'c3_cloud',
          mesh: 'm_cloud',
          material: { mode: 3, color: '#f0b429', size: 2.6, glow: 0, opacity: 1 },
          xform: { matrix: null, frames: null, n: 0 },
          visible: true,
          order: 2,
        },
        {
          id: 'c3_helix',
          mesh: 'm_helix',
          material: { mode: 1, color: '#e8e6df', size: 1, glow: 0, opacity: 0.9 },
          xform: { matrix: null, frames: null, n: 0 },
          visible: true,
          order: 3,
        },
      ],
      objects: ['c3_cloud', 'c3_helix', 'c3_cube', 'c3_grid'],
    },
  };
}

/** 绕 Z 轴旋转再平移，列主序（Three.js 顺序） */
function rotZ(deg, tx, ty, tz) {
  const r = (deg * Math.PI) / 180;
  const c = Math.cos(r), s = Math.sin(r);
  return [
    c, s, 0, 0,
    -s, c, 0, 0,
    0, 0, 1, 0,
    tx, ty, tz, 1,
  ];
}

/* ---------------- v0.30 表达式绑定 + 事件写变量 ---------------- */

/**
 * 两条线扛住两件事：
 *   c_beat —— width 绑在变量 v_w 上：点击把 v_w 在 0/1 之间翻，宽度 1.5 ↔ 7.5
 *   c_breath —— width 绑在 time 上：2 + 2*sin(2t)，不需要 GH 每帧求解
 * 另外给一条静态线做对照，证明"没绑定的指令不受影响"。
 */
function sceneBinding() {
  const geoms = new Map();
  const cmds = [];
  const line = (id, y) => geoms.set(id, {
    kind: 'polyline', enc: 'f32',
    coords: b64f32([180, y, W - 180, y]),
  });
  line('g_beat', H * 0.36);
  line('g_breath', H * 0.5);
  line('g_plain', H * 0.64);

  cmds.push({
    id: 'c_beat', geom: 'g_beat', layer: 'ink', order: 0,
    style: { stroke: { color: '#EDEBE4', width: 1.5, cap: 'round', join: 'round', alpha: 1 } },
    // 1.5 + 6 * v_w
    bind: {
      width: {
        o: 'add',
        a: { o: 'const', v: 1.5 },
        b: { o: 'mul', a: { o: 'var', n: 'v_w', i: 0 }, b: { o: 'const', v: 6 } },
      },
    },
  });

  cmds.push({
    id: 'c_breath', geom: 'g_breath', layer: 'ink', order: 1,
    style: { stroke: { color: '#10B363', width: 2, cap: 'round', join: 'round', alpha: 1 } },
    // 2 + 2 * sin(2t)
    bind: {
      width: {
        o: 'add',
        a: { o: 'const', v: 2 },
        b: {
          o: 'mul',
          a: { o: 'sin', a: { o: 'mul', a: { o: 'time' }, b: { o: 'const', v: 2 } } },
          b: { o: 'const', v: 2 },
        },
      },
    },
  });

  cmds.push({
    id: 'c_plain', geom: 'g_plain', layer: 'ink', order: 2,
    style: { stroke: { color: '#FD7AB4', width: 2, cap: 'round', join: 'round', alpha: 1 } },
  });

  // 圆环：缩放 + 色相都绑在时间上 —— "半径呼吸 + 颜色脉冲"，不用 Morph 也不用 GH Timer
  const cx = W / 2, cy = H * 0.79, R = 74;
  const ring = [];
  for (let i = 0; i < 64; i++) {
    const a = (i / 64) * Math.PI * 2;
    ring.push(cx + Math.cos(a) * R, cy + Math.sin(a) * R);
  }
  geoms.set('g_ring', { kind: 'polygon', enc: 'f32', coords: b64f32(ring) });
  cmds.push({
    id: 'c_ring', geom: 'g_ring', layer: 'ink', order: 3,
    // 底色要有饱和度，换色相才看得出来
    style: { stroke: { color: '#10B363', width: 3, cap: 'round', join: 'round', alpha: 1 } },
    transform: { pivot: [cx, cy], scale: [1, 1], rotate: 0, translate: [0, 0], progress: 1 },
    bind: {
      // 0.85 ± 0.15
      scale: {
        o: 'add',
        a: { o: 'const', v: 0.85 },
        b: { o: 'mul', a: { o: 'sin', a: { o: 'mul', a: { o: 'time' }, b: { o: 'const', v: 2 } } }, b: { o: 'const', v: 0.15 } },
      },
      // 色相 0..1 循环
      hue: {
        o: 'add',
        a: { o: 'const', v: 0.5 },
        b: { o: 'mul', a: { o: 'sin', a: { o: 'mul', a: { o: 'time' }, b: { o: 'const', v: 0.9 } } }, b: { o: 'const', v: 0.5 } },
      },
    },
  });

  // 小方块：按下放大到 4 倍，松开复位 —— 指针事件 + scale 绑定的最小闭环
  geoms.set('g_hold', {
    kind: 'polygon', enc: 'f32',
    coords: b64f32([236, 120, 266, 120, 266, 150, 236, 150]),
  });
  cmds.push({
    id: 'c_hold', geom: 'g_hold', layer: 'ink', order: 4,
    style: { stroke: { color: '#10B363', width: 2, cap: 'round', join: 'round', alpha: 1 } },
    transform: { pivot: [251, 135], scale: [1, 1], rotate: 0, translate: [0, 0], progress: 1 },
    bind: {
      scale: {
        o: 'add',
        a: { o: 'const', v: 1 },
        b: { o: 'mul', a: { o: 'var', n: 'v_hold', i: 0 }, b: { o: 'const', v: 3 } },
      },
    },
  });

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto' },
    timeline: { duration: 8, loop: true },
    layers: [{ id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' }],
    geoms, cmds, entities: [],
    vars: [{ id: 'v_w', value: 0 }, { id: 'v_hold', value: 0 }],
    events: [
      { id: 'e_click', event: 'click', writes: [{ var: 'v_w', op: 'toggle' }] },
      // 按住累加、松开归零：add / set 各用一次
      { id: 'e_down', event: 'pointerdown', writes: [{ var: 'v_hold', op: 'add', value: 1 }] },
      { id: 'e_up', event: 'pointerup', writes: [{ var: 'v_hold', op: 'set', value: 0 }] },
    ],
  };
}

/* ---------------- v0.31：同一曲线的五种静态画法 ---------------- */

/** 印章：圆点 / 短线，局部坐标、以原点为中心 —— 与 GH 的 Brush 电池同一套语义 */
function stampDot(r, n = 16) {
  const out = [];
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 2;
    out.push(Math.cos(a) * r, Math.sin(a) * r);
  }
  return out;
}
const stampDash = (half) => [-half, 0, half, 0];

/**
 * 等距采样：总长 / 整数份，末段不留零头 —— 与 MarkMath.Sample 同一套规则。
 * 两边的算法必须一致，否则"GH 里看着均匀、浏览器里最后一段挤在一起"。
 */
function samplePath(xy, spacing) {
  const segs = [];
  let total = 0;
  for (let i = 0; i + 3 < xy.length; i += 2) {
    const dx = xy[i + 2] - xy[i], dy = xy[i + 3] - xy[i + 1];
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;
    segs.push({ x: xy[i], y: xy[i + 1], dx: dx / len, dy: dy / len, len, s: total });
    total += len;
  }
  if (!segs.length) return { total: 0, marks: [] };
  const count = Math.max(1, Math.round(total / spacing));
  const step = total / count;
  const marks = [];
  let si = 0;
  for (let i = 0; i <= count; i++) {
    const d = Math.min(i * step, total);
    while (si < segs.length - 1 && d > segs[si].s + segs[si].len) si++;
    const g = segs[si];
    const t = g.len > 0 ? (d - g.s) / g.len : 0;
    marks.push({ x: g.x + g.dx * g.len * t, y: g.y + g.dy * g.len * t, a: Math.atan2(g.dy, g.dx), u: d / total });
  }
  return { total, marks };
}

/** 一条 Mark 指令：位置与朝向全在 matrix 里，几何是共享的印章 */
function markCmd(id, brushId, s, theta, k, style, order, opacity) {
  const c = Math.cos(theta) * k, sn = Math.sin(theta) * k;
  return {
    id, geom: brushId, layer: 'ink', order,
    visible: true,
    opacity: opacity == null ? 1 : opacity,
    style: {
      stroke: { color: style.color, width: style.width, cap: 'round', join: 'round', alpha: 1 },
      // 闭合印章默认填充 —— 圆点画成空心圈就不叫点描了
      fill: style.fill ? { color: style.color, alpha: 1, rule: 'nonzero' } : { color: null, alpha: 1, rule: 'nonzero' },
    },
    transform: { matrix: [c, sn, -sn, c, s.x, s.y], progress: 1, translate: [0, 0], rotate: 0, scale: [1, 1], pivot: [0, 0] },
  };
}

/** 确定性伪随机：同一序号永远同一朝向，重绘不会让画面跳。 */
function markHash(i) {
  let h = Math.imul(i ^ 0x9e3779b9, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

function sceneMarks() {
  const geoms = new Map();
  const cmds = [];

  // 三枚印章，四种画法共用 —— 等价于 GH 里"一只 Brush 接四只 Mark"。
  // 浏览器端看到的是 500+ 条指令只引用 3 份几何。
  geoms.set('g_brush_dot', { kind: 'polygon', enc: 'f32', coords: b64f32(stampDot(2.2, 16)) });
  geoms.set('g_brush_nib', { kind: 'polyline', enc: 'f32', coords: b64f32(stampDash(7)) });
  geoms.set('g_brush_tick', { kind: 'polyline', enc: 'f32', coords: b64f32(stampDash(4)) });

  const rows = [
    { y: 130, style: 'line' },
    { y: 300, style: 'stipple' },
    { y: 470, style: 'nib' },
    { y: 640, style: 'hatch' },
    { y: 810, style: 'scatter' },
  ];
  const spacing = { stipple: 11, nib: 8, hatch: 9, scatter: 12 };

  let order = 0;
  rows.forEach((row, ri) => {
    const xy = [];
    for (let i = 0; i <= 96; i++) {
      const t = i / 96;
      xy.push(240 + 960 * t, row.y + Math.sin(t * Math.PI * 2) * 40);
    }
    geoms.set(`g_wave${ri}`, { kind: 'polyline', enc: 'f32', coords: b64f32(xy) });

    if (row.style === 'line') {
      cmds.push({
        id: `c_wave${ri}`, geom: `g_wave${ri}`, layer: 'ink', order: order++,
        style: { stroke: { color: '#EDEBE4', width: 2, cap: 'round', join: 'round', alpha: 1 } },
      });
      return;
    }

    // 引导线：证明五种画法读的是同一条曲线（也是"没被标记覆盖住"的参照）
    cmds.push({
      id: `c_guide${ri}`, geom: `g_wave${ri}`, layer: 'ink', order: order++,
      style: { stroke: { color: '#2A2A34', width: 1, cap: 'round', join: 'round', alpha: 1 } },
    });

    const { marks } = samplePath(xy, spacing[row.style]);
    marks.forEach((s, i) => {
      let theta = s.a, k = 1, width = 1, color = '#EDEBE4', opacity = 1, brush = 'g_brush_dot', fill = false;

      if (row.style === 'stipple') {
        // 点描：沿路径由小到大（Style Map · size）
        k = 0.7 + 1.5 * s.u;
        color = '#10B363';
        fill = true;
      } else if (row.style === 'nib') {
        // 书法笔锋：固定 45°，粗细随位置（Style Map · width）
        brush = 'g_brush_nib';
        theta = Math.PI / 4;
        width = 1 + 1.6 * s.u;
      } else if (row.style === 'hatch') {
        // 排线：垂直于切向
        brush = 'g_brush_tick';
        theta = s.a + Math.PI / 2;
        color = '#FD7AB4';
      } else {
        // 散点：短线随机朝向 + 沿路径淡出（Style Map · opacity）
        brush = 'g_brush_tick';
        theta = markHash(i) * Math.PI * 2;
        opacity = 1 - 0.7 * s.u;
      }

      cmds.push(markCmd(`c_${row.style}_${i}`, brush, s, theta, k, { color, width, fill }, order++, opacity));
    });
  });

  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto' },
    timeline: { duration: 8, loop: true },
    layers: [{ id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' }],
    geoms, cmds, entities: [],
  };
}

/* ---------------- v0.32：状态程序（自定义弹簧 + 分裂 + 销毁留痕） ---------------- */

/**
 * 这个场景不使用任何"行为预设"：四行写入就是一个绕中心的弹簧积分，
 * 分裂与销毁只是两条条件。引擎不知道"弹簧"是什么，它只会读状态、算数、写状态。
 */
function sceneBehavior() {
  const geoms = new Map();
  const ents = [];

  // 曲线只提供初始位置：程序一旦写了 x / y，就不再从曲线取样。
  const R = 250, cx = W / 2, cy = H / 2;
  const ring = [];
  for (let i = 0; i < 128; i++) {
    const a = (i / 128) * Math.PI * 2;
    ring.push(cx + Math.cos(a) * R, cy + Math.sin(a) * R);
  }
  geoms.set('g_orbit', { kind: 'polygon', enc: 'f32', coords: b64f32(ring) });

  const S = (n) => ({ o: 'state', n });
  const C = (v) => ({ o: 'const', v });
  const DT = { o: 'dt' };
  const add = (a, b) => ({ o: 'add', a, b });
  const sub = (a, b) => ({ o: 'sub', a, b });
  const mul = (a, b) => ({ o: 'mul', a, b });
  const gt = (a, b) => ({ o: 'gt', a, b });
  // 向心 + 切向 = 绕中心转的稳定轨道；参数取得小一点，让粒子是"绕"而不是"砸向中心"。
  const K = 0.9;       // 向心系数（ω = √K ≈ 0.95 rad/s，约 6.6 秒一圈）
  const DAMP = 0.2;    // 阻尼

  // ax = (cx - x)·K·dt − (y − cy)·K·dt − vx·DAMP·dt
  // ay = (cy - y)·K·dt + (x − cx)·K·dt − vy·DAMP·dt
  const ax = sub(
    sub(mul(sub(C(cx), S('x')), mul(C(K), DT)), mul(sub(C(cy), S('y')), mul(C(K), DT))),
    mul(S('vx'), mul(C(DAMP), DT)),
  );
  const ay = sub(
    sub(mul(sub(C(cy), S('y')), mul(C(K), DT)), mul(sub(S('x'), C(cx)), mul(C(-K), DT))),
    mul(S('vy'), mul(C(DAMP), DT)),
  );

  ents.push({
    id: 'e_swarm',
    value: {
      geom: 'g_orbit',
      layer: 'particles',
      order: 0,
      count: 24,
      speed: 0,          // 不走曲线：程序自己积分
      lifetime: 0,       // 不按寿命重生（销毁由程序决定）
      phase: 0,
      trail: 5,
      size: 4.5,
      width: 1,
      shape: { kind: 'polygon', enc: 'f32', coords: b64f32([0, -0.5, 0.5, 0.5, -0.5, 0.5]) },
      color: '#10B363',
      fade: false,
      program: {
        steps: [
          { slot: 'vx', op: 'add', expr: ax },
          { slot: 'vy', op: 'add', expr: ay },
          { slot: 'x', op: 'add', expr: mul(S('vx'), DT) },
          { slot: 'y', op: 'add', expr: mul(S('vy'), DT) },
          // alpha 随速度衰减：纯展示用，证明 alpha 也是可写槽位
          { slot: 'alpha', op: 'set', expr: C(1) },
        ],
        spawn: { when: gt(S('age'), C(1.0)), count: 2, spread: 14, maxGen: 2, maxPop: 600 },
        destroy: { when: mul(gt(S('gen'), C(0)), gt(S('age'), C(4.0))), keep: 200 },
      },
    },
  });

  return {
    canvas: { w: W, h: H, bg: '#08080c', dpr: 'auto' },
    layers: [{ id: 'particles', name: 'particles', order: 10, opacity: 1, blend: 'lighter' }],
    geoms,
    cmds: [],
    entities: ents,
  };
}

// 64×64 棋盘 PNG（216 字节 base64）：用最小的真图片证明解码链路，而不是塞一张几 MB 的照片
const CHECKER_B64 = 'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAaUlEQVR42u3XoQ0AIAxFwU5SzYiMzQgsUFQNhEuQFZx6+bHmKF9m/W67DwAAAACAFuCVj57uAQAAAAB6ACUGAAAAsAeUGAAAAMAeUGIAAAAAe0CJAQAAAOwBJQYAAACwB5QYAAAA4BvABjgwoOIgZTMsAAAAAElFTkSuQmCC';

/* ---------------- v0.33：图片 / 裁剪 ---------------- */

/**
 * 图片几何是**单位方块**：位置与尺寸全在矩阵里，与 Mark 的印章同一套语义。
 * 三张同源图共用一份 geom.upsert —— 这正是 base64 走文档、而不是"每条指令带一张图"的理由。
 */
function sceneImage() {
  const geoms = new Map();
  const cmds = [];

  geoms.set('g_checker', {
    kind: 'image',
    src: 'data:image/png;base64,' + CHECKER_B64,
    w: 64,
    h: 64,
  });

  // 一条圆：作为裁剪模具（页面坐标）
  const circle = (cx, cy, r, n = 64) => {
    const out = [];
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2;
      out.push(cx + Math.cos(a) * r, cy + Math.sin(a) * r);
    }
    return out;
  };
  geoms.set('g_clip_circle', { kind: 'polygon', enc: 'f32', coords: b64f32(circle(930, 270, 120)) });
  geoms.set('g_clip_hole', { kind: 'polygon', enc: 'f32', coords: b64f32(circle(250, 660, 90)) });

  const imgCmd = (id, m, extra) => ({
    id, geom: 'g_checker', layer: 'ink', order: 0,
    visible: true, opacity: 1,
    style: { stroke: { color: '#000', width: 0, alpha: 0 }, fill: { color: null, alpha: 1, rule: 'nonzero' } },
    transform: { matrix: m, progress: 1 },
    ...extra,
  });

  // 1) 原样：300×300，用来证明"解码成功且画出来了"
  cmds.push(imgCmd('c_img_plain', [300, 0, 0, 300, 120, 120]));
  // 2) 圆形裁剪：只保留圆内
  cmds.push(imgCmd('c_img_circle', [300, 0, 0, 300, 780, 120], { clip: { geom: 'g_clip_circle', invert: false } }));
  // 3) 反相裁剪：整块保留圆外
  cmds.push(imgCmd('c_img_hole', [260, 0, 0, 260, 120, 530], { clip: { geom: 'g_clip_hole', invert: true } }));

  return {
    canvas: { w: W, h: H, bg: '#0b0b10', dpr: 'auto' },
    layers: [{ id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' }],
    geoms, cmds, entities: [],
  };
}

/* ---------------- v0.34：Shader IR ---------------- */

/**
 * S1 的 golden path：Position → Sin → Mix → colorNode。
 * 一张图同时验证两件事 —— x 变化会改变颜色（空间函数），time 增长会改变颜色（时间函数）。
 * 用 52 尺寸的立方体：x ∈ [-26,26]，乘 0.12 后约一个 sin 周期，条纹肉眼可辨。
 */
const SHADER_IR_WAVE = {
  v: 1,
  nodes: [
    { id: 'p', op: 'position', space: 'local' },
    { id: 'px', op: 'swizzle', a: 'p', c: 'x', ty: 'f' },
    { id: 'k', op: 'const', v: 0.12, ty: 'f' },
    { id: 'm', op: 'mul', a: 'px', b: 'k', ty: 'f' },
    { id: 'tm', op: 'time', ty: 'f' },
    { id: 'a', op: 'add', a: 'm', b: 'tm', ty: 'f' },
    { id: 's', op: 'sin', a: 'a', ty: 'f' },
    { id: 'r', op: 'remap', x: 's', in0: -1, in1: 1, out0: 0, out1: 1, ty: 'f' },
    { id: 'c0', op: 'const', v: [0.03, 0.06, 0.12], ty: 'v3' },
    { id: 'c1', op: 'const', v: [1.0, 0.55, 0.1], ty: 'v3' },
    { id: 'o', op: 'mix', a: 'c0', b: 'c1', t: 'r', ty: 'v3' },
  ],
  out: 'o',
};

function sceneShader3D() {
  const cube = buildCube(52);
  return {
    canvas: { w: W, h: H, bg: '#07070a', dpr: 'auto', clearMode: 'clear' },
    layers: [],
    geoms: new Map(),
    cmds: [],
    entities: [],
    scene3d: {
      bg: '#07070a',
      lights: true,
      ambient: 1,
      camera: null,
      meshes: [
        {
          id: 'm_scube',
          positions: b64f32(cube.pos),
          indices: b64u32(cube.idx),
          count: cube.pos.length / 3,
          tris: cube.idx.length / 3,
        },
      ],
      cmds: [
        {
          id: 'c3_shader',
          mesh: 'm_scube',
          material: {
            mode: 5,               // unlit：只让 colorNode 决定颜色，不受光照干扰
            color: '#e8e6df',
            size: 2,
            glow: 0,
            opacity: 1,
            shader: SHADER_IR_WAVE,
          },
          xform: { matrix: b64f32(rotZ(22, 18, 0, 0)), frames: null, n: 0 },
          visible: true,
          order: 0,
        },
      ],
      objects: ['c3_shader'],
    },
  };
}



/* ---------------- 场景注册 ---------------- */

const SCENES = {
  static: { build: sceneStatic, tick: null, hz: 0 },
  mutate: { build: sceneMutate, tick: mutateTick, hz: 12 },
  stress: { build: sceneStress, tick: stressTick, hz: 6 },
  entities: { build: sceneEntities, tick: null, hz: 0, drift: driftEntities },
  living: { build: sceneLiving, tick: null, hz: 0 },
  morph: { build: sceneMorph, tick: null, hz: 0 },
  scene3d: { build: scene3D, tick: null, hz: 0 },
  shader3d: { build: sceneShader3D, tick: null, hz: 0 },
  binding: { build: sceneBinding, tick: null, hz: 0 },
  marks: { build: sceneMarks, tick: null, hz: 0 },
  behavior: { build: sceneBehavior, tick: null, hz: 0 },
  image: { build: sceneImage, tick: null, hz: 0 },
  random: {
    build: sceneRandom, tick: randomTick, hz: 30,
    // 等价于 GH 里 Signal to Float 每帧推的定义
    signals: [{ id: 'sig_demo', kind: 'noise1', seed: 7, rate: 0.6, args: [0] }],
  },
};

/* ---------------- diff 与推送 ---------------- */

class Session {
  constructor(ws) {
    this.ws = ws;
    this.seq = 0;
    this.rev = 0;
    this.sceneName = 'static';
    this.state = null;
    this.sent = null;      // 上次发出的状态快照（id -> value 引用）
    this.timer = null;
    this.t0 = performance.now();
    this.acks = 0;
    this.lastAckMs = 0;
  }

  load(name) {
    const def = SCENES[name];
    if (!def) return;
    clearInterval(this.timer);
    clearInterval(this.driftTimer);
    this.timer = null;
    this.driftTimer = null;
    this.t0 = performance.now();
    this.sceneName = name;
    this.state = def.build();
    for (const sg of def.signals || []) this._send({ v: '0.1', t: 'signal', ...sg });
    if (!def.signals) this._send({ v: '0.1', t: 'signal', id: '__clear', kind: 'noise1', seed: 0, rate: 1, args: [] });
    this.sent = null;
    this.rev = 0;
    this.t0 = performance.now();   // 每个场景从 0 开始计时，否则切到后面的场景时动画已经"跑完"了
    this.snapshot();
    if (def.tick) {
      this.timer = setInterval(() => {
        def.tick(this.state, (performance.now() - this.t0) / 1000);
        this.diff();
      }, 1000 / def.hz);
    }
  }

  /** 只漂移几何、不动实体定义：模拟"GH 里改了曲线，但粒子群定义没变" */
  setDrift(on) {
    clearInterval(this.driftTimer);
    this.driftTimer = null;
    const def = SCENES[this.sceneName];
    if (!on || !def || !def.drift) return;
    this.driftTimer = setInterval(() => {
      def.drift(this.state, (performance.now() - this.t0) / 1000);
      this.diff();
    }, 200);
  }

  _send(msg) {
    if (this.ws.readyState !== 1) return;
    msg.v = '0.1';
    msg.seq = ++this.seq;
    msg.ts = Date.now();
    this.ws.send(JSON.stringify(msg));
  }

  snapshot() {
    this.rev++;
    const st = this.state;
    const ops = [{ op: 'doc.reset', canvas: st.canvas }];
    // 全局时间轴：doc.reset 会把它重置成默认值，所以每次快照都重发一遍
    const tl = st.timeline || { duration: 8, loop: true };
    ops.push({ op: 'timeline.set', duration: tl.duration, loop: tl.loop });
    for (const l of st.layers) ops.push({ op: 'layer.upsert', value: l });
    for (const [id, v] of st.geoms) ops.push({ op: 'geom.upsert', id, value: v });
    for (const c of st.cmds) ops.push({ op: 'cmd.upsert', value: c });
    for (const e of st.entities || []) ops.push({ op: 'entity.upsert', id: e.id, value: e.value });
    for (const v of st.vars || []) ops.push({ op: 'var.upsert', id: v.id, value: v.value });
    for (const e of st.events || []) ops.push({ op: 'event.upsert', id: e.id, value: e });

    // 三维（v0.25）：没有场景就发 clear，避免切场景后残留上一个作品的三维层
    if (st.scene3d) {
      const s3 = st.scene3d;
      for (const m of s3.meshes) ops.push({ op: 'mesh.upsert', id: m.id, value: m });
      for (const c of s3.cmds) ops.push({ op: 'cmd3d.upsert', id: c.id, value: c });
      ops.push({
        op: 'scene3d.set',
        value: { bg: s3.bg, lights: s3.lights, ambient: s3.ambient, camera: s3.camera, objects: s3.objects },
      });
    } else {
      ops.push({ op: 'scene3d.clear' });
    }

    this.sent = {
      canvas: JSON.stringify(st.canvas),
      timeline: JSON.stringify(tl),
      layers: new Map(st.layers.map((l) => [l.id, JSON.stringify(l)])),
      geoms: new Map([...st.geoms].map(([id, v]) => [id, v.coords])),
      cmds: new Map(st.cmds.map((c) => [c.id, JSON.stringify(c)])),
      entities: new Map((st.entities || []).map((e) => [e.id, JSON.stringify(e.value)])),
    };
    this._send({ t: 'patch', rev: this.rev, snapshot: true, ops, stats: { geoms: st.geoms.size, cmds: st.cmds.length, buildMs: 0 } });
  }

  diff() {
    const st = this.state;
    const ops = [];
    const sent = this.sent;

    const cv = JSON.stringify(st.canvas);
    if (cv !== sent.canvas) { ops.push({ op: 'canvas.set', value: st.canvas }); sent.canvas = cv; }

    const tl = JSON.stringify(st.timeline || { duration: 8, loop: true });
    if (tl !== sent.timeline) {
      const o = JSON.parse(tl);
      ops.push({ op: 'timeline.set', duration: o.duration, loop: o.loop });
      sent.timeline = tl;
    }

    for (const l of st.layers) {
      const j = JSON.stringify(l);
      if (sent.layers.get(l.id) !== j) { ops.push({ op: 'layer.upsert', value: l }); sent.layers.set(l.id, j); }
    }

    for (const [id, v] of st.geoms) {
      if (sent.geoms.get(id) !== v.coords) {
        ops.push({ op: 'geom.upsert', id, value: v });
        sent.geoms.set(id, v.coords);
      }
    }

    for (const c of st.cmds) {
      const j = JSON.stringify(c);
      if (sent.cmds.get(c.id) !== j) { ops.push({ op: 'cmd.upsert', value: c }); sent.cmds.set(c.id, j); }
    }

    for (const e of st.entities || []) {
      const j = JSON.stringify(e.value);
      if (sent.entities.get(e.id) !== j) {
        ops.push({ op: 'entity.upsert', id: e.id, value: e.value });
        sent.entities.set(e.id, j);
      }
    }

    if (!ops.length) return;   // 整帧无变化则不发，这是带宽的基本保障
    this.rev++;
    this._send({ t: 'patch', rev: this.rev, ops, stats: { geoms: st.geoms.size, cmds: st.cmds.length, buildMs: 0 } });
  }
}

/* ---------------- 服务 ---------------- */

const wss = new WebSocketServer({ host: '127.0.0.1', port: PORT, path: PATH });

wss.on('connection', (ws, req) => {
  const s = new Session(ws);
  console.log(`[mock-gh] client connected ${req.socket.remoteAddress}`);
  s._send({ t: 'welcome', sessionId: Math.random().toString(36).slice(2, 8), doc: 'mock-colugo.gh', rev: 0, mock: true });
  s.load(process.env.SCENE || 'static');

  ws.on('message', (raw) => {
    let m;
    try { m = JSON.parse(raw.toString()); } catch { return; }
    if (m.t === 'switch' && SCENES[m.name]) {
      console.log(`[mock-gh] scene -> ${m.name}`);
      s.load(m.name);
    } else if (m.t === 'cmd' && m.action === 'drift') {
      console.log(`[mock-gh] drift -> ${m.on}`);
      s.setDrift(!!m.on);
    } else if (m.t === 'ack') {
      s.acks++;
      s.lastAckMs = m.ms || 0;
    } else if (m.t === 'ping') {
      s._send({ t: 'pong' });
    }
  });

  ws.on('close', () => {
    clearInterval(s.timer);
    console.log('[mock-gh] client closed');
  });
});

console.log(`[mock-gh] listening ws://127.0.0.1:${PORT}${PATH}`);
console.log(`[mock-gh] scenes: ${Object.keys(SCENES).join(' / ')}`);
