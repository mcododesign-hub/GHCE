using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Timeline —— 全局时间轴。
    ///
    /// **只回答两件事：整段多长、要不要循环。** 不含任何具体动画实例。
    ///
    /// 为什么必须这么切：如果把"时长"塞在 Track 里，每条轨就各自有一个 0→1 的本地时钟，
    /// 多轨就不可能错开排布 —— 你没法表达"第 0 秒淡入、第 2 秒开始转、第 5 秒收尾"，
    /// 因为这需要在同一条时间线上比较不同轨的位置。唯一的办法是所有轨共用一个播放头，
    /// 也就是这个电池。
    ///
    /// 与之对应，Track 只回答"我什么时候开始、什么时候结束、怎么缓动、要不要静默"。
    /// 分工跟剪辑软件一致：Time Line 是容器，Track 是轨，轨道里的素材是你在 GH 里算的帧序列。
    /// </summary>
    public class TimelineComponent : GH_Component
    {
        public TimelineComponent()
            : base("Timeline", "Timeline", "Global timeline: total duration and looping; collects tracks",
                   "Colugo", "Animation") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000b2");
        protected override Bitmap Icon => Icons.Timeline;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Tracks", "Tracks",
                "Tracks from the Track component. Static commands without a track may be passed through",
                GH_ParamAccess.list);
            p.AddNumberParameter("Duration", "Dur", "Total duration in seconds. All tracks share this single timeline", GH_ParamAccess.item, 8.0);
            p.AddBooleanParameter("Loop", "Loop", "Restart from the beginning after the last frame", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds",
                "Commands carrying the global timeline", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Command>();
            double duration = 8.0;
            bool loop = true;

            DA.GetDataList(0, cmds);
            DA.GetData(1, ref duration);
            DA.GetData(2, ref loop);

            if (duration <= 1e-6)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Duration must be greater than 0, using 1s instead");
                duration = 1.0;
            }

            var tl = new TimelineDef { Duration = duration, Loop = loop };

            var outCmds = new List<Colugo_Command>(cmds.Count);
            int tracks = 0, over = 0;
            double maxEnd = 0;

            foreach (var gc in cmds)
            {
                var src = gc?.Value;
                if (src == null) continue;

                var copy = src.Duplicate();
                copy.Timeline = tl;

                if (copy.Track != null)
                {
                    tracks++;
                    // 出点超出全局时长：那一段永远不会被播到。说清楚，不要静默截断。
                    if (copy.Track.End > duration + 1e-6)
                    {
                        over++;
                        if (copy.Track.End > maxEnd) maxEnd = copy.Track.End;
                    }
                }

                outCmds.Add(new Colugo_Command(copy));
            }

            if (over > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{over} tracks end beyond the timeline duration {duration:0.##}s (furthest {maxEnd:0.##}s). The excess will not play.");
            }

            Message = $"{duration:0.##}s{(loop ? " ↻" : " →")} · {tracks} tracks";
            DA.SetDataList(0, outCmds);
        }
    }
}
