# TSL 沙箱

验证 Three.js r186 的 WebGPU 渲染器 + TSL 节点材质，在**没有打包工具**的浏览器里能否跑通。
这是阶段 5 的技术前提 —— 如果这里起不来，整个 3D 路线要换方案。

## 复现

```bash
# 1. 取 three 的三个 build 文件（约 3.8 MB，不入仓库）
cd mock/tsl-sandbox
npm init -y >/dev/null && npm install three
mkdir -p vendor
cp node_modules/three/build/{three.core.js,three.webgpu.js,three.tsl.js} vendor/

# 2. 起静态服务（必须带 no-store，且别走系统代理）
node -e "
require('http').createServer(async (q,s)=>{
  const {readFile}=require('fs/promises'); const {extname}=require('path');
  try{ const b=await readFile('.'+(q.url==='/'?'/index.html':q.url.split('?')[0]));
    s.writeHead(200,{'Content-Type':extname(q.url)==='.js'?'text/javascript':'text/html','Cache-Control':'no-store'});
    s.end(b); }catch{ s.writeHead(404); s.end(); }
}).listen(8891,'127.0.0.1',()=>console.log('8891'));
"

# 3. 两个后端各跑一次
cd ../..
URL='http://127.0.0.1:8891/'          node mock/tsl-probe.mjs   # WebGPU
URL='http://127.0.0.1:8891/?webgl=1'  node mock/tsl-probe.mjs   # 强制 WebGL2
```

## 实测结论（2026-10-23，本机）

| 后端 | 结果 |
|---|---|
| WebGPU | `backend: "WebGPUBackend"`，343 帧，出图 |
| WebGL2 | `backend: "WebGLBackend"`，354 帧，出图，画面一致 |

截图：`docs/shots/m5-tsl-webgpu.png` / `m5-tsl-webgl.png`

## 为什么要测 WebGL2

WebView2 里 WebGPU 不可靠（Microsoft 明确不建议在生产应用里用 flags 开启），
所以 GH 的预览窗大概率走 WebGL2。TSL 的价值就是同一份节点代码两个后端都能编译。

## 两个坑

1. **`three/tsl` 用裸标识符**，必须有 `importmap`；`three.webgpu.js` 内部用相对路径
   `./three.core.js`，所以三个文件必须在同一目录。
2. **不要用 python `-m http.server` 在子 shell 里起服务** —— 父 shell 退出后进程被回收，
   浏览器请求到一半就断，表现是"页面白屏 + 无 console 错误"，很容易误判成 TSL 挂了。
