# 阶段 5 · 三维场景与 TSL 材质

对应任务书第五节。**这一阶段不碰二维那条链路** —— 现有的 `Path → Draw Path → Track → Timeline` 一行不改。

---


---

## 当前进度（v0.25.0，已安装）

### 已完成：初步目标

按你给的四条拆开，逐条对应：

| 你的描述 | 实现 | 验收 |
|---|---|---|
| 「独立模块，之前是 drawing 模块」 | GH 分类 `Colugo / 3D`，与 `Drawing` 平级，5 颗电池 | 标签页里出现 `3D` 分组 |
| 「读取 Rhino mesh 类型」 | `Mesh Input`：Mesh / SubD / Brep → 三角化 | probe `in=2 out=2` |
| 「独立 3D camera」 | `Camera 3D`：位置 / 目标 / FOV / 投影 / 缩放 | probe `in=5 out=1` |
| 「不设 camera 默认 boundingbox 居中」 | 运行时按包围球自动取景，`up = Z` | 实测 `dist/radius = 1.35`，相机在包围球外 |
| 「先 3D 动画组件，再形成 cmd，再进 Draw 3D」 | `Mesh Input → Transform 3D → Draw 3D → Scene 3D` | 见下方链路 |
| 「3D 和 2D 共存」 | 两个 canvas 叠层，独立渲染循环 | 切回 2D 后 3D 清空，2D 内容完好 |
| 「预设材质 + uniform，点云线框等」 | `Draw 3D` 的 Mode 1–5 | 见材质表 |
| 追加：3D 点、3D 线 | `Point 3D`（1 口）/ `Curve 3D`（2 口）/ `Draw Points`（4 口） | `verify-3d` 点云 420、线段 438 |

### 链路（与二维同构）

```
Rhino Mesh ─► Mesh Input ─► Transform 3D ─► Draw 3D ─► Scene 3D ─► Render Output.Scene
   (Mesh/SubD/Brep)              ↑                        ↑
                              Xform ×1 或 ×N          Camera 3D（可选）
```

| 层 | 三维电池 | 二维对应物 |
|---|---|---|
| 几何 | `Mesh Input` / `Transform 3D` | `Path Input` / `Transform` / `Morph` |
| 绘制 | `Draw 3D`（材质与颜色） | `Draw Path`（描边与填充） |
| 时间 | 未接（下一批） | `Track` / `Timeline` |

### 材质：Mode 是数字，不是字符串

| Mode | 名称 | Three.js 材质 | 说明 |
|---|---|---|---|
| 1 | solid | `MeshStandardNodeMaterial` | 受光，可用 Glow 加自发光 |
| 2 | wireframe | `MeshBasicNodeMaterial` + `wireframe` | `Size` 当线宽 |
| 3 | points | `PointsNodeMaterial` | `Size` 当点径，不随距离衰减 |
| 4 | normals | `MeshNormalNodeMaterial` | 忽略 Colour |
| 5 | unlit | `MeshBasicNodeMaterial` | 不受光，纯色 |

全部走 TSL（`colorNode` / `emissiveNode`），所以 WebGPU 与 WebGL2 两个后端共用一套材质代码。

### 验证

| 项 | 结果 |
|---|---|
| 运行时三维验收 | `verify-3d.mjs` **17/17**（四种几何 + 自动取景 + DPR 跟随） |
| C# → 运行时协议回放 | `replay-3d.mjs` **6/6**（mesh / 材质 / 点径 / 矩阵平移 / 对象数） |
| 二维回归 | `verify.mjs` **64/64**（没碰坏任何东西） |
| 插件编译 | 0 错误 0 警告 |
| 已安装产物 | `Colugo 0.25.0.0`，probe `RESULT: PASS` |

### 两个真 bug（都是运行时才暴露的）

1. **矩阵不该转置**。Rhino 把平移放在最后一行（M30–M32），Three.js 的 `Matrix4.elements`
   也把平移放在索引 12–14 —— 两套布局是同一个序列。我按"行向量 vs 列向量"的直觉做了转置，
   结果旋转反向、平移丢失。探针用"绕 Z 90° + 平移 (10,20,5) 把 (1,0,0) 送到 (10,21,5)"
   把它钉死了。
2. **`Transform 3D` 的动画在 `Draw 3D` 处断了**。`Draw 3D` 没把 mesh 上的 `Xform` 搬到 command 上，
   几何层的动画到绘制层就丢了。探针的 `matrix=False` 报出来的。

### v0.26.0 追加：点云、线与分辨率

**三种几何一个类型。** `MeshDef.Kind` = `mesh` / `points` / `lines`，索引含义不同，
编码与增量逻辑完全复用。`Curve 3D` 按归一化长度采样（nurbs 的参数化不均匀，按 count 采样会疏密不匀）。

**"糊"是真 bug，两个原因叠在一起**：`resize()` 在 `renderer` 建好之前被调用，画布 buffer
停在默认 300×150 被 CSS 拉伸；且 `_syncSize` 只比宽高、不比 DPR，跨屏拖窗口不会重建。
现在每帧比对 宽/高/DPR，实测 `bufW = cssW × min(dpr,2)`，DPR 改 2 后跟着翻倍。

### v0.27.0：一组而不是一根

`Curve 3D` 之前把所有曲线**合并成一个对象**，所以整组只能共用一个样式，也读不出"这是几根"。
现在每条曲线一个对象：`Curves` 进 N 条 → `Objects` 出 N 个 → `Draw 3D` 出 N 条指令。

`Draw Points` 是"点直接进场景"的短路：`Point3d → Draw Points → Scene 3D`，
不用先转成 `Point 3D` 再套 `Draw 3D` 的 Mode。`Point 3D` 仍然保留，用于需要先 `Transform 3D` 的场合。

**3D 线宽是硬限制**：`LineBasicNodeMaterial` 恒为 1px（WebGL 的 `linewidth` 大多不生效）；
`Line2` 能画粗线但依赖 `ShaderMaterial`，在 WebGPU 后端不可用。要粗线只能走管状 mesh。

### 还没做

| 项 | 状态 |
|---|---|
| 三维动画接 `Track` / `Timeline` | 现在 `Transform 3D` 的帧序列按 1.5 秒固定循环 |
| 鼠标环绕 | 代码在，但 3D canvas 设了 `pointer-events: none`，等交互层统一处理 |
| `Light 3D` 电池 | 现在用内置三点光，`Scene 3D` 的 `Lights` 开关控制 |
| 粗糙度 / 金属度可调 | 固定 0.42 / 0.05，端口留给下一版 |
| 后处理（Bloom / 色调映射） | 未做 |

---

## 一、路线已验证（不是推演）

在真实 Chrome 里跑了一个最小用例：一个 24×24 细分的立方体，三个 TSL 节点同时生效
（`colorNode` 按 UV 渐变、`positionNode` 沿法线做正弦位移、`emissiveNode` 随时间呼吸）。

| 项 | 实测结果 |
|---|---|
| Three.js 版本 | **0.186.1**（r186） |
| 无打包工具 | ✅ `importmap` + 三个 build 文件即可，不需要 webpack/rollup |
| WebGPU 后端 | ✅ `backend: "WebGPUBackend"`，343 帧无错误，出图 |
| **WebGL2 后端** | ✅ `backend: "WebGLBackend"`，354 帧无错误，**画面与 WebGPU 一致** |
| TSL 三节点 | ✅ 颜色/位移/自发光全部编译并生效 |
| 体积 | `three.core.js` + `three.webgpu.js` + `three.tsl.js` = 3.8 MB 原始，**gzip 后 0.70 MB** |
| `.gha` 影响 | 0.25 MB → 约 1.0 MB |

**为什么一定要验 WebGL2**：WebView2 里 WebGPU 不可靠（Microsoft 明确不建议在生产应用里用 flags 开启），
所以你的预览窗大概率会走 WebGL2 后端。TSL 的价值就在这里 —— 同一份节点代码，两个后端都能编译。

---

## 二、架构：3D 是叠上去的第二层，不是重写

```
#stage-inner
├── <canvas id="layer-2d">     ← 现有 Canvas 2D，一行不改
└── <canvas id="layer-3d">     ← 新增，WebGL/WebGPU，叠在上层
```

- 两个 canvas 各自独立的渲染循环，互不驱动
- 2D 有内容时 3D 层透明；反之亦然
- `Runtime Info` 多两个字段：`3D 后端`（webgpu / webgl2）、`3D 帧率`

任务书要求"不应影响前面已经建立的二维绘图架构"，这是唯一能满足它的结构。

---

## 三、分批任务

每批都是**能判定真假**的产物，不是"完成了 X"。

### S1 · 三维骨架（最小可跑）

| 做什么 | 验收 |
|---|---|
| three.js 三个 build 文件作为嵌入资源进 `.gha` | `unzip -l Colugo.gha` 能看到三个 js |
| `BridgeServer` 提供 `/vendor/*`，带 `Content-Encoding: gzip` | `curl -I` 返回 200 + gzip |
| 运行时加 `layer-3d` + 一个 `Scene3D` 模块（渲染循环、resize、dispose） | 页面加载后 `window.Colugo.scene3d` 存在 |
| `Scene` 电池：背景色 + 内置一个测试立方体（不接任何几何） | 预览窗里出现一个 TSL 着色的立方体 |
| HUD 显示后端类型 | 显示 `3d webgl2` 或 `3d webgpu` |

**这一步的价值是证伪**：如果 WebView2 里 TSL 起不来，后面全部要重选。所以它必须先单独跑通。

### S2 · Mesh 通路

| 做什么 | 验收 |
|---|---|
| `Mesh` 电池：Rhino `Mesh` / `SubD` / `Brep`（自动转 Mesh）→ `Colugo Mesh` | 端口 3 个以内 |
| 协议 `mesh.upsert`：`positions` / `indices` / `normals` / `uvs`，f32 + base64 | 与现有 `geom` 同一套编码 |
| 运行时：`MeshDef → BufferGeometry`，指纹不变则复用 | 拖顶点时只重传变化的网格 |
| 增量策略：几何指纹（顶点哈希）不变就跳过 | 拖动相机不产生任何 patch |

验收：Rhino 里一个 5000 面网格，拖动控制点，浏览器跟着变；单帧 patch 体积实测并记录。

### S3 · 相机 / 灯光 / 材质（预设）

| 电池 | 输入 | 说明 |
|---|---|---|
| `Camera` | 位置 / 目标 / FOV / 正交? | 4 个口 |
| `Light` | 类型 / 位置 / 颜色 / 强度 | 4 个口，列表可加多个 |
| `Mesh Look` | Meshes / 颜色 / 粗糙度 / 金属度 / 自发光 / 透明度 | 6 个口 |
| `Scene` | Meshes / Camera / Lights / 背景 | 4 个口，输出给 `Render Output` |

`Render Output` 加第 6 个输入 `Scene`（Canvas / Commands / Entities / Scene / On / Preview = 6，到上限）。

验收：鼠标拖拽能绕物体转；改光源位置能看到明暗变化；材质参数实时生效。

### S4 · 三维动画（复用现有时间系统）

| 做什么 | 验收 |
|---|---|
| `Transform 3D` 电池：接 GH 原生 `Transform`（4×4），1 个=静态、N 个=帧序列 | 与现有 2D `Transform` 同一套语义 |
| 协议：`mesh.transform` 支持 4×4 矩阵 | SVG 不涉及，PNG 导出保留 |
| `Track` 的 `Frames` 扩展为可选 4×4 | 2D 的 2×3 不受影响 |

验收：`Noise 1D → Rotate → Transform 3D`，3D 物体随噪声实时转，GH 每 100ms 更新一次，画面不卡。

### S5 · TSL 材质（**这一批需要你先选方案**）

见下方"三个分叉"的第 2 条。

### S6 · 后处理与示范作品

| 做什么 | 验收 |
|---|---|
| 后处理：Bloom / 色调映射 / 暗角 | 在 WebGL2 后端下也能出效果（`RenderPipeline`，r183+） |
| 示范作品 `Dynamic Shader Surface` | 任务书点名的交付物 |

---

## 四、三个需要你定的分叉

### ① 3D 与 2D 的关系

| 选项 | 含义 | 代价 |
|---|---|---|
| **A. 叠层共存（推荐）** | 2D 和 3D 同时显示，各自独立 | 导出要分两套；HUD 多两个字段 |
| B. 互斥模式 | 切到 3D 就关掉 2D | 简单，但"2D 图形贴在 3D 表面上"这类做不了 |

我推荐 A，因为它不破坏任何现有东西，而且以后想做"把 2D 路径当成 3D 里的线"时，路是通的。

### ② 材质：预设还是节点图

任务书要的是完整节点系统（`ShaderFloat` / `ShaderVec3` / Add / Multiply / Sin / Position / Normal / UV / Time …）。
但你在 Signal 那件事上说过一句关键的话：**"不是创建一个专用概念"**。

| 选项 | 内容 | 工作量 | 风险 |
|---|---|---|---|
| **A. 预设 + uniform** | 6 种材质（Standard / Basic / Toon / Normal / Points / Line），颜色、粗糙度、金属度、自发光、透明度都能接 GH 数值 | 小 | 做不了"颜色随 UV 变化"这类**空间变化** |
| **B. 节点图** | 任务书原文那套，在 GH 里连线，编译成 TSL 表达式 | 大 | GH 里 `Add`/`Multiply` 各一颗电池，一个中等材质要连十几颗，可能比直接写 TSL 还啰嗦 |
| **C. A 先上，B 后置** | 先让 3D 能用，节点图作为独立模块后做 | 中 | 需要你先确认顺序 |

我的判断：**C**。理由 —— 现在 3D 里连一个网格都还没有，谈节点图太早；而"预设 + uniform"能立刻做出作品，并且它本身就是 `Material` 类型的地基，B 之后可以接在同一层上。

### ③ 3D 几何的来源

- 只吃 Rhino `Mesh`？
- 还是也吃 `SubD` / `Brep`（自动 `Mesh.CreateFromBrep`）？
- 是否也吃 `Curve`（转成 3D 里的 `Line`）？

我建议 **Mesh + SubD + Brep 自动转**，`Curve` 单独一颗 `Curve 3D` 电池后置。因为曲线的 3D 化和曲线的 2D 化是两件事，混在一颗电池里会重蹈 `Draw Path` 10 个端口的覆辙。

---

## 五、明确不做的

| 不做 | 原因 |
|---|---|
| Compute Shader 粒子 | 只有 WebGPU 后端支持，WebView2 里不可用 |
| GLTF / GLB 导入导出 | 阶段 6 的事 |
| PBR 环境贴图 / IBL | 需要 HDR 资源，会再涨 1–2 MB |
| 阴影的软硬混合调参 | 先把方向光阴影做对，参数化后置 |
| 3D 里的 Entity 系统 | 现有的 Entity 是 2D 屏幕空间的，不适用于 3D 空间 |

---

## 六、我的建议顺序

```
S1（1 步，证伪）→ S2（Mesh 通路）→ S3（相机灯光）→ S4（动画）→ 停下来看作品 → S5 → S6
```

S1 单独跑通之后再往下走。如果 WebView2 里 TSL 起不来，即时换方案（退回 `WebGLRenderer` + 手写 GLSL），
损失只有 S1 那一步。
