// shader.js — Shader IR → TSL 节点树
//
// 为什么是"扁平节点表 + 连接表"而不是嵌套 JSON：
// 增量更新要按节点 id 做 diff。嵌套结构一旦内层变，整棵树都得重发；
// 扁平表可以只发改动的节点。代价是这里要做一次拓扑求值 —— 但 GH 侧本来就要
// 拒绝循环依赖，那笔账跑不掉。
//
// 白名单分发，绝不 eval：IR 是从 .gh 文件来的字符串，等同于不可信输入。
//
// 类型是显式标注的（ty: 'f' | 'v2' | 'v3'）。TSL 自己的运算会重载，
// 所以这个标注只用来做两件事：给缺省值兜底、在需要 float 的地方（remap/step）报错。

export const SHADER_IR_VERSION = 1;

/** 节点 op → 求值函数。每个函数拿到已求值的入参，返回 TSL node。 */
const UNARY = {
  sin: (t, a) => t.sin(a),
  cos: (t, a) => t.cos(a),
  abs: (t, a) => t.abs(a),
  fract: (t, a) => t.fract(a),
  floor: (t, a) => t.floor(a),
  sqrt: (t, a) => t.sqrt(a),
  oneMinus: (t, a) => t.oneMinus(a),
  saturate: (t, a) => t.saturate(a),
};

const BINARY = {
  add: (t, a, b) => t.add(a, b),
  sub: (t, a, b) => t.sub(a, b),
  mul: (t, a, b) => t.mul(a, b),
  div: (t, a, b) => t.div(a, b),
  pow: (t, a, b) => t.pow(a, b),
  dot: (t, a, b) => t.dot(a, b),
};

/** 从 op 名推断输出类型。显式写了 ty 就用写的。 */
function inferType(node, ins) {
  if (node.ty) return node.ty;
  switch (node.op) {
    case 'position':
    case 'normal':
      return 'v3';
    case 'uv':
      return 'v2';
    case 'vec2':
      return 'v2';
    case 'vec3':
      return 'v3';
    case 'time':
      return 'f';
    case 'step':
      return ins.x ? ins.x.ty : 'f';
    case 'const':
      return Array.isArray(node.v) ? (node.v.length === 2 ? 'v2' : 'v3') : 'f';
    case 'uniform':
      return node.type === 'vec3' ? 'v3' : node.type === 'vec2' ? 'v2' : 'f';
    case 'dot':
    case 'length':
      return 'f';
    case 'mix':
      return ins.a ? ins.a.ty : 'f';
    case 'remap':
    case 'smoothstep':
    case 'clamp':
    case 'step':
      return ins.x ? ins.x.ty : 'f';
    default:
      if (BINARY[node.op] && ins.a) return ins.a.ty;
      if (UNARY[node.op] && ins.a) return ins.a.ty;
      return 'f';
  }
}

/** 稳定序列化：键排序后再拼，保证同一张图在任何机器上得到同一个哈希。 */
function stableKey(ir) {
  // uniform 的**值**不进哈希。这是 S2 的前提：拖滑条只改 uniform 值，
  // 结构没变就必须命中材质缓存，否则每次改色都重建材质、重编译 shader。
  // 只有 name / type 参与哈希 —— 它们变了确实意味着换了一张图。
  const nodes = (ir.nodes || []).map((n) => {
    if (!n || n.op !== 'uniform') return n;
    const { value, ...rest } = n;
    return rest;
  });

  const norm = (v) => {
    if (Array.isArray(v)) return v.map(norm);
    if (v && typeof v === 'object') {
      return Object.keys(v).sort().reduce((o, k) => { o[k] = norm(v[k]); return o; }, {});
    }
    return v;
  };
  return JSON.stringify(norm({ nodes, out: ir.out }));
}

/** FNV-1a 32 位。够用：只用来做材质缓存 key，不做安全用途。 */
function hash32(s) {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(16).padStart(8, '0');
}

export class ShaderCompiler {
  /**
   * @param {object} tsl  three/tsl 命名空间
   * @param {object} THREE three/webgpu
   */
  constructor(tsl, THREE) {
    this.tsl = tsl;
    this.THREE = THREE;
    /** name → { node, type, value }  同一 name 复用同一个 uniform 对象，否则每帧新对象会让 GPU 侧不断重建 */
    this.uniforms = new Map();
    /** 每次 compile 的统计，Runtime Info 直接读它 */
    this.stats = { builds: 0, failures: 0, uniformUpdates: 0 };
  }

  /** 取（或建）一个共享 uniform。S1 只有 Shared 作用域，PerObject 要等实例 uniform 通道。 */
  uniform(name, type, value) {
    const key = `${name}|${type}`;
    const hit = this.uniforms.get(key);
    if (hit) return hit.node;
    const start = type === 'vec3' ? [value?.[0] ?? 0, value?.[1] ?? 0, value?.[2] ?? 0] : (value ?? 0);
    const node = this.tsl.uniform(start);
    this.uniforms.set(key, { node, type, value: start });
    return node;
  }

  /** 把 GH 侧传来的值更新进 uniform。只改 .value，不重建节点 —— 这就是 S2 要省掉重编译的地方。 */
  setUniform(name, type, value) {
    const hit = this.uniforms.get(`${name}|${type}`);
    if (!hit) return false;
    const v = type === 'vec3' ? [value?.[0] ?? 0, value?.[1] ?? 0, value?.[2] ?? 0] : value;
    const tgt = hit.node.value;
    if (Array.isArray(v)) { tgt[0] = v[0]; tgt[1] = v[1]; tgt[2] = v[2]; }
    else hit.node.value = v;
    hit.value = v;
    this.stats.uniformUpdates++;
    return true;
  }

  /** 时钟统一走 uTime，不用 TSL 自带的 time —— 导出时要能暂停、跳帧、倍速。 */
  setTime(t) {
    this.setUniform('uTime', 'f', t);
  }

  /**
   * 编译一张 IR 图。
   * @returns {{ node: object|null, errors: string[], hash: string, uniforms: string[] }}
   * 失败时 node 为 null，调用方应回退到原来的常量色材质 —— 坏一个材质，不是坏一个作品。
   */
  compile(ir) {
    const errors = [];
    if (!ir || typeof ir !== 'object') return { node: null, errors: ['IR 为空'], hash: '' };
    if (ir.v != null && ir.v > SHADER_IR_VERSION) {
      return { node: null, errors: [`IR 版本 ${ir.v} 高于运行时支持的 ${SHADER_IR_VERSION}`], hash: '' };
    }
    const list = Array.isArray(ir.nodes) ? ir.nodes : [];
    if (!list.length) return { node: null, errors: ['IR 没有节点'], hash: '' };

    const byId = new Map();
    for (const n of list) {
      if (!n || !n.id) { errors.push('节点缺 id'); continue; }
      if (byId.has(n.id)) errors.push(`节点 id 重复：${n.id}`);
      byId.set(n.id, n);
    }

    const done = new Map();   // id → { node, ty }
    const visiting = new Set(); // 循环检测
    const usedUniforms = [];
    const usedOps = new Set();  // 用过的 op，诊断与验收都靠它 —— TSL 节点的 toString 是 [object Object]，读不出东西

    const t = this.tsl;
    const self = this;

    const ref = (v, wantTy) => {
      if (v == null) return null;
      if (typeof v === 'number') return t.float(v);
      if (Array.isArray(v)) {
        return v.length === 2 ? t.vec2(v[0], v[1]) : t.vec3(v[0], v[1], v[2]);
      }
      if (typeof v === 'string') {
        const r = resolve(v);
        if (!r) return null;
        if (wantTy && r.ty !== wantTy && !(wantTy === 'f' && r.ty === 'f')) {
          // 不在这里做隐式转换：类型不对就该在 GH 侧被发现，运行时静默转换会让问题更难找
          errors.push(`节点 ${v} 类型是 ${r.ty}，此处需要 ${wantTy}`);
        }
        return r.node;
      }
      return null;
    };

    const resolve = (id) => {
      if (done.has(id)) return done.get(id);
      const n = byId.get(id);
      if (!n) { errors.push(`引用了不存在的节点：${id}`); return null; }
      if (visiting.has(id)) { errors.push(`节点图有环：${id}`); return null; }
      visiting.add(id);
      const r = evalNode(n);
      visiting.delete(id);
      if (!r) return null;
      done.set(id, r);
      return r;
    };

    const evalNode = (n) => {
      const op = n.op;
      usedOps.add(op);
      let node = null;
      let ty = 'f';

      if (op === 'position') {
        node = n.space === 'world' ? t.positionWorld : t.positionLocal;
        ty = 'v3';
      } else if (op === 'normal') {
        node = n.space === 'world' ? t.normalWorld : t.normalLocal;
        ty = 'v3';
      } else if (op === 'uv') {
        node = t.uv();
        ty = 'v2';
      } else if (op === 'time') {
        node = self.uniform('uTime', 'f', 0);
        usedUniforms.push('uTime');
        ty = 'f';
      } else if (op === 'const') {
        const v = n.v;
        if (Array.isArray(v)) {
          node = v.length === 2 ? t.vec2(v[0], v[1]) : t.vec3(v[0], v[1], v[2]);
          ty = v.length === 2 ? 'v2' : 'v3';
        } else {
          node = t.float(Number(v) || 0);
          ty = 'f';
        }
      } else if (op === 'uniform') {
        const type = n.type === 'vec3' ? 'vec3' : n.type === 'vec2' ? 'vec2' : 'f';
        node = self.uniform(n.name, type, n.value);
        if (n.name) usedUniforms.push(n.name);
        ty = type === 'vec3' ? 'v3' : type === 'vec2' ? 'v2' : 'f';
      } else if (op === 'swizzle') {
        const a = ref(n.a);
        if (!a) return null;
        const f = n.c || 'x';
        if (f === 'x' || f === 'y' || f === 'z') { node = a[f]; ty = 'f'; }
        else { node = a; ty = 'v3'; }
      } else if (op === 'vec2' || op === 'vec3') {
        // 把若干标量拼成向量。没这个 op，"方向"这类值根本无法从 GH 里构造出来
        const x = ref(n.x ?? 0), y = ref(n.y ?? 0);
        if (!x || !y) return null;
        if (op === 'vec2') { node = t.vec2(x, y); ty = 'v2'; }
        else {
          const z = ref(n.z ?? 0);
          if (!z) return null;
          node = t.vec3(x, y, z);
          ty = 'v3';
        }
      } else if (op === 'step') {
        const edge = ref(n.e0 ?? 0.5), x = ref(n.x);
        if (!x) return null;
        node = t.step(edge, x);
        ty = byId.get(n.x) ? (done.get(n.x)?.ty || 'f') : 'f';
      } else if (op === 'mix') {
        const a = ref(n.a), b = ref(n.b), f = ref(n.t);
        if (!a || !b || !f) return null;
        node = t.mix(a, b, f);
        ty = byId.get(n.a) ? (done.get(n.a)?.ty || 'f') : 'f';
      } else if (op === 'remap') {
        // 手写而不是用 tsl.remap：签名带 clamp 行为差异，手写一份语义自己说了算
        const x = ref(n.x), i0 = ref(n.in0 ?? 0), i1 = ref(n.in1 ?? 1),
              o0 = ref(n.out0 ?? 0), o1 = ref(n.out1 ?? 1);
        if (!x || !i0 || !i1 || !o0 || !o1) return null;
        const k = t.div(t.sub(x, i0), t.sub(i1, i0));
        node = t.add(o0, t.mul(t.sub(o1, o0), k));
        ty = 'f';
      } else if (op === 'smoothstep') {
        const e0 = ref(n.e0 ?? 0), e1 = ref(n.e1 ?? 1), x = ref(n.x);
        if (!x) return null;
        node = t.smoothstep(e0, e1, x);
        ty = 'f';
      } else if (op === 'clamp') {
        const x = ref(n.x), lo = ref(n.lo ?? 0), hi = ref(n.hi ?? 1);
        if (!x) return null;
        node = t.clamp(x, lo, hi);
        ty = byId.get(n.x) ? (done.get(n.x)?.ty || 'f') : 'f';
      } else if (op === 'length') {
        const a = ref(n.a);
        if (!a) return null;
        node = a.length();
        ty = 'f';
      } else if (op === 'normalize') {
        const a = ref(n.a);
        if (!a) return null;
        node = t.normalize(a);
        ty = byId.get(n.a) ? (done.get(n.a)?.ty || 'v3') : 'v3';
      } else if (UNARY[op]) {
        const a = ref(n.a);
        if (!a) return null;
        node = UNARY[op](t, a);
        ty = byId.get(n.a) ? (done.get(n.a)?.ty || 'f') : 'f';
      } else if (BINARY[op]) {
        const a = ref(n.a), b = ref(n.b);
        if (!a || !b) return null;
        node = BINARY[op](t, a, b);
        ty = op === 'dot' ? 'f' : (byId.get(n.a) ? (done.get(n.a)?.ty || 'f') : 'f');
      } else {
        errors.push(`未知的 shader op：${op}`);
        return null;
      }

      if (!node) return null;
      ty = inferType(n, { a: byId.get(n.a) ? done.get(n.a) : null, x: byId.get(n.x) ? done.get(n.x) : null }) || ty;
      return { node, ty };
    };

    const rootId = ir.out || list[list.length - 1].id;
    const root = resolve(rootId);
    if (!root) {
      this.stats.failures++;
      return { node: null, errors: errors.length ? errors : ['图没有可用的输出节点'], hash: '' };
    }
    if (errors.length) {
      this.stats.failures++;
      return { node: null, errors, hash: '' };
    }

    this.stats.builds++;
    const hash = hash32(stableKey(ir));
    this.lastOps = [...usedOps];
    this.lastOutType = root.ty;
    return {
      node: root.node, errors: [], hash, outType: root.ty,
      uniforms: [...new Set(usedUniforms)], ops: [...usedOps],
    };
  }
}
