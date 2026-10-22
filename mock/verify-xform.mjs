// verify-xform.mjs — 纯 Node 断言：变换进度插值 + 振荡器波形
//
// 这两块都是"错了整张图就不对"的数学，但不需要浏览器。放在这里跑，秒级反馈。
// 用法：node mock/verify-xform.mjs

import { resolveMatrix, decompose, compose, IDENTITY } from '../runtime/src/xform.js';
import { waveValue, oscValue } from '../runtime/src/osc.js';

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

const tau = Math.PI * 2;
check('sin 波形：相位 0 / π/2 / π', 
  near(waveValue('sin', 0), 0) && near(waveValue('sin', tau / 4), 1) && near(waveValue('sin', tau / 2), 0, 1e-12),
  [waveValue('sin', 0), waveValue('sin', tau / 4)]);
check('tri 波形：相位 π/2 取 1，3π/2 取 −1',
  near(waveValue('tri', Math.PI / 2), 1) && near(waveValue('tri', (3 * Math.PI) / 2), -1), null);
check('saw 波形：0 → −1，半周期 → 0，四分之一 → −0.5',
  near(waveValue('saw', 0), -1) && near(waveValue('saw', tau / 2), 0) && near(waveValue('saw', tau / 4), -0.5), null);
check('square 波形：正半周 +1，负半周 −1',
  waveValue('square', 0.1) === 1 && waveValue('square', Math.PI + 0.1) === -1, null);

// 值 = 波形 × 幅度 + 中心
const v = oscValue({ wave: 'sin', freq: 0.25, amp: 10, offset: 30, phase: 0 }, 1);
// 0.25Hz → 周期 4s；t=1s 相位 π/2 → sin=1 → 10+30
check('oscValue = 波形×幅度 + 中心（0.25Hz, t=1s）', near(v, 40, 1e-9), v);

const v0 = oscValue({ wave: 'sin', freq: 0.25, amp: 10, offset: 30 }, 0);
check('oscValue 的 Offset 就是摆动中心（相位 0 时回落 offset）', near(v0, 30, 1e-9), v0);

const pass = results.filter(Boolean).length;
console.log(`\n${pass}/${results.length} passed`);
process.exit(pass === results.length ? 0 : 1);
