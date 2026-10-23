using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Grasshopper.Kernel;

namespace GHCE.Ui
{
    /// <summary>
    /// 让"传感器"电池（Interaction / Runtime Info）在浏览器回传新状态时自动刷新。
    ///
    /// 不用 GH_Document.NewSolution —— 那会把整张图重解一遍。
    /// 这里只 ExpireSolution 自己，所以下游该重算的自然重算，别的电池不动。
    /// 频率 100ms：
    ///   - 与 stat 回传同频（运行时每 100ms 发一次），再快也只是空转
    ///   - 必须小于 tween 的 CONTINUOUS_GAP（120ms），否则每次刷新都会被当成
    ///     "一次离散变更"而起一段 0.9 秒过渡，画面会黏在路上
    /// </summary>
    internal static class StatTicker
    {
        const int IntervalMs = 100;

        static readonly List<WeakReference<GH_Component>> Subs = new List<WeakReference<GH_Component>>();
        static System.Windows.Forms.Timer _timer;
        static readonly object _lock = new object();

        public static void Subscribe(GH_Component c)
        {
            lock (_lock)
            {
                if (!Subs.Exists(w => w.TryGetTarget(out var t) && t == c)) Subs.Add(new WeakReference<GH_Component>(c));
                if (_timer == null)
                {
                    _timer = new System.Windows.Forms.Timer { Interval = IntervalMs };
                    _timer.Tick += OnTick;
                }
                if (!_timer.Enabled) _timer.Start();
            }
        }

        public static void Unsubscribe(GH_Component c)
        {
            lock (_lock)
            {
                Subs.RemoveAll(w => !w.TryGetTarget(out var alive) || alive == c);
                if (Subs.Count == 0 && _timer != null) _timer.Stop();
            }
        }

        static void OnTick(object sender, EventArgs e)
        {
            List<GH_Component> live;
            lock (_lock)
            {
                Subs.RemoveAll(w => !w.TryGetTarget(out _));
                live = new List<GH_Component>(Subs.Count);
                foreach (var w in Subs) if (w.TryGetTarget(out var c)) live.Add(c);
            }

            foreach (var c in live)
            {
                if (c == null) continue;
                try { c.ExpireSolution(true); } catch { /* 组件已被移除 */ }
            }
        }
    }
}
