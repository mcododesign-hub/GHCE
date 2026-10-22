# v0.15 计划 · 从"预设"退回"工具"

## 0. 反思：我错在同一个地方，三次

你连着三轮说的其实是同一件事，我每轮只修了表面。

**第一次**：`Oscillator` 默认 ±15° 摆、`Timeline` 默认 reveal、`Keyframe` 默认 loop —— 我一拖出来你就能看到动静。
代价是那动静是我选的，不是你的数据。**我把"演示"当成了"功能"。**

**第二次**：`Event Trigger` 我自己加了 6 个动作预设、`Timeline` 加了 `Field` 下拉。我以为是"给你选项"，
其实是**用下拉列表代替模块设计**：一个 Mode 端口等于我替你决定了可能性的全集，而你连第 6 个是什么都不知道
（实测只有 follow-curve 有效，另外 5 个是摆设）。

**第三次**：`Blueprint` 给了 `Seed` + `Vary`。随机让画面"看起来有生命"，但它同时让你**无法精确控制** ——
你要的"特定规则"的粒子，被这层随机搅掉了。

**判断标准（从现在起我用它自查）**：
> 一个端口如果是在替你选择"要哪种效果"，它就不该存在。端口只该传"你的数据"。

按这条，`Mode` 下拉、`Field` 下拉、`Seed`、`Vary` 全部不合格。`Count`、`Speed`、`Lifetime`、`Matrix` 合格。

## 1. 明确划掉的东西（你点名的）

| 项 | 处理 |
|---|---|
| 噪声抖动 / 水波 / 磁铁吸附 / 圆变花瓣 / 沿法线膨胀（T2 具体算法） | 不做 |
| T3 交叉淡入 | 不做 |
| 场景 / 镜头切换 | 不做 |
| 阶段 4 高级绘图（Image Sample / Halftone） | 不做 |
| `Entity Behavior` 的 drift / orbit / attract / repel / follow-pointer | 删 |
| `Blueprint` 的 `Seed` / `Vary` | 删 |

文档里那套 T1/T2/T3 分层保留当**分类法**用（判断一个效果属于哪层、能不能插值），但**不再往里填具体算法**。

## 2. 实体层：3 只电池 → 2 只

### 现在的问题

```
Blueprint(Path, Count, Speed, Lifetime, Seed, Vary)   ← 生存规则里混了随机
   ↓
Behavior(BP, Mode=1..6, Amount)                      ← 6 个预设塞进一个下拉
   ↓
Look(BP, Colour, Size, Trail, Layer, Fade)           ← 只能改颜色/大小，形状固定是方块
```

三处冗余：`Mode` 是预设集合、`Seed/Vary` 是随机、`Look` 改不了粒子的形状。

### 改成

```
Blueprint(Path, Count, Speed, Lifetime, Phase)        ← 确定性：第 i 个粒子 u = i/Count + Phase
   ↓
Look(BP, Shape, Colour, Width, Size, Trail, Layer, Fade)   ← Shape = Rhino 里某一条曲线
```

- **`Behavior` 电池删除。** 蓝图第一个输入就是 `Path`，"沿哪条曲线跑"这件事已经在蓝图里了，
  再造一个电池只为换个模式，是多余的。只剩"沿路径"这一种语义，不需要端口。
- **确定性分布**：第 i 个粒子的归一化弧长位置 `u = (i / Count + Phase) % 1`，速度统一 = `Speed`，
  寿命统一 = `Lifetime`。没有随机、没有种子。`Phase` 接到时间上就是"整体往前挪"。
  - `Lifetime = 0` → 永不重生（粒子跑完一圈接着跑）
  - 要粒子各不相同，由你在 GH 里给不同 `Speed`（多蓝图），不是我掷骰子
- **`Look.Shape`**：接受 Rhino 里**单个**曲线对象（`Param_Curve`，`item` 访问 —— 多条会报错，
  因为粒子形状只能是一种）。取它的折线点、以 bbox 中心为原点、按最长边归一化，
  运行时把这个形状平移到每个粒子位置并按 `Size` 缩放。`Width` 是描边宽度。
  - 不给 `Shape` → 回退成原来的方块点（不强迫你准备曲线）

## 3. 时间的核心：Track

你要的三样是 `运行时间` / `是否循环` / `运动曲线`。这三样都不是"效果"，是**时间本身的参数**，
所以它们可以合法地做端口。

```
Cmds ─────┐
          ├─ Track ──→ Cmds
Frames ───┤      ↑ Duration / Loop / Ease
```

- `Frames`：**一串 Transform**，就是这条动画在时间上的采样。`Series(0..1, N)` → 你的数学 → `Rotate/Scale/Move`。
  浏览器在这 N 帧之间插值（用的是 v0.14 那套按不动点分解的插值，所以锚点不会漂）
- `Duration`：跑完一遍多少秒
- `Loop`：布尔，播完是否从头再来
- `Ease`：**一条 Rhino 曲线当缓动表**（横轴 0..1 时间，纵轴 0..1 进度）。不给你下拉，因为下拉里
  的曲线是我选的；给一条曲线，你能画任何形状 —— 这跟 `Look.Shape` 是同一个道理
- 没有 `Field`、没有 `Reveal/Rotate/Move` 三选一。帧序列长什么样，完全由你在 GH 里算

为什么这样能给 60 fps：N 帧由 GH 算一次（不需要每帧解算），播放和插值在浏览器里跑。
GH 的 `Timer` 只负责"定时逼重算一次"，它本身不产出时间值（我读了程序集确认）：
`GH_Timer` 是 `GH_ActiveObject`，字段只有 `_Interval` / `m_targets` / `_LockTargets`，**没有时间输出端口**。

### 曲线生长（Path 行为）

现有的 `Draw Progress`（`style.reveal`）就是沿弧长生长，**渲染层用 dash 截断实现**。
所以：描边能生长，填充不能。要"填充也一起生长"必须做几何截断 —— 那需要时间驱动，
跟 `Track` 同一批做，否则又是"电池里有动画但没有时间"。

## 4. 导出与交付（C）

| 目标 | 做法 |
|---|---|
| PNG 序列 | 运行时已有 `exportPNG`。加"按帧导出"：给定 `Duration` + `FPS`，逐帧渲染 + 下载 zip |
| 独立 HTML | 单文件：内联 runtime + patch JSON，双击就能播（不需要 GH、不需要 dev server） |
| MP4 / WebM | 浏览器 `MediaRecorder` 录 `canvas.captureStream()` → WebM 直接可用；MP4 走 `video/mp4;codecs=avc1`（Chrome 支持时） |

WebM 是稳的，MP4 取决于浏览器编码器 —— 会先用 `MediaRecorder.isTypeSupported()` 探，不行就只出 WebM 并说明。

## 5. 3D 升维（后续，不急）

现在整条链路是 2D 的：`Camera.Proj = [s, ox, oy]`，矩阵是 6 个数 `[a,b,c,d,e,f]`。
升维要动的是**核心表示**，不是加个 `Z` 参数：

- 矩阵 6 → 12 个数（3×4），插值走同一套分解（旋转用四元数，锚点仍然是不动点）
- 相机：`Proj` 变成一个 4×4 视图投影矩阵，`Path Input` 吃 3D 曲线而不是 Top 视窗投影
- 渲染器：`renderer2d.js` 加深度排序 / painter's algorithm，或者直接上 WebGL
- 指令语义不变（cmd 仍是一次绘图调用），所以上层 GH 定义不用重写

这一步不能顺手做，会把 v0.14 刚修好的插值全部推倒重来。

## 6. 分批

| 批次 | 内容 | 状态 |
|---|---|---|
| **v0.15.0** | 删 `Behavior`、`Blueprint` 去随机改确定性、`Look` 加 `Shape` + `Width` | ✅ 已交付（41/41、18/18、14/14、13/13、15/15，安装 `69305A8CD466`） |
| v0.16.0 | `Track`（Frames/Duration/Loop/Ease）+ 几何截断生长 | 下一批 |
| v0.17.0 | 导出：PNG 序列 / 独立 HTML / WebM-MP4 | |
| v0.18+ | 3D 升维 | |
