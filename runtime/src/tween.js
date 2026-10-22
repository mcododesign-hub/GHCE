// tween.js — 把"变换"本身当成动画来演
//
// 痛点：Transform 电池给的是终态矩阵。直接套上去，画面在第 0 帧就已经是结果，
// 看起来像"卡住"——因为它确实没有中间态。
//
// 立场：**变换本身就是那段动画**。GH 只管把"每条指令现在该在哪儿"发过来，
// 浏览器负责记录每一条指令上一次在哪儿，然后在这两者之间插值演过去。
// 所以这套东西不需要 progress 输入、不需要 Timeline，也不逐帧发包。
//
// 规则：
//   1. 指令第一次出现 → 从**原始状态**（identity）演到 GH 给的位置。
//      这就是"黑线变成绿线"的那个过程：cmds 是没变位置的几何，matrix 是变化，中间由这里补。
//   2. 矩阵变了 → 从"当前画面上的位置"演到新位置，耗时 duration 秒
//      起点取的是**插值中的当前值**，所以连续拖动滑块会平滑跟随，不会跳回上一帧的起点
//   3. 微小抖动不算变化（见 NOISE_*）：否则重算时最后几位浮点漂移会让画面永远在微微转
//   4. 事件动作 restart → 同一段变换从头再演一次

import { resolveMatrix } from './xform.js';

export const DEFAULT_DURATION = 0.9; // 秒

// 两次变化间隔小于这个值，就认为你在"逐帧驱动"（比如用 Time 电池喂一个循环），
// 这时候不能再起一段 0.9 秒的过渡 —— 否则每一帧都重新缓动，看起来就是一直在抖。
// 逐帧驱动的正确行为是**直通**：这一帧给什么就画什么。
const CONTINUOUS_GAP = 0.12;

// 过渡用**墙钟**，不用动画时钟：暂停只该停时间线，不该让一个已经开始的变换停在半路。
const nowSec = () => (typeof performance !== 'undefined' ? performance.now() : Date.now()) / 1000;

const same = (a, b) => {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
};

// "变化大到值得演一遍"的阈值。
// 前四个是旋转/缩放分量（无量纲），后两个是平移（像素）。
// 为什么要它：GH 每次重算都会产出数学上"相同"的矩阵，但最后几位浮点可能不同；
// 不设阈值的话，画面会永远在一个几乎看不见的微旋转里抖动。
const NOISE = [1e-5, 1e-5, 1e-5, 1e-5, 1e-3, 1e-3];
const moved = (a, b) => {
  if (!a || !b) return true;
  for (let i = 0; i < 6; i++) if (Math.abs(a[i] - b[i]) > NOISE[i]) return true;
  return false;
};

export class Tweens {
  constructor({ duration = DEFAULT_DURATION } = {}) {
    this.duration = duration;
    this.items = new Map();   // cmdId -> { from, to, t0, dur, active, done }
    this.stats = { active: 0, played: 0 };
  }

  /**
   * patch 应用之后调用：对比每条指令的矩阵，变化了就起一段过渡。
   * @param doc 文档
   * @param time 当前时钟
   * @returns 是否有新起的过渡
   */
  observe(doc, time = nowSec()) {
    let started = 0;
    const alive = new Set();

    for (const cmd of doc.cmds.values()) {
      const t = cmd.transform;
      if (!t || !t.matrix) continue;
      if (cmd._xformDriven) { this.items.delete(cmd.id); continue; }
      alive.add(cmd.id);

      const rec = this.items.get(cmd.id);
      if (!rec) {
        // 第一次出现：从原始状态（identity）演到 GH 给的位置。
        // 这就是用户要的"变换过程"——cmds 是没变位置的几何，matrix 是变化后的样子。
        this.items.set(cmd.id, { from: null, to: t.matrix, t0: time, dur: this.duration, active: true, done: false });
        started++;
        continue;
      }

      if (same(rec.to, t.matrix) || !moved(rec.to, t.matrix)) {
        // 矩阵没变（可能只是改了颜色/透明度，或者只是 GH 又算了一遍）：
        // 把过渡中的进度重新按上，否则 normalizeCmd 的缺省 progress=1 会让
        // 正在演的变换突然跳到终点。注意这里不更新 lastTarget —— 没变的帧
        // 不该让"逐帧驱动"的判定误判。
        if (rec.active) {
          t.from = rec.from;
          t.progress = clamp01((time - rec.t0) / rec.dur);
        } else {
          t.progress = 1;
        }
        continue;
      }

      // 真的变了。看距上一次变化过了多久：
      //   < 120ms → 你在逐帧喂数据（Time 电池做循环 / 拖滑块），直通，别缓动
      //   否则   → 一次性改动，起一段过渡
      const gap = time - (rec.lastTarget == null ? -1 : rec.lastTarget);
      rec.lastTarget = time;

      if (gap > 0 && gap < CONTINUOUS_GAP) {
        rec.from = t.matrix;
        rec.to = t.matrix;
        rec.active = false;
        rec.done = true;
        t.progress = 1;
        continue;
      }

      // 起点 = 此刻画面上的矩阵（可能是上一段过渡的中间态）
      const nowOnScreen = rec.active
        ? resolveMatrix({ matrix: rec.to, progress: clamp01((time - rec.t0) / rec.dur), from: rec.from })
        : rec.to;

      rec.from = nowOnScreen;
      rec.to = t.matrix;
      rec.t0 = time;
      rec.dur = this.duration;
      rec.active = true;
      rec.done = false;
      started++;
    }

    // 指令被删掉了，记录也跟着走
    for (const id of [...this.items.keys()]) if (!alive.has(id)) this.items.delete(id);

    if (started) this.stats.played += started;
    return started;
  }

  /**
   * 每帧调用：把进度写回指令。
   * @returns 是否有变化（决定要不要重绘）
   */
  update(doc, time = nowSec()) {
    let touched = false;
    let active = 0;

    for (const [id, rec] of this.items) {
      const cmd = doc.cmds.get(id);
      // 有东西在显式驱动 xform（旧定义的轨道 / 振荡器）时让位：
      // 两套机制每帧抢写同一个字段，只会互相打架。
      if (!cmd || !cmd.transform) { this.items.delete(id); continue; }
      if (cmd._xformDriven) { this.items.delete(id); continue; }

      if (rec.active) {
        let p = (time - rec.t0) / rec.dur;
        if (!(p > 0)) p = 0;
        if (p >= 1) { p = 1; rec.active = false; rec.done = true; }
        else { active++; touched = true; }

        cmd.transform.from = rec.from;
        cmd.transform.progress = p;
      }
    }

    this.stats.active = active;
    return touched;
  }

  /** 事件动作 restart：这些指令的变换从头再演一遍 */
  restart(ids, time = nowSec()) {
    let n = 0;
    for (const id of ids || []) {
      const rec = this.items.get(id);
      if (!rec) continue;
      rec.t0 = time;
      rec.active = true;
      rec.done = false;
      n++;
    }
    return n;
  }

  clear() {
    this.items.clear();
    this.stats.active = 0;
  }
}

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
