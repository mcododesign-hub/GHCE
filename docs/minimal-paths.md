# 最小连接路径

每条都只列**必需**的电池。省略的端口用默认值即可。
共同终点只有一个：`Render Output` 的四个输入 —— `Canvas` / `Commands` / `Entities` / `Scene`。

---

## 0. 端口速查

| 电池 | 输入（顺序） | 输出 |
|---|---|---|
| `Brush` | Curve, Shape, Size, Aspect, Angle | Brush |
| `Mark Along Path` | Paths, Brush, Spacing, Rotate, Angle, Layer | Commands |
| `Style Map` | Commands, Property, In, Out, Bias | Commands |
| `Runtime Var` | Name, Initial | Expression |
| `Runtime Time` | Scale, Offset | Expression |
| `Runtime DT` | — | Expression |
| `Runtime Math` | A, B, Op | Expression |
| `Runtime Remap` | X, In, Out | Expression |
| `Runtime Select` | Condition, A, B | Expression |
| `Bind` | Commands, Property, Expression | Commands |
| `Event Rule` | Commands, Event, Variable, Write, Value, Enable | Commands |
| `Read State` | Slot, Name | Expression |
| `Write State` | Entities, Slot, Expression, When, Op | Entities |
| `Spawn` | Entities, When, Count, Spread, Max Gen, Max Pop | Entities |
| `Destroy` | Entities, When, Keep | Entities |

序号表：`Runtime Math` 1 add · 2 sub · 3 mul · 4 div · 5 min · 6 max · 7 sin(A) · 8 clamp01(A) · 9 A>B · 10 A<B · 11 noise(A)。
`Bind` 1 width · 2 opacity · 3 hue · 4 scale。`Style Map` 同上（1 size · 2 rotation · 3 opacity · 4 width）。
状态槽位：1 x · 2 y · 3 vx · 4 vy · 5 u · 6 age · 7 index · 8 size · 9 alpha · 10 aux · 11 gen。

> 所有 `Expression` 端口接数字 = 常量。不必为了一个 `0` 摆一只电池。

---

## 1. Mark：同一个几何的五种画法

### 1.1 最小（点描）

```
Circle ──► Path Input ──► Mark Along Path ──► Render Output.Commands
                              ▲
                    Brush (Shape=1, Size=6)
```

`Spacing=10`、`Rotate=1`（跟随切向）。24 个点的圆 → 一圈点。

### 1.2 沿路径由小到大

```
… Mark Along Path ──► Style Map ──► Stroke Style ──► Render Output
                        Property=1
                        In=0..1
                        Out=0.4..2.2
```

`Style Map` 是**乘法**：`Out=1..1` 表示不改。要"末端大 3 倍"就写 `1..3`。

### 1.3 书法笔锋

```
Brush (Shape=2, Size=14, Aspect=6) ──► Mark Along Path (Rotate=3, Angle=45)
                                            ──► Style Map (Property=4, Out=1..3)
                                            ──► Render Output
```

`Rotate=3` 是固定角；`2` 是垂直路径（排线），`4` 是确定性随机。

### 1.4 自定义笔形

```
任意小曲线 ──► Brush.Curve        （Shape/Size 只决定归一化后的尺寸）
```

笔形会被**居中、按最长边缩到 Size**，所以画多大都行。

> 闭合笔形（点 / 方块 / 三角）**自带填充**。要空心就在后面接 `Fill Style` 把颜色改回背景色。

---

## 2. Runtime：不重解 GH 的交互

### 2.1 点击让线变粗

```
Curve ─► Path Input ─► Draw Path ─► Bind ─► Event Rule ─► Render Output
                                     ▲          ▲
              Runtime Var ("v_w") ───┘          │
                    │                           │
                    └─► Runtime Math (3 mul, B=6) ─► Runtime Math (1 add, B=1.5)
                                        ↑
                                 Bind.Expression

Event Rule: Event=click, Variable=Runtime Var, Write=2 (toggle)
```

`Bind` 会**整个替换**宽度，所以表达式里必须自己带上基准值 1.5。

### 2.2 呼吸线宽（不用 Track，不用 Timer）

```
Runtime Time (Scale=2) ─► Runtime Math (7 sin) ─► Runtime Remap (In=-1..1, Out=1..3) ─► Bind (1 width)
```

`Remap` 一步就把 `-1..1` 变成 `1..3`，比"乘 0.5 再加 2"少一只电池。

### 2.3 色相循环

```
Runtime Time (Scale=0.9) ─► Math (7 sin) ─► Remap (In=-1..1, Out=0..1) ─► Bind (3 hue)
```

只换色相，保留底色的饱和度与明度；底色是灰/白时自动补饱和度。

### 2.4 按住放大、松开复位

```
Draw Path ─► Bind (4 scale) ─► Event Rule ─► Event Rule ─► Render Output
                 ▲                ▲              ▲
   Runtime Var ─► Math (3 mul, B=3) ─► Math (1 add, B=1)
   ("v_hold")
   规则 1: Event=pointerdown, Write=3 (add), Value=1
   规则 2: Event=pointerup,   Write=1 (set), Value=0
```

两条规则挂在同一条指令上，互不覆盖。

---

## 3. Behavior：没有预设，只有赋值

### 3.1 最小：停在原地

```
Curve ─► Path Input ─► Entity Blueprint (Count=24, Speed=0, Life=0) ─► Entity Look ─► Render Output.Entities
```

`speed=0` 就是"停留"——不需要任何 Write。加 `Trail=8` 只是画拖尾。

### 3.2 自定义弹簧（x 轴）

方程：`vx += ((cx − x) · K − vx · damp) · dt`

```
Read State (1 x) ─► Math (A=720, Op=2 sub) ─► Math (Op=3 mul, B=3.2) ─┐
                                                                       ├─► Math (Op=2 sub) ─► Math (Op=3 mul, B=Runtime DT) ─► Write State (3 vx, add)
Read State (3 vx) ─► Math (Op=3 mul, B=0.9) ───────────────────────────┘
```

再把位置接上：

```
Read State (3 vx) ─► Math (Op=3 mul, B=Runtime DT) ─► Write State (1 x, add)
```

`Runtime DT` 一只电池可以同时喂给两条乘法 —— 引擎的步长只有一处定义。

**y 轴复制一份**，把 `720` 换成 `450`、`x/vx` 换成 `y/vy`。

四条写入的顺序就是执行顺序：`vx → vy → x → y`。反过来写会用到上一帧的速度，肉眼看得出来"迟钝一帧"。

### 3.3 分裂

```
Read State (6 age) ─► Math (9 A>B, B=1) ─► Spawn (Count=2, Spread=14, Max Gen=2, Max Pop=600)
```

条件在 `age > 1` 时变真，之后不再回落 → 每个实例**只分裂一次**。
要周期性分裂，把条件换成会回落的东西，例如 `Math(9, sin(Time·0.6), 0.9)`。

### 3.4 销毁并留痕（只销毁子代）

```
Read State (11 gen) → Math (9 A>B, B=0) ─┐
                                          ├─► Math (3 mul) ─► Destroy (Keep=120)
Read State (6 age)  → Math (9 A>B, B=4) ─┘
```

两个条件相乘：`gen>0` 排除初代，`age>4` 到龄。只写 `age>4` 会把初代一起销毁。
不想区分子代就只留后半条。

`Keep` 是"最多留几个残影"，不是"每个都留"。残影不参与步进、不进 patch。

---

## 4. 常见断路点

| 现象 | 原因 |
|---|---|
| 画面空、`Render Output` 不亮 | `Commands` / `Entities` 没接到对应端口；两个端口不一样，别接反 |
| Mark 画出来是一串空心圈 | 闭合笔形默认有 fill，被 `Fill Style` 清空了 |
| Style Map 没变化 | `Out` 两端填了同一个数 |
| 点击没反应 | `Event Rule.Variable` 没接 `Runtime Var`（或以文本写变量名），或 `Write` 用了 1(set) 而 `Value` 为 0 |
| 粒子不动 | 写了 `x/y` 但没有 `dt`，或写了 `u` 同时期望 `speed` 生效（写了 `u` 就不再自增） |
| 粒子瞬间消失 | `Destroy` 条件对初代也成立；加 `gen>0` 或 `age>…` |
| 分裂一次就停 | 条件 `age>1` 不会回落，这是设计如此；要重复分裂用会回落的信号 |
| 画面每帧抖一下 | 两个 `Write State` 都在写同一个槽位，后者覆盖前者 |

---

## 5. 三条完整的最小作品

```
点描海报   Curve → Path Input → Mark Along Path ← Brush → Style Map → Render Output
点击加粗   Curve → Path Input → Draw Path → Bind ← Math ← Runtime Var
           Draw Path → Event Rule(click) → Render Output
分裂弹簧   Curve → Path In → Blueprint(Speed=0,Life=0) → Look
           → Write×4(弹簧) → Spawn → Destroy → Render Output
```
