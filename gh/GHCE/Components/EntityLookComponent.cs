using System;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;
using Rhino.Geometry;

namespace GHCE.Components
{
    /// <summary>
    /// Entity Look —— 只改外观，不动生存规则。
    ///
    /// v0.15 加了 Shape：粒子长什么样由你在 Rhino 里画的那**一条**曲线决定，
    /// 而不是我写死的方块。Shape 是 item 访问，接多条 GH 会直接报错 ——
    /// 这是故意的：一份蓝图只能有一种粒子形状，要么拆成两份蓝图。
    ///
    /// 形状坐标是**局部**的（以 bbox 中心为原点、最长边归一化成 1），
    /// 所以 Size = 1 表示"和原曲线一样大"，跟画布缩放无关。
    /// </summary>
    public class EntityLookComponent : GH_Component
    {
        public EntityLookComponent()
            : base("Entity Look", "Look", "粒子的形状 / 颜色 / 线宽 / 拖尾 / 图层", "GHCE", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a6");
        protected override Bitmap Icon => Icons.EntityLook;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Entity(), "Blueprint", "BP", "来自 Entity Blueprint", GH_ParamAccess.item);
            var shape = new Grasshopper.Kernel.Parameters.Param_Curve
            {
                Name = "Shape",
                NickName = "Shape",
                Description = "粒子形状：Rhino 里单条曲线（不要接多条）。不接 = 方块点",
                Access = GH_ParamAccess.item,
                Optional = true,
            };
            p.AddParameter(shape);
            p.AddColourParameter("Colour", "Col", "粒子颜色", GH_ParamAccess.item, Color.FromArgb(0xFD, 0x7A, 0xB4));
            p.AddNumberParameter("Width", "W", "形状描边宽度（像素）", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Size", "Size", "形状缩放：1 = 原始大小（按最长边归一化后）", GH_ParamAccess.item, 1.8);
            p.AddIntegerParameter("Trail", "Trail", "拖尾采样点数，0 = 关闭", GH_ParamAccess.item, 4);
            p.AddTextParameter("Layer", "Layer", "图层名（同名归为一层）", GH_ParamAccess.item, "particles");
            p.AddBooleanParameter("Fade", "Fade", "按寿命淡出", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Entity(), "Entities", "Ent", "带外观的蓝图，接到 Render Output", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var bp = new GHCE_Entity();
            if (!DA.GetData(0, ref bp) || bp.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "缺少 Blueprint");
                return;
            }

            Curve shapeCurve = null;
            var col = Color.Empty;
            double width = 1.0, size = 1.8;
            int trail = 4;
            string layer = "particles";
            bool fade = true;

            DA.GetData(1, ref shapeCurve);
            DA.GetData(2, ref col);
            DA.GetData(3, ref width);
            DA.GetData(4, ref size);
            DA.GetData(5, ref trail);
            DA.GetData(6, ref layer);
            DA.GetData(7, ref fade);

            var def = bp.Value.Duplicate();
            if (!col.IsEmpty) def.Color = $"#{col.R:x2}{col.G:x2}{col.B:x2}";
            def.Width = Math.Max(0.1, width);
            def.Size = Math.Max(0.05, size);
            def.Trail = Math.Max(0, Math.Min(32, trail));
            def.Layer = string.IsNullOrWhiteSpace(layer) ? "particles" : layer;
            def.Fade = fade;
            def.Shape = shapeCurve == null ? null : LocalShape(shapeCurve);

            DA.SetData(0, new GHCE_Entity(def));
        }

        /// <summary>
        /// 把一条曲线变成"以中心为原点、最长边 = 1"的局部折线。
        /// 为什么归一化：粒子大小应该由 Size 一个端口控制，而不是"你在 Rhino 里画多大就多大"。
        /// </summary>
        PathDef LocalShape(Curve c)
        {
            if (c == null || !c.IsValid) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Shape 曲线无效，已忽略"); return null; }

            var pts = PathInputComponent.Sample(c, 0.4);
            if (pts == null || pts.Length < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Shape 采样点数不足，已忽略");
                return null;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var pt in pts)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
            double span = Math.Max(maxX - minX, maxY - minY);
            if (span <= 1e-9) span = 1;

            var xy = new double[pts.Length * 2];
            for (int k = 0; k < pts.Length; k++)
            {
                xy[k * 2] = (pts[k].X - cx) / span;
                xy[k * 2 + 1] = (pts[k].Y - cy) / span;   // 局部空间不做 Y 翻转：运行时按页面坐标直接放
            }

            // Id 只为协议里给个稳定标识，不参与绘制
            return new PathDef { Id = "shape", Closed = c.IsClosed, XY = xy, IsPoints = false, Proj = null };
        }
    }
}
