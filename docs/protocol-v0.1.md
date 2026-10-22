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

### Entity（动态对象蓝图，阶段 2）

```json
{ "id":"e_loop0", "value":{ "geom":"g_loop0", "layer":"particles", "order":0,
  "count":320, "seed":7, "behavior":"follow-curve",
  "speed":[0.02,0.09], "lifetime":[4,11], "trail":5,
  "size":[1.2,2.4], "color":"#FD7AB4", "fade":true } }
```

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `geom` | string | null | 引用的几何 id（`follow-curve` 时必需） |
| `count` | int | 0 | 展开的实例数，上限 20000 |
| `seed` | int | 1 | 决定初始 u / 速度 / 寿命 / 尺寸的确定性种子 |
| `behavior` | string | `follow-curve` | 已实现：`follow-curve` / `drift` / `orbit` / `attract` / `repel` / `follow-pointer`。未知值回退为 `follow-curve` |
| `amount` | number | 1 | 行为强度：跟随的速度倍率 / 漂移倍率 / 环绕半径倍率。**不影响实例身份**，改它不会重启 |
| `speed` | [min,max] | [0.05,0.15] | 每秒推进的归一化弧长 |
| `lifetime` | [min,max] | [4,10] | 秒 |
| `trail` | int | 0 | 拖尾采样点数（0 关闭） |
| `size` | [min,max] | [1.2,2.4] | 绘制半像素 |
| `fade` | bool | true | 按 age/life 淡出 |

**关键约定：实例身份 = `(blueprintId, index)`，与几何内容无关。**
所以 GH 改曲线时只发 `geom.upsert`，实例的 `age` / `u` / 拖尾全部保留，只是路径换了 ——
这正是阶段 2 验收项「同一 Entity ID 在 GH 重新求解后仍可保留状态」的实现方式。
反之，如果 `count` / `seed` / `behavior` / `speed` / `lifetime` / `trail` 变了（定义指纹变化），才按 Restart 语义重建实例。

`doc.reset` 会同时清空 `Doc` 与 `EntitySystem`，两者顺序由 op 顺序保证。

### Track（时间线轨道，阶段 3 B）

```json
{ "id":"a_c0", "value":{
  "field":"reveal",
  "targets":[{"kind":"cmd","id":"c0"}],
  "keys":[[0,0],[2.5,1]],
  "easing":"easeOut", "loop":false, "offset":0 } }
```

| 字段 | 说明 |
|---|---|
| `field` | `reveal` 或 `opacity` |
| `targets` | `{kind:'cmd'|'entity', id}` 数组 |
| `keys` | `[[t, v], …]`，秒 |
| `easing` | `linear` / `easeIn` / `easeOut` / `easeInOut` / `easeOutBack` / `pulse` |
| `lut` | 可选，`number[]`（64 点）。来自 Graph Mapper 采样，**存在时覆盖 `easing`** |
| `loop` | 是否循环；`false` 时到末尾保持末值 |
| `offset` | 整体延后（秒），用来错开多条指令 |

`field` 决定这条轨道在动什么：

| field | 写入 | 单位 |
|---|---|---|
| `reveal` | `cmd.style.reveal` | 0–1 |
| `opacity` | `cmd.opacity` | 0–1 |
| `moveX` / `moveY` | `cmd.transform.translate` | 像素 |
| `rotate` | `cmd.transform.rotate` | 度 |
| `scale` | `cmd.transform.scale` 两轴 | 倍率 |

**轨道只在浏览器里求值**。GH 发一次即止，画面推进不产生任何 patch —— 这是与 `Draw Progress` 电池的根本区别：
后者是"GH 算好进度发下来"，前者是"GH 给曲线，浏览器自己跑"。

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
