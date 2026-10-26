using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Colugo.Types;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Colugo.Components
{
    /// <summary>
    /// 所有 Shader 电池的基类。
    /// </summary>
    public abstract class ShaderBattery : GH_Component
    {
        readonly Guid _guid;

        protected ShaderBattery(string name, string nick, string desc, Guid guid)
            : base(name, nick, desc, "Colugo", "Shader")
        {
            _guid = guid;
        }

        public override Guid ComponentGuid => _guid;
        public override GH_Exposure Exposure => GH_Exposure.primary;

        /// <summary>
        /// 节点 id 的命名空间。必须每只电池不同：两只下游电池各自并入同一上游后，
        /// 自增出来的 id 会撞在一起，合并进 Mix 时运行时就报重复 id。
        /// 探针里 InstanceGuid 可能是空 Guid（没进文档），所以退到类型名的确定性哈希。
        /// </summary>
        protected string Ns
        {
            get
            {
                var g = InstanceGuid;
                if (g != Guid.Empty) return g.ToString("N").Substring(0, 6);
                uint h = 2166136261;
                foreach (var ch in GetType().Name) { h ^= ch; h *= 16777619; }
                return (h % 0xffffff).ToString("x6");
            }
        }

        protected ShaderExprDef NewExpr() => new ShaderExprDef(Ns);

        protected static ShaderExprDef In(IGH_DataAccess DA, int i)
        {
            Colugo_ShaderExpr g = null;
            return DA.GetData(i, ref g) && g != null ? g.Value : null;
        }

        protected static double Num(IGH_DataAccess DA, int i, double dflt)
        {
            double v = dflt;
            DA.GetData(i, ref v);
            return v;
        }
    }

    /* ---------------- 几何输入 ---------------- */

    public class ShaderPositionComponent : ShaderBattery
    {
        public ShaderPositionComponent() : base("Shader Position", "SPos",
            "Vertex position as a shader input (vec3)", new Guid("5a0c7e10-2222-4b02-8d02-0000000000d1")) { }
        protected override Bitmap Icon => Icons.ShaderPosition;

        protected override void RegisterInputParams(GH_InputParamManager p) =>
            p.AddIntegerParameter("Space", "S", "1 local · 2 world", GH_ParamAccess.item, 1);

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "vec3 position node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int space = 1;
            DA.GetData(0, ref space);
            var e = NewExpr();
            var id = e.Add("position", "v3", ("space", space == 2 ? "world" : "local"));
            e.OutId = id;
            e.Type = "v3";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderNormalComponent : ShaderBattery
    {
        public ShaderNormalComponent() : base("Shader Normal", "SNrm",
            "Surface normal as a shader input (vec3)", new Guid("5a0c7e10-2222-4b02-8d02-0000000000d2")) { }
        protected override Bitmap Icon => Icons.ShaderNormal;

        protected override void RegisterInputParams(GH_InputParamManager p) =>
            p.AddIntegerParameter("Space", "S", "1 local · 2 world", GH_ParamAccess.item, 1);

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "vec3 normal node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int space = 1;
            DA.GetData(0, ref space);
            var e = NewExpr();
            var id = e.Add("normal", "v3", ("space", space == 2 ? "world" : "local"));
            e.OutId = id;
            e.Type = "v3";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderUVComponent : ShaderBattery
    {
        public ShaderUVComponent() : base("Shader UV", "SUV",
            "Texture coordinate as a shader input (vec2). First version: UV0 only",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d3")) { }
        protected override Bitmap Icon => Icons.ShaderUV;

        protected override void RegisterInputParams(GH_InputParamManager p) { }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "vec2 uv node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var e = NewExpr();
            var id = e.Add("uv", "v2");
            e.OutId = id;
            e.Type = "v2";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    /* ---------------- 参数 ---------------- */

    public class ShaderUniformComponent : ShaderBattery
    {
        public ShaderUniformComponent() : base("Shader Uniform", "SUni",
            "Named float uniform. Changing the value never rebuilds the material",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d4")) { }
        protected override Bitmap Icon => Icons.ShaderUniform;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Name", "N", "Uniform name, must be unique per artwork", GH_ParamAccess.item, "uAmp");
            p.AddNumberParameter("Value", "V", "Current value", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "float uniform node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string name = "uAmp";
            DA.GetData(0, ref name);
            double value = Num(DA, 1, 1.0);
            if (string.IsNullOrWhiteSpace(name))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Uniform 名字为空，运行时会拿不到这个参数");
                name = "u_" + Ns;
            }
            var e = NewExpr();
            var id = e.Add("uniform", "f", ("name", name), ("value", value), ("type", "f"));
            e.OutId = id;
            e.Type = "f";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderColorComponent : ShaderBattery
    {
        public ShaderColorComponent() : base("Shader Color", "SCol",
            "Constant colour. RGB takes 0-1; HSL takes degrees / 0-1 / 0-1",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d7")) { }
        protected override Bitmap Icon => Icons.ShaderColor;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Mode", "M", "1 RGB · 2 HSL", GH_ParamAccess.item, 1);
            p.AddNumberParameter("R / H", "R", "Red 0-1, or hue 0-360", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("G / S", "G", "Green 0-1, or saturation 0-1", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("B / L", "B", "Blue 0-1, or lightness 0-1", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "vec3 colour constant", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int mode = 1;
            DA.GetData(0, ref mode);
            double a = Num(DA, 1, 1), b = Num(DA, 2, 1), c = Num(DA, 3, 1);

            double r, g, bl;
            if (mode == 2)
            {
                // HSL 在 GH 侧就算成 RGB：运行时的 const 只是 vec3，不该让它认识色相
                var rgb = HslToRgb(a, b, c);
                r = rgb[0]; g = rgb[1]; bl = rgb[2];
            }
            else { r = a; g = b; bl = c; }

            r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1); bl = Math.Clamp(bl, 0, 1);

            var e = NewExpr();
            var id = e.Add("const", "v3", ("v", new[] { r, g, bl }));
            e.OutId = id;
            e.Type = "v3";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }

        static double[] HslToRgb(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360.0;
            s = Math.Clamp(s, 0, 1);
            l = Math.Clamp(l, 0, 1);
            if (s <= 0) return new[] { l, l, l };
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            double Hue(double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            return new[] { Hue(h + 1.0 / 3), Hue(h), Hue(h - 1.0 / 3) };
        }
    }

    /* ---------------- 运算 ---------------- */

    public class ShaderMathComponent : ShaderBattery
    {
        static readonly string[] Ops =
        {
            "add", "sub", "mul", "div", "pow",
            "sin", "cos", "abs", "fract", "floor", "sqrt", "oneMinus", "saturate",
            "remap", "smoothstep", "clamp", "step",
            "dot", "length", "normalize",
        };

        static readonly HashSet<string> Unary = new HashSet<string>
        {
            "sin", "cos", "abs", "fract", "floor", "sqrt", "oneMinus", "saturate",
            "length", "normalize",
        };

        public ShaderMathComponent() : base("Shader Math", "SM",
            "One operator for the whole whitelist. remap takes X / in0 / in1 / out1",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d5")) { }
        protected override Bitmap Icon => Icons.ShaderMath;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Op", "Op",
                "1 add · 2 sub · 3 mul · 4 div · 5 pow · 6 sin · 7 cos · 8 abs · 9 fract · 10 floor · "
                + "11 sqrt · 12 oneMinus · 13 saturate · 14 remap · 15 smoothstep · 16 clamp · 17 step · "
                + "18 dot · 19 length · 20 normalize", GH_ParamAccess.item, 6);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "A", "A", "Primary input", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "B", "B",
                "Second input / remap in0 / smoothstep edge0 / clamp low", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "C", "C",
                "remap in1 / smoothstep edge1 / clamp high", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "D", "D", "remap out1", GH_ParamAccess.item);
            for (int i = 1; i <= 4; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "Result node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int op = 6;
            DA.GetData(0, ref op);
            op = Math.Clamp(op, 1, Ops.Length);
            string name = Ops[op - 1];

            var a = In(DA, 1);
            var b = In(DA, 2);
            var c = In(DA, 3);
            var d = In(DA, 4);

            if (a == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A 是必需的（这是整棵图的输入）");
                return;
            }

            var e = NewExpr();
            e.Absorb(a);
            if (b != null) e.Absorb(b);
            if (c != null) e.Absorb(c);
            if (d != null) e.Absorb(d);

            var args = new List<(string, object)> { ("a", a.OutId) };
            if (b != null) args.Add(("b", b.OutId));
            if (c != null) args.Add(("c", c.OutId));
            if (d != null) args.Add(("d", d.OutId));

            string type;
            string id;

            if (name == "remap")
            {
                // remap 写成 x / in0 / in1 / out1，输出下界固定 0 —— 这是"把 sin 接到 0..1"的常见形状
                args.Clear();
                args.Add(("x", a.OutId));
                if (b != null) args.Add(("in0", b.OutId));
                if (c != null) args.Add(("in1", c.OutId));
                if (d != null) args.Add(("out1", d.OutId));
                id = e.Add("remap", "f", args.ToArray());
                type = "f";
            }
            else if (name == "smoothstep")
            {
                args.Clear();
                args.Add(("x", a.OutId));
                if (b != null) args.Add(("e0", b.OutId));
                if (c != null) args.Add(("e1", c.OutId));
                id = e.Add("smoothstep", "f", args.ToArray());
                type = "f";
            }
            else if (name == "clamp")
            {
                args.Clear();
                args.Add(("x", a.OutId));
                if (b != null) args.Add(("lo", b.OutId));
                if (c != null) args.Add(("hi", c.OutId));
                id = e.Add("clamp", "f", args.ToArray());
                type = a.Type;
            }
            else if (name == "step")
            {
                // step(edge, x)：x >= edge 取 1，否则 0。B 是 edge
                args.Clear();
                args.Add(("x", a.OutId));
                if (b != null) args.Add(("e0", b.OutId));
                id = e.Add("step", "f", args.ToArray());
                type = "f";
            }
            else if (name == "dot")
            {
                if (b == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "dot 需要 A 和 B 两个向量");
                    return;
                }
                id = e.Add("dot", "f", args.ToArray());
                type = "f";
            }
            else if (name == "length")
            {
                id = e.Add("length", "f", ("a", a.OutId));
                type = "f";
            }
            else if (name == "normalize")
            {
                id = e.Add("normalize", "v3", ("a", a.OutId));
                type = "v3";
            }
            else
            {
                if (!Unary.Contains(name) && b == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{name} 需要 B");
                    return;
                }
                id = e.Add(name, a.Type, args.ToArray());
                type = a.Type;
            }

            e.OutId = id;
            e.Type = type;
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderMixComponent : ShaderBattery
    {
        public ShaderMixComponent() : base("Shader Mix", "SMix",
            "Linear interpolation between two colours or vectors",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d6")) { }
        protected override Bitmap Icon => Icons.ShaderMix;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_ShaderExpr(), "A", "A", "Value at 0", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "B", "B", "Value at 1", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Factor", "F", "0-1 blend factor", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "Mixed result", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var a = In(DA, 0);
            var b = In(DA, 1);
            var f = In(DA, 2);
            if (a == null || b == null || f == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A / B / Factor 三个都要接");
                return;
            }

            var e = NewExpr();
            e.Absorb(a); e.Absorb(b); e.Absorb(f);
            var id = e.Add("mix", a.Type, ("a", a.OutId), ("b", b.OutId), ("t", f.OutId));
            e.OutId = id;
            e.Type = a.Type;
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderSwizzleComponent : ShaderBattery
    {
        public ShaderSwizzleComponent() : base("Shader Swizzle", "SSwz",
            "Pick one component out of a vector (vec3 → float)",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d9")) { }
        protected override Bitmap Icon => Icons.ShaderSwizzle;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_ShaderExpr(), "A", "A", "Vector input", GH_ParamAccess.item);
            p.AddIntegerParameter("Channel", "C", "1 x · 2 y · 3 z", GH_ParamAccess.item, 1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "float component", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var a = In(DA, 0);
            if (a == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A 要接一个向量（Position / Normal / UV / Vector）");
                return;
            }
            int ch = 1;
            DA.GetData(1, ref ch);
            ch = Math.Clamp(ch, 1, 3);
            string comp = "xyz".Substring(ch - 1, 1);

            var e = NewExpr();
            e.Absorb(a);
            var id = e.Add("swizzle", "f", ("a", a.OutId), ("c", comp));
            e.OutId = id;
            e.Type = "f";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderTimeComponent : ShaderBattery
    {
        public ShaderTimeComponent() : base("Shader Time", "STime",
            "Artwork clock as a shader input. Scale and offset are baked into the graph",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000db")) { }
        protected override Bitmap Icon => Icons.ShaderTime;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("Scale", "S", "Speed multiplier, 2 = twice as fast", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Offset", "O", "Phase offset in seconds", GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "float time node", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double scale = Num(DA, 0, 1.0);
            double offset = Num(DA, 1, 0.0);

            var e = NewExpr();
            string cur = e.Add("time", "f");

            // scale = 1 / offset = 0 时不生成节点：图越短，增量 diff 越省
            if (Math.Abs(scale - 1.0) > 1e-9)
            {
                var k = e.Add("const", "f", ("v", scale));
                cur = e.Add("mul", "f", ("a", cur), ("b", k));
            }
            if (Math.Abs(offset) > 1e-9)
            {
                var k = e.Add("const", "f", ("v", offset));
                cur = e.Add("add", "f", ("a", cur), ("b", k));
            }

            e.OutId = cur;
            e.Type = "f";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    public class ShaderVectorComponent : ShaderBattery
    {
        public ShaderVectorComponent() : base("Shader Vector", "SVec",
            "Compose three scalars into a vec3. Missing inputs become 0",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000dc")) { }
        protected override Bitmap Icon => Icons.ShaderVector;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_ShaderExpr(), "X", "X", "X component", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Y", "Y", "Y component", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Z", "Z", "Z component", GH_ParamAccess.item);
            for (int i = 0; i < 3; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S", "vec3", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var x = In(DA, 0);
            var y = In(DA, 1);
            var z = In(DA, 2);
            if (x == null && y == null && z == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "三个分量都没接，输出零向量");
            }

            var e = NewExpr();
            string Zero() => e.Add("const", "f", ("v", 0.0));
            string xid, yid, zid;
            if (x != null) { e.Absorb(x); xid = x.OutId; } else xid = Zero();
            if (y != null) { e.Absorb(y); yid = y.OutId; } else yid = Zero();
            if (z != null) { e.Absorb(z); zid = z.OutId; } else zid = Zero();

            var id = e.Add("vec3", "v3", ("x", xid), ("y", yid), ("z", zid));
            e.OutId = id;
            e.Type = "v3";
            DA.SetData(0, new Colugo_ShaderExpr(e));
        }
    }

    /* ---------------- 挂到三维命令上 ---------------- */

    public class ApplyMaterialComponent : ShaderBattery
    {
        public ApplyMaterialComponent() : base("Apply Material", "ApplyMat",
            "Attach a shader graph to 3D draw commands as their colour",
            new Guid("5a0c7e10-2222-4b02-8d02-0000000000d8")) { }
        protected override Bitmap Icon => Icons.ApplyMaterial;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Cmd3D(), "Commands", "C", "3D commands from Draw 3D",
                GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_ShaderExpr(), "Shader", "S",
                "Colour graph. Leave empty to keep the constant colour", GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_Cmd3D(), "Commands", "C",
                "Commands carrying the shader, feed into Scene 3D", GH_ParamAccess.list);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Cmd3D>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Commands");
                return;
            }

            var shader = In(DA, 1);
            if (shader == null || !shader.IsValid)
            {
                // 没接 shader 就原样透传，而不是报错 —— 用户可以先搭好链路再上 Shader
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "没有接 Shader，命令原样透传");
                DA.SetDataList(0, cmds);
                return;
            }

            if (shader.Type != "v3")
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Shader 输出是 {shader.Type}，颜色槽要的是 v3。用 Shader Color 或 Mix 收口。");
                return;
            }

            var outCmds = new List<Colugo_Cmd3D>(cmds.Count);
            for (int i = 0; i < cmds.Count; i++)
            {
                var src = cmds[i]?.Value;
                if (src == null) continue;
                var copy = src.Duplicate();
                if (copy.Material == null) copy.Material = new Material3DDef();
                copy.Material.Shader = shader.Duplicate();
                outCmds.Add(new Colugo_Cmd3D(copy));
            }
            DA.SetDataList(0, outCmds);
        }
    }
}
