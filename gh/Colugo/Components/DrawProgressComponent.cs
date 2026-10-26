using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Draw Progress —— Stroke Reveal（阶段 3 C）。
    /// 不给每条指令单独一个进度口（那会让 Draw Path 变成十几口），
    /// 而是在这里一次性把整组指令按 Spread 错开：0 = 一起画，1 = 依次画完。
    /// </summary>
    public class DrawProgressComponent : GH_Component
    {
        public DrawProgressComponent()
            : base("Draw Progress", "Progress", "Progressive reveal along paths: progress and spread", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a8");
        protected override Bitmap Icon => Icons.DrawProgress;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "Commands from Draw Path", GH_ParamAccess.list);
            p.AddNumberParameter("Progress", "P", "0-1: how far the whole group is drawn", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Spread", "S", "0-1: stagger between paths. 0 draws them together", GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "Commands with reveal applied", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            double progress = 1.0, spread = 0.0;
            DA.GetData(1, ref progress);
            DA.GetData(2, ref spread);

            progress = Math.Max(0, Math.Min(1, progress));
            spread = Math.Max(0, Math.Min(1, spread));

            int n = cmds.Count;
            var outCmds = new List<Colugo_Command>(n);

            for (int i = 0; i < n; i++)
            {
                var src = cmds[i]?.Value;
                if (src == null) continue;

                // 第 i 条的起始偏移：spread=0 时全为 0（同时开始），spread=1 时首尾相差一整段
                double offset = n > 1 ? spread * (i / (double)(n - 1)) : 0;
                double local = (progress * (1 + spread) - offset) / 1.0;
                local = Math.Max(0, Math.Min(1, local));

                var copy = src.Duplicate();
                copy.Reveal = local;
                outCmds.Add(new Colugo_Command(copy));
            }

            DA.SetDataList(0, outCmds);
        }
    }
}
