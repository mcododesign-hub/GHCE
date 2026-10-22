// bridge-client.js — 与 GH（或模拟桥）的 WebSocket 连接
// 职责边界：只管传输、重连、回执；不解析文档语义

export class BridgeClient {
  constructor(url, { onPatch, onStatus, onError } = {}) {
    this.url = url;
    this.onPatch = onPatch || (() => {});
    this.onStatus = onStatus || (() => {});
    this.onError = onError || (() => {});
    this.ws = null;
    this.state = 'idle';
    this.seq = 0;
    this.appliedRev = -1;
    this.retry = 0;
    this.metrics = { patches: 0, lastPatchMs: 0, lastBytes: 0, lastOps: 0, rtt: 0, statSent: 0 };
    this._timer = null;
    this._closed = false;
  }

  connect() {
    this._closed = false;
    this._setState('connecting');
    let ws;
    try {
      ws = new WebSocket(this.url);
    } catch (e) {
      this.onError({ code: 'CONNECT_FAIL', msg: String(e) });
      return this._scheduleRetry();
    }
    this.ws = ws;
    ws.binaryType = 'arraybuffer';

    ws.onopen = () => {
      this.retry = 0;
      this._send({
        v: '0.1',
        t: 'hello',
        client: 'ghce-runtime',
        caps: { backends: ['canvas2d', 'svg'], dpr: Math.min(window.devicePixelRatio || 1, 2) },
      });
      this._setState('open');
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data !== 'string') return;
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.v !== '0.1') {
        this.onError({ code: 'VERSION_MISMATCH', msg: `接收 v=${msg.v}` });
        return;
      }
      if (msg.t === 'patch') {
        const t0 = performance.now();
        const res = this.onPatch(msg) || {};
        this.metrics.patches++;
        this.metrics.lastPatchMs = performance.now() - t0;
        this.metrics.lastBytes = ev.data.length;
        this.metrics.lastOps = (msg.ops || []).length;
        if (typeof msg.rev === 'number') this.appliedRev = Math.max(this.appliedRev, msg.rev);
        this._send({ v: '0.1', t: 'ack', seq: ++this.seq, rev: this.appliedRev, ms: +this.metrics.lastPatchMs.toFixed(2), rejected: res.rejected || 0 });
      } else if (msg.t === 'error') {
        this.onError(msg);
      } else if (msg.t === 'pong') {
        this.metrics.rtt = performance.now() - (this._pongAt || performance.now());
      } else if (msg.t === 'welcome') {
        this._setState('open', msg);
      }
    };

    ws.onclose = () => {
      this.ws = null;
      if (!this._closed) {
        this._setState('closed');
        this._scheduleRetry();
      }
    };
    ws.onerror = () => { /* onclose 会接着触发，避免重复报错 */ };
  }

  _scheduleRetry() {
    clearTimeout(this._timer);
    const wait = Math.min(4000, 400 * Math.pow(1.7, this.retry++));
    this._setState('retry', { wait });
    this._timer = setTimeout(() => this.connect(), wait);
  }

  _setState(s, extra) {
    this.state = s;
    this.onStatus(s, extra);
  }

  _send(obj) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(obj));
      return true;
    }
    return false;
  }

  /** 仅模拟桥使用；真实 GH 插件忽略 */
  switchScenario(name) {
    return this._send({ v: '0.1', t: 'switch', name });
  }

  /** 通用发送（调试 / 模拟桥控制） */
  send(msg) {
    return this._send(msg);
  }

  ping() {
    this._pongAt = performance.now();
    this._send({ v: '0.1', t: 'ping' });
  }

  close() {
    this._closed = true;
    clearTimeout(this._timer);
    if (this.ws) this.ws.close();
  }
}
