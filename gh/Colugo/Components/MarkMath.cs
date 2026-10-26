using System;
using System.Collections.Generic;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>路径上的一次采样：位置、切向、沿程比例。</summary>
    public readonly struct MarkSample
    {
        public readonly double X, Y, Angle, U;

        public MarkSample(double x, double y, double angle, double u)
        {
            X = x; Y = y; Angle = angle; U = u;
        }
    }

    /// <summary>
    /// Mark 的纯数学层。
    ///
    /// 为什么单独抽出来：三只电池（Brush / Mark Along Path / Style Map）都只是
    /// 端口包装，真正的算法在这里。探针可以绕过 GH 的 IGH_DataAccess 直接调这些
    /// 函数断言数值 —— 否则"标记放对了没有"只能靠肉眼开 GH 看。
    ///
    /// 关键设计：**一个印章几何 + 每条标记一个矩阵**。
    /// 500 个标记只发一次几何，其余全是 6 个数的仿射，这是 patch 体积能守住的原因。
    /// </summary>
    public static class MarkMath
    {
        /// <summary>单次求解的标记上限。超过就截断并告警 —— 无上限的采样会直接打爆 patch。</summary>
        public const int MaxMarks = 4000;

        /// <summary>
        /// 内置印章形状，局部坐标、以原点为中心。形状 1–5 一一对应 Brush 的 Shape 端口。
        /// 尺寸按 Size 定义"最大外接范围"，不是半径 —— 这样五个形状并排看大小一致。
        /// </summary>
        public static double[] Stamp(int shape, double size, double aspect)
        {
            double h = Math.Max(0.01, size) * 0.5;
            double hx = h * Math.Max(0.01, aspect);

            switch (shape)
            {
                // 2 短线（书法笔锋的一笔）
                case 2:
                    return new[] { -hx, 0.0, hx, 0.0 };

                // 3 方块
                case 3:
                    return new[] { -hx, -h, hx, -h, hx, h, -hx, h, -hx, -h };

                // 4 三角
                case 4:
                    return new[] { 0.0, -h, hx, h * 0.85, -hx, h * 0.85, 0.0, -h };

                // 5 半圆弧（月牙、逗号笔画的底）
                case 5:
                {
                    const int n = 12;
                    var xy = new double[(n + 1) * 2];
                    for (int i = 0; i <= n; i++)
                    {
                        double a = Math.PI * i / n;
                        xy[i * 2] = hx * Math.Cos(a);
                        xy[i * 2 + 1] = -h * Math.Sin(a);
                    }
                    return xy;
                }

                // 1 圆点（默认）
                default:
                {
                    const int n = 24;
                    var xy = new double[n * 2];
                    for (int i = 0; i < n; i++)
                    {
                        double a = Math.PI * 2 * i / n;
                        xy[i * 2] = hx * Math.Cos(a);
                        xy[i * 2 + 1] = h * Math.Sin(a);
                    }
                    return xy;
                }
            }
        }

        /// <summary>
        /// 自定义印章归一化：居中到原点、最大外接范围缩到 Size、按 Aspect 压 X、再按角度旋转。
        /// 不归一化的话，印章会被源曲线的世界尺寸带偏 —— 同一只 Brush 在不同图里大小不一。
        /// </summary>
        public static double[] Normalize(double[] src, double size, double aspect, double angleDeg)
        {
            if (src == null || src.Length < 4) return null;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < src.Length; i += 2)
            {
                if (src[i] < minX) minX = src[i];
                if (src[i] > maxX) maxX = src[i];
                if (src[i + 1] < minY) minY = src[i + 1];
                if (src[i + 1] > maxY) maxY = src[i + 1];
            }

            double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
            double w = maxX - minX, h = maxY - minY;
            double extent = Math.Max(w, h);
            double k = extent > 1e-9 ? Math.Max(0.01, size) / extent : 1.0;
            double ax = Math.Max(0.01, aspect);

            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            var xy = new double[src.Length];
            for (int i = 0; i < src.Length; i += 2)
            {
                double x = (src[i] - cx) * k * ax;
                double y = (src[i + 1] - cy) * k;
                xy[i] = x * cos - y * sin;
                xy[i + 1] = x * sin + y * cos;
            }
            return xy;
        }

        /// <summary>
        /// 沿路径等距采样。
        ///
        /// 间距取"总长 / 整数份"而不是原样用 Spacing：否则最后一段会剩一个零头，
        /// 五个标记里有四个齐、最后一个贴到端点 —— 排线一眼就能看出不均匀。
        /// 闭合路径不重复首点，开放路径两端都有标记。
        /// </summary>
        public static List<MarkSample> Sample(PathDef p, double spacing, int limit)
        {
            var list = new List<MarkSample>();
            if (p?.XY == null || p.XY.Length < 4) return list;

            var xy = p.XY;
            int n = xy.Length / 2;

            // 段表（含闭合段）
            var sx = new List<double>(n);
            var sy = new List<double>(n);
            var ex = new List<double>(n);
            var ey = new List<double>(n);
            var len = new List<double>(n);
            for (int i = 0; i < n - 1; i++)
            {
                sx.Add(xy[i * 2]); sy.Add(xy[i * 2 + 1]);
                ex.Add(xy[i * 2 + 2]); ey.Add(xy[i * 2 + 3]);
                len.Add(Math.Sqrt(
                    (ex[i] - sx[i]) * (ex[i] - sx[i]) + (ey[i] - sy[i]) * (ey[i] - sy[i])));
            }
            bool closed = p.Closed && n > 2;
            if (closed)
            {
                double dx = xy[0] - xy[(n - 1) * 2];
                double dy = xy[1] - xy[(n - 1) * 2 + 1];
                sx.Add(xy[(n - 1) * 2]); sy.Add(xy[(n - 1) * 2 + 1]);
                ex.Add(xy[0]); ey.Add(xy[1]);
                len.Add(Math.Sqrt(dx * dx + dy * dy));
            }

            double total = 0;
            for (int i = 0; i < len.Count; i++) total += len[i];
            if (total < 1e-6) return list;

            if (!(spacing > 0.05)) spacing = 0.05;
            int count = (int)Math.Round(total / spacing);
            if (count < 1) count = 1;
            if (count > limit) count = limit;

            double step = total / count;
            int marks = closed ? count : count + 1;
            if (marks > limit) marks = limit;

            int seg = 0;
            double segStart = 0;
            for (int i = 0; i < marks; i++)
            {
                double d = i * step;
                if (d > total) d = total;

                while (seg < len.Count - 1 && segStart + len[seg] < d)
                {
                    segStart += len[seg];
                    seg++;
                }

                double t = len[seg] > 1e-12 ? (d - segStart) / len[seg] : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;

                double x = sx[seg] + (ex[seg] - sx[seg]) * t;
                double y = sy[seg] + (ey[seg] - sy[seg]) * t;
                double angle = Math.Atan2(ey[seg] - sy[seg], ex[seg] - sx[seg]);
                list.Add(new MarkSample(x, y, angle, d / total));
            }
            return list;
        }

        /// <summary>放置矩阵 [a,b,c,d,e,f]：绕自身原点转 theta、平移到 (x,y)。</summary>
        public static double[] Placement(double x, double y, double theta)
        {
            double c = Math.Cos(theta), s = Math.Sin(theta);
            // -0 会在 JSON 里写成 "-0"：数值没错，但矩阵指纹会因此抖动，
            // 同一帧算出 "-0" 和 "0" 会被判定成"变了"而重发整条指令。
            if (c == 0) c = 0;
            if (s == 0) s = 0;
            double ns = -s;
            if (ns == 0) ns = 0;
            return new[] { c, s, ns, c, x, y };
        }

        /// <summary>缩放线性部分（位置不动）。</summary>
        public static void ScaleLinear(double[] m, double k)
        {
            if (m == null || m.Length < 6) return;
            m[0] *= k; m[1] *= k; m[2] *= k; m[3] *= k;
        }

        /// <summary>
        /// 旋转线性部分（位置不动）：M' = M · R(δ)。
        /// 只改 a,b,c,d 四元，平移列不参与 —— 否则标记会绕着页面原点公转。
        /// </summary>
        public static void RotateLinear(double[] m, double deltaRad)
        {
            if (m == null || m.Length < 6) return;
            double cos = Math.Cos(deltaRad), sin = Math.Sin(deltaRad);
            double a = m[0], b = m[1], c = m[2], d = m[3];
            m[0] = a * cos + c * sin;
            m[1] = b * cos + d * sin;
            m[2] = -a * sin + c * cos;
            m[3] = -b * sin + d * cos;
        }

        /// <summary>把 u 从 [in0,in1] 映射到 [out0,out1]，Bias 是伽马（&lt;1 前段快、&gt;1 后段快）。</summary>
        public static double Map(double u, double in0, double in1, double out0, double out1, double bias)
        {
            double span = in1 - in0;
            if (Math.Abs(span) < 1e-12) return out0;

            double t = (u - in0) / span;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            if (bias > 0 && Math.Abs(bias - 1) > 1e-9) t = Math.Pow(t, bias);
            return out0 + (out1 - out0) * t;
        }

        /// <summary>
        /// 把映射结果写进一条指令。属性：1 尺寸 / 2 旋转（增量，度）/ 3 不透明度 / 4 描边宽度。
        /// 尺寸与旋转优先改矩阵：标记的位置是矩阵的平移列，缩放/旋转不能动它。
        /// </summary>
        public static bool Apply(CommandDef c, int property, double value)
        {
            if (c == null || double.IsNaN(value) || double.IsInfinity(value)) return false;

            switch (property)
            {
                case 2:
                    if (c.Matrix != null && c.Matrix.Length >= 6) RotateLinear(c.Matrix, value * Math.PI / 180.0);
                    else c.Rotation += value;
                    return true;

                case 3:
                    c.Opacity = Clamp01(c.Opacity * value);
                    return true;

                case 4:
                    c.Stroke.Width = Math.Max(0, c.Stroke.Width * value);
                    return true;

                default:
                    double k = Math.Max(0, value);
                    if (c.Matrix != null && c.Matrix.Length >= 6) ScaleLinear(c.Matrix, k);
                    else c.Scale *= k;
                    return true;
            }
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
