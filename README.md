# Colugo · Grasshopper Creative Coding Engine

把 Grasshopper 当**可视化编程前端**，浏览器当**执行环境**：GH 只负责定义作品，浏览器负责绘画、动画、状态。

任务书见 `Grasshopper Creative Coding Engine.md`。本仓库是它的阶段性产出，当前 **v0.39.0（63 只电池 / 201 项断言）**，
阶段 0–5 已闭环 —— 包括 3D 与 Shader 全链（S1 IR → S2 增量传输 → S3 GPU 顶点位移 → S4 二维贴三维 → S5 Bloom）。

**电池怎么接，看 `docs/batteries.html`**（浏览器直接打开）：14 级案例阶梯，从"一条线"到"完整作品"，
每级标出电池数与失败点；后面是 63 只电池的平铺清单（图标 / 端口 / 说明）。

先读这份再动手：**`docs/motion-model.md`** —— cmds 是什么、几何与变换的关系、非仿射运动（场映射/拓扑变化）该怎么表达。
Shader 的 IR 设计与白名单见 `docs/tsl-plan-calibration.md`。

---

## 现在能跑什么

**在 GH 里**：五条最短路径

```
静态：Curve → Path Input → Render Output
动态：Curve → Entity Blueprint → Entity Look → Render Output
动画：Curve → Path Input → Draw Path → Track → Render Output
平面：Text / Image → Draw Image → Clip（可选）→ Render Output
三维：Mesh Input → Draw 3D → Scene 3D → Render Output
```

三维这一条可以往上叠三样东西，都是**只发一次数据、之后在 GPU 上算**：

```
Apply Material ── Colour   ← Shader（Position / Normal / UV / Time / Canvas）
               └─ Displace ← Shader（positionNode，几何本身在 GPU 上变形）
Scene 3D ─ Transparent ✓  →  2D 图层从 3D 背后透出来
         → Post FX (Bloom) → Render Output
```

`Render Output` 的 `Preview` 打开就会弹一个内嵌 WebView2 窗口（运行时页面编在 `.gha` 里，不需要 node、不需要另开浏览器）。拖动曲线，窗口里的图形实时跟着变。

想要交互：把 `Interaction` 电池拖出来，它的 `Pointer` 是**画布坐标**（左上原点、Y 向下、像素）——不是 Rhino 世界坐标，映射关系由你定。`Runtime Info` 是自检面板，显示帧率/对象数/耗时。

**做开发**（改运行时源码时用，带模拟桥和热更新）：

```bash
# 首次：只一个依赖 ws
cd mock && npm install && cd ..

# 常驻开发（热更新）：5173 运行时页面 · 8777 模拟 GH 桥
node mock/dev.mjs

# 一条命令：起服务 → 跑验收 → 收工（进程退出监听器随之消失，端口不残留）
node mock/run-verify.mjs verify.mjs          # 2D + 表达式 + Mark + 行为 + 图片（109 项）
node mock/run-verify.mjs verify-3d.mjs       # 3D 结构 / 相机 / 帧循环（17 项）
node mock/run-verify.mjs verify-shader.mjs   # IR / 位移 / Bloom / 合成（43 项）
node mock/run-verify.mjs replay-csharp.mjs   # C# 产出的 2D patch 回放（15 项）
node mock/run-verify.mjs replay-3d.mjs       # C# 产出的 3D patch 回放（6 项）
node mock/run-verify.mjs replay-shader.mjs   # C# 产出的 IR 走 JSON→WS→GPU（11 项）

# 纯 Node，不需要浏览器
node mock/verify-xform.mjs                    # 矩阵插值 + 时间轨取值（16 项）
node mock/verify-tween.mjs                    # 变换过渡：起点/插值/归位/最短弧（18 项）
node mock/verify-v012.mjs                     # 逐指令 Xform + 事件只影响目标指令（14 项）
node mock/verify-plugin-serve.mjs             # 插件自供页面 + 真浏览器渲染（7 项）
```

上面六套浏览器/回放验收合起来是 **201 项**，全绿才算改完。

GH 插件：

```bash
cd gh/Colugo
dotnet build -c Release          # 需要真正的 SDK（不是只有运行时），产出 Colugo.gha
cd ..
.\install.ps1                    # 复制 + 校验 md5；Rhino 在跑会停下来说原因
```

> `Path Input` 在 **v0.10.0** 从 6 口变 4 口（`Geo / Tol / Camera / Id`），页面尺寸搬进 `Project to 2D`
> 产的相机。不接相机时行为不变，但**接在旧 W/H/Margin 上的线会掉**。详见 `gh/build.md` §10。

**让一个变换"演"出来**：`Transform` 吃 `Cmds` + **每个指令一个 Xform**（list）。
`cmds` 是没变位置的原始几何，`Xform` 是变化后的样子，中间那些帧由浏览器补
（`runtime/src/tween.js`）—— 第一次出现从原始状态演过去，之后改了就从当前位置演到新位置。
没有 progress 输入，也没有 Field：**GH 怎么变，画面就怎么变**。
插值走 TRS 分解（`runtime/src/xform.js`），旋转走最短弧，180° 不会塌成零缩放。

**要"有节奏地动"就用 `Track`**（v0.16）：把 N 个 Xform 喂进 `Frames`、N 个 0..1 喂进 `Reveal`，
再给 `Duration`（跑一遍几秒）和 `Loop`，`Ease` 接任何一条曲线当缓动。
GH **只发一次**帧序列，浏览器按 `clock.time` 插值到 60fps —— 求解器不用跟着帧率跑。
`Reveal` 是几何截断：描边和填充一起长，不是虚线那种只截描边。

打开 http://127.0.0.1:5173/ 会看到运行时页面：左边 HUD 是连接状态 / rev / 几何数 / patch 耗时 / 单帧绘制耗时，八个演示场景按钮，六个导出按钮。

| 场景 | 演示的是 | 对应里程碑 |
|---|---|---|
| `Static` | 97 条路径的摩尔纹版画，含图层、虚线、透明度 | M0 绘图闭环 |
| `Mutate` | 24 条曲线相位持续变化，**只重传变的几何** | 阶段 1「几何与画笔独立修改」 |
| `Stress` | 500 条路径 × 40 点，6 Hz 全量扫动 | 阶段 1 性能验收 |
| `Entities` | 6 条闭环 × 320 粒子，1920 个实例自己走时钟 | 阶段 2 动态对象 |
| `Living` | 3 条时间轨（纯生长 / 旋转 / 位移）+ 3 组粒子 + 残影累积 | 阶段 3 动画（v0.16） |
| `Morph` | 24 帧"方 → 圆"，逐顶点插值 —— 非仿射、矩阵表达不了 | 阶段 4 形变 |
| `Noise` | 不对称三角形按信号转，不叠过渡缓动 | 阶段 4 |
| `3D` | 网格 + 立方体 + 点云 + 螺旋，四种 `Draw 3D` 模式 | 阶段 5 三维 |

其余场景（`shader3d` / `shaderdisplace` / `bloom` / `canvasex` / `composite` 等）是验收脚本专用的，
用 `Colugo.selectScene(name)` 切，不占页面按钮。

导出有六种：`PNG ×2`（带底色）、`PNG 透明`（只有几何，alpha 保留）、`SVG`（矢量），
以及 v0.17 的三样交付物：
`PNG 序列 (zip)`（按 fps × 时长逐帧离屏渲染，`frame_0000.png…`）、
`独立 HTML`（运行时与作品一起内联，双击即播，不需要服务器）、
`视频`（`canvas.captureStream` + `MediaRecorder`，本机命中 `video/webm;codecs=vp9`）。

播放控制是四个**互相独立**的动作（任务书 3E）：`暂停/播放`、`重置时间`、`重启对象`、`清屏`。空格暂停、`0` 重置时间。

`Living` 场景里鼠标移过去只会记录指针位置（`Colugo.pointer`）——**没有**"吸附"这类行为预设：
粒子只有一种运动（沿曲线），要不一样的感觉就改 `Speed` / `Lifetime` / `Phase`，或者多挂一条蓝图。

**没有 Rhino 也能验。** `mock/mock-gh.mjs` 是模拟桥，它按 `PatchBuilder` 的语义做事（算全量状态 → 与上次发出的状态 diff → 只发变化的 op），所以协议问题在这里就能暴露。

## 目录

```
docs/
  protocol-v0.1.md     通信协议 + IR 草案（最重要的文档，先读这个）
  batteries.html       电池手册：14 级案例阶梯 + 63 只电池平铺清单（浏览器打开）
  motion-model.md      cmds 是什么、几何与变换的关系、非仿射运动怎么表达
  tsl-plan-calibration.md  Shader IR 的白名单、op 表、被推翻的判断
  stage-plan.md        阶段拆解与验收清单（含哪些项已完成/半成品）
runtime/
  index.html           运行时页面
  src/
    doc.js             文档状态模型 + op 应用（只做状态，不做绘制）
    entity.js          动态对象系统：蓝图/实例/生命周期/固定步长（阶段 2 内核）
    geom.js            几何解码（f32/base64）、Path2D 构建、弧长截断、指纹
    xform.js           矩阵插值（TRS 分解）+ 时间轨取值（Track）
    tween.js           变换过渡：第一次出现从原始状态演过去，之后从当前位置演
    renderer2d.js      Canvas 2D 后端（Path2D 缓存、图层合成、几何截断、PNG 导出）
    svg.js             矢量后端（不可矢量化项显式降级并报出）
    exporter.js        PNG 序列 / 独立 HTML（data: 模块内联）/ 视频 / 透明 PNG
    zip.js             极小 STORE zip 写入（零依赖，打包 PNG 序列用）
    clock.js           运行时时钟（Play/Pause/Reset/TimeScale）
    bridge-client.js   连接 / 重连 / 回执
    expr.js            运行时表达式求值（A 轨：JS 每帧算）
    events.js          事件模型（v0.31）
    signal.js          信号 / 平滑
    pointer.js         指针状态
    scene3d.js         3D 层：材质缓存、材质构造、GPU 位移、Bloom、2D 合成
    shader.js          Shader IR 编译器：白名单分发 → TSL 节点树（不 eval）
    main.js            装配 + HUD + 两个渲染循环的驱动顺序
mock/
  mock-gh.mjs              模拟 GH 侧（按 PatchBuilder 的语义做 diff）
  serve.mjs                零依赖静态服务器
  run-verify.mjs           一条命令：起服务 → 跑指定验收 → 收工
  verify.mjs               2D + 表达式 + Mark + 行为 + 图片（109 项）
  verify-3d.mjs            3D 结构 / 相机 / 帧循环（17 项）
  verify-shader.mjs        IR / 位移 / Bloom / 合成（43 项）
  replay-csharp.mjs        C# 产出的 2D patch 回放（15 项）
  replay-3d.mjs            C# 产出的 3D patch 回放（6 项）
  replay-shader.mjs        C# 产出的 IR 走 JSON→WS→GPU（11 项）
  verify-xform.mjs / verify-tween.mjs / verify-v012.mjs / verify-plugin-serve.mjs
gh/
  build.md                 构建 / 安装 / 编译期踩的坑 / 已知边界（按版本追加）
  Colugo/                    Grasshopper 插件源码（net7.0-windows）
    Bridge/                桥：一个端口同时做 HTTP 静态页 + WS + patch 差分
    Ui/PreviewWindow.cs    WPF + WebView2 预览窗
    Icons.cs               每只电池一个图标（GDI+ 现画，24×24）
    Components/            63 只电池，21 个文件
    Types/                 15 个数据类型 + 相机/变换数学
  probe/                   探针：反射实例化全部电池 + 图标覆盖率 + patch 导出
```

**探针是这个仓库的第二套验收**：它用反射把每只电池实例化一遍，检查端口数、GUID 唯一性、
图标覆盖率与反差，再把 C# 真实产出的 patch 写到临时文件供 `replay-*.mjs` 回放。
`RESULT: PASS` 且 78 行（63 电池 + 15 参数类型）才算齐。

## 四条设计决定（都不是随手写的）

**1. 几何与绘图指令分离。** 协议里 `geom.*` 与 `cmd.*` 是两组独立的 op。同一个几何可以挂多条指令（不同画笔、不同图层），改画笔时几何一个字节不重传。这是阶段 2 的对象系统能长起来的前提。

**2. 身份来自几何指纹，不来自数据树。** `Path Input` 用 NurbsCurve 控制点算 FNV-1a 得到 `g_xxxxxxxx`。所以只改采样容差、挪动列表位置、增删其他曲线，都不会让运行时里的对象"换人"。任务书 2.3 点名要防的就是这个。

**3. 整帧无变化就不发包。** Slider 微调时，只要曲线位移没超过 f32 可表示的差异，patch 就是空的。带宽的基本保障在这里，不在后面补缓存。

**4. 端口只传数据，不传选项。** `Count` / `Speed` / `Duration` / `Loop` / `Matrix` / `Shape` / `Ease` 是数据；
`Mode`、`Field`、`Wave`、`Seed`、`Vary` 是"我替你选一种效果"，一律不做。
按这条规则删掉的东西：`Timeline` / `Keyframe` / `Oscillator`（靠 `Field` 选驱动哪个属性）、
`Entity Behavior`（6 个模式只剩 1 个有用）、蓝图里的随机（`Seed` / `Vary`）。
时间是唯一破例过的地方，v0.16 也用 `Track` 把它还原成"数据"：帧序列 + 时长 + 循环 + 一条缓动曲线。

## 边界

**已验的**：C# 编译 0 错误 0 警告；**63 只电池**在真 .NET 7 + 真 Grasshopper 程序集下经反射实例化通过，
GUID 无重复、图标覆盖率与反差全部达标；C# 产出的 patch 回放进运行时 `15/11/6` 项全绿；
浏览器验收 **201 项**（`109 + 17 + 43 + 15 + 6 + 11`）。三维位移链与 Bloom 导出已在真 Rhino 里手验过。

**还没做的，以及为什么**：

- **3D 的点大小与线宽固定 1px。** 不是参数没接上：WGSL 的 `point-list` / `line-list` 原语没有 width，
  three 自己的注释也写明"只有用 Sprite 渲染点，size 才 honored"。要真修等于把点/线几何化成 quad
  （Line2 那套做法），会改动 points/lines 的渲染路径并要求重写 `verify-3d` 的 17 项，所以单独立项。
  过渡方案：把曲线 `Pipe` 成圆柱、把点 `Sphere` 成小球，它们就是 mesh 了，粗细完全可控。
- **`mask` 与 `blend` 组**：协议留了位置，实现没做。
- **Image Sample / Halftone**（阶段 4 尾巴）判定暂缓。
- **3D 的断言仍以结构为主**（对象数、三角数、相机、材质键），没有像素级验证。
  Bloom 与位移之所以能确认，靠的是截图人工看 —— 补像素级要先读通 WebGPU 的 `copyTextureToBuffer`。
- 构建细节（为什么不用 NuGet 包、PATH 里的 dotnet 没有 SDK、编译时踩到的错）全在 `gh/build.md`。

**加字段时的一个坑，犯过两次**：`mock/mock-gh.mjs` 里 `scene3d.set` 的 payload 是**白名单式拷贝**，
新加的字段忘了进列表，表现是"运行时收不到参数"但两边各自的断言全绿。加字段第一件事就是改那个列表。
