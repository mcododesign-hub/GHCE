using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;
using Rhino.Geometry;

namespace GHCE.Components
{
    /// <summary>
    /// Track —— 时间轨。**只吸收 cmds 来表达动态，不预设任何"可动画的属性"。**
    ///
    /// 用法：`Series(0..1, N)` → 你的数学 → `Rotate` / `Scale` / `Move` → 这里。
    /// 这 N 个 Transform 就是这条动画在时间上的采样，浏览器在帧之间插值并按 `Duration` 播放。
    ///
    /// 三个时间参数都不是"效果"，是时间本身的属性，所以可以做端口：
    ///   Duration（跑完一遍多少秒）/ Loop（是否循环）/ Ease（一条曲线当缓动表）
    ///
    /// 没有 `Field` 下拉、没有 Reveal/Rotate/Move 三选一 —— 帧序列长什么样完全由你算。
    /// </summary>
    public class TrackComponent : GH_Component
    {
        const int EASE_SAMPLES = 33;

        public TrackComponent()
            : base("Track", "Track", "Turns a series of transforms into a clip: frames, span, easing", "GHCE", "Animation") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b1");
        protected override Bitmap Icon => Icons.Track;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to animate", GH_ParamAccess.list);
            var frames = new Grasshopper.Kernel.Parameters.Param_Transform
            {
                Name = "Frames",
                NickName = "F",
                Description = "One Transform per frame. Optional when only reveal is animated",
                Access = GH_ParamAccess.list,
                Optional = true,
            };
            p.AddParameter(frames);

            var reveal = new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "Reveal",
                NickName = "Rev",
                Description = "Reveal value per frame in 0..1. Optional",
                Access = GH_ParamAccess.list,
                Optional = true,
            };
            p.AddParameter(reveal);

            // 入点 / 出点。用 GH 原生 Interval（Construct Domain 输出）：
            // 一个端口表达两个数，比 Start+End 两个端口更 Grasshopper，也省一格。
            var range = new Grasshopper.Kernel.Parameters.Param_Interval
            {
                Name = "Range",
                NickName = "Range",
                Description = "Start and end of this track on the global timeline, in seconds. Empty spans the whole duration",
                Access = GH_ParamAccess.item,
                Optional = true,
            };
            p.AddParameter(range);

            var ease = new Grasshopper.Kernel.Parameters.Param_Curve
            {
                Name = "Ease",
                NickName = "Ease",
                Description = "Easing curve: X is time 0..1, Y is progress 0..1. Empty means linear",
                Access = GH_ParamAccess.item,
                Optional = true,
            };
            p.AddParameter(ease);

            p.AddBooleanParameter("Hidden", "Hide", "Mutes this track. Useful for isolating layers without disconnecting wires",
                GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands carrying a track, feed into Render Output", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            var frames = new List<Transform>();
            var reveals = new List<double>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;
            DA.GetDataList(1, frames);
            DA.GetDataList(2, reveals);

            // morph 模式下轨道只提供区间 —— 帧序列由 Morph 电池自带（cmd.morph）
            bool hasMorph = false;
            foreach (var gc in cmds) { if (gc?.Value?.Morph != null) { hasMorph = true; break; } }

            if (frames.Count < 2 && reveals.Count < 2 && !hasMorph)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Frames or Reveal needs at least 2 values. Morph supplies its own sequence");
                return;
            }

            // 入点 / 出点。Duration 与 Loop 不在这里 —— 归 Timeline 管。
            Interval range = Interval.Unset;
            Curve easeCurve = null;
            bool hidden = false;
            DA.GetData(3, ref range);
            DA.GetData(4, ref easeCurve);
            DA.GetData(5, ref hidden);

            double start = 0, end = -1;
            if (range.IsValid && Math.Abs(range.T1 - range.T0) > 1e-9)
            {
                start = Math.Min(range.T0, range.T1);
                end = Math.Max(range.T0, range.T1);
                if (start < 0) start = 0;
            }

            double[] easeX = null, easeY = null;
            if (easeCurve != null) SampleEase(easeCurve, out easeX, out easeY);

            // 每帧先取 6 个分量（未投影）—— 投影随每条指令的 P·M·P⁻¹ 走，所以逐指令合成
            // 至少取 2：morph 模式下 Frames 为空，轨道仍要作为"区间 + 缓动"发给运行时
            int n = Math.Max(Math.Max(frames.Count, reveals.Count), 2);
            var raw = frames.Count >= 2 ? new double[n][] : null;
            if (raw != null)
            {
                for (int k = 0; k < n; k++)
                {
                    var m = k < frames.Count ? frames[k] : frames[frames.Count - 1];
                    raw[k] = new[] { m.M00, m.M01, m.M02, m.M10, m.M11, m.M12 };
                }
            }

            double[] revealSeq = null;
            if (reveals.Count >= 2)
            {
                revealSeq = new double[n];
                for (int k = 0; k < n; k++)
                {
                    double v = k < reveals.Count ? reveals[k] : reveals[reveals.Count - 1];
                    revealSeq[k] = Math.Max(0, Math.Min(1, v));
                }
            }

            var outCmds = new List<GHCE_Command>(cmds.Count);
            foreach (var gc in cmds)
            {
                var src = gc?.Value;
                if (src == null) continue;

                double[] flat = null;
                if (raw != null)
                {
                    var proj = src.Path == null ? null : src.Path.Proj;
                    flat = new double[n * 6];
                    for (int k = 0; k < n; k++)
                    {
                        var page = TransformComponent.ComposePageMatrix(
                            raw[k][0], raw[k][1], raw[k][2], raw[k][3], raw[k][4], raw[k][5], proj);
                        for (int i = 0; i < 6; i++) flat[k * 6 + i] = page[i];
                    }
                }

                var copy = src.Duplicate();
                var prev = src.Track;

                // 帧序列可能来自更上游（Draw Path 从 Transform 的矩阵帧转交过来）。
                // 那种情况下 flat 为 null，必须沿用 prev —— 否则 Track 会把几何层的动画抹掉，
                // 只剩一个"有区间没有内容"的空轨道。
                copy.Track = new TrackDef
                {
                    N = flat != null ? n : (prev != null && prev.N >= 2 ? prev.N : n),
                    Frames = flat ?? prev?.Frames,
                    Reveal = revealSeq == null ? prev?.Reveal : (double[])revealSeq.Clone(),
                    Start = start,
                    End = end,
                    Hidden = hidden,
                    EaseX = easeX,
                    EaseY = easeY,
                };
                // 终态矩阵不再由 tween 演：轨道自己就是时间轴
                copy.Progress = 1.0;
                outCmds.Add(new GHCE_Command(copy));
            }

            string span = end > start ? $"{start:0.##}–{end:0.##}s" : "full span";
            Message = $"{n} frames x {cmds.Count} cmds · {span}{(hidden ? " · muted" : "")}{(revealSeq != null ? " · 生长" : "")}";
            DA.SetDataList(0, outCmds);
        }

        /// <summary>
        /// 把一条曲线采成"横轴 0..1、纵轴 0..1"的缓动表。
        /// 为什么不直接按参数采样：曲线的参数分布和 x 分布无关，按参数采会得到不均匀的横轴。
        /// 这里按参数采点、再归一化 x，运行时按 x 二分查找 —— 你画什么形状就是什么形状。
        /// 曲线在 x 上掉头（非函数）时退回线性，避免产生 x 不单调的表。
        /// </summary>
        static void SampleEase(Curve c, out double[] xs, out double[] ys)
        {
            const int N = EASE_SAMPLES;
            xs = new double[N];
            ys = new double[N];

            var px = new double[N];
            var py = new double[N];
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;

            var dom = c.Domain;
            for (int k = 0; k < N; k++)
            {
                double t = dom.Min + (dom.Max - dom.Min) * (k / (double)(N - 1));
                var pt = c.PointAt(t);
                px[k] = pt.X; py[k] = pt.Y;
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }

            bool monotonic = true;
            for (int k = 1; k < N; k++)
                if (px[k] < px[k - 1] - 1e-9) { monotonic = false; break; }

            double spanX = maxX - minX, spanY = maxY - minY;
            if (!monotonic || spanX <= 1e-9 || spanY <= 1e-9)
            {
                for (int k = 0; k < N; k++) { xs[k] = k / (double)(N - 1); ys[k] = xs[k]; }
                return;
            }

            for (int k = 0; k < N; k++)
            {
                xs[k] = (px[k] - minX) / spanX;
                ys[k] = (py[k] - minY) / spanY;
            }
        }
    }
}
