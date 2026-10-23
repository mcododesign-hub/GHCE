// 最小验证：Three.js WebGPU 渲染器 + TSL 节点材质，在"无打包工具"的浏览器里能否跑通。
// 环境与 WebView2 最接近的部分是：WebGPU 可能不可用 → 自动回退 WebGL2。
import {
  WebGPURenderer, Scene, PerspectiveCamera, Mesh, BoxGeometry,
  MeshStandardNodeMaterial, DirectionalLight, AmbientLight, REVISION,
} from 'three/webgpu';
import {
  color, positionLocal, normalLocal, uv, time, sin, mix,
} from 'three/tsl';

const probe = window.__probe = {
  revision: REVISION,
  backend: null,
  isWebGL: null,
  frames: 0,
  errors: [],
  ok: false,
};

try {
  // WebView2 里 WebGPU 不可靠，实际会走 WebGL2。用 ?webgl=1 强制走那条路验证。
  const forceWebGL = new URLSearchParams(location.search).get('webgl') === '1';
  probe.forced = forceWebGL;
  const renderer = new WebGPURenderer({ antialias: true, forceWebGL });
  renderer.setSize(640, 400);
  renderer.setPixelRatio(1);
  document.body.appendChild(renderer.domElement);

  const scene = new Scene();
  const camera = new PerspectiveCamera(45, 640 / 400, 0.1, 100);
  camera.position.set(2.4, 1.8, 2.8);
  camera.lookAt(0, 0, 0);

  const mat = new MeshStandardNodeMaterial();

  // 颜色随 UV 变化：这是"节点着色"而不是固定色
  mat.colorNode = mix(color('#07a954'), color('#fd7ab4'), uv().x);

  // 顶点位移：沿法线按位置的正弦推出去，随时间脉动
  mat.positionNode = positionLocal.add(
    normalLocal.mul(sin(time.mul(1.6).add(positionLocal.y.mul(3.0))).mul(0.06))
  );

  // 自发光跟着同一相位呼吸
  mat.emissiveNode = color('#fd7ab4').mul(
    sin(time.mul(2.0)).mul(0.5).add(0.5)
  ).mul(0.45);

  const box = new Mesh(new BoxGeometry(1.2, 1.2, 1.2, 24, 24, 24), mat);
  scene.add(box);
  scene.add(new DirectionalLight('#ffffff', 2.2).translateY(3));
  scene.add(new AmbientLight('#223344', 1.4));

  // init() 会决定走 WebGPU 还是回退 WebGL2
  await renderer.init();
  probe.backend = renderer.backend?.constructor?.name || 'unknown';
  probe.isWebGL = !!renderer.backend?.isWebGLBackend;

  renderer.setAnimationLoop(() => {
    box.rotation.y += 0.01;
    renderer.render(scene, camera);
    probe.frames++;
    if (probe.frames > 8) probe.ok = true;
  });
} catch (e) {
  probe.errors.push(String(e && e.stack || e));
  console.error('[probe]', e);
}

addEventListener('error', (e) => probe.errors.push(String(e.message || e)));
addEventListener('unhandledrejection', (e) => probe.errors.push(String(e.reason)));
