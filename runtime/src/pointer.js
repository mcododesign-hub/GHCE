// pointer.js — 交互输入（阶段 3 D）
//
// 只做一件事：把浏览器事件翻译成"页面坐标"，并且保证坐标系和绘画一致。
// 画布是等比缩放显示的，所以必须经过 renderer.clientToPage，不能直接用 clientX。

export class Pointer {
  constructor() {
    this.x = 0;
    this.y = 0;
    this.inside = false;
    this.down = false;
    this.downX = 0;
    this.downY = 0;
    this.keys = new Set();
    this.clicks = 0;      // 累计点击次数，供"点一下才发生"的作品用
    this.enabled = true;
  }

  attach(el, toPage) {
    const move = (e) => {
      const p = toPage(e.clientX, e.clientY);
      this.x = p.x;
      this.y = p.y;
      this.inside = true;
      if (this.down) { this.downX = p.x; this.downY = p.y; }
    };

    el.addEventListener('pointermove', move);
    el.addEventListener('pointerdown', (e) => {
      move(e);
      this.down = true;
      this.downX = this.x;
      this.downY = this.y;
      this.clicks++;
      el.setPointerCapture?.(e.pointerId);
    });
    el.addEventListener('pointerup', (e) => {
      move(e);
      this.down = false;
      try { el.releasePointerCapture?.(e.pointerId); } catch { /* ignore */ }
    });
    el.addEventListener('pointerleave', () => { this.inside = false; this.down = false; });

    addEventListener('keydown', (e) => {
      if (e.target && /INPUT|TEXTAREA/.test(e.target.tagName)) return;
      this.keys.add(e.key.toLowerCase());
    });
    addEventListener('keyup', (e) => this.keys.delete(e.key.toLowerCase()));
    addEventListener('blur', () => { this.keys.clear(); this.down = false; });
  }

  /** 键盘轴向：WASD / 方向键 */
  axis() {
    let ax = 0, ay = 0;
    if (this.keys.has('a') || this.keys.has('arrowleft')) ax -= 1;
    if (this.keys.has('d') || this.keys.has('arrowright')) ax += 1;
    if (this.keys.has('w') || this.keys.has('arrowup')) ay -= 1;
    if (this.keys.has('s') || this.keys.has('arrowdown')) ay += 1;
    return { ax, ay };
  }

  stats() {
    return {
      x: Math.round(this.x), y: Math.round(this.y),
      inside: this.inside, down: this.down,
      keys: [...this.keys].join(''),
    };
  }
}
