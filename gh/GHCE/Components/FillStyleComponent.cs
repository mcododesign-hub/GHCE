using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Fill Style —— 改一批指令的填充。与 Stroke Style 对称：未接的端口保留原值。
    ///
    /// 想要"取消填充"就打开 Clear（因为"把颜色拔掉"在 GH 里没法表达：
    /// 未接的 Colour 与"故意不填"是同一个状态，必须另有开关区分）。
    /// </summary>
    public class FillStyleComponent : GH_Component
    {
        public FillStyleComponent()
            : base("Fill Style", "Fill", "Override fill on a batch of commands", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000e4");
        protected override Bitmap Icon => Icons.FillStyle;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to modify", GH_ParamAccess.list);
            p.AddColourParameter("Colour", "Colour", "Fill color. Leave empty to keep the current value", GH_ParamAccess.item);
            p.AddNumberParameter("Alpha", "Alpha", "Fill alpha 0-1. Leave empty to keep the current value", GH_ParamAccess.item);
            p.AddBooleanParameter("Clear", "Clear", "Clear the fill. Takes precedence over Colour", GH_ParamAccess.item, false);

            for (int i = 1; i <= 2; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "New commands. The originals are left untouched", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            var colour = Color.Empty;
            double alpha = 1;
            bool clear = false;

            bool hasColour = DA.GetData(1, ref colour) && !colour.IsEmpty;
            bool hasAlpha = DA.GetData(2, ref alpha);
            DA.GetData(3, ref clear);

            string hex = hasColour ? $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}" : null;

            var outCmds = new List<GHCE_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                var c = g?.Value;
                if (c == null) continue;
                var d = c.Duplicate();

                if (clear)
                {
                    d.Fill = new FillDef { Color = null, Alpha = 1.0, Rule = "nonzero" };
                }
                else
                {
                    d.Fill = d.Fill ?? new FillDef();
                    if (hex != null) d.Fill.Color = hex;
                    if (hasAlpha) d.Fill.Alpha = Clamp01(alpha);
                }

                outCmds.Add(new GHCE_Command(d));
            }

            DA.SetDataList(0, outCmds);
            Message = clear ? "no fill" : (hex == null ? "Nothing was modified" : hex + (hasAlpha ? $" · α {Clamp01(alpha):0.##}" : ""));
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
