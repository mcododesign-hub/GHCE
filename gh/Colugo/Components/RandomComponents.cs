using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Bridge;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Base for live value sources.
    ///
    /// Two outputs, because there are two honest ways to get a changing number:
    ///
    ///   Value   computed here, in GH. 30 Hz. Feeds any native numeric input with no latency,
    ///           but the whole downstream re-solves on every tick.
    ///   Signal  the same function handed to the browser, which evaluates it at frame rate and
    ///           reports back through `stat`. Costs GH nothing; convert it with Signal to Float.
    ///
    /// Neither is better — one buys latency, the other buys frame rate.
    /// </summary>
    public abstract class LiveValueComponent : GH_Component
    {
        const double Rate = 30.0;
        double _nextSolve;

        protected LiveValueComponent(string name, string nick, string desc)
            : base(name, nick, desc, "Colugo", "Random") { }

        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Seconds since the plugin loaded. Monotonic; system clock changes do not affect it.</summary>
        protected static double Now => Clock.Elapsed.TotalSeconds;

        /// <summary>False for components that consume a signal rather than produce one.</summary>
        protected virtual bool EmitsSignal => true;

        /// <summary>
        /// True for components whose value arrives from outside (a signal read back from the
        /// browser). Those cannot use ScheduleSolution as their heartbeat: a component with no
        /// downstream still has to refresh, and a self-scheduled solve is not guaranteed to run.
        /// A UI timer is the mechanism that actually works here.
        /// </summary>
        protected virtual bool UseTicker => false;

        protected string SignalId => InstanceGuid.ToString("N").Substring(0, 8);

        protected int Seed => InstanceGuid.GetHashCode() & 0xffff;

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("Value", "V", "Value computed in GH at 30 Hz. Wire it into any numeric input",
                GH_ParamAccess.item);
            if (EmitsSignal)
            {
                p.AddParameter(new Param_Colugo_Signal(), "Signal", "S",
                    "Same source evaluated in the browser at frame rate. Convert with Signal to Float",
                    GH_ParamAccess.item);
            }
        }

        /// <summary>Filled by subclasses right before Emit so the signal mirrors the same parameters.</summary>
        protected SignalDef Pending;

        protected void Emit(IGH_DataAccess DA, double value)
        {
            DA.SetData(0, value);
            if (EmitsSignal && Pending != null) DA.SetData(1, new Colugo_Signal(Pending));

            if (UseTicker) Ui.StatTicker.Subscribe(this);
            else ScheduleNext();
        }

        /// <summary>Requests the next solve, throttled to 30 Hz.</summary>
        protected void ScheduleNext()
        {
            var doc = OnPingDocument();
            if (doc == null) return;

            double now = Now;
            if (now < _nextSolve) return;
            _nextSolve = now + 1.0 / Rate;

            doc.ScheduleSolution(1, d =>
            {
                try { ExpireSolution(false); } catch { }
            });
        }
    }

    /// <summary>Random — a value that jumps every half second. Constant within each step.</summary>
    public class RandomComponent : LiveValueComponent
    {
        public RandomComponent()
            : base("Random", "Random", "Live random value, stepping twice per second") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c1");
        protected override Bitmap Icon => Icons.SignalRandom;

        protected override void RegisterInputParams(GH_InputParamManager p) { }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int step = (int)Math.Floor(Now * 2.0);
            double v = Hash01(step ^ Seed);
            Message = v.ToString("0.###");

            Pending = new SignalDef { Id = SignalId, Kind = "random", Seed = Seed, Rate = 2.0 };
            Emit(DA, v);
        }

        static double Hash01(int i)
        {
            unchecked
            {
                uint h = (uint)i;
                h ^= h >> 16;
                h *= 0x7feb352d;
                h ^= h >> 15;
                h *= 0x846ca68b;
                h ^= h >> 16;
                return h / 4294967296.0;
            }
        }
    }

    /// <summary>Noise 1D — Perlin noise sampled at X, with time sliding along the same axis.</summary>
    public class Noise1Component : LiveValueComponent
    {
        public Noise1Component()
            : base("Noise 1D", "N1", "1D Perlin noise. X sets position, time is added automatically") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c2");
        protected override Bitmap Icon => Icons.Noise;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("X", "X", "Noise coordinate. The same X always yields the same curve", GH_ParamAccess.item, 0.0);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double x = 0;
            DA.GetData(0, ref x);
            double v = Noise.ToUnit(Noise.Perlin(x + Now, 0.5, 0.5, Seed));
            Message = v.ToString("0.###");

            Pending = new SignalDef { Id = SignalId, Kind = "noise1", Seed = Seed, Rate = 1.0, Args = new[] { x } };
            Emit(DA, v);
        }
    }

    /// <summary>Noise 2D — two coordinates, time on the first axis.</summary>
    public class Noise2Component : LiveValueComponent
    {
        public Noise2Component()
            : base("Noise 2D", "N2", "2D Perlin noise. X and Y set position, time is added automatically") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c3");
        protected override Bitmap Icon => Icons.Noise;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("X", "X", "Noise plane X. Feed an index to decorrelate instances", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Y", "Y", "Noise plane Y", GH_ParamAccess.item, 0.0);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double x = 0, y = 0;
            DA.GetData(0, ref x);
            DA.GetData(1, ref y);
            double v = Noise.ToUnit(Noise.Perlin(x + Now, y, 0.5, Seed));
            Message = v.ToString("0.###");

            Pending = new SignalDef { Id = SignalId, Kind = "noise2", Seed = Seed, Rate = 1.0, Args = new[] { x, y } };
            Emit(DA, v);
        }
    }

    /// <summary>Noise 3D — three coordinates, time on the first axis.</summary>
    public class Noise3Component : LiveValueComponent
    {
        public Noise3Component()
            : base("Noise 3D", "N3", "3D Perlin noise. X, Y and Z set position, time is added automatically") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c4");
        protected override Bitmap Icon => Icons.Noise;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("X", "X", "Noise space X", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Y", "Y", "Noise space Y", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Z", "Z", "Noise space Z", GH_ParamAccess.item, 0.0);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double x = 0, y = 0, z = 0;
            DA.GetData(0, ref x);
            DA.GetData(1, ref y);
            DA.GetData(2, ref z);
            double v = Noise.ToUnit(Noise.Perlin(x + Now, y, z, Seed));
            Message = v.ToString("0.###");

            Pending = new SignalDef { Id = SignalId, Kind = "noise3", Seed = Seed, Rate = 1.0, Args = new[] { x, y, z } };
            Emit(DA, v);
        }
    }

    /// <summary>
    /// Signal to Float — converts a browser-evaluated signal back into a number.
    ///
    /// The value travels: this component publishes the signal definition, the browser evaluates it
    /// every frame, and reports it back inside `stat`. So the number here is one telemetry tick old
    /// (about 100 ms). In exchange the browser runs at frame rate and GH does no work.
    ///
    /// If latency matters more than frame rate, use the Value output of the source component —
    /// that one is computed here, with no round trip.
    /// </summary>
    public class SignalToFloatComponent : LiveValueComponent
    {
        public SignalToFloatComponent()
            : base("Signal to Float", "S>F",
                   "Reads the value the browser computes for a signal (frame rate, about 100 ms latency)") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c6");
        protected override Bitmap Icon => Icons.SignalToFloat;

        protected override bool EmitsSignal => false;

        // 回传值每 100ms 才到一次，250ms 刷新足够；关键是它能刷新。
        protected override bool UseTicker => true;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Signal(), "Signal", "S", "From Random or Noise", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var sig = new Colugo_Signal();
            if (!DA.GetData(0, ref sig) || sig.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Signal");
                Emit(DA, 0);
                return;
            }

            var def = sig.Value;
            if (string.IsNullOrEmpty(def.Id))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Signal has no id");
                Emit(DA, 0);
                return;
            }

            // 每次求解都广播定义：约 100 字节，换来运行时的无状态 —— 不用注册表，也不用在删除时清理。
            BridgeServer.PublishSignal(def);

            bool known = RuntimeStat.HasSignal(def.Id);
            double v = RuntimeStat.SignalValue(def.Id);

            // waiting 有三种截然不同的原因，混成一句话用户没法排查。
            if (known)
                Message = v.ToString("0.###");
            else if (!BridgeServer.IsRunning)
                Message = "no bridge · connect a Render Output";
            else if (!RuntimeStat.Fresh)
                Message = "no telemetry · open the preview window";
            else
                Message = "not echoed · " + def.Id;

            Emit(DA, v);
        }
    }

    /// <summary>
    /// Perlin improved noise. One 3D kernel serves all dimensions: fixing the unused axes
    /// keeps 1D/2D/3D on the same texture instead of three unrelated characters.
    /// </summary>
    internal static class Noise
    {
        public static double ToUnit(double n) => (n + 1.0) * 0.5;

        public static double Perlin(double x, double y, double z, int seed)
        {
            var p = Perm(seed);

            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
            x -= fx; y -= fy; z -= fz;

            double u = Fade(x), v = Fade(y), w = Fade(z);
            int A = p[X] + Y, AA = p[A] + Z, AB = p[A + 1] + Z;
            int B = p[X + 1] + Y, BA = p[B] + Z, BB = p[B + 1] + Z;

            return Lerp(
                Lerp(
                    Lerp(Grad(p[AA], x, y, z), Grad(p[BA], x - 1, y, z), u),
                    Lerp(Grad(p[AB], x, y - 1, z), Grad(p[BB], x - 1, y - 1, z), u), v),
                Lerp(
                    Lerp(Grad(p[AA + 1], x, y, z - 1), Grad(p[BA + 1], x - 1, y, z - 1), u),
                    Lerp(Grad(p[AB + 1], x, y - 1, z - 1), Grad(p[BB + 1], x - 1, y - 1, z - 1), u), v),
                w);
        }

        static readonly int[] CacheKey = new int[64];
        static readonly byte[][] CacheVal = new byte[64][];
        static int _cacheCount;
        static readonly object _lock = new object();

        static byte[] Perm(int seed)
        {
            lock (_lock)
            {
                for (int i = 0; i < _cacheCount; i++)
                    if (CacheKey[i] == seed) return CacheVal[i];

                var baseArr = new byte[256];
                for (int i = 0; i < 256; i++) baseArr[i] = (byte)i;

                uint s = (uint)seed | 1u;
                Func<double> rnd = () =>
                {
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    return s / 4294967296.0;
                };

                for (int i = 255; i > 0; i--)
                {
                    int j = (int)(rnd() * (i + 1));
                    if (j > i) j = i;
                    byte t = baseArr[i]; baseArr[i] = baseArr[j]; baseArr[j] = t;
                }

                var p = new byte[512];
                for (int i = 0; i < 512; i++) p[i] = baseArr[i & 255];

                if (_cacheCount >= CacheKey.Length) _cacheCount = 0;
                CacheKey[_cacheCount] = seed;
                CacheVal[_cacheCount] = p;
                _cacheCount++;
                return p;
            }
        }

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }
}
