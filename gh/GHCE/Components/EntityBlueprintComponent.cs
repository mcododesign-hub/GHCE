using System;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Entity Blueprint —— 只知道"多少个粒子、沿哪条曲线、跑多快、活多久"。
    ///
    /// v0.15 去掉了 Seed 和 Vary：随机让画面"看起来有生命"，但同时让"特定规则"做不到。
    /// 现在的分布是完全确定的：第 i 个粒子的归一化弧长位置 u = (i / Count + Phase) % 1。
    /// 要每个粒子不一样，你在 GH 里给不同的 Speed / 多条蓝图，而不是我掷骰子。
    ///
    /// 也没有 Behavior 电池了：第一个输入就是 Path，"沿这条曲线跑"就是唯一语义，
    /// 不需要再用一个 Mode 端口在下拉里选 —— 那等于我替你决定可能性的全集。
    /// </summary>
    public class EntityBlueprintComponent : GH_Component
    {
        public EntityBlueprintComponent()
            : base("Entity Blueprint", "Blueprint", "沿一条曲线运动的动态对象：数量 / 速度 / 寿命 / 相位", "GHCE", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a5");
        protected override Bitmap Icon => Icons.EntityBlueprint;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Path(), "Path", "Path", "粒子要沿的曲线（原生 Curve 或 Path Input）", GH_ParamAccess.item);
            p.AddIntegerParameter("Count", "N", "实例数量", GH_ParamAccess.item, 200);
            p.AddNumberParameter("Speed", "Speed", "每秒推进的归一化弧长（1 = 一秒跑完整条）", GH_ParamAccess.item, 0.06);
            p.AddNumberParameter("Lifetime", "Life", "寿命（秒）。0 = 永不重生，跑完一圈接着跑", GH_ParamAccess.item, 7.0);
            p.AddNumberParameter("Phase", "Phase", "整体相位偏移（0..1）。接时间进来就是整组往前挪", GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Entity(), "Blueprint", "BP", "动态对象蓝图", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var pathGoo = new GHCE_Path();
            if (!DA.GetData(0, ref pathGoo) || pathGoo.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "缺少路径输入（可直连 Curve）");
                return;
            }

            int count = 200;
            double speed = 0.06, life = 7, phase = 0;
            DA.GetData(1, ref count);
            DA.GetData(2, ref speed);
            DA.GetData(3, ref life);
            DA.GetData(4, ref phase);

            if (count < 0 || count > 20000)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Count 需在 0..20000 之间");
                return;
            }

            var def = new EntityDef
            {
                Id = GHCE_Entity.EntityIdFor(pathGoo.Value.Id),
                Path = pathGoo.Value,
                Count = count,
                Speed = Math.Max(0.0, speed),
                Lifetime = Math.Max(0.0, life),
                Phase = phase,
            };

            Message = $"{count} × {speed:0.###}/s";
            DA.SetData(0, new GHCE_Entity(def));
        }
    }
}
