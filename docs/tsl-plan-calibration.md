# TSL Shader 任务书 · 落地校准

> 校准对象：根目录 `Colugo_TSL_Shader.md`（V1.0，2026-10-26 编制）
> 校准基准：**Colugo 0.33.0**（50 只电池 / 14 个数据类型 / 63 GUID / 147 项断言）
> 校准日期：2026-10-26

---

## 0. 一句话结论

**任务书的架构判断我全部同意，但它的工作量估算偏高了一档——因为 Shader 的底座已经铺完了，而任务书不知道。**

任务书自报基准是 0.28.2，并据此推测"需要处理 WebGPU / WebGL2 两套后端"、"TSL 节点表怎么映射"这一整类前置工作。
实际情况是：`runtime/src/scene3d.js` 从建起就是 `WebGPURenderer`，五种绘制模式用的**本来就都是 NodeMaterial**，`three.tsl.js` 已经 vendor 在本地，`colorNode` 这个插槽已经在用：

```js
// runtime/src/scene3d.js:365
mat = new THREE.MeshBasicNodeMaterial();
mat.colorNode = tsl.color(c);      // ← 常量色
```

所以 S1 不是"把材质换成 TSL"，而是**把 `tsl.color(常量)` 换成一棵从 IR 编译出来的节点树**。这是插槽替换，不是架构迁移。

---

## 1. 四处必须先校正的事实

| 任务书说法（基于 0.28.2） | 实际（0.33.0 源码） | 对排期的影响 |
|---|---|---|
| 「31 只电池 / 12 数据类型 / 102 项断言」 | 50 / 14 / 147 | S1 的可复用面比它想的大 |
| 「TSL 需要 WebGPU 优先、WebGL2 回退」 | **已是 `WebGPURenderer`**（`scene3d.js:97`），three **r186** 已 vendor，`three/tsl` 已在 import | 这条前置工作**不存在**，可直接进 S1 |
| 「`Draw 3D` 是颜色/Glow/Opacity + 固定材质预设」 | 五种模式**全是 NodeMaterial**，Glow/Opacity 之外 `colorNode` 已接通 | 材质层零迁移，只换节点来源 |
| 「3D Transform 动画未接通统一时间」 | 0.30.1 已加 `Duration` 输入，替掉了 `scene3d.js` 的 1.5s 魔数 | S2 的统一时钟少一件事 |

另有两条任务书列为"缺口"、实际已闭环：

- **`Mask`** 仍是空字段 —— 但 0.33.0 已交付**指令级 `Clip`**（页面坐标模具 + evenodd 反相），海报场景里 90% 的"蒙版"已被覆盖。图层级 alpha mask 仍后置。
- **`blend`** 已真实合成（`renderer2d.js` 的 `globalCompositeOperation`），不是"仅传递协议属性"。

---

## 2. S1 电池清单：14 只压到 7 只

任务书 §4.1 建议 12–14 只。逐个对照现有 50 只之后，我认为**只需要新增 7 只**，其余要么已有等价物，要么该合并：

| 任务书建议 | 我的判断 | 理由 |
|---|---|---|
| `Shader Value` | **不做** | GH Slider 直连已经是「GH Number → Shader Constant」通路，加一层电池只是多一次端口跳转 |
| `Shader Uniform` | **要做** | 这是 S2 的全部价值所在，且必须带 `Scope`（Shared / PerObject）与唯一名校验 |
| `Shader Position` | **要做** | `positionLocal` → 节点，`Space` 枚举先只给 Local |
| `Shader Normal` | **要做** | `normalLocal`；缺法线时要给可读诊断而不是崩 |
| `Shader UV` | **要做** | 首版只 UV0，`Channel` 端口留着但只接受 0 |
| `Shader Time` | **要做（原判断已推翻）** | 我原本写"复用 `Runtime Time`"，实测是错的：它输出 `Colugo_Expr`（运行时 JS 求值的 AST），和 `ShaderExpr` 是两个类型，接上去 CastFrom 直接失败。表达式是每帧在 JS 里算，Shader 是每帧在 GPU 里算 —— 两个执行层，不能互换。0.34.1 已单独补 |
| `Shader Math` | **要做** | Op 枚举，与现有 `Runtime Math` 的算子名保持一致，避免"两个 Remap 长得不一样" |
| `Shader Vector` | **合并进 Math** | Split / Dot / Normalize 归到同一个 Op 枚举，省一只电池 |
| `Shader Mix` | **要做** | 插值是渐变与位移的基础，且它需要类型推导（float/vec3 混插） |
| `Shader Remap` | **合并进 Math** | 同 `Runtime Remap` 的语义，Op 枚举里一个值 |
| `Shader Color` | **要做** | 必须与 `Fill Style` 的 color 语义对齐（HSL 还是 RGB，见 §4 决策 2） |
| `TSL Material` | **要做** | 输出 `MaterialDefinition`；首版 Unlit，插槽只开 `colorNode` / `opacityNode` |
| `Apply Material` | **要做** | 最小侵入：`Draw 3D` 的命令挂一个 `material` 字段，旧定义不连它照原样跑 |
| `Shader Diagnostics` | **不做（扩展）** | 已有 `Runtime Info` 电池。诊断塞进它的第二输出，不再新开一只 |

净增：`Shader Uniform` / `Position` / `Normal` / `UV` / `Math` / `Mix` / `Color` / `TSL Material` / `Apply Material` = **9 只**。
（`Shader Value` / `Time` / `Vector` / `Remap` / `Diagnostics` 五只靠复用与合并消掉。）

50 → 59 只。比任务书的 12–14 只少三分之一，且没有一只重复现有语义。

---

## 3. 排期：P1-C 拆成两版，不要一版吞完

任务书把 S1+S2 合成"第一轮必须跑通的核心价值闭环"。我同意这个闭环定义，但**不同意一次交付**——S2 的验收条件是"改 Uniform 时 Shader 编译数不增加、Mesh 上传数不增加"，这是一条**计数器断言**，必须先把 S1 的 golden path 钉死，否则计数基线不稳。

| 版本 | 内容 | 验收（都要进 147 项体系） |
|---|---|---|
| **0.34 · S1** | Shader IR Schema（C# 生成 DAG）+ 运行时白名单编译器 + 上面 9 只电池 + 首个 golden image `tsl-01-position-gradient` | 静态 Mesh 的 Position→Sin→Color 出正确渐变；**旧 `Draw 3D` 不带 Material 时 17 项 3D 断言全绿** |
| **0.35 · S2** | Uniform Registry + Slider 绑定 + Artwork Clock 绑定 + `tsl-02` / `tsl-03` | 拖滑条改变色相/频率期间，`shaderBuilds` 与 `geometryUploads` **两个计数都不涨** |

S3 及以后（顶点变形 / 曲率属性 / 纹理 / Bloom）暂不排入版本号。理由：S1 的 IR 一旦定错，S3 会连带返工，而 IR 的正确性只能靠 S1 跑一轮真实作品才看得出来。

---

## 4. 需要拍板的三件事

**决策 1：IR 里要不要有「节点表 + 连接表」，还是直接嵌套 JSON？**

我选**扁平的节点表 + 连接表**（`{nodes:[{id,op,ins:{a:"n1"}}]}`），不选嵌套。理由：增量更新要按节点 id 做 diff，嵌套结构一旦内层变化就得整棵树重发；扁平表可以只发改动的节点。代价是 C# 侧要多写一次拓扑排序——但任务书 §14.1 本来就要求"拓扑排序稳定、拒绝循环"，这笔成本本来就逃不掉。

**决策 2：`Shader Color` 用 RGB 还是 HSL？**

我选**两者都收，但默认 HSL**。理由：现有 `Runtime Bind` 的 hue 绑定已经走 HSL（0–1 色相循环是这类作品最常见的驱动方式），如果 Shader 层用 RGB，同一个"颜色参数"在 2D 和 3D 会有两套语义，用户必然踩。RGB 保留为显式模式，给需要精确控制的场合。

**决策 3：TSL 编译失败时怎么办？**

我选**回退到当前的 `tsl.color(常量)` 材质 + 电池上报警**，不做"整帧不画"。理由：0.33.0 的 `Clip` 已经确立了这套原则——找不到裁剪几何时 `skipped++` 并跳过该指令，而不是让整帧空掉。Shader 应该同构：坏一个材质，坏一个物体，不是坏一个作品。

---

## 5. 与现有断言体系怎么接

147 项的分布是 verify 109 / verify-3d 17 / replay-csharp 15 / replay-3d 6。Shader 的断言要落在两个位置：

- **verify-3d**（17 → 约 26）：像素取样证明颜色真的是算出来的（不满足于"非空像素"）；`forceWebGL` 与 WebGPU 各跑一遍基础用例。
- **C# 探针**（现在 PASS / 50 电池 / 63 GUID）：IR 的拓扑排序、循环拒绝、类型不匹配诊断、`Uniform 改值不改 Graph Hash`。

**一条硬约束**：`replay-csharp` 现在验的是"两端字段名/编码/类型一致"。Shader IR 必须走同一条路——C# 产出的 IR JSON 要能直接灌进运行时，不能只在 mock 里手搓一份长得像的。

---

## 6. 为什么现在还不能开工

0.34 的前提是 **S0 架构冻结**（IR Schema + Patch op + 版本策略）。而 S0 里有一个决定没法在文档上拍——`Uniform Scope` 首版到底 Shared 还是 PerObject，取决于 0.33.0 的 `Entity` 系统能不能承载"每个实例一份 uniform"。

现有 `entity.js` 的实例属性已经能写 `x/y/u/age/gen/index`，但没有自定义 uniform 通道。所以 S0 的第一件实事是：**读一遍 `entity.js` 的实例属性存储，确认它能不能扩成 uniform 容器**。能，则 S1 顺手支持 PerObject；不能，则首版只能 Shared，且必须在文档里明确写"同材质的多物体共享一个 uniform"。

这件事读代码就能定，不需要写代码。建议作为 0.34 的第一个任务卡。
