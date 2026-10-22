# GHCE 插件构建与加载验证

**状态：已编译通过（0 错误 0 警告），并做过加载冒烟测试与协议回放。尚未在 Grasshopper 界面里做端到端联调。**

---

## 0. 本机实际环境（2025-10 实测）

| 项 | 值 | 说明 |
|---|---|---|
| Rhino | **8.15.25013.13001** | `D:\Program Files\Rhino 8` |
| 插件目标 | `net7.0-windows` | Rhino 8 的 .NET 7 运行时 |
| .NET SDK | 10.0.402 @ `C:\Users\ASUS\.dotnet` | 系统 PATH 里的 `C:\Program Files\dotnet` **只有运行时没有 SDK**，直接敲 `dotnet` 会报 "No SDK" |
| .NET 7 运行时 | 7.0.20 @ `C:\Program Files\dotnet` | 跑探针要用它 |

因为 PATH 指到了没有 SDK 的那个 dotnet，本仓库所有构建命令都用全路径：

```bash
"/c/Users/ASUS/.dotnet/dotnet.exe" build -c Release
```

## 1. 为什么不用 NuGet 的 RhinoCommon 包

先试过 `PackageReference RhinoCommon 8.15.25013.13001` + `Grasshopper 8.15...`，还原成功但报 **NU1701**：
8.15 这一版包**只有 `lib/net48`**，没有 net7.0 资产，等于把一个 .NET Framework 程序集塞进 .NET 7 工程。能编过，但运行时变体是错的。

改成直接引用本机安装的 .NET 7 程序集：

```xml
<RhinoDir>D:\Program Files\Rhino 8</RhinoDir>
System\netcore\RhinoCommon.dll
Plug-ins\Grasshopper\Grasshopper.dll
Plug-ins\Grasshopper\GH_IO.dll
```

换机器改 `RhinoDir` 一个属性即可：`dotnet build -p:RhinoDir="E:\Rhino 8"`。

## 2. 构建与安装

```bash
cd gh/GHCE
"/c/Users/ASUS/.dotnet/dotnet.exe" build -c Release
```

`csproj` 里有个 `MakeGha` 目标，编译后自动把 `GHCE.dll` 复制成 `GHCE.gha`。

安装（已执行过，文件已就位）：

```powershell
cd gh
.\install.ps1            # 检查 Rhino → 复制 → 校验 md5
```

**不要手敲 `cp`。** GH 会锁住已加载的 `.gha`，复制失败的表现是"代码改了、重启了、没变化"——
本轮就是踩在这个坑上，装了三遍才成功。`install.ps1` 把"检查 → 复制 → 校验 hash"合成一步，失败会明确报出来。

**`install.ps1` 必须存成 UTF-8 with BOM。** 无 BOM 的 UTF-8 会被 Windows PowerShell 5.1 按 ANSI 读，
中文变乱码 → `Write-Host` 字符串缺终止符 → **整个脚本解析失败，一行都不执行**（报
`字符串缺少终止符` / `缺少右"}"`，看起来像语法错，其实是编码错）。换机器迁移时踩过一次，已补 BOM。
`gh/` 下所有 `.ps1` 都守这条规矩。

卸载就是删掉 `%APPDATA%\Grasshopper\Libraries\GHCE.gha`。

## 3. 编译时踩到的三个真错误（留档）

| 错误 | 原因 | 改法 |
|---|---|---|
| `CS0019: && 不能用于 double[] 和 bool` | `Curve.DivideByCount(int, bool)` 的重载返回的是 **参数值 `double[]`**，不是 `Point3d[]` | 干脆不用它，改成按归一化弧长自己采样 `PointAtNormalizedLength` |
| `CS0029: uint 不能隐式转 string` | `Dictionary<string,string>` 里塞了 `Fnv()` 的 `uint` | `Fnv(b64).ToString("x8")` |
| 探针 `FileNotFoundException: Grasshopper` | `Main` 里出现 GH 类型 → JIT 编译 `Main` 时就去解析程序集，早于 `AssemblyResolve` 注册 | 把 GH 相关代码挪进独立方法 `Run()`，`Main` 只负责注册解析器 |
| `ReflectionTypeLoadException: Microsoft.Web.WebView2.Core` | **GH 加载 .gha 时会遍历全部类型**，只要有一个类型的字段引用了找不到的程序集，整个库加载失败、电池一个都不出现。WebView2 在 `<Rhino>\System\`，不在 GH 库目录 | `AssemblyInit.cs` 用 `[ModuleInitializer]` 在程序集载入的瞬间挂 `AssemblyResolve`，把 WebView2 系映射到 RhinoCommon 的兄弟目录 |

最后一条是加载期的隐形炸弹：**它在 GH 里表现为"这个库根本没加载"，而不是某个电池报错**。所以任何新增第三方引用（WebView2、图像库、MathNet…）都必须配一个加载期解析钩子，否则用户看到的是空白。

## 4. 已验证到什么程度

### 4.1 加载冒烟（`gh/probe`）

```bash
cd gh/probe
"/c/Users/ASUS/.dotnet/dotnet.exe" build -c Release
DOTNET_ROOT="C:/Program Files/dotnet" "/c/Program Files/dotnet/dotnet.exe" ./bin/Release/net7.0-windows/probe.dll
```

**验安装后的那份文件**（不是构建输出），把 BIN 指向库目录：

```bash
GHCE_BIN="C:/Users/ASUS/AppData/Roaming/Grasshopper/Libraries" \
DOTNET_ROOT="C:/Program Files/dotnet" \
"/c/Program Files/dotnet/dotnet.exe" ./bin/Release/net7.0-windows/probe.dll
```

这条检查很重要：`cp` 因为 Rhino 锁文件而**静默失败**过一次（旧文件还是 37KB），
只有比对 md5 或直接对库目录那份跑探针才能发现"装了但其实没装"。

实测输出（节选）：

```
loaded: GHCE 0.1.0.0
runtime: 7.0.20
assembly info: GHCE / 3c9e0000-3333-4c03-9003-0000000000b1
  OK  Canvas         5a0c…a1  in=4 out=1
  OK  Draw Path      5a0c…a3  in=10 out=1
  OK  Path Input     5a0c…a2  in=6 out=2
  OK  Render Output  5a0c…a4  in=4 out=1
  goo: valid=True str=g_test [2 pts]
  patch1: rev 1: 4 ops, 1.64 ms  len=691
  patch2 (expect null): null  no-op
RESULT: PASS
```

也就是说：GH_Component 基类构造、参数注册、自定义 Goo/Param、PatchBuilder 首帧输出与"无变化不发包"都在真 .NET 7 + 真 Grasshopper 程序集下跑通了。

### 4.2 协议回放（`mock/replay-csharp.mjs`）

把上面导出的真实 patch JSON 灌进浏览器运行时：

```
PASS  运行时连上回放桥
PASS  doc.reset 的 canvas 被接受
PASS  layer/geom/cmd 三个 op 全部入库
PASS  f32 base64 解码成 2 个点
PASS  dash 数组原样保留
PASS  颜色大小写不影响解析
PASS  该指令被真正画出来
PASS  面板无错误 / 无控制台错误
9/9 passed
```

这一步能挡住"字段名/编码/类型"三类错误，挡不住"GH 界面里的求解时机"。

### 4.3 没验到的

- **GH 界面里的端到端**：GH 求解 → 推 WS → 浏览器更新。需要你手动跑一次。
- **Rhino 无头脚本跑不通**：`Rhino.exe /runscript="-_RunPythonScript (...)"` 在无头模式下 100 秒无输出，疑似被启动对话框卡住。已放弃这条路，改用 4.1 的探针。

## 5. GH 里的第一个定义

```
Curve ──► Path Input ──► Draw Path ──► Render Output
                              ▲              ▲
                          样式/图层        Canvas
```

1. 拖一条曲线进 `Path Input`，`Render Output` 的 `Preview` 保持 true
2. 预览窗口自动弹出，HUD 的"连接"应是 `open`
3. 拖曲线控制点 → 窗口里的图形跟着变，**不刷新页面**

`Render Output` 的 `Message` 会显示 `1 client · rev N: M ops, X ms`。如果一直是 `0 client`，看 HUD 的连接状态：`retry` 就是端口没对上（默认 8777）。

想用外部浏览器看同一份东西，把 `Url` 输出接了 Display，复制地址即可。

## 6. 图标

电池图标在 `GHCE/Icons.cs` 里用 GDI+ 现画（24×24），**不放任何 PNG/SVG 资源文件**。四个都是同一套语义：

| 电池 | 图形 | 读作 |
|---|---|---|
| Canvas | 深底画框 + 绿色裁切角标 | 画布 |
| Path Input | 左虚线源 + 曲线 + 两端节点 | 从 Rhino 取曲线、采样成路径 |
| Draw Path | 同一条曲线，前半段被粉色盖住 | 正在被描绘的路径 |
| Render Output | 画框内的曲线 + 绿色箭头穿出右边界 | 送出到浏览器 |

另有三个**数据（参数）图标**——GH 会把自定义 `Param` 也列进标签页，不给图标就是棋盘格：

| 参数 | 图形 |
|---|---|
| `Param_GHCE_Canvas` | 细边框 + 空画框 |
| `Param_GHCE_Path` | 细边框 + 四点折线 |
| `Param_GHCE_Command` | 细边框 + 曲线 + 粉色方块（带样式） |

参数图标必须覆写 `Internal_Icon_24x24`，**不是 `Icon`**：GH 画参数走的是 `Icon_24x24`（final），它内部才读 `Internal_Icon_24x24`；
`Icon` 这个属性对参数恒为 `null`，拿它判断会误以为没图标。

改颜色只改 `Icons.cs` 顶部的常量。想看效果不用开 GH，跑一次探针就会导出对照图到 `docs/shots/icons-preview.png`（放大 8 倍，NearestNeighbor 保持像素边界）：

```bash
cd gh/probe
DOTNET_ROOT="C:/Program Files/dotnet" "/c/Program Files/dotnet/dotnet.exe" ./bin/Release/net7.0-windows/probe.dll
```

**坑 1**：Rhino 运行时会锁住已加载的 `.gha`，`cp` 会报 `Device or resource busy`。要更新插件必须关掉所有 Rhino（GH 没有热重载），用 `install.ps1`。

**坑 2（图标只显示一个）**：把同一个 `Bitmap` 实例反复交给宿主时，GH 画完会释放它，
于是只有第一个拿到实例的电池有图标、其余全空白。现在 `Icons.Get()` 每次**交出一份独立副本**（`new Bitmap(master)`）。
GH 里右键 `Render Output` → `图标自检…`，会把四个图标放大 8 倍导出并打开，用来区分"图没画出来"和"GH 没显示"。

## 7. 预览窗口与内嵌页面（v0.2）

v0.1 要你手动开浏览器、手动跑 node 服务。v0.2 起不用了：

- **运行时页面被编进 `.gha` 的嵌入资源**，插件的桥在 8777 上同时提供 HTTP 页面和 `/ghce` 的 WS 升级。
- `Render Output` 多了 `Preview` 开关（默认 true），第一次求解就会弹一个 **WPF + WebView2 窗口**，里面就是运行时页面 —— 和 Processing 的运行窗一个意思。
- 窗口关掉后不会再自己弹回来（开关是上升沿触发）。把 `Preview` 拨到 false 也会关窗。
- WebView2 起不来时**降级为系统浏览器**打开同一地址，错误会挂到电池的 Warning 上，不静默失败。

端口只有一个：`8777`。`http://127.0.0.1:8777/` 是页面，`ws://127.0.0.1:8777/ghce` 是协议。

```
Curve ─► Path Input ─► Draw Path ─► Render Output ─► 预览窗口（自动弹出）
                                        ▲
                                     Canvas
```

`Render Output` 的 `Url` 输出就是那个地址，复制到 Chrome 里也能看 —— 同一份页面，两个入口。

### 怎么验的（不需要你开 GH）

探针带 `--serve` 模式，直接调插件自己的 `BridgeServer`，在 8799 上发内嵌页面并用真 `PatchBuilder` 持续推帧：

```bash
cd gh/probe
DOTNET_ROOT="C:/Program Files/dotnet" "/c/Program Files/dotnet/dotnet.exe" ./bin/Release/net7.0-windows/probe.dll --serve
# 另开终端
cd mock && node verify-plugin-serve.mjs
```

实测 **7/7**：页面来自嵌入资源、WS 连上插件自己的桥、60 条路径全部入库并绘制、帧在持续变化、接真 GH 时模拟桥的演示按钮自动隐藏、无控制台错误。

### 没验到的

**WPF 窗口本身没在无头环境里验过**。WebView2 需要真实桌面会话，无头跑不了。降级路径（系统浏览器）是兜底，但窗口能否正常弹出，只有你开 Rhino 那次才知道。

## 8. v0.3 的三处改动

**a. `Path` 可以直接接进 `Render Output.Commands`。**
`GHCE_Command.CastFrom` 现在接受 `GHCE_Path`，用默认样式（白线 1px、main 层）包一层。
所以"只想看一眼"的时候：`Curve → Path Input → Render Output` 三步就够，不用先摆 `Draw Path`。
要控制线宽/颜色/图层/顺序时才需要 `Draw Path` —— 它的存在意义是**一个几何配多种画笔**，不是必经环节。

**b. `Port` 从输入口去掉了。**
端口是环境配置，不该出现在作品图里。现在桥从 8777 起自动找空闲端口（8777–8788），实际端口显示在 `Render Output` 的右键菜单里（菜单第一行就是 `GHCE v0.3.1 · 端口 N`），菜单里还有"用外部浏览器打开"。

端口会漂移带来一个陷阱：页面里 WebSocket 的默认值写死是 8777，端口一漂向导就会连到别处。
所以预览地址统一用 `BridgeServer.PreviewUrl`（`http://127.0.0.1:{实际端口}/?ws=ws://127.0.0.1:{实际端口}/ghce`），
`Preview` 开关、右键菜单、`Url` 输出三处都走它。实测：8777 被占时自动落到 8778，页面照样 7/7 通过。

**c. 图标每次交独立副本**（见 §6 坑 2）。

## 8. 电池总表（v0.16.0）

十六只电池。除 `Draw Path`（10 个，按你的要求保留原样）之外都在 8 个端口以内。
**v0.13 删掉了 `Timeline` / `Keyframe` / `Oscillator`** —— 那三只靠"选字段"驱动动画，
属于我预设的动画，不是你在 GH 里表达的那个变化。**v0.15 删掉 `Entity Behavior`**（6 个模式只剩 1 个有用）。
**v0.16 补上 `Track`** —— 用"帧序列 + 时长 + 循环 + 缓动曲线"取代它们，不选字段、不给进度。

| 电池 | 组 | 输入 | 作用 |
|---|---|---|---|
| Canvas | Output | 4 | 画布 |
| **Project to 2D** | Drawing | 6 | 曲线包围盒 → 相机（页面尺寸 / 内边距 / 固定缩放） |
| Path Input | Drawing | **4** | 曲线 → 页面坐标路径（相机可选） |
| Draw Path | Drawing | 10 | 路径 → Draw Command |
| **Layer** | Drawing | 6 | 指令收进显式图层（顺序 / 不透明度 / 混合 / 可见） |
| **Stroke Style** | Drawing | 6 | 改描边（颜色 / 线宽 / 透明度 / 虚线 / 端点） |
| **Fill Style** | Drawing | 4 | 改填充（颜色 / 透明度 / 清除） |
| Draw Progress | Drawing | 3 | Progress + Spread → 沿路径逐步显现 |
| Transform | Drawing | **2** | Commands + 每个指令一个 Xform（1 个则共用）；改了浏览器自己演过去 |
| **Track** | Animation | **6** | 时间轨：`Frames`(N 个 Transform) / `Reveal`(N 个数) / `Ease`(一条曲线) / `Duration` / `Loop`。GH 算一次，浏览器按时间插值 |
| **Event Trigger** | Animation | **5** | 管道：把"事件 → 动作"挂到喂进来的这几条指令上 |
| Render Output | Output | **5** | 编译 + 推送 + 预览窗（事件跟着指令走，不再单独占口） |
| Entity Blueprint | Entity | **5** | 沿一条曲线运动的动态对象（数量 / 速度 / 寿命 / 相位）· 确定性分布 |
| Entity Look | Entity | **8** | 形状（单条曲线）/ 颜色 / 线宽 / 缩放 / 拖尾 / 图层 / 淡出 |
| Interaction | Input | 0 | 指针 / 点击 / 按键（画布坐标） |
| Runtime Info | Input | 0 | 自检：帧率 / 对象数 / 耗时 / 连接数 |

### 序号化的对照表（都写在端口说明里）

| 端口 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|
| `Event Trigger.Action` | toggle | show | hide | restart | clear | clear-region / reset-time / toggle-play |

**`Keyframe.Ease` 那一行随电池一起删了（v0.13）**；现在缓动是一条真曲线（`Track.Ease`），
不是六个名字里挑一个。

**`Entity Behavior.Mode` 这一行在 v0.15 删了** —— 六个模式里五个是摆设，只有一个 `follow-curve` 有实际意义。
而"沿哪条曲线"这件事，蓝图第一个输入就是 `Path`，本来就不需要再用一个下拉去表达。

**动画没有"字段"这一层了。** v0.11–v0.12 曾让 `Timeline` / `Oscillator` 用 `Field` 选
"驱动哪个属性"（reveal / opacity / rotate / scale / xform…）。这是错的：那等于预设了一套动画，
盖住了你在 GH 里真正表达的变化。现在只有一条规则 —— **GH 的数据怎么变，浏览器就怎么演过去**
（`runtime/src/tween.js`），第一次出现从原始状态演到当前状态。

**为什么用名字/序号而不是枚举下拉**：GH 里做真正的下拉要自定义 Attributes，代价大且难搜；
文本/数字 + 电池的 `Message` 区域实时显示当前语义（如 `3 orbit`、`p 0.5`），既好学也不占端口。
输入越界会 Warning 并回退到默认值，不会静默出错。

### Transform：只吃一个 Transform 对象

早期版本有 `Move / Scale / Rotation / Anchor` 四个口，实测太抽象、也抢端口，已删除。
现在只有两个输入：`Commands` + `Xform`。

注意：Grasshopper 自带的 `Move / Rotate / Scale` 输出的是**几何**，不是 Transform 对象；
要拿 Transform，得自己用 C#/Python 或矩阵运算合成。

**不能直接搬矩阵。** 页面上看到的是"世界被一台相机拍下来"的结果，相机就是 Path Input 那一步：

```
P:  px = s·X + ox
    py = −s·Y + oy        ← s 是全部曲线的联合包围盒缩放到页面；ox/oy 是居中后的偏移
```

世界里的 M 要在页面上等价成立，必须做 **`P·M·P⁻¹`**：

```
世界仿射：X' = a·X + b·Y + tx ,  Y' = c·X + d·Y + ty
页面矩阵（Canvas 语义 [a,b,c,d,e,f]）：

[  a,  −c,  −b,  d,
   −a·ox + b·oy + s·tx + ox,
    c·ox − d·oy − s·ty + oy ]

退化（无相机信息）：[a, −c, −b, d, tx, −ty]   ← 早期版本只有这个，所以锚点必偏
```

区别有多大：绕几何中心转，早期版本会变成**绕页面原点转**。世界坐标里的中心若是 (30,40)、相机 s=2，页面被当成锚点的位置偏出去 (100,700) 量级——看起来就是"转的时候整个人甩出去了"。

`Proj`（`[s, ox, oy]`）由 Path Input 写进 `PathDef`，**不进协议**；Transform 从指令引用的 Path 上读。所以只要链路上有 Path Input，锚点就是对的；手工拼的 `CommandDef` 没有相机信息，会 Warning 并退回 F 共轭。

**已数值验证**（`gh/probe`，刻意只用 double，不碰 Rhino 类型）：
```
transform anchor: (160,620) expect (160,620) ok=True   ← 世界中心是页面不动点
transform orbit : (160,420) expect (160,420) ok=True   ← 绕中心转 90° 后落位正确
transform shift : (120,660) expect (120,660) ok=True   ← 世界平移要乘 s、Y 反向
transform fallback: e=70 f=-10 expect 70,-10 ok=True
```
`runtime/verify` 再用像素验了一次"画布真的绕指定点转"（横条→竖条）。**仍然没在 Rhino 内验过**的是 `Transform.Rotation` / `Point3d.Transform` 那侧能不能正确吐出世界仿射分量——那部分只能在 Rhino 里看。

**变换要动起来**：不要给 Xform 做动画（它是静态矩阵），把 N 个 Xform 一起喂给 `Track.Frames` ——
GH 每次求解给你当前那一帧，`Track` 把整段矩阵序列一次发给浏览器，剩下的插值与循环由运行时做。
`Track` 里没有"驱动哪个属性"这个选项：帧就是矩阵，reveal 就是 reveal，它们各自有输入口。

**一个必须知道的边界**：相机在 Path Input 里一次框定，**不会跟着几何旋转重算**（输入几何不变，P 就不变）。所以绕自身中心转是稳的；但如果一条细长条转 90°，它可能超出画框被裁——那是相机没动，不是坐标错了。要留余量就把 `Margin` 调大，或者把 `Page W/H` 设成动画全程的外接尺寸。

### Track.Ease 接任何曲线
`Track` 的 `Ease` 端口接一条曲线（Graph Mapper 的输出本身就是 Curve）。
插件在 `[0,1]` 上采 33 点发给运行时，运行时按 x 二分查表得到 `u`。
于是"带过冲、带停顿、任意形状的运动曲线"不需要写代码，也不需要预置一堆缓动名 ——
缓动是一条你自己画的线，不是一个下拉里的名字。


**Entity Blueprint**（`GHCE / Entity`）

| 端口 | 类型 | 默认 | 说明 |
|---|---|---|---|
| Path | Path / **Curve** | — | 直连原生 Curve 也行，会用默认页面 1440×900 / margin 60 投影 |
| Count | int | 200 | 实例数量 |
| Speed | number | 0.06 | 每秒推进的归一化弧长（1 = 一秒跑完整条） |
| Lifetime | number | 7 | 寿命（秒），到点原地重生 |
| Seed | int | 1 | 初始分布种子 |
| Vary | number | 0.35 | 0–1：把单值展开成区间，省掉 4 个端口 |

输出 `Blueprint`。

**Entity Look**

| 端口 | 类型 | 默认 |
|---|---|---|
| Blueprint | Entities | — |
| Colour | Colour | #FD7AB4 |
| Size | number | 1.8 |
| Trail | int | 4（0 = 关闭） |
| Layer | text | particles |
| Fade | bool | true |

输出 `Entities` → 接 `Render Output` 的 `Entities` 口。

接线：

```
Curve ─────► Entity Blueprint ─► Entity Look ─► Render Output
                ▲ 调数量/速度                      ▲
                                              Canvas
```

**为什么 Vary 而不是 Speed Min/Max**：Grasshopper 里区间要两个 Number 或一个 Interval，端口翻倍；而调参时人真正想说的是"快一点/慢一点、乱一点/整齐一点"。Vary 就是这个旋钮，区间由它推出：`speed×(1±vary)`，寿命用 `×0.6` 的幅度。

**Entity Behavior / Draw Progress 为什么是字符串与两个旋钮**：
- `Mode` 用文本而不是枚举，是因为运行时那侧 `BEHAVIORS` 是扩展点 —— 加一种行为只改 `entity.js` 一个数组，电池不用重新编译。
- `Progress` + `Spread` 两个端口替代"每条指令一个进度口"，否则 `Draw Path` 就要多一个端口乘以线数。
  `Spread=0` 是一起画，`Spread=1` 是依次画完。

**为什么 Vary 与 Amount 不进身份指纹**：它们只影响手感，不决定“这个对象是谁”。修改它们时，实例的 age 与位置都保留。

**Track 与 Draw Progress 的区别**（两只都能让线"长出来"，但完全不是一回事）：

| | Draw Progress | Track |
|---|---|---|
| 谁算进度 | GH 每次求解算好再发 | 浏览器按运行时钟插值 |
| 每帧有没有 patch | 有（进度变了就发） | **没有**，只发一次帧序列 |
| 适合 | 手工 scrub、跟 GH 参数联动 | 连续动画、播放/暂停/循环 |

两者可以并存。想验证"真的在浏览器里跑"：看 HUD 的 `rev` 不动而画面在变，就是 Track 在起作用。

**原生数据的入口**：
- 任何接 `Path` 端口的地方都能直连 `Curve`（`GHCE_Path.CastFrom` 内部投影）
- `Path` 也能直连 `Entities` 端口（默认 200 个实例）
- `Render Output.Commands` 能直连 `Path`（默认白线 1px）

这三条都是"先能看，再精修"的退路，不是替代 `Path Input` / `Draw Path`。

## 9. 已知边界（V0.1 有意留的坑）

| 项 | 现状 | 归属阶段 |
|---|---|---|
| 图层 | ✅ v0.10 起 `Layer` 电池显式给 order / opacity / blend / visible；不接则仍按首次出现序号 × 10 推导 | 阶段 1 D |
| `Project to 2D` | ✅ v0.10 独立成电池；`Path Input` 只负责采样 + 套相机 | 阶段 1 A |
| 点集 | 协议支持 `points`，还没有电池产出 | 阶段 1 B |
| 笔画样式电池 | ✅ v0.10 起 `Stroke Style` / `Fill Style`（管道式，不改 `Draw Path` 端口） | 阶段 1 B |
| 实体图层的顺序 | `Entity Look` 只有 `Layer` 文本口，order 仍按推导。要显式顺序得等 Entity 侧补 Layer 口 | 阶段 2 |
| 增量粒度 | 几何按 f32 全量指纹比对，没有分块更新 | 阶段 2 |
| 双向回传 | 只处理 `ack` / `ping`，不回灌求解 | 阶段 7 |
| 多实例 | `PatchBuilder` 是组件实例级的；同一文档里放两颗 `Render Output` 会互相覆盖 | 未排期 |

---

## 10. v0.10.0 · 阶段 1 地基（四只电池 + 相机对象）

### 新增

| 电池 | 输入 | 说明 |
|---|---|---|
| `Project to 2D` | Geo / W / H / Margin / Scale / Bounds(可选) | 包围盒 → 相机。`Scale > 0` 时固定缩放（动画全程共用一套框）；`Bounds` 接外接矩形时相机完全不被单帧几何带跑 |
| `Layer` | Cmds / Name / Order / Opacity / Blend / Visible | 显式图层。顺序小的在下，负值可插到默认层之下 |
| `Stroke Style` | Cmds / Colour / Width / Alpha / Dash / Cap | 改描边。**未接的端口 = 保留原值** |
| `Fill Style` | Cmds / Colour / Alpha / Clear | 改填充。`Clear` 打开 = 取消填充（"不接线"和"故意不填"必须能区分） |

样式做成**管道**而不是给 `Draw Path` 加端口：它已经有 10 个口且要求保持原样。
管道还有个副产品 —— 同一份几何可以分叉成两条 `Stroke Style`，被两种画笔分别解释，
这正是任务书 1.4 原则二要的效果。样式电池全部 `Duplicate()` 后再改，不改上游对象。

### ⚠️ 破坏性变更：Path Input 从 6 口变 4 口

```
旧：Geo / Tol / Page W / Page H / Margin / Id
新：Geo / Tol / Camera(可选) / Id
```

页面尺寸与内边距搬进了相机。**v0.9 存的 .gh 里，接在 W/H/Margin 上的线会掉。**
不接 `Camera` 时行为与 v0.9 完全一致（1440×900 / margin 60 就地拟合），所以最短链
`Curve → Path Input → Render Output` 不用改线。

迁移做法：想继续自动取景就什么都不接；想要"整段动画共用一套框"，串
`Curve → Project to 2D → Camera → Path Input`。

### 相机语义（这段是这轮唯一的"概念"）

投影从"每次求解按当前几何重新拟合"变成"可以只算一次的对象"。差别在细长条转 90°：

- 旧：相机跟着当前包围盒变 → 转到一半时框变宽、比例跳变；或者按初始框算死 → 出框被裁
- 新：把**整个动画过程的曲线**（或一个显式 `Bounds` 矩形）接进 `Project to 2D`，框一次，
  之后几何在框里怎么转都不裁

数学在 `Types/CameraMath.cs`，**纯 double，不碰 Rhino 类型**，所以探针能在 Rhino 外验。
`GHCE.Types.CameraDef.Proj` 仍然是 `[s, ox, oy]`，`Transform` 的 `P·M·P⁻¹` 一个字节没动。

### 这轮踩的两个坑（都属于"换机器才会暴露"）

**1. 手写 ComponentGuid 撞车。** 新增四只电池时按 `…a5 / a6 / a7 / a8` 往下排，
结果这四个号段**已经被 Draw Progress / Entity Behavior / Entity Blueprint / Entity Look 占了**。
GH 用 `ComponentGuid` 做组件身份，撞了不报错，表现是"某只电池拖着拖着变成另一只"。
现在 `gh/probe` 有一条断言数 GUID 唯一性（23 个 GUID / 23 个唯一），撞了就 FAIL。

**2. 两处写死路径。**
- `gh/probe` 导出图标对照图的路径写死 `F:/CodingArt/…`（原机器）→ 现在从产物目录上溯 5 级推仓库根，取不到就落临时目录
- `mock/replay-csharp.mjs` 读 patch 的路径写死 `C:/Users/ASUS/AppData/Local/Temp/…` → 改成 `os.tmpdir()`

这两处都只在"换一台机器"时炸，而它们本身是验证工具 —— 验证工具自己坏了，人会以为是产品坏了。

### 验证（本机实测）

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 错误 0 警告**（SDK 10.0.402 编 net7.0-windows） |
| `gh/probe`（对**构建输出**） | **RESULT: PASS**，17 只电池 + 6 个参数类型，icon 全 24×24 |
| `gh/probe`（对**安装后的 .gha**） | RESULT: PASS，`loaded: GHCE 0.10.0.0` |
| 相机数学断言 | 5 条全过：fit s=10 / 中心对齐 / Y 翻转 / 固定 s=5 / margin s=3.6 中心对齐 |
| 显式图层进协议 | `order42=True multiply=True opacity0.5=True` |
| `mock/verify.mjs` | **38/38** |
| `mock/replay-csharp.mjs` | **14/14**（新增"显式图层 order/opacity/blend 落地"一条） |
| 已安装 | 166,912 bytes，md5 `FEE812170ED8`，`GHCE 0.10.0.0` |

**仍未在 Rhino 里验的**：四只新电池的实际手感（尤其 `Project to 2D` 的 `Bounds` 接法）、
样式管道在真定义里的连线、以及新图标在 GH 里画出来是什么样。
`%TEMP%\ghce-icons.png`（或右键 `Render Output` → 图标自检）能一次性看全 12 个图标。

---

# v0.11.0 · 阶段 3 收尾 + PSR 修好

## 1. 你报的三个问题，根因分别是

### a) PSR"卡在最终位置、没有过程"

**根因一**：`Transform` 只输出终态矩阵。指令里只有 `matrix`，运行时拿到就直接 `ctx.transform(m)`，
第 0 帧和第 60 帧的画面完全一样 —— 不是动画没生效，是**根本没有中间态可演**。

**根因二**（坐标不对的那半个）：`PathConversion.FromCurve` 投影时用的是自己那套内联数学，
**从来没给 `PathDef.Proj` 赋值**。于是走 `Curve → Path` 快捷方式的路径进入 `Transform` 时，
`ComposePageMatrix` 拿不到 `Proj`，退化到不带锚点的 `F·M·F⁻¹` 分支，锚点就飞了。

**修法**：
- 协议加 `transform.progress`（0 = 原位，1 = 终态，缺省 1 所以旧定义行为不变）
- `Transform` 电池加第 3 个输入 `Progress`；`Timeline` / `Oscillator` 的 `Field` 写 `xform` 也能驱动它
- 插值用 **TRS 分解**（`runtime/src/xform.js`）：先把矩阵拆成 平移/旋转/缩放，各自插值再合成。
  六分量线性插值在旋转 180° 时会塌成零缩放，这是必须避开的坑
- `xform.js` 在 `p ≥ 1` 时**原样返回传入矩阵**，不做往返换算，避免浮点漂移
- `PathConversion.FromCurve` 改成走 `CameraMath.Fit` + `CameraMath.ToPage`，并**一定写上 `Proj`**

### b) `Timeline.Field` 看不懂

以前 `Field` 是 `1–6` 的整数，端口说明里才有对照表 —— 等于要求用户背表。
现在改成**直接写字段名**：`reveal` / `opacity` / `moveX` / `moveY` / `rotate` / `scale` / `xform`。
`AnimFields.Resolve` 同时接受名字和 1–7 序号，所以旧 `.gh` 打开照样能跑；
写了不认识的名字会 Warning 并回退到 `reveal`，不静默吃掉。

**它是什么**：一条轨道只描述**一个数值怎么随时间变**，`Field` 就是"这个数值要喂给指令的哪个属性"。
所以它要跟指令分开 —— 轨道是"时间 → 数值"的曲线，指令是"画什么、怎么画"。
两者的生命周期不同：改一下颜色不该让动画重来，换一条轨道也不该重发几何。

### c) `Path Input` 输入 Page Width/Height 爆红

`Data conversion failed from Number to GHCE Camera` —— 这是 v0.10 的**故意破坏性改动**：
`Path Input` 从 6 个输入收到 4 个，原来的第 2/3 个（Page Width / Page Height）位置现在是 `Camera`。
旧定义里那两根线还插在原位置，于是数字往 `Camera` 参数里灌，必然报这个错。

**怎么修**（二选一）：
1. 删掉 `Path Input` 上连到第 2/3 口的线 —— 不接相机它就用缺省 `1440×900 / margin 60`
2. 接一台相机：`Project to 2D`（给几何 + 页宽 + 页高 + 边距）→ `Camera` → `Path Input` 第 2 口

第二种才是正路：页面上所有指令共用同一台相机，缩放才对得齐。

## 2. 新增四样东西

| 电池 | 端口 | 干什么 |
|---|---|---|
| `Oscillator` | 6 | 永久摆动。`Field` + 波形（sin/tri/saw/square）+ 频率 + 幅度 + 中心。给一次值，跑到天荒地老 |
| `Event Trigger` | 4 | 事件 → 动作。click / dblclick / keydown × clear / clear-region / restart-time / restart-objects / toggle-play |
| `Transform` | 3 | 多了 `Progress` |
| `Render Output` | 6 | 多了 `Events` 口 |

运行时侧：
- `runtime/src/osc.js` —— `waveValue` / `oscValue` / `applyOscillators`（三角波用 `asin(sin)` 得到，不用查表）
- `runtime/src/events.js` —— 事件表 + 按区域擦除；`clear-region` 的 detail 就是 `x,y,w,h`
- `renderer2d.clearRegion()` 同时清掉 `getImageData` 意义上的像素（验收里直接读 alpha 判的）
- **求值顺序**：时间线先写、振荡器后写（后者赢）。振荡器是"持续驱动"，时间线是"一次编排"
- 振荡器只在 `clock.playing` 时求解 —— 暂停就是真停，不会偷偷继续摆

`applyField()` 是唯一写字段的地方（`anim.js`），时间线和振荡器都过它。
这样加一个新字段只改一处，不会出现"时间线认得、振荡器不认得"。

## 3. 验证（本机实测）

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 错误 0 警告** |
| `gh/probe` | **RESULT: PASS**，19 只电池，`guids: 26 个，唯一 26 个` |
| 协议断言 | `progress0.5=True osc=True event=True`、`anim fields: 9/9 解析正确` |
| `mock/verify-v011.mjs`（新） | **13/13**：progress 随时钟推进 0.083→0.333、SVG 同步到 30°、振荡器 ±20° 摆动、clear-region 左上 alpha=0 / 右下保留 |
| `mock/verify-xform.mjs`（新） | **15/15** 纯数学：分解-合成往返、180° 不塌、四种波形、offset 就是摆动中心 |
| `mock/verify.mjs` | **38/38** |
| `mock/replay-csharp.mjs` | **15/15** |
| 构建产物 | 187,904 bytes，md5 `4BE30FFD5D70`，`GHCE 0.11.0.0` |

**已安装**：`%APPDATA%\Grasshopper\Libraries\GHCE.gha`，187,904 bytes，md5 `4BE30FFD5D70`。
`GHCE_BIN=%APPDATA%\Grasshopper\Libraries` 对**安装后那份**跑探针 → `loaded: GHCE 0.11.0.0`、RESULT: PASS。
装之前 Rhino 正开着（PID 6776 / 8920）锁了文件，用 `install.ps1 -Force` 结束进程后装上去的
（用户确认过会丢未保存定义）。**下次改版本记得先关 Rhino，别走 -Force。**

### 两条验收脚本自己写错的地方（记下来，别重犯）

**1. `verify-v011.mjs` 的 progress 断言窗口定错了。**
轨道只有 2 秒，页面加载后已经跑了 1.2 秒（progress 0.58），再睡 1 秒自然跑满到 1，
断言 `progress < 0.95` 就 FAIL。**是断言错，不是运行时错**。
改成先把时钟归零、留两帧、再采样两次 —— 测的是"同一段轨道内进度跟着时间走"，
而不是"页面开了多久"。这类"拿墙钟当自变量"的断言，一律要先把自变量按住。

**2. `replay-csharp.mjs` 的矩阵断言还停在 progress 不存在的年代。**
夹具里 `progress = 0.5`（探针故意写一半），所以 SVG 里应该是**插值后的 45°**，
旧断言却要求原样的 90° `matrix(0,-1,1,0,10,-20)`。
现在改成两条：`transform.progress 原样落地` + `SVG 矩阵 = 按 0.5 插值后的 45°`，
数学本身由 `verify-xform.mjs` 单独负责，两层不重复。

## 4. 一个已知的语义边界

带 `matrix` 的指令上挂振荡器：`moveX/moveY/rotate/scale` 是**叠在矩阵之后**的标量层
（`renderer2d` 里先 `g.transform(m)` 再 `g.translate/rotate/scale`，SVG 同理拼 `matrix() rotate()`），
所以能一起用；但 `xform` 字段改的是 `progress`，它会重算矩阵本身 —— 两者别同时用来控制同一件事。

---

# v0.12.0 · 变换自己演 + 事件绑到指令 + 预览窗口三处修

这一版是**改语义**，不是加功能。四件事，前三件是你明确指出的问题。

## 1. `Transform` 去掉了 `Progress`，改成"每个指令配一个 Xform"

之前的模型是"给一条指令一个终态矩阵 + 一个全局进度"，等于让用户自己拧进度条 —— 但
**变换本身就是那段动画**，进度是浏览器的事，不该出现在电池上。现在：

```
Cmds(25 个方块) ─┐
Xform(25 个)   ──┴─ Transform ──► Cmds ──► Render Output
```

- `Xform` 是 **list**：给 1 个则全部共用，给 N 个按序对应（不够的沿用最后一个）
- 输出里每条指令带自己的矩阵，`Progress` 恒为 1（终态）
- 鼠标一改 `Xform`（或上游曲线一动），浏览器就在 `0.9 s` 内从**当前画面上的位置**演到新位置
- 连续拖动滑块 = 平滑跟随，因为起点取的是插值中的当前值，不会跳回上一段的起点
- 首帧不演：指令第一次出现直接落在终态，否则刷新页面时全场会从原点飞一遍

`runtime/src/tween.js` 干这件事。三种"谁在控制 transform"的优先级：
时间线 / 振荡器显式写 `xform` 时过渡引擎完全让位（`cmd._xformDriven`），否则过渡引擎接管。

## 2. `Event Trigger` 改成管道，事件挂在指令上

之前是"规则表 + 第六个端口"，等于预设了"事件是全局的"。现在：

```
Cmds(任意几条) + Event + Action + Detail ──► Event Trigger ──► Cmds
```

喂进去哪几条，命中的就只有那几条。动作分两类：

| 作用域 | 动作 | 说明 |
|---|---|---|
| 逐指令 | `toggle` / `show` / `hide` | 改这批指令的 `visible` |
| 逐指令 | `restart` | 让这批指令的变换从头再演 |
| 整屏/时钟 | `clear` / `clear-region` / `reset-time` / `toggle-play` | 画布只有一张、时钟只有一个，作用域天然全局 |

- 规则 id = `事件|动作|细节|排序后的目标集` 的指纹，换一组指令就是另一条规则
- 同一条规则挂在 25 条指令上时，PatchBuilder 只发**一条**规则 + `targets` 数组，不是 25 条重复
- `Render Output` 的第 6 口（Events）删掉了 —— 事件跟着指令走，两种入口只会让人猜
- 旧名兼容：`restart-time` → `reset-time`，`restart-objects` → `restart`，默默映射不报警

## 3. 预览窗重新"先空白、拖动才出现"

**根因是握手顺序，不是渲染。** `RequestResync()` 只是置了个位，而这个位只在**下一次 Build** 时被读取；
页面连上来的时候 GH 并不会重算，所以那一帧永远补不上 —— 直到你动一下数据。

修法：桥接上新客户端时，除了置位，还要在 UI 线程把 `Render Output` expire 一次
（`Ui/ResyncTicker.cs`，80 ms 的 WinForms Timer，只 expire 自己，不 NewSolution 整张图）。
页面自己发 `hello` 时也补一次，覆盖"窗口比第一次握手还晚"的情况。

## 4. 预览窗置顶

WPF 的 `Window.Topmost` —— **注意是小写 m**，WinForms 才叫 `TopMost`。
写成 `TopMost` 会报 `CS0117: Window 未包含 TopMost 的定义`，而这个错很容易被误判成"引用没加"。

## 5. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 错误 0 警告 |
| `gh/probe` | **RESULT: PASS**，`Transform in=2`、`Event Trigger in=5 out=1`、`Render Output in=5`，26 个 GUID 全唯一 |
| 协议断言 | `v0.12 协议: progress0.5=True osc=True event+targets=True` |
| `mock/verify-tween.mjs`（新，纯 Node） | **11/11**：首帧不演 / 中途插值 / 演完归位 / 连续变化不跳回 / 显式驱动时让位 / restart 重演 / 最短弧 |
| `mock/verify-v012.mjs`（新，真浏览器） | **13/13**：25 个方块改 5 个只有那 5 个在演（p=0.198）、其余 20 个 p=1、演完归 1、SVG 同步 22.5°、点击只影响目标 3 条、restart 归零 |
| `mock/verify.mjs` | 38/38 |
| `mock/replay-csharp.mjs` | 15/15 |
| `mock/verify-v011.mjs` | 13/13（旧动作名兼容后仍全绿） |
| `mock/verify-xform.mjs` | 15/15 |

---

# v0.13.0 · 删掉"预设动画"，只留"GH 怎么变就怎么演"

## 1. 你看到的微旋转和缩放，是我预设的

具体来源，写在这里让你可以核对：

- `Oscillator` 电池默认 `Field = rotate`、`Amp = 15` —— **一拖出来就在 ±15° 摆**，不用连任何东西
- `Oscillator` 默认还有 `Freq = 0.5`、`Wave = sin`，于是它看起来像"轻微旋转的入场动画"
- `Timeline` 电池默认 `Field = reveal`，配 `Keyframe` 的默认 `Loop = true`，选了 `scale`
  就是"预设的缩放动画"
- 更早的 v0.11 我甚至在说明里让你"接一个 Oscillator(Field=rotate, Amp=20)"，那是把这个坑推给你

**这三个电池已删除**：`Timeline` / `Keyframe` / `Oscillator`。
面板从 19 只变 16 只，`gh/probe` 里也确认它们不在程序集里了。

## 2. 现在的动画规则只有一条

```
cmd = 原始几何 + 样式          ← 你在 GH 里画的
matrix = 变化后的样子          ← Transform 给的
中间那些帧 = 浏览器补          ← tween.js
```

- **第一次出现**：从原始状态（identity）演到 GH 给的位置 —— 这就是"黑线变绿线"的过程
- **之后变了**：从"此刻画面上的位置"演到新位置，0.9 s
- **不再有 Field、不再有 progress 输入**。你在 GH 里怎么改数据，画面就怎么变过去

## 3. 顺手修掉一个隐性问题：浮点抖动

GH 每次重算产出的矩阵在数学上一样，但最后几位浮点可能不同。之前只要有一个 bit 不同就当成
"变了"，于是画面会永远卡在一个几乎看不见的微旋转里 —— 现在有阈值（旋转/缩放 1e-5、平移 1e-3），
小于阈值一律当没变。

## 4. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 错误 0 警告 |
| `gh/probe`（构建产物 + 安装后 .gha） | **RESULT: PASS**，16 只电池，`guids 23 个 / 唯一 23 个`，`loaded: GHCE 0.13.0.0` |
| `mock/verify-tween.mjs` | **13/13**（新增"第一次出现从 0 演"、"浮点抖动不触发"） |
| `mock/verify-v012.mjs` | **14/14**（新增"新指令第一次出现时从 0 开始演"） |
| `mock/verify.mjs` | 38/38 |
| `mock/replay-csharp.mjs` | 15/15 |
| `mock/verify-v011.mjs` | 13/13 |
| 已安装 | 194,048 bytes，md5 `E23A8EA04747` |

**回归时改了两条旧断言**（不是产品错，是语义变了）：
`replay-csharp` 里"progress 原样落地 0.5"改成"入场演完后 progress = 1"，
"SVG = 按 0.5 插值的 45°"改成"SVG = C# 发来的终态 90°"。
插值数学归 `verify-tween` / `verify-v012` 管，回放只验两端一致。

# v0.14.0 · 抖动和锚点：两个问题其实是同一个错

你报的是"过程中反复抖 + 锚点不是我给的那个"。追下去发现是**一处数学错**加**两条触发逻辑错**，
三条合起来才是你看到的样子。

## 1. 锚点：矩阵里本来就带着锚点，是我把它丢了

一个"绕 (160,620) 转 45°"的矩阵长这样：

```
M(p) = A·p + τ        A = 旋转部分，τ = 平移部分
```

**只插值 `rot` 和 `τ` 是错的。** 因为 τ 里混了两样东西：真正的位移，加上"旋转中心不在原点"
造成的补偿。把它们当纯位移去线性插值，等于让物体一边绕页面原点转、一边沿直线往目标位置跑 ——
看起来就是"锚点不对"和"中间在抖"。

正确做法：解出这个仿射映射的**不动点**

```
C = (I − A)⁻¹ · τ       ← 锚点
```

然后按 `T(t)·T(C)·R(θ)·S(s)·T(−C)` 逐段插值（`C` 自己也跟着插值）。
不动点在变换中保持不动，所以锚点不漂移、中间不摆。

- 纯平移（`I − A` 奇异）没有不动点，退化成 `C = 0`，位移全走 `t`
- 起点是 identity 时，`C` 直接取终态的锚点 —— 于是"原始状态 → 变换后"是**绕你自己的锚点**转过去的
- 这个推法有个额外好处：**锚点不需要额外输入**。`Transform` 电池给的矩阵只有 6 个数，
  但锚点被数学唯一确定了，你用什么中心转的，它就用什么中心转

## 2. 抖动一：浮点漂移（v0.13 已修，这里记全）

GH 每次重算的矩阵数学上相同，末位浮点可能不同。v0.13 加了 `NOISE` 阈值
（旋转/缩放 `1e-5`、平移 `1e-3`）。

## 3. 抖动二：逐帧驱动被当成"每一次都是新变化"

这是 v0.13 之后残留的那层抖。如果你用 `Time` / `Timer` 电池逐帧喂值，
每帧矩阵都真的在变 —— 于是引擎每帧都重新起一段 0.9 s 的缓动，永远走不到头，
画面上就是持续的高频抖。

现在：**两次变化间隔 < 120 ms（`CONTINUOUS_GAP`）就认定你在逐帧驱动，直接直通**
（`progress = 1`，给什么画什么，不叠任何缓动）。这同时就是 `void draw()` 的语义 —— 见下。

顺带把 `same()/moved()` 的提前返回挪到了间隔判断**之前**，否则没变化的帧也会污染
`lastTarget`，让下一帧的间隔算出负数。

## 4. 关于 loop / `void draw()`

`Timeline` 删除后确实没有"播放头"了。原因要说清楚：**60 fps 的循环，值必须一秒被产出 60 次。**
只有两个地方能产出：

| 谁产出 | 怎么做 | 代价 |
|---|---|---|
| GH（逐帧解算） | 原生 `Timer`（`Params › Util`，`GH_Timer`）→ 你的表达式 → `Transform` | 受 GH 解算速度限制，实际 10–30 Hz，画面台阶式 |
| 浏览器（本地时钟） | 把"一段循环"烘焙成 N 帧矩阵发给浏览器，由它在帧间插值 | 需要协议扩容（一条指令收一串矩阵） |

当前 v0.14 已经能吃第一种（`CONTINUOUS_GAP` 直通）。第二种是真正能到 60 fps 的路，
还没做 —— 但它是"你在 GH 里算 N 帧、浏览器负责播"，仍然没有 Field、没有预设曲线。

## 5. 验证（本机实测）

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 错误 0 警告 |
| `gh/probe` | **RESULT: PASS**，16 只电池，`guids 23/23 唯一`，`transform anchor / orbit / shift` 三项全 ok |
| `mock/verify-tween.mjs` | **18/18**（新增锚点不漂移、22.5° 中点、逐帧直通） |
| `mock/verify-v012.mjs` | **14/14** |
| `mock/verify.mjs` | **38/38** |
| `mock/replay-csharp.mjs` | **15/15** |
| `mock/verify-v011.mjs` | **13/13** |
| 构建产物 | 196,608 bytes，md5 `e2f0e337953a` |

最关键的三条断言：

```
PASS  绕自身中心转时，锚点在 1/4 / 1/2 / 3/4 处都不漂移  → [0,0,0]
PASS  中点角度是 22.5°（不是先跑到别处再回来）  → 22.5
PASS  逐帧喂数据时直通（不叠 0.9 秒缓动）  → {"active":0,"p":1,"x":50}
```

**踩到的坑**：`verify.mjs` 一开始 8 项 FAIL，原因不是代码 —— 页面默认连 `ws://127.0.0.1:8777`，
而那是当时**正开着的真 Rhino**，不是 mock 桥。必须 `PAGE="...?ws=ws://127.0.0.1:8788/ghce"`。
症状误导性很强（"模拟桥下显示演示场景按钮 → false"，像是 UI 坏了），实际是连错了桥。


# v0.15.0 · 把实体层的"预设"拆掉

## 1. 你点的三处冗余，其实是同一条病

| 你的话 | 实际问题 |
|---|---|
| "entities behavior 的预设很冗余，只有沿曲线的粒子才有效果" | `Mode` 是 1–6 的下拉，其中 5 个没有可复用性，等于我替你决定了可能性的全集 |
| "entities look 粒子对象都无法设置" | 只能改颜色/大小，形状写死是方块 |
| "blueprint 是粒子的一些随机，预设的不太好" | `Seed` + `Vary` 让"特定规则"根本做不到 |

共同点：**这些端口在替你选择"要哪种效果"，而不是在传你的数据。**
判断标准记在这里，以后自查：端口只该传数据，不该传选项。

## 2. 实体层：3 只电池 → 2 只

```
v0.14  Blueprint(Path,Count,Speed,Lifetime,Seed,Vary) → Behavior(BP,Mode,Amount) → Look(BP,Col,Size,Trail,Layer,Fade)
v0.15  Blueprint(Path,Count,Speed,Lifetime,Phase)     → Look(BP,Shape,Colour,Width,Size,Trail,Layer,Fade)
```

- **`Entity Behavior` 电池删除。** 蓝图第一个输入就是 `Path`，"沿这条曲线跑"就是唯一语义，
  再拿一只电池去"换模式"是多余的一层。`runtime/entity.js` 里 `BEHAVIORS` 只剩 `['follow-curve']`，
  `_initDrift` / `_initOrbit` / 鼠标吸引分支全部删除，`mulberry32` 也删了。
- **分布改成确定式**：第 i 个粒子 `u = (i / Count + Phase) % 1`，`age = (i/Count)·Life` 错开重生。
  没有种子、没有抖动。要每个粒子不一样，在 GH 里给不同 `Speed` / 多条蓝图。
- **`Lifetime = 0` 现在表示"永不重生"**（跑完一圈接着跑），不再是"取最小值"。
- **`Look.Shape`**：接 Rhino 里**单条**曲线（`Param_Curve`，item 访问；接多条 GH 直接报错 ——
  这是故意的，一份蓝图只能有一种粒子形状）。曲线被采样后**以 bbox 中心为原点、最长边归一化成 1**，
  所以 `Size = 1` 就是"跟原曲线一样大"，与画布缩放无关。`Width` 是描边宽度（像素）。
  不给 Shape → 回退方块点。
- 形状和几何用同一套 `f32 + base64` 编码（`ShapePayload`），因为形状可能上千个点，
  走 JSON 数字数组会把带宽吃掉。

## 3. 时间：轨道只写 phase

`anim.js` 里发给实体的轨道不再写 `amount`（行为强度，已经没有行为可选了），改成写 `phase`：
整组粒子沿曲线往前挪。这就是"时间"进入实体系统的唯一入口，没有第二个。

## 4. 验证（本机实测）

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 错误 0 警告 |
| `gh/probe`（安装后 .gha） | **RESULT: PASS**，16 只电池，`guids 23/23 唯一`，`track in patch: True` |
| `mock/verify.mjs` | **49/49**（v0.16：时间轨 25 帧 / 缓动 33 点 / 生长按弧长截断；v0.17：PNG 序列 zip / 独立 HTML 离线跑 / WebM；clear-region） |
| `mock/verify-plugin-serve.mjs` | 7/7（插件自供页面 → 60 条路径入库并绘制，无控制台错误） |
| `mock/verify-tween.mjs` | 18/18 |
| `mock/verify-xform.mjs` | 16/16（v0.16 起改验时间轨 Duration/Loop/Ease，波形断言随 osc.js 删除） |
| `mock/verify-v012.mjs` | 14/14 |
| `mock/replay-csharp.mjs` | 15/15 |
| ~~`mock/verify-v011.mjs`~~ | 已删除：它验的是 `Timeline.Field` 与 `Oscillator`，这两个东西在 v0.16 已经不存在了。`clear-region` 那条断言搬进了 `verify.mjs` |
| 安装产物 | 194,560 bytes，md5 `69305A8CD466` |

关键三条：

```
PASS  同一蓝图内粒子沿曲线均匀铺开（步长 = 1/Count）  → {"step":0.033,"expect":0.0333}
PASS  写 phase 让整组沿曲线前移（时间就是这么进来的）  → {"before":0.057,"after":0.5}
PASS  带 Shape 的蓝图拿到三角形形状（3 点、闭合）  → {"pts":3,"closed":true}
```

**形状是目检过的**：把 count 调到 10、size 调到 30、speed 归零后重新截图，
带 Shape 的蓝图是三角形、不带的是方块，都沿各自曲线均匀铺开。
（第一次截图看不出来 —— 我改 `count` 时没有清空实例列表，`u` 还是按老的 count 算的，全叠在一起了。
改参数后必须 `list.length = 0` 再 `_resize`。）

## 5. 回归里被改掉的断言（语义变了，不是产品错）

- "三种行为并存（drift + orbit + attract）" → "三条蓝图并存（同一种语义，只差参数）"，id 换成 `e_slow/e_fast/e_life`
- "attract 行为把粒子拉向指针" → 删除；换成"均匀铺开"和"写 phase 前移"
- mock 的 `living` 场景改成三条 `follow-curve` 蓝图，用速度/相位/寿命表达三种观感
- `snapshot().meanDistTo` 随 attract 一起删除（没有调用者了）
