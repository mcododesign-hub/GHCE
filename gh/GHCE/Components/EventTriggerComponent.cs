using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Event Trigger —— 管道电池：把"点击/按键 → 动作"挂到**喂进来的那几条指令**上。
    ///
    /// 为什么是管道而不是全局规则表：
    ///   事件的作用域应该由"你喂了哪些 cmds"决定。25 个方块里挑 8 个进来，
    ///   点一下就只有那 8 个响应；想同时有点击和键盘两套，就并两条管道，各自带一组指令。
    ///   全局规则表则逼着你在一开始就回答"这条规则属于谁"，而那根本是个假问题。
    ///
    /// 动作分两类（端口说明里也写了）：
    ///   toggle / show / hide / restart —— 只作用于喂进来的这几条指令
    ///   clear / clear-region / reset-time / toggle-play —— 画布或时钟只有一个，作用域天然是全局
    ///
    /// 规则由 GH 发一次，命中后浏览器直接执行 —— 点一次不需要 GH 重新求解。
    /// </summary>
    public class EventTriggerComponent : GH_Component
    {
        public EventTriggerComponent()
            : base("Event Trigger", "Event", "点击 / 按键 → 对这组指令执行动作", "GHCE", "Input") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000e6");
        protected override Bitmap Icon => Icons.EventTrigger;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds",
                "这条规则作用的指令（任意组合、任意几条）。输出仍是这些指令，串在管道里",
                GH_ParamAccess.list);
            p.AddTextParameter("Event", "Event", "click / dblclick / key（默认 click）", GH_ParamAccess.item, "click");
            p.AddTextParameter("Action", "Action",
                "逐指令：toggle 显隐切换 · show 显示 · hide 隐藏 · restart 变换重演\n" +
                "整屏/时钟：clear 清屏 · clear-region 擦一块 · reset-time 时间归零 · toggle-play 播放暂停",
                GH_ParamAccess.item, "toggle");
            p.AddTextParameter("Detail", "Detail",
                "clear-region 填 \"x,y,w,h\"（页面坐标）；key 填键名（如 a / Space，留空 = 任意键）；其它动作忽略",
                GH_ParamAccess.item);
            p.AddBooleanParameter("Enable", "On", "关掉时不发这条规则（指令原样通过）", GH_ParamAccess.item, true);
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "带事件规则的指令", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            string ev = "click", action = "toggle", detail = "";
            bool on = true;
            DA.GetData(1, ref ev);
            DA.GetData(2, ref action);
            DA.GetData(3, ref detail);
            DA.GetData(4, ref on);

            if (!on)
            {
                Message = "off";
                DA.SetDataList(0, cmds);
                return;
            }

            ev = (ev ?? "click").Trim().ToLowerInvariant();
            action = EventDef.NormalizeAction(action);   // 兼容 restart-time / restart-objects 这类旧名字

            if (Array.IndexOf(EventDef.Events, ev) < 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Event 只能是 {string.Join(" / ", EventDef.Events)}，收到 \"{ev}\" 已回退 click");
                ev = "click";
            }
            if (Array.IndexOf(EventDef.Actions, action) < 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Action 只能是 {string.Join(" / ", EventDef.Actions)}，收到 \"{action}\" 已回退 toggle");
                action = "toggle";
            }
            if (action == "clear-region" && CountNumbers(detail) < 4)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "clear-region 的 Detail 需要 4 个数：\"x,y,w,h\"（页面坐标）。这条规则不会生效");
            }

            // 目标 = 这次喂进来的全部指令。身份用 targets 指纹，所以换个组合就是另一条规则，
            // 不会和旧规则互相覆盖。
            var targets = new List<string>(cmds.Count);
            for (int i = 0; i < cmds.Count; i++)
            {
                var src = cmds[i]?.Value;
                if (src != null && !string.IsNullOrEmpty(src.Id)) targets.Add(src.Id);
            }
            if (targets.Count == 0) return;

            var def = new EventDef
            {
                Id = MakeId(ev, action, detail, targets),
                Event = ev,
                Action = action,
                Detail = detail == null ? "" : detail.Trim(),
                Targets = targets.ToArray()
            };

            var outCmds = new List<GHCE_Command>(cmds.Count);
            for (int i = 0; i < cmds.Count; i++)
            {
                var src = cmds[i]?.Value;
                if (src == null) continue;
                var copy = src.Duplicate();
                if (copy.Events == null) copy.Events = new List<EventDef>();
                copy.Events.Add(def);
                outCmds.Add(new GHCE_Command(copy));
            }

            DA.SetDataList(0, outCmds);
            Message = $"{ev} → {action} × {targets.Count}" + (string.IsNullOrEmpty(def.Detail) ? "" : $" ({def.Detail})");
        }

        /// <summary>身份 = 事件 + 动作 + 细节 + 目标集合的指纹</summary>
        static string MakeId(string ev, string action, string detail, List<string> targets)
        {
            var t = new List<string>(targets);
            t.Sort(StringComparer.Ordinal);
            var s = $"{ev}|{action}|{(detail ?? "").Trim()}|{string.Join(",", t)}";
            uint h = 2166136261;
            foreach (var ch in s)
            {
                h ^= (byte)ch;
                h *= 16777619;
            }
            return "ev_" + h.ToString("x8");
        }

        static int CountNumbers(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            int n = 0;
            bool inNum = false;
            foreach (var ch in s)
            {
                if (char.IsDigit(ch) || ch == '-' || ch == '.')
                {
                    if (!inNum) { n++; inNum = true; }
                }
                else inNum = false;
            }
            return n;
        }
    }
}
