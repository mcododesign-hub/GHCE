using System;

namespace Colugo.Types
{
    /// <summary>
    /// 世界 → 页面的相机数学。**刻意只用 double 与 double[]，不碰任何 Rhino 类型。**
    ///
    /// 理由：`gh/probe` 在没有 Rhino 的环境里跑，一旦这里出现 Curve/Point3d，
    /// 探针就只能在 Rhino 里跑，"改一行验一次"的节奏就没了。
    /// 这条规矩是被 `Transform.Rotation` 在 Rhino 外不可用（缺 rhcommon_c）逼出来的。
    /// </summary>
    public static class CameraMath
    {
        /// <summary>
        /// 由世界包围盒与页面尺寸解出相机 [s, ox, oy]：px = s*X + ox，py = -s*Y + oy。
        /// </summary>
        /// <param name="bounds">[minX, minY, maxX, maxY]；null 或退化时按 0..1 兜底</param>
        /// <param name="scaleOverride">&gt;0 时强制用这个缩放（固定相机），0 表示按包围盒拟合</param>
        public static double[] Fit(double[] bounds, int evenW, int evenH, double margin, double scaleOverride)
        {
            int pw = evenW > 0 ? evenW : 1440;
            int ph = evenH > 0 ? evenH : 900;
            if (margin < 0) margin = 0;

            double minX = 0, minY = 0, maxX = 1, maxY = 1;
            if (bounds != null && bounds.Length >= 4)
            {
                minX = bounds[0]; minY = bounds[1]; maxX = bounds[2]; maxY = bounds[3];
            }

            double dx = Math.Max(maxX - minX, 1e-9);
            double dy = Math.Max(maxY - minY, 1e-9);

            double innerW = Math.Max(1, pw - 2 * margin);
            double innerH = Math.Max(1, ph - 2 * margin);

            double s = scaleOverride > 0 ? scaleOverride : Math.Min(innerW / dx, innerH / dy);
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) s = 1;

            double cx = (minX + maxX) / 2.0;
            double cy = (minY + maxY) / 2.0;

            double ox = pw / 2.0 - s * cx;
            double oy = ph / 2.0 + s * cy;      // Y 翻转在 py 的减号里，这里 + 是为了让包围盒中心落在页面中心

            return new[] { s, ox, oy };
        }

        /// <summary>世界 → 页面</summary>
        public static void ToPage(double[] proj, double x, double y, out double px, out double py)
        {
            px = proj[1] + x * proj[0];
            py = proj[2] - y * proj[0];
        }

        /// <summary>页面 → 世界（相机是可逆的，交互回传需要它）</summary>
        public static void ToWorld(double[] proj, double px, double py, out double x, out double y)
        {
            double s = proj[0] == 0 ? 1 : proj[0];
            x = (px - proj[1]) / s;
            y = (proj[2] - py) / s;
        }

        /// <summary>把一组点并进包围盒（就地扩展）。返回是否真的吃进去了点。</summary>
        public static bool Extend(double[] box, double x, double y)
        {
            if (box == null || box.Length < 4) return false;
            if (x < box[0]) box[0] = x;
            if (y < box[1]) box[1] = y;
            if (x > box[2]) box[2] = x;
            if (y > box[3]) box[3] = y;
            return true;
        }

        /// <summary>空包围盒（用 ±∞ 表示空，Extend 第一次调用后即成为真实范围）</summary>
        public static double[] EmptyBox() =>
            new[] { double.MaxValue, double.MaxValue, double.MinValue, double.MinValue };

        public static bool IsEmpty(double[] box) =>
            box == null || box.Length < 4 || box[0] > box[2] || box[1] > box[3];
    }
}
