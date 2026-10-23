using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using GHCE.Bridge;
using GHCE.Types;
using Rhino.Geometry;

namespace GHCE.Components
{
    /// <summary>
    /// Morph —— 把一串几何打包成可插值的帧序列。
    ///
    /// ```
    /// 原始曲线 ─► 你的任意运算（fillet / offset / 布尔 / 自定义脚本）─► (N 条) ─► Morph ─► Track
    /// ```
    ///
    /// **电池不认识你在中间做了什么，也不提供任何操作。** 它只接受一个事实：
    /// 这 N 条曲线是同一样东西的 N 个时刻，列表下标就是时间。
    ///
    /// 为什么一个操作都不给：
    ///   · PSR 已经有 Transform 电池，放这里是重复；
    ///   · 倒角只是例子，还有布尔、放样、细分、你自己写的 C# —— 枚举永远列不全，
    ///     列出来的那一刻就把没用到的可能性关在门外了；
    ///   · 采样应该发生在 GH 原生电池里，那里你能连任何东西。
    ///
    /// 唯一的技术责任是**让插值成立**：
    ///   1. 按归一化长度固定取 POINTS 个点（不按弦高 —— 弦高会让 offset 后点数变化，
    ///      点数一变就没有一一对应的顶点可插，整条 morph 会被运行时丢弃）
    ///   2. 投影用所有帧的联合包围盒（用第一帧会出框，用每帧各自会让画面随参数"呼吸"）
    /// </summary>
    public class MorphComponent : GH_Component
    {
        /// <summary>每条曲线的固定采样点数。不开放成端口：这是"能不能插值"的技术参数，不是创作参数。</summary>
        const int POINTS = 96;

        public MorphComponent()
            : base("Morph", "Morph",
                   "Packs N geometries into an interpolatable frame sequence. List order is time order",
                   "GHCE", "Animation") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b3");
        protected override Bitmap Icon => Icons.Morph;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter("Geometries", "Geo",
                "N states of the same shape. List order is time order; the operation that produced them is irrelevant here",
                GH_ParamAccess.list);

            p.AddNumberParameter("Step", "Step",
                "Frames per second. Natural clip length is (N-1)/Step",
                GH_ParamAccess.item, 12.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Path(), "Paths", "Paths", "Paths carrying a vertex sequence, feed into Draw Path", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var geos = new List<Curve>();
            double step = 12;

            if (!DA.GetDataList(0, geos) || geos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Geometries");
                return;
            }
            DA.GetData(1, ref step);
            if (step <= 1e-6) step = 12;

            geos = geos.Where(c => c != null && c.IsValid).ToList();
            if (geos.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "At least 2 curves are required. One frame cannot form an animation");
                return;
            }

            int n = geos.Count;
            var raw = new double[n][];
            for (int k = 0; k < n; k++) raw[k] = SampleXY(geos[k], POINTS);

            // ---- 投影：所有帧的联合包围盒 ----
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var f in raw)
            {
                for (int i = 0; i < f.Length; i += 2)
                {
                    if (f[i] < minX) minX = f[i];
                    if (f[i] > maxX) maxX = f[i];
                    if (f[i + 1] < minY) minY = f[i + 1];
                    if (f[i + 1] > maxY) maxY = f[i + 1];
                }
            }

            const double PW = 1440, PH = 900, MARGIN = 60;
            double dx = Math.Max(maxX - minX, 1e-9);
            double dy = Math.Max(maxY - minY, 1e-9);
            double scale = Math.Min((PW - 2 * MARGIN) / dx, (PH - 2 * MARGIN) / dy);
            if (double.IsInfinity(scale) || scale <= 0) scale = 1;

            double offY = (PH - dy * scale) / 2.0;
            double ox = (PW - dx * scale) / 2.0 - minX * scale;
            double oy = PH - offY + minY * scale;

            var page = new double[n * POINTS * 2];
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < POINTS; i++)
                {
                    int o = (k * POINTS + i) * 2;
                    page[o] = ox + raw[k][i * 2] * scale;
                    page[o + 1] = oy - raw[k][i * 2 + 1] * scale;   // Y 向下翻转
                }
            }

            bool closed = geos[0].IsClosed;

            var morph = new MorphDef
            {
                N = n,
                Count = POINTS,
                Step = step,
                Frames = page,
                Closed = closed,
            };

            // 承载用的 Path 取第一帧：morph 被判为不合格时它仍是一条合法路径，整条指令不会消失
            var first = new double[POINTS * 2];
            Array.Copy(page, 0, first, 0, POINTS * 2);

            string gid = PatchBuilder.StableId("g_morph_", IdentityCoords(geos[0]));
            var pathDef = new PathDef
            {
                Id = gid,
                XY = first,
                Closed = closed,
                IsPoints = false,
                Proj = new[] { scale, ox, oy },
            };

            pathDef.Morph = morph;

            double natural = (n - 1) / step;
            Message = $"{n} frames · {POINTS} points · {natural:0.##}s at {step:0.##}fps";

            // 只吐 Paths —— 样式归 Draw Path，时间归 Track。Morph 只管"顶点在时间上怎么变"
            DA.SetDataList(0, new[] { new GHCE_Path(pathDef) });
        }

        /// <summary>
        /// 固定点数采样：按归一化长度取 count 个点。
        /// 关键是"点数固定"而不是"按弦高"—— 弦高采样会让 offset 后的点数变化，
        /// 而点数一变就没有一一对应的顶点可插。
        /// </summary>
        static double[] SampleXY(Curve c, int count)
        {
            var arr = new double[count * 2];
            for (int k = 0; k < count; k++)
            {
                Point3d p;
                try
                {
                    p = c.PointAtNormalizedLength(k / (double)(count - 1));
                }
                catch
                {
                    p = c.PointAtStart;
                }
                arr[k * 2] = p.X;
                arr[k * 2 + 1] = p.Y;
            }
            return arr;
        }

        /// <summary>身份基准：控制点。参数变化不改变身份，运行时不会"换人"。</summary>
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
