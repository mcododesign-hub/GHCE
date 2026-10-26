# 阶段拆解与验收清单

按任务书第三节拆分。每一项都是"能判定真假"的句子，不是"完成了 X"。

---

## 阶段 0 · GH → Browser 绘图闭环（P0）

| # | 交付 | 状态 | 怎么验 |
|---|---|---|---|
| 0.1 | GH 插件工程（`net7.0-windows` + 本机 .NET 7 程序集引用） | ✅ 编译 0 错误 / 加载冒烟通过 | `dotnet build`；`gh/probe` 输出 `RESULT: PASS` |
| 0.2 | 独立浏览器运行时（Canvas 2D） | ✅ | `node mock/verify.mjs` 14/14 |
| 0.3 | GH ↔ 浏览器通信（WebSocket + JSON） | ✅ 双端实现，协议回放 9/9 | `node mock/replay-csharp.mjs` |
| 0.4 | Point / Polyline / Curve → 统一路径数据 | ✅ | `Path Input` 采样 + 投影；协议 `polyline`/`polygon`/`path` |
| 0.5 | 画布刷新与数据更新 | ✅ | 增量 op 应用，整帧无变化不发包（C# 侧已验：第二帧返回 null） |
| 0.6 | 稳定对象 ID + 消息版本号 | ✅ | ID 由几何指纹生成；`rev` 单调、快照可越级 |
| 0.7 | 改几何后浏览器无需刷新 | ✅ | `mutate` 场景：19 帧连续 patch，页面不重载 |

**M0 判定**：`mock/verify.mjs` + `mock/replay-csharp.mjs` 全绿（已达成），**再加一次 GH 界面里的手动联调**（待你验收）。

### 阶段 0 已完成的可验收项（本机实测）

- 500 条路径单帧绘制 **1.6–5.5 ms**（1440×900 @ DPR 2）
- 单帧 patch 应用 **0.8–2.9 ms**
- 单帧 op 上限：`mutate` 场景 24 ops / 48 KB（只发变化的几何 + 1 条样式）
- SVG 导出保留 500 条 `<path>`，全矢量
- 四档视口（390/700/800/1400）无横向溢出、无裁切
- C# 插件：`dotnet build` **0 错误 0 警告**，4 个电池在真 .NET 7 + 真 Grasshopper 程序集下实例化成功
- C# 产出的 patch JSON 灌进运行时：**9/9**（字段名、编码、dash 数组、颜色全部对上）

### 唯一没验的

GH 界面里的端到端联调（GH 求解 → WS → 浏览器更新）。C# 已装到 `%APPDATA%\Grasshopper\Libraries\Colugo.gha`，等你开 GH 试一次。

---

## 阶段 1 · 二维绘图与视觉编排（P0）

| # | 任务 | 状态 | 备注 |
|---|---|---|---|
| A | 画布尺寸 / 背景 / DPR | ✅ | `Canvas` 电池 |
| A | 世界坐标 → 页面坐标投影 | ✅ v0.10 | 独立 `Project to 2D` 电池产出相机；`Path Input` 只采样 + 套相机 |
| A | 平移 / 旋转 / 缩放 | ✅ 运行时 | 协议 `transform`，尚无电池产出 |
| A | 图层与绘画顺序 | ✅ 运行时 | 三层排序键 `(layer.order, cmd.order, id)` |
| A | 裁剪 | ⬜ | |
| B | Stroke / Fill / 线宽 / 端点 / 连接 / 虚线 / 透明度 | ✅ | `Draw Path` 电池 + 运行时 |
| B | 点、线、Polyline、Bezier | ✅ 运行时 | `path` kind 支持 `M/L/C/Q/Z`；Bezier 尚无电池 |
| C | Draw Command 数据类型 | ✅ | 含 ID / 几何引用 / 样式 / 变换 / 图层 / 顺序 / 可见性 |
| D | Layer / Sequence / Mask / Blend / Group | ⚠️ 半 | v0.10 起 `Layer` 电池显式给 order/opacity/blend/visible；`mask` 未实现；`sequence` 由 `Draw Path` 的 Order 承担 |
| — | 500 条路径交互式编辑 | ✅ | `stress` 场景 |
| — | 同一 Draw Command 出 PNG + SVG | ✅ | 面板两个按钮 |
| — | 几何与绘图表达独立修改 | ✅ | `mutate` 场景：几何重传，样式单独 upsert |
| — | 笔画样式电池 | ✅ v0.10 | `Stroke Style` / `Fill Style` 做管道，`Draw Path` 端口不动；同一几何可分叉成两种画笔 |

**M1 判定**：Generative Plotter Art 场景（`static`）能在 500 条路径下保持 < 16.7 ms/帧，且 PNG/SVG 输出一致。

---

## 阶段 2 · 动态对象与状态（P0）

**运行时内核已完成，GH 电池未做。** 这一阶段选择了"先定协议、先跑运行时"，因为 Entity 的语义一旦确定，电池只是往图上搬参数。

| # | 任务 | 状态 | 证据 |
|---|---|---|---|
| A | Geometry / EntityDefinition / RuntimeInstance 三层分离 | ✅ | `runtime/src/entity.js`；Doc 只存静态表，实体归 `EntitySystem` |
| B | 生命周期 Create / Update / Preserve / Restart / Remove | ✅ | 定义指纹相同→Preserve，变化→Restart，count 变小→Remove（从尾部，不影响前面身份） |
| C | 稳定 Entity ID（不依赖数据树） | ✅ | 身份 = `(blueprintId, index)`；删除中间实例不会交换他人身份 |
| D | 状态变量 position / velocity / age / lifetime / visibility / custom | ⚠️ 部分 | 有 age / life / u / speed / size / trail；velocity 与自定义变量未做 |
| E | Behavior Graph（浏览器每帧执行，不依赖 GH） | ❌ v0.15 改判 | 6 种硬编码行为已删，只留 `follow-curve`。给下拉式的"行为图"仍然是预设，不做 |
| — | 1920 个实例的实时运行 | ✅ | 步进 0.40 ms / 帧，绘制 1.20 ms / 帧 |
| — | **GH 不发包时画面照走** | ✅ | rev 不变（1→1）而 age 4.53→5.78 |
| — | **改几何后实例状态保留** | ✅ | 几何漂移 rev 1→9，age 5.80→7.61，未被重置 |
| — | 固定步长 | ✅ | 1/60 累加，单帧最多追 8 步 |
| — | 多个 Entity 共享同一份几何资源 | ⚠️ | 协议允许（多个蓝图指向同一 `geom`），未做引用计数与释放 |
| — | 运行时状态读取 | ⚠️ | 只能 `Colugo.entities.snapshot(id)` 在控制台看，未回传 GH |
| — | GH 电池（`Entity Blueprint` / `Entity Look`） | ✅ v0.15 | Blueprint 5 入 / Look 8 入（含 `Shape` 单条曲线 + `Width`） |
| — | `State Variable` / `Behavior Graph` 电池 | ❌ v0.15 | 不做。行为只剩 `follow-curve`，蓝图第一个口就是 `Path` |

示范作品 **Curve Particle System** 已可跑：`mock` 的 `Entities` 场景 = 6 条闭环 × 320 粒子。

---

## 阶段 3 · 动画、绘画行为与交互（P0）

**阶段 3 于 v0.11.0 收尾：A–E 全部落地。v0.16 把时间这一层重做了一遍（见下面的 Track）。**

| # | 任务 | 状态 | 证据 |
|---|---|---|---|
| A | 独立动画时钟 Play / Pause / Reset / TimeScale | ✅ | `runtime/src/clock.js`；暂停后时钟与对象同时冻结（dt=0、dAge=0） |
| A | 浏览器动画独立于 GH 求解 | ✅ | 阶段 2 已验：rev 不变而 age 增长 |
| B | Timeline / Keyframe / Easing | ❌ 已删除（v0.13） | 这三只靠 `Field` 选"驱动哪个属性"，等于预设动画，盖住了用户在 GH 里的真实变化。运行时 `anim.upsert` 也一并删除 |
| B | Oscillator | ❌ 已删除（v0.13） | 默认 `Field=rotate / Amp=15`，拖出来就在摆 —— 正是"预设的微旋转"。`osc.js` 与 `OscDef` 已删除 |
| B | **Track（时间轨，v0.16）** | ✅ | `Track` 电池 6 入：`Commands` / `Frames`(N 个 Transform) / `Reveal`(N 个数) / `Ease`(一条曲线) / `Duration` / `Loop`。GH 算一次 N 帧，浏览器按 `clock.time` 插值 → 60fps 不依赖求解 |
| C | Stroke Reveal / Progressive Drawing | ✅ v0.16 改几何截断 | `reveal` 序列挂在轨道上；渲染按**弧长截断路径**，描边和填充一起长（虚线只能截描边） |
| C | Accumulation / Fade Previous Frame | ✅ | `canvas.clearMode = fade` + `fadeAlpha` |
| C | Selective Clear | ✅ v0.11 | `Event Trigger` 的 `clear-region` 动作 + `renderer.clearRegion()`；实测左上 alpha 0、右下保留 |
| D | Mouse / Keyboard | ✅ | `pointer.js` + **独立的 `Interaction` 电池**（0 入 5 出，画布坐标）；实测平均距离 832→284 |
| D | Event Trigger | ✅ v0.12 | `events.js` + `Event Trigger` 管道电池（5 入 1 出）：事件挂在**喂进来的那几条指令**上；click/dblclick/keydown × toggle/show/hide/restart（逐指令）+ clear/clear-region/reset-time/toggle-play（全局） |
| E | Entity / Drawing / Render 三类状态分离 | ✅ | 「重置时间」「重启对象」「清屏」是三个独立动作，HUD 上是三个按钮 |
| — | 多种行为并存 | ❌ v0.15 删除 | `drift`/`orbit`/`attract`/`repel`/`follow-pointer` 全部删除；改成"同一种语义、不同参数"的多蓝图并存 |
| — | 固定步长 + 确定性 | ✅ v0.15 | 1/60 累加；分布是 `u = i/Count + Phase`，没有随机数、没有种子 |

**v0.16 的 Track 是"时间的唯一入口"**：它只吸收 `Commands`（任意组合的指令），
序列由 GH 算，节奏由 `Duration` / `Loop` / `Ease` 定。**没有 Field 下拉、没有进度端口、没有波形预设。**
一条轨道可以只给帧（矩阵动画）、只给 reveal（生长）、或两者都给。

**v0.13 只留一条动画规则**：`GH 的数据怎么变，浏览器就怎么演过去`（`tween.js`）。
第一次出现从原始状态（identity）演到当前状态 —— 这就是"黑线变成绿线"的过程。
没有 Field、没有 progress 输入、没有预设。

**v0.12 把 PSR 的语义摆正**：`Transform` 去掉了 `Progress`，`Xform` 改成 list（每个指令一个）。
GH 只发终态矩阵，浏览器（`tween.js`）在 0.9 s 内从"当前画面位置"演到新位置 ——
变换本身就是那段动画，不需要任何人拧进度条。逐层模型见 `docs/motion-model.md`。

**v0.11 顺带修掉的 PSR 问题**：`Transform` 的 `Matrix` 是终态，以前只给终态就"卡住"。
现在指令上多一个 `transform.progress`（0 = 原位，1 = 终态），`Transform` 电池第 3 个输入直接给值，
或用 `Timeline` / `Oscillator` 把 `Field` 写成 `xform` 让它在浏览器里演出来。
插值用 TRS 分解（`xform.js`），不是六分量线性插值 —— 后者在 180° 会把中点塌成零缩放。
同时修掉 `PathConversion.FromCurve` 丢 `Proj` 的老 bug（走 `Curve → Path` 快捷方式时 `Transform` 会退化成不带锚点的分支）。

示范作品 **Living Lines** 已可跑：`mock` 的 `Living` 场景 = 8 条线逐步显现 + 两类粒子 + 残影累积。


---

## 阶段 4 · 高级绘图（暂缓）

Halftone / Image Sample / 纹理与笔触分层等。**当前判定：暂缓。**
理由：它是"效果库"，在时间与交付这两层没闭合之前加它，只会让工具更像一堆预设。
`docs/plan-v0.15.md` 里已经把这一批列为"不做"，等 3D 之后再回头评估。

---

## 阶段 5 · 导出与交付（v0.17）

| # | 任务 | 状态 | 证据 |
|---|---|---|---|
| A | PNG 单帧（含透明） | ✅ 阶段 1 | 面板 `PNG ×2` / `PNG 透明` |
| A | SVG 矢量 | ✅ 阶段 1 | 500 条 `<path>`，`svg.js` |
| A | **PNG 序列 + zip** | ✅ v0.17 | `exportPngSequence()`：按 `duration × fps` 逐帧离屏渲染，时间是自己给的（不跟墙钟赛跑），STORE 方式打包（`zip.js`），`frame_0000.png…` |
| A | **独立 HTML** | ✅ v0.17 | `exportStandalone()`：把依赖树递归抓下来、按拓扑序内联成 `data:` 模块 URL，文档状态压成一次 `doc.reset` 全量 op。**存成文件用 `file://` 打开即可播放**，不需要服务器 |
| A | **WebM / MP4** | ✅ v0.17 | `exportVideo()`：`canvas.captureStream(fps)` + `MediaRecorder`，按 1× 播一遍录制（MediaRecorder 的时间戳来自墙钟，硬塞帧会得到快进视频）。本机 `isTypeSupported` 命中 `video/webm;codecs=vp9` |
| B | 导出参数（fps / 时长） | ✅ v0.17 | HUD 两个数字输入；时长默认跟随最长轨道 |
| C | 交付到仓库 | ✅ v0.17 | `github.com/mcododesign-hub/Colugo` |

**验收**：`mock/verify.mjs` 49/49。其中三条是"真产物"级别的：
- PNG 序列 → zip 头 `504b0304`，3 帧 / 492 KB
- 独立 HTML → 塞进 iframe 后画布上真有墨迹（`ink=5291`）；单独用 `file://` 打开也是 `cmds=8` 且无 `__bootError`
- 视频 → `video/webm;codecs=vp9`，0.8 s / 592 KB

---

## 阶段 7 · AI / 双向回传（P2，已提前做了一小块）

| 任务 | 状态 |
|---|---|
| 浏览器状态回传 GH（`stat`，200ms） | ✅ `RuntimeStat` + `Interaction` / `Runtime Info` 电池 |
| 运行时诊断面板 | ⚠️ 只有帧率/对象数/耗时，没有结构化错误列表 |
| MCP Server / Schema / Graph Validator | ⬜ |

## 下一步（我建议的顺序）

1. **你在 GH 里跑一次端到端**：`Curve → Path Input → Draw Path → Track → Render Output`，拖控制点、改 `Frames` 看页面跟不跟。这是唯一还没在真 Rhino 里点过的一格。
2. ~~补 `Project to 2D` 与 `Layer` 两颗电池~~ → **v0.10.0 已做**（另加 `Stroke Style` / `Fill Style`）。
3. ~~阶段 2 的 Entity 系统~~ → **v0.15 收口**（行为只剩 `follow-curve`）。
4. ~~时间这一层~~ → **v0.16 收口**（`Track`：帧序列 + Duration/Loop/Ease）。
5. ~~导出与交付~~ → **v0.17 收口**（PNG 序列 / 独立 HTML / WebM）。
6. **3D 升维**：矩阵 6 → 12 分量、相机 4×4、渲染器深度排序。这一步会改协议，
   所以先把 v0.16/v0.17 的形态在真 Rhino 里跑一遍、确认要在 3D 里保留什么。
