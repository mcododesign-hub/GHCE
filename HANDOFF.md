# 迁移与续作说明（HANDOFF）

打包时间：2026-10-22 23:30 · 插件版本 **GHCE 0.11.0** · 19 只电池

这个包是一份**可继续开发的完整工作副本**，不是只读快照。到新机器上按下面顺序做，
最短路径是"不编译，直接装预编译的 `.gha`"。

---

## 0. 这是什么

Grasshopper 当创作前端，浏览器当执行环境。GH 里的曲线/样式/动画编译成一份 JSON 协议，
推给运行着的网页渲染；改几何只重传变化的部分，不刷新页面。

三块组成，互不依赖编译：

| 部分 | 位置 | 作用 |
|---|---|---|
| GH 插件 | `gh/GHCE`（C# / net7.0-windows） | 17 只电池，编译并推送，内嵌预览窗（WebView2） |
| 浏览器运行时 | `runtime/`（原生 JS，零依赖） | Canvas 2D 渲染、实体系统、时间线、导出 |
| 模拟桥 + 验收 | `mock/`（Node，仅依赖 `ws`） | 不开 Rhino 也能演示；CDP 驱动的真浏览器断言 |

运行时页面被**编进 `.gha`**，所以装完插件就能用，不需要 node、不需要另开浏览器。

---

## 1. 新机器上最快的一条路（不需要 dotnet）

前提：装了 **Rhino 8**（.NET 7 运行时随 Rhino 一起装好）。

```powershell
# 1) 解压后进 gh 目录
cd "<解压位置>\gh"

# 2) 安装（会自动检查 Rhino 是否占用文件，并校验 md5）
.\install.ps1
```

看到 `已安装 GHCE.gha  <字节数> bytes  md5 ...` 就成了（脚本会自己比对源与目标的 md5）。然后：

```
打开 Rhino → 命令 Grasshopper → 左侧标签页找 GHCE
```

**不需要 node，也不需要那台机器上有 dotnet**——`install.ps1` 只是复制文件。

---

## 2. 要改代码才需要的（编译）

前提：**真正的 .NET SDK**（不是仅运行时）。先自检：

```powershell
dotnet --list-sdks
```

有输出才能编。如果报 "No SDK" 或命令不存在，有两种情况：

- 这台机器装了 Visual Studio → 用 `dotnet` 应该正常
- 只装了运行时 → 去装 .NET SDK 7 或更新版本；或沿用第 1 节直接装预编译包

日常用的两条命令：

```powershell
cd "<解压位置>\gh\GHCE"
dotnet build -c Release          # 产物 bin/Release/net7.0-windows/GHCE.gha

cd ..\probe
dotnet build -c Release
dotnet run -c Release             # 加载冒烟 + 变换数学断言，末尾应打印 RESULT: PASS
```

**Rhino 路径已改成自动探测**（C/D/E 盘 + `%ProgramFiles%`），换机器不用改 csproj。
装在其他位置时显式指定：

```powershell
dotnet build -c Release -p:RhinoDir="F:\Rhino 8"
```

找不到时构建会直接报一句人话，不会抛一堆 `HintPath` 谜语。

**运行时页面是嵌入资源**：改了 `runtime/` 里的 js/html 之后必须重新
`dotnet build`（不是重启 Rhino）才会进 `.gha`。

---

## 3. 目录里都是什么

```
README.md                    总览
HANDOFF.md                   本文件
Grasshopper Creative Coding Engine.md   最初的任务书
docs/
  protocol-v0.1.md           协议定义（op 列表、坐标语义、动画、状态回传）
  stage-plan.md              阶段 0–7 的实际状态，哪些做了哪些没做
  shots/                     各阶段截图证据
runtime/
  index.html                 HUD + 舞台
  src/doc.js                 文档状态 + op 应用
  src/geom.js                几何解码 / Path2D / 指纹
  src/renderer2d.js          Canvas 2D 渲染（含 reveal、matrix、fade）
  src/entity.js              动态对象：6 种行为、生命周期、拖尾
  src/anim.js                时间线：关键帧插值、LUT 缓动
  src/clock.js               播放/暂停/重置
  src/pointer.js             鼠标键盘 → 页面坐标
  src/svg.js                 SVG 导出
mock/
  dev.mjs                    一条命令起运行时页面 + 模拟桥
  mock-gh.mjs                模拟 GH 侧（5 个演示场景）
  verify.mjs                 CDP 真浏览器断言（38 项）
  replay-csharp.mjs          把 C# 真实产出的 patch 灌进运行时
gh/
  install.ps1                安装 + 校验
  build.md                   构建细节、每个坑、每个已知边界
  GHCE/                      插件源码（含预编译产物 bin/）
  probe/                     加载冒烟 + 数值断言
```

`obj/`、`probe/bin/`、`mock/node_modules/` 没有打包：都是可再生的派生产物，
带着反而会因为绝对路径和增量缓存出错。

---

## 4. 验证（跑一遍确认没坏）

不需要 Rhino，只需要 Node 18+：

```powershell
# 终端 1：起演示
cd "<解压位置>\mock"
npm install                       # 只有一个依赖：ws
$env:RUNTIME_PORT=5273; $env:PORT=8877
node dev.mjs

# 终端 2：跑断言（会启动真 Chrome，无头）
cd "<解压位置>\mock"
$env:PAGE='http://127.0.0.1:5273/?ws=ws://127.0.0.1:8877/ghce'
node verify.mjs
```

末尾应为 `38/38 passed`。截图落在 `%TEMP%\ghce-*.png`。

**为什么换端口**：8777 是插件的默认端口，如果 Rhino 正开着，插件已经占住了它。
上面这套用 5273/8877 避开。

C# 侧的验证（需要第 2 节的编译环境）：

```powershell
cd gh\probe
dotnet run -c Release
# 期望最后一行 RESULT: PASS
# 其中四条是变换数学：anchor / orbit / shift / fallback
```

---

## 5. 已知边界（别当成 bug 找）

- **相机在 Path Input 里一次框定**，不随几何旋转重算。绕自身中心转是稳的；
  细长条转 90° 可能出框被裁 —— 那是相机没动，调 `Margin` 或 `Page W/H`。
- **`Xform` 必须在世界坐标里合成**。GH 自带 `Move/Rotate/Scale` 输出的是几何，不是
  Transform 对象；要合成得自己写 C#/Python 或组矩阵。变换走到页面会做一次
  `P·M·P⁻¹`（相机共轭），这部分**已数值验证**。
- **运行时状态回传是单向快照**：GH 能读到帧率/对象数/指针，但还不能反过来改定义。
- **未做**：Oscillator、Event Trigger、Selective Clear、阶段 4（画笔/字体/图像）、
  阶段 5（3D/Shader）。别按任务书目录找，看 `docs/stage-plan.md`。
- **`Draw Path` 有 10 个输入**，超过"≤6"的约定。用户明确要求保持原样，不要擅自拆。

---

## 6. 踩过的坑（会再踩一次）

| 现象 | 原因 | 处理 |
|---|---|---|
| 改了代码、重启 Rhino 没变化 | Rhino 锁着 `.gha`，`cp` 静默失败 | 一律走 `install.ps1`，它会查进程并校验 md5 |
| `dotnet` 报 No SDK | PATH 里的是**只有运行时**的那份 | `dotnet --list-sdks` 自检；本机 SDK 在 `C:\Users\<用户>\.dotnet` |
| GH 里整个 GHCE 标签页都不出现 | 某个类型的字段引用了找不到的程序集（如 WebView2），`GetTypes()` 整体抛异常 | 已用 `[ModuleInitializer]` 挂 `AssemblyResolve`；新增第三方引用必须配这个钩子 |
| 参数类型显示棋盘格 | 参数的图标不能覆写 `Icon`（恒 null），走 `Internal_Icon_24x24` | 已在 `Icons.cs` 里按参数单独画 |
| 切换场景后测量值像是"时间倒流" | `selectScene` 走 WS，快照还没到就开始采样 | 等可判定标志（如 `rev === 1`）再采样 |
| 预览窗连不上 | 端口自动避让后，页面里 WS 地址还是 8777 | 统一用 `BridgeServer.PreviewUrl`（带 `?ws=`） |

---

## 7. 下一步建议

阶段 0–3 的 P0 项已闭环。接下来优先级（详见 `docs/stage-plan.md`）：

1. 在 GH 里用一个**真实定义**把 13 只电池串起来跑一遍 —— 目前所有验证都是
   "运行时 + 协议 + C# 单点"，还没有一个端到端作品。
2. 若需要"相机框住整个动画过程"，给 Path Input 加可选 `Camera` 输入（端口 6 → 7）。
3. 阶段 3 尾巴：Oscillator / Event Trigger / Selective Clear。
