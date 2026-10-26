using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Layer —— 把一批指令收进一个显式图层。
    ///
    /// V0.9 之前图层顺序是"首次出现序号 × 10"的隐式推导：谁先连进 Render Output 谁在下面，
    /// 调顺序只能靠改接线。任务书阶段 1 D 要求"图层与绘画顺序明确且结果可重复"，
    /// 所以顺序、不透明度、混合模式必须能被显式指定。
    ///
    /// 运行时的 layer.upsert 早就支持 order / opacity / blend / visible（renderer2d.js），
    /// 缺的只是 GH 这一侧把它发出去。
    /// </summary>
    public class LayerComponent : GH_Component
    {
        public LayerComponent()
            : base("Layer", "Layer", "Assigns commands to an explicit layer", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000e2");
        protected override Bitmap Icon => Icons.Layer;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "Commands to assign", GH_ParamAccess.list);
            p.AddTextParameter("Name", "Name", "Layer name. Same name shares one layer", GH_ParamAccess.item, "main");
            p.AddNumberParameter("Order", "Order", "Layer order: lower draws first. Negative values go beneath default layers", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Opacity", "Opacity", "Layer alpha 0-1, multiplied with per-command opacity", GH_ParamAccess.item, 1.0);
            p.AddTextParameter("Blend", "Blend", "Blend mode: source-over, multiply, screen, overlay, lighter, difference and more. Empty means source-over", GH_ParamAccess.item, "source-over");
            p.AddBooleanParameter("Visible", "On", "Layer visibility", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds", "New commands carrying the layer definition", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            string name = "main", blend = "source-over";
            double order = 0, opacity = 1;
            bool visible = true;

            DA.GetData(1, ref name);
            DA.GetData(2, ref order);
            DA.GetData(3, ref opacity);
            DA.GetData(4, ref blend);
            DA.GetData(5, ref visible);

            var def = new LayerDef
            {
                Name = string.IsNullOrWhiteSpace(name) ? "main" : name.Trim(),
                Order = order,
                Opacity = Clamp01(opacity),
                Blend = LayerDef.NormalizeBlend(blend),
                Visible = visible
            };

            if (!def.BlendIsKnown(blend))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Unknown blend mode \"{blend}\", fell back to source-over. Valid values: {string.Join(" / ", LayerDef.Blends)}");
            }

            var outCmds = new List<Colugo_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                var c = g?.Value;
                if (c == null) continue;
                var d = c.Duplicate();          // 不改上游对象：同一批指令要能被多个 Layer 分别解释
                d.Layer = def.Name;
                d.LayerDef = def.Duplicate();
                outCmds.Add(new Colugo_Command(d));
            }

            DA.SetDataList(0, outCmds);
            Message = $"{def.Name} #{order:0.#} · {def.Opacity:0.##} · {def.Blend}{(def.Visible ? "" : " · hidden")}";
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
