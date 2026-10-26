// run-verify.mjs — 一条命令：起服务 → 跑验收 → 收工
//
// 为什么需要它：后台常驻的 dev.mjs 在这台机器上会被沙箱回收，
// 每次都变成"手动起服务 → 跑脚本 → 忘了关端口"。这里把三件事放进同一个进程：
// 进程退出时监听器随之消失，端口不会残留。
//
//   node run-verify.mjs                跑 verify.mjs（2D + 表达式 + Mark + 行为 + 图片）
//   node run-verify.mjs verify-3d.mjs  跑指定的验收脚本
import { spawn } from 'node:child_process';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const target = process.argv[2] || 'verify.mjs';

process.env.PORT ||= '8790';
process.env.RUNTIME_PORT ||= '5173';

await import('./dev.mjs');

const page = process.env.PAGE || `http://127.0.0.1:${process.env.RUNTIME_PORT}/?ws=ws://127.0.0.1:${process.env.PORT}/ghce`;

// 等两个端口真的能连上，再启动验收：serve 与 mock 的 listen 不是同步完成的
const waitPort = async (url, ms = 8000) => {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    try {
      const r = await fetch(url, { method: 'GET' });
      if (r.ok || r.status === 404) return true;
    } catch { /* 还没起来 */ }
    await new Promise((r) => setTimeout(r, 100));
  }
  return false;
};

// WS 端口要单独等：页面一加载就去连，比它先起来就会记录一条 ERR_CONNECTION_REFUSED，
// 而"无控制台错误"那条断言是严格的 —— 表现是偶发失败，查起来很浪费时间。
const waitTcp = (port, ms = 8000) => new Promise((resolve) => {
  const t0 = Date.now();
  const tick = () => {
    const sock = net.connect({ host: '127.0.0.1', port: Number(port) });
    sock.once('connect', () => { sock.destroy(); resolve(true); });
    sock.once('error', () => {
      sock.destroy();
      if (Date.now() - t0 > ms) resolve(false);
      else setTimeout(tick, 120);
    });
  };
  tick();
});

const ok = await waitPort(`http://127.0.0.1:${process.env.RUNTIME_PORT}/`)
  && await waitTcp(process.env.PORT);
if (!ok) {
  console.error('服务没有起来，验收中止');
  process.exit(2);
}

const child = spawn(process.execPath, [join(here, target)], {
  stdio: 'inherit',
  env: { ...process.env, PAGE: page, NO_PROXY: '*', no_proxy: '*' },
});

child.on('exit', (code) => process.exit(code ?? 1));
