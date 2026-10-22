using System;
using System.Collections.Generic;
using System.Threading;
using GHCE.Bridge;
using Grasshopper.Kernel;

namespace GHCE.Ui
{
    /// <summary>
    /// "新客户端接上了，补一帧快照"的触发器。
    ///
    /// 为什么需要它：BridgeServer 只会把 <see cref="BridgeServer.Broadcast"/> 送来的帧转发给
    /// 当前在线的客户端。预览窗口是在 Render Output **已经算完之后**才把页面加载起来的，
    /// 页面连上来的时候那一帧早就发完了 —— 于是画布空白，只有等你动一下数据、
    /// GH 重新求解才会出现第一笔。这不是渲染问题，是**握手顺序问题**。
    ///
    /// 做法：桥接上新客户端时 Poke() 一下，这里用一个 80ms 的 WinForms Timer 在 UI 线程上
    /// expire 一次 Render Output —— 它重算时会因为 ConsumeResync() 而发出整帧快照。
    /// 只 expire 自己，不 NewSolution 整张图。
    /// </summary>
    internal static class ResyncTicker
    {
        const int IntervalMs = 80;

        static readonly List<WeakReference<GH_Component>> Subs = new List<WeakReference<GH_Component>>();
        static System.Windows.Forms.Timer _timer;
        static readonly object _lock = new object();
        static int _pending;

        public static void Subscribe(GH_Component c)
        {
            lock (_lock)
            {
                if (!Subs.Exists(w => w.TryGetTarget(out var t) && t == c))
                    Subs.Add(new WeakReference<GH_Component>(c));
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

        /// <summary>可以从任意线程调用（桥的线程）</summary>
        public static void Poke() => Interlocked.Exchange(ref _pending, 1);

        static void OnTick(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _pending, 0) == 0) return;

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
