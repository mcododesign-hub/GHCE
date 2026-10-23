// events.js — 事件触发（阶段 3 D）+ 按区域擦除（阶段 3 C 的 Selective Clear）
//
// 立场：触发规则由 GH 发一次，命中后由浏览器执行 —— 一次点击不需要 GH 重新求解。
//
// v0.12 起事件**绑定在指令上**：Event Trigger 是管道电池，喂进去哪几条指令，
// 命中的就只有那几条。规则结构：
//   { id, event: 'click'|'dblclick'|'key', action, detail, targets: [cmdId...] }
//
// 动作分两类：
//   逐指令  toggle / show / hide / restart      → 只改 targets 里的指令
//   全局    clear / clear-region / reset-time / toggle-play
//           → 画布只有一张、时钟只有一个，作用域天然是全局（喂哪组指令都一样）
//
// v0.30 起多了一种更通用的规则：**写变量**（`writes`）。
//   { id, event, detail, writes: [{ var, op: 'set'|'toggle'|'add', value }] }
// 事件不再直接碰指令，而是改 doc.vars；谁读这个变量由表达式绑定决定。
// 老规则（action）继续认，两者可以同时挂在一个 id 上。

export const EVENT_TRIGGERS = {
  // v0.30.1：除点击与键盘，补上指针生命周期事件。
  // 高频的 pointermove 也走同一条派发路径 —— 规则表很小，逐帧遍历不是瓶颈；
  // 真正贵的是"每次写完变量都要重绘"，那由 varsDirty 控制，不在这里。
  events: ['click', 'dblclick', 'key', 'pointerdown', 'pointerup', 'pointermove', 'enter', 'leave', 'wheel'],
  actions: ['toggle', 'show', 'hide', 'restart', 'clear', 'clear-region', 'reset-time', 'toggle-play'],
  perTarget: ['toggle', 'show', 'hide', 'restart'],
};

// 旧名字 → 新名字（v0.11 的 restart-time / restart-objects）。
// 运行时也要认，因为老 patch 可能还在路上、测试数据也可能用旧名。
const ALIAS = { 'restart-time': 'reset-time', 'restart-objects': 'restart' };
const canon = (a) => ALIAS[String(a || '').trim().toLowerCase()] || String(a || '').trim().toLowerCase();

/** 写变量的三种算子；未知值退化成 set，不静默变成别的语义。 */
const WRITES = ['set', 'toggle', 'add'];
const normalizeWrite = (o) => {
  const w = String(o || '').trim().toLowerCase();
  return WRITES.includes(w) ? w : 'set';
};

function parseRegion(detail) {
  const nums = String(detail || '').match(/-?\d+(\.\d+)?/g);
  if (!nums || nums.length < 4) return null;
  const [x, y, w, h] = nums.map(Number);
  return { x, y, w, h };
}

export class EventTriggers {
  /**
   * @param rt { clock, doc, tweens, entities, renderer, requestDraw }
   */
  constructor(rt) {
    this.rt = rt;
    this.items = new Map();      // id -> { id, event, action, detail, targets }
    this.stats = { triggers: 0, fired: 0, last: null };
    this._bound = false;
    this._onClick = () => this.fire('click');
    this._onDbl = () => this.fire('dblclick');
    this._onKey = (e) => this.fire('key', { key: e.key });
    this._onDown = () => this.fire('pointerdown');
    this._onUp = () => this.fire('pointerup');
    this._onMove = () => this.fire('pointermove');
    this._onEnter = () => this.fire('enter');
    this._onLeave = () => this.fire('leave');
    // 滚轮带方向：写变量时用 add 就能做"每滚一格 +1"
    this._onWheel = (e) => this.fire('wheel', { sign: e.deltaY > 0 ? 1 : -1 });
    this._host = null;
  }

  /** 把监听挂到画布上（pointer 只在画布内算命中，键盘挂 window） */
  bind(canvas) {
    if (this._bound || !canvas) return;
    this._bound = true;
    this._host = canvas;
    canvas.addEventListener('click', this._onClick);
    canvas.addEventListener('dblclick', this._onDbl);
    canvas.addEventListener('pointerdown', this._onDown);
    canvas.addEventListener('pointerup', this._onUp);
    canvas.addEventListener('pointermove', this._onMove);
    canvas.addEventListener('pointerenter', this._onEnter);
    canvas.addEventListener('pointerleave', this._onLeave);
    canvas.addEventListener('wheel', this._onWheel, { passive: true });
    window.addEventListener('keydown', this._onKey);
  }

  unbind() {
    if (!this._bound || !this._host) return;
    this._host.removeEventListener('click', this._onClick);
    this._host.removeEventListener('dblclick', this._onDbl);
    this._host.removeEventListener('pointerdown', this._onDown);
    this._host.removeEventListener('pointerup', this._onUp);
    this._host.removeEventListener('pointermove', this._onMove);
    this._host.removeEventListener('pointerenter', this._onEnter);
    this._host.removeEventListener('pointerleave', this._onLeave);
    this._host.removeEventListener('wheel', this._onWheel);
    window.removeEventListener('keydown', this._onKey);
    this._bound = false;
    this._host = null;
  }

  clear() {
    this.items.clear();
    this.stats.triggers = 0;
  }

  applyOps(ops) {
    if (!ops || !ops.length) return;
    for (const op of ops) {
      if (!op) continue;
      if (op.op === 'doc.reset') { this.clear(); continue; }
      if (op.op === 'event.remove') { this.items.delete(String(op.id)); continue; }
      if (op.op === 'event.upsert') {
        const v = op.value || {};
        if (!op.id || !v.event) continue;
        const writes = Array.isArray(v.writes)
          ? v.writes
              .filter((w) => w && w.var)
              .map((w) => ({
                var: String(w.var),
                op: normalizeWrite(w.op),
                value: Number.isFinite(Number(w.value)) ? Number(w.value) : 0,
                min: Number.isFinite(Number(w.min)) ? Number(w.min) : -Infinity,
                max: Number.isFinite(Number(w.max)) ? Number(w.max) : Infinity,
              }))
          : [];
        // 既没有动作也没有写入 —— 这条规则什么都不会发生，别占着表
        if (!writes.length && !v.action) continue;
        this.items.set(String(op.id), {
          id: String(op.id),
          event: String(v.event),
          action: v.action ? canon(v.action) : null,
          detail: v.detail == null ? '' : String(v.detail),
          targets: Array.isArray(v.targets) ? v.targets.map(String) : [],
          writes,
        });
      }
    }
    this.stats.triggers = this.items.size;
  }

  /** 命中一次事件。extra.key 用于匹配 key 类触发器的 detail */
  fire(eventName, extra = {}) {
    let fired = 0;
    for (const t of this.items.values()) {
      if (t.event !== eventName) continue;
      if (eventName === 'key') {
        const want = (t.detail || '').trim().toLowerCase();
        if (want && want !== (extra.key || '').toLowerCase()) continue;
      }
      this.run(t);
      fired++;
    }
    if (fired) {
      this.stats.fired += fired;
      this.stats.last = eventName;
      this.rt.requestDraw();
    }
    return fired;
  }

  run(t) {
    const { clock, doc, tweens, entities, renderer } = this.rt;
    const ids = t.targets || [];

    // ---- 写变量（v0.30）：唯一的通用出口 ----
    if (t.writes && t.writes.length) {
      for (const w of t.writes) {
        const cur = doc.vars.has(w.var) ? Number(doc.vars.get(w.var)) : 0;
        let next;
        if (w.op === 'toggle') next = cur ? 0 : 1;
        else if (w.op === 'add') next = cur + w.value;
        else next = w.value;
        next = Math.min(w.max, Math.max(w.min, next));
        if (Number.isFinite(next)) {
          doc.vars.set(w.var, next);
          doc.varsDirty = true;      // 下一帧必须重绘，否则变量变了画面不动
        }
      }
    }
    if (!t.action) return;

    switch (t.action) {
      // ---- 逐指令 ----
      case 'toggle': {
        for (const id of ids) {
          const c = doc.cmds.get(id);
          if (c) c.visible = !c.visible;
        }
        break;
      }
      case 'show':
      case 'hide': {
        const on = t.action === 'show';
        for (const id of ids) {
          const c = doc.cmds.get(id);
          if (c) c.visible = on;
        }
        break;
      }
      case 'restart': {
        // 把这段变换从头再演一次（见 tween.js）
        if (tweens) tweens.restart(ids);
        break;
      }

      // ---- 画布 / 时钟（全局）----
      case 'clear':
        renderer.clear();
        break;
      case 'clear-region': {
        const r = parseRegion(t.detail);
        if (!r) return;
        renderer.clearRegion(r.x, r.y, r.w, r.h);
        break;
      }
      case 'reset-time':
        clock.reset();
        if (entities) entities.restart?.();
        break;
      case 'toggle-play':
        clock.toggle();
        break;
      default:
        break;
    }
  }

  /** 键盘触发器的 detail 是键名；留空表示任意键 */
  describe() {
    return [...this.items.values()].map((t) => {
      const n = t.targets.length ? ` ×${t.targets.length}` : '';
      return `${t.event}→${t.action}${t.detail ? `(${t.detail})` : ''}${n}`;
    });
  }
}
