using System;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Bridge;
using GHCE.Ui;
using Rhino.Geometry;

namespace GHCE.Components
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
            : base("Interaction", "Input", "读取浏览器里的指针 / 点击 / 按键（画布坐标）", "GHCE", "Input") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ab");
        protected override Bitmap Icon => Icons.Interaction;

        protected override void RegisterInputParams(GH_InputParamManager p) { }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter("Pointer", "Pt", "指针位置（画布像素，左上原点，Y 向下）", GH_ParamAccess.item);
            p.AddBooleanParameter("Inside", "In", "指针是否在画布内", GH_ParamAccess.item);
            p.AddBooleanParameter("Down", "Down", "是否按下", GH_ParamAccess.item);
            p.AddIntegerParameter("Clicks", "Clicks", "累计点击次数", GH_ParamAccess.item);
            p.AddTextParameter("Keys", "Keys", "当前按下的键（小写，如 wd）", GH_ParamAccess.item);
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
                Message = "无数据";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "还没收到浏览器回传（预览窗是否打开？）");
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
