// scene3d.js — 三维层
//
// 职责边界：只负责把协议里的 mesh / cmd3d / scene3d 变成 Three.js 对象并画出来。
// 不做任何解析、不做差分、不认识 GH。所有增量判断都在 C# 的 PatchBuilder 里。
//
// 与二维层的关系：两个 canvas，两个渲染循环，互不驱动。
// Three.js 是动态 import 的 —— 不用 3D 的作品不会为那 2.3 MB 付一分钱。

import { ShaderCompiler } from './shader.js';

let _three = null;
let _tsl = null;

async function loadThree() {
  if (_three) return { THREE: _three, tsl: _tsl };
  // import map 把 three/webgpu 指到 vendor/three.webgpu.js，three/tsl 指到 vendor/three.tsl.js
  let tw, tsl;
  try {
    tw = await import('three/webgpu');
  } catch (e) {
    console.error('[scene3d] three/webgpu 解析失败', e);
    throw e;
  }
  try {
    tsl = await import('three/tsl');
  } catch (e) {
    console.error('[scene3d] three/tsl 解析失败', e);
    throw e;
  }
  _three = tw;
  _tsl = tsl;
  return { THREE: tw, tsl };
}

const MODE = { SOLID: 1, WIRE: 2, POINTS: 3, NORMALS: 4, UNLIT: 5 };

export class Scene3D {
  constructor(canvas) {
    this.canvas = canvas;
    this.T = null;          // 加载完的 three 模块
    this.tsl = null;

    this.meshes = new Map();   // id -> { geometry, count, tris }
    this.cmds = new Map();     // id -> cmd def
    this.objects = new Map();  // id -> Object3D
    this.materials = new Map();// materialKey -> Material
    this.shader = null;        // ShaderCompiler，three 加载后才有

    this.config = null;        // scene3d.set 的载荷
    this.scene = null;
    this.camera = null;
    this.renderer = null;
    this.lights = null;

    this.backend = null;
    this._ready = false;   // renderer.init() 之前调 render() 会被 three 拒绝
    this.frames = 0;
    this.lastMs = 0;
    this._needsFrame = false;
    this._queue = [];
    this._loading = null;
    this._dirty = false;
    this._time = 0;
    // 有材质用了 uTime 就必须逐帧跑：否则第 566 行的提前返回会让 Shader 定格
    this._shaderAnimated = false;
    this.lastShaderError = '';
    // 后处理（S5）：fx 配置变了才重建，否则每帧重建 pipeline 等于每帧新建 RT
    this._postFX = null;
    this._fxKey = '';
    // 2D 画布当纹理用（S4）：两侧渲染循环不同，纹理只在 2D 真的重画过之后才上传
    this.canvasTexture = null;
    this._srcCanvas = null;
    // 只有真的有材质采样了 canvasTex，才值得每帧上传整张画布 ——
    // 否则一个 2D 动画场景会把 1440×900 的纹理每帧传一遍 GPU，直接把渲染拖死。
    this._usesCanvasTex = false;

    // 视图旋转（不改 GH 数据，只改相机；在自动取景的基础上叠加）
    this._orbit = { yaw: 0, pitch: 0, dragging: false, lastX: 0, lastY: 0 };
    this._bindOrbit();
  }

  /**
   * S4：把 2D 画布挂成纹理源。
   * 2D 和 3D 是两个独立循环（各自 requestAnimationFrame 驱动，但同一个 tick 里 2D 先画），
   * 所以纹理不能每帧无条件上传 —— 那样等于每帧把整张画布传一遍 GPU。
   * 由调用方在"2D 真的重画了"之后调 markSourceDirty()。
   */
  setSourceCanvas(canvas) {
    if (!canvas) return;
    // 顺序很重要：先判断 three 有没有加载完，再判断"是不是同一张画布"。
    // 反过来的话，第一次调用会把 _srcCanvas 设上，加载完再调就被当成重复而早退，
    // 纹理永远建不出来。
    if (!this.T) { this._pendingSource = canvas; return; }
    if (this._srcCanvas === canvas && this.canvasTexture) return;
    this._srcCanvas = canvas;
    this.canvasTexture = this.T.CanvasTexture ? new this.T.CanvasTexture(canvas) : null;
    if (this.canvasTexture) {
      this.canvasTexture.flipY = true;
      this.canvasTexture.needsUpdate = true;
      this._dirty = true;
    }
  }

  markSourceDirty() {
    if (!this._usesCanvasTex || !this.canvasTexture) return;
    this.canvasTexture.needsUpdate = true;
    this._dirty = true;
  }

  /** three 加载完成前收到的 op 全部排队，加载后一次性回放 */
  applyOps(ops) {
    if (!this.T) {
      for (const o of ops) this._queue.push(o);
      this._load();
      return;
    }
    this._apply(ops);
  }

  async _load() {
    if (this._loading) return;
    this._loading = (async () => {
      try {
        const { THREE, tsl } = await loadThree();
        this.T = THREE;
        this.tsl = tsl;
        this.shader = new ShaderCompiler(tsl, THREE, () => this.canvasTexture);
        if (this._pendingSource) {
          const c = this._pendingSource;
          this._pendingSource = null;
          this.setSourceCanvas(c);
        }
        this._initRenderer();
        const queued = this._queue;
        this._queue = [];
        if (queued.length) this._apply(queued);
      } catch (e) {
        console.error('[scene3d] three 加载失败', e);
        this._loading = null;
      }
    })();
  }

  _initRenderer() {
    const THREE = this.T;
    this.renderer = new THREE.WebGPURenderer({ canvas: this.canvas, antialias: true });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
    this.renderer.init().then(() => {
      this.backend = this.renderer.backend?.isWebGLBackend ? 'webgl2' : 'webgpu';
      this._ready = true;      // 后端就绪，第一帧从这里开始
      if (this._size) this.resize(this._size.w, this._size.h);
      this._syncPostFX();      // 配置可能早于 init 到达，这里补建
      this._dirty = true;
    }).catch((e) => {
      console.error('[scene3d] renderer init 失败', e);
      this.backend = 'failed';
    });

    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.01, 1e7);
    // Rhino 的世界是 Z 向上。不设这个，模型会躺着。
    this.camera.up.set(0, 0, 1);
    this._needsFrame = true;
  }

  // ------------------------------------------------------------------
  // 协议
  // ------------------------------------------------------------------

  _apply(ops) {
    for (const op of ops) {
      switch (op.op) {
        case 'mesh.upsert': this._upsertMesh(op.id, op.value); break;
        case 'mesh.remove': this._removeMesh(op.id); break;
        case 'cmd3d.upsert': this._upsertCmd(op.id, op.value); break;
        case 'cmd3d.remove': this._removeCmd(op.id); break;
        // S2：只有 uniform 值变了。不重建材质、不重传几何，只改 uniform 的 value。
        // 如果这个名字还没注册（比如首帧走的是 upsert），这里什么都不做 —— 那是正常时序，
        // 下一次结构变化会把整条 cmd 发过来。
        case 'shader.uniform':
          if (this.shader) this.shader.setUniform(op.name, op.type, op.value);
          break;
        case 'scene3d.set': this._setConfig(op.value); break;
        case 'scene3d.clear': this._clear(); break;
        default: break;
      }
    }
    this._dirty = true;
  }

  _upsertMesh(id, v) {
    const THREE = this.T;
    if (!v || !v.positions) return;

    let rec = this.meshes.get(id);
    if (!rec) {
      rec = { geometry: new THREE.BufferGeometry(), count: 0, tris: 0, kind: 'mesh' };
      this.meshes.set(id, rec);
    }
    rec.kind = v.kind || 'mesh';

    const g = rec.geometry;
    const pos = decodeF32(v.positions);
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));

    if (v.indices) {
      const idx = decodeU32(v.indices);
      g.setIndex(new THREE.Uint32BufferAttribute(idx, 1));
      // 线段每两个索引一段，三角每三个
      rec.tris = rec.kind === 'lines' ? 0 : idx.length / 3;
      rec.segments = rec.kind === 'lines' ? idx.length / 2 : 0;
    }

    if (v.normals) {
      g.setAttribute('normal', new THREE.Float32BufferAttribute(decodeF32(v.normals), 3));
    } else {
      g.deleteAttribute('normal');
    }

    if (v.uvs) {
      g.setAttribute('uv', new THREE.Float32BufferAttribute(decodeF32(v.uvs), 2));
    }

    if (!v.normals) g.computeVertexNormals();

    // 包围球用于自动取景与视锥剔除
    g.computeBoundingSphere();
    rec.count = v.count || pos.length / 3;

    // 已经画出来的对象要换几何
    const cmd = this._cmdUsingMesh(id);
    if (cmd) this._rebuildObject(cmd);
  }

  _removeMesh(id) {
    const rec = this.meshes.get(id);
    if (!rec) return;
    rec.geometry.dispose();
    this.meshes.delete(id);
    const cmd = this._cmdUsingMesh(id);
    if (cmd) this._rebuildObject(cmd);
  }

  _cmdUsingMesh(meshId) {
    for (const c of this.cmds.values()) if (c.mesh === meshId) return c;
    return null;
  }

  _upsertCmd(id, v) {
    if (!v || !v.mesh) return;
    const prev = this.cmds.get(id);
    this.cmds.set(id, { id, ...v });

    const changedGeometry = !prev || prev.mesh !== v.mesh
      || prev.material?.mode !== v.material?.mode
      || prev.material?.color !== v.material?.color
      || prev.material?.size !== v.material?.size
      || prev.material?.glow !== v.material?.glow
      || prev.material?.opacity !== v.material?.opacity;

    if (changedGeometry || !this.objects.has(id)) this._rebuildObject(this.cmds.get(id));
    else this._applyXform(this.objects.get(id), v.xform);
  }

  _removeCmd(id) {
    this.cmds.delete(id);
    const obj = this.objects.get(id);
    if (!obj) return;
    this.scene.remove(obj);
    this.objects.delete(id);
    this._needsFrame = true;
  }

  _setConfig(v) {
    if (!v) return;
    this.config = v;

    if (v.bg) {
      this.scene.background = new this.T.Color(v.bg);
    } else {
      this.scene.background = null;
    }

    this._syncLights(v);
    this._syncCamera(v.camera);

    // 顺序与可见性由 scene3d.set 的 objects 数组决定
    const order = v.objects || [];
    for (let i = 0; i < order.length; i++) {
      const obj = this.objects.get(order[i]);
      if (obj) obj.renderOrder = i;
    }
    for (const [id, obj] of this.objects) obj.visible = order.includes(id);

    this._syncPostFX();
    this._needsFrame = true;
  }

  /**
   * S5：bloom 用一条**亮度加权模糊**近似，不需要多级 RT 链 ——
   * 每次采样原图时同时算亮度掩码再累加，等价于"只对亮的部分做模糊"。
   * RenderPipeline.outputNode 直接吃节点图，所以整条链是一张图，没有额外的全屏 quad pass。
   */
  _syncPostFX() {
    if (!this._ready || !this.T || !this.shader) return;
    const fx = (this.config && this.config.fx) || {};
    const bloom = Number(fx.bloom);
    const on = Number.isFinite(bloom) && bloom > 0;

    if (!on) {
      if (this._postFX) {
        try { this._postFX.pipeline.dispose(); } catch { /* 已经没了 */ }
        this._postFX = null;
        this._fxKey = '';
      }
      return;
    }

    const threshold = Number.isFinite(Number(fx.threshold)) ? Number(fx.threshold) : 0.55;
    const radius = Number.isFinite(Number(fx.radius)) ? Number(fx.radius) : 1;
    const key = `b${bloom.toFixed(3)}|t${threshold.toFixed(3)}|r${radius.toFixed(3)}`;
    if (key === this._fxKey && this._postFX) return;

    try {
      if (this._postFX) { try { this._postFX.pipeline.dispose(); } catch { /* ignore */ } }
      this._postFX = this._buildBloom(bloom, threshold, radius);
      this._fxKey = key;
    } catch (e) {
      console.warn('[scene3d] 后处理构建失败，回退直接渲染：', e);
      this._postFX = null;
      this._fxKey = '';
    }
  }

  _buildBloom(bloom, threshold, radius) {
    const THREE = this.T;
    const { tsl } = this;

    const pipeline = new THREE.RenderPipeline(this.renderer);
    const scenePass = tsl.pass(this.scene, this.camera);
    const tex = scenePass.getTextureNode();

    const uv = tsl.uv();
    const base = tex.sample(uv);

    const w = Math.max(1, (this._size && this._size.w) || this.canvas.width || 1280);
    const h = Math.max(1, (this._size && this._size.h) || this.canvas.height || 720);
    const texel = tsl.vec2(1 / w, 1 / h);

    const LUM = (c) => c.r.mul(0.2126).add(c.g.mul(0.7152)).add(c.b.mul(0.0722));

    // 13 抽头：内圈 9 个做主体，外圈 4 个拉出辉光的"长尾巴"
    const step = 1.4 * radius;
    const taps = [
      [0, 0], [1, 0], [-1, 0], [0, 1], [0, -1],
      [0.7, 0.7], [-0.7, 0.7], [0.7, -0.7], [-0.7, -0.7],
      [2.4, 0], [-2.4, 0], [0, 2.4], [0, -2.4],
    ];

    let acc = null;
    for (const [dx, dy] of taps) {
      const o = texel.mul(tsl.vec2(dx * step, dy * step));
      const smp = tex.sample(uv.add(o));
      const mask = tsl.smoothstep(threshold, threshold + 0.25, LUM(smp));
      const term = smp.rgb.mul(mask);
      acc = acc ? acc.add(term) : term;
    }
    const glow = acc.div(taps.length);

    pipeline.outputNode = tsl.vec4(base.rgb.add(glow.mul(bloom)), 1);
    return { pipeline, render: () => pipeline.render() };
  }

  _syncLights(v) {
    const THREE = this.T;
    if (!v.lights) {
      if (this.lights) { this.scene.remove(this.lights); this.lights = null; }
      return;
    }
    if (this.lights) this.scene.remove(this.lights);

    const rig = new THREE.Group();
    const k = v.ambient == null ? 1 : v.ambient;
    rig.add(new THREE.AmbientLight('#8fa8c8', 1.2 * k));
    const key = new THREE.DirectionalLight('#ffffff', 2.6 * k);
    key.position.set(1, -1.6, 1.4);
    rig.add(key);
    const fill = new THREE.DirectionalLight('#7fb0ff', 0.9 * k);
    fill.position.set(-1.4, -0.6, 0.4);
    rig.add(fill);

    this.lights = rig;
    this.scene.add(rig);
  }

  _syncCamera(cam) {
    const THREE = this.T;
    if (cam && cam.hasPos) {
      this.camera = this.camera.isPerspectiveCamera
        ? this.camera
        : new THREE.PerspectiveCamera(45, 1, 0.01, 1e7);

      const pos = cam.pos || [0, 0, 100];
      const tgt = cam.hasTarget ? (cam.target || [0, 0, 0]) : [0, 0, 0];
      this.camera.up.set(0, 0, 1);
      this.camera.fov = cam.fov || 45;
      this.camera.position.set(pos[0], pos[1], pos[2]);
      this.camera.lookAt(tgt[0], tgt[1], tgt[2]);
      this._explicitCamera = true;
    } else {
      // 没给相机：每帧按包围盒自动框，等价于"居中放置"
      this._explicitCamera = false;
      this._needsFrame = true;
    }
  }

  _rebuildObject(cmd) {
    const THREE = this.T;
    const old = this.objects.get(cmd.id);
    if (old) {
      this.scene.remove(old);
      this.objects.delete(cmd.id);
    }

    const rec = this.meshes.get(cmd.mesh);
    if (!rec) return;

    const kind = rec.kind || 'mesh';
    const mat = this._material(cmd.material, kind);
    let obj;

    if (kind === 'lines') {
      obj = new THREE.LineSegments(rec.geometry, mat);
    } else if (kind === 'points' || cmd.material.mode === MODE.POINTS) {
      obj = new THREE.Points(rec.geometry, mat);
    } else {
      // 线框也走 Mesh：wireframe 材质比 LineSegments 省一半顶点
      obj = new THREE.Mesh(rec.geometry, mat);
    }

    obj.name = cmd.id;
    obj.visible = true;
    this._applyXform(obj, cmd.xform);
    this.scene.add(obj);
    this.objects.set(cmd.id, obj);
    this._needsFrame = true;
  }

  _material(m, kind = 'mesh') {
    const THREE = this.T;
    const { tsl } = this;

    // Shader IR：先编译，再算缓存 key。
    // key 里放的是**结构哈希**，不含 uniform 值 —— 拖滑条改颜色时哈希不变，
    // 材质直接命中缓存，不重建、不重编译。这就是 S2 的验收前提。
    let shaderNode = null;
    let shaderHash = '';
    if (m.shader) {
      const r = this.shader.compile(m.shader);
      if (r.node) {
        shaderNode = r.node;
        shaderHash = r.hash;
        this.lastShaderError = '';
        if (r.uniforms && r.uniforms.includes('uTime')) this._shaderAnimated = true;
        if (r.ops && r.ops.includes('canvasTex')) this._usesCanvasTex = true;
      } else {
        // 坏一个材质，不是坏一个作品：回退到常量色，诊断留给 Runtime Info 读
        this.lastShaderError = r.errors.join('; ');
        console.warn('[scene3d] shader 编译失败，回退常量色：', r.errors);
      }
    }

    // 顶点偏移：0.36。语义是"局部空间位移"，所以算 positionLocal + 图，
    // 而不是直接拿图当 positionNode —— 后者会让用户接一个 sin 波就把几何塌到原点。
    let dispNode = null;
    let dispHash = '';
    if (m.shaderDisplace) {
      const r = this.shader.compile(m.shaderDisplace);
      if (r.node) {
        dispNode = r.node;
        dispHash = r.hash;
        if (r.uniforms && r.uniforms.includes('uTime')) this._shaderAnimated = true;
      } else {
        this.lastShaderError = r.errors.join('; ');
        console.warn('[scene3d] 顶点偏移编译失败，跳过位移：', r.errors);
      }
    }

    const key = `${kind}|${m.mode}|${m.color}|${m.size}|${m.glow}|${m.opacity}|${shaderHash}|${dispHash}`;
    const hit = this.materials.get(key);
    if (hit) return hit;

    const c = new THREE.Color(m.color || '#e8e6df');
    // 接了 shader 就用节点树，没接就用常量色 —— 旧定义因此一行都不用改
    const colorNode = shaderNode || tsl.color(c);
    let mat;

    // 点与线只有一种画法，Mode 对它们没有意义 —— 用颜色和尺寸就够
    if (kind === 'lines') {
      mat = new THREE.LineBasicNodeMaterial();
      mat.colorNode = colorNode;
      mat.transparent = (m.opacity != null && m.opacity < 1);
      mat.opacity = m.opacity == null ? 1 : m.opacity;
      this.materials.set(key, mat);
      return mat;
    }
    if (kind === 'points') {
      mat = new THREE.PointsNodeMaterial();
      mat.colorNode = colorNode;
      mat.size = m.size || 2;
      // 点大小在 WebGPU 下改不了：WGSL 的 point-list 固定 1 像素，
      // mat.size 和 sizeNode 都不生效。three 的注释（three.webgpu.js:29584）说得很清楚：
      // 只有用 Sprite 渲染点才 honored。保留这行是为了将来切 Sprite 路径时有地方挂尺寸。
      mat.sizeNode = tsl.float(m.size || 2);
      mat.sizeAttenuation = false;
      mat.transparent = (m.opacity != null && m.opacity < 1);
      mat.opacity = m.opacity == null ? 1 : m.opacity;
      this.materials.set(key, mat);
      return mat;
    }

    switch (m.mode) {
      case MODE.WIRE: {
        mat = new THREE.MeshBasicNodeMaterial({ wireframe: true });
        mat.colorNode = colorNode;
        break;
      }
      case MODE.POINTS: {
        mat = new THREE.PointsNodeMaterial();
        mat.colorNode = colorNode;
        mat.size = m.size || 2;
        mat.sizeNode = tsl.float(m.size || 2);
        mat.sizeAttenuation = false;
        break;
      }
      case MODE.NORMALS: {
        // 接了 shader 时"法线着色"没有意义 —— 颜色已经由用户那棵树决定
        if (shaderNode) {
          mat = new THREE.MeshBasicNodeMaterial();
          mat.colorNode = colorNode;
          break;
        }
        mat = new THREE.MeshNormalNodeMaterial();
        break;
      }
      case MODE.UNLIT: {
        mat = new THREE.MeshBasicNodeMaterial();
        mat.colorNode = colorNode;
        break;
      }
      default: {
        mat = new THREE.MeshStandardNodeMaterial();
        mat.colorNode = colorNode;
        mat.roughness = 0.42;
        mat.metalness = 0.05;
        if (m.glow > 0) mat.emissiveNode = tsl.color(c).mul(m.glow);
        break;
      }
    }

    mat.transparent = (m.opacity != null && m.opacity < 1);
    mat.opacity = m.opacity == null ? 1 : m.opacity;
    if (mat.transparent) mat.depthWrite = false;

    // 顶点位移只对网格成立：点云和线没有"面"可推，强行套 positionNode 只会炸掉拓扑。
    // mode 3 是 points（虽然 kind 是 mesh，材质却是 PointsNodeMaterial）。
    const displaceable = kind === 'mesh' && m.mode !== 3;
    if (dispNode && displaceable) {
      mat.positionNode = tsl.positionLocal.add(dispNode);
    }

    this.materials.set(key, mat);
    return mat;
  }

  /** 单个矩阵 → set；帧序列 → 按时间插值 */
  _applyXform(obj, x) {
    if (!obj) return;
    const THREE = this.T;

    if (!x || (!x.matrix && !x.frames)) {
      obj.matrixAutoUpdate = true;
      obj.position.set(0, 0, 0);
      obj.quaternion.identity();
      obj.scale.set(1, 1, 1);
      return;
    }

    if (x.matrix && !x.frames) {
      obj.matrixAutoUpdate = false;
      obj.matrix.fromArray(decodeF32(x.matrix));
      obj.matrixWorldNeedsUpdate = true;
      return;
    }

    // 帧序列：按 GH 给的 Duration 循环（缺省 1.5s）。接 Track 之前，这是唯一的时间参数。
    const n = x.n || 0;
    const frames = x.frames ? decodeF32(x.frames) : null;
    if (!frames || n < 2) return;

    const span = Number.isFinite(x.duration) && x.duration > 1e-4 ? x.duration : 1.5;
    const u = ((this._time / span) % 1 + 1) % 1;
    const f = u * (n - 1);
    const i0 = Math.floor(f);
    const i1 = Math.min(n - 1, i0 + 1);
    const t = f - i0;

    const a = new THREE.Matrix4().fromArray(frames, i0 * 16);
    const b = new THREE.Matrix4().fromArray(frames, i1 * 16);
    const out = new THREE.Matrix4();
    lerpMatrix(a, b, t, out);

    obj.matrixAutoUpdate = false;
    obj.matrix.copy(out);
    obj.matrixWorldNeedsUpdate = true;
  }

  // ------------------------------------------------------------------
  // 取景与渲染
  // ------------------------------------------------------------------

  frameScene() {
    const THREE = this.T;
    if (!this.scene || this.objects.size === 0) return;

    this.scene.updateMatrixWorld(true);
    const box = new THREE.Box3();
    let any = false;
    for (const obj of this.objects.values()) {
      if (!obj.visible) continue;
      box.expandByObject(obj);
      any = true;
    }
    if (!any || box.isEmpty()) return;

    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const radius = Math.max(size.length() * 0.5, 1e-4);
    const fov = (this.camera.fov || 45) * Math.PI / 180;
    const dist = (radius / Math.sin(fov / 2)) * 1.15;

    // 等轴方向，比正对 Z 轴更容易看出体积
    const dir = new THREE.Vector3(0.62, -0.96, 0.72).normalize();
    const orbit = new THREE.Vector3(
      Math.sin(this._orbit.yaw) * Math.cos(this._orbit.pitch),
      -Math.cos(this._orbit.yaw) * Math.cos(this._orbit.pitch),
      Math.sin(this._orbit.pitch),
    );
    const d = dir.add(orbit.multiplyScalar(0.9)).normalize();

    this.camera.position.copy(center).add(d.multiplyScalar(dist));
    this.camera.up.set(0, 0, 1);
    this.camera.lookAt(center);
    this.camera.near = Math.max(dist * 0.001, 0.01);
    this.camera.far = dist * 100;
    this.camera.updateProjectionMatrix();
  }

  _bindOrbit() {
    const el = this.canvas;
    if (!el) return;
    el.style.pointerEvents = 'none';

    const onDown = (e) => {
      if (!this.config) return;
      this._orbit.dragging = true;
      this._orbit.lastX = e.clientX;
      this._orbit.lastY = e.clientY;
      el.setPointerCapture?.(e.pointerId);
    };
    const onMove = (e) => {
      if (!this._orbit.dragging) return;
      const dx = e.clientX - this._orbit.lastX;
      const dy = e.clientY - this._orbit.lastY;
      this._orbit.lastX = e.clientX;
      this._orbit.lastY = e.clientY;
      this._orbit.yaw += dx * 0.006;
      this._orbit.pitch = Math.max(-1.2, Math.min(1.2, this._orbit.pitch + dy * 0.006));
      this._needsFrame = true;
    };
    const onUp = () => { this._orbit.dragging = false; };

    el.addEventListener('pointerdown', onDown);
    addEventListener('pointermove', onMove);
    addEventListener('pointerup', onUp);
  }

  /**
   * 每帧核对一次 CSS 尺寸与当前记录。初始化时 canvas 可能还没布局（clientWidth = 0），
   * 一次性的 resize 调用不可靠 —— 让它自愈比猜调用时机稳。
   */
  _syncSize() {
    const w = this.canvas.clientWidth;
    const h = this.canvas.clientHeight;
    // DPR 也要比：把窗口拖到另一块不同缩放的显示器时，CSS 尺寸不变但像素密度变了，
    // 只看 w/h 的话画布会一直用旧的 pixelRatio，看起来就是糊的。
    const dpr = Math.min(devicePixelRatio || 1, 2);
    if (w > 0 && h > 0
        && (!this._size || this._size.w !== w || this._size.h !== h || this._dpr !== dpr)) {
      this.resize(w, h);
    }
  }

  resize(w, h) {
    // renderer 建好之前收到的尺寸要留住 —— main.js 是在构造后立刻调用的，
    // 那时 this.renderer 还是 null。不记住的话画布 buffer 会停在 300×150，
    // 被 CSS 拉到全屏，看起来就是"糊"。
    if (w > 0 && h > 0) this._size = { w, h };
    if (!this.renderer) return;
    this._dpr = Math.min(devicePixelRatio || 1, 2);
    this.renderer.setPixelRatio(this._dpr);
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / Math.max(1, h);
    this.camera.updateProjectionMatrix();
    this._needsFrame = true;
  }

  render(dt) {
    if (!this.renderer || !this.scene || !this.camera || !this._ready) return 0;
    this._syncSize();
    this._time += dt;
    if (this.shader) this.shader.setTime(this._time);

    const animated = this._shaderAnimated
      || [...this.cmds.values()].some((c) => c.xform && c.xform.frames);
    if (!this._dirty && !this._needsFrame && !animated) return 0;

    const t0 = performance.now();

    // 帧序列要每帧重算矩阵
    if (animated) {
      for (const c of this.cmds.values()) {
        if (c.xform && c.xform.frames) this._applyXform(this.objects.get(c.id), c.xform);
      }
    }

    if (this._needsFrame) {
      this._needsFrame = false;
      this.frameScene();
    }

    if (this._postFX) this._postFX.render();
    else this.renderer.render(this.scene, this.camera);
    this.frames++;

    const ms = performance.now() - t0;
    this.lastMs = ms;
    this._dirty = false;
    return ms;
  }

  _clear() {
    this._usesCanvasTex = false;
    for (const [id, obj] of this.objects) {
      this.scene.remove(obj);
      this.objects.delete(id);
    }
    for (const rec of this.meshes.values()) rec.geometry.dispose();
    for (const m of this.materials.values()) m.dispose();
    this.meshes.clear();
    this.cmds.clear();
    this.materials.clear();
    this.config = null;
    this._dirty = true;
    this._needsFrame = true;
  }

  stats() {
    let tris = 0, segments = 0, points = 0;
    for (const rec of this.meshes.values()) {
      tris += rec.tris;
      segments += rec.segments || 0;
      if (rec.kind === 'points') points += rec.count;
    }
    return {
      backend: this.backend,
      objects: this.objects.size,
      meshes: this.meshes.size,
      tris,
      segments,
      points,
      frames: this.frames,
      ms: +this.lastMs.toFixed(2),
    };
  }
}

// ----------------------------------------------------------------------
// 解码：与 C# 的 Buffer.BlockCopy 对齐（小端）
// ----------------------------------------------------------------------

function decodeF32(b64) {
  if (!b64) return new Float32Array(0);
  const bin = atob(b64);
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
  return new Float32Array(buf.buffer);
}

function decodeU32(b64) {
  if (!b64) return new Uint32Array(0);
  const bin = atob(b64);
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
  return new Uint32Array(buf.buffer);
}

/** 矩阵逐分量线性插值。不做极分解 —— 帧序列本来就来自同一个连续变形，近似足够。 */
function lerpMatrix(a, b, t, out) {
  const A = a.elements, B = b.elements, O = out.elements;
  for (let i = 0; i < 16; i++) O[i] = A[i] + (B[i] - A[i]) * t;
  return out;
}
