using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Colugo.Types
{
    // ---------------------------------------------------------------------
    // 数据定义（与 docs/protocol-v0.1.md 第四节的字段一一对应）
    // 这一层只放"纯数据"，不放行为；行为在 Runtime 侧。
    // ---------------------------------------------------------------------

    public sealed class CanvasDef
    {
        public int W = 1200;
        public int H = 800;
        public string Bg = "#0b0b0f";
        public double Dpr;              // 0 = auto（运行时按设备像素比，上限 2）
        public bool Transparent;        // true 时不发背景色

        public CanvasDef Duplicate() => (CanvasDef)MemberwiseClone();
    }

    public sealed class StrokeDef
    {
        public string Color = "#E8E6DF";
        public double Width = 1.0;
        public double Alpha = 1.0;
        public string Cap = "round";     // butt | round | square
        public string Join = "round";    // miter | round | bevel
        public double[] Dash;            // null = 实线

        public StrokeDef Duplicate() => (StrokeDef)MemberwiseClone();
    }

    public sealed class FillDef
    {
        public string Color;             // null = 不填充
        public double Alpha = 1.0;
        public string Rule = "nonzero";

        public FillDef Duplicate() => (FillDef)MemberwiseClone();
    }

    /// <summary>
    /// 相机：世界 → 页面的一次性映射。V0.9 之前这组数硬编码在 Path Input 里，
    /// 结果是"几何一变相机就重算"，细长条转 90° 会被裁。
    /// 抽成独立对象后，相机可以只算一次、整个动画共用一套框。
    /// </summary>
    public sealed class CameraDef
    {
        public int W = 1440;
        public int H = 900;
        public double Margin = 60;
        public double Scale;              // 0 = 按包围盒自动拟合
        public double[] Bounds;           // 世界包围盒 [minX,minY,maxX,maxY]，null = 由几何推出

        // 求解结果：[s, ox, oy]，px = s*X + ox，py = -s*Y + oy
        public double[] Proj;
        public double[] Resolved;         // 实际用到的包围盒，供 Info 显示

        public CameraDef Duplicate() => (CameraDef)MemberwiseClone();

        public CameraDef Solved(double[] proj, double[] bounds)
        {
            var c = Duplicate();
            c.Proj = proj;
            c.Resolved = bounds;
            return c;
        }
    }

    /// <summary>
    /// 图层定义。V0.9 之前顺序是"首次出现序号 × 10"的隐式推导，
    /// 多图层海报里"谁在上面"不可控。现在由 Layer 电池显式给出。
    /// </summary>
    public sealed class LayerDef
    {
        public string Name = "main";
        public double Order;
        public double Opacity = 1.0;
        public string Blend = "source-over";
        public bool Visible = true;

        public static readonly string[] Blends =
        {
            "source-over", "multiply", "screen", "overlay", "lighter",
            "difference", "exclusion", "hard-light", "soft-light",
            "color-dodge", "color-burn", "darken", "lighten"
        };

        public LayerDef Duplicate() => (LayerDef)MemberwiseClone();

        public static string NormalizeBlend(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "source-over";
            var t = s.Trim().ToLowerInvariant();
            return Array.IndexOf(Blends, t) >= 0 ? t : "source-over";
        }

        public bool BlendIsKnown(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            return Array.IndexOf(Blends, s.Trim().ToLowerInvariant()) >= 0;
        }
    }

    public sealed class PathDef
    {
        public string Id;                // 稳定 ID，由几何指纹生成，不依赖数据树路径/索引
        public bool Closed;
        public bool IsPoints;            // 点集：不连线，按 pointSize 打点
        public double[] XY;              // 已投影到页面坐标：[x0,y0,x1,y1,...]

        /// <summary>
        /// 世界 → 页面的相机参数 [s, ox, oy]：px = s*X + ox，py = -s*Y + oy。
        /// 只在 GH 内部用，不进协议。Transform 靠它把世界里的矩阵共轭到页面：
        /// 少了这组数，绕几何中心的旋转会被当成绕页面原点旋转，锚点直接飞掉。
        /// </summary>
        public double[] Proj;

        /// <summary>
        /// 仿射帧序列（v0.21）：N*6 个页面矩阵，由 Transform 电池写。
        /// 放在 Path 上而不是直接挂在 Cmd 上，是因为 Transform 工作在**几何层** ——
        /// 它不关心这条路径最后被画成什么样式，只负责"这条几何在时间上怎么变"。
        /// Draw Path 才把这三样（几何 / 矩阵帧 / 顶点帧）合成为一条绘图指令。
        /// </summary>
        public double[] MatrixFrames;
        public int MatrixN;

        /// <summary>单个静态矩阵（6 个数）：只给了一个 Xform 时用它，走老的 transform.matrix 路径，不建轨道。</summary>
        public double[] Matrix;

        /// <summary>顶点帧序列（v0.21）：由 Morph 电池写。与 MatrixFrames 二选一，不串联。</summary>
        public MorphDef Morph;

        public PathDef Duplicate()
        {
            return (PathDef)MemberwiseClone();
        }
    }

    public sealed class CommandDef
    {
        public string Id;
        public string GeomId;
        public PathDef Path;             // 几何引用（同一 PathDef 可被多条指令共享，只传一次）
        public string Layer;
        public LayerDef LayerDef;        // 可选：显式图层定义（来自 Layer 电池）；null = 隐式推导
        public int Order;
        public bool Visible = true;
        public double Opacity = 1.0;
        public double Reveal = 1.0;      // Stroke Reveal：0..1，沿路径画到哪
        public TrackDef Track;           // 时间轨：帧序列 + 区间/缓动（v0.18）
        public MorphDef Morph;           // 几何变形帧序列（v0.19）：任意参数扫成的 N 份同拓扑几何

        /// <summary>
        /// 全局时间轴。只由 Timeline 电池写，且同一帧里每条指令的值相同；
        /// PatchBuilder 取第一份非空值发一条 `timeline.set`，所以它搭在 cmd 上只是为了
        /// 少一个端口，语义上属于文档级。
        /// </summary>
        public TimelineDef Timeline;

        // 变换（PSR）：位置 / 缩放 / 旋转 + 锚点
        public double MoveX, MoveY;
        public double Scale = 1.0;
        public double Rotation;          // 度
        public double AnchorX, AnchorY;
        public double[] Matrix;          // 2D 仿射 [a,b,c,d,e,f]，来自 GH 的 Transform 对象

        /// <summary>
        /// 变换进度：0 = 原位（identity），1 = Matrix 的终态。
        /// 为什么需要它：Matrix 是"终态"，直接套上去画面就卡在结果上，没有"从原状变过去"的过程。
        /// 浏览器侧在 0..1 之间按 TRS 分解插值（线性插值矩阵分量会在 180° 处塌成零缩放）。
        /// </summary>
        public double Progress = 1.0;

        /// <summary>
        /// 挂在这条指令上的事件规则（来自 Event Trigger 电池）。
        /// 事件跟着指令走，所以"任意组合的命令"都能各自成组：
        /// 25 个方块里挑 8 个喂进 Event Trigger，点一下只影响那 8 个。
        /// </summary>
        public List<EventDef> Events;

        /// <summary>
        /// 属性绑定（v0.30）：属性名 → 表达式。绘制时求值，不写回本对象。
        /// 目前支持 width / opacity 两个属性 —— 先把一条路走通，再谈颜色与进度。
        /// </summary>
        public Dictionary<string, RuntimeExprDef> Binds;

        /// <summary>
        /// 这条指令在源路径上的位置，0..1（v0.31，Mark 专用）。
        ///
        /// 为什么不进协议：它只是 GH 侧 Style Map 的输入 —— "越往后越粗" 这种映射
        /// 在求解期就算完了，浏览器不需要知道某个标记来自路径的哪个位置。
        /// 小于 0 表示这条指令不是 Mark（Draw Path 生成的），Style Map 会跳过它。
        /// </summary>
        public double MarkU = -1.0;

        /// <summary>图片几何（v0.33）：与 Path 二选一，二者都为空时这条指令画不出东西。</summary>
        public ImageDef Image;

        /// <summary>裁剪（v0.33）：页面坐标的模具，不跟着指令的变换走。</summary>
        public ClipDef Clip;

        public StrokeDef Stroke = new StrokeDef();
        public FillDef Fill = new FillDef();
        public double PointSize = 3.0;

        public CommandDef Duplicate()
        {
            var c = (CommandDef)MemberwiseClone();
            c.Stroke = Stroke.Duplicate();
            c.Fill = Fill.Duplicate();
            if (Image != null) c.Image = Image.Duplicate();
            if (Clip != null) c.Clip = Clip.Duplicate();
            if (LayerDef != null) c.LayerDef = LayerDef.Duplicate();
            if (Track != null) c.Track = Track.Duplicate();
            if (Binds != null)
            {
                c.Binds = new Dictionary<string, RuntimeExprDef>(Binds.Count);
                foreach (var kv in Binds) c.Binds[kv.Key] = kv.Value?.Duplicate();
            }
            if (Events != null)
            {
                c.Events = new List<EventDef>(Events.Count);
                foreach (var e in Events) c.Events.Add(e == null ? null : e.Duplicate());
            }
            return c;
        }

        /// <summary>样式指纹：与几何无关，用于判断"只改画笔"时不必重传几何。</summary>
        public string StyleKey()
        {
            return string.Join("|", Order, Visible, Opacity, PointSize, Reveal,
                Stroke.Color, Stroke.Width, Stroke.Alpha, Stroke.Cap, Stroke.Join,
                Stroke.Dash == null ? "-" : string.Join(",", Stroke.Dash),
                Fill.Color ?? "-", Fill.Alpha, Fill.Rule, Layer ?? "-");
        }

        /// <summary>
        /// 给"只有路径、没有画笔"的输入兜一个默认指令（白线 1px，main 层）。
        /// 这是 Path → Command 隐式转换的依据：让人可以先直接预览，再回头接 Draw Path 精修。
        /// </summary>
        public static CommandDef DefaultFor(PathDef p)
        {
            if (p == null || string.IsNullOrEmpty(p.Id)) return null;
            return new CommandDef
            {
                Id = p.Id.StartsWith("g_", StringComparison.Ordinal) ? "c_" + p.Id.Substring(2) : "c_" + p.Id,
                GeomId = p.Id,
                Path = p,
                Layer = "main",
                Order = 0,
                Visible = true,
                Opacity = 1.0,
                Stroke = new StrokeDef(),
                Fill = new FillDef(),
                PointSize = 3.0,
            };
        }
    }

    /// <summary>
    /// 动态对象蓝图（阶段 2）。字段与 docs/protocol-v0.1.md 的 Entity 定义一一对应。
    /// GH 只描述"怎么活"，运行时自己持有 age / u / 拖尾这些状态。
    /// </summary>
    /// <summary>
    /// 一条状态写入：每个固定步对每个实例执行一次。
    /// op 只有 set / add —— 行为本身由表达式决定，不由算子表决定。
    /// </summary>
    public sealed class EntityStepDef
    {
        public string Slot;              // x / y / vx / vy / u / size / alpha / aux / index / gen
        public string Op = "set";        // set | add
        public RuntimeExprDef Expr;
        public RuntimeExprDef When;      // 非 0 才执行；null = 每步都执行

        public EntityStepDef Duplicate() => new EntityStepDef
        {
            Slot = Slot, Op = Op,
            Expr = Expr?.Duplicate(), When = When?.Duplicate()
        };

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["slot"] = Slot, ["op"] = Op };
            if (Expr != null) d["expr"] = Expr.ToJson();
            if (When != null) d["when"] = When.ToJson();
            return d;
        }

        public string Key() => $"{Slot}/{Op}/{Expr?.Key()}/{When?.Key() ?? "-"}";
    }

    /// <summary>
    /// 实例状态程序（v0.32）。三件事：**每步写状态**、**条件满足时生成**、**条件满足时销毁**。
    ///
    /// 为什么不是"行为预设 + 参数"：预设等于替用户决定可能性的全集。
    /// 这里只有"读状态 → 算 → 写状态"，分裂/继承/停留/留痕都是这套规则组合出来的结果。
    /// </summary>
    public sealed class EntityProgramDef
    {
        public List<EntityStepDef> Steps = new List<EntityStepDef>();

        // 生成：条件上升沿触发一次，避免条件持续为真时每帧爆人口
        public RuntimeExprDef SpawnWhen;
        public int SpawnCount = 1;
        public double SpawnSpread;       // 子代绕父代铺开的半径（像素）
        public int MaxGen = 3;           // 0 = 不限制代数
        public int MaxPop;               // 0 = 只用 Blueprint 的 Count

        // 销毁：留痕 = 最后 N 个状态保留成静态残影
        public RuntimeExprDef DestroyWhen;
        public int Keep;

        public EntityProgramDef Duplicate()
        {
            var p = new EntityProgramDef
            {
                SpawnWhen = SpawnWhen?.Duplicate(),
                SpawnCount = SpawnCount,
                SpawnSpread = SpawnSpread,
                MaxGen = MaxGen,
                MaxPop = MaxPop,
                DestroyWhen = DestroyWhen?.Duplicate(),
                Keep = Keep,
            };
            foreach (var s in Steps) p.Steps.Add(s?.Duplicate());
            return p;
        }

        public bool IsEmpty => Steps.Count == 0 && SpawnWhen == null && DestroyWhen == null;

        public string Key() =>
            string.Join(",", Steps.ConvertAll(s => s.Key()))
            + "|" + (SpawnWhen?.Key() ?? "-") + $"#{SpawnCount}@{SpawnSpread:0.###}g{MaxGen}p{MaxPop}"
            + "|" + (DestroyWhen?.Key() ?? "-") + $"k{Keep}";

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            if (Steps.Count > 0)
            {
                var arr = new List<Dictionary<string, object>>(Steps.Count);
                foreach (var s in Steps) arr.Add(s.ToJson());
                d["steps"] = arr;
            }
            if (SpawnWhen != null)
            {
                d["spawn"] = new Dictionary<string, object>
                {
                    ["when"] = SpawnWhen.ToJson(),
                    ["count"] = SpawnCount,
                    ["spread"] = SpawnSpread,
                    ["maxGen"] = MaxGen,
                    ["maxPop"] = MaxPop,
                };
            }
            if (DestroyWhen != null)
            {
                d["destroy"] = new Dictionary<string, object>
                {
                    ["when"] = DestroyWhen.ToJson(),
                    ["keep"] = Keep,
                };
            }
            return d;
        }
    }

    /// <summary>
    /// 图片几何（v0.33）。src 是 data URL，自带尺寸信息。
    ///
    /// 为什么走 base64 而不是本地路径：快照、重连、独立 HTML 导出、离线回放全部免费，
    /// 不需要"预览窗能不能读本地文件"这种平台差异。代价是首帧变大。
    /// </summary>
    public sealed class ImageDef
    {
        public string Id;
        public string Src;            // data:image/png;base64,…
        public int W;                 // 像素宽（信息用，布局由矩阵决定）
        public int H;

        public ImageDef Duplicate() => (ImageDef)MemberwiseClone();

        public string Key() => Id + ":" + W + "x" + H + ":" + (Src == null ? "-" : Src.Length.ToString());
    }

    /// <summary>裁剪：一条页面坐标的闭合路径，可选反相。</summary>
    public sealed class ClipDef
    {
        public string GeomId;
        public bool Invert;

        /// <summary>裁剪轮廓本体。指令只带 id，几何由 PatchBuilder 去重后发送。</summary>
        public PathDef Path;

        public ClipDef Duplicate()
        {
            var c = (ClipDef)MemberwiseClone();
            return c;
        }

        public string Key() => (GeomId ?? "-") + "|" + (Invert ? "1" : "0");
    }

    public sealed class EntityDef
    {
        // 身份与生存规则（Entity Blueprint 电池产出）
        public string Id;
        public PathDef Path;            // 粒子沿哪条曲线跑 —— 就是"行为"本身，不需要单独端口
        public int Count = 200;
        public double Speed = 0.06;
        public double Lifetime = 7;     // 0 = 永不重生，跑完一圈接着跑
        public double Phase;            // 整体相位偏移（0..1）；接时间就是"往前挪"

        // 外观（Entity Look 电池填）
        public PathDef Shape;           // 粒子形状：Rhino 里**单条**曲线；null = 方块点
        public string Color = "#FD7AB4";
        public double Width = 1.0;      // 形状描边宽度（像素）
        public double Size = 1.8;       // 形状缩放（1 = 原始大小，按最长边归一化后）
        public int Trail = 4;
        public string Layer = "particles";
        public bool Fade = true;
        public double Opacity = 1.0;

        /// <summary>状态程序（v0.32）。null = 沿用 v0.15 的"沿曲线跑"。</summary>
        public EntityProgramDef Program;

        public EntityDef Duplicate()
        {
            var e = (EntityDef)MemberwiseClone();
            if (Program != null) e.Program = Program.Duplicate();
            return e;
        }
    }

    /// <summary>
    /// 时间轨（v0.16）。**它就是一段动画本身**：N 个采样矩阵的序列 + 时长 + 是否循环 + 缓动曲线。
    ///
    /// 为什么不是"关键帧 + 字段下拉"：那样等于我预设了"能驱动哪些属性"（reveal / rotate / move…），
    /// 用户要表达的动作被压进我的枚举里。这里只收**矩阵序列** —— 这 N 帧是他在 GH 里自己算的，
    /// 浏览器只负责在帧之间插值并按时间播放。
    ///
    /// 每帧一个 2D 仿射 [a,b,c,d,e,f]（页面坐标），所以 Frames.Length = N*6。
    /// </summary>
    public sealed class TrackDef
    {
        public int N;                    // 帧数
        public double[] Frames;          // N*6，页面坐标；null = 这条轨道只管生长
        public double[] Reveal;          // N 个 0..1；null = 不管生长

        // ---- 这条轨在全局时间轴上的位置（剪辑软件里的入点/出点）----
        // 时长与循环不在这里：那是 Timeline 的事。一条轨只回答"我什么时候开始、什么时候结束"。
        public double Start;             // 入点（秒）
        public double End = -1;          // 出点（秒）；< = Start 表示"占满全局时长"
        public bool Hidden;              // 该轨静默：不参与驱动，指令保持静态状态

        public double[] EaseX;           // 缓动曲线采样（归一化到 0..1），可为 null
        public double[] EaseY;

        public TrackDef Duplicate()
        {
            var t = (TrackDef)MemberwiseClone();
            if (Frames != null) t.Frames = (double[])Frames.Clone();
            if (Reveal != null) t.Reveal = (double[])Reveal.Clone();
            if (EaseX != null) t.EaseX = (double[])EaseX.Clone();
            if (EaseY != null) t.EaseY = (double[])EaseY.Clone();
            return t;
        }
    }

    /// <summary>
    /// A live signal: random or noise, evaluated inside the browser at frame rate.
    ///
    /// This is the second path next to the Random/Noise components, which compute in GH.
    /// The difference is where the number is produced:
    ///   GH components     30 Hz, zero latency, but the whole downstream re-solves
    ///   browser signal    60 Hz, costs GH nothing, arrives back through `stat` with ~100 ms lag
    ///
    /// Carries no samples, only the function: kind, seed, rate, coordinates.
    /// </summary>
    public sealed class SignalDef
    {
        public string Id;                // stable id; the runtime echoes values back under this key
        public string Kind = "noise1";   // random / noise1 / noise2 / noise3
        public int Seed;
        public double Rate = 1.0;
        public double[] Args;

        public SignalDef Duplicate()
        {
            var s = (SignalDef)MemberwiseClone();
            if (Args != null) s.Args = (double[])Args.Clone();
            return s;
        }
    }

    /// <summary>
    /// 几何 morph：N 组**同拓扑**点缓冲，浏览器按时间在相邻帧之间插值顶点。
    ///
    /// 为什么这是"任意 slider 都能动画"的答案：运行时不认识 fillet / offset / 布尔，
    /// 它只知道"第 k 帧的点在这里、第 k+1 帧的点在那里"。所以只要 GH 侧把某个参数
    /// 扫一遍采样成 N 份几何，任何造型变化都能变成动画 —— 与矩阵插值同构，
    /// 只是插值的对象从 6 个数变成 2·Count 个数。
    ///
    /// 唯一前提：**点数与顺序一致**。所以采样用固定点数（归一化参数取点），
    /// 而不是按弦高，否则 offset 之后点数就变了。
    /// </summary>
    public sealed class MorphDef
    {
        public int N;                    // 帧数
        public int Count;                // 每条曲线的点数（所有帧一致）
        public double Step = 12;         // 每秒帧数：这段素材的自然时长 = (N-1)/Step
        public double[] Frames;          // N*Count*2，页面坐标
        public bool Closed;

        public MorphDef Duplicate()
        {
            var m = (MorphDef)MemberwiseClone();
            if (Frames != null) m.Frames = (double[])Frames.Clone();
            return m;
        }
    }

    /// <summary>
    /// 实时信号：随机 / 噪声。
    ///
    /// 这里**不含任何采样值** —— 只有一个函数描述（哪种噪声、种子、速率、坐标偏移）。
    /// 值由浏览器每帧算，因为"跟随时间变化"本质是 f(t)：
    /// 传函数是几十字节，传 60 秒的采样是几十万字节，而且还卡在 GH 的求解频率上。
    /// </summary>
    /// <summary>
    /// 全局时间轴：只有"整段多长、要不要循环"两件事。
    /// 刻意不含任何具体动画 —— 那是 Track 的职责。这里放具体实例，就等于把
    /// 播放头和素材混成一个东西，多轨错位排布就不可能表达。
    /// </summary>
    public sealed class TimelineDef
    {
        public double Duration = 8.0;    // 全局时长（秒）
        public bool Loop = true;
    }

    /// <summary>
    /// 事件触发（阶段 3 D）。一次点击不需要 GH 重新求解 —— 规则发一次，浏览器自己执行。
    /// 选择性清屏（3 C）复用同一套机制，Action 填 clear-region。
    /// </summary>
    /// <summary>
    /// 运行时表达式：一棵在浏览器里求值的树，GH 只负责拼，不求值。
    ///
    /// 为什么是树不是字符串：GH 传来的东西一律当数据，浏览器不 eval、不 new Function；
    /// 算子白名单之外的节点整体不成立，运行时回退到静态值。
    ///
    /// 节点：const / var / time / add / sub / mul / div / min / max / sin / abs / clamp01
    /// </summary>
    public sealed class RuntimeExprDef
    {
        public string Op = "const";
        public double Value;          // const
        public string Name;           // var 的变量名
        public double Init;           // var 的声明初值（第一次 var.upsert 用）
        public RuntimeExprDef A;
        public RuntimeExprDef B;
        public RuntimeExprDef C;      // select 的条件

        public static RuntimeExprDef Const(double v) => new RuntimeExprDef { Op = "const", Value = v };
        public static RuntimeExprDef Time() => new RuntimeExprDef { Op = "time" };
        /// <summary>实例状态：x / y / vx / vy / u / age / index / size / alpha / aux / gen</summary>
        public static RuntimeExprDef State(string slot) => new RuntimeExprDef { Op = "state", Name = slot };
        /// <summary>固定步长（秒）。行为里做积分必须用它，不能用 time 差分。</summary>
        public static RuntimeExprDef DeltaTime() => new RuntimeExprDef { Op = "dt" };
        public static RuntimeExprDef Var(string name, double init) =>
            new RuntimeExprDef { Op = "var", Name = name, Init = init };
        public static RuntimeExprDef Bin(string op, RuntimeExprDef a, RuntimeExprDef b) =>
            new RuntimeExprDef { Op = op, A = a, B = b };
        public static RuntimeExprDef Unary(string op, RuntimeExprDef a) =>
            new RuntimeExprDef { Op = op, A = a };

        public RuntimeExprDef Duplicate() => new RuntimeExprDef
        {
            Op = Op, Value = Value, Name = Name, Init = Init,
            A = A?.Duplicate(), B = B?.Duplicate(), C = C?.Duplicate()
        };

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object> { ["o"] = Op };
            switch (Op)
            {
                case "const": d["v"] = Value; break;
                case "var": d["n"] = Name; d["i"] = Init; break;
                case "state": d["n"] = Name; break;
                case "time": break;
                case "dt": break;
                default:
                    if (A != null) d["a"] = A.ToJson();
                    if (B != null) d["b"] = B.ToJson();
                    if (C != null) d["c"] = C.ToJson();
                    break;
            }
            return d;
        }

        /// <summary>整棵树的稳定指纹，用于判断"绑定改没改"。</summary>
        public string Key()
        {
            switch (Op)
            {
                case "const": return $"c{Value:0.#####}";
                case "var": return $"v{Name}@{Init:0.#####}";
                case "state": return "s:/" + Name;
                case "time": return "t";
                case "dt": return "dt";
                default: return $"{Op}({(C == null ? "" : C.Key() + ",")}{(A == null ? "-" : A.Key())},{(B == null ? "-" : B.Key())})";
            }
        }

        /// <summary>把树里所有 var 节点的 名字→初值 收集出来，PatchBuilder 据此发 var.upsert。</summary>
        public void CollectVars(Dictionary<string, double> into)
        {
            if (into == null) return;
            if (Op == "var" && !string.IsNullOrEmpty(Name) && !into.ContainsKey(Name)) into[Name] = Init;
            A?.CollectVars(into);
            B?.CollectVars(into);
            C?.CollectVars(into);
        }
    }

    /// <summary>一次事件对变量的写入。op：set / toggle / add。</summary>
    public sealed class WriteDef
    {
        public string Var;
        public string Op = "set";
        public double Value;
        public double Min = double.NegativeInfinity;
        public double Max = double.PositiveInfinity;

        public WriteDef Duplicate() => (WriteDef)MemberwiseClone();

        public static readonly string[] Ops = { "set", "toggle", "add" };
    }

    public sealed class EventDef
    {
        public string Id;
        public string Event = "click";   // click | dblclick | key
        public string Action = "toggle";
        public string Detail;            // clear-region: "x,y,w,h" · key: 键名 · 其它: 忽略

        /// <summary>
        /// 这条规则作用在哪几条指令上（指令 id）。
        /// 为什么要有它：事件不是全局开关，而是挂在"任意一组指令"上的 ——
        /// 喂进 Event Trigger 的是哪几条，命中的就只是那几条。
        /// 空数组 = 全局动作（clear / clear-region / reset-time / toggle-play 本来就作用于整个画布或时钟）。
        /// </summary>
        public string[] Targets = new string[0];

        /// <summary>
        /// 写变量（v0.30）。这是比 action 更通用的那条路：
        /// 事件只改 doc.vars，谁读这个变量由表达式绑定决定。
        /// 与 action 可以并存 —— 一个事件既切可见性、又改一个数值。
        /// </summary>
        public List<WriteDef> Writes;

        public static readonly string[] Events =
        {
            "click", "dblclick", "key",
            "pointerdown", "pointerup", "pointermove", "enter", "leave", "wheel"
        };

        /// <summary>前三个是逐指令的，后三个是画布/时钟的（作用域写在端口说明里）</summary>
        public static readonly string[] Actions =
        {
            "toggle", "show", "hide", "restart",
            "clear", "clear-region", "reset-time", "toggle-play"
        };

        /// <summary>这条动作是否只作用于 Targets（其余是整屏/时钟级）</summary>
        public static bool IsPerTarget(string action) =>
            action == "toggle" || action == "show" || action == "hide" || action == "restart";

        /// <summary>
        /// 旧名字 → 新名字。v0.11 的动作表里有 restart-time / restart-objects，
        /// v0.12 起事件绑定到指令上，改叫 reset-time（时钟归零）与 restart（变换重演）。
        /// 老定义不该因为改名就失效，所以这里默默映射，不报警。
        /// </summary>
        public static string NormalizeAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return "toggle";
            switch (action.Trim().ToLowerInvariant())
            {
                case "restart-time": return "reset-time";
                case "restart-objects": return "restart";
                default: return action.Trim().ToLowerInvariant();
            }
        }

        public EventDef Duplicate()
        {
            var e = (EventDef)MemberwiseClone();
            e.Targets = Targets == null ? new string[0] : (string[])Targets.Clone();
            if (Writes != null)
            {
                e.Writes = new List<WriteDef>(Writes.Count);
                foreach (var w in Writes) e.Writes.Add(w?.Duplicate());
            }
            return e;
        }
    }

    // ---------------------------------------------------------------------
    // Goo 包装
    // ---------------------------------------------------------------------
    public class Colugo_Canvas : GH_Goo<CanvasDef>
    {
        public Colugo_Canvas() { }
        public Colugo_Canvas(CanvasDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.W > 0 && Value.H > 0;
        public override string TypeName => "Colugo Canvas";
        public override string TypeDescription => "Canvas definition: size, background, DPR";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Canvas();
            return new Colugo_Canvas(Value.Duplicate());
        }

        public override string ToString() => Value == null ? "<null>" : $"{Value.W}x{Value.H} {Value.Bg}";

        public override bool CastFrom(object source)
        {
            if (source is CanvasDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(CanvasDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Colugo_Camera : GH_Goo<CameraDef>
    {
        public Colugo_Camera() { }
        public Colugo_Camera(CameraDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.W > 0 && Value.H > 0
                                        && Value.Proj != null && Value.Proj.Length == 3;

        public override string TypeName => "Colugo Camera";
        public override string TypeDescription => "World to page camera: page size, margin, scale";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Camera();
            return new Colugo_Camera(Value.Duplicate());
        }

        public override string ToString()
        {
            if (Value == null) return "<null>";
            var p = Value.Proj;
            return p == null
                ? $"{Value.W}x{Value.H} (not solved)"
                : $"{Value.W}x{Value.H} · s {p[0]:0.###} · cam ({p[1]:0.#},{p[2]:0.#})";
        }

        public override bool CastFrom(object source)
        {
            if (source is CameraDef d) { Value = d; return true; }
            if (source is Colugo_Camera c) { Value = c.Value; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(CameraDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Colugo_Path : GH_Goo<PathDef>
    {        public Colugo_Path() { }
        public Colugo_Path(PathDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.XY != null && Value.XY.Length >= 2;
        public override string TypeName => "Colugo Path";
        public override string TypeDescription => "Projected path data in page space";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Path();
            return new Colugo_Path(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Id} [{Value.XY.Length / 2} pts{(Value.Closed ? ", closed" : "")}]";

        public override bool CastFrom(object source)
        {
            // 原生 GH 曲线直接接进 Path 端口：用默认页面（1440×900 / margin 60）就地投影。
            // 需要和 Canvas 尺寸严格对应时，请走 Path Input 电池。
            var curve = source as Rhino.Geometry.Curve;
            if (curve == null && source is Grasshopper.Kernel.Types.GH_Curve ghc) curve = ghc.Value;
            if (curve != null)
            {
                Value = PathConversion.FromCurve(curve);
                return Value != null;
            }
            if (source is PathDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(PathDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Colugo_Command : GH_Goo<CommandDef>
    {
        public Colugo_Command() { }
        public Colugo_Command(CommandDef def) { Value = def; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Id) && !string.IsNullOrEmpty(Value.GeomId);
        public override string TypeName => "Colugo Draw Command";
        public override string TypeDescription => "Draw command: geometry reference, style, layer, order";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Command();
            return new Colugo_Command(Value.Duplicate());
        }

        public override string ToString() => Value == null ? "<null>" : $"{Value.Id} @{Value.Layer}#{Value.Order}";

        public override bool CastFrom(object source)
        {
            // 允许把 Path 直接接到需要 Draw Command 的端口上：
            // 用一个默认样式包一层，这样"只想看一眼"的时候不必先摆一颗 Draw Path。
            if (source is Colugo_Path gp && gp.Value != null)
            {
                Value = CommandDef.DefaultFor(gp.Value);
                return Value != null;
            }
            if (source is PathDef pd)
            {
                Value = CommandDef.DefaultFor(pd);
                return Value != null;
            }
            if (source is CommandDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(CommandDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Colugo_Entity : GH_Goo<EntityDef>
    {
        public Colugo_Entity() { }
        public Colugo_Entity(EntityDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.Path != null && !string.IsNullOrEmpty(Value.Id);
        public override string TypeName => "Colugo Entities";
        public override string TypeDescription => "Entity blueprint. The runtime owns instance state";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Entity();
            return new Colugo_Entity(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Id} ×{Value.Count} @{Value.Layer}";

        public override bool CastFrom(object source)
        {
            if (source is Colugo_Entity e) { Value = e.Value; return true; }
            if (source is EntityDef d) { Value = d; return true; }
            // 只接了一条曲线也能直接当实体用：给一套默认生存规则
            var curve = source as Rhino.Geometry.Curve;
            if (curve == null && source is Grasshopper.Kernel.Types.GH_Curve ghc) curve = ghc.Value;
            if (curve != null)
            {
                var p = PathConversion.FromCurve(curve);
                if (p == null) return false;
                Value = new EntityDef { Id = EntityIdFor(p.Id), Path = p };
                return true;
            }
            // Path 也能直接当实体（默认 200 个、沿曲线走）
            if (source is Colugo_Path gp && gp.Value != null)
            {
                Value = new EntityDef { Id = EntityIdFor(gp.Value.Id), Path = gp.Value };
                return true;
            }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(EntityDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }

        public static string EntityIdFor(string pathId)
        {
            if (string.IsNullOrEmpty(pathId)) return "e_0";
            return pathId.StartsWith("g_", StringComparison.Ordinal) ? "e_" + pathId.Substring(2) : "e_" + pathId;
        }
    }

    public class Colugo_Track : GH_Goo<TrackDef>
    {
        public Colugo_Track() { }
        public Colugo_Track(TrackDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.N >= 2 && Value.Frames != null && Value.Frames.Length >= Value.N * 6;
        public override string TypeName => "Colugo Track";
        public override string TypeDescription => "Timeline track: frame sequence, span, easing";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Track();
            return new Colugo_Track(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null
                ? "<null>"
                : $"{Value.N} frames · {(Value.End > Value.Start ? $"{Value.Start:0.##}–{Value.End:0.##}s" : "full span")}"
                  + (Value.Hidden ? " · muted" : "");

        public override bool CastFrom(object source)
        {
            if (source is TrackDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(TrackDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Colugo_Event : GH_Goo<EventDef>
    {
        public Colugo_Event() { }
        public Colugo_Event(EventDef def) { Value = def; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Event)
                                        && !string.IsNullOrEmpty(Value.Action);

        public override string TypeName => "Colugo Event";
        public override string TypeDescription => "Event rule: event to action";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_Event();
            return new Colugo_Event(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Event} → {Value.Action}{(string.IsNullOrEmpty(Value.Detail) ? "" : " (" + Value.Detail + ")")}";

        public override bool CastFrom(object source)
        {
            if (source is EventDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(EventDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Param 包装（端口类型；没有这些，自定义 Goo 无法在端口间连）
    // ---------------------------------------------------------------------

    public class Param_Colugo_Canvas : GH_Param<Colugo_Canvas>
    {
        public Param_Colugo_Canvas() : base("Canvas", "Canvas", "Canvas definition", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c1");

        // 自定义 Param 也会出现在 GH 标签页里，没有图标就是一块棋盘格
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCanvas;
    }

    public class Param_Colugo_Camera : GH_Param<Colugo_Camera>
    {
        public Param_Colugo_Camera() : base("Camera", "Camera", "World to page camera", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c6");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCamera;
    }

    public class Param_Colugo_Path : GH_Param<Colugo_Path>    {
        public Param_Colugo_Path() : base("Paths", "Paths", "Path data", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c2");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataPath;
    }

    public class Param_Colugo_Command : GH_Param<Colugo_Command>
    {
        public Param_Colugo_Command() : base("Commands", "Commands", "Draw command", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c3");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCommand;
    }

    public class Param_Colugo_Entity : GH_Param<Colugo_Entity>
    {
        public Param_Colugo_Entity() : base("Entities", "Entities", "Entity blueprint", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c4");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataEntity;
    }

    public class Colugo_Signal : GH_Goo<SignalDef>
    {
        public Colugo_Signal() { }
        public Colugo_Signal(SignalDef v) { Value = v; }

        public override bool IsValid => Value != null;
        public override string TypeName => "Colugo Signal";
        public override string TypeDescription => "Live signal evaluated in the browser";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Signal() : new Colugo_Signal(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Kind} · {(Value.Args == null ? 0 : Value.Args.Length)}D";

        public override bool CastFrom(object source)
        {
            if (source is SignalDef d) { Value = d; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q).IsAssignableFrom(typeof(SignalDef)))
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Param_Colugo_Signal : GH_Param<Colugo_Signal>
    {
        public Param_Colugo_Signal() : base("Signal", "Signal", "Live signal evaluated in the browser", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c9");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataSignal;
    }

    public class Param_Colugo_Track : GH_Param<Colugo_Track>
    {
        public Param_Colugo_Track() : base("Track", "Track", "Timeline track", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c5");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataTrack;
    }

    public class Param_Colugo_Event : GH_Param<Colugo_Event>
    {
        public Param_Colugo_Event() : base("Events", "Events", "Event rule", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c7");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataEvent;
    }

    /// <summary>运行时表达式的 Goo。GH 只负责拼这棵树，求值发生在浏览器。</summary>
    public class Colugo_Expr : GH_Goo<RuntimeExprDef>
    {
        public Colugo_Expr() { }
        public Colugo_Expr(RuntimeExprDef v) { Value = v; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Op);
        public override string TypeName => "Colugo Expression";
        public override string TypeDescription => "Runtime expression evaluated in the browser";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Expr() : new Colugo_Expr(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : Value.Key();

        public override bool CastFrom(object source)
        {
            if (source is RuntimeExprDef d) { Value = d; return true; }
            // 数字直接接到表达式端口 = 常量：少摆一颗电池
            if (source is double n) { Value = RuntimeExprDef.Const(n); return true; }
            if (source is int i) { Value = RuntimeExprDef.Const(i); return true; }
            // 文本 = 变量名。方便在 Event Rule 里直接手写 v_w，不必非拖一颗 Runtime Var
            if (source is string name && !string.IsNullOrWhiteSpace(name))
            {
                Value = RuntimeExprDef.Var(name.Trim(), 0);
                return true;
            }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q).IsAssignableFrom(typeof(RuntimeExprDef)))
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Param_Colugo_Expr : GH_Param<Colugo_Expr>
    {
        public Param_Colugo_Expr() : base("Expression", "Expr", "Runtime expression", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000ca");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataExpr;
    }

    /// <summary>图片的 Goo。值里带的是 data URL，不是文件路径 —— 路径只在读取时用一次。</summary>
    public class Colugo_Image : GH_Goo<ImageDef>
    {
        public Colugo_Image() { }
        public Colugo_Image(ImageDef v) { Value = v; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Src) && Value.Src.StartsWith("data:image/");
        public override string TypeName => "Colugo Image";
        public override string TypeDescription => "Embedded image as a data URL";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Image() : new Colugo_Image(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.W}×{Value.H} · {Value.Src.Length / 1024} KB";

        public override bool CastFrom(object source)
        {
            if (source is ImageDef d) { Value = d; return true; }
            if (source is Colugo_Image img) { Value = img.Value; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q).IsAssignableFrom(typeof(ImageDef)) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Param_Colugo_Image : GH_Param<Colugo_Image>
    {
        public Param_Colugo_Image() : base("Image", "Image", "Embedded image", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000cb");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataImage;
    }

}
