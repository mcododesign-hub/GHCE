# P1-B：Composition / Graphic Media —— 下一步开发任务

> 基准：**0.32.1**（46 只电池 / 59 GUID / 137 项断言）。
> 路线图里 P0-A、P0-B、P1-A 都已交付，剩下的两件事是 **P1-B 平面表达** 和 **P1-C Shader**。
> 本文只排 P1-B；P1-C 等你的 shader 计划，理由见第 6 节。

---

## 1. 为什么先做 P1-B

一句话：**现在能做笔触，做不了版面。**

| 已有的 | 缺的 | 后果 |
|---|---|---|
| Mark 五种画法（0.31） | 文字、图片 | 只能"画线"，做不了海报 |
| Layer / blend 真合成 | Mask 仍是空字段 | 图片裁不进形状里 |
| 任意 slider 驱动几何 | 图像采样 | 照片无法变成点阵/排线 |
| PNG / SVG / 序列 / HTML | SVG 里的文字与图片 | 导出只对纯几何成立 |

验收原文是一张**含文字、图像、蒙版、笔触的完整海报**。这四样里我们只有"笔触"。

---

## 2. 三个决策（我的判断）

### 2.1 文字：在 GH 里转成曲线，运行时只认路径

- **做法**：`Text` 电池调 RhinoCommon 的 `TextEntity.Create` + `CreateCurves`，输出 `Curve` → 接现有 `Path Input`。
- **理由**：导出与字体彻底解耦。运行时不需要内嵌字体、不需要 `@font-face`、不需要担心用户机器缺字；SVG 里是真路径，PNG 与屏幕逐像素一致。
- **代价**：改字号 = 重新求解（跟 Mark 的 Spacing 一样，属于"定义变了"）。可接受 —— 文字在作品里本来就是版式决定，不是每帧变量。
- **不做**：运行时 `fillText`。那会引入"预览窗和导出用不同字体"的一整类问题。

### 2.2 图片：base64 进 patch，单张 4MB 上限

- **做法**：`geom.upsert` 增加 `kind:"image"`，`src` 是 data URL；GH 侧按 `Max Size` 降采样后再编码。
- **理由**：自包含。快照、重连、独立 HTML 导出、离线回放全部免费，不需要"预览窗能不能读本地文件"这种平台差异。
- **代价**：首帧变大（1MB 的图 ≈ 1.33MB base64）。图片有指纹缓存，**不随每帧重发**；改文字/改颜色不会带上图片。
- **上限**：单张 4MB data URL（约 3MB 原图），整帧 12MB 硬顶。超了就在电池上报警并降到 `Max Size`。

### 2.3 蒙版：先做「指令级裁剪」，图层蒙版后置

- **做法**：`Clip` 电池给一批指令挂 `clip = { geom, invert }`，运行时 `ctx.clip()`，SVG 用 `<clipPath>`。
- **理由**：海报里 90% 的"蒙版"就是"把这张图裁进这个形状"。指令级裁剪不需要离屏画布、不影响 blend、导出天然可表达。
- **后置**：图层蒙版（整层做 alpha mask）要离屏合成，成本是前者的三倍，而且 SVG 只能退化。等真有人要再做。

---

## 3. 0.33 任务清单（按顺序）

### T0 探针确认文本接口（0.5h）

`TextEntity.Create(text, plane, height, doubleStroke, font, bold, italic)` → `CreateCurves(...)` 的签名要在探针里先打出来。
不成立就退到 `Explode()`；两条都不行，就改成"输出 TextEntity 让用户自己 `Explode`"。

**验收**：探针打印 `TextEntity` 的静态/实例方法各一屏，选定一条写进注释。

### T1 `Text` 电池（0.5d）

| 端口 | 内容 |
|---|---|
| Text | 字符串，支持 `\n` 换行 |
| Font | 字体名，默认 `Arial`；找不到时报警并回退 |
| Size | 字高（页面单位） |
| Bold / Italic | 布尔 |
| Tolerance | 曲线采样容差，默认 0.3 |

输出 `Curves`（每行一串）+ `Info`（行数 / 字符数 / 曲线数）。
位置靠 GH 原生的 `Move` / `Orient` 处理 —— 不给它加 Plane 端口，否则就 7 个输入了。

**验收**：三行文字 → `Path Input` → `Draw Path` 出字形；改字号曲线跟着变；缺字体时报警且不崩。

### T2 图片几何（1d）

协议（`geom.upsert`）：

```json
{ "id":"g_img_9f2a", "kind":"image",
  "src":"data:image/png;base64,…", "w":960, "h":640 }
```

- `doc.js`：`normalizeGeom` 接受 `image`，校验前缀 `data:image/`、`w/h` 有限、单张 ≤ 4MB。
- `renderer2d.js`：按 id 缓存 `HTMLImageElement`；未解码时跳过该指令并登记一次重绘；`globalAlpha / transform` 与普通指令一致。
- `svg.js`：`<image href="data:…" width height>`，尺寸带 `preserveAspectRatio="none"`（缩放由矩阵负责）。
- `exporter.js`：PNG/序列导出前 `await` 所有图片 `decode()`，否则首帧会拍出空白。

**验收**：一张 1200×800 的图进画面，与 `geom` 的 bbox 对齐；PNG 与 SVG 里图片位置一致；同一张图不重复出现在两帧的 patch 里。

### T3 `Image` + `Draw Image` 电池（0.5d）

| 电池 | 输入（≤6） | 输出 |
|---|---|---|
| `Image` | File, Max Size | Image, Info |
| `Draw Image` | Image, Layer, Order, X, Y, Opacity | Commands |

`Image` 读文件、按 `Max Size` 等比降采样、编码成 data URL；同一路径+尺寸缓存，不重复读盘。
`Draw Image` 只负责放哪、多透明 —— 缩放归 `Clip` 之外的变换（接 `Transform` 也行）。

新数据类型 `GHCE Image`（第 14 个），归 `GHCE / Data`，带图标。

**验收**：`Image → Draw Image → Render Output` 出图；改 X/Y 图片跟着动；改 `Max Size` 后 patch 里的 `src` 长度跟着变。

### T4 `Clip` 电池（0.5d）

| 端口 | 内容 |
|---|---|
| Commands | 要裁的指令 |
| Clip Path | 裁剪轮廓（页面坐标，取第一条，闭合） |
| Mode | 1 保留内部 · 2 保留外部 |

- 运行时：在指令自身变换**之后**建裁剪路径 —— 它是"页面上的模具"，不跟着指令转。
- SVG：`<clipPath>` + 唯一 id；`Mode=2` 用"整块画布 + 轮廓"的 evenodd 组合。
- 一个指令只能有一个 clip；后接的 `Clip` 覆盖前一个（可预期，不叠加）。

**验收**：圆形裁剪一张图，图像只在圆内；`Mode=2` 时圆外有、圆内无；SVG 与 PNG 的裁剪边界误差 < 1px。

### T5 导出与自检（0.5d）

- PNG / 序列：导出前等图片解码；导出报告里注明"内嵌 N 张图，共 X MB"。
- SVG：clipPath 去重、id 稳定（同一几何两次导出 id 相同）。
- 独立 HTML：图片随 doc 一起序列化 —— 文件会变大，导出时给出体积提示。

**验收**：同一作品导出 PNG 与 SVG，四个角点与中心点的元素位置误差 < 1px（复用 replay 的口径）。

### T6 文档与版本（0.2d）

- `docs/protocol-v0.1.md`：`image` 几何、`clip` 字段、体积上限。
- `docs/minimal-paths.md`：文字海报、图片裁剪两条最小路径。
- `gh/build.md`、`docs/progress-report.md` 计数同步。
- 版本 0.33.0。

---

## 4. 0.34 候选（先不承诺）

| 项 | 判断 |
|---|---|
| `Image Sample`：图片 → 网格点阵 / 排线，亮度映射到 mark 大小 | 价值最高。它就是"照片变铜版画"，且完全复用 Mark |
| `Color Ramp`：0–1 → 调色板取色 | 中。现在只能绑色相，多色映射要它 |
| 图层蒙版 | 低。等指令级 `Clip` 用过一轮再说 |
| 文字排版（对齐 / 字距 / 行距） | 中。`Text` 加端口会超 6 个，得单独一只 `Text Layout` |

---

## 5. 风险

| 风险 | 影响 | 对策 |
|---|---|---|
| 图片让首帧 patch 过大 | 首帧卡顿、内存峰值 | 单张 4MB / 整帧 12MB 硬顶；`Max Size` 默认 1024；超限报警而不是静默 |
| 图片解码是异步的 | 首帧空白、导出的 PNG 缺图 | 未解码的指令当帧跳过并登记重绘；导出前 `await decode()` |
| 字体名在别人的机器上不存在 | 字形完全不同 | 电池上报警 + 回退到 `Arial`；文档写清"文字已转曲线，导出不依赖字体" |
| `Clip` 与 `transform` 的顺序搞反 | 裁剪框跟着图片转，看着像"裁歪了" | 明确"裁剪是页面坐标的模具"；验收里加一条旋转后再裁的断言 |
| SVG 内嵌图片过大 | 十几 MB 的 .svg | 导出报告给体积；超过 20MB 时提示改用 PNG |

---

## 6. 为什么 P1-C 先等

Shader 不是"再加几只电池"，而是**换一层执行模型**：GH 图要编译成 GPU 节点图，参数要分成"编译期常量"和"每帧 uniform"，还要处理 WebGPU / WebGL2 两套后端。

它的数据边界（哪些参数进 uniform、材质如何复用、TSL 节点表怎么映射到我们的电池）必须和你的 shader 计划对齐 —— 现在动手，大概率会先造一批用不上的电池，再推倒重来。

你说过会把 shader 计划发我；在那之前，P1-B 是独立、可验收、且能立刻用起来的一段。

---

## 7. 需要你拍板的两件事

1. **文字走"GH 转曲线"还是"运行时画字"？** 我选前者（导出稳、零字体依赖），代价是改字号要重新求解。
2. **图片用 base64 进 patch，还是走本地文件路径？** 我选前者（自包含、重连/导出免费），代价是首帧体积。
