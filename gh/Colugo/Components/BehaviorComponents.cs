using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>状态槽的名字，Read / Write 共用一张表，避免两处写死两份。</summary>
    internal static class StateSlots
    {
        public static readonly string[] Names =
        {
            "x", "y", "vx", "vy", "u", "age", "index", "size", "alpha", "aux", "gen"
        };

        public static readonly string[] Labels =
        {
            "x", "y", "vx", "vy", "u (path)", "age", "index", "size", "alpha", "aux", "gen"
        };

        public static string Resolve(int slot) =>
            slot >= 1 && slot <= Names.Length ? Names[slot - 1] : Names[0];
    }

    /// <summary>
    /// Read State —— 把一个实例状态读成表达式。
    ///
    /// 它不返回数值：返回的是"每个实例各自读自己"的表达式树。
    /// 这样才能接进 Runtime Math / Remap / Select，和现有的表达式体系共用一套。
    /// </summary>
    public class ReadStateComponent : GH_Component
    {
        public ReadStateComponent()
            : base("Read State", "State", "Reads a per-instance state slot as a runtime expression", "Colugo", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f4");
        protected override Bitmap Icon => Icons.ReadState;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("Slot", "Slot",
                "1 x · 2 y · 3 vx · 4 vy · 5 u · 6 age · 7 index · 8 size · 9 alpha · 10 aux · 11 gen",
                GH_ParamAccess.item, 1);
            p.AddTextParameter("Name", "Name", "Optional slot name, overrides Slot", GH_ParamAccess.item);
            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Expr(), "Expression", "Expr", "Per-instance state read", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Resolved slot", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int slot = 1;
            string name = null;
            DA.GetData(0, ref slot);
            DA.GetData(1, ref name);

            string resolved = string.IsNullOrWhiteSpace(name) ? StateSlots.Resolve(slot) : name.Trim();
            DA.SetData(0, new Colugo_Expr(RuntimeExprDef.State(resolved)));
            Message = resolved;
            DA.SetData(1, resolved);
        }
    }

    /// <summary>
    /// Write State —— 每个固定步对每个实例执行一次写入。
    ///
    /// 一条 Write 就是一个赋值语句；接几条就是几行代码，顺序即执行顺序。
    /// `When` 非 0 才执行，所以"只在某种情况下减速"不需要再套一层条件电池。
    ///
    /// 写了 `x` / `y` 就不再从曲线取样，写了 `u` 就不再自增 —— **写谁，谁归你**。
    /// 不这样的话，"自定义弹簧"和"沿曲线跑"会互相覆盖，用户看到的是一半一半的抖动。
    /// </summary>
    public class WriteStateComponent : GH_Component
    {
        public WriteStateComponent()
            : base("Write State", "Write", "Writes a per-instance state slot every fixed step", "Colugo", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f5");
        protected override Bitmap Icon => Icons.WriteState;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities from Blueprint / Look", GH_ParamAccess.list);
            p.AddIntegerParameter("Slot", "Slot",
                "1 x · 2 y · 3 vx · 4 vy · 5 u · 6 age · 7 index · 8 size · 9 alpha · 10 aux · 11 gen",
                GH_ParamAccess.item, 3);
            p.AddParameter(new Param_Colugo_Expr(), "Expression", "Expr", "Value to write", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_Expr(), "When", "When", "Only write when this is non-zero. Optional", GH_ParamAccess.item);
            p.AddIntegerParameter("Op", "Op", "1 set · 2 add", GH_ParamAccess.item, 1);
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities carrying the step", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Step summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var list = new List<Colugo_Entity>();
            if (!DA.GetDataList(0, list) || list.Count == 0) return;

            int slot = 3, op = 1;
            Colugo_Expr expr = null, when = null;
            DA.GetData(1, ref slot);
            bool hasExpr = DA.GetData(2, ref expr) && expr?.Value != null;
            DA.GetData(3, ref when);
            DA.GetData(4, ref op);

            if (!hasExpr)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Expression is required");
                return;
            }

            string name = StateSlots.Resolve(slot);
            var outList = new List<Colugo_Entity>(list.Count);
            foreach (var g in list)
            {
                if (g?.Value == null) continue;
                var def = g.Value.Duplicate();
                if (def.Program == null) def.Program = new EntityProgramDef();
                def.Program.Steps.Add(new EntityStepDef
                {
                    Slot = name,
                    Op = op == 2 ? "add" : "set",
                    Expr = expr.Value.Duplicate(),
                    When = when?.Value?.Duplicate(),
                });
                outList.Add(new Colugo_Entity(def));
            }

            DA.SetDataList(0, outList);
            string info = $"{name} {(op == 2 ? "+=" : "=")} expr{(when?.Value != null ? " (when)" : "")}";
            Message = info;
            DA.SetData(1, info);
        }
    }

    /// <summary>
    /// Spawn —— 条件**上升沿**生成子代。
    ///
    /// 上升沿而不是每帧：条件持续为真时每帧都生成，人口会在一秒内炸到上限，
    /// 这种"看起来能动、其实不可控"的行为不该是默认。
    ///
    /// 子代继承父代的全部状态（速度/尺寸/透明度/aux），只有 age 归零、gen +1、index 递增。
    /// 所以"继承"不是特例，是默认；"变得不一样"要靠表达式读 `index` 或 `gen` 自己算。
    /// </summary>
    public class SpawnComponent : GH_Component
    {
        public SpawnComponent()
            : base("Spawn", "Spawn", "Creates child instances when the condition turns true", "Colugo", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f6");
        protected override Bitmap Icon => Icons.Spawn;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities from Blueprint / Look", GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_Expr(), "When", "When", "Spawn on the rising edge of this expression", GH_ParamAccess.item);
            p.AddIntegerParameter("Count", "Count", "Children per spawn", GH_ParamAccess.item, 2);
            p.AddNumberParameter("Spread", "Spread", "Radius of the ring the children are placed on, in pixels", GH_ParamAccess.item, 8.0);
            p.AddIntegerParameter("Max Gen", "MaxGen", "Maximum generation. 0 = unlimited", GH_ParamAccess.item, 3);
            p.AddIntegerParameter("Max Pop", "MaxPop", "Population ceiling per blueprint. 0 = only the 20000 hard limit", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities carrying the spawn rule", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Spawn summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var list = new List<Colugo_Entity>();
            if (!DA.GetDataList(0, list) || list.Count == 0) return;

            Colugo_Expr when = null;
            if (!DA.GetData(1, ref when) || when?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "When is required");
                return;
            }

            int count = 2, maxGen = 3, maxPop = 0;
            double spread = 8;
            DA.GetData(2, ref count);
            DA.GetData(3, ref spread);
            DA.GetData(4, ref maxGen);
            DA.GetData(5, ref maxPop);

            count = Math.Max(1, Math.Min(64, count));
            maxGen = Math.Max(0, Math.Min(64, maxGen));
            maxPop = Math.Max(0, Math.Min(20000, maxPop));

            var outList = new List<Colugo_Entity>(list.Count);
            foreach (var g in list)
            {
                if (g?.Value == null) continue;
                var def = g.Value.Duplicate();
                if (def.Program == null) def.Program = new EntityProgramDef();
                def.Program.SpawnWhen = when.Value.Duplicate();
                def.Program.SpawnCount = count;
                def.Program.SpawnSpread = Math.Max(0, spread);
                def.Program.MaxGen = maxGen;
                def.Program.MaxPop = maxPop;
                outList.Add(new Colugo_Entity(def));
            }

            DA.SetDataList(0, outList);
            string info = $"×{count} · spread {spread:0.#} · gen ≤ {(maxGen == 0 ? "∞" : maxGen.ToString())}";
            Message = info;
            DA.SetData(1, info);
        }
    }

    /// <summary>
    /// Destroy —— 条件非 0 时移除实例。
    ///
    /// `Keep` &gt; 0 时，被移除实例的最后状态会留成静态残影（最近 N 个）：
    /// 这是"留痕"最便宜的实现 —— 不生成新几何、不进 patch，只是不再参与步进。
    /// </summary>
    public class DestroyComponent : GH_Component
    {
        public DestroyComponent()
            : base("Destroy", "Destroy", "Removes instances when the condition is non-zero", "Colugo", "Entity") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000f7");
        protected override Bitmap Icon => Icons.Destroy;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities from Blueprint / Look", GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_Expr(), "When", "When", "Remove the instance when this is non-zero", GH_ParamAccess.item);
            p.AddIntegerParameter("Keep", "Keep", "Ghosts kept from removed instances. 0 = remove completely", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent", "Entities carrying the destroy rule", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Destroy summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var list = new List<Colugo_Entity>();
            if (!DA.GetDataList(0, list) || list.Count == 0) return;

            Colugo_Expr when = null;
            if (!DA.GetData(1, ref when) || when?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "When is required");
                return;
            }

            int keep = 0;
            DA.GetData(2, ref keep);
            keep = Math.Max(0, Math.Min(2000, keep));

            var outList = new List<Colugo_Entity>(list.Count);
            foreach (var g in list)
            {
                if (g?.Value == null) continue;
                var def = g.Value.Duplicate();
                if (def.Program == null) def.Program = new EntityProgramDef();
                def.Program.DestroyWhen = when.Value.Duplicate();
                def.Program.Keep = keep;
                outList.Add(new Colugo_Entity(def));
            }

            DA.SetDataList(0, outList);
            string info = keep > 0 ? $"remove · keep {keep}" : "remove";
            Message = info;
            DA.SetData(1, info);
        }
    }
}
