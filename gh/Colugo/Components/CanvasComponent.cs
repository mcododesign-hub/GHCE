using System;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>Canvas —— 画布定义（阶段 0 最小电池之一）</summary>
    public class CanvasComponent : GH_Component
    {
        public CanvasComponent()
            : base("Canvas", "Canvas", "Defines the browser canvas: size, background, pixel ratio", "Colugo", "Output") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a1");

        protected override Bitmap Icon => Icons.Canvas;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Width", "W", "Canvas width in pixels", GH_ParamAccess.item, 1440);
            p.AddIntegerParameter("Height", "H", "Canvas height in pixels", GH_ParamAccess.item, 900);
            p.AddColourParameter("Background", "Bg", "Background color. Alpha=0 renders transparent", GH_ParamAccess.item, Color.FromArgb(255, 11, 11, 15));
            p.AddNumberParameter("DPI Scale", "DPR", "0 = follow device pixel ratio (capped at 2)", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Canvas(), "Canvas", "Canvas", "Canvas definition", GH_ParamAccess.item);
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
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Canvas size must be within 1..8192");
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

            DA.SetData(0, new Colugo_Canvas(def));
        }
    }
}
