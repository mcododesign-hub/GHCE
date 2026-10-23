# 下一阶段：Event 2.0 —— 事件不再是一张动作清单

> **起点**：v0.29 起运行时默认只有画面，播放/重置这类操作已从界面上撤掉，全部交给 GH 定义的事件。
> 这一改动把现有事件系统的天花板照出来了：**动作是固定枚举，改行为必须回 GH 加电池并重新求解。**
> **目标**：事件只做一件事 —— 写运行时变量；一切可见变化由表达式读变量完成。事件与表达式的接口，就是一张 `vars` 表。

---

> **实现状态（0.30.0）**：R1 已落地。协议加了 `var.upsert/var.remove`、`event.upsert.writes`、
> `cmd.upsert.bind`；运行时新增 `expr.js`（AST 求值）并把绑定接进绘制；GH 侧新增 5 只电池。
> 第 4 节的协议草案与实现一致，可以直接当规格看。

## 1. 现状（代码事实）

`events.js` 的规则结构：

```js
{ id, event: 'click'|'dblclick'|'key', action, detail, targets: [cmdId...] }
```

- 规则由 GH 发一次，命中后**浏览器直接执行**（`events.js:112 run()`）—— 这一点是对的，保留：一次点击不需要 GH 重新求解。
- 动作是 8 个固定枚举：`toggle / show / hide / restart / clear / clear-region / reset-time / toggle-play`。
- 动作能改的东西只有三样：指令的 `visible`、时钟、画布像素。
- `targets` 必须写死指令 id 列表。

## 2. 三个结构性缺口

1. **表达力由引擎决定，不由用户决定。** "点一下让线条粗一点 / 让颜色变一下 / 让进度走到一半"——每加一种，都要改 C#、改 JS、发版。这正是上一份评审反对的"堆预设"。
2. **事件与属性之间没有中间层。** 事件只能碰 `visible`，碰不到 `width / opacity / color / progress`，更碰不到表达式。
3. **没有状态。** `toggle` 只能翻 `visible`；做不了"点三下变一次"、"按住时加速"这类带计数的交互。

## 3. 目标模型：事件源 → 变量 → 表达式

```
pointer / key ─► Event Rule ─► vars{x,y,play} ─► Expression ─► 属性(width/opacity/...)
                                    │
                                    └─► 内置消费者（图层可见性 / 时钟 / 清屏）
```

- **事件源**：`click / dblclick / key` 保持；第二版加 `pointerdown / pointerup / pointermove / enter / leave / wheel`。
- **变量**：运行时一张小表，只允许 `number` 与 `bool`。文档级声明，有初值。
- **表达式**：读变量参与计算，与 Runtime Expression 共用同一套 AST（见 `plan-review-addendum.md` §3）。

事件的动作退化成三种**写入**，不再有语义枚举：

| 写入 | 含义 |
|---|---|
| `set` | 赋成给定值 |
| `toggle` | bool 取反（number 按 0/1 翻转） |
| `add` | 数值累加，可带 `min/max` 夹取 |

## 4. 协议（拟，未实现）

```json
{"op":"var.upsert","id":"v_play","value":{"type":"bool","init":false}}
{"op":"event.upsert","id":"e1","value":{
  "event":"click",
  "writes":[{"var":"v_play","op":"toggle"}]
}}
```

- `var` 是文档级状态，进 patch 增量与快照，`doc.reset` 时清空。
- 事件**不直接碰 cmd**，唯一出口是 vars。
- **兼容**：旧 `action` 字段继续认，内部翻译成 `writes` 的语法糖（`toggle` → `v_visible_<cmd>` 翻转、`clear` → `v_clear` 脉冲），0.30 之后标 deprecated，不删。

## 5. 电池（新增 3 只，改 1 只）

| 电池 | 输入 | 输出 | 说明 |
|---|---|---|---|
| `Runtime Var` | Name, Type, Initial | Var | 声明一个变量 |
| `Event Rule` | Var, Event, Detail, Write, Value | Rule | 一次事件写一个变量；多条规则可堆叠 |
| `Runtime Var Read` | Var | RuntimeFloat | 读变量给表达式/绑定用 |
| `Event Trigger`（改） | … | … | 保留为"预设动作"的快捷方式，内部编译成 `writes` |

## 6. 谁来消费变量

1. **第一阶段**：绑定的表达式，例如 `opacity = var("v_play")`，沿用 `cmd.upsert.bind` 的做法。
2. **第二阶段**：把今天由固定动作承担的三件事（图层可见性、时钟播放、画布清屏）改成"写变量 + 内置消费者"，用户就能给它们接任意事件源。

## 7. 执行顺序与确定性（必须写死）

- 每帧：**采样输入 → 派发事件写 vars → 求值表达式 → 绘制**。写入当帧生效，不留一帧延迟。
- 一个事件写多个变量时，按 `writes` 数组顺序执行 —— 顺序对用户可见、可解释，不依赖对象键序。
- 重连 / 快照：vars 恢复到文档里声明的 `init`，**不恢复"用户点过的状态"**；否则导出不可复现。
- 事件是数据，必须进 `serializeCmd`/`serializeDoc` 那条链路 —— 导出的独立 HTML 里事件照样生效。

## 8. 验收（先写死，再动手）

1. 点击一次把一条线的宽度从 1 切到 4：**GH `rev` 不变**，画面变化。
2. 键盘 `a` 按住累加、松开回落（第二版，验证 `add` + clamp）。
3. 同一份作品导出独立 HTML，点击仍然生效。
4. 断线重连后变量回到 `init`，与首帧逐像素一致。
5. 旧的 8 个 action 用例零回归（现有 64 项里的事件断言全部照跑）。

## 9. 分步实施

| 步 | 版本 | 内容 | 完成定义 |
|---|---|---|---|
| **R1** | **0.30 已实现** | `var.upsert` + `writes(set/toggle/add)` + 旧 action 兼容 + 五只电池（Var / Time / Math / Bind / Event Rule）+ demo（点击切换线宽） | **`rev` 不变的交互成立：74 项运行时断言里的「点击后宽度 1.5→7.5、rev 不变、像素 8644→34731」** |
| R2 | **0.30.1 事件源已完成**；目标选择待做 | 事件源扩到 9 种（down/up/move/enter/leave/wheel 已加）；按图层选目标未做 | 拖拽 / 悬停类交互已可搭 |
| R3 | 0.31 | 事件驱动的小状态机（连击计数、拖拽阈值），与 Behavior 的 State 合流 | 复杂交互不需要写 JS |

---

## 10. 一句话

> 现在的事件是"8 个动词"，将来是"写一个变量"；动词的数量是引擎的天花板，变量的数量是用户自己的天花板。
