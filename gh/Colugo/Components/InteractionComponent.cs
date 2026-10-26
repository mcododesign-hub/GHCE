using System;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Bridge;
using Colugo.Ui;
using Rhino.Geometry;

namespace Colugo.Components
{
    /// <summary>
    /// Interaction —— 把浏览器里的鼠标/键盘变成一个真正的 Grasshopper 数据源。
    ///
    /// 坐标说明：输出的是**弹出画布的页面坐标**（左上原点、Y 向下、单位像素），
    /// 不是 Rhino 世界坐标。想拿去驱动几何，先自己决定怎么映射（除以画布高宽、翻转 Y…），
    /// 这一步不该由插件替你猜。
    /// </summary>
    public class InteractionComponent : GH_Component
    {
        public InteractionComponent()
            : base("Interaction", "Input", "Pointer, click and key state from the browser, in canvas pixels", "Colugo", "Input") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ab");
        protected override Bitmap Icon => Icons.Interaction;

        protected override void RegisterInputParams(GH_InputParamManager p) { }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter("Pointer", "Pt", "Pointer position in canvas pixels, origin top-left, Y down", GH_ParamAccess.item);
            p.AddBooleanParameter("Inside", "In", "Whether the pointer is inside the canvas", GH_ParamAccess.item);
            p.AddBooleanParameter("Down", "Down", "Whether the pointer is pressed", GH_ParamAccess.item);
            p.AddIntegerParameter("Clicks", "Clicks", "Accumulated click count", GH_ParamAccess.item);
            p.AddTextParameter("Keys", "Keys", "Currently held keys, lowercase", GH_ParamAccess.item);
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            StatTicker.Subscribe(this);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StatTicker.Unsubscribe(this);
            base.RemovedFromDocument(document);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (!RuntimeStat.Fresh)
            {
                Message = "No data";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "No telemetry yet. Is the preview window open?");
            }
            else
            {
                Message = $"{Math.Round(RuntimeStat.PtrX)}, {Math.Round(RuntimeStat.PtrY)}";
            }

            DA.SetData(0, new Point3d(RuntimeStat.PtrX, RuntimeStat.PtrY, 0));
            DA.SetData(1, RuntimeStat.Inside);
            DA.SetData(2, RuntimeStat.Down);
            DA.SetData(3, RuntimeStat.Clicks);
            DA.SetData(4, RuntimeStat.Keys ?? "");
        }
    }
}
