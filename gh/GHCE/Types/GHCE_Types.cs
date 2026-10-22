using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace GHCE.Types
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

        public PathDef Duplicate() => (PathDef)MemberwiseClone();
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
        public TrackDef Track;           // 时间轨：帧序列 + 时长/循环/缓动（v0.16）

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

        public StrokeDef Stroke = new StrokeDef();
        public FillDef Fill = new FillDef();
        public double PointSize = 3.0;

        public CommandDef Duplicate()
        {
            var c = (CommandDef)MemberwiseClone();
            c.Stroke = Stroke.Duplicate();
            c.Fill = Fill.Duplicate();
            if (LayerDef != null) c.LayerDef = LayerDef.Duplicate();
            if (Track != null) c.Track = Track.Duplicate();
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

        public EntityDef Duplicate() => (EntityDef)MemberwiseClone();
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
        public double Duration = 1.0;    // 跑完一遍多少秒
        public bool Loop = true;
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
    /// 事件触发（阶段 3 D）。一次点击不需要 GH 重新求解 —— 规则发一次，浏览器自己执行。
    /// 选择性清屏（3 C）复用同一套机制，Action 填 clear-region。
    /// </summary>
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

        public static readonly string[] Events = { "click", "dblclick", "key" };

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
            return e;
        }
    }

    // ---------------------------------------------------------------------
    // Goo 包装
    // ---------------------------------------------------------------------
    public class GHCE_Canvas : GH_Goo<CanvasDef>
    {
        public GHCE_Canvas() { }
        public GHCE_Canvas(CanvasDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.W > 0 && Value.H > 0;
        public override string TypeName => "GHCE Canvas";
        public override string TypeDescription => "画布定义（尺寸 / 背景 / DPR）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Canvas();
            return new GHCE_Canvas(Value.Duplicate());
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

    public class GHCE_Camera : GH_Goo<CameraDef>
    {
        public GHCE_Camera() { }
        public GHCE_Camera(CameraDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.W > 0 && Value.H > 0
                                        && Value.Proj != null && Value.Proj.Length == 3;

        public override string TypeName => "GHCE Camera";
        public override string TypeDescription => "世界 → 页面相机（页面尺寸 / 内边距 / 缩放）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Camera();
            return new GHCE_Camera(Value.Duplicate());
        }

        public override string ToString()
        {
            if (Value == null) return "<null>";
            var p = Value.Proj;
            return p == null
                ? $"{Value.W}x{Value.H} (未求解)"
                : $"{Value.W}x{Value.H} · s {p[0]:0.###} · cam ({p[1]:0.#},{p[2]:0.#})";
        }

        public override bool CastFrom(object source)
        {
            if (source is CameraDef d) { Value = d; return true; }
            if (source is GHCE_Camera c) { Value = c.Value; return true; }
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

    public class GHCE_Path : GH_Goo<PathDef>
    {        public GHCE_Path() { }
        public GHCE_Path(PathDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.XY != null && Value.XY.Length >= 2;
        public override string TypeName => "GHCE Path";
        public override string TypeDescription => "已投影的路径数据（页面坐标）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Path();
            return new GHCE_Path(Value.Duplicate());
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

    public class GHCE_Command : GH_Goo<CommandDef>
    {
        public GHCE_Command() { }
        public GHCE_Command(CommandDef def) { Value = def; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Id) && !string.IsNullOrEmpty(Value.GeomId);
        public override string TypeName => "GHCE Draw Command";
        public override string TypeDescription => "绘图指令：几何引用 + 样式 + 层级 + 顺序";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Command();
            return new GHCE_Command(Value.Duplicate());
        }

        public override string ToString() => Value == null ? "<null>" : $"{Value.Id} @{Value.Layer}#{Value.Order}";

        public override bool CastFrom(object source)
        {
            // 允许把 Path 直接接到需要 Draw Command 的端口上：
            // 用一个默认样式包一层，这样"只想看一眼"的时候不必先摆一颗 Draw Path。
            if (source is GHCE_Path gp && gp.Value != null)
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

    public class GHCE_Entity : GH_Goo<EntityDef>
    {
        public GHCE_Entity() { }
        public GHCE_Entity(EntityDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.Path != null && !string.IsNullOrEmpty(Value.Id);
        public override string TypeName => "GHCE Entities";
        public override string TypeDescription => "动态对象蓝图（运行时自己持有状态）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Entity();
            return new GHCE_Entity(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Id} ×{Value.Count} @{Value.Layer}";

        public override bool CastFrom(object source)
        {
            if (source is GHCE_Entity e) { Value = e.Value; return true; }
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
            if (source is GHCE_Path gp && gp.Value != null)
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

    public class GHCE_Track : GH_Goo<TrackDef>
    {
        public GHCE_Track() { }
        public GHCE_Track(TrackDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.N >= 2 && Value.Frames != null && Value.Frames.Length >= Value.N * 6;
        public override string TypeName => "GHCE Track";
        public override string TypeDescription => "时间轨（帧序列 / 时长 / 循环 / 缓动）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Track();
            return new GHCE_Track(Value.Duplicate());
        }

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.N} 帧 · {Value.Duration:0.##}s{(Value.Loop ? " · loop" : "")}";

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

    public class GHCE_Event : GH_Goo<EventDef>
    {
        public GHCE_Event() { }
        public GHCE_Event(EventDef def) { Value = def; }

        public override bool IsValid => Value != null && !string.IsNullOrEmpty(Value.Event)
                                        && !string.IsNullOrEmpty(Value.Action);

        public override string TypeName => "GHCE Event";
        public override string TypeDescription => "事件触发规则（事件 → 动作）";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new GHCE_Event();
            return new GHCE_Event(Value.Duplicate());
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

    public class Param_GHCE_Canvas : GH_Param<GHCE_Canvas>
    {
        public Param_GHCE_Canvas() : base("Canvas", "Canvas", "画布定义", "GHCE", "Output", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c1");

        // 自定义 Param 也会出现在 GH 标签页里，没有图标就是一块棋盘格
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCanvas;
    }

    public class Param_GHCE_Camera : GH_Param<GHCE_Camera>
    {
        public Param_GHCE_Camera() : base("Camera", "Camera", "世界 → 页面相机", "GHCE", "Drawing", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c6");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCamera;
    }

    public class Param_GHCE_Path : GH_Param<GHCE_Path>    {
        public Param_GHCE_Path() : base("Paths", "Paths", "路径数据", "GHCE", "Drawing", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c2");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataPath;
    }

    public class Param_GHCE_Command : GH_Param<GHCE_Command>
    {
        public Param_GHCE_Command() : base("Commands", "Commands", "绘图指令", "GHCE", "Drawing", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c3");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCommand;
    }

    public class Param_GHCE_Entity : GH_Param<GHCE_Entity>
    {
        public Param_GHCE_Entity() : base("Entities", "Entities", "动态对象蓝图", "GHCE", "Entity", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c4");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataEntity;
    }

    public class Param_GHCE_Track : GH_Param<GHCE_Track>
    {
        public Param_GHCE_Track() : base("Track", "Track", "关键帧轨道", "GHCE", "Entity", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c5");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataTrack;
    }

    public class Param_GHCE_Event : GH_Param<GHCE_Event>
    {
        public Param_GHCE_Event() : base("Events", "Events", "事件触发规则", "GHCE", "Input", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("6f1a2b30-1111-4a01-9e01-0000000000c7");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataEvent;
    }
}
