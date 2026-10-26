# GHCE · TSL Shader 模块专项研发任务书

> **文档版本**：V1.0（可执行设计与研发规划草案）  
> **编制日期**：2026-10-26  
> **基准系统**：GHCE **0.28.2**（依据用户提供的《GHCE 开发进度报告》，2026-10-23）  
> **运行时基准**：Three.js **r186**、`WebGPURenderer`、WebGPU 优先并在可用时回退 WebGL2  
> **适用范围**：Rhino 8 / Grasshopper 插件、GHCE PatchBuilder、浏览器 Runtime、独立 HTML 导出  
> **状态说明**：标有「现有」的能力仅依据进度报告；标有「拟新增」的设计尚未实现。本任务书不是对现有仓库代码的审计。

---

## 0. 执行摘要：这个 Shader 模块究竟要做什么

**目标不是给 `Draw 3D` 增加更多材质预设，而是让用户通过 Grasshopper 电池编写真正运行在 GPU 上的视觉程序。**

从使用体验看，用户应该能完成：

1. GH 输入一张 Mesh，无须手写 JS/GLSL，即可通过 `Position → Sin → Color` 节点生成程序化颜色。
2. 在 GH 中修改 Slider，浏览器只更新 GPU Uniform，不重新构建 Mesh 或整个 Shader。
3. 让几何顶点根据位置、时间、法线及纹理在 GPU 上变形，而不是 GH 每帧输出 Morph 帧。
4. 用 GH 提供的曲率、标量场、分组 ID、顶点颜色等**几何属性**驱动 GPU 视觉效果。
5. 将多个场景、后处理和画面图层组织成可编排的实时视觉作品。
6. 最后直接从 GHCE 输出静帧、动画序列或独立交互作品。

**产品定位**：Shader 是 GHCE 九大功能域中 `Render & Shader` 的一个子系统，与 `Geometry / Drawing / Logic / Entity / Animation / Interaction / Publish` 协作；不能把 GHCE 缩小成 Shader 编辑器。

**推荐推进顺序**：

`S0 架构约束 → S1 材质 Shader MVP → S2 Uniform / Runtime Expression → S3 顶点变形与几何属性 → S4 纹理与二维 GPU 画布 → S5 Render Pipeline / 后处理 → S6 函数、SDF、Raymarch → S7 GPU Compute（可选）`

其中 **S1 + S2 是第一轮必须跑通的核心价值闭环**；S5 和 S7 不作为 S1 验收的前置条件。

---

## 1. 现有 GHCE 基础与不可破坏的边界

### 1.1 基于 0.28.2 已具备

| 子系统 | 已有能力 | 对 Shader 开发的意义 |
|---|---|---|
| GH 插件 | `.gha`、31 只电池、12 个数据类型 | 可直接扩展 Shader 类型及电池家族 |
| 3D 输入 | `Mesh Input`、`Point 3D`、`Curve 3D` | 有稳定的 GPU 几何来源 |
| 3D 表达 | `Draw 3D`、`Camera 3D`、`Scene 3D` | 可新增 Node Material，而不重做场景 |
| Runtime | Three.js r186、`WebGPURenderer` | 已选定可承载 TSL 的渲染后端 |
| 桥接 | WebSocket + JSON Patch、Geometry Fingerprint | 可扩展 shader graph / uniform 增量操作 |
| 预览 | WPF + WebView2、独立浏览器 URL | Shader 在同一运行时预览 |
| 动画 | Timeline / Track / Morph + 浏览器时钟 | Shader 时间可共享作品时钟，不需 GH Timer |
| 导出 | PNG、SVG、帧序列、独立 HTML、视频 | 可扩展 Shader 作品导出和可重复性测试 |
| 验证 | 31 只电池探针、102 项运行时断言 | 必须延续组件/API/像素回归测试 |

### 1.2 现有缺口（报告明确列出）

- `Draw 3D` 当前是颜色、Glow、Opacity 和固定材质预设；**没有真正的 Shader 类型、电池图或 TSL 编译**。
- `Light 3D` 尚无独立电池，三维光照只可整体开关。
- 3D Transform 动画还没有与统一 `Timeline / Track` 完全接通。
- 3D 没有后处理 Bloom / RenderPipeline。
- 图层 `mask / blend` 仅传递部分协议属性，不能假定完整图层合成已就绪。
- 仅有预览端的作品导出能力，不代表完整的资源打包、确定性模拟和浏览器兼容性已完成。

### 1.3 绝对不能破坏

1. **不替换现有 `Draw 3D` 的基础使用方式**。旧 GH 定义不连 TSL Material 仍按原模式渲染。
2. **不为每个 Shader 电池开独立 WebSocket**。所有电池只生成描述数据，由 PatchBuilder / Render Output 统一提交。
3. **不让 GH Timer 承担 GPU 逐帧运行**。浏览器有独立时间循环。
4. **不将 Scene Graph、Shader Graph、Render Pipeline Graph 合并成一个无差别的类型**。
5. **不将 TSL 当做任意 JavaScript 执行接口**。默认采用白名单化、可验证的 Shader IR。
6. **不因 Shader 失败导致整个 2D/3D 预览进程崩溃**。需要错误隔离和可用的回退材质。

---

## 2. 设计哲学：Shader 是一种“延迟执行的 GPU 表达式”

GH 中的一条连接可以传递三种本质不同的值。

| 类型 | 计算发生在哪儿 | 一条 `Sin` 的含义 | 适合用途 |
|---|---|---|---|
| **GH Number** | GH 求解时 / CPU | 立即计算一个数 | 几何参数、离散设计状态 |
| **RuntimeFloat** | 浏览器运行时 / JavaScript | 每帧对运行时数据求值 | 对象移动、画笔粗细、事件驱动 |
| **ShaderFloat** | GPU 的 Vertex / Fragment 阶段 | 每顶点或每片元执行数学运算 | 着色、纹理、法线扰动、位移 |

三者不应该因为都叫 `Float` 就隐式混合。

**唯一允许的桥接方向（首版）**：

- `GH Number → Shader Constant`：写入 Shader 图中的常量；改值可导致表达式图更新。
- `GH Number → Shader Uniform`：创建动态 Uniform；改值只更新 Uniform，不重编译。
- `RuntimeFloat → Shader Uniform Binding`：浏览器每帧更新 Uniform；不回流 GH，不重编译。
- `GH Mesh Attribute → Shader Attribute`：几何上传时携带顶点数据；GPU 按顶点读取。
- `ShaderFloat → Material Slot`：绑定到颜色、透明度或位移等**类型受控**的目标。

**禁止默认实现**：`ShaderFloat → GH Number`。GPU 每像素产生的值不是 GH 求解中的单一数值；如果需要 GPU 结果回读，应另做异步采样 / Compute Readback 接口，并明确延迟和数据量。

### 2.1 与 GH 原生数据树的关系

- 数据树负责**组织多少个几何、材质、Uniform、对象**，不负责让一个 Shader `Sin` 在 GH 中执行成百万次。
- 同一个 `ShaderGraph` 可以复用到树里的 N 个 Mesh；运行时采用**材质共享 + 实例/对象 Uniform 区分**策略。
- 数据树 Branch Path 不是 Shader 图节点永久 ID；使用稳定 GUID、端口索引和业务 ID。
- 同一 Shader 图可由多个对象引用；只有在参数需要隔离时复制实例 Uniform 容器，不必复制整个 Shader 程序。

### 2.2 与 Runtime Expression 的关系

二者是并列而非替代：

```text
GH Slider ───────────────────────┐
                                  ▼
Runtime Time → Sin → Remap → RuntimeFloat ── Uniform Binding
                                                    │
GH Position / Normal / UV → Shader Graph ← Shader Uniform
                                 │
                                 ▼
                         TSL Node Material
                                 │
                                 ▼
                           GPU 绘制结果
```

时间信号来自同一个 **Artwork Clock**。播放、暂停、跳帧、导出时必须使用 GHCE 的统一时间，而不是让不同材质各自使用不可控的墙上时钟。TSL 自带 `time` 节点，但 **GHCE 首版建议显式创建并同步 `uTime` Uniform**，避免与离线时间、重置、倍速和交互状态冲突。

---

## 3. 总体架构与数据流

```text
Rhino Geometry / GH Data Tree                   GH Slider / Runtime Expression
             │                                           │
             ▼                                           ▼
      Mesh Input / Attributes                       Shader Uniform
             │                                           │
             └──────────────────┐  ┌────────────────────┘
                                ▼  ▼
                    GH Shader Nodes / ShaderGoo
                     [typed expression DAG]
                                │
                                ▼
                      Shader IR Compiler (C#)
                   校验 / 拓扑排序 / 语义 Hash
                                │
                                ▼
                 PatchBuilder: shader/material ops
                                │
                           WebSocket
                                │
                                ▼
                   JS TSL Graph Builder (r186)
           节点白名单解释 / Uniform Registry / Texture Registry
                                │
                                ▼
                      Three.js Node Material
                                │
             ┌──────────────────┴──────────────────┐
             ▼                                     ▼
       Scene 3D Output                       RenderPipeline
             │                                     │
             └──────────────────┬──────────────────┘
                                ▼
                      Browser Artwork Output
                         PNG / Frames / Web
```

### 3.1 关键架构决策

**编译两段式**：

1. **GH 侧“结构编译”**：电池只产生结构化 Shader IR，执行静态验证、类型检查、Graph Hash 与常量折叠，不生成任意 JS 源代码。
2. **浏览器侧“TSL 构造”**：遍历受支持的 IR Op，在 JS 中调用 `three/tsl` 的 API，构造 NodeMaterial / Node Graph；最终 WGSL/GLSL 编译由 Three.js 后端完成。

TSL 节点编译器将来可以替换实现，但 Grasshopper 的 Shader IR 不应与某个 Three.js 私有类实例直接绑定。

### 3.2 三种图分离

| 图 | 根输出 | 生命周期 | 例子 |
|---|---|---|---|
| **Shader Graph** | `Material.color / position / opacity` | Shader 拓扑变化才重建 | `UV → Sin → Color` |
| **Scene Graph** | `Scene / Camera / Visual Object` | 几何/对象变更时局部更新 | `GH Mesh → Draw 3D → Scene 3D` |
| **Render Pipeline Graph** | `FinalImage` | Pass 拓扑、尺寸或格式变化时重建资源 | `ScenePass → Bloom → Output` |

它们可以共享资源引用与 Uniform，但**不能假定 ShaderFloat 能直接连接到 Scene 或 Texture RenderTarget 端口**。

---

## 4. 电池规划：按“创作语义”而不是复刻所有 TSL API

### 4.1 S1 首批 MVP 电池（建议 12–14 种）

| 电池（建议） | 输入 | 输出 | 作用 / 首版限制 |
|---|---|---|---|
| `Shader Value` | Value, Type | `ShaderExpr` | Float / Vec2 / Vec3 / Color 常量 |
| `Shader Uniform` | Value, Name, Scope | `ShaderExpr`, `UniformRef` | 创建动态参数；名称与 ID 需唯一 |
| `Shader Position` | Space | `ShaderVec3` | Local 优先，World 后续 |
| `Shader Normal` | Space | `ShaderVec3` | Local / World，缺法线时报错或自动生成 |
| `Shader UV` | Channel | `ShaderVec2` | 首版仅 UV0 |
| `Shader Time` | — | `ShaderFloat` | GHCE 统一时钟（Uniform 桥） |
| `Shader Math` | Op, A, B | `ShaderExpr` | Add/Sub/Mul/Div、Sin/Cos、Abs、Clamp 等 |
| `Shader Vector` | Op / XYZ | `ShaderExpr` | vec2/vec3、Split、Dot、Normalize |
| `Shader Mix` | A, B, Factor | `ShaderExpr` | 插值和简单渐变 |
| `Shader Remap` | Value, From, To | `ShaderExpr` | 安全映射（输入范围零宽须明确处理） |
| `Shader Color` | RGB / Hex / Space | `ShaderVec3` | 颜色域与数值向量区分 |
| `TSL Material` | Color, Opacity, Position, MaterialMode | `MaterialDefinition` | 首版 Unlit，第二期扩展 Standard/PBR |
| `Apply Material` | Draw3D Command, Material | Draw3D Command | 最少侵入现有 `Draw 3D` |
| `Shader Diagnostics` | Shader / Material | Info | 编译日志、类型/阶段/ID 和错误来源 |

> **设计建议**：GH UI 初期不应堆 40 个常数运算电池。首版用 `Shader Math` 的 Op 枚举快速验证；待语言规则稳定后再提供独立的 Sin / Multiply 等常用快捷节点。对需要高复用的节点，后期增加组合封装组件。

### 4.2 S3 扩展：几何驱动型 Shader

| 电池 | 说明 |
|---|---|
| `Shader Attribute` | 按受控名称和类型读取 BufferGeometry 属性 |
| `Vertex Color / UV Set` | 顶点颜色、多套 UV；配置与验证来源数据 |
| `Curvature Attribute` | GH 计算曲率或其他标量，烘焙为 `float32` 属性供 Shader 使用 |
| `Field Bake` | GH 的场值在顶点上采样生成 Attribute / Texture，不强制每帧回 GH |
| `Vertex Displacement` | 将 `ShaderVec3` 绑定给 NodeMaterial 的顶点位置 |
| `Normal Strategy` | 原法线 / 近似更新 / 用户提供变形法线；避免自动宣称法线实时准确 |
| `Instance Attribute` | 按实例索引或 ID 携带随机种子、颜色和自定义数值（实现后验证） |

**重要警告**：顶点在 GPU 位移后，GH Mesh 的 RhinoCommon 包围盒、碰撞与几何测量不会自动更新。如果要导出“真正变形后的 Rhino Mesh”，必须另外实现 CPU 求值 / GPU Readback / Bake 策略，不能把视觉位移当成 GH 几何已改变。

### 4.3 S4 扩展：纹理与二维 GPU 绘图

| 电池 | 用途 |
|---|---|
| `Shader Texture` | 加载静态纹理，提供 UV / Sampling 配置 |
| `Texture Sample` | 在 Shader 中采样纹理；颜色空间明确 |
| `Shader Gradient` | Color Ramp 采样，可采用小型 LUT 纹理 |
| `Canvas as Texture` | 把 GHCE Canvas 2D 的内容作为 Shader 纹理输入 |
| `Render to Texture` | 把某个场景或中间结果生成纹理供后续使用 |
| `Shader 2D Surface` | 全屏四边形或专用 Canvas GPU Layer，不依赖 GH Mesh |
| `Screen UV / Resolution` | 面向屏幕空间、像素与归一化坐标的强类型输入 |

特别建议优先实现 `Canvas as Texture`：让 GHCE 的 Drawing / Composition 与 Shader 之间真正相互连接，而不是形成两个互不相干的工具岛。

### 4.4 S5 扩展：后处理与渲染管线

独立电池建议：

- `Scene Pass`
- `Bloom`
- `Blur`
- `Color Grade`
- `Edge Detection`
- `Composite Pass`
- `Render Pipeline`
- `Render Target / History`（后期）

这些电池的输出是 `ImageNode / PassDefinition / RenderTargetRef`，**不是 `MaterialDefinition`**。最终与 `Render Output` 统一合成。

### 4.5 S6/S7 高级阶段（按需）

- `Shader Function`：函数封装与子图复用。
- `SDF Primitive / Union / Smooth Union`：从几何数学场构造可复用函数。
- `Raymarch Scene`：特殊的全屏/材质阶段，包含步进、视线与终止条件；与普通 Material 分开建模。
- `Shader Loop / Condition`：受控循环与分支，校验静态边界和阶段限制。
- `Shader Custom Code`：高级且受信任的代码扩展接口，默认关闭，不与普通 Shader IR 混合执行。
- `GPU Compute / Buffer / Dispatch`：单独 GPU 计算子系统；对 WebGL2 fallback 提示不支持或明确的降级，不承诺自动等价。

---

## 5. 类型系统与端口契约（必须早于大量电池）

### 5.1 数据类型清单

| GHCE 类型 | 数据含义 | 关键约束 |
|---|---|---|
| `ShaderFloat` | GPU 标量表达式 | 不能直接输出到 GH Number |
| `ShaderVec2` | GPU 二维向量 | 空间/纹理坐标需要语义标签 |
| `ShaderVec3` | GPU 三维向量 | Normal/Position/Color 需要语义区分 |
| `ShaderVec4` | GPU 四维向量 | Color+Alpha 及全屏输出 |
| `ShaderColor` | GPU 颜色表达式 | 明确线性/sRGB 的输入边界 |
| `ShaderBool` | GPU 布尔表达式 | 仅支持阶段允许的条件逻辑 |
| `ShaderTexture` | 可采样纹理引用 | 独立资源生命周期 |
| `ShaderMaterial` | 材质定义 | 不作为普通数值输入 |
| `ShaderGraph` | 表达式 DAG | 稳定 Graph ID、Type、Stage |
| `RenderPass` | 渲染管线节点 | 与 ShaderMaterial 互斥用途 |
| `ShaderUniformRef` | 动态 Uniform 标识 | 定义与赋值分离 |

**端口显示建议**：Shader 类型使用独立的 Goo / Param，连接类型不匹配时给出明确提示；不允许 GH 默认隐式转换把 `Curve`、`Number` 和 `ShaderFloat` 随意连接。

### 5.2 强类型运算约束（首版）

- `float + float → float`
- `vec3 + vec3 → vec3`
- `vec3 * float → vec3`（明确广播）
- `dot(vec3,vec3) → float`
- `normalize(vec3) → vec3`
- `sin(float) → float`；若允许向量逐分量版本，另列白名单
- `mix(A,B,float) → type(A)`，要求 A/B 同类型
- `positionNode` 必须是 `vec3`，`opacityNode` 必须是 `float`，`colorNode` 至少是受支持的颜色向量
- `texture()` 输入需要 TextureRef 与有效 UV；若纹理尚未加载，提供占位策略

### 5.3 Stage 与 Space 规则

每个表达式记录：

```text
ValueType: float / vec2 / vec3 / vec4 / bool / texture
Stage: vertex / fragment / both / pipeline
Space: local / world / view / clip / screen / uv / none
Semantic: position / normal / color / generic
```

- 顶点属性在片元阶段的使用由 Three.js 节点系统处理插值或经明确接口表达；编译器不能假定任意操作在任意阶段都合法。
- `Shader Position Local` 与 `Shader Position World` 应是清晰可区分的选择。
- `Screen UV` 不得与 Mesh UV 静默互换。
- Rhino Z-up / Three.js 常用 Y-up 的转换必须**与现有运行时保持一致**，同时显式记录变换。若需统一约定，可采用 `(x,y,z) → (x,z,-y)` 的保手性坐标变换，但不得未经迁移就改变旧数据协议。

---

## 6. Shader IR：结构化中间表示

### 6.1 原则

1. Shader IR 是 **DAG**（有向无环图），不是 JavaScript 源码文本。
2. 节点采用**受控 op 白名单**，浏览器通过函数表构造 TSL 节点。
3. Graph ID 与 Node ID 稳定；不能根据 GH Data Tree 列表索引自动替代。
4. 所有节点包含值类型、操作、输入引用、阶段信息和错误来源。
5. 常量/Uniform/Attribute 必须区分，避免数值变化造成不必要的重编译。
6. 编译输出可 Hash 和缓存；禁用任意字符串代码执行。

### 6.2 概念协议示例（拟新增，并非当前 V0.1 已支持）

```json
{
  "schema": "ghce.shader-ir/0.1",
  "graphId": "mat-wave-01",
  "material": "unlit",
  "nodes": [
    { "id": "pos", "op": "position.local", "type": "vec3", "inputs": {} },
    { "id": "px", "op": "swizzle.x", "type": "float", "inputs": { "value": "pos" } },
    { "id": "frequency", "op": "uniform", "type": "float", "uniformId": "uFreq", "default": 3.0 },
    { "id": "phase", "op": "mul", "type": "float", "inputs": { "a": "px", "b": "frequency" } },
    { "id": "wave", "op": "sin", "type": "float", "inputs": { "value": "phase" } },
    { "id": "baseColor", "op": "const.vec3", "type": "vec3", "value": [0.15, 0.45, 0.85] },
    { "id": "color", "op": "mul", "type": "vec3", "inputs": { "a": "baseColor", "b": "wave" } }
  ],
  "outputs": {
    "color": "color"
  }
}
```

> 这段仅说明数据模型和依赖关系；真实 `mul(vec3,float)`、色域限制、输出范围和字段格式需在 Schema 中固定。颜色若需要位于 0–1 区间，应额外显式 Map/Clamp，不要静默截断表达式。

### 6.3 C# 对象建议（概念设计）

```csharp
public enum ShaderValueType { Float, Vec2, Vec3, Vec4, Bool, Texture }
public enum ShaderStage { Both, Vertex, Fragment, Pipeline }

public sealed record ShaderExprNode(
    string Id,
    string Op,
    ShaderValueType Type,
    ShaderStage Stage,
    IReadOnlyDictionary<string, string> Inputs,
    object? Literal,
    Guid SourceComponentId);

public sealed record ShaderGraphDefinition(
    string GraphId,
    int SchemaVersion,
    IReadOnlyList<ShaderExprNode> Nodes,
    IReadOnlyDictionary<string, string> Outputs);
```

**实现提示**：使用当前插件的 Goo / Param 标准包装这些定义；上面的 `record` 是建议接口形态，实际应匹配现有项目的语言版本、序列化库与二进制兼容方式。

### 6.4 编译流水线

1. 收集 Shader Material 上游表达式子图。
2. 检查缺失输入、重复 ID 和悬空引用。
3. 拓扑排序，检测循环；出现循环应在 GH 编译阶段失败，不能卡在 GPU 编译阶段。
4. 类型推断或验证：操作签名、Stage、Space、颜色语义。
5. 对纯常量子图做安全常量折叠。
6. 统一稳定序列化规则（键顺序、float 规范、ID）。
7. 计算 `graphHash`（拓扑/类型/常量/材质模式），另计算 `uniformValuesHash`。
8. 生成材料描述与增量 Patch。
9. 浏览器查缓存并构造 TSL / NodeMaterial。
10. 收到编译反馈并显示在 GH 组件与 Runtime Diagnostics 中。

**Graph Hash 与 Uniform Hash 必须不同**。若一个普通 Slider 接 `Shader Uniform`，其值发生变化只能触发 Uniform 更新；若 Slider 接 `Shader Constant`，可触发 Graph 变化。

---

## 7. WebSocket 协议扩展与兼容机制

### 7.1 新操作（建议）

沿用 GHCE 的 Patch 模型，按类型划分：

| Op | 触发条件 | 运行时行为 |
|---|---|---|
| `shader.graph.upsert` | Shader 拓扑/常量/类型变化 | 校验、编译或命中 Graph Cache |
| `shader.graph.remove` | 引用释放且无需保留 | 延迟释放材质/节点图资源 |
| `shader.uniform.set` | Uniform 值变化 | 更新已有 Uniform 节点值 |
| `shader.material.bind` | 物体绑定材质变化 | 将 MaterialRef 绑定到 `cmd3d` / 对象 |
| `shader.material.unbind` | 解除绑定 | 回退到旧材质模式 |
| `shader.asset.upsert` | 纹理上传/变更 | 纹理缓存与引用计数 |
| `shader.asset.remove` | 纹理资源移除 | 在无引用时释放 |
| `pipeline.set` | 后处理图变化（S5） | 重建 RenderPipeline 资源 |
| `pipeline.clear` | 关闭后处理（S5） | 回到基础场景输出 |

### 7.2 概念消息示例

```json
{
  "v": 1,
  "t": "patch",
  "seq": 405,
  "ops": [
    {
      "op": "shader.uniform.set",
      "graphId": "mat-wave-01",
      "uniformId": "uFreq",
      "type": "float",
      "value": 4.25
    }
  ]
}
```

此示例**不是当前 V0.1 的真实可用协议**。必须遵守现有封帧字段与消息字段的具体实现。鉴于当前未知 Op 会报错，新增 Op 时应通过 `hello/welcome` 的能力声明或协议版本协商，仅向支持 Shader 的 Runtime 发送新 Op，不能假定旧运行时会静默忽略。

### 7.3 增量更新判定表

| GH 侧变化 | 几何重传 | Shader 重新构建 | Uniform 更新 | 场景重建 |
|---|---:|---:|---:|---:|
| 修改 `Shader Uniform` 的 Slider | 否 | 否 | **是** | 否 |
| 浏览器 Runtime Time 推进 | 否 | 否 | **是（本地）** | 否 |
| 将 `Sin` 改为 `Noise`（图结构变） | 否 | **是** | 视情况 | 否 |
| GH 曲线 / Mesh 顶点变化 | **是（增量）** | 通常否 | 否 | 通常否 |
| 变换矩阵变化 | 否 | 否 | 否 | 否（更新对象矩阵） |
| 更换 Mesh UV / Attribute | 必要属性更新 | 通常否 | 否 | 否 |
| 更换 Material 引用 | 否 | 必要时 | 视情况 | 否 |
| 改 Bloom 强度 | 否 | 通常不改材质 | 后处理参数更新 | 否 |
| 改 RenderPipeline Pass 链接 | 否 | 不改材质 | 视情况 | **重建 Pipeline** |

---

## 8. Browser Runtime：TSL 构造与资源管理

### 8.1 模块划分（拟新增）

```text
runtime/
  shader/
    shader-registry.js        # graphId → Material / nodes / refs
    shader-validator.js       # schema/type/op whitelist
    tsl-builder.js            # Shader IR → three/tsl
    uniform-registry.js       # uniformId → UniformNode
    material-binding.js       # object/command → material
    texture-registry.js       # texture lifetime / color space
    shader-diagnostics.js     # errors / fallback
  render/
    pipeline-builder.js       # S5 才实现
    render-target-pool.js     # S5 才实现
```

> 目录为推荐拆分，不是断言当前仓库存在这些文件；集成时应遵守现有代码组织，避免为了目录整洁重构运行时。

### 8.2 白名单分发：不要 eval

推荐用固定映射表（示意）：

```javascript
const OPS = {
  'sin': ({ value }) => value.sin(),
  'add': ({ a, b }) => a.add(b),
  'mul': ({ a, b }) => a.mul(b),
  // position.local、uv 等由明确的 TSL import 提供
};
```

由服务端和浏览器双重校验，限制节点数、图深度、纹理数量等，避免恶意或错误子图造成大量 GPU 编译负担。

### 8.3 r186 版本的最小 TSL 例子（仅供 JS 实现参考）

```javascript
import * as THREE from 'three/webgpu';
import { positionLocal, sin, uniform, vec3 } from 'three/tsl';

const uTime = uniform(0);
const uAmplitude = uniform(0.25);
const uFrequency = uniform(3);

const phase = positionLocal.x.mul(uFrequency).add(uTime);
const wave = sin(phase);

const material = new THREE.MeshBasicNodeMaterial();
material.colorNode = vec3(
  wave.mul(0.4).add(0.5),
  wave.mul(0.2).add(0.4),
  wave.mul(0.3).add(0.5)
);

// 如果启用位移，位移语义是“顶点局部位置”，不是 Rhino Mesh 修改：
material.positionNode = positionLocal.add(
  vec3(0, 0, 1).mul(wave.mul(uAmplitude))
);

// 使用 GHCE 作品时间更新，不以独立墙钟作为导出基准。
function updateShaderClock(artworkTimeSeconds) {
  uTime.value = artworkTimeSeconds;
}

function updateAmplitude(nextValue) {
  uAmplitude.value = nextValue;
}
```

**限制**：该代码是最小 API 方向示意，实际在当前仓库运行前，仍要确认 three r186 的 import map、材质构建入口、WebView2 后端和位置节点处理。不要将它当成已经在 GHCE 上跑通的代码。

### 8.4 Graph Cache 与 GPU 资源生命周期

- 按 `graphHash + materialMode + backendCapability` 查材质缓存。
- Uniform Registry 单独维护值，不作为 Graph Hash 的一部分。
- 对同一个 Material 的多对象共享应明确 Uniform 范围：`Shared` / `PerObject`；MVP 默认 Shared。
- 对纹理、材质、RenderTargets 采用引用计数或集中所有权规则。
- 只有编译成功且材质完整后，原子切换到新材质；失败时保留最后一个可用材质或使用明确的错误材质。
- 对于可反复调整的 Shader，防止旧资源滞留（调用相应 `dispose()` 并由 Runtime 管理生命周期）。
- 如果浏览器断线重连，Runtime 应从当前 GHCE Artwork Snapshot 完整恢复 Shader 定义和 Uniform 值。

### 8.5 编译诊断信息

必须返回至少：

```json
{
  "kind": "shader.compile.error",
  "graphId": "mat-wave-01",
  "nodeId": "wave",
  "sourceComponentId": "<GH-Component-GUID>",
  "code": "TYPE_MISMATCH",
  "message": "mul expected float/vec3 compatible inputs"
}
```

其中 NodeID/GH GUID 可能只能精确定位到 GH 层静态验证错误；GPU 后端原始编译报错未必能自动准确映射到某个电池。应该明确区分“GH IR 类型错误”与“GPU 后端编译错误”，不要伪造精确位置。

---

## 9. Material 与 Scene 的集成方案

### 9.1 首版接口选择

**建议：新增 `TSL Material`，再新增 `Apply Material`；暂时不改旧 `Draw 3D` 的所有已有端口。**

```text
GH Mesh → Mesh Input → Draw 3D ───────────┐
                                         ▼
Shader Position → Sin → Shader Color → TSL Material
                                         │
                                         ▼
                                   Apply Material
                                         │
                                         ▼
                                Scene 3D → Render Output
```

如果 `Apply Material` 需要变更现有 Commands 3D 数据类型，先添加可选 MaterialRef 字段并保留旧序列化格式的默认解释；不要为了减少一只电池就改变旧端口顺序或破坏 GUID。

### 9.2 材质类型演进

| 阶段 | 模式 | 能力 |
|---|---|---|
| S1 | `Unlit / MeshBasicNodeMaterial` | 程序化颜色、透明度、基础顶点位移 |
| S3 | `Lit / MeshStandardNodeMaterial` | 基础光照、材质颜色、粗糙度、金属度、法线 |
| S4 | `Texture Material` | UV 纹理、Ramp、程序化图案与顶点属性 |
| S6 | `Advanced Material` | 自定义函数、SDF / 特殊视觉效果 |

`MeshBasicNodeMaterial` 不依赖灯光，非常适合判断 Shader IR 是否正确；PBR 先后顺序可后调。

### 9.3 顶点变形与相机

- **Vertex Displacement 不是 GH Transform/Morph 的替代品**，而是每帧在 GPU 上基于位置/法线等数据进行变形。
- 阴影、法线、包围盒与裁剪：GPU 位移后如需准确照明/阴影，必须提供正确的法线与边界策略；首版先要求可见颜色和几何位移，不承诺完美阴影。
- TSL 的 `positionNode` 代表新的局部顶点位置；不能把 GH 的世界坐标直接作为局部位置。
- Mesh 点云/线框与 Surface Material 的适配需分开考虑，不能默认所有 Draw 3D 模式共享同一材质实现。

---

## 10. S4：2D Shader Canvas 与 p5.js 绘图系统的关系

**Shader 不应该只对 3D Mesh 生效。** 这是避免 GHCE 退化成材质编辑器的关键。

建议做两个平等入口：

1. **Material Shader**：附着在 Mesh、Point 或其他 3D 对象上。
2. **Screen Shader**：在独立的 GPU 2D 画布上运行；其输入包括 `Screen UV / Time / Resolution / Canvas Texture`，结果可与现有 Canvas 2D 合成。

### 10.1 示例工作流

```text
GH Curves → Draw Path → Canvas 2D ── Canvas as Texture ──┐
                                                         ▼
Shader UV → Distortion / Noise ── Texture Sample ── Screen Shader
                                                         │
                                                         ▼
                                         Composite → Render Output
```

这样可以把 GH 的几何线稿交给 Shader 做位移、彩色干涉、像素化或实时合成。纯 2D Shader 也不必拥有一个无意义的 GH Mesh。

### 10.2 资源与合成边界

- Canvas 2D → GPU Texture 可能引入上传成本；静态/低频更新应缓存并只在画面改变时上传。
- 如果 Canvas 2D 本身每帧变化，需明确允许更新频率与性能预算。
- Shader 的输出与原有 Canvas 图层叠放，应提前定义：绘制顺序、透明度、色彩空间、是否预乘 Alpha、遮罩策略。
- SVG **不能无损表达任意 GPU Shader 效果**。导出为 SVG 时须明确栅格嵌入或禁用部分 Shader 图，绝不能暗中当成矢量结果。

---

## 11. S5：RenderPipeline / 后处理（独立里程碑）

Three.js 官方 `RenderPipeline` 与 TSL 后处理链可以管理 Scene Pass、Bloom 等效果，但它是一套**运行时图像处理机制**，不是 `MeshStandardNodeMaterial` 的普通插槽。

### 11.1 MVP 后处理路径

```text
Scene 3D → Scene Pass → Bloom → Final Output
```

首版只做：

- 可启用/停用 Bloom
- Strength / Radius / Threshold 动态调节
- 与原始场景输出一致的尺寸及 DPR
- 输出图片中保留后处理结果
- 浏览器不支持所需功能时明确反馈，不悄悄忽略

### 11.2 后处理资源约束

- `RenderPipeline` 需独立持有并管理 output node 与内部纹理。
- Pass 图结构变化与参数变化区分，不要每次 Slider 改 Bloom 强度就重建所有目标纹理。
- 任何持久历史纹理/Feedback 必须是显式状态节点，不能伪装成无状态 ShaderFloat。
- 同屏 Canvas2D 与 Three.js 的最终合成顺序要记录在 Artwork，而非隐藏在 HTML DOM 顺序里。
- 视频逐帧导出时，必须渲染并采集的是**经过后处理的最终输出**。

---

## 12. S6/S7：高级 Shader / SDF / Shadertoy / Compute

### 12.1 SDF 与 Raymarch 是独立语言层

你的早期 Shadertoy 例子是沿射线做 80 次密度积分，而不是单纯读取 `h=0` 曲面。GHCE 可以支持这类作品，但不能误把“GH 隐式面 + 简单材质”说成原图等效。

示意场：

```glsl
h = y + 0.5*sin(2*x+t) + 0.3*sin(3*z-t) + 0.15*sin(5*x+2*z);
```

要复现它的视觉，除了计算场值，还需要：相机射线、三维采样、步进循环、密度积分、颜色累积和最终映射。

建议在 Shader MVP 稳定后新增高层的：

- `SDF Field`（由受控公式定义空间标量场）
- `Ray Setup`（可用相机/UV 定义射线）
- `Raymarch`（有上限的循环与终止规则）
- `Volume Accumulation`（受控颜色/密度累积）
- `Fullscreen Output`（屏幕空间画面）

复杂 Shadertoy 迁移可能需要额外数学函数、反复采样和循环机制，**不得承诺自动导入任意 GLSL**。

### 12.2 自定义代码扩展

三类入口按安全性、可维护性递进：

1. **Built-in TSL Functions**：首选，完全从 IR 白名单构建。
2. **Custom TSL Function Library**：受信任开发者提供封装好的已测试函数，GH 只调用命名函数与强类型参数。
3. **Raw WGSL / GLSL Code Node**：高级模式，必须明确版本/后端、错误处理和禁用策略；不作为默认 GH 图的通用文本输入。

官方 TSL 生态也有原生代码函数能力，但这不代表用户写的任意 GLSL 会自动跨 WebGPU/WebGL 后端兼容。

### 12.3 GPU Compute 另列专项

GPU Compute 涉及：

- Storage Buffers / Texture 写入
- Dispatch / 工作组
- 同步与资源竞争
- GPU → CPU 异步回读
- WebGPU 能力差异与 WebGL2 回退限制

这不是首批 Shader 材质电池的一部分。建议必须在 S1–S5 的性能、数据模型和导出可靠性稳定后单独立项。

---

## 13. 分阶段研发路线图与验收门槛

| 阶段 | 核心交付 | 验收作品 | 强制退出条件 |
|---|---|---|---|
| **S0** 架构冻结 | Shader IR Schema、类型签名、Patch op、版本策略 | 不需要画面 | 文档评审通过，旧协议回放不退化 |
| **S1** 材质 MVP | 12–14 只核心电池、TSL Unlit Material、IR Builder | 静态 Mesh 的 UV/Position 渐变 | 改 GH Shader 图能看到正确颜色；旧 Draw3D 正常 |
| **S2** Uniform 动态 | Uniform Registry、GH Slider Binding、Runtime Time Binding | 不使用 Timeline 的动态波纹颜色 | 变化期间 Shader 编译数不增加、Mesh 上传数不增加 |
| **S3** Vertex / Attribute | GPU 顶点变形、顶点属性、PBR 基础材质 | 曲率驱动色彩 + GPU 呼吸曲面 | 位移随时间平滑，错误法线/UV 可诊断 |
| **S4** Texture / Screen | Texture Sample、Screen Shader、Canvas as Texture | GH 线稿被 GPU 实时扭曲 | 2D 与 GPU 层可合成；静态纹理不重复上传 |
| **S5** Render Pipeline | Scene Pass、Bloom、Composite | 发光三维雕塑或抽象影像 | 导出与预览视觉一致（约定容差内） |
| **S6** Advanced Shader | Function/Subgraph、SDF、Raymarch（有限） | 原 Shadertoy 概念作品 | 可控循环、错误可定位、后端能力声明明确 |
| **S7** Compute（可选） | Buffer/Compute/Readback | 大规模并行粒子实验 | WebGPU 通过，fallback 行为明确 |

### 13.1 最小迭代建议

**版本 A（最优先，约等于 S0+S1）**：支持 `Mesh → Shader Position → Sin → Color → Unlit Material → Scene`，完成一个可编辑 Demo。

**版本 B（S2）**：增加 Uniform/Time，`Slider` 和 `Runtime Time` 改值时不重新编译，形成真正的 GPU 实时程序。

**版本 C（S3）**：曲率/法线/UV/顶点变形，实现 GH 几何与 Shader 的独特结合。

**版本 D（S4+S5）**：2D Drawing 与 Shader 合成，增加 Bloom、独立作品导出。

**版本 E（S6/S7）**：高级 Shader、SDF、Compute，不作为最初产品承诺。

不建议一次性从 S0 开发到 S7；每个阶段都要有可视觉验证的作品和自动化测试。

---

## 14. 测试计划（与 GHCE 已有 102 项断言体系衔接）

### 14.1 C# GH 插件层

- 新 Goo / Param 能实例化、序列化、往返与 Clone。
- 每只电池 GUID 唯一，端口名、类型、访问方式正确。
- 对表达式图的拓扑排序稳定，拒绝循环和悬空引用。
- 类型不匹配返回可解释诊断。
- GH Data Tree 改分支时，GraphID 与 ObjectID 不被错误替代。
- Uniform 的值变化不改变 Graph Hash。
- 常量变化遵循既定 Graph Hash 规则。
- 旧 GH 文件/旧 Patch 回放无损。

### 14.2 Browser + CDP 验收

- GH 编译的 IR 可以生成 TSL Material，无 JS Console 未捕获异常。
- 颜色渐变使用像素取样/图像差异测试（非空像素不足以证明正确）。
- Shader 结构变化后，材质编译次数符合预期。
- 调 Slider 连续修改 Uniform，Graph Build Count 不变，Mesh Upload Count 不变。
- WebGPU 与 `forceWebGL` 两套场景至少各跑基础 Shader 用例。
- Shader 错误时保留可见画面并返回可读诊断。
- 断线重连后 Shader 和 Uniform 状态正确恢复。
- 反复切换不同材质时无持续增长的资源泄漏。
- 2D Drawing 不连接任何 Shader 时仍正常运行。

### 14.3 性能监控（建议新增 Runtime Info 指标）

- `shaderGraphs` / `materials` / `textures`
- `shaderBuilds` / `shaderCompileFailures`
- `uniformUpdates` / `geometryUploads`
- `renderPassCount` / `renderTargets`
- `gpuBackend`（webgpu / webgl2）
- `frameCPUms` / `frameGPUms`（GPU 测时不总可用；需注明缺失）
- 浏览器内存及 GPU 资源数（以可观测指标为准）

**性能标准**：不要宣称跨设备固定 FPS；先指定测试机、GPU、浏览器/WebView2、画布尺寸、Mesh 数和节点规模。关键验证的是：**单纯 Uniform 变化不导致图重编译或重复几何上传**。

### 14.4 每阶段至少保留的 Demo / Golden Image

1. `tsl-01-position-gradient`：静态坐标渐变（S1）
2. `tsl-02-uniform-slider`：Slider 改颜色/频率（S2）
3. `tsl-03-time-wave`：运行时 Time 动态波纹（S2）
4. `tsl-04-normal-curvature`：法线/顶点属性上色（S3）
5. `tsl-05-vertex-displacement`：GPU 顶点变形（S3）
6. `tsl-06-canvas-texture`：2D Canvas 作为纹理（S4）
7. `tsl-07-bloom-export`：Bloom 预览与导出（S5）
8. `tsl-08-shadertoy-field`：有边界的隐式场/raymarch（S6）

---

## 15. 错误代码与调试可视化

建议错误分类：

| 错误码 | 阶段 | 处理方式 |
|---|---|---|
| `SHADER_TYPE_MISMATCH` | GH 静态检查 | 标注出错电池、输入端口与预期类型 |
| `SHADER_STAGE_INVALID` | GH 静态检查 | 告知节点不能用于当前 Stage |
| `SHADER_GRAPH_CYCLE` | GH 静态检查 | 显示循环路径及相关 GUID |
| `SHADER_UNIFORM_NOT_FOUND` | Runtime | 保留最后可用材质，返回 Uniform ID |
| `SHADER_ASSET_MISSING` | Runtime | 使用占位纹理并报警 |
| `SHADER_COMPILE_FAILED` | GPU/Three | 返回 Graph ID、后端日志和最近修改项 |
| `SHADER_BACKEND_UNSUPPORTED` | Runtime | 明确 WebGPU-only 或 fallback 限制 |
| `SHADER_RESOURCE_LIMIT` | Runtime | 拒绝超出设定规模的图/资源 |

建议在 GH 端的 `Shader Diagnostics` 输出：`IsValid`、`GraphId`、`GraphHash`、`Stage`、`Backend`、`NodeCount`、`CompileCount`、`Warnings`、`Errors`，并提供可定位到 GH 组件的错误源 ID。

---

## 16. 典型用户工作流（电池链路）

### 案例 A：最小材质渐变（不需要时间）

```text
GH Mesh → Mesh Input → Draw 3D ──────────────┐
                                            ▼
Shader Position → Split X → Multiply → Sin → Color Ramp
                                            │
                                            ▼
                                       TSL Material
                                            │
                                            ▼
                                      Apply Material
                                            │
                                            ▼
                              Scene 3D → Render Output
```

**验收意义**：GH 电池确实在编写 GPU Shader，而不是输出顶点颜色图片。

### 案例 B：不用 Timeline 的动态 Shader

```text
GH Mesh → Mesh Input → Draw 3D ──────────────────┐
                                                ▼
Shader Position.X ────────┐                 TSL Material
                          ▼                      ▲
Shader Time → Add → Sin → Remap → Shader Color ──┘
                                                │
                                   Apply Material → Scene 3D
```

浏览器只更新时间 Uniform；GH 不需要每帧重算，也不需要预生成 Morph 帧。

### 案例 C：GH 曲率决定视觉（GHCE 的差异化场景）

```text
GH Surface → Mesh + Curvature Sampling
                         │
                         ▼
                Attribute: curvature
                         │
             Shader Attribute(float)
                         │
                 Remap → Color Ramp
                         │
                     TSL Material
                         │
                     Scene 3D
```

使用 GH 擅长的几何计算，将曲率作为每顶点信息传给 GPU 做视觉解释。

### 案例 D：2D 绘图 + GPU 反馈（后续阶段）

```text
GH Curves → Mark/Brush → Canvas 2D
                            │
                    Canvas as Texture
                            │
                 Shader UV + Noise + Time
                            │
                       Screen Shader
                            │
                      Composite / Output
```

重点不在 3D Mesh，而是让 GPU Shader 变成 Drawing / Composition 的一种表达手段。

---

## 17. 后续研发任务卡（可直接交给 Claude Code）

### TASK-S0-01：建立 Shader IR 与类型系统

**范围**：

- 新增 ShaderExpr Goo / Param；保持 0.28.2 既有类型不变。
- 建立 Shader Node 白名单、数据结构、类型验证、循环检测和稳定序列化。
- 建立 Graph Hash / Uniform Hash 分离策略。
- 编写 C# 编译测试、序列化测试、GUID 检查。

**完成定义**：能够在 GH 测试层生成一个 `position.x → mul → sin → color` DAG，通过类型检查并稳定序列化；错误图能返回准确诊断。**此任务不要求 GPU 画面**。

### TASK-S1-01：实现 3D TSL Material 最小闭环

**范围**：

- 接入 `Shader Value / Position / Math / Color / TSL Material` 的最小组件集合。
- 新增 Runtime TSL Builder，按 Shader IR 在 `three/tsl` 中构建节点。
- 为 `Draw 3D` 提供向后兼容的材质绑定方式。
- 完成一个 Unlit 颜色渐变 Demo。

**完成定义**：运行时无需 `eval`，GH 接线变化改变 Shader 画面；断线重连可恢复；旧 3D/2D 功能测试全部继续通过。

### TASK-S2-01：Uniform 热更新与统一作品时钟

**范围**：

- 建立 Uniform Registry 与 `shader.uniform.set` Patch。
- GH Slider 改值只更新 Uniform。
- Runtime Time 与 GHCE 时钟共享，支持暂停/重启/导出时间。
- 新增 `shaderBuilds`、`uniformUpdates`、`geometryUploads` 诊断指标。

**完成定义**：在持续 10 秒的运行时间测试中，时间 Uniform 每帧更新，Shader 图只建立一次（不考虑首次后端缓存初始化）；GH 不触发高频 Solve；Mesh 不重复上传。

### TASK-S3-01：顶点位移与几何属性

**范围**：

- 引入 `Vertex Displacement`、`Shader Attribute`、`Curvature Attribute`。
- 与 GH Mesh 的 UV / Normal / 自定义 float 属性对接。
- 诊断缺失 Attribute、非法长度与坐标空间不匹配。

**完成定义**：生成曲率驱动颜色与随时间位移的 Mesh；输入几何未改变时，不重新上传 Mesh；法线和边界限制有文档说明。

### TASK-S4-01：2D Shader Canvas

**范围**：

- 实现 Screen Shader、Screen UV、Canvas as Texture。
- 与现有 2D Canvas 图层建立明确合成顺序。
- 处理纹理更新、颜色空间、Alpha 与 SVG 降级。

**完成定义**：同一 GH 曲线绘制结果可作为 GPU Shader 输入，生成动态变形作品；高频更新成本有明确性能数据。

### TASK-S5-01：RenderPipeline + Bloom

**范围**：

- 使用 `RenderPipeline / pass / bloom` 等受支持的 r186 API。
- 实现 ScenePass → Bloom → Output 的最小链。
- 输出链与 PNG/序列导出保持一致。

**完成定义**：Bloom Slider 更新不重建整个场景；预览和离线单帧在约定容差下可比对；关闭 Bloom 可回退到原 3D 输出。

---

## 18. 首轮开发指令模板（可复制）

> 你将基于 GHCE **0.28.2** 当前源码增量开发 TSL Shader 模块。请先阅读现有 PatchBuilder、Command 3D、Scene 3D、WebSocket 协议、Three.js r186 初始化与测试脚本，不允许先假设源代码目录结构。  
> 
> **第一轮只实现 TASK-S0-01 和 TASK-S1-01**：支持 `GH Mesh → Shader Position → Shader Math(Sin) → Shader Color → TSL Material → Apply Material → Scene 3D → Render Output`。  
> 
> 约束：保留所有现有 GUID 和旧端口；不修改既有 Draw3D 默认表现；Shader 输入必须使用类型安全的 IR；不执行任意 JS 字符串；不为每个电池开独立 socket；不以 GH Timer 触发每帧计算；不得默默更换 three.js r186 版本。  
> 
> 测试：旧 GHCE 探针与浏览器 102 项现有断言全部保持通过；新增 Shader IR 单元测试、Shader 编译失败回退测试、真实浏览器渐变像素测试、断线重连快照测试。  
> 
> 输出：变更文件清单、协议说明、组件/数据类型定义、测试日志、一个可打开的 GH Demo、浏览器预览截图和未完成/待决项。  
> 
> **请在第一轮完成后停止扩展功能，不要顺手实现 Bloom、Compute、复杂 Shadertoy 兼容或发布系统。**

---

## 19. 必须提前做出的关键决策（ADR）

| 决策 | 推荐取舍 | 原因 |
|---|---|---|
| IR 在哪一端生成 | C# GH 侧生成结构化 DAG，浏览器侧构建 TSL | 强类型验证、便于缓存和跨后端迁移 |
| 默认材质 | Unlit Node Material | 最小化灯光与 PBR 干扰，先验证 GPU 表达式 |
| 默认时间 | GHCE Artwork Clock 绑定 Uniform | 统一暂停、时间轴和逐帧导出 |
| Uniform Scope | S1/S2 默认 Shared，PerObject 后做 | 缩小缓存/实例差异问题 |
| Shader 输入边界 | GH Number、RuntimeFloat、ShaderFloat 显式桥接 | 防止把三种执行时机混为一谈 |
| 旧 Draw3D 兼容 | `Apply Material` 增量绑定，旧模式保留 | 最小侵入已完成主干 |
| 是否支持 Raw GLSL | 首版否 | 后端差异、安全性、诊断复杂 |
| 是否用 GH Timer 更新 Shader | 否（仅按需推新定义与 Uniform） | 避免阻塞、高频 Solve |
| 后处理是否同 Shader 材质一张图 | 否 | Pass 资源和材质节点生命周期不同 |
| Compute 是否首版做 | 否 | 与材质图完全不同的 GPU 资源模型 |

---

## 20. 外部依据与版本核验资料

> 以下链接是**技术选型和实现核验资料**。GHCE “已完成”事实仅依据用户 0.28.2 进度报告；设计建议为本任务书提出，并非 Three.js 官方对 GHCE 项目的指导。

1. **Three.js TSL 官方 API**：https://threejs.org/docs/pages/TSL.html  
   可核验 `uniform()`、`time`、`positionLocal`、`pass()`、`bloom()`、`texture()` 等接口。
2. **Three.js TSL Guide（官方，页面标示 r186）**：https://threejs.org/tsl/  
   TSL 的核心设计及节点范式。
3. **WebGPURenderer 官方说明**：https://threejs.org/manual/pages/webgpurenderer  
   `three/webgpu`、`three/tsl`、WebGPU → WebGL2 fallback，以及使用限制。
4. **Three.js NodeMaterial**：https://threejs.org/docs/pages/NodeMaterial.html  
   `colorNode`、`opacityNode`、`positionNode` 等材质插槽。
5. **Three.js MeshBasicNodeMaterial**：https://threejs.org/docs/pages/MeshBasicNodeMaterial.html  
   Unlit Node Material 的起点。
6. **Three.js RenderPipeline**：https://threejs.org/docs/pages/RenderPipeline.html  
   后处理链与生命周期；RenderPipeline 是 WebGPURenderer 路线的模块。
7. **Three.js FunctionNode**：https://threejs.org/docs/pages/FunctionNode.html  
   WGSL / GLSL 函数扩展及其后端限制。

**版本纪律**：当前项目以报告中的 **r186** 为实现基线。在线文档可能随后升级，若 API 示例与运行时不同，应以锁定 r186 的包源码和本地实际测试为准。升级 Three.js 应单独立项，先跑全部 Shader/非 Shader 回归测试，不能在 S1 同时升级。

---

## 21. 最终成功标准

GHCE Shader 模块成功不在于实现了多少 TSL 函数，也不在于 Shader 示例画面有多酷，而在于同时满足以下六点：

1. **可编程**：通过 GH 电池可自行组合 Shader 计算，而不只是选择预设效果。
2. **强类型**：GPU/Runtime/GH 值有清晰执行边界，错误可在正确层级被发现。
3. **增量**：修改 Uniform 不重建 Shader、不重新上传几何；修改拓扑才重新编译。
4. **可整合**：能使用 GH 几何属性，也能与 Drawing / Composition / Entity / Animation 协作。
5. **可发布**：实时预览、逐帧导出、独立作品包拥有一致可解释的画面。
6. **可维护**：旧 GHCE 定义不退化，编译失败可回退，资源与性能可观测。

**最终定位**：Shader 不是 GHCE 的终点，而是它的一种视觉表达语言。GH 负责定义结构、规则和艺术意图；Creative IR 负责可验证的编译；Three.js / TSL 负责 GPU 执行；Artwork Runtime 负责让它与绘图、行为、交互和作品输出共存。
