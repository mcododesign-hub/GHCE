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
            : base("Draw Path", "Draw", "路径 + 样式 → Draw Command", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a3");
        protected override Bitmap Icon => Icons.DrawPath;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Path(), "Paths", "Paths", "来自 Path Input 的路径", GH_ParamAccess.list);
            p.AddTextParameter("Layer", "Layer", "图层名（同名归为一层）", GH_ParamAccess.item, "main");
            p.AddIntegerParameter("Order", "Order", "层内绘制顺序，从 0 开始", GH_ParamAccess.item, 0);
            p.AddColourParameter("Stroke", "Stroke", "描边颜色", GH_ParamAccess.item, Color.FromArgb(232, 230, 223));
            p.AddNumberParameter("Width", "Width", "线宽（像素）", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Alpha", "Alpha", "描边不透明度 0-1", GH_ParamAccess.item, 1.0);
            p.AddTextParameter("Dash", "Dash", "虚线，如 4,3；留空为实线", GH_ParamAccess.item);
            p.AddColourParameter("Fill", "Fill", "填充颜色（不接则不填充）", GH_ParamAccess.item);
            p.AddNumberParameter("Fill Alpha", "FA", "填充不透明度", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Opacity", "Opacity", "整条指令的不透明度", GH_ParamAccess.item, 1.0);

            p[6].Optional = true;
            p[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "绘图指令", GH_ParamAccess.list);
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
