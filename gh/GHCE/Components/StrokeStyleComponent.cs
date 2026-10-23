using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Stroke Style —— 改一批指令的描边。
    ///
    /// 为什么不给 Draw Path 加端口：它已经有 10 个输入（用户明确要求保持原样）。
    /// 样式扩展只能另开电池，做成"管道"：接在 Draw Path 之后，改完再送给 Render Output。
    /// 这样同一份几何可以被 `Draw Path → Stroke Style A` 和 `Draw Path → Stroke Style B`
    /// 分别解释成两种画笔 —— 正是任务书 1.4 原则二要的效果。
    ///
    /// **未接的端口 = 保留原值**，不是重置为默认。所以只改颜色时，线宽虚线原样不动。
    /// </summary>
    public class StrokeStyleComponent : GH_Component
    {
        public StrokeStyleComponent()
            : base("Stroke Style", "Stroke", "Override stroke on a batch of commands", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000e3");
        protected override Bitmap Icon => Icons.StrokeStyle;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to modify", GH_ParamAccess.list);
            p.AddColourParameter("Colour", "Colour", "Stroke color. Leave empty to keep the current value", GH_ParamAccess.item);
            p.AddNumberParameter("Width", "Width", "Stroke width in pixels. Leave empty to keep the current value", GH_ParamAccess.item);
            p.AddNumberParameter("Alpha", "Alpha", "Stroke alpha 0-1. Leave empty to keep the current value", GH_ParamAccess.item);
            p.AddTextParameter("Dash", "Dash", "Dash pattern, e.g. 4,3. Empty text restores a solid line", GH_ParamAccess.item);
            p.AddTextParameter("Cap", "Cap", "Line cap: butt, round or square", GH_ParamAccess.item);

            for (int i = 1; i <= 5; i++) p[i].Optional = true;
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
            double width = 0, alpha = 1;
            string dash = null, cap = null;

            bool hasColour = DA.GetData(1, ref colour) && !colour.IsEmpty;
            bool hasWidth = DA.GetData(2, ref width);
            bool hasAlpha = DA.GetData(3, ref alpha);
            bool hasDash = DA.GetData(4, ref dash);
            bool hasCap = DA.GetData(5, ref cap);

            if (hasWidth && width < 0) width = 0;
            if (hasCap)
            {
                cap = (cap ?? "").Trim().ToLowerInvariant();
                if (cap != "butt" && cap != "round" && cap != "square")
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Cap must be butt, round or square. Received \"{cap}\" and ignored it");
                    hasCap = false;
                }
            }

            string hex = hasColour ? $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}" : null;
            double[] dashArr = hasDash ? ParseDash(dash) : null;

            var outCmds = new List<GHCE_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                var c = g?.Value;
                if (c == null) continue;
                var d = c.Duplicate();
                d.Stroke = d.Stroke ?? new StrokeDef();

                if (hex != null) d.Stroke.Color = hex;
                if (hasWidth) d.Stroke.Width = width;
                if (hasAlpha) d.Stroke.Alpha = Clamp01(alpha);
                if (hasDash) d.Stroke.Dash = dashArr;      // 空文本 → null → 实线
                if (hasCap) d.Stroke.Cap = cap;

                outCmds.Add(new GHCE_Command(d));
            }

            DA.SetDataList(0, outCmds);

            var bits = new List<string>();
            if (hex != null) bits.Add(hex);
            if (hasWidth) bits.Add($"w {width:0.##}");
            if (hasAlpha) bits.Add($"α {Clamp01(alpha):0.##}");
            if (hasDash) bits.Add(dashArr == null ? "solid" : $"dash {string.Join(",", dashArr)}");
            if (hasCap) bits.Add(cap);
            Message = bits.Count == 0 ? "Nothing was modified" : string.Join(" · ", bits);
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static double[] ParseDash(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<double>(parts.Length);
            foreach (var part in parts)
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                    list.Add(v);
            return list.Count == 0 ? null : list.ToArray();
        }
    }
}
