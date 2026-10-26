// verify-shader.mjs — Shader IR 的验收（真 Chrome + scene.shader3d）
//
// 为什么不比像素：WebGPU canvas 读像素要么开 preserveDrawingBuffer 的等价物，
// 要么走 copyTextureToBuffer，两条路都重。而 S1 真正要证明的是**节点树被正确构造**，
// 那在节点层就能判定真假，比像素更稳、更可重复。
// 像素证据留给截图（人工看），断言只认可判定的结构事实。
import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import WebSocket from 'ws';

const PAGE = process.env.PAGE || 'http://127.0.0.1:5294/?ws=ws://127.0.0.1:8910/colugo';
const CDP_PORT = process.env.CDP_PORT || 9778;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let pass = 0;
let fail = 0;
const check = (name, ok, detail) => {
  if (ok) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; console.log(`  FAIL  ${name}  ${JSON.stringify(detail)}`); }
};

const profile = mkdtempSync(join(tmpdir(), 'colugo-shader-'));
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--window-size=1280,820', '--no-first-run', '--no-proxy-server',
  '--enable-unsafe-swiftshader',
  `--user-data-dir=${profile}`, `--remote-debugging-port=${CDP_PORT}`, 'about:blank',
], { stdio: 'ignore' });

let wsUrl;
for (let i = 0; i < 80; i++) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`)).json();
    const page = list.find((t) => t.type === 'page');
    if (page) { wsUrl = page.webSocketDebuggerUrl; break; }
  } catch { /* 等 */ }
  await sleep(200);
}
if (!wsUrl) { console.log('FAIL: chrome 没起来'); process.exit(1); }

const cdp = new WebSocket(wsUrl);
await new Promise((r) => cdp.on('open', r));
let id = 0;
const pending = new Map();
const logs = [];
cdp.on('message', (raw) => {
  const m = JSON.parse(raw);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); }
  if (m.method === 'Runtime.consoleAPICalled') {
    logs.push(m.params.type + ': ' + (m.params.args || []).map((a) => a.value ?? a.description).join(' '));
  }
  if (m.method === 'Runtime.exceptionThrown') {
    logs.push('EXC: ' + (m.params.exceptionDetails?.exception?.description || m.params.exceptionDetails?.text));
  }
});
const send = (method, params = {}) => new Promise((r) => {
  const i = ++id; pending.set(i, r); cdp.send(JSON.stringify({ id: i, method, params }));
});
const ev = async (expr) => {
  const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) return { __error: r.exceptionDetails.text };
  return r.result.value;
};

await send('Runtime.enable');
await send('Page.enable');
await send('Page.navigate', { url: PAGE });
await sleep(2500);

/* ---------- 切场景，等 three 加载 + 材质编译 ---------- */
const r1 = await ev(`(async () => {
  const G = window.Colugo;
  if (!G) return { noApp: true };
  G.selectScene('shader3d');
  const t0 = performance.now();
  let compiled = false;
  while (performance.now() - t0 < 20000) {
    const s = G.scene3d.stats();
    const st = G.scene3d.shader;
    if (s.objects >= 1 && s.backend && st && st.stats.builds >= 1) { compiled = true; break; }
    await new Promise(r => setTimeout(r, 100));
  }
  await new Promise(r => setTimeout(r, 500));
  const st = G.scene3d.shader;
  return {
    ready: true, compiled,
    ms: Math.round(performance.now() - t0),
    ...G.scene3d.stats(),
    builds: st ? st.stats.builds : -1,
    failures: st ? st.stats.failures : -1,
  };
})()`);

check('shader3d 场景在超时前就绪', r1.ready === true && r1.objects >= 1, r1);
check('渲染后端已确定（webgpu / webgl2）', r1.backend === 'webgpu' || r1.backend === 'webgl2', r1.backend);
check('IR 被编译过（builds ≥ 1）', r1.builds >= 1, { builds: r1.builds });
check('没有编译失败', r1.failures === 0, { failures: r1.failures });

/* ---------- 材质层：colorNode 是不是真的换成了节点树 ---------- */
const m = await ev(`(() => {
  const S = window.Colugo.scene3d;
  const keys = [...S.materials.keys()];
  const mat = S.materials.get(keys[0]);
  const u = S.shader.uniforms;
  return {
    keys,
    hasMat: !!mat,
    hasColorNode: !!(mat && mat.colorNode),
    // three 的 nodeType 在构建前恒为 null（延迟构建），改用编译器推断的类型
    outType: S.shader.lastOutType || null,
    // TSL 节点的 toString 恒为 [object Object]，读不出东西 —— 改用编译器自己记的 op 清单
    ops: S.shader.lastOps || [],
    animated: !!S._shaderAnimated,
    err: S.lastShaderError || '',
    uniforms: u ? [...u.keys()] : [],
  };
})()`);

check('场景里只有一个材质（一张 IR 图 → 一份材质）', m.keys.length === 1, m.keys);
check('材质的 colorNode 被替换（不再是常量色）', m.hasColorNode === true, m);
check('输出类型被推断为 v3（颜色域，不是标量）', m.outType === 'v3', m.outType);
check('节点树用到了 position / sin / mix（不是空壳）',
  ['position', 'sin', 'mix'].every((o) => m.ops.includes(o)), m.ops);
check('IR 里的 time 节点让场景逐帧渲染', m.animated === true, m.animated);
check('uTime uniform 已注册（时钟走作品时间，不是 TSL 自带 time）',
  m.uniforms.some((k) => k.startsWith('uTime')), m.uniforms);
check('材质缓存 key 里带结构哈希', /[0-9a-f]{8}/.test(m.keys[0] || ''), m.keys);

/* ---------- S2：shader.uniform 增量 op 不能重建材质 ---------- */
const uni = await ev(`(() => {
  const S = window.Colugo.scene3d;
  const before = S.shader.stats.builds;
  const beforeMat = S.materials.size;
  const beforeVal = S.shader.uniforms.get('uTime|f').value;
  // 同一同步块里发 op 再读值：中间不会插进 render 循环，读到的就是 op 的效果
  S.applyOps([{ op: 'shader.uniform', name: 'uTime', type: 'f', value: 42 }]);
  const afterVal = S.shader.uniforms.get('uTime|f').value;
  return {
    before, after: S.shader.stats.builds,
    beforeMat, afterMat: S.materials.size,
    beforeVal, afterVal,
  };
})()`);

check('shader.uniform op 真的改了 uniform 的值',
  uni.afterVal === 42 && uni.beforeVal !== 42, uni);
check('改 uniform 值不重编译（builds 不变）', uni.after === uni.before, uni);
check('改 uniform 值不新建材质（材质数不变）', uni.afterMat === uni.beforeMat, uni);

/* ---------- 编译器本身：白名单 / 环 / 悬空引用 / 哈希稳定性 ---------- */
const c = await ev(`(() => {
  const C = window.Colugo.scene3d.shader;
  const bad = C.compile({ v: 1, nodes: [{ id: 'x', op: 'eval', code: 'alert(1)' }], out: 'x' });
  const cyc = C.compile({ v: 1, nodes: [
    { id: 'a', op: 'sin', a: 'b' }, { id: 'b', op: 'sin', a: 'a' }], out: 'a' });
  const dangling = C.compile({ v: 1, nodes: [{ id: 'a', op: 'sin', a: 'zzz' }], out: 'a' });
  const mk = (val) => ({ v: 1, nodes: [
    { id: 'u', op: 'uniform', name: 'uMix', type: 'float', value: val },
    { id: 'c0', op: 'const', v: [0, 0, 0], ty: 'v3' },
    { id: 'c1', op: 'const', v: [1, 1, 1], ty: 'v3' },
    { id: 'o', op: 'mix', a: 'c0', b: 'c1', t: 'u', ty: 'v3' },
  ], out: 'o' });
  const h1 = C.compile(mk(0.2)), h2 = C.compile(mk(0.9)), h3 = C.compile(mk(0.2));
  const hiVer = C.compile({ v: 99, nodes: [{ id: 'a', op: 'time' }], out: 'a' });
  // 0.34 补的三个 op：swizzle 取分量、step 二分、vec3 合成
  const vecG = C.compile({ v: 1, nodes: [
    { id: 'p', op: 'position', space: 'local', ty: 'v3' },
    { id: 's', op: 'swizzle', a: 'p', c: 'y', ty: 'f' },
    { id: 'st', op: 'step', x: 's', e0: 0, ty: 'f' },
    { id: 'v', op: 'vec3', x: 'st', y: 's', z: 0.5, ty: 'v3' },
  ], out: 'v' });
  return {
    badOk: bad.node === null, badErr: bad.errors.join('|'),
    cycOk: cyc.node === null, cycErr: cyc.errors.join('|'),
    danglingOk: dangling.node === null, danglingErr: dangling.errors.join('|'),
    h1: h1.hash, h2: h2.hash, h3: h3.hash,
    hiVerOk: hiVer.node === null, hiVerErr: hiVer.errors.join('|'),
    vecOk: !!vecG.node, vecType: vecG.outType, vecOps: vecG.ops || [],
    vecErr: vecG.errors.join('|'),
  };
})()`);

check('白名单拒绝未知 op（不 eval）', c.badOk === true && /未知的 shader op/.test(c.badErr), c);
check('环依赖被拒绝', c.cycOk === true && /有环/.test(c.cycErr), c);
check('悬空引用被拒绝', c.danglingOk === true && /不存在的节点/.test(c.danglingErr), c);
check('IR 版本高于运行时被拒绝', c.hiVerOk === true && /版本/.test(c.hiVerErr), c);
check('同一张图两次编译得到同一哈希', c.h1 && c.h1 === c.h3, c);
check('只改 uniform 值 → 哈希不变（S2 的缓存前提）', c.h1 && c.h1 === c.h2, c);
check('swizzle / step / vec3 三个 op 能被编译',
  c.vecOk && ['swizzle', 'step', 'vec3'].every((o) => c.vecOps.includes(o)), c);
check('vec3 的输出类型是 v3', c.vecType === 'v3', c.vecType);

/* ---------- S3：顶点位移（positionNode）---------- */
const d = await ev(`(async () => {
  const G = window.Colugo;
  G.selectScene('shaderdisplace');
  const t0 = performance.now();
  while (performance.now() - t0 < 20000) {
    const S = G.scene3d;
    if (S && S.backend && S.materials.size >= 1 && S.shader.stats.builds >= 2) break;
    await new Promise(r => setTimeout(r, 100));
  }
  await new Promise(r => setTimeout(r, 500));
  const S = G.scene3d;
  // 3D→3D 切场景时 mock 不清空（真实 GH 里 scene3d 也是持续的），
  // 所以这里可能有上一场残留的材质 —— 必须遍历找"带 positionNode 的那份"，不能取 keys[0]。
  const mats = [...S.materials.entries()].map(([k, mv]) => ({
    key: k,
    color: !!(mv && mv.colorNode),
    pos: !!(mv && mv.positionNode),
  }));
  const dispMat = mats.find((m) => m.pos) || null;
  return {
    objects: S.stats().objects,
    tris: S.stats().tris,
    matCount: mats.length,
    mats,
    key: dispMat ? dispMat.key : '',
    hasColorNode: dispMat ? dispMat.color : false,
    hasPositionNode: !!dispMat,
    ops: S.shader.lastOps || [],
    animated: !!S._shaderAnimated,
    err: S.lastShaderError || '',
  };
})()`);

check('位移场景就绪（网格进了场景）', d.objects >= 1 && d.tris > 1000, d);
check('顶点位移生效：有材质带上了 positionNode', d.hasPositionNode === true, d.mats);
check('那份材质同时也带颜色节点', d.hasColorNode === true, d.key);
check('位移图用到 vec3 / sin / swizzle',
  ['vec3', 'sin', 'swizzle'].every((o) => d.ops.includes(o)), d.ops);
check('位移图里有 time → 场景逐帧渲染', d.animated === true, d.animated);
check('颜色与位移两张图的哈希都进了材质键',
  (d.key.match(/[0-9a-f]{8}/g) || []).length >= 2, d.key);
check('位移编译没有报错', !d.err, d.err);

/* ---------- S5：Bloom 后处理 ---------- */
const fxOn = await ev(`(async () => {
  const G = window.Colugo;
  G.selectScene('bloom');
  const t0 = performance.now();
  while (performance.now() - t0 < 20000) {
    const S = G.scene3d;
    if (S && S._postFX && S.frames > 3) break;
    await new Promise(r => setTimeout(r, 100));
  }
  await new Promise(r => setTimeout(r, 600));
  const S = G.scene3d;
  return {
    hasPipeline: !!S._postFX,
    fxKey: S._fxKey || '',
    frames: S.frames,
    objects: S.stats().objects,
  };
})()`);

check('bloom 打开后建起了 RenderPipeline', fxOn.hasPipeline === true, fxOn);
check('bloom 参数写进了重建键（b/threshold/radius）', /b0\.850/.test(fxOn.fxKey), fxOn.fxKey);
check('挂后处理时场景照常出帧', fxOn.frames > 3 && fxOn.objects >= 1, fxOn);

/* ---------- 截图：像素证据 ---------- */
const shot = await send('Page.captureScreenshot', { format: 'png' });
if (shot.data) {
  const { writeFileSync } = await import('node:fs');
  writeFileSync(join(tmpdir(), 'colugo-shader.png'), Buffer.from(shot.data, 'base64'));
  console.log('  screenshot: ' + join(tmpdir(), 'colugo-shader.png'));
}

/* ---------- 关掉 bloom：pipeline 必须被拆掉，不能留着继续吃帧 ---------- */
const fxOff = await ev(`(() => {
  const S = window.Colugo.scene3d;
  const cfg = { ...S.config, fx: { bloom: 0, threshold: 0.3, radius: 1.2 } };
  S._setConfig(cfg);
  return { hasPipeline: !!S._postFX, fxKey: S._fxKey || '' };
})()`);

check('bloom = 0 时 pipeline 被拆掉', fxOff.hasPipeline === false, fxOff);
check('拆掉后重建键被清空（下次开启会重建）', fxOff.fxKey === '', fxOff.fxKey);

const bad = logs.filter((l) => /^EXC:/.test(l) || /^error:/.test(l));
check('无控制台错误', bad.length === 0, bad);

console.log(`\n${pass}/${pass + fail} passed`);
process.exit(fail ? 1 : 0);
