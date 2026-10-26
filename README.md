# Colugo · Grasshopper Creative Coding Engine

把 Grasshopper 当**可视化编程前端**，浏览器当**执行环境**：GH 只负责定义作品，浏览器负责绘画、动画、状态。

任务书见 `Grasshopper Creative Coding Engine.md`。本仓库是它的阶段性产出，当前 **v0.33.0（50 只电池 / 147 项断言）**，
阶段 0–4 已闭环；阶段 5 的 Shader 有任务书与校准，待开工（`docs/tsl-plan-calibration.md`）。

先读这份再动手：**`docs/motion-model.md`** —— cmds 是什么、几何与变换的关系、非仿射运动（场映射/拓扑变化）该怎么表达。

---

## 现在能跑什么

**在 GH 里**：三条最短路径

```
静态：Curve → Path Input → Render Output
动态：Curve → Entity Blueprint → Entity Look → Render Output
动画：Curve → Path Input → Draw Path → Track → Render Output
平面：Text / Image → Draw Image → Clip（可选）→ Render Output
```

`Render Output` 的 `Preview` 打开就会弹一个内嵌 WebView2 窗口（运行时页面编在 `.gha` 里，不需要 node、不需要另开浏览器）。拖动曲线，窗口里的图形实时跟着变。

想要交互：把 `Interaction` 电池拖出来，它的 `Pointer` 是**画布坐标**（左上原点、Y 向下、像素）——不是 Rhino 世界坐标，映射关系由你定。`Runtime Info` 是自检面板，显示帧率/对象数/耗时。

**做开发**（改运行时源码时用，带模拟桥和热更新）：

```bash
cd mock
npm install        # 只一个依赖：ws
npm run dev        # 5173 运行时页面 · 8777 模拟 GH 桥

# 另开一个终端（必须显式把页面指到 mock 桥，否则会连到你开着的真 Rhino）
RUNTIME=http://127.0.0.1:5173 PAGE="http://127.0.0.1:5173/index.html?ws=ws://127.0.0.1:8788/colugo" \
  node mock/verify.mjs              # 真 Chrome 跑 49 项断言 + 出截图
node mock/replay-csharp.mjs       # 把 C# 产出的 patch 灌进运行时（15 项）
node mock/verify-plugin-serve.mjs # 插件自供页面 + 真浏览器渲染（7 项）
node mock/verify-xform.mjs        # 矩阵插值 + 时间轨取值（16 项，纯 Node 不需浏览器）
node mock/verify-tween.mjs        # 变换过渡：起点/插值/归位/restart/最短弧（18 项，纯 Node）
node mock/verify-v012.mjs         # 逐指令 Xform 各自演 + 事件只影响目标指令（14 项）
```

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

打开 http://127.0.0.1:5173/ 会看到运行时页面：左边 HUD 是连接状态 / rev / 几何数 / patch 耗时 / 单帧绘制耗时，三个演示场景按钮，两个导出按钮。

| 场景 | 演示的是 | 对应里程碑 |
|---|---|---|
| `Static` | 97 条路径的摩尔纹版画，含图层、虚线、透明度 | M0 绘图闭环 |
| `Mutate` | 24 条曲线相位持续变化，**只重传变的几何** | 阶段 1「几何与画笔独立修改」 |
| `Stress 500` | 500 条路径 × 40 点，6 Hz 全量扫动 | 阶段 1 性能验收 |
| `Entities` | 6 条闭环 × 320 粒子，1920 个实例自己走时钟 | 阶段 2 动态对象 |
| `Living` | 3 条时间轨（纯生长 / 旋转 / 位移）+ 3 组粒子 + 残影累积 | 阶段 3 动画（v0.16） |

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
  stage-plan.md        阶段拆解与验收清单（含哪些项已完成/半成品）
runtime/
  index.html           运行时页面
  src/
    doc.js             文档状态模型 + op 应用（只做状态，不做绘制）
    entity.js          动态对象系统：蓝图/实例/生命周期/固定步长（阶段 2 内核）
    geom.js            几何解码（f32/base64）、Path2D 构建、弧长截断、指纹
    xform.js           矩阵插值（TRS 分解）+ 时间轨取值（Track）
    renderer2d.js      Canvas 2D 后端（Path2D 缓存、图层合成、几何截断、PNG 导出）
    svg.js             矢量后端（不可矢量化项显式降级并报出）
    exporter.js        v0.17：PNG 序列 / 独立 HTML（data: 模块内联）/ 视频
    zip.js             极小 STORE zip 写入（零依赖，打包 PNG 序列用）
    clock.js           运行时时钟（Play/Pause/Reset/TimeScale）
    bridge-client.js   连接 / 重连 / 回执
    main.js            装配 + HUD
mock/
  mock-gh.mjs              模拟 GH 侧
  serve.mjs                零依赖静态服务器
  verify.mjs               CDP 驱动的真浏览器验收（49 项）
  replay-csharp.mjs        把 C# PatchBuilder 的真实输出回放进运行时（15 项）
  verify-plugin-serve.mjs  插件自供页面 + 真浏览器渲染（7 项）
gh/
  build.md                 构建 / 安装 / 编译期踩的坑 / 已知边界
  Colugo/                    Grasshopper 插件源码（net7.0-windows）
    Bridge/                桥：一个端口同时做 HTTP 静态页 + WS
    Ui/PreviewWindow.cs    WPF + WebView2 预览窗
    Icons.cs               四个电池图标（GDI+ 现画）
  probe/                   探针：反射实例化电池 + --serve 模式验证自供页面
```

## 三条设计决定（都不是随手写的）

**1. 几何与绘图指令分离。** 协议里 `geom.*` 与 `cmd.*` 是两组独立的 op。同一个几何可以挂多条指令（不同画笔、不同图层），改画笔时几何一个字节不重传。这是阶段 2 的对象系统能长起来的前提。

**2. 身份来自几何指纹，不来自数据树。** `Path Input` 用 NurbsCurve 控制点算 FNV-1a 得到 `g_xxxxxxxx`。所以只改采样容差、挪动列表位置、增删其他曲线，都不会让运行时里的对象"换人"。任务书 2.3 点名要防的就是这个。

**3. 整帧无变化就不发包。** Slider 微调时，只要曲线位移没超过 f32 可表示的差异，patch 就是空的。带宽的基本保障在这里，不在后面补缓存。

**4. 端口只传数据，不传选项。** `Count` / `Speed` / `Duration` / `Loop` / `Matrix` / `Shape` / `Ease` 是数据；
`Mode`、`Field`、`Wave`、`Seed`、`Vary` 是"我替你选一种效果"，一律不做。
按这条规则删掉的东西：`Timeline` / `Keyframe` / `Oscillator`（靠 `Field` 选驱动哪个属性）、
`Entity Behavior`（6 个模式只剩 1 个有用）、蓝图里的随机（`Seed` / `Vary`）。
时间是唯一破例过的地方，v0.16 也用 `Track` 把它还原成"数据"：帧序列 + 时长 + 循环 + 一条缓动曲线。

## 边界

- **C# 已编译（0 错误 / 0 警告），16 只电池在真 .NET 7 + 真 Grasshopper 程序集下实例化通过，产出的 patch 回放进运行时 15/15，插件自供页面 7/7，浏览器验收 49/49。** 还差两件只有你开 Rhino 才能验的事：GH 里的端到端联调、WebView2 预览窗口能否正常弹出。
- 构建细节（为什么不用 NuGet 包、PATH 里的 dotnet 没有 SDK、编译时踩到的错）全在 `gh/build.md`。
- `mask`、`blend` 组、3D：协议留了位置，实现没做。阶段 4（Image Sample / Halftone）判定暂缓。
