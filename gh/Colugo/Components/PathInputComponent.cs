using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Colugo.Bridge;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Path Input —— 把 Rhino 曲线变成"Page-space paths"。
    /// 这一步完成了任务书里的"世界坐标 → 页面坐标投影"，运行时因此完全不懂 Rhino。
    /// 身份 ID 由 NurbsCurve 控制点指纹生成：改采样容差不会改变身份，改曲线形状才会。
    ///
    /// v0.10：页面尺寸 / 内边距 / 缩放移交 <see cref="ProjectTo2DComponent"/> 产的相机。
    /// 端口从 6 个减到 4 个，且相机可以只算一次、整个动画共用 —— 这是"细长条转 90° 被裁"的正解。
    /// 没接 Camera 时行为与 v0.9 完全一致（1440×900 / margin 60 自动拟合）。
    /// </summary>
    public class PathInputComponent : GH_Component
    {
        public PathInputComponent()
            : base("Path Input", "Path In", "Rhino curves to page-space paths with stable IDs", "Colugo", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a2");
        protected override Bitmap Icon => Icons.PathInput;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter("Geometry", "Geo", "Curves to convert", GH_ParamAccess.list);
            p.AddNumberParameter("Tolerance", "Tol", "Sampling chord tolerance in page pixels", GH_ParamAccess.item, 1.0);
            p.AddParameter(new Param_Colugo_Camera(), "Camera", "Cam",
                "Optional camera from Project to 2D. Falls back to a 1440x900 fit with 60px margin", GH_ParamAccess.item);
            p.AddTextParameter("Id", "Id", "Optional explicit identity. Defaults to a geometry fingerprint", GH_ParamAccess.list);
            p[2].Optional = true;
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Path(), "Paths", "Paths", "Page-space paths", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Sampling summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var geo = new List<Curve>();
            var ids = new List<string>();
            double tol = 1.0;
            Colugo_Camera camGoo = null;

            if (!DA.GetDataList(0, geo) || geo.Count == 0) return;
            DA.GetData(1, ref tol);
            DA.GetData(2, ref camGoo);
            DA.GetDataList(3, ids);

            if (tol <= 0) tol = 0.5;

            // ---- 第一遍：采样 ----
            var items = new List<(Curve c, Point3d[] pts, bool closed, string id)>(geo.Count);
            int totalPts = 0;

            for (int i = 0; i < geo.Count; i++)
            {
                var c = geo[i];
                if (c == null || !c.IsValid) continue;

                var pts = Sample(c, tol);
                if (pts == null || pts.Length < 2)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Curve {i} produced too few samples and was skipped");
                    continue;
                }

                string id = (i < ids.Count && !string.IsNullOrWhiteSpace(ids[i]))
                    ? ids[i].Trim()
                    : PatchBuilder.StableId("g_", IdentityCoords(c));

                items.Add((c, pts, c.IsClosed, id));
                totalPts += pts.Length;
            }

            if (items.Count == 0) return;

            // ---- 相机：优先用上游 Camera，缺省按 v0.9 的规则就地拟合（1440×900 / margin 60）----
            var box = CameraMath.EmptyBox();
            foreach (var it in items)
                foreach (var pt in it.pts)
                    CameraMath.Extend(box, pt.X, pt.Y);

            double[] proj;
            string camSource;
            var camDef = camGoo == null ? null : camGoo.Value;

            if (camDef != null && camDef.Proj != null && camDef.Proj.Length == 3)
            {
                proj = camDef.Proj;
                camSource = "camera";
            }
            else
            {
                proj = CameraMath.Fit(CameraMath.IsEmpty(box) ? new double[] { 0, 0, 1, 1 } : box, 1440, 900, 60, 0);
                camSource = "auto";
            }

            var outPaths = new List<Colugo_Path>(items.Count);
            foreach (var it in items)
            {
                var xy = new double[it.pts.Length * 2];
                for (int k = 0; k < it.pts.Length; k++)
                {
                    CameraMath.ToPage(proj, it.pts[k].X, it.pts[k].Y, out var px, out var py);
                    xy[k * 2] = px;
                    xy[k * 2 + 1] = py;
                }

                var def = new PathDef { Id = it.id, Closed = it.closed, XY = xy, IsPoints = false, Proj = proj };
                outPaths.Add(new Colugo_Path(def));
            }

            DA.SetDataList(0, outPaths);
            DA.SetData(1, $"{outPaths.Count} paths · {totalPts} pts · scale {proj[0]:0.###} · cam ({proj[1]:0.#},{proj[2]:0.#}) · {camSource}");
        }

        internal static Point3d[] Sample(Curve c, double tol)
        {
            if (c.TryGetPolyline(out Polyline pl))
            {
                var a = pl.ToArray();
                if (a.Length >= 2) return a;
            }

            double len = c.GetLength();
            if (len <= 0) return null;

            int n = (int)Math.Ceiling(len / tol);
            n = Math.Clamp(n, 2, 4000);

            // 按归一化弧长采样：与曲线速度分布无关，采样密度才真的随 Tol 均匀。
            // 不用 DivideByCount —— 它有几个重载返回 double[]（参数值）而不是 Point3d[]，容易踩错。
            try
            {
                var pts = new Point3d[n + 1];
                for (int k = 0; k <= n; k++)
                    pts[k] = c.PointAtNormalizedLength(k / (double)n);
                return pts;
            }
            catch (Exception)
            {
                return new[] { c.PointAtStart, c.PointAtEnd };
            }
        }

        /// <summary>身份基准：控制点。与采样容差无关，因此 Tol 变化不会让运行时的对象"换人"。</summary>
        static double[] IdentityCoords(Curve c)
        {
            var nc = c.ToNurbsCurve();
            if (nc != null && nc.Points.Count >= 2)
            {
                var arr = new double[nc.Points.Count * 3];
                for (int i = 0; i < nc.Points.Count; i++)
                {
                    var p = nc.Points[i].Location;
                    arr[i * 3] = p.X;
                    arr[i * 3 + 1] = p.Y;
                    arr[i * 3 + 2] = p.Z;
                }
                return arr;
            }
            return new[] { c.PointAtStart.X, c.PointAtStart.Y, c.PointAtEnd.X, c.PointAtEnd.Y };
        }
    }
}
