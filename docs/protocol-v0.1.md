# GHCE 通信协议 V0.1

**状态：草案，对应里程碑 M0**
**传输：WebSocket 文本帧 · `ws://127.0.0.1:8777/ghce` · UTF-8 JSON**

设计前提来自任务书 1.4：GH 负责定义，浏览器负责执行。所以协议里没有"让浏览器算几何"的入口——所有几何都已是**页面坐标**，运行时不做投影、不做投影矩阵推导。

---

## 1. 帧封装

所有消息共用一个信封：

| 字段 | 类型 | 说明 |
|---|---|---|
| `v` | string | 协议版本，固定 `"0.1"`。不匹配时接收方整帧丢弃并报 `error` |
| `t` | string | 消息类型，见第 2 节 |
| `seq` | int | GH 侧递增发送序号。浏览器用它判断丢帧/乱序 |
| `ts` | int | Unix 毫秒，仅用于诊断 |

浏览器 → GH 的消息也带 `v` / `t`，但 `seq` 是浏览器自己的序号空间。

---

## 2. 消息类型

| `t` | 方向 | 作用 |
|---|---|---|
| `hello` | B → G | 浏览器上线，声明能力（后端、DPR、协议版本） |
| `welcome` | G → B | 返回 sessionId、GH 文档名、当前 rev |
| `patch` | G → B | **核心**。一批增量 op |
| `ack` | B → G | 回报已应用的 `rev` 与本帧耗时 |
| `stat` | B → G | 低频运行时指标（fps、对象数、内存） |
| `error` | 双向 | 结构化错误，带定位信息 |
| `stat` | B → G | **运行时状态回传**（200ms）：指针、帧率、对象数、耗时。GH 只读，不触发求解 |
| `ping` / `pong` | 双向 | 保活，15s 无消息即判定断线 |
| `switch` | B → G | **仅模拟桥使用**，切换演示场景。真实 GH 插件忽略此消息 |

### hello / welcome

```json
{ "v":"0.1", "t":"hello", "client":"ghce-runtime", "caps":{
    "backends":["canvas2d","svg"], "dpr":2, "ws":true, "webgl2":false } }
```

`welcome` 回 `sessionId` / `doc` / `rev`。若带 `"mock": true`，说明对端是开发用的模拟桥，运行时才会显示演示场景按钮（真 GH 插件不发这个字段）。

### patch

```json
{ "v":"0.1", "t":"patch", "seq":42, "rev":18,
  "ops":[ … ],
  "stats":{ "geoms":420, "cmds":500, "buildMs":2.7 } }
```

`rev` 单调递增。浏览器只应用 `rev > 本地 rev` 的帧；重连后 GH 先发一帧 `doc.reset` + 全量 `upsert`。

---

## 3. op 指令集

| op | 载荷 | 说明 |
|---|---|---|
| `doc.reset` | `canvas` | 清空全部文档状态（layers / geoms / cmds），通常后接全量 upsert |
| `canvas.set` | `value` | 画布尺寸、背景、DPR |
| `layer.upsert` | `value` | 图层插入或整体替换 |
| `layer.remove` | `id` | |
| `geom.upsert` | `id`, `value` | 几何插入或替换。**同一 id 内容变化才发** |
| `geom.remove` | `id` | |
| `cmd.upsert` | `value` | 绘图指令插入或整体替换 |
| `cmd.remove` | `id` | |
| `entity.upsert` | `id`, `value` | 动态对象蓝图插入或替换（阶段 2） |
| `entity.remove` | `id` | 移除蓝图及其全部实例 |
| `anim.upsert` | `id`, `value` | 关键帧轨道（阶段 3 B），浏览器侧插值 |
| `anim.remove` | `id` | 移除轨道 |

op 的应用语义是**幂等**的：同一批 ops 重复应用结果一致。这是断线重连能靠全量重放恢复的前提。

`geom` 与 `cmd` 分离是协议最要紧的一条。同一个几何可以挂多条 cmd（不同画笔/图层/顺序），改画笔时只发 `cmd.upsert`，几何一个字节都不重传 —— 直接满足验收项「几何计算与绘图表达能够独立修改」。

---

## 4. 数据类型

### Canvas

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `w`,`h` | number | 1200 / 800 | 页面尺寸，单位 px |
| `bg` | string | `#0b0b0f` | 背景色，`null` 表示透明 |
| `dpr` | number \| `"auto"` | `"auto"` | 后端像素比，上限 2 |
| `clearMode` | `clear` \| `fade` \| `none` | `clear` | 每帧如何清屏：清空 / 叠半透明底色（残影、累积） / 不清 |
| `fadeAlpha` | number | 0.08 | `fade` 模式每帧盖的底色不透明度，越小残影越久 |

### Layer

| 字段 | 类型 | 默认 |
|---|---|---|
| `id` | string | 必填 |
| `name` | string | `""` |
| `order` | number | 0 |
| `opacity` | number | 1 |
| `blend` | string | `"source-over"` |
| `visible` | bool | true |
| `mask` | string \| null | null（**V0.1 未实现**，收到时忽略并报一次 warning） |

### Geometries

几何对象是**纯坐标容器**，不含样式、不含语义。

| `kind` | 载荷 | 说明 |
|---|---|---|
| `polyline` | `enc:"f32"` + `coords`(base64) | 开放折线。`[x0,y0,x1,y1,…]` |
| `polygon` | 同上 | 闭合，渲染时自动 `closePath` |
| `points` | 同上 | 独立点集，按 `style.pointSize` 画 |
| `path` | `cmds` 数组 | 保留贝塞尔语义：`["M",x,y]` `["L",x,y]` `["C",x1,y1,x2,y2,x,y]` `["Q",cx,cy,x,y]` `["Z"]` |

`enc:"f32"` 是 `Float32Array` 的 little-endian 原始字节做 base64。500 条 × 40 点的一条折线，JSON 数字数组约 3.4KB，f32 编码约 1.1KB。解析端用 `DataView` 直接读，不经过字符串解析。

`coords` 省略 `enc` 时视为普通数字数组，仅用于调试与手写测试。

### DrawCommand

```json
{ "id":"c_0f3a", "geom":"g_0f3a", "layer":"ink", "order":10,
  "visible":true, "opacity":1,
  "style":{
    "stroke":{ "color":"#e8e6df", "width":1.2, "cap":"round", "join":"round", "dash":[4,3], "alpha":1 },
    "fill":{ "color":null, "alpha":1, "rule":"nonzero" },
    "pointSize": 3
  },
  "transform":{ "translate":[0,0], "rotate":0, "scale":[1,1], "pivot":[0,0] } }
```

`color` 支持 `#RGB` / `#RRGGBB` / `#RRGGBBAA`。`dash` 为 `null` 表示实线。
`transform.rotate` 的单位是**度**（运行时内部转弧度）；`translate` / `pivot` 是页面像素；`scale` 为 `[sx, sy]`。

`transform.matrix` 是可选的 2D 仿射 `[a,b,c,d,e,f]`（与 Canvas 2D `ctx.transform` / SVG `matrix(...)` 同序）。
存在时**先**应用矩阵，`translate/rotate/scale` 再叠在上面。参数按 `[a,b,c,d,e,f]` 排布：
`x' = a·x + c·y + e`，`y' = b·x + d·y + f`。

`style.reveal`（0–1）是 **Stroke Reveal**：只把路径画到 100×reveal% 的位置。
实现方式是用虚线把路径截断（`setLineDash([len*reveal, len+10])`），因此**只对 `polyline` / `polygon` 生效**；
`path`（贝塞尔）长度未知，会退化为完整绘制。`reveal` 优先级高于 `dash`。

### Mark（v0.31，无新增 op）

Mark 不是新的数据类型，就是**一批普通 DrawCommand**：

```
geom.upsert   g_brush_…      印章轮廓，局部坐标、以原点为中心，整帧只发一次
cmd.upsert    c_mk_…  ×N     每条标记一个 id，geom 都指向同一个印章
              transform.matrix = [cosθ·k, sinθ·k, −sinθ·k, cosθ·k, x, y]
```

要点：

- **位置与朝向只存在于矩阵里**，不烘进几何。同一枚印章被几百次复用，patch 里只有一份 `geom.upsert`。
  101 条标记的整帧约 42 KB（实测），其中几何占不到 1 KB。
- `order` 按标记序号递增，因此同一批标记的绘制顺序稳定；跨电池的层叠用 `Layer` 控制。
- 沿程位置 `u`（0–1）**不进协议**：`Style Map` 在 GH 求解期就把"越往后越粗"算进矩阵与样式了，
  浏览器不需要知道某条标记来自路径的哪一段。
- 闭合印章（圆点 / 方块 / 三角）默认带 `fill`，否则"点描"画出来是一串空心圈。

### Entity（动态对象蓝图，阶段 2）

```json
{ "id":"e_loop0", "value":{ "geom":"g_loop0", "layer":"particles", "order":0,
  "count":320, "speed":0.06, "lifetime":7, "phase":0, "trail":5,
  "size":1.8, "width":1, "shape":null, "color":"#FD7AB4", "fade":true, "opacity":1 } }
```

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `geom` | string | null | 引用的几何 id。程序自己写 `x`/`y` 时可以不接 |
| `count` | int | 0 | 初始实例数，上限 20000 |
| `speed` | number | 0.06 | 每秒推进的归一化弧长 |
| `lifetime` | number | 7 | 秒；0 = 不按寿命重生 |
| `phase` | number | 0 | 整体相位偏移，`u = (i / count + phase) % 1` |
| `trail` | int | 0 | 拖尾采样点数（0 关闭） |
| `size` | number | 1.8 | 绘制尺寸（像素） |
| `width` | number | 1 | 形状描边宽度 |
| `shape` | geom | null | 粒子形状（局部坐标、最长边归一化成 1）；null = 方块点 |
| `color` | string | `#FD7AB4` | 颜色 |
| `fade` | bool | true | 按 age/life 淡出 |
| `opacity` | number | 1 | 整组不透明度 |

**关键约定：实例身份 = `(blueprintId, index)`，与几何内容无关。**
所以 GH 改曲线时只发 `geom.upsert`，实例的 `age` / `u` / 拖尾全部保留，只是路径换了。
`count` / `speed` / `lifetime` / `phase` / `trail` 变了（定义指纹变化）才按 Restart 语义重建实例；
`program` **不在指纹里** —— 拖一根滑条改系数不该把粒子群清空。

#### 状态程序 `program`（v0.32）

```json
"program": {
  "steps": [
    { "slot":"vx", "op":"add", "expr": { "o":"mul", "a": { "o":"state","n":"vx" }, "b": { "o":"dt" } } },
    { "slot":"x",  "op":"add", "expr": { "o":"mul", "a": { "o":"state","n":"vx" }, "b": { "o":"dt" } },
      "when": { "o":"gt", "a": { "o":"state","n":"age" }, "b": { "o":"const","v":2 } } }
  ],
  "spawn":   { "when": { … }, "count":2, "spread":14, "maxGen":2, "maxPop":600 },
  "destroy": { "when": { … }, "keep":200 }
}
```

| 槽位 | 可写 | 说明 |
|---|---|---|
| `x` `y` | ✅ | 页面坐标。**写了就接管运动**：不再从曲线取样 |
| `vx` `vy` | ✅ | 速度，引擎本身不用它，纯粹给表达式做积分 |
| `u` | ✅ | 路径参数。写了就不再自增（`speed` 失效） |
| `size` `alpha` `aux` | ✅ | 尺寸、不透明度、一个自由标量 |
| `age` `index` `gen` | ❌ | 只读：年龄、实例序号、代数 |

- `steps` 按**数组顺序**执行；`when` 非 0 才执行（`null` = 每步都执行）。
- `op` 只有 `set` / `add`，没有算子表 —— 行为由表达式决定。
- 表达式节点新增 `{ "o":"state", "n":"vx" }` 与 `{ "o":"dt" }`（固定 1/60 秒）。
- `spawn` 在条件**上升沿**触发一次：条件持续为真时每帧都生成会在一秒内爆人口。
- 子代继承父代全部状态（`vx/vy/size/alpha/aux`），只有 `age` 归零、`gen+1`、`index` 递增。
- `destroy.keep > 0` 时，被移除实例的最后状态留成静态残影（最多 N 个，不进 patch）。
- 所有写入都在固定步长里执行，所以同样的步数在任何机器上得到同样的结果。

`doc.reset` 会同时清空 `Doc` 与 `EntitySystem`（含残影），两者顺序由 op 顺序保证。

### Track（轨道，v0.18）

轨道不是独立的 op 流 —— 它挂在 `cmd.upsert` 的指令值里，跟着指令一起增删。
一条轨只回答两件事：**我占全局时间轴的哪一段**、**这一段里播什么帧**。

```json
{ "track": {
    "n": 25,
    "frames": "<base64 f32，n*6 个 2D 仿射>",
    "reveal": [0, 0.04, "…"],
    "start": 1.5, "end": 4.5,
    "hidden": false,
    "easeX": [0, 0.5, 1], "easeY": [0, 0.75, 1]
} }
```

| 字段 | 说明 |
|---|---|
| `n` | 帧数，≥ 2 |
| `frames` | `n*6` 个 float（base64 f32），每帧一个 `[a,b,c,d,e,f]` 页面矩阵；只做生长时可为 `null` |
| `reveal` | `n` 个 0–1；只做矩阵动画时可为 `null` |
| `start` / `end` | 轨道在全局时间轴上的入点 / 出点（秒）。`end <= start` 视为占满整段 |
| `hidden` | `true` = 该轨静默，不驱动，指令保持静态值（调试时临时关一条轨用） |
| `easeX` / `easeY` | 缓动表（横轴 0–1 → 进度 0–1），可为 `null` |

播放规则：

- 全局播放头 `t` 由 `doc.timeline` 决定（回绕或夹在两端）
- 轨道局部进度 `p = clamp01((t − start) / (end − start))`，再过缓动
- **入点前停在首帧，出点后停在末帧** —— 不会突然出现或消失

### timeline.set（全局时间轴）

```json
{ "op": "timeline.set", "duration": 6, "loop": true }
```

| 字段 | 默认 | 说明 |
|---|---|---|
| `duration` | 8 | 整段多长（秒）。**所有轨道共用这一条时间轴** |
| `loop` | `true` | 回绕；`false` 时夹在 `[0, duration]`，两端保持 |

这里刻意不含任何具体动画。反过来，如果把时长放进轨道，每条轨就各自有一个 0→1 的本地时钟，
"第 0 秒淡入、第 2 秒开始转、第 5 秒收尾"这种多轨错开就无法表达了 —— 没有共同的播放头，
就没有先后可言。

**轨道只在浏览器里求值**。GH 发一次即止，画面推进不产生任何 patch —— 这是与 `Draw Progress` 电池的根本区别：
后者是"GH 算好进度发下来"，前者是"GH 给帧序列，浏览器自己跑"。

### morph（几何变形帧序列，v0.19）

挂在 `cmd.upsert` 的指令值上，与 `track` 并列。一条 cmd 可以同时有 `track`（时间）和 `morph`（形态）。

```json
{ "morph": {
    "n": 24, "count": 96, "step": 12, "closed": true,
    "frames": "<base64 f32，n*count*2 个页面坐标>"
} }
```

| 字段 | 说明 |
|---|---|
| `n` | 帧数 ≥ 2 |
| `count` | 每帧点数 ≥ 2。**所有帧必须一致**，否则整条丢弃 |
| `step` | 每秒帧数。素材自然时长 = `(n-1)/step`；没有 track 时拿它兜底播放 |
| `closed` | 是否闭合 |
| `frames` | `n·count·2` 个 float，按「第 k 帧 → 第 i 点 → x,y」顺序排列 |

运行时用**与 track 相同的 `u`** 在相邻两帧之间逐顶点线性插值；`u` 有 track 就取 track 的进度，
没有就按 `step` 播一遍后停住。三者共用同一个 `u`，形状、位置、笔画才不会各走各的。

这是"任意 slider 都能动画"的落地方式：运行时不需要认识 fillet / offset / 布尔，
只要 GH 侧把参数扫一遍、给出 N 份同拓扑几何，插值就成立。
**硬约束是点数与顺序一致** —— 顺序错位会得到扭曲的中间态，运行时无法检测，只能由 GH 侧保证。

### 交互

指针与键盘状态**不进协议的正向命令**（它们是浏览器本地输入，直接喂给需要它的行为）；
但它们会通过 `stat` **回传**给 GH，供 `Interaction` 电池读取。

```json
{"v":"0.1","t":"stat","ptr":[1024,412],"inside":true,"down":false,"clicks":3,
 "keys":"wd","fps":60,"entities":1920,"alive":1920,"rev":12,
 "drawMs":1.2,"stepMs":0.4,"time":8.31}
```

**坐标是弹出画布的页面坐标**（左上原点、Y 向下、像素），不是 Rhino 世界坐标。
想拿它驱动几何，映射关系由你决定 —— 插件不替你做这个假设。




绘制顺序 = `(layer.order, cmd.order, cmd.id)`。三级排序保证结果可重复——只靠 `order` 时同值项的先后取决于 Map 迭代顺序，是隐性不确定源。

---

## 5. 坐标与单位

- 运行时坐标系：**页面像素，Y 轴向下，原点左上**。
- 世界坐标 → 页面坐标的转换在 GH 侧完成（`Project to 2D`），运行时只接受页面坐标。
- 缩放/旋转/平移在 cmd 的 `transform` 里表达，不烘焙进几何。这样同一个几何可以被多条 cmd 以不同变换复用。

---

## 6. 增量更新判定（对应 4.3）

GH 侧 `PatchBuilder` 维护"上次发出的状态"，本帧求解后做五类比较：

| 比较对象 | 触发 | 结果 |
|---|---|---|
| 几何坐标指纹 | 指纹变化 | `geom.upsert` |
| 样式 / 变换 / 层级 / 顺序 | 值变化 | `cmd.upsert` |
| 图层属性 | 值变化 | `layer.upsert` |
| 画布参数 | 值变化 | `canvas.set` |
| 对象集合 | 增删 | `upsert` / `remove` |

几何指纹 = FNV-1a(量化到容差的顶点字节)。量化容差等于插件的 `ID Tolerance`，因此"曲线移动量小于容差"不会触发重传，也不会改变 Entity ID。

整帧无 op 时**不发 patch**。这是 Slider 微调不打爆带宽的基本保证。

---

## 7. 错误

```json
{ "v":"0.1", "t":"error", "code":"GEOM_INVALID", "msg":"curve c_12 采样点数 < 2",
  "loc":{ "component":"Draw Path", "id":"c_12" } }
```

`code` 取值：`VERSION_MISMATCH` `OP_UNKNOWN` `GEOM_INVALID` `LAYER_MISSING` `CAP_UNSUPPORTED` `RUNTIME_EXCEPTION`。

---

## 8. V0.1 明确不做

- 浏览器状态回传 GH 的闭环（`stat` 只上行不参与求解）
- 二进制几何的独立帧（当前 base64 内嵌 JSON）
- `mask` / `blend` 的真实合成（仅透传）
- 多文档 / 多画布会话
