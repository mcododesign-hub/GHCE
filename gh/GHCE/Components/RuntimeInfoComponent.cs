using System;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Bridge;
using GHCE.Ui;

namespace GHCE.Components
{
    /// <summary>
    /// Runtime Info —— 自检面板。
    /// GH 里原本完全看不见画布那边发生了什么：多少对象、帧率多少、上一帧画了多久。
    /// 这里把浏览器每 200ms 回传的 stat 摊开，出问题时有数可看，而不是靠盯着窗口猜。
    /// </summary>
    public class RuntimeInfoComponent : GH_Component
    {
        public RuntimeInfoComponent()
            : base("Runtime Info", "Info", "浏览器侧自检：帧率 / 对象数 / 绘制耗时 / 连接数", "GHCE", "Input") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ac");
        protected override Bitmap Icon => Icons.RuntimeInfo;

        protected override void RegisterInputParams(GH_InputParamManager p) { }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("FPS", "FPS", "浏览器实际绘帧率", GH_ParamAccess.item);
            p.AddIntegerParameter("Entities", "Ent", "运行时对象数", GH_ParamAccess.item);
            p.AddNumberParameter("Draw ms", "Draw", "上一帧绘制耗时", GH_ParamAccess.item);
            p.AddNumberParameter("Step ms", "Step", "上一帧模拟耗时", GH_ParamAccess.item);
            p.AddIntegerParameter("Rev", "Rev", "运行时已应用的文档版本", GH_ParamAccess.item);
            p.AddIntegerParameter("Clients", "Cli", "已连接的预览端数量", GH_ParamAccess.item);
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
            bool fresh = RuntimeStat.Fresh;
            if (!fresh)
            {
                Message = "无数据";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "还没收到浏览器回传（预览窗是否打开？）");
            }
            else
            {
                Message = $"{RuntimeStat.Fps:0} fps · {RuntimeStat.Entities} obj";
            }

            DA.SetData(0, fresh ? RuntimeStat.Fps : 0);
            DA.SetData(1, fresh ? RuntimeStat.Entities : 0);
            DA.SetData(2, fresh ? RuntimeStat.DrawMs : 0);
            DA.SetData(3, fresh ? RuntimeStat.StepMs : 0);
            DA.SetData(4, fresh ? RuntimeStat.Rev : 0);
            DA.SetData(5, BridgeServer.ClientCount);
        }
    }
}
