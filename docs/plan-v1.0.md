# GHCE 任务编排 · v0.9.0 → v1.0

写于 2026-10-22 21:45 · 基线 **GHCE 0.9.0 / 13 只电池**
更新 2026-10-22 22:20 · **GHCE 0.10.0 / 17 只电池 · R1 的三项已落地，R0 待人工验收**
更新 2026-10-22 23:30 · **GHCE 0.11.0 / 19 只电池 · R3（阶段 3）已收尾，PSR 修好，仍待人工验收**
更新 2026-10-23 00:05 · **GHCE 0.12.0 · Transform 改为逐指令 Xform + 自动过渡；Event Trigger 改管道；预览补齐三处**

这份文档回答两个问题：**现在站在哪**、**接下来按什么顺序做**。
它不重复任务书（`Grasshopper Creative Coding Engine.md`）和阶段清单（`docs/stage-plan.md`），
只补那两份文档里没有的：**排序理由、依赖关系、每一步的验收句、以及会卡住人的工程护栏。**

---

## 0. 本机基线（实测，2026-10-22）

| 项 | 值 | 影响 |
|---|---|---|
| Rhino | 8.15.25013.13001 @ `D:\Program Files\Rhino 8` | 与插件编译时用的版本**完全一致**，程序集绑定无风险 |
| .NET 7 运行时 | 7.0.0 @ `C:\Program Files\dotnet` | 够跑插件（Rhino 自带 netcore 运行时） |
| .NET SDK | **10.0.402 @ `C:\Program Files\dotnet`**（2026-10-22 22:05 装上） | 可编译。`dotnet build` 直接可用，不再需要全路径 |
| WebView2 Runtime | 155.0.4283.62 | 预览窗能用 |
| Node | v22.22.2 | 够跑 `mock/`，但 `node_modules` 未装（`npm install` 一次） |
| 8777–8788 | 全空闲 | 插件首次启动会占 8777 |
| 已装插件 | `%APPDATA%\Grasshopper\Libraries\GHCE.gha` · **166912 bytes · md5 `fee812170ed8…` · v0.10.0** | 与源产物一致（脚本已校验） |

**能跑的三条链（不需要编译）**

```
静态：Curve ─► Path Input ─► Render Output
精修：Curve ─► Path Input ─► Draw Path ─► Render Output
动态：Curve ─► Entity Blueprint ─► Entity Look ─► Render Output.Entities
```

---

## 1. 现在最该做的一件事

阶段 0–3 的 P0 项在协议层、运行时层、C# 单点层都已闭环，但**没有一个端到端作品**：

> 全部验证都是「运行时 + 协议 + C# 单点」，从来没有一次「GH 求解 → WS → 页面真的更新」在人眼下发生。

这是**唯一的 M0 空格**，也是所有后续阶段的信任基础。所以 R0 不是一个开发任务，是一次**验收**。
在它绿之前不动任何 C# 代码 —— 否则出问题时无法区分「新代码坏了」和「原本就没通」。

---

## 2. 任务编排

排序原则：**先把已有的东西验成可信，再补它在真实使用中暴露的缺口，最后才开新阶段。**
每一轮都要求「打开 Rhino 能看见」作为收尾，不接受只在 mock 里绿。

### R0 · 端到端联调（半天，零代码）

| 项 | 内容 |
|---|---|
| 目标 | 证明 GH 求解能驱动浏览器，且几何改动不重载页面 |
| 交付 | 一份 `.gh` 定义文件存入 `examples/`（目前仓库里没有可复用的定义，这是缺口） |
| 验收 | ① GHCE 标签出现 13 只电池 ② 预览窗自动弹出、HUD `open` ③ 拖控制点时图形变且 `document` 不重新加载 ④ `Message` 从 `rev 1` 单调递增 |
| 依赖 | 无 |

产出物建议：`examples/static-plotter.gh`、`examples/entity-flow.gh`、`examples/living-lines.gh`。
有了这三个文件，「作品」才第一次存在，也才能做回归。

### R1 · 补齐阶段 1 的两个 ⚠️（1–2 天）

**状态：1.1 / 1.2 / 1.3 已在 v0.10.0 落地。** 剩下的见文末「R1 实际交付」。

| 序号 | 任务 | 为什么排在这里 |
|---|---|---|
| 1.1 | `Project to 2D` 独立电池（把硬编码在 `Path Input` 里的投影抽出来） | 现在相机锁在 `Path Input` 里一次框定，导致「绕自身中心转很稳、转 90° 出框被裁」这个边界无法从图上解决。抽出后可以喂显式 `Camera` 输入，`Margin` 不再是唯一旋钮 |
| 1.2 | `Layer` 电池（order / opacity / blend 显式化） | 现在 order = 首次出现序号 × 10，是隐式推导。做多图层海报时「谁在上面」不可控，违反任务书「绘画顺序必须明确」 |
| 1.3 | `Stroke Style` / `Fill Style` 电池 | `Draw Path` 有 10 个口且**按你的要求保持原样**。样式必须另开电池才不继续加端口 |

验收句：同一份 Draw Command 能在两个不同 `Layer` 下渲染出可复现的上下关系；改 `Project to 2D` 的相机参数时，细长条转 90° 不出框。

### R2 · ~~让 Behavior Graph 真的成图~~ · ❌ v0.15 判定不做

原计划把 `Entity Behavior.Mode`（1–6 的序号）换成可组合的 `Force → Accel → Velocity → Position` 图。
**v0.15 把这个模块整个删了。** 理由不是"做不出来"，是**方向不对**：

- 图仍然是我预设好一组节点让你连，你还是在我的可能性里挑；实测六个模式只有一个有意义。
- 真正缺的不是"更多行为"，而是**时间**和**形状**：`Track`（时长/循环/缓动曲线）和 `Look.Shape`（单条曲线）。
- 唯一保留下来的动作只剩 `2.3 几何资源引用计数`（长跑内存问题，与行为无关，留待后做）。

验收句（作废）：~~同一份场景把行为换成电池拼的 drift + attract 组合~~。

### R2' · 时间与交付（替代 R2）

| 序号 | 任务 | 说明 |
|---|---|---|
| 2.1 | `Track` 电池 | `Cmds` + `Frames`(一串 Transform) + `Duration` + `Loop` + `Ease`(一条曲线)；浏览器插值播放 |
| 2.2 | 几何截断生长 | `Draw Progress` 的 reveal 是 dash 截断，只能描边生长；填充也要生长就得在几何层截断 |
| 2.3 | 导出 | PNG 序列 / 独立 HTML / WebM·MP4（`MediaRecorder` + `canvas.captureStream()`） |
| 2.4 | 几何资源引用计数 | 多个蓝图共享一份 `geom` 时，删除实例要真的释放，不然长跑会涨内存 |

### R3 · 阶段 3 收尾（1–2 天）· ✅ v0.11.0 已交付

`Oscillator` / `Event Trigger` / `Selective Clear` 三样都落地了。
`Event Trigger` 没有绑死单一行为 —— 它只做五个通用动作（clear / clear-region / restart-time /
restart-objects / toggle-play），对实体系统一无所知，所以 R2 的行为怎么改都不会波及它。

验收句：`Selective Clear` 清掉 `particles` 层后，`main` 层的墨迹仍在；`Oscillator` 的相位能作为 `Timeline` 的输入源。
→ 前半句已验（`clear-region` 后左上 alpha=0、右下 255）；后半句**没做**，而且不建议做：
两者都是"每帧写同一个字段"，串起来只会互相覆盖。真正的组合方式是并行两条链，各自控制不同字段。

顺带把 PSR 的两个根因修了（`transform.progress` + `PathConversion` 丢 `Proj`），见 `gh/build.md` §v0.11.0。

### R4 · 阶段 4 高级绘图（P1，1 周）

画笔（压力/宽度轮廓/散点/纹理）→ 字体 → 图像（halftone / dither / displace）→ 版面。
**建议先做 `Image Sample` + `Halftone`**：它们复用 GH 现有几何算法，且能立刻出一张「几何驱动网点」的作品 —— 性价比最高的一格。

### R5 · 阶段 5 三维 / TSL（P1，独立分支）

任务书明确要求「不影响已建立的二维架构」。所以**另起 `runtime/src/scene3d/`，不改 `renderer2d.js`**，
`Render Output` 加一个后端选择（`2d` / `three`），协议层加 `Scene IR` 子模型。

### R6 · 导出与独立网页（P1）

PNG / SVG 已有。缺：PNG 序列、指定帧率与时间区间的确定性逐帧导出、打包成无 Rhino 可播的 HTML。
固定步长在 `clock.js` 里已经是 1/60 累加，确定性导出的地基有了。

### R7 · MCP / AI（P2，架构稳定后）

先把 `Artwork Definition` 抽成 schema，再谈 MCP。顺序不可颠倒。

### 优先级一览

```
R0  ──► R1  ──► R2  ──► R3  ─┬─► R4 ──┐
                              └─► R5 ──┴─► R6 ──► R7
```

**R0 是硬门禁。** R4 与 R5 可并行（互不碰同一批文件）。

---

## 3. 每个阶段的收尾定义

不满足以下四条就不算完成：

1. `%APPDATA%\Grasshopper\Libraries\GHCE.gha` 的 md5 与 `bin/Release` 产物一致（`install.ps1` 会打）。
2. 在 Rhino 里用一个 `examples/*.gh` 真的跑过一次，有截图进 `docs/shots/`。
3. `mock/verify.mjs` 全绿（R1 之后会从 38 项继续长）。
4. `docs/stage-plan.md` 里对应行的 ✅/⚠️/⬜ 被改过 —— 文档不同步等于没做。

---

## 4. 工程护栏（不写下来一定会再踩）

### 4.1 没有 .NET SDK

改 C# 之前必须先有 SDK：

```powershell
winget install Microsoft.DotNet.SDK.8      # 或 SDK.7，Rhino 8 目标 net7.0-windows
dotnet --list-sdks                          # 有输出才算装好
```

目标框架是 `net7.0-windows`，用 SDK 8 编 net7 目标需要运行时 7（已有）+ targeting pack（SDK 会自动拉）。
**只装运行时的那份 `C:\Program Files\dotnet` 不能编译** —— 这是 `build.md` 里记过的第一个坑。

### 4.2 脚本必须带 UTF-8 BOM

`install.ps1` 原本是无 BOM 的 UTF-8，Windows PowerShell 5.1 按 ANSI 读，中文变乱码，
`Write-Host` 的字符串缺终止符 → **解析直接失败，一行都跑不了**。已补 BOM。

**规矩：`gh/` 下所有 `.ps1` 一律存成 UTF-8 with BOM。** 新建脚本时注意，别顺手存成无 BOM。

### 4.3 新增第三方引用必须配加载期解析钩子

GH 加载 `.gha` 会遍历全部类型。任何类型的字段引用了找不到的程序集 → 整个库加载失败 →
**表现是「GHCE 标签页根本没出现」，而不是某个电池报红**。
`AssemblyInit.cs` 里的 `[ModuleInitializer]` + `AssemblyResolve` 是为此存在的，新增引用（图像库、MathNet…）必须在这里补映射。

### 4.4 改 `runtime/` 之后必须重编

运行时页面是**嵌入资源**。改 `runtime/index.html` 或 `runtime/src/*.js` 后，只重启 Rhino 无效，
必须 `dotnet build` → `install.ps1`。判断方法：`GHCE.gha` 的字节数和 md5 变了才是真的新版本。

### 4.5 更新插件必须关 Rhino

GH 锁住已加载的 `.gha`，`cp` 会静默失败。`install.ps1` 检测到 Rhino 在跑会停下并说明（`-Force` 才强杀）。

### 4.6 端口会漂移

8777 被占时自动落到 8778–8788。页面里的 WS 地址默认写死 8777，
所以**一切地址都取 `BridgeServer.PreviewUrl`**（`Render Output` 的 `Url` 输出 / 右键菜单 / 预览开关三处已经统一）。
自己拼地址就会出现「连接 open 但画面不动」的假象。

### 4.7 `Draw Path` 的 10 个端口不改

你明确要求保持原样。样式扩展走新电池（§R1 1.3），不许拆它。

---

## 5. 今天的手动验证清单

Rhino 当前未运行，安装已在此状态下完成。按顺序做，每步都有「不符合时看哪里」。

| # | 操作 | 期望 | 不符合时 |
|---|---|---|---|
| 1 | 启动 `D:\Program Files\Rhino 8\System\Rhino.exe`，命令行输入 `Grasshopper` | GH 打开 | — |
| 2 | 左侧标签栏找 `GHCE` | 出现，5 组 **17 只**电池（Output 2 / Drawing 8 / Animation 2 / Entity 3 / Input 2） | 标签不出现 → Rhino 命令栏搜 `GHCE` 有没有加载异常；确认 `%APPDATA%\Grasshopper\Libraries\GHCE.gha` 是 166912 字节（§4.3） |
| 3 | 拉一条 `Curve`，接 `Path Input` → `Render Output`，`Preview` 保持 true | 弹出一个窗口，里面是深色画布 + 左上 HUD | 窗口不弹 → 电池 Warning 会写原因；WebView2 起不来会降级成系统浏览器 |
| 4 | 看 HUD 的「连接」和 `Render Output` 的 `Message` | `open`；`1 client · rev 1: N ops, X ms` | 一直 `0 client` / HUD `retry` → 端口没对上，右键 `Render Output` 第一行看实际端口（§4.6） |
| 5 | 在 Rhino 里拖曲线控制点 | 窗口里图形实时跟变，页面**不刷新**（HUD 的 `rev` 涨，不闪白） | 涨了但画面不动 → 相机把几何框到画外了，调 `Margin` 或 `Page W/H` |
| 6 | 加一条曲线进同一个 `Path Input` | 旧图形不动，新图形出现（老对象身份不变） | 全部重画 → 指纹算法回归，属 bug |
| 7 | 右键 `Render Output` → `图标自检…` | 四张放大 8 倍的图标在图片查看器里打开 | 空白 → `Icons.cs` 的 GDI+ 路径出问题 |
| 8 | 照第 3 步另搭动态链：`Curve` → `Entity Blueprint` → `Entity Look` → `Render Output.Entities` | 粒子沿曲线跑；HUD 对象数 = `Count` | 不动 → 看 HUD 帧率是否为 0（时钟被暂停） |
| 9 | 按空格 / `0` | 空格暂停播放，`0` 重置时间 | 无响应 → 焦点在 GH 面板上而非预览窗，先点一下预览窗 |
| 10 | 拖 `Entity Blueprint` 的 `Count` 从 200 到 50 | 实例从尾部减少，剩下的**身份与年龄不变**（不重新出生） | 全部重生 → 生命周期策略回归 |

**第 5、10 步是这次真正要验的两条语义**：几何改动不重载、实例身份不交换。
其余步骤都是在确认「环境对不对」。

截图保存到 `docs/shots/r0-e2e-*.png`，作为 M0 的封口证据。

---

## 6. 决策记录

| 问题 | 结论（2026-10-22） |
|---|---|
| 要不要装 .NET SDK | **装。** SDK 10.0.402 已装到 `C:\Program Files\dotnet`，`dotnet build` 直接可用 |
| R0 之后先走 R1 还是 R4 | **R1（补地基）。** 已交付 v0.10.0 |
| `examples/*.gh` 谁搭 | **用户在 Rhino 里自己搭。** 所以下文给的是「接线规格 + 期望现象」，不生成 `.gh` 文件 |
| 第 4 节 §4.1「没有 SDK」那条护栏 | 已失效（SDK 装好了），保留作历史记录 |

---

## 7. R1 实际交付（v0.10.0）

**新增四只电池**：`Project to 2D`（6 入）、`Layer`（6 入）、`Stroke Style`（6 入）、`Fill Style`（4 入）。
新的数据类型 `GHCE Camera`。共 **17 只电池 / 6 个参数类型**。

**破坏性变更**：`Path Input` 从 6 口变 4 口（`Geo / Tol / Camera / Id`），W/H/Margin 搬进相机。
不接相机时行为与 v0.9 一致，所以最短链不用改线，但**接在 W/H/Margin 上的线会掉**。

**为 R1 建立的三条护栏**（都已进代码，不是口头约定）：
1. `gh/probe` 断言 GUID 唯一性 —— 这轮手写 GUID 真撞了 4 个
2. `Types/CameraMath.cs` 只用 double，相机数学不开 Rhino 也能验
3. 探针与 `replay-csharp.mjs` 不再写死任何用户目录路径

**验证**：build 0/0 · probe 对构建产物与安装产物均 PASS · 相机 5 条断言 · verify 38/38 · replay 14/14。

**R1 里没做的**：`mask`（协议留位）、`Sequence` 独立电池（现由 `Draw Path` 的 Order 承担）、
`Entity Look` 的图层顺序（只有 Layer 文本口，order 仍按推导）。

### 下一步给用户的接线规格（R0 封口 + R1 验收）

```
① 最短链（确认环境）
Curve ─► Path Input ─► Render Output

② 固定相机（验 R1 的相机语义 —— 细长条转 90° 不该被裁）
Curves(整段动画全部曲线) ─► Project to 2D ─Camera─► Path Input ─► Draw Path ─► Render Output
                              ▲ W/H/Margin

③ 图层顺序（验 R1 的 Layer）
Draw Path ─► Layer(Order = 0, Blend = source-over) ─┐
Draw Path ─► Layer(Order = 10, Blend = multiply) ──┴─► Render Output
期望：Order 小的在下；blend 在 HUD 上看不出，但 multiply 会让重叠处变暗

④ 样式管道（验 R1 的 Stroke/Fill）
Path Input ─┬─► Draw Path ─► Stroke Style(Colour 红, Width 3) ─► Render Output
            └─► Draw Path ─► Stroke Style(Colour 白, Width 1, Dash "4,3") ─► Render Output
期望：同一份几何出现两种画笔，rev 只涨样式相关的 op，几何不再重传
```

第 ④ 条是这轮最值得看的一条：它同时证明了「几何与画笔分离」和「一个几何多种解释」。

