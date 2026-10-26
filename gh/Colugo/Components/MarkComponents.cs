using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Colugo.Bridge;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Brush —— 定义一枚印章（页坐标、以原点为中心）。
    ///
    /// 为什么不复用 Path Input：Path Input 会把曲线**投影到页面**（过相机），
    /// 而印章必须留在局部坐标系里 —— 它要被搬到几百个位置上。
    /// 两者的坐标含义不同，不能互借。
    ///
    /// 输出仍然是 Colugo Paths，所以既能被 Mark Along Path 用，也能直接 Draw Path 画出来看。
    /// </summary>
    public class BrushComponent : GH_Component
    {
        public BrushComponent()
            : base("Brush", "Brush", "Builds a brush stamp in page units, centred at the origin", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f1");
        protected override Bitmap Icon => Icons.Brush;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter("Curve", "Crv", "Custom stamp outline. Overrides Shape when connected", GH_ParamAccess.list);
            p.AddIntegerParameter("Shape", "Shape", "1 dot · 2 dash · 3 square · 4 triangle · 5 arc", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Size", "Size", "Stamp extent in page pixels", GH_ParamAccess.item, 16.0);
            p.AddNumberParameter("Aspect", "Aspect", "Width / height of the stamp", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Angle", "Angle", "Stamp rotation in degrees", GH_ParamAccess.item, 0.0);
            p[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Path(), "Brush", "Brush", "Stamp geometry in local page units", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Stamp summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var curves = new List<Curve>();
            int shape = 1;
            double size = 16, aspect = 1, angle = 0;

            DA.GetDataList(0, curves);
            DA.GetData(1, ref shape);
            DA.GetData(2, ref size);
            DA.GetData(3, ref aspect);
            DA.GetData(4, ref angle);

            if (size <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Size must be positive. Fell back to 16");
                size = 16;
            }
            if (aspect <= 0) aspect = 1;
            if (shape < 1 || shape > 5) shape = 1;

            bool closed;
            double[] xy;
            string source;

            if (curves.Count > 0 && curves[0] != null && curves[0].IsValid)
            {
                xy = SampleCurve(curves[0], out closed);
                xy = MarkMath.Normalize(xy, size, aspect, angle);
                source = "curve";
                if (xy == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Curve produced too few points. Fell back to the built-in shape");
                    xy = MarkMath.Stamp(shape, size, aspect);
                    closed = shape != 2 && shape != 5;
                    source = "shape";
                }
            }
            else
            {
                xy = MarkMath.Stamp(shape, size, aspect);
                closed = shape != 2 && shape != 5;
                source = "shape";
            }

            // 按坐标生成 id：改尺寸/形状就是另一枚印章，改颜色不是。
            var def = new PathDef
            {
                Id = PatchBuilder.StableId("g_brush_", xy),
                Closed = closed,
                XY = xy,
                IsPoints = false,
            };

            DA.SetData(0, new Colugo_Path(def));
            Message = $"{source} · {xy.Length / 2} pts · {size:0.#}px";
            DA.SetData(1, Message);
        }

        /// <summary>
        /// 自定义曲线采样。用参数等分而不是 ToPolyline：后者对封闭曲线和退化段的行为
        /// 在不同 Rhino 版本里不一样，而印章最多几百个点，等分足够。
        /// </summary>
        static double[] SampleCurve(Curve c, out bool closed)
        {
            closed = c.IsClosed;
            const int n = 96;
            var xy = new double[(n + 1) * 2];
            var dom = c.Domain;
            for (int i = 0; i <= n; i++)
            {
                var pt = c.PointAt(dom.ParameterAt(i / (double)n));
                xy[i * 2] = pt.X;
                xy[i * 2 + 1] = pt.Y;
            }
            return xy;
        }
    }

    /// <summary>
    /// Mark Along Path —— 沿路径等距放置印章，每条标记一条指令。
    ///
    /// 输出是**指令**而不是几何：同一枚印章被几百次复用，几何只发一次，
    /// 每条标记只带 6 个数的仿射矩阵。这是 Mark 不炸 patch 的关键。
    ///
    /// 不负责颜色/粗细：那是 Stroke Style / Fill Style 的事，这里只管"印在哪、朝哪"。
    /// </summary>
    public class MarkAlongPathComponent : GH_Component
    {
        public MarkAlongPathComponent()
            : base("Mark Along Path", "Marks", "Distributes a brush stamp along paths", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f2");
        protected override Bitmap Icon => Icons.MarkPath;

        static readonly string[] RotateLabels =
        {
            "follow tangent", "across path", "fixed angle", "random"
        };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Path(), "Paths", "Paths", "Source paths from Path Input", GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_Path(), "Brush", "Brush", "Stamp from the Brush component", GH_ParamAccess.item);
            p.AddNumberParameter("Spacing", "Spacing", "Distance between marks in page pixels", GH_ParamAccess.item, 8.0);
            p.AddIntegerParameter("Rotate", "Rotate", "1 follow tangent · 2 across path · 3 fixed angle · 4 random", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Angle", "Angle", "Offset for 1/2, absolute angle for 3, in degrees", GH_ParamAccess.item, 0.0);
            p.AddTextParameter("Layer", "Layer", "Layer name. Same name shares one layer", GH_ParamAccess.item, "main");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "One command per mark", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Mark count and spacing", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var src = new List<Colugo_Path>();
            Colugo_Path brushGoo = null;
            double spacing = 8, angle = 0;
            int rotate = 1;
            string layer = "main";

            if (!DA.GetDataList(0, src) || src.Count == 0) return;
            if (!DA.GetData(1, ref brushGoo) || brushGoo?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Brush is required. Use the Brush component or a Path Input");
                return;
            }
            DA.GetData(2, ref spacing);
            DA.GetData(3, ref rotate);
            DA.GetData(4, ref angle);
            DA.GetData(5, ref layer);

            var brush = brushGoo.Value;
            if (brush.XY == null || brush.XY.Length < 4)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Brush geometry is empty");
                return;
            }

            if (spacing <= 0.05)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Spacing must be positive. Fell back to 0.5px");
                spacing = 0.5;
            }
            if (rotate < 1 || rotate > 4) rotate = 1;

            string layerName = string.IsNullOrWhiteSpace(layer) ? "main" : layer.Trim();
            double angleRad = angle * Math.PI / 180.0;

            var outCmds = new List<Colugo_Command>();
            int order = 0;
            int total = 0;
            bool truncated = false;

            foreach (var g in src)
            {
                var p = g?.Value;
                if (p?.XY == null || p.XY.Length < 4) continue;

                int budget = MarkMath.MaxMarks - total;
                if (budget <= 0) { truncated = true; break; }

                var samples = MarkMath.Sample(p, spacing, budget);
                if (samples.Count == 0) continue;

                for (int i = 0; i < samples.Count; i++)
                {
                    var s = samples[i];
                    double theta;
                    switch (rotate)
                    {
                        case 2: theta = s.Angle + Math.PI / 2 + angleRad; break;
                        case 3: theta = angleRad; break;
                        case 4: theta = Hash01(p.Id, i) * Math.PI * 2; break;
                        default: theta = s.Angle + angleRad; break;
                    }

                    var cmd = new CommandDef
                    {
                        // 身份 = 电池实例 + 源路径 + 序号。
                        // 只带源路径与序号时，同一路径上两只不同间距的 Mark 会撞 id 互相顶掉。
                        Id = PatchBuilder.StableId("c_mk_", $"{InstanceGuid}|{p.Id}|{i}"),
                        GeomId = brush.Id,
                        Path = brush,                 // 共享印章几何：patch 里只出现一次
                        Layer = layerName,
                        Order = order++,
                        Visible = true,
                        Opacity = 1.0,
                        Progress = 1.0,
                        Matrix = MarkMath.Placement(s.X, s.Y, theta),
                        MarkU = s.U,
                        Stroke = new StrokeDef
                        {
                            Color = "#E8E6DF",
                            Width = 1.0,
                            Alpha = 1.0,
                            Cap = "round",
                            Join = "round",
                        },
                        // 闭合印章默认填充：圆点/方块画成空心圈是最常见的"这不是我要的"。
                        // 想要空心就在后面接 Fill Style 改回去 —— 样式电池永远能覆盖默认值。
                        Fill = new FillDef
                        {
                            Color = brush.Closed ? "#E8E6DF" : null,
                            Alpha = 1.0,
                            Rule = "nonzero",
                        },
                    };

                    outCmds.Add(new Colugo_Command(cmd));
                    total++;
                }
            }

            if (truncated || total >= MarkMath.MaxMarks)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Reached the {MarkMath.MaxMarks} mark limit. Increase Spacing or split the paths");
            }
            if (outCmds.Count == 0) return;

            DA.SetDataList(0, outCmds);
            var info = $"{outCmds.Count} marks · {spacing:0.##}px · {RotateLabels[rotate - 1]}";
            Message = info;
            DA.SetData(1, info);
        }

        /// <summary>确定性抖动：同一 (路径, 序号) 永远同一个角度，重算不会让画面跳。</summary>
        static double Hash01(string id, int i)
        {
            unchecked
            {
                uint h = 2166136261;
                if (id != null)
                    foreach (var ch in id) { h ^= (byte)ch; h *= 16777619; }
                h ^= (uint)i;
                h *= 16777619;
                h ^= h >> 13;
                return (h & 0xFFFFFF) / 16777216.0;
            }
        }
    }

    /// <summary>
    /// Style Map —— 沿路径把某个属性从 A 映射到 B。
    ///
    /// 输入的不是"曲线粗细曲线"，而是两个区间 + 一个伽马：
    /// 起点值、终点值、偏向。这样五个标记的粗细差别是连续的，
    /// 不需要再生成一组数值列表（那正是之前 Morph 被否决的原因）。
    ///
    /// 只改 mark 指令（MarkU ≥ 0）：把 Draw Path 的指令混进来时原样返回，
    /// 免得"整幅图被一起放大"这种意外。
    /// </summary>
    public class StyleMapComponent : GH_Component
    {
        public StyleMapComponent()
            : base("Style Map", "Map", "Maps a mark property along the path, from one value to another", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f3");
        protected override Bitmap Icon => Icons.StyleMap;

        static readonly string[] Props = { "size", "rotation", "opacity", "width" };
        static readonly string[] Labels = { "Size", "Rotation", "Opacity", "Stroke Width" };

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "Marks from Mark Along Path", GH_ParamAccess.list);
            p.AddIntegerParameter("Property", "Prop", "1 size · 2 rotation · 3 opacity · 4 stroke width", GH_ParamAccess.item, 1);
            p.AddIntervalParameter("In", "In", "Part of the path that participates, 0-1", GH_ParamAccess.item, new Interval(0, 1));
            p.AddIntervalParameter("Out", "Out", "Start and end value of the property", GH_ParamAccess.item, new Interval(1, 1));
            p.AddNumberParameter("Bias", "Bias", "Gamma on the path position. Below 1 front-loads, above 1 back-loads", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "New commands. The originals are left untouched", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Mapping summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Command>();
            int prop = 1;
            var inRange = new Interval(0, 1);
            var outRange = new Interval(1, 1);
            double bias = 1;

            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;
            DA.GetData(1, ref prop);
            DA.GetData(2, ref inRange);
            DA.GetData(3, ref outRange);
            DA.GetData(4, ref bias);

            if (prop < 1 || prop > 4) prop = 1;
            if (!(bias > 0)) bias = 1;
            if (outRange.T0 < 0 && prop != 2) outRange.T0 = 0;
            if (outRange.T1 < 0 && prop != 2) outRange.T1 = 0;

            var outCmds = new List<Colugo_Command>(cmds.Count);
            int mapped = 0, skipped = 0;

            foreach (var g in cmds)
            {
                if (g?.Value == null) continue;
                var c = g.Value.Duplicate();

                if (c.MarkU < 0)
                {
                    skipped++;
                    outCmds.Add(new Colugo_Command(c));
                    continue;
                }

                double v = MarkMath.Map(c.MarkU, inRange.T0, inRange.T1, outRange.T0, outRange.T1, bias);
                if (MarkMath.Apply(c, prop, v)) mapped++;
                else skipped++;
                outCmds.Add(new Colugo_Command(c));
            }

            DA.SetDataList(0, outCmds);

            string info = $"{Labels[prop - 1]} {outRange.T0:0.##} → {outRange.T1:0.##} · {mapped} mapped"
                + (skipped > 0 ? $" · {skipped} untouched" : "");
            Message = info;
            DA.SetData(1, info);
        }
    }
}
