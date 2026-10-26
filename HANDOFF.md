# 迁移与续作说明（HANDOFF）

打包时间：2026-10-26 15:10 · 插件版本 **Colugo 0.33.0** · 50 只电池 · 147 项断言

这个包是一份**可继续开发的完整工作副本**，不是只读快照。到新机器上按下面顺序做，
最短路径是"不编译，直接装预编译的 `.gha`"。

---

## 0. 这是什么

Grasshopper 当创作前端，浏览器当执行环境。GH 里的曲线/样式/动画编译成一份 JSON 协议，
推给运行着的网页渲染；改几何只重传变化的部分，不刷新页面。

三块组成，互不依赖编译：

| 部分 | 位置 | 作用 |
|---|---|---|
| GH 插件 | `gh/Colugo`（C# / net7.0-windows） | 50 只电池，编译并推送，内嵌预览窗（WebView2） |
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

看到 `已安装 Colugo.gha  <字节数> bytes  md5 ...` 就成了（脚本会自己比对源与目标的 md5）。然后：

```
打开 Rhino → 命令 Grasshopper → 左侧标签页找 Colugo
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
cd "<解压位置>\gh\Colugo"
dotnet build -c Release          # 产物 bin/Release/net7.0-windows/Colugo.gha

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

**探针跑不起来、报 "must install .NET 7"**：编译只需要 SDK，**运行**需要 net7.0 运行时。
本机只有 .NET 10 运行时时，直接跑 `probe.exe` 会被拒。加环境变量绕过：

```powershell
$env:DOTNET_ROLL_FORWARD='LatestMajor'
dotnet bin\Release\net7.0-windows\probe.dll
```

`dotnet run` 不会把 `probe.runtimeconfig.json` 里的 `7.0.0` 抬上去，所以这条路要用
`dotnet <dll>` 而不是 `dotnet run`。末尾看到 `RESULT: PASS` 才算过。

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
  run-verify.mjs             一条命令：起服务 → 跑验收 → 退出即回收端口
  verify.mjs                 CDP 真浏览器断言（109 项）
  replay-csharp.mjs          把 C# 真实产出的 patch 灌进运行时
gh/
  install.ps1                安装 + 校验
  build.md                   构建细节、每个坑、每个已知边界
  Colugo/                      插件源码（含预编译产物 bin/）
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
$env:PAGE='http://127.0.0.1:5273/?ws=ws://127.0.0.1:8877/colugo'
node verify.mjs
```

末尾应为 `109/109 passed`。截图落在 `%TEMP%\colugo-*.png`。

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
- **未做**：图层级 alpha mask（指令级 `Clip` 已有）、Shader IR 与 TSL 节点编译、后处理、
  GPU Compute。别按任务书目录找，看 `docs/stage-plan.md` 与 `docs/tsl-plan-calibration.md`。
- **文字已转曲线**：`Text` 电池输出的是 Rhino `Curve`，运行时没有字体概念。
  改字号 = 重新求解，不是每帧变量。缺字体时报警回退 Arial，字形会完全不同。
- **图片走 base64 进 patch**：单张 4MB / 整帧 12MB 是硬顶。图片有指纹缓存，不随每帧重发；
  同路径 + 同 `Max Size` 的图只读一次盘。
- **`Clip` 的模具是页面坐标**，在指令 `transform` 之前落剪。反过来写会让裁剪框跟着图转，
  表现是"裁歪了"。一个指令只能有一个 clip，后接的覆盖前一个。
- **3D 的点大小与线宽调不了**——这不是 bug，是 WebGPU 的能力边界。WGSL 的 `point-list`
  和 `line-list` 原语固定 1 像素，`mat.size` / `sizeNode` / `linewidth` 全部无效。
  three 自己的注释（`three.webgpu.js:29584`）说得很清楚：只有用 **Sprite** 渲染点，
  尺寸才会被尊重。要真的可调，得把点/线几何化成 quad（每点/每段一个 billboard 四边形），
  那是换渲染路径，不是调参数。`Draw 3D` 的 `Size` 参数目前只对 `Mode=2` 的线宽**无效**、
  对 `Mode=3` 的点大小**无效**，留着是为了将来切到几何化路径时不用改端口。
- **`Draw Path` 有 10 个输入**，超过"≤6"的约定。用户明确要求保持原样，不要擅自拆。

---

## 6. 踩过的坑（会再踩一次）

| 现象 | 原因 | 处理 |
|---|---|---|
| 改了代码、重启 Rhino 没变化 | Rhino 锁着 `.gha`，`cp` 静默失败 | 一律走 `install.ps1`，它会查进程并校验 md5 |
| `dotnet` 报 No SDK | PATH 里的是**只有运行时**的那份 | `dotnet --list-sdks` 自检；本机 SDK 在 `C:\Users\<用户>\.dotnet` |
| GH 里整个 Colugo 标签页都不出现 | 某个类型的字段引用了找不到的程序集（如 WebView2），`GetTypes()` 整体抛异常 | 已用 `[ModuleInitializer]` 挂 `AssemblyResolve`；新增第三方引用必须配这个钩子 |
| 参数类型显示棋盘格 | 参数的图标不能覆写 `Icon`（恒 null），走 `Internal_Icon_24x24` | 已在 `Icons.cs` 里按参数单独画 |
| 切换场景后测量值像是"时间倒流" | `selectScene` 走 WS，快照还没到就开始采样 | 等可判定标志（如 `rev === 1`）再采样 |
| 预览窗连不上 | 端口自动避让后，页面里 WS 地址还是 8777 | 统一用 `BridgeServer.PreviewUrl`（带 `?ws=`） |
| 探针报"重复的 GUID" | 新写的电池随手用了 `c1–c4`，与 `RandomComponents` 撞车 | 新文件写 GUID 前先 `grep -o "0000000000.." ` 看已占后缀；撞了会让旧 `.gh` 认错电池 |
| `dotnet run` 报 "must install .NET 7" | 编译只需要 SDK，**运行**需要 net7.0 运行时 | `DOTNET_ROLL_FORWARD=LatestMajor` + `dotnet <dll>`，详见第 2 节 |
| 像素断言偶发失败、颜色像被"稀释" | 采样点压在测试图的格缝/边缘上，被插值混色 | 换算到素材的稳定区（格心、色块中心），别沿用缩小前的坐标 |

---

## 7. 下一步建议

0.33.0 把 P1-B 关掉了。路线图上 **P0-A / P0-B / P1-A / P1-B 全部交付，只剩 P1-C（Shader）**。

1. **端到端作品仍然没有** —— 这条从 0.11 挂到现在。所有验证都是"运行时 + 协议 + C# 单点"，
   50 只电池里没有任何一个真实 `.gh` 把它们串起来跑过。这是当前最大的验证盲区，也是唯一能
   暴露"电池之间对不上"的方式（端口类型对、协议字段对，但连起来语义不对）。
2. **0.34 = P1-C 的 S1**：Shader IR Schema + 9 只新电池 + 首个 golden image。
   为什么是 9 只而不是任务书说的 14 只、三个待拍板的决策，都写在 `docs/tsl-plan-calibration.md`。
3. **GitHub 仓库还叫 GHCE**。本地 manifest 与文档已指向 `mcododesign-hub/Colugo`，
   仓库没改名之前那个地址是 404。改完 GitHub 会自动重定向旧地址，本地 remote 也要跟上：

   ```powershell
   git remote set-url origin git@github.com:mcododesign-hub/Colugo.git
   ```
