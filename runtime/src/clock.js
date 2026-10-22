// clock.js — 动画时钟（阶段 3 A）
//
// 为什么单独一层：GH 的求解节奏不能决定画面节奏。
// 这里管的是"浏览器时间"：Play/Pause、TimeScale、Reset、帧计数。
// 模拟本身仍然用固定步长（EntitySystem 内部 1/60 累加），时钟只负责给"真实经过了多少秒"。

export class Clock {
  constructor() {
    this.time = 0;          // 运行时时间（受 playing / timeScale 影响）
    this.wallTime = 0;      // 墙钟时间（不受影响，用于测量）
    this.frame = 0;
    this.playing = true;
    this.timeScale = 1;
  }

  /** 返回本帧应推进的时间（秒）。暂停或 timeScale=0 时返回 0 */
  step(realDt) {
    const dt = Math.max(0, Math.min(0.25, realDt));
    this.wallTime += dt;
    if (this.playing) this.time += dt * this.timeScale;
    return this.playing ? dt * this.timeScale : 0;
  }

  reset() {
    this.time = 0;
    this.frame = 0;
  }

  play() { this.playing = true; }
  pause() { this.playing = false; }
  toggle() { this.playing = !this.playing; }

  stats() {
    return { time: this.time, frame: this.frame, playing: this.playing, timeScale: this.timeScale };
  }
}
