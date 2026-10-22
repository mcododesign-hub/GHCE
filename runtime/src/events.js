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

export const EVENT_TRIGGERS = {
  events: ['click', 'dblclick', 'key'],
  actions: ['toggle', 'show', 'hide', 'restart', 'clear', 'clear-region', 'reset-time', 'toggle-play'],
  perTarget: ['toggle', 'show', 'hide', 'restart'],
};

// 旧名字 → 新名字（v0.11 的 restart-time / restart-objects）。
// 运行时也要认，因为老 patch 可能还在路上、测试数据也可能用旧名。
const ALIAS = { 'restart-time': 'reset-time', 'restart-objects': 'restart' };
const canon = (a) => ALIAS[String(a || '').trim().toLowerCase()] || String(a || '').trim().toLowerCase();

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
    this._host = null;
  }

  /** 把监听挂到画布上（pointer 只在画布内算命中，键盘挂 window） */
  bind(canvas) {
    if (this._bound || !canvas) return;
    this._bound = true;
    this._host = canvas;
    canvas.addEventListener('click', this._onClick);
    canvas.addEventListener('dblclick', this._onDbl);
    window.addEventListener('keydown', this._onKey);
  }

  unbind() {
    if (!this._bound || !this._host) return;
    this._host.removeEventListener('click', this._onClick);
    this._host.removeEventListener('dblclick', this._onDbl);
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
        if (!op.id || !v.event || !v.action) continue;
        this.items.set(String(op.id), {
          id: String(op.id),
          event: String(v.event),
          action: canon(v.action),
          detail: v.detail == null ? '' : String(v.detail),
          targets: Array.isArray(v.targets) ? v.targets.map(String) : [],
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
