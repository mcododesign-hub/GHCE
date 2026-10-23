using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// 表达式电池组的公共部分。
    ///
    /// 这里的原则只有一条：**GH 只拼树，不求值。**
    /// 每次求解都算出来的是"GH 数值"；这些电池产出的是"浏览器将来每帧才执行的公式"，
    /// 两者不能互相冒充。所以这里没有任何随时间变化的东西 —— 时间在浏览器里。
    /// </summary>
    public abstract class ExprComponentBase : GH_Component
    {
        protected ExprComponentBase(string name, string nick, string desc)
            : base(name, nick, desc, "GHCE", "Runtime") { }

        protected static RuntimeExprDef AsExpr(GHCE_Expr goo) => goo?.Value;

        protected static void Warn(GH_Component owner, string msg) =>
            owner.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);
    }

    /// <summary>
    /// Runtime Var —— 声明一个运行时变量。
    ///
    /// 名字是唯一身份：事件用同名写它，表达式用同名读它。初值只在文档开始时生效，
    /// 之后由事件改写；`doc.reset`（换作品、重连快照）会让它回到初值。
    /// </summary>
    public class RuntimeVarComponent : ExprComponentBase
    {
        public RuntimeVarComponent()
            : base("Runtime Var", "Var", "Declares a runtime variable: events write it, expressions read it") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b4");
        protected override Bitmap Icon => Icons.ExprVar;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Name", "Name", "Variable name, e.g. v_w. Must match the name used by the event and the expression",
                GH_ParamAccess.item, "v");
            p.AddNumberParameter("Initial", "Init", "Initial value. Used at document start and after a reset",
                GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr",
                "A variable node. Feed it into Runtime Math, Bind, or Event Rule", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string name = "v";
            double init = 0;
            if (!DA.GetData(0, ref name) || string.IsNullOrWhiteSpace(name))
            {
                Warn(this, "Name is empty. Nothing can reference this variable");
                return;
            }
            DA.GetData(1, ref init);
            name = name.Trim();

            DA.SetData(0, new GHCE_Expr(RuntimeExprDef.Var(name, init)));
            Message = $"{name} = {init:0.##}";
        }
    }

    /// <summary>
    /// Runtime Time —— 浏览器时钟。不是 GH 的当前值，也不会随 GH 求解推进。
    /// 输出 `time * Scale + Offset`，换算成"秒"或"弧度"由使用者决定。
    /// </summary>
    public class RuntimeTimeComponent : ExprComponentBase
    {
        public RuntimeTimeComponent()
            : base("Runtime Time", "Time", "Browser clock in seconds. Scale and offset are applied to it") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b5");
        protected override Bitmap Icon => Icons.ExprTime;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("Scale", "Scale", "Multiplier applied to time, e.g. 2 for twice the speed",
                GH_ParamAccess.item, 1);
            p.AddNumberParameter("Offset", "Offset", "Added after scaling", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr", "Runtime clock expression", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double scale = 1, offset = 0;
            DA.GetData(0, ref scale);
            DA.GetData(1, ref offset);

            RuntimeExprDef node = RuntimeExprDef.Time();
            if (Math.Abs(scale - 1) > 1e-9) node = RuntimeExprDef.Bin("mul", node, RuntimeExprDef.Const(scale));
            if (Math.Abs(offset) > 1e-9) node = RuntimeExprDef.Bin("add", node, RuntimeExprDef.Const(offset));

            DA.SetData(0, new GHCE_Expr(node));
            Message = $"t×{scale:0.##}{(Math.Abs(offset) > 1e-9 ? $" + {offset:0.##}" : "")}";
        }
    }

    /// <summary>
    /// Runtime Math —— 两个表达式算一个。
    /// 操作是序号不是字符串：1 add / 2 sub / 3 mul / 4 div / 5 min / 6 max / 7 sin(A) / 8 clamp01(A)。
    /// 7 与 8 只用 A，B 可以不接。
    /// </summary>
    public class RuntimeMathComponent : ExprComponentBase
    {
        static readonly string[] Ops = { "add", "sub", "mul", "div", "min", "max", "sin", "clamp01", "gt", "lt", "noise" };
        static readonly string[] Mode =
        {
            "A+B", "A-B", "A×B", "A/B", "min", "max", "sin(A)", "clamp01(A)",
            "A>B ? 1 : 0", "A<B ? 1 : 0", "noise(A)",
        };

        public RuntimeMathComponent()
            : base("Runtime Math", "Math", "Combines two runtime expressions. 1 add 2 sub 3 mul 4 div 5 min 6 max 7 sin 8 clamp01") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b6");
        protected override Bitmap Icon => Icons.ExprMath;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "A", "A", "Left operand (or the only one for sin/clamp01)", GH_ParamAccess.item);
            p.AddParameter(new Param_GHCE_Expr(), "B", "B", "Right operand", GH_ParamAccess.item);
            p[1].Optional = true;
            p.AddIntegerParameter("Op", "Op",
                "1 add · 2 sub · 3 mul · 4 div · 5 min · 6 max · 7 sin(A) · 8 clamp01(A) · 9 A>B · 10 A<B · 11 noise(A)",
                GH_ParamAccess.item, 1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr", "Combined expression", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GHCE_Expr a = null, b = null;
            int op = 1;
            if (!DA.GetData(0, ref a) || a?.Value == null)
            {
                Warn(this, "A is required");
                return;
            }
            DA.GetData(1, ref b);
            DA.GetData(2, ref op);
            if (op < 1 || op > Ops.Length) op = 1;

            var A = AsExpr(a);
            var B = b?.Value;
            bool unary = op >= 7;
            if (!unary && B == null)
            {
                Warn(this, $"{Mode[op - 1]} needs B");
                return;
            }

            var node = unary
                ? RuntimeExprDef.Unary(Ops[op - 1], A)
                : RuntimeExprDef.Bin(Ops[op - 1], A, B);

            DA.SetData(0, new GHCE_Expr(node));
            Message = Mode[op - 1];
        }
    }

    /// <summary>
    /// Bind —— 把表达式绑到指令的某个属性上。
    ///
    /// 绑定不写回指令本体：绘制时才求值，所以同一份指令可以同时被两条 Bind 绑成两种样子，
    /// 导出与 SVG 拿到的仍然是静态真值。
    /// </summary>
    public class BindComponent : ExprComponentBase
    {
        static readonly string[] Props = { "width", "opacity", "hue", "scale" };
        static readonly string[] Labels = { "Stroke Width", "Opacity", "Hue 0-1", "Scale" };

        public BindComponent()
            : base("Bind", "Bind", "Binds a runtime expression to a draw command property. 1 width 2 opacity 3 hue 4 scale") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b7");
        protected override Bitmap Icon => Icons.Bind;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to bind", GH_ParamAccess.list);
            p.AddIntegerParameter("Property", "Prop", "1 stroke width · 2 opacity · 3 hue (0-1) · 4 scale",
                GH_ParamAccess.item, 1);
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr", "Expression to evaluate every frame",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands with the binding attached",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            GHCE_Expr expr = null;
            int prop = 1;
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;
            DA.GetData(1, ref prop);
            if (prop < 1 || prop > Props.Length) prop = 1;
            if (!DA.GetData(2, ref expr) || expr?.Value == null)
            {
                Warn(this, "Expression is required");
                return;
            }

            string key = Props[prop - 1];
            var outCmds = new List<GHCE_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                var c = g?.Value;
                if (c == null) continue;
                var d = c.Duplicate();
                if (d.Binds == null) d.Binds = new Dictionary<string, RuntimeExprDef>();
                d.Binds[key] = expr.Value.Duplicate();
                outCmds.Add(new GHCE_Command(d));
            }

            DA.SetDataList(0, outCmds);
            Message = $"{Labels[prop - 1]} ← {expr.Value.Key()}";
        }
    }

    /// <summary>
    /// Event Rule —— 事件写变量。
    ///
    /// 和 Event Trigger 的分工：Event Trigger 是"8 个预设动作"的快捷方式；
    /// 这里只有三种写入（set / toggle / add），能改的是变量，至于变量被谁读、
    /// 最后变成宽度还是透明度，由表达式决定。想加一种新效果不必再改引擎。
    /// </summary>
    public class EventRuleComponent : ExprComponentBase
    {
        static readonly string[] WriteOps = { "set", "toggle", "add" };

        public EventRuleComponent()
            : base("Event Rule", "EvtRule", "Writes a runtime variable when an event fires. 1 set 2 toggle 3 add") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b8");
        protected override Bitmap Icon => Icons.EventRule;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to carry this rule", GH_ParamAccess.list);
            p.AddTextParameter("Event", "Event", "click, dblclick, key, or key:a for a specific key",
                GH_ParamAccess.item, "click");
            p.AddParameter(new Param_GHCE_Expr(), "Variable", "Var", "The Runtime Var to write (or a name as text)",
                GH_ParamAccess.item);
            p.AddIntegerParameter("Write", "Write", "1 set · 2 toggle · 3 add", GH_ParamAccess.item, 2);
            p.AddNumberParameter("Value", "Value", "Value for set / add. Ignored by toggle", GH_ParamAccess.item, 1);
            p.AddBooleanParameter("Enable", "On", "Disable to keep the rule out of the scene", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands carrying the rule", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            string ev = "click";
            DA.GetData(1, ref ev);
            ev = (ev ?? "click").Trim().ToLowerInvariant();

            GHCE_Expr varExpr = null;
            if (!DA.GetData(2, ref varExpr) || varExpr?.Value == null || varExpr.Value.Op != "var")
            {
                Warn(this, "Variable must come from a Runtime Var (or a plain name)");
                return;
            }

            int write = 2;
            double value = 1;
            bool on = true;
            DA.GetData(3, ref write);
            DA.GetData(4, ref value);
            DA.GetData(5, ref on);
            if (!on) { DA.SetDataList(0, cmds); return; }
            if (write < 1 || write > 3) write = 2;

            string evName = ev, detail = "";
            int colon = ev.IndexOf(':');
            if (colon > 0)
            {
                evName = ev.Substring(0, colon).Trim();
                detail = ev.Substring(colon + 1).Trim();
            }
            if (Array.IndexOf(EventDef.Events, evName) < 0)
            {
                Warn(this, $"Event must be one of {string.Join(" / ", EventDef.Events)}. Received \"{ev}\" and fell back to click");
                evName = "click";
                detail = "";
            }

            var varName = varExpr.Value.Name;
            var rule = new EventDef
            {
                Id = $"ev_{evName}_{(detail.Length == 0 ? "any" : detail)}_{varName}",
                Event = evName,
                Detail = detail,
                Action = null,
                Targets = new string[0],
                Writes = new List<WriteDef>
                {
                    new WriteDef { Var = varName, Op = WriteOps[write - 1], Value = value }
                }
            };

            var outCmds = new List<GHCE_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                var c = g?.Value;
                if (c == null) continue;
                var d = c.Duplicate();
                if (d.Events == null) d.Events = new List<EventDef>();
                d.Events.Add(rule);
                outCmds.Add(new GHCE_Command(d));
            }

            DA.SetDataList(0, outCmds);
            Message = $"{evName}{(detail.Length > 0 ? ":" + detail : "")} → {varName} {WriteOps[write - 1]}";
        }
    }

    /// <summary>
    /// Runtime Remap —— 把一个运行时值从一个区间映射到另一个区间。
    ///
    /// 两个区间是 GH 的 Domain（求解期常量），所以这里只是把已有算子拼成一棵树：
    /// out0 + (x - in0) / (in1 - in0) * (out1 - out0)
    /// 运行时不必多一个 remap 算子 —— 少一个算子就少一处要单独维护的语义。
    /// </summary>
    public class RuntimeRemapComponent : ExprComponentBase
    {
        public RuntimeRemapComponent()
            : base("Runtime Remap", "Remap", "Maps a runtime value from one domain to another") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b9");
        protected override Bitmap Icon => Icons.ExprRemap;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "X", "X", "Value to remap", GH_ParamAccess.item);
            p.AddIntervalParameter("In", "In", "Source interval", GH_ParamAccess.item, new Interval(0, 1));
            p.AddIntervalParameter("Out", "Out", "Target interval", GH_ParamAccess.item, new Interval(0, 1));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr", "Remapped expression", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GHCE_Expr x = null;
            if (!DA.GetData(0, ref x) || x?.Value == null)
            {
                Warn(this, "X is required");
                return;
            }

            var inD = new Interval(0, 1);
            var outD = new Interval(0, 1);
            DA.GetData(1, ref inD);
            DA.GetData(2, ref outD);

            double a0 = inD.T0, a1 = inD.T1, b0 = outD.T0, b1 = outD.T1;
            double span = a1 - a0;
            if (Math.Abs(span) < 1e-9)
            {
                Warn(this, "Source domain is empty. Result is a constant");
                DA.SetData(0, new GHCE_Expr(RuntimeExprDef.Const(b0)));
                return;
            }

            var norm = RuntimeExprDef.Bin("div",
                RuntimeExprDef.Bin("sub", x.Value, RuntimeExprDef.Const(a0)),
                RuntimeExprDef.Const(span));
            var tree = RuntimeExprDef.Bin("add",
                RuntimeExprDef.Const(b0),
                RuntimeExprDef.Bin("mul", norm, RuntimeExprDef.Const(b1 - b0)));

            DA.SetData(0, new GHCE_Expr(tree));
            Message = $"[{a0:0.##},{a1:0.##}] → [{b0:0.##},{b1:0.##}]";
        }
    }

    /// <summary>
    /// Runtime Select —— 条件非 0 取 A，否则取 B。
    /// 条件一般来自 Runtime Math 的比较算子（9 A&gt;B / 10 A&lt;B，输出 0 或 1）。
    /// </summary>
    public class RuntimeSelectComponent : ExprComponentBase
    {
        public RuntimeSelectComponent()
            : base("Runtime Select", "Select", "Picks A when the condition is non-zero, otherwise B") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ba");
        protected override Bitmap Icon => Icons.ExprSelect;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Condition", "If", "Non-zero means A", GH_ParamAccess.item);
            p.AddParameter(new Param_GHCE_Expr(), "A", "A", "Value when the condition is true", GH_ParamAccess.item);
            p.AddParameter(new Param_GHCE_Expr(), "B", "B", "Value when the condition is false", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Expr(), "Expression", "Expr", "Selected expression", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GHCE_Expr cond = null, a = null, b = null;
            if (!DA.GetData(0, ref cond) || cond?.Value == null) { Warn(this, "Condition is required"); return; }
            if (!DA.GetData(1, ref a) || a?.Value == null) { Warn(this, "A is required"); return; }
            if (!DA.GetData(2, ref b) || b?.Value == null) { Warn(this, "B is required"); return; }

            var tree = new RuntimeExprDef { Op = "select", C = cond.Value, A = a.Value, B = b.Value };
            DA.SetData(0, new GHCE_Expr(tree));
            Message = "if → A else B";
        }
    }
}
