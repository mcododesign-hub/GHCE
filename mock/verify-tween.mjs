// verify-tween.mjs — 变换过渡（v0.12）的纯 Node 验收
//
// 测的是 tween.js 的判定，不碰浏览器：
//   1. 指令第一次出现 → 不演，直接落在 GH 给的状态
//   2. 矩阵变了 → 从"此刻画面上的位置"演到新位置，时长内线性推进
//   3. 演完之后 progress 恰好 1，且不再 active（不能一直算下去）
//   4. 连续变化时，起点取插值中的当前值（不会跳回上一段起点）
//   5. 被时间线/振荡器显式驱动 xform 的指令，这里完全不插手
//   6. restart 让同一段变换从头再演
//   7. 旋转走最短弧（170° → −170° 只走 20°，不是 340°）
//
// 用法：node mock/verify-tween.mjs   （不需要起服务）

import { Tweens } from '../runtime/src/tween.js';
import { resolveMatrix, shortestArc, decompose, decomposeAffine, composeAffine } from '../runtime/src/xform.js';

const results = [];
function check(name, pass, detail) {
  results.push(pass);
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}  → ${JSON.stringify(detail)}`);
}
const near = (a, b, tol = 1e-6) => Math.abs(a - b) <= tol;

const mkCmd = (id, matrix, progress = 1) => ({ id, visible: true, transform: { matrix, progress, from: null } });
const mkDoc = (cmds) => ({ cmds: new Map(cmds.map((c) => [c.id, c])) });

/* ---------- 1. 首次出现：从原始状态演到目标 ---------- */
{
  const c = mkCmd('a', [0, 1, -1, 0, 100, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 0);           // 第一次看到这条指令
  tw.update(doc, 0);
  const p0 = c.transform.progress;
  tw.update(doc, 0.5);
  const half = resolveMatrix(c.transform);
  tw.update(doc, 1.2);
  check('第一次出现就从原始状态开始演（p = 0）', near(p0, 0), p0);
  check('中途是 identity→目标的中间态（45°，位移 50）',
    Math.abs(half[0] - Math.SQRT1_2) < 0.01 && Math.abs(half[4] - 50) < 0.5,
    { a: +half[0].toFixed(3), e: +half[4].toFixed(2) });
  check('演完落在 GH 给的终态', near(c.transform.progress, 1), c.transform.progress);
}

/* ---------- 1b. 微小浮点抖动不算变化（否则画面永远在微微转） ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 10, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 0);
  tw.update(doc, 2);                 // 演完
  const before = c.transform.progress;
  c.transform.matrix = [1, 1e-9, -1e-9, 1, 10.0000002, 0];   // 重算后的浮点漂移
  tw.observe(doc, 3);
  tw.update(doc, 3.1);
  check('浮点级抖动不触发重演', tw.stats.active === 0 && near(c.transform.progress, before), {
    active: tw.stats.active, p: c.transform.progress,
  });
}

/* ---------- 2/3. 矩阵变了 → 演到新位置，演完归位 ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 0, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 10);                 // 记录基准 = 平移 0

  c.transform.matrix = [1, 0, 0, 1, 200, 0];   // GH 换了个新位置
  tw.observe(doc, 10);
  tw.update(doc, 10);
  const p0 = c.transform.progress;
  tw.update(doc, 10.5);
  const mid = resolveMatrix(c.transform)[4];
  tw.update(doc, 11.2);
  const end = c.transform.progress;

  check('矩阵变了就开始演（时刻 0 的进度是 0）', near(p0, 0), p0);
  check('中途停在两端的中间（x 位移 100 / 200）', near(mid, 100, 0.5), mid);
  check('时长走完后 progress = 1 且不再 active', near(end, 1) && tw.stats.active === 0, { end, active: tw.stats.active });
}

/* ---------- 4. 连续变化：起点取当前插值值 ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 0, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 0);
  c.transform.matrix = [1, 0, 0, 1, 100, 0];
  tw.observe(doc, 0);
  tw.update(doc, 0.5);                       // 此刻在 x=50
  c.transform.matrix = [1, 0, 0, 1, 300, 0];  // 又变了
  tw.observe(doc, 0.5);
  tw.update(doc, 0.5);
  const fromX = resolveMatrix(c.transform)[4];
  tw.update(doc, 1.0);                       // 再走半秒
  const halfX = resolveMatrix(c.transform)[4];

  check('新过渡从"当前画面位置"起步，不跳回 0', near(fromX, 50, 1), fromX);
  check('新过渡按 (50→300) 推进，半程 ≈ 175', near(halfX, 175, 2), halfX);
}

/* ---------- 5. 显式驱动时让位 ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 0, 0]);
  c._xformDriven = true;
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 0);
  c.transform.matrix = [1, 0, 0, 1, 500, 0];
  tw.observe(doc, 0);
  tw.update(doc, 0.2);
  check('时间线/振荡器驱动 xform 时，过渡引擎不接管',
    tw.items.size === 0 && near(c.transform.progress, 1), { items: tw.items.size, p: c.transform.progress });
}

/* ---------- 6. restart 重演 ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 0, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 1 });
  tw.observe(doc, 0);
  c.transform.matrix = [1, 0, 0, 1, 80, 0];
  tw.observe(doc, 0);
  tw.update(doc, 2);                          // 已经演完
  const done = c.transform.progress;
  tw.restart(['a'], 5);
  tw.update(doc, 5);
  const again = c.transform.progress;
  tw.update(doc, 5.5);
  const half = resolveMatrix(c.transform)[4];
  check('restart 让同一段变换从头再演', near(done, 1) && near(again, 0) && near(half, 40, 1),
    { done, again, half });
}

/* ---------- 7. 最短弧 ---------- */
{
  const a = (170 * Math.PI) / 180;
  const b = (-170 * Math.PI) / 180;
  const d = shortestArc(a, b);
  // 目标矩阵就是 −170° 的旋转矩阵（不是 90°，那是另一回事）
  const to = [Math.cos(b), Math.sin(b), -Math.sin(b), Math.cos(b), 0, 0];
  const fromM = [Math.cos(a), Math.sin(a), -Math.sin(a), Math.cos(a), 0, 0];
  const m = resolveMatrix({ matrix: to, progress: 0.5, from: fromM });
  const rot = (decompose(m).rot * 180) / Math.PI;
  check('170° → −170° 走最短弧（20°，不是 340°）', near(Math.abs(d), (20 * Math.PI) / 180, 1e-9), (d * 180) / Math.PI);
  check('插值中点落在 ±180° 附近', Math.abs(Math.abs(rot) - 180) < 1, rot);
}


/* ---------- 8. 锚点：绕自身中心转，中心必须一动不动 ---------- */
{
  const cx = 200, cy = 150;
  const th = Math.PI / 4;
  // 世界里的"绕 (cx,cy) 转 45°"
  const m = composeAffine({ rot: th, sx: 1, sy: 1, cx, cy });

  const apply = (mat, x, y) => [mat[0] * x + mat[2] * y + mat[4], mat[1] * x + mat[3] * y + mat[5]];

  // decomposeAffine → composeAffine 必须能原样还原
  const rt = composeAffine(decomposeAffine(m));
  check('分解-合成能还原原矩阵（含锚点）',
    rt.every((v, i) => near(v, m[i], 1e-9)), rt.map((v) => +v.toFixed(3)));

  // 整个过程中锚点不动 —— 这是"锚点对不对"的判据
  const drift = [0.25, 0.5, 0.75].map((p) => {
    const mat = resolveMatrix({ matrix: m, progress: p, from: null });
    const [x, y] = apply(mat, cx, cy);
    return Math.hypot(x - cx, y - cy);
  });
  check('绕自身中心转时，锚点在 1/4 / 1/2 / 3/4 处都不漂移',
    drift.every((d) => d < 0.01), drift.map((d) => +d.toFixed(6)));

  // 中点角度正好一半
  const half = decomposeAffine(resolveMatrix({ matrix: m, progress: 0.5, from: null }));
  check('中点角度是 22.5°（不是先跑到别处再回来）',
    Math.abs((half.rot * 180) / Math.PI - 22.5) < 0.01, +((half.rot * 180) / Math.PI).toFixed(3));

  // 起点必须是严格的 identity（"没变位置的原始几何"）
  const p0 = resolveMatrix({ matrix: m, progress: 0, from: null });
  check('进度 0 时就是原始几何（严格 identity）',
    p0.every((v, i) => near(v, [1, 0, 0, 1, 0, 0][i], 1e-12)), p0);
}

/* ---------- 9. 逐帧驱动时直通，不重新缓动 ---------- */
{
  const c = mkCmd('a', [1, 0, 0, 1, 0, 0]);
  const doc = mkDoc([c]);
  const tw = new Tweens({ duration: 0.9 });
  tw.observe(doc, 0);                 // 首次出现
  tw.update(doc, 1.5);                // 演完
  for (let i = 1; i <= 5; i++) {
    c.transform.matrix = [1, 0, 0, 1, i * 10, 0];   // 每 33ms 变一次
    tw.observe(doc, 1.5 + i * 0.033);
  }
  tw.update(doc, 1.5 + 5 * 0.033 + 0.01);
  check('逐帧喂数据时直通（不叠 0.9 秒缓动）',
    tw.stats.active === 0 && near(c.transform.progress, 1) && near(c.transform.matrix[4], 50),
    { active: tw.stats.active, p: c.transform.progress, x: c.transform.matrix[4] });
}

const passed = results.filter(Boolean).length;
console.log(`\n${passed}/${results.length} passed`);
process.exit(passed === results.length ? 0 : 1);
