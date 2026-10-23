using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Draw Path —— 生成 Draw Command，不直接画图。
    /// 这一层刻意只产出"可排序、可组合、可更新"的描述，
    /// 因为同一个几何要能被多种画笔重复解释（任务书 1.4 原则二）。
    /// </summary>
    public class DrawPathComponent : GH_Component
    {
        public DrawPathComponent()
            : base("Draw Path", "Draw", "Paths plus style to draw commands", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a3");
        protected override Bitmap Icon => Icons.DrawPath;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Path(), "Paths", "Paths", "Paths from Path Input, Morph or Transform", GH_ParamAccess.list);
            p.AddTextParameter("Layer", "Layer", "Layer name. Same name shares one layer", GH_ParamAccess.item, "main");
            p.AddIntegerParameter("Order", "Order", "Draw order within the layer, starting at 0", GH_ParamAccess.item, 0);
            p.AddColourParameter("Stroke", "Stroke", "Stroke color", GH_ParamAccess.item, Color.FromArgb(232, 230, 223));
            p.AddNumberParameter("Width", "Width", "Stroke width in pixels", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Alpha", "Alpha", "Stroke alpha, 0-1", GH_ParamAccess.item, 1.0);
            p.AddTextParameter("Dash", "Dash", "Dash pattern, e.g. 4,3. Empty means solid", GH_ParamAccess.item);
            p.AddColourParameter("Fill", "Fill", "Fill color. Leave empty for no fill", GH_ParamAccess.item);
            p.AddNumberParameter("Fill Alpha", "FA", "Fill alpha", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Opacity", "Opacity", "Overall command opacity", GH_ParamAccess.item, 1.0);

            p[6].Optional = true;
            p[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Draw command", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var paths = new List<GHCE_Path>();
            if (!DA.GetDataList(0, paths) || paths.Count == 0) return;

            string layer = "main";
            int order = 0;
            var stroke = Color.Empty;
            double width = 1.0, alpha = 1.0, fillAlpha = 1.0, opacity = 1.0;
            string dash = null;
            var fill = Color.Empty;

            DA.GetData(1, ref layer);
            DA.GetData(2, ref order);
            DA.GetData(3, ref stroke);
            DA.GetData(4, ref width);
            DA.GetData(5, ref alpha);
            DA.GetData(6, ref dash);
            DA.GetData(7, ref fill);
            DA.GetData(8, ref fillAlpha);
            DA.GetData(9, ref opacity);

            var outCmds = new List<GHCE_Command>(paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i]?.Value;
                if (p == null || p.XY == null || p.XY.Length < 2) continue;

                var cmd = new CommandDef
                {
                    Id = CommandIdFor(p.Id),
                    GeomId = p.Id,
                    Path = p,                                  // 几何引用：同几何多指令时只传一次
                    Layer = string.IsNullOrWhiteSpace(layer) ? "main" : layer,
                    Order = order + i,                         // 保留列表顺序，避免同 Order 的隐性不确定
                    Visible = true,
                    Opacity = Clamp01(opacity),
                    PointSize = 3.0,
                    Stroke = new StrokeDef
                    {
                        Color = stroke.IsEmpty ? "#E8E6DF" : ToHex(stroke),
                        Width = Math.Max(0, width),
                        Alpha = Clamp01(alpha),
                        Cap = "round",
                        Join = "round",
                        Dash = ParseDash(dash)
                    },
                    Fill = new FillDef
                    {
                        Color = fill.IsEmpty ? null : ToHex(fill),
                        Alpha = Clamp01(fillAlpha),
                        Rule = "nonzero"
                    }
                };

                // 几何层（Transform / Morph）带来的动画信息，这里只做转交，不解释。
                // 分层的好处：样式与时间互不认识，但可以在同一条指令上汇合。
                if (p.Matrix != null && p.Matrix.Length >= 6)
                {
                    cmd.Matrix = (double[])p.Matrix.Clone();
                    cmd.Progress = 1.0;
                }
                else if (p.MatrixFrames != null && p.MatrixN >= 2)
                {
                    cmd.Track = new TrackDef
                    {
                        N = p.MatrixN,
                        Frames = (double[])p.MatrixFrames.Clone(),
                        Start = 0,
                        End = -1,
                        Hidden = false,
                    };
                    cmd.Progress = 1.0;
                }

                if (p.Morph != null) cmd.Morph = p.Morph;

                outCmds.Add(new GHCE_Command(cmd));
            }

            DA.SetDataList(0, outCmds);
        }

        static string CommandIdFor(string pathId)
        {
            if (string.IsNullOrEmpty(pathId)) return "c_0";
            return pathId.StartsWith("g_", StringComparison.Ordinal) ? "c_" + pathId.Substring(2) : "c_" + pathId;
        }

        static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static double[] ParseDash(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<double>(parts.Length);
            foreach (var part in parts)
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                    list.Add(v);
            }
            return list.Count == 0 ? null : list.ToArray();
        }
    }
}
