using System;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>Canvas —— 画布定义（阶段 0 最小电池之一）</summary>
    public class CanvasComponent : GH_Component
    {
        public CanvasComponent()
            : base("Canvas", "Canvas", "定义浏览器画布：尺寸 / 背景 / 像素比", "GHCE", "Output") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a1");

        protected override Bitmap Icon => Icons.Canvas;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Width", "W", "画布宽度（像素）", GH_ParamAccess.item, 1440);
            p.AddIntegerParameter("Height", "H", "画布高度（像素）", GH_ParamAccess.item, 900);
            p.AddColourParameter("Background", "Bg", "背景色，Alpha=0 表示透明", GH_ParamAccess.item, Color.FromArgb(255, 11, 11, 15));
            p.AddNumberParameter("DPI Scale", "DPR", "0 = 跟随浏览器设备像素比（上限 2）", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Canvas(), "Canvas", "Canvas", "画布定义", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int w = 1440, h = 900;
            double dpr = 0;
            var bg = Color.Empty;

            DA.GetData(0, ref w);
            DA.GetData(1, ref h);
            DA.GetData(2, ref bg);
            DA.GetData(3, ref dpr);

            if (w <= 0 || h <= 0 || w > 8192 || h > 8192)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "画布尺寸需在 1..8192 之间");
                return;
            }

            var def = new CanvasDef
            {
                W = w,
                H = h,
                Bg = bg.IsEmpty ? "#0b0b0f" : $"#{bg.R:x2}{bg.G:x2}{bg.B:x2}",
                Transparent = !bg.IsEmpty && bg.A == 0,
                Dpr = dpr
            };

            DA.SetData(0, new GHCE_Canvas(def));
        }
    }
}
