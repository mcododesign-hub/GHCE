# 对照《最终模块架构与开发路线图》：现状差距与下一步

> **基准差异**：那份路线图写于 **0.28.2**，本仓库现在是 **0.30.0**。它的模块划分和产品哲学我基本同意，
> 但有四处事实会让排期算错，先把它们校正，再谈下一步。

---

## 1. 四处要先校正

| 路线图说法 | 实际（0.30.0 源码） | 影响 |
|---|---|---|
| P0-A Runtime Expression「待做」 | **已完成**。`Runtime Var / Time / Math / Bind / Event Rule` 五只电池 + AST 求值 + 变量表，74 项运行时断言里 10 项专测它 | 排期里可以直接划掉，P0-B 可以立刻开始 |
| Entity 有六种预设行为 | **只有 `follow-curve`**。另外五种在 v0.15 就删了（`entity.js:73-76`） | P1-A「不再只能选六种」的起点其实更低 —— 目前只有一种 |
| Mask / Blend 未真正合成 | **Blend 已真实合成**（`renderer2d.js:207` `globalCompositeOperation`），只有 `Mask` 是空字段 | P1-B 的合成工作量减半 |
| 交互只有基础事件动作 | 事件已经能**写运行时变量**（`Event Rule`：set/toggle/add），缺的是事件源（只有 click/dblclick/key）与条件组合 | P2 的交互扩展不是从零起 |

> 另外：路线图第 10 行的「六种预设行为」和我们自己的进度报告写错过同一句（当时引的是协议文档而不是代码），
> 那份报告已在 0.30.0 一并修正。以后这类描述以 `runtime/src/` 为准。

---

## 2. 十个模块的现状矩阵

| 模块 | 已有 | 缺口 | 下一步 |
|---|---|---|---|
| **01 Geometry** | `Path Input` / `Project to 2D` / `Mesh Input` / `Point 3D` / `Curve 3D` | 采样、属性提取（曲率/切向）、Field | 跟 Mark 一起做（`Style Map` 需要曲率） |
| **02 Drawing** | `Draw Path` / `Draw Progress` / `Stroke Style` / `Fill Style` | **Mark / Brush 整个抽象** | **P0-B，主攻** |
| **03 Composition** | `Canvas` / `Layer`（blend 真合成） | Mask、Clear Policy 未暴露、字体、图像 | P1-B，暂缓 |
| **04 Logic** | **5 只电池已落地**（Var/Time/Math/Bind/Event Rule） | 只有 width/opacity 可绑；算子缺 remap/select/noise | **R1.5，先补完** |
| **05 Entity** | `Entity Blueprint` / `Entity Look`，确定性分布 + 沿曲线 | 状态读写、生命周期策略、行为图 | P1-A |
| **06 Animation** | `Timeline` / `Track` / `Transform` / `Morph` | 3D 未接时间轴（写死 1.5s） | R1.5 里先给 `Duration` 输入 |
| **07 Interaction** | `Interaction` / `Event Trigger` / `Event Rule` | 事件源只有 3 种；无条件、无路由 | R1.5 扩事件源 |
| **08 Render & Shader** | 3D 场景 / 相机 / 光照开关 / 5 种绘制模式 | TSL、后处理、`Light 3D`、粗糙度可调 | P1-C（沿用 `stage5-plan.md` 的 C 方案） |
| **09 Publish** | PNG / 透明 PNG / SVG / 序列 / 独立 HTML / 视频 | 作品包、确定性检查点、资源封装 | P2 |
| **10 Creative Core** | 增量 PatchBuilder / WS / 运行时 / 回放断言 | Creative IR 名义化、诊断聚合 | 边做边补，不单独开版本 |

---

## 3. 我建议的顺序（与路线图有一处不同）

路线图把 P0-A 和 P0-B 并列。P0-A 虽然落地了，但**没做完它自己写的验收**：

> 「一个圆不经 Timeline、Morph、GH Timer，实现**半径呼吸、颜色脉冲**」

现在只绑了描边宽度和不透明度 —— **颜色改不了，半径（实例尺寸）也改不了**。所以先花一个小版本把它收干净，再开 Mark：

| 步 | 内容 | 量级 | 验收 |
|---|---|---|---|
| **R1.5 ✅ 已交付（0.30.1）** | 绑定扩到 hue/scale（实例尺寸留到 Behavior 一起做）；算子补 gt/lt/select/noise + Remap 电池；事件源扩到 9 种；`Transform 3D` 加 `Duration` | S–M | 圆环半径呼吸 + 色相循环，`rev` 全程不变 —— **已进 82 项断言** |
| **P0-B ✅ 已交付（0.31.0）** | `Brush` / `Mark Along Path` / `Style Map`（`Draw Marks` 并入 Mark Along Path：输出就是 DrawCommand，再拆一只只是多一次端口跳转） | M–L | 同一组曲线五种静态画法，patch 体积有上限 —— **已进 89 项断言：404 标记 / 3 份几何 / 整帧 200 KB** |
| **P1-A ✅ 已交付（0.32.0）** | `Read State` / `Write State` / `Spawn` / `Destroy`（`Condition` 不单独做：比较用 `Runtime Math` 的 9/10，分支用 `Runtime Select`，条件本身就是一个 0/1 表达式） | L | 自定义弹簧粒子（非预设行为）—— **已进 10 项断言：72→168 人口阶梯、销毁留痕、确定性** |
| **P1-B ✅ 已交付（0.33.0）** | `Text`（GH 转曲线）、`Image` / `Draw Image`、`Clip`（指令级裁剪） | M | 含文字/图片/裁剪/笔触的海报 —— **已进 19 项断言：图片只进一次 patch / 圆裁 / 反相裁 / SVG clipPath** |
| **P1-C 📐 计划已校准（0.34 起）** | TSL 材质图 + 后处理 | L | Mesh → Shader → Bloom；任务书见根目录 `GHCE_TSL_Shader.md`，校准说明见 `docs/tsl-plan-calibration.md` |

**为什么不是先 Mark**：Mark 会引入新数据类型和一批新电池，而表达式层现在只有两个属性可绑 —— 先绑完再上 Mark，
Mark 的“样式映射”就能直接复用 Bind，不用为它再造一套驱动方式。这是顺序问题，不是取舍问题。

---

## 4. 下一步具体做什么（R1.5 三块）

### PR-1 绑定属性扩面（先做这个）

- `Bind` 的 `Property` 从 2 项扩到 4 项：`1 width / 2 opacity / 3 hue / 4 entity size`。
- `hue` 走 HSL：表达式输出 0–1 的色相，运行时合成颜色并保留原 alpha。比绑 RGB 三通道省两个端口，且“色相循环”是这类作品最常见的形式。
- `entity size` 要**另开一条路**：实例的属性不在 `cmd` 上，得让 `entity.upsert` 也接受 `bind`，由 `EntitySystem` 在绘制时求值。
- 表达式补三个算子：`remap(x, in0,in1,out0,out1)`、`select(cond, a, b)`、`noise(x)`（复用 `signal.js` 的 Perlin）。

### PR-2 事件源扩面

- `Event Rule` 的 `Event` 文本接受：`click` / `dblclick` / `key:a` / `pointerdown` / `pointerup` / `pointermove` / `enter` / `leave` / `wheel`。
- 运行时把 `pointer` 的现有事件接进 `EventTriggers.fire()`；`wheel` 带方向值，写进变量时用 `add`。
- 验收：按住让线变粗（down → add）、松开回落（up → set），全程 `rev` 不变。

### PR-3 三维时长参数化

- `Transform 3D` 加 `Duration`（默认 1.5，行为不变），`scene3d` 读它替掉 `scene3d.js:412` 的魔数。
- 这不是“接 Timeline”，只是把写死的常量变成参数 —— 半小时的改动，但演示时三维和二维能对上拍子。

---

## 5. 需要你定的两件事

1. **工具栏是否按路线图的 10 分类重排？** 现在是 7 个分类（Drawing / Animation / Entity / Random / 3D / Input / Output）。
   重排只改 SubCategory，GUID 不变、旧 `.gh` 照常打开，但用户找电池的位置会变。要做就趁电池还少。
2. **颜色绑定用色相还是 RGB？** 我建议色相（一个表达式、能做循环）；RGB 更自由但要三个表达式和三个端口。

---

## 6. 同意路线图「近期不优先」的部分

不再堆预设行为、不把 Rhino 视口当主舞台、不为 Shader 一次性造大量电池、不重复实现 GH 几何算法、
不把所有模式塞进 Timeline、不在 Creative IR 稳定前做 AI 逐节点连线 —— 这几条我都同意，也确实是当前容易走偏的地方。
