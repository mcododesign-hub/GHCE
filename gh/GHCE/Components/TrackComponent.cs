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
            : base("Track", "Track", "把一串变换当成一段动画：帧序列 + 时长 + 循环 + 缓动曲线", "GHCE", "Animation") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b1");
        protected override Bitmap Icon => Icons.Track;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "要动的指令（原样传进来）", GH_ParamAccess.list);
            var frames = new Grasshopper.Kernel.Parameters.Param_Transform
            {
                Name = "Frames",
                NickName = "F",
                Description = "时间上的采样：每给一个 Transform 就是一帧。可选（只做生长时不用给）",
                Access = GH_ParamAccess.list,
                Optional = true,
            };
            p.AddParameter(frames);

            var reveal = new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "Reveal",
                NickName = "Rev",
                Description = "每帧的 reveal（0..1），用于曲线生长。可选",
                Access = GH_ParamAccess.list,
                Optional = true,
            };
            p.AddParameter(reveal);

            var ease = new Grasshopper.Kernel.Parameters.Param_Curve
            {
                Name = "Ease",
                NickName = "Ease",
                Description = "缓动曲线：横轴 = 时间 0..1，纵轴 = 进度 0..1。不接 = 线性",
                Access = GH_ParamAccess.item,
                Optional = true,
            };
            p.AddParameter(ease);

            p.AddNumberParameter("Duration", "Dur", "跑完一遍多少秒", GH_ParamAccess.item, 1.0);
            p.AddBooleanParameter("Loop", "Loop", "播完是否从头再来", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "带时间轨的指令，接到 Render Output", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            var frames = new List<Transform>();
            var reveals = new List<double>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;
            DA.GetDataList(1, frames);
            DA.GetDataList(2, reveals);
            if (frames.Count < 2 && reveals.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Frames 或 Reveal 至少要给 2 帧（一帧构不成动画）");
                return;
            }

            Curve easeCurve = null;
            double duration = 1.0;
            bool loop = true;
            DA.GetData(3, ref easeCurve);
            DA.GetData(4, ref duration);
            DA.GetData(5, ref loop);

            if (duration <= 0) duration = 1.0;

            double[] easeX = null, easeY = null;
            if (easeCurve != null) SampleEase(easeCurve, out easeX, out easeY);

            // 每帧先取 6 个分量（未投影）—— 投影随每条指令的 P·M·P⁻¹ 走，所以逐指令合成
            int n = Math.Max(frames.Count, reveals.Count);
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
                copy.Track = new TrackDef
                {
                    N = n,
                    Frames = flat,
                    Reveal = revealSeq == null ? null : (double[])revealSeq.Clone(),
                    Duration = duration,
                    Loop = loop,
                    EaseX = easeX,
                    EaseY = easeY,
                };
                // 终态矩阵不再由 tween 演：轨道自己就是时间轴
                copy.Progress = 1.0;
                outCmds.Add(new GHCE_Command(copy));
            }

            Message = $"{n} 帧 × {cmds.Count} 条 · {duration:0.##}s{(loop ? " ↻" : "")}{(revealSeq != null ? " · 生长" : "")}";
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
