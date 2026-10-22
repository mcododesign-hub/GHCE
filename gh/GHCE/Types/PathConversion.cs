using System;
using Rhino.Geometry;
using GHCE.Bridge;

namespace GHCE.Types
{
    /// <summary>
    /// 曲线 → 页面坐标路径。
    /// 存在的意义：让原生 GH 的 Curve 能直接接进需要 Path 的端口（不经过 Path Input 电池）。
    /// 多曲线共用一个包围盒的投影在 Path Input 里（那里才知道全局范围），这里是单曲线的就地投影。
    /// </summary>
    public static class PathConversion
    {
        public const double DefaultTolerance = 1.0;
        public const int DefaultPageW = 1440;
        public const int DefaultPageH = 900;
        public const double DefaultMargin = 60;

        public static Point3d[] Sample(Curve c, double tol)
        {
            if (c == null || !c.IsValid) return null;

            if (c.TryGetPolyline(out Polyline pl))
            {
                var a = pl.ToArray();
                if (a.Length >= 2) return a;
            }

            double len = c.GetLength();
            if (len <= 0) return null;

            int n = (int)Math.Ceiling(len / Math.Max(0.05, tol));
            n = Math.Clamp(n, 2, 4000);

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

        /// <summary>身份基准：控制点（与采样容差无关）</summary>
        public static double[] IdentityCoords(Curve c)
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

        /// <summary>
        /// 单曲线就地投影到页面（等比 + Y 翻转 + margin）。
        ///
        /// v0.11：必须把相机参数写回 <see cref="PathDef.Proj"/>。
        /// 之前这里投影了却不记相机，于是 Transform 的 P·M·P⁻¹ 拿不到 P、走退化分支，
        /// 绕几何中心转就变成绕页面原点转 —— 表现是"转起来锚点飞了"。
        /// 走 Draw Path 而不接 Path Input 的那条捷径最容易踩到。
        /// </summary>
        public static PathDef FromCurve(Curve c,
            double tol = DefaultTolerance, int pw = DefaultPageW, int ph = DefaultPageH, double margin = DefaultMargin)
        {
            var pts = Sample(c, tol);
            if (pts == null || pts.Length < 2) return null;

            var box = CameraMath.EmptyBox();
            foreach (var p in pts) CameraMath.Extend(box, p.X, p.Y);
            if (CameraMath.IsEmpty(box)) box = new double[] { 0, 0, 1, 1 };

            var proj = CameraMath.Fit(box, pw, ph, margin, 0);

            var xy = new double[pts.Length * 2];
            for (int k = 0; k < pts.Length; k++)
            {
                CameraMath.ToPage(proj, pts[k].X, pts[k].Y, out var px, out var py);
                xy[k * 2] = px;
                xy[k * 2 + 1] = py;
            }

            return new PathDef
            {
                Id = PatchBuilder.StableId("g_", IdentityCoords(c)),
                Closed = c.IsClosed,
                XY = xy,
                IsPoints = false,
                Proj = proj,
            };
        }
    }
}
