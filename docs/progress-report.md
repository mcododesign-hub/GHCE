# GHCE 开发进度报告

**Grasshopper Creative Coding Engine** — 在 Rhino / Grasshopper 里创作，在浏览器里实时执行

| 项 | 值 |
|---|---|
| 版本 | **GHCE 0.32.1** |
| 报告日期 | 2026-10-23 |
| 代码仓库 | `github.com/mcododesign-hub/GHCE` |
| 技术形态 | Rhino 插件（`.gha`）+ 内嵌浏览器预览窗 + 独立 Web 运行时 |
| 规模 | C# 8,117 行 / 运行时 JS 3,947 行 / 验证脚本 3,171 行 |
| 交付物 | 46 只电池、13 个数据类型、59 个 GUID、协议 V0.1 |

---

## 0. 摘要

GHCE 把 Grasshopper 当成一个**可视化代码编辑器**：几何在 GH 里算，样式和时间在 GH 里编排，最终编译成一份增量指令流，推给浏览器实时渲染。

| 能力域 | 状态 | 说明 |
|---|---|---|
| 阶段 0 通信闭环 | 已完成 | WebSocket + JSON 协议，增量下发，无 op 不发帧 |
| 阶段 1 二维绘图 | 已完成 | 路径输入、投影、图层、描边/填充、逐笔生长 |
| 阶段 2 动态对象 | 已完成 | 蓝图 + 外观两段式，确定性分布 + 沿曲线运动，拖尾/淡出 |
| 阶段 3 动画与交互 | 已完成 | 全局时间轴 + 轨道 + 任意参数帧序列 + 事件绑定 |
| 阶段 3+ 运行时表达式 | 已完成（最小闭环） | 事件写变量 → 表达式每帧求值 → 属性；不重解 GH |
| 阶段 4 高级绘图 | 未开始 | 画笔、字体、图像、平面设计系统 |
| 阶段 5 三维 | 部分完成 | Mesh / 点云 / 线框 / 相机 / 光照 / 材质预设已通；Shader 类型、TSL 节点、后处理未开始 |
| 阶段 6 发布封装 | 未开始 | 已具备运行时导出能力，尚未做作品包与播放器 |
| 阶段 7 AI / MCP | 未开始 | — |

**验证状态**：137 项运行时断言 + 46 只电池探针全部通过（详见第 5 节）。

**研发节奏**：2026-10-22 起自阶段 0 搭建，两日内完成阶段 0–3 与三维主干，迭代至 0.28.2；每一轮都以"验证脚本 + 探针断言"收尾，不以"打开看一眼没报错"作为完成标准。

---

## 1. 系统架构

四层，与任务书一致；每一层都是可独立验证的：

```
┌─ Rhino / Grasshopper ─────────────────────────────────────────┐
│  GHCE 电池组：几何 → 样式 → 时间 → 三维                        │
│  Path Input / Draw Path / Track / Draw 3D …                   │
└───────────────────────┬───────────────────────────────────────┘
                        │  GH 求解（参数变化才触发）
┌───────────────────────▼───────────────────────────────────────┐
│  PatchBuilder（编译层）                                        │
│  与上一帧比对：几何指纹 / 样式 / 图层 / 画布 / 增删            │
│  只输出变化的 op；整帧无变化则不发                              │
└───────────────────────┬───────────────────────────────────────┘
                        │  WebSocket（本机，8777 起自动避让到 8788）
┌───────────────────────▼───────────────────────────────────────┐
│  Browser Runtime（2D Canvas + three.js 双层）                  │
│  doc 增量应用 → 绘制顺序 (layer, cmd, id) → 时钟驱动动画        │
└───────────────────────┬───────────────────────────────────────┘
                        │  PNG / SVG / 序列 / 视频 / 独立 HTML
┌───────────────────────▼───────────────────────────────────────┐
│  发布层（导出已完成，封装未做）                                 │
└───────────────────────────────────────────────────────────────┘
```

### 关键工程决策

1. **协议只传页面坐标**。世界坐标 → 页面坐标的换算在 GH 侧（`Project to 2D`）完成，运行时不做任何假设。
2. **变换不烘焙进几何**。同一个几何可以被多条指令以不同 `transform` 复用，旋转/缩放/平移只改矩阵。
3. **增量更新靠指纹**。几何指纹 = 顶点字节的 FNV-1a，量化容差 = 插件容差；小于容差的移动不重传。
4. **预览窗内嵌**。WPF + WebView2，Rhino 8 自带 WebView2 装配，不额外装浏览器、不起 node 服务、不需要手动开页面。

### 技术栈（实测版本）

| 项 | 版本 / 说明 |
|---|---|
| Rhino | 8.15.25013.13001 |
| 插件目标框架 | `net7.0-windows`，直接引用 Rhino 安装目录下的 `RhinoCommon` / `Grasshopper`（NuGet 8.15 只有 net48 资产） |
| 预览窗 | WPF + WebView2（Runtime 155.x，随 Rhino 分发） |
| 渲染 | Canvas 2D（手工批处理）+ three.js **r186**（`WebGPURenderer`，环境不支持 WebGPU 时回退 WebGL2） |
| 运行时 | 原生 ES Module，无打包器、无框架，3,758 行 |
| 通信 | WebSocket + JSON，几何数据 base64(f32) 内嵌 |

---

## 2. 已实现功能

### 2.1 画布与输出

| 电池 | 输入 | 输出 |
|---|---|---|
| `Canvas` | Width, Height, Background, DPI Scale | Canvas |
| `Render Output` | Canvas, Commands, Entities, Enabled, Preview, Scene | Status, Url |

- **弹窗即预览**：`Render Output` 首次求解时启动内嵌桥，自动弹出预览窗，行为对齐 Processing 的播放键；不占端口失败（8777–8788 自动避让），不依赖外部进程。
- **同一端口双用**：HTTP 提供页面与资源，`/ghce` 提供 WebSocket，`Url` 输出可直接在外部浏览器打开同一画面。
- **画布参数**：逻辑尺寸、背景色（可透明）、DPR（上限 2）。运行时按 DPR 分配物理像素，四档视口实测无裁切、无横向溢出。
- **清屏模式**：协议层已支持 `clear`（每帧清空）/ `fade`（叠半透明底色做残影）/ `none`；当前 `Canvas` 电池尚未暴露该项，运行时按默认值执行。
- **画面优先**：运行时默认只有画面 —— 没有常驻面板、没有播放按钮，画布铺满窗口、不留边距。
- **播放控制由事件定义**（不依赖 GH 求解）：暂停 / 重置时间 / 重启对象 / 清屏都由 `Event Trigger` 决定，运行时不自作主张放按钮。
- **开发面板**：`H` 唤出，显示连接状态、rev、几何与指令数、patch 字节、FPS；关闭时不做任何 DOM 写入。诊断信息只服务于调试，不进作品画面。

### 2.2 二维几何与绘制

| 电池 | 输入 | 输出 | 作用 |
|---|---|---|---|
| `Path Input` | Geometry, Tolerance, Camera, Id | Paths, Info | Rhino 曲线 → 页面路径，ID 稳定 |
| `Project to 2D` | Geometry, Page W/H, Margin, Scale, Bounds | Camera, Info | 世界坐标 → 页面坐标（含自动取景） |
| `Draw Path` | Paths, Layer, Order, Stroke, Width, Alpha, Dash, Fill, Fill Alpha, Opacity | Commands | 路径 → 绘制指令 |
| `Draw Progress` | Commands, Progress, Spread | Commands | 逐笔生长（0–1 + 展开量） |
| `Layer` | Commands, Name, Order, Opacity, Blend, Visible | Commands | 显式图层归属 |
| `Stroke Style` | Commands, Colour, Width, Alpha, Dash, Cap | Commands | 批量改描边 |
| `Fill Style` | Commands, Colour, Alpha, Clear | Commands | 批量改填充 |
| `Brush` | Curve, Shape, Size, Aspect, Angle | Brush, Info | 印章定义（局部坐标、原点居中） |
| `Mark Along Path` | Paths, Brush, Spacing, Rotate, Angle, Layer | Commands, Info | 沿路径等距落笔，一条标记一条指令 |
| `Style Map` | Commands, Property, In, Out, Bias | Commands, Info | 沿程把尺寸/旋转/透明度/线宽从 A 推到 B |

- **绘制顺序**：`(layer.order, cmd.order, cmd.id)` 三级排序 —— 结果可复现，不依赖 Map 迭代顺序。
- **几何类型**：折线 / 多边形 / 点集 / 保留贝塞尔语义的 path（`M L C Q Z`）。
- **样式叠加**：`Stroke Style` / `Fill Style` 是"覆盖一批指令"的管道式电池，不改几何。

**Mark（v0.31）** 把"几何 → 一笔"升级成"几何 → 多次落笔"：

```
Curve → Path Input → Mark Along Path → Style Map → Stroke Style → Render Output
                          ↑
                   Brush（圆点 / 短线 / 方块 / 三角 / 弧，或任意曲线）
```

- **一个印章几何 + N 条矩阵**：404 条标记只引用 3 份几何，整帧 200 KB；几何本身不到 1 KB。
- `Rotate`：1 跟随切向 / 2 垂直路径 / 3 固定角 / 4 确定性随机。`Spacing` 取"总长 ÷ 整数份"，末端不留零头。
- `Style Map` 读每条标记的沿程位置 `u`，用 `In/Out` 两个区间 + `Bias` 伽马映射 —— 不需要再喂一列数值。
- 闭合印章默认填充：圆点画成空心圈是这类工具最常见的"这不是我要的"。

### 2.3 动画系统

GHCE 的动画模型是**一条全局时间轴 + 若干条轨道 + 可插值的帧序列**，与剪辑软件同构：

| 电池 | 输入 | 输出 | 作用 |
|---|---|---|---|
| `Timeline` | Tracks, Duration, Loop | Commands | **全局**时长与循环，不含任何实例 |
| `Track` | Commands, Frames, Reveal, Range, Ease, Hidden | Commands | 单条轨道：入出点、缓动、静音 |
| `Transform` | Paths, Xform | Paths | 几何级仿射变换；1 个 Xform = 静态，N 个 = 帧序列 |
| `Morph` | Geometries, Step | Paths | N 份几何 → 可插值帧序列（列表顺序即时间顺序） |

**"任意 slider 都能动画"的落地方式**：GH 侧把参数扫一遍，得到 N 份同拓扑几何或 N 个矩阵，打包成帧序列；运行时只做逐顶点 / 逐元素线性插值，**不需要认识 fillet、offset、布尔**任何具体操作。因此它不是"支持倒角"，而是支持任何能输出同拓扑结果的数据操作。

- **共用同一个 `u`**：形状（morph）、位置（track 矩阵）、生长（reveal）三者用同一个进度，不会各走各的。
- **缓动 = Graph Mapper**：`Ease` 吃一条 0–1 → 0–1 的曲线，横轴进度、纵轴输出，任意曲线可用。
- **单条可静音**：`Hidden` 让一条轨保持静态值，排错时不必拆线。
- **参数序号化**：所有模式类参数用 1 / 2 / 3 序号，不接受字符串。

### 2.4 动态对象（粒子/实例）

| 电池 | 输入 | 输出 |
|---|---|---|
| `Entity Blueprint` | Path, Count, Speed, Lifetime, Phase | Blueprint |
| `Entity Look` | Blueprint, Shape, Colour, Width, Size, Trail, Layer, Fade | Entities |
| `Read State` | Slot, Name | Expression |
| `Write State` | Entities, Slot, Expression, When, Op | Entities |
| `Spawn` | Entities, When, Count, Spread, Max Gen, Max Pop | Entities |
| `Destroy` | Entities, When, Keep | Entities |

- **两段式**：蓝图管"怎么动"，外观管"长什么样"，同一套运动可以换外观复用。
- **确定性分布**：第 i 个实例的 `u = (i / count + phase) % 1`，没有随机数、没有种子 —— 要变化就在 GH 里算，而不是让引擎替你掷骰子。
- **固定步长**：模拟按 1/60 累加，不受显示器刷新率与 GH 求解节奏影响。
- **身份稳定**：身份 = `(blueprintId, index)`，删掉中间一个不会让别人换人；曲线被 GH 改写时实例继续跑，只是路径换了。
- **拖尾**（采样点数）与**按寿命淡出**已实现；实例上限 20,000。

**Behavior Graph（v0.32）**：没有行为预设，只有"读状态 → 算 → 写状态"。

```
Blueprint → Look → Write State ×N → Spawn → Destroy → Render Output
                        ↑            ↑
                   Read State ─ Runtime Math ─┘
```

- 可写槽位：`x y vx vy u size alpha aux`；只读：`age index gen`。
- **写谁谁归你**：写了 `x`/`y` 就不再从曲线取样，写了 `u` 就不再自增 —— 否则自定义运动和沿曲线跑会互相覆盖。
- `Spawn` 在条件**上升沿**生成，子代继承父代全部状态（`vx/vy/size/alpha/aux`），只有 `age` 归零、`gen+1`、`index` 递增；"变得不一样"由表达式读 `index`/`gen` 自己算。
- `Destroy` 的 `Keep` 把最后 N 个状态留成残影（留痕），不生成新几何、不进 patch。
- 全部在固定步长里执行：同样的步数在任何机器上得到同样的结果（已进断言）。

### 2.5 实时信号

| 电池 | 输入 | 输出 | 作用 |
|---|---|---|---|
| `Random` | — | Value, Signal | 每秒跳变两次的随机数 |
| `Noise 1D / 2D / 3D` | X / X,Y / X,Y,Z | Value, Signal | Perlin 噪声，时间自动叠加 |
| `Signal to Float` | Signal | Value | 把信号接回 **GH 原生电池** |

- 信号是**实时变化的数据**，不是绘制指令：可以把 `Value` 直接喂给原生 GH 电池（Move、Rotate、Scale…），让整棵 GH 树跟着时间动。
- 运行时有独立的 100ms 计时器推动信号，与 Tween 的判定间隔（120ms）配合，不会被误判为离散跳变而起过渡。

### 2.6 交互

| 电池 | 输入 | 输出 |
|---|---|---|
| `Interaction` | — | Pointer, Inside, Down, Clicks, Keys |
| `Event Trigger` | Commands, Event, Action, Detail, Enable | Commands |

- **坐标是弹出画布的页面坐标**（左上原点、Y 向下），不是 Rhino 世界坐标；怎么映射由使用者决定。
- **事件**：`click` / `dblclick` / `key`。
- **动作**：`toggle` / `show` / `hide` / `restart`（作用于指令），`clear` / `clear-region` / `reset-time` / `toggle-play`（作用于画布与时钟）。

### 2.7 三维

链路与二维同构：**先造对象 → 再动 → 再绘制 → 再成场景**。

| 电池 | 输入 | 输出 | 作用 |
|---|---|---|---|
| `Mesh Input` | Geometry, Density | Meshes, Info | Rhino Mesh / SubD / Brep → 三角网格 |
| `Point 3D` | Points | Objects | Rhino 点 → 点云 |
| `Curve 3D` | Curves, Samples | Objects | Rhino 曲线 → 3D 折线（每条曲线一个对象） |
| `Transform 3D` | Meshes, Xform | Meshes | 1 个矩阵 = 静态，N 个 = 帧序列 |
| `Draw 3D` | Meshes, Mode, Colour, Size, Glow, Opacity | Commands 3D | 材质预设 + uniform |
| `Draw Points` | Points, Colour, Size, Opacity | Commands 3D | 点云绘制 |
| `Camera 3D` | Position, Target, Fov, Projection, Zoom | Camera 3D | 独立相机；**留空则按包围盒自动取景居中** |
| `Scene 3D` | Commands, Camera, Background, Lights | Scene | 收集成场景 |

- **绘制模式**（序号）：`1` 实体 / `2` 线框 / `3` 点 / `4` 法线 / `5` 无光照。
- **材质**：`Glow` 控制自发光（0–2，实体与无光照模式有效），`Opacity` 独立于颜色。
- **光照**：`Lights` 开关（主光 + 补光 + 环境光）。
- **与二维共存**：两个叠层画布，3D 无内容时透明且不吃鼠标事件；切回二维时 3D 层自动清空，不残留。

### 2.8 运行时遥测

| 电池 | 输出 |
|---|---|
| `Runtime Info` | FPS, Entities, Draw ms, Step ms, Rev, Clients |

浏览器每 100ms 回传一次 `stat`（指针/按键/帧率/对象数/耗时/信号值，只上行，不参与 GH 求解，避免互相等待）；`Rev` 用来判断"GH 到底有没有真的发新帧"。

### 2.9 导出

预览窗内直接可用，不依赖 GH：

| 导出 | 说明 |
|---|---|
| PNG ×2 | 2 倍分辨率位图 |
| PNG 透明 | 无背景，只有几何 |
| SVG | 矢量，保留矩阵与描边样式；个别特性不支持时降级并在面板提示 |
| PNG 序列 (zip) | 按 fps / 时长逐帧渲染打包 |
| 独立 HTML | 单文件，带运行时与数据，可离线播放 |
| 视频 | 按浏览器支持的编码输出 WebM / MP4 |

### 2.10 运行时表达式与事件写变量

让"点一下线变粗"这类交互不需要回到 GH 求解。链路：

```
click ─► Event Rule ─► 变量 v_w ─► Bind(width = 1.5 + 6·v_w) ─► 绘制时求值
```

| 电池 | 输入 | 输出 | 作用 |
|---|---|---|---|
| `Runtime Var` | Name, Initial | Expression | 声明变量（名字即身份，初值在重置时恢复） |
| `Runtime Time` | Scale, Offset | Expression | 浏览器时钟，GH 只拼树不求值 |
| `Runtime DT` | — | Expression | 固定步长 1/60 秒，积分用；步长只在这一个地方定义 |
| `Runtime Math` | A, B, Op | Expression | 1–8 基本运算，9 A>B，10 A<B，11 noise |
| `Runtime Remap` | X, In, Out | Expression | 区间映射（在 GH 侧展开，运行时无新算子） |
| `Runtime Select` | Condition, A, B | Expression | 条件非 0 取 A |
| `Bind` | Commands, Property, Expression | Commands | 1 描边宽度 / 2 不透明度 / 3 色相 / 4 缩放 |
| `Event Rule` | Commands, Event, Variable, Write, Value, Enable | Commands | 1 set / 2 toggle / 3 add；事件源 9 种 |

- **表达式是 AST 不是 JS 字符串**：浏览器只按白名单算子求值，不 `eval`、不 `new Function`。
- **绑定不写回指令本体**：绘制时才求值，所以导出与 SVG 拿到的仍是静态真值，同一份指令还能被绑成两种样子。
- **事件与表达式解耦**：事件只写变量，改视觉规则不必再改引擎；旧 8 个预设动作照旧可用。

---

## 3. 通信协议 V0.1

WebSocket + JSON，`{v, t, seq, ts, ...}` 封帧；核心消息是 `patch`（一批增量 op）。

| 分类 | op |
|---|---|
| 画布 | `canvas.set` / `doc.reset` |
| 二维 | `layer.upsert` `layer.remove` / `geom.upsert` `geom.remove` / `cmd.upsert` `cmd.remove` |
| 动态对象 | `entity.upsert` `entity.remove` |
| 时间 | `timeline.set`；单条轨挂在指令的 `track` 字段上 |
| 事件 | `event.upsert` `event.remove`（`writes` 写变量，与旧 `action` 并存） |
| 运行时 | `var.upsert` `var.remove`；绑定挂在 `cmd.upsert` 的 `bind` 字段 |
| 三维 | `mesh.upsert` `mesh.remove` / `cmd3d.upsert` `cmd3d.remove` / `scene3d.set` `scene3d.clear` |

消息类型：`hello` / `welcome` / `patch` / `ack` / `stat` / `error` / `ping` / `pong`。

- **版本不匹配**整帧丢弃并报错，不做静默降级。
- **`snapshot: true`**：切场景时强制全量，避免增量保护吃掉第一帧。
- **错误码**：`VERSION_MISMATCH` `OP_UNKNOWN` `GEOM_INVALID` `LAYER_MISSING` `CAP_UNSUPPORTED` `RUNTIME_EXCEPTION`。

---

## 4. 电池一览（46 只）

| 分类 | 电池 |
|---|---|
| **Drawing**（10） | Path Input, Project to 2D, Draw Path, Draw Progress, Layer, Stroke Style, Fill Style, Brush, Mark Along Path, Style Map |
| **Animation**（4） | Timeline, Track, Transform, Morph |
| **Entity**（6） | Entity Blueprint, Entity Look, Read State, Write State, Spawn, Destroy |
| **Random**（5） | Random, Noise 1D, Noise 2D, Noise 3D, Signal to Float |
| **3D**（8） | Mesh Input, Point 3D, Curve 3D, Transform 3D, Draw 3D, Draw Points, Camera 3D, Scene 3D |
| **Runtime**（8） | Runtime Var, Runtime Time, Runtime DT, Runtime Math, Runtime Remap, Runtime Select, Bind, Event Rule |
| **Input**（3） | Interaction, Event Trigger, Runtime Info |
| **Output**（2） | Canvas, Render Output |

另有 **13 个数据类型**（Canvas / Camera / Paths / Commands / Entities / Signal / Track / Events / Expression / Meshes / Commands 3D / Camera 3D / Scene），统一归在 `GHCE / Data` 分类下，全部带图标。

**设计约束**：单只电池输入端控制在 6 个以内（`Draw Path` 因暴露完整描边/填充参数为 10 个，已识别为待收敛项）。

---

## 5. 质量保证与验证

不依赖"打开看一眼"，每层都有可复跑的断言。

### 5.1 插件探针（`gh/probe`）

反射实例化全部电池与数据类型，逐项断言：

| 检查组 | 内容 |
|---|---|
| 结构 | 46 只电池可实例化、端口名/类型/访问方式/可选性、图标非空 |
| 身份 | 59 个 GUID 全局唯一（撞 GUID 会让电池互相顶掉且不报错） |
| 图标 | 逐只图标：底色取不透明像素亮度众数，字形覆盖率 ≥ 4%，与底色亮度差 ≥ 80 |
| 数据 | Goo 往返、Param 访问模式、Path→Command 转换 |
| 变换 | 锚点/环绕/位移/回退 4 组世界→页面坐标断言 |
| 相机 | 缩放、居中、翻转、固定值、边距 |
| 编译 | 二维 patch 结构、track/timeline/layer/progress/event 是否进入 patch |
| 三维 | mesh/cmd3d/scene 三类 op、矩阵行列序（Rhino 行向量 vs three 列向量）、模式与类型 |

**实测**：`RESULT: PASS`；图标最弱覆盖率 4.5%（`Runtime Math`），最弱亮度差 122（`Bind`）。Mark 数学层 11 组数值断言、101 条标记 → `geom.upsert=1`、`cmd.upsert=101`、42 KB；状态程序另有 4 组断言（指纹随槽位变化、深拷贝隔离、`program` 进 patch、`state`/`dt` 节点可序列化）。

### 5.2 运行时验收（真 Chrome + CDP）

| 套件 | 项数 | 覆盖 |
|---|---|---|
| `verify.mjs`（二维 + 表达式 + Mark + 行为） | **99** | 连接与握手、增量 patch、像素级非空校验、图层与排序、生长动画、morph 插值、信号时变性、Mark 五种画法、状态程序（人口/代数/销毁/残影/确定性）、四档视口布局 |
| `verify-3d.mjs`（三维） | **17** | 网格上传、相机拟合、五种绘制模式、光照、与二维共存/清理、单帧 < 16.7ms、无控制台错误 |
| `replay-csharp.mjs` | **15** | 把 C# 真实导出的 patch 回放进浏览器，验证矩阵与 SVG 终态 |
| `replay-3d.mjs` | **6** | 三维 patch 回放，矩阵平移落点 |

**合计 137 项，全部通过**（2026-10-23 实跑）。其中 18 项专测表达式与事件，7 项专测 Mark，10 项专测状态程序：72 → 168 的人口阶梯、6s/7s 的销毁与残影数量、代数上限、固定步长可复现、`rev` 不变。

### 5.3 视觉回归

`docs/shots/` 存有每个阶段的截图基准（首帧、压力 500、实体、交互、morph、场景 3D、导出帧等），以及两套图标对照图。

---

## 6. 已知限制

按影响面排序，均为已知且可解释，不是故障：

| 限制 | 影响 | 备注 |
|---|---|---|
| 三维动画未接 `Track` / `Timeline` | 3D 帧序列按固定 1.5s 循环，不能对齐全局时长 | 二维已完全接入，三维是下一步 |
| 三维鼠标环绕未启用 | 相机只能由 `Camera 3D` 电池控制 | 代码在，`#layer-3d` 当前 `pointer-events: none`（避免吃掉二维的鼠标） |
| 无 `Light 3D` 电池 | 光照只能整体开关，不能加灯 | 粗糙度 / 金属度目前固定 0.42 / 0.05 |
| 三维线宽恒为 1px | WebGL 限制，无法设置线宽 | 需要粗线要走管状网格（`CreateFromCurvePipe`），尚未决定是否加 `Radius` |
| 三维无后处理 | 无 Bloom / 色调映射 | — |
| Entity 行为只有 `follow-curve` 一种 | 无法表达力、阻尼、分裂等自定义规则 | 行为组合（Behavior Graph）是下一阶段议题，不在当前范围 |
| 表达式目前只绑四个属性 | 宽度 / 不透明度 / 色相 / 缩放 | 结构与白名单已通用，加属性只改 `Bind` 的枚举与运行时一处；颜色只支持色相，不支持 RGB 分通道 |
| Mark 单次上限 4000 条 | 超过会截断并告警 | 4000 条约 1.7 MB 整帧；实际作品建议 1000 条以内，或加大 `Spacing` |
| `Style Map` 只按沿程位置映射 | 不能按曲率/切向做映射 | 曲率在 `Mark Along Path` 里已经算得出来，是下一版的小改 |
| `mask` 未实现 | 图层蒙版不生效 | `blend` 已通过 `globalCompositeOperation` 真实合成；只有 `mask` 是空字段，SVG 导出会写警告 |
| 行为程序只在 GH 侧编译 | 改一条写入要重新求解 | 与表达式绑定（绑在指令上）不同：状态程序是定义，改定义本来就要重发 |
| 实例状态不可外部读取 | 面板/导出拿不到"某个粒子现在多少岁" | `snapshot()` 只在运行时内部与验收里用；作品不需要读它 |
| `Draw Path` 有 10 个输入 | 与"电池不超过 6 个输入"的约束冲突 | 待收敛（拆样式或合并参数） |
| 多文档 / 多画布会话 | 单会话 | 协议明确列为 V0.1 不做 |
| 图标不跟随主题 | 固定深色芯片 | 刻意决策：面板恒浅色，避免宿主换肤导致整组图标失效 |

---

## 7. 下一步（建议优先级）

0. **行为图后续**：`Condition` 的一体化写法（目前比较用 `Runtime Math` 的 9/10）、按图层批量写状态、导出时把状态程序写进独立 HTML。
1. **Shader（P1-C）**：TSL 材质图 + 后处理，等你给方案。
1. **三维接时间系统**：`Transform 3D` 的帧序列接入 `Track`（区间 + 缓动 + 静音），与二维共用一条时间轴。
2. **三维交互**：按需开关的鼠标环绕，明确与二维指针事件的边界。
3. **光照与材质**：`Light 3D` 电池 + 粗糙度 / 金属度可调 —— 这是"作品感"提升最明显的一项。
4. **阶段 4（画笔 / 字体 / 图像）**：二维的表现力仍停在"线 + 面"，文字与图像缺失是最大缺口。
5. **阶段 6（发布封装）**：导出已通，把"一个 GH 定义 + 一份运行时"打成可分发的作品包。
6. **工程收尾**：`Draw Path` 输入收敛、三维线宽方案、图标在深色面板下的可选变体。

---

## 附：当前可用链路

```
静态：  Curve ─► Path Input ─► Render Output
精修：  Curve ─► Path Input ─► Draw Path ─► Render Output
生长：  Curve ─► Path Input ─► Draw Path ─► Draw Progress ─► Track ─► Timeline ─► Render Output
变形：  Curve × N ─► Morph ─► Draw Path ─► Track ─► Render Output
对象：  Curve ─► Entity Blueprint ─► Entity Look ─► Render Output.Entities
实时：  Noise 1D ─► Signal to Float ─► (GH 原生电池) ─► 任意几何 ─► Draw Path

三维（三条来源，可并行）：
  Mesh  ─► Mesh Input ─► Transform 3D ─► Draw 3D ─────────────┐
  Point ─► Point 3D   ─► Transform 3D ─► Draw 3D（模式 3 点）─┤
  Curve ─► Curve 3D   ─► Transform 3D ─► Draw 3D ─────────────┤
  Point ──────────────────────────────► Draw Points ──────────┴─► Scene 3D ─► Render Output.Scene
```
