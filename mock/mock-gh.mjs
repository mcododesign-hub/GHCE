// mock-gh.mjs — 模拟 Grasshopper 侧的桥。
// 目的：在没有 Rhino 的机器上，用同一份协议驱动运行时，使 M0/M1 可在浏览器里验收。
// 它替换的是「GH 求解 + PatchBuilder」，因此这里刻意按 GH 的语义做事：
//   1. 先算完整文档状态，2. 与上次发出的状态做 diff，3. 只发送变化的 op。

import { WebSocketServer } from 'ws';

const PORT = Number(process.env.PORT || 8777);
const PATH = '/ghce';

/* ---------------- 几何工具 ---------------- */

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
  const STEP = 2.2;
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

    const track = { n: NF, duration: STEP, loop: false };

    if (i === 0) {
      // 纯生长：只给 reveal 序列，不给任何矩阵
      track.reveal = Array.from({ length: NF }, (_, k) => k / (NF - 1));
      // 自定义缓动曲线（等价于 GH 里接一条 Curve 到 Ease）
      track.easeX = Array.from({ length: 33 }, (_, k) => k / 32);
      track.easeY = track.easeX.map((u) => Math.sin(u * Math.PI * 0.5) ** 0.7);
    } else if (i === 1) {
      // 一圈旋转：帧序列自己转，浏览器只负责播
      track.n = NF;
      track.duration = 3;
      track.loop = true;
      track.frames = b64f32(Array.from({ length: NF }, (_, k) => rotFrame((360 * k) / (NF - 1), cx, cy)).flat());
    } else if (i === 2) {
      // 上下摆动：0 → 48 → 0
      track.n = NF;
      track.duration = 3.2;
      track.loop = true;
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
    layers: [
      { id: 'ink', name: 'ink', order: 0, opacity: 1, blend: 'source-over' },
      { id: 'dust', name: 'dust', order: 10, opacity: 1, blend: 'lighter' },
    ],
    geoms, cmds, entities,
  };
}

/* ---------------- 场景注册 ---------------- */

const SCENES = {
  static: { build: sceneStatic, tick: null, hz: 0 },
  mutate: { build: sceneMutate, tick: mutateTick, hz: 12 },
  stress: { build: sceneStress, tick: stressTick, hz: 6 },
  entities: { build: sceneEntities, tick: null, hz: 0, drift: driftEntities },
  living: { build: sceneLiving, tick: null, hz: 0 },
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
    for (const l of st.layers) ops.push({ op: 'layer.upsert', value: l });
    for (const [id, v] of st.geoms) ops.push({ op: 'geom.upsert', id, value: v });
    for (const c of st.cmds) ops.push({ op: 'cmd.upsert', value: c });
    for (const e of st.entities || []) ops.push({ op: 'entity.upsert', id: e.id, value: e.value });

    this.sent = {
      canvas: JSON.stringify(st.canvas),
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
  s._send({ t: 'welcome', sessionId: Math.random().toString(36).slice(2, 8), doc: 'mock-ghce.gh', rev: 0, mock: true });
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
