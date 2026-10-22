// verify-xform.mjs — 纯 Node 断言：矩阵插值（TRS 分解）+ 时间轨取值
//
// 这两块都是"错了整张图就不对"的数学，但不需要浏览器。放在这里跑，秒级反馈。
// 用法：node mock/verify-xform.mjs
//
// v0.16 起振荡器（osc.js）与波形表随预设一起删除，这里改成验时间轨：
// 帧序列按 Duration / Loop / Ease 取到的插值必须和手算一致。

import { resolveMatrix, decompose, compose, IDENTITY, trackU, resolveTrackMatrix, sampleEase } from '../runtime/src/xform.js';

const results = [];
const check = (name, pass, detail) => {
  results.push(pass);
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}  → ${JSON.stringify(detail)}`);
};
const near = (a, b, eps = 1e-9) => Math.abs(a - b) < eps;

/* ---------- 变换进度 ---------- */

// 页面里绕原点顺时针 90°（Canvas 的 Y 向下，所以 [0,1,-1,0] 看起来是顺时针）
const rot90 = [0, 1, -1, 0, 0, 0];

const at1 = resolveMatrix({ matrix: rot90, progress: 1 });
check('progress=1 原样返回矩阵（不往返换算，避免浮点漂移）',
  at1 === rot90, at1);

const at0 = resolveMatrix({ matrix: rot90, progress: 0 });
check('progress=0 = 原位（identity）',
  at0.every((v, i) => near(v, IDENTITY[i])), at0);

const at05 = resolveMatrix({ matrix: rot90, progress: 0.5 });
const deg = (m) => (Math.atan2(m[1], m[0]) * 180) / Math.PI;
check('90° 变换在 progress=0.5 时是 45°（这就是"演出来"的核心）',
  near(deg(at05), 45, 1e-6), { deg: deg(at05) });
check('插值中点仍是纯旋转（没有被线性插值压扁）',
  near(at05[0], Math.SQRT1_2, 1e-9) && near(at05[1], Math.SQRT1_2, 1e-9), at05);

// 缩放 2 倍 + 平移 (100, 0)：progress=0.5 应是缩放 1.5、平移 50
const str = [2, 0, 0, 2, 100, 0];
const s05 = resolveMatrix({ matrix: str, progress: 0.5 });
check('缩放 2 → 1.5，平移 100 → 50',
  near(s05[0], 1.5) && near(s05[3], 1.5) && near(s05[4], 50), s05);

// 没有矩阵时返回 null（渲染端据此跳过）
check('没有 matrix 时返回 null', resolveMatrix({ progress: 0.5 }) === null, null);

// 缺省 progress 视为 1：v0.10 的 patch 没有这个字段，行为必须完全不变
check('缺省 progress = 终态（向后兼容）',
  resolveMatrix({ matrix: rot90 }) === rot90, null);

// 分解 / 合成往返（只对 TRS 成立：shear 会被丢掉，见文末说明）
const trs = compose({ tx: 12, ty: -7, rot: 0.6, sx: 1.5, sy: 2 });
const d = decompose(trs);
const back = compose(d);
const close = trs.every((v, i) => near(v, back[i], 1e-9));
check('decompose → compose 往返一致（TRS 分解自洽）', close, back);
check('往返后参数不变形（rot/sx/sy 稳定）',
  near(d.rot, 0.6, 1e-9) && near(d.sx, 1.5, 1e-9) && near(d.sy, 2, 1e-9), d);

/* ---------- 振荡器 ---------- */

/* ---------- 时间轨：Duration / Loop / Ease ---------- */

const tau = Math.PI * 2;
const shift = (dy) => [1, 0, 0, 1, 0, dy];
// 3 帧：0 → 40 → 0，时长 2s
const track = {
  n: 3,
  frames: Float32Array.from([...shift(0), ...shift(40), ...shift(0)]),
  reveal: null,
  duration: 2,
  loop: true,
  ease: null,
};

check('轨道进度 = t/duration（不循环时间半程取 0.5）',
  near(trackU({ ...track, loop: false }, 1), 0.5), trackU({ ...track, loop: false }, 1));
check('循环轨道在 t=duration 时回到 0（不是 1）',
  near(trackU(track, 2), 0), trackU(track, 2));
check('t 取 mid-duration 时矩阵是两帧之间的插值（20 而不是 40）',
  near(resolveTrackMatrix(track, 0.5)[5], 20, 1e-6), resolveTrackMatrix(track, 0.5)[5]);
check('取到端点时就是原始帧（t=1s → 40）',
  near(resolveTrackMatrix(track, 1)[5], 40, 1e-6), resolveTrackMatrix(track, 1)[5]);
check('缓动表把线性进度掰弯（x=0.5 → y=0.75）',
  near(sampleEase({ x: [0, 0.5, 1], y: [0, 0.75, 1] }, 0.5), 0.75), sampleEase({ x: [0, 0.5, 1], y: [0, 0.75, 1] }, 0.5));
check('缓动表端点严格是 0 和 1',
  sampleEase({ x: [0, 0.5, 1], y: [0, 0.75, 1] }, 0) === 0
  && sampleEase({ x: [0, 0.5, 1], y: [0, 0.75, 1] }, 1) === 1, null);
// 相位 0 那一刻必须精确等于第 0 帧，不能有"最小进度"之类的偏移
check('t=0 时矩阵就是第 0 帧（严格）',
  JSON.stringify(resolveTrackMatrix(track, 0)) === JSON.stringify([1, 0, 0, 1, 0, 0]),
  resolveTrackMatrix(track, 0));

const pass = results.filter(Boolean).length;
console.log(`\n${pass}/${results.length} passed`);
process.exit(pass === results.length ? 0 : 1);
