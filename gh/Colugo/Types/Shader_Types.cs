using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Colugo.Types
{
    /// <summary>
    /// 一个 Shader IR 节点。除 id / op 之外的字段全放 Args：
    /// a / b / t（输入引用）、v（常量）、name / type（uniform）、space、c（swizzle 分量）、in0..out1（remap）。
    /// 用字典而不是一长串可空字段，是因为白名单会长（任务书 S3/S4 还要加），
    /// 每加一个 op 都要改编一个 struct 的字段，那是自找的返工。
    /// </summary>
    public class ShaderNodeDef
    {
        public string Id;
        public string Op;
        public Dictionary<string, object> Args = new Dictionary<string, object>();

        public ShaderNodeDef Duplicate() => new ShaderNodeDef
        {
            Id = Id,
            Op = Op,
            Args = new Dictionary<string, object>(Args),
        };

        public Dictionary<string, object> ToPayload()
        {
            var d = new Dictionary<string, object> { ["id"] = Id, ["op"] = Op };
            foreach (var kv in Args) d[kv.Key] = kv.Value;
            return d;
        }

        /// 用于增量判断：op 相同、参数相同就是同一个节点
        public string Key() => BuildKey(false);

        /// <summary>
        /// 结构键：剔除 uniform 的**值**。
        /// 用来判断"这张图的结构有没有变"——拖动 uniform 的值不算结构变化，
        /// 走增量 op 就够了，不该让整条 cmd3d 重发。
        /// </summary>
        public string StructKey() => BuildKey(true);

        string BuildKey(bool skipUniformValue)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Id).Append(':').Append(Op);
            foreach (var k in Args.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (skipUniformValue && Op == "uniform" && k == "value") continue;
                sb.Append('|').Append(k).Append('=');
                if (Args[k] is double[] arr) sb.Append(string.Join(",", arr.Select(v => v.ToString("R"))));
                else if (Args[k] is double dv) sb.Append(dv.ToString("R"));
                else sb.Append(Args[k]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一张 Shader 子图：节点表 + 输出节点 id + 输出类型。
    /// 它才是电池之间流动的东西 —— 不是"一个值"，而是"一段可拼进更大图的节点表"。
    /// </summary>
    public class ShaderExprDef
    {
        public List<ShaderNodeDef> Nodes = new List<ShaderNodeDef>();
        public string OutId;
        public string Type = "v3";   // f / v2 / v3

        /// <summary>
        /// 节点 id 的命名空间。必须每只电池不同 —— 否则两个下游电池各自并入同一上游后，
        /// 自增出来的 id 会撞在一起（都是 n0、n1），合并进 Mix 时运行时就看到重复 id。
        /// 用电池的 InstanceGuid 前 6 位：同一只电池反复求解时 id 稳定（哈希才稳定），
        /// 换一只电池就自动换命名空间。
        /// </summary>
        readonly string _prefix;
        int _seq;

        public ShaderExprDef(string prefix = "n") { _prefix = string.IsNullOrEmpty(prefix) ? "n" : prefix; }

        public bool IsValid => Nodes.Count > 0 && !string.IsNullOrEmpty(OutId);

        public ShaderExprDef Duplicate() => new ShaderExprDef(_prefix)
        {
            Nodes = Nodes.Select(n => n.Duplicate()).ToList(),
            OutId = OutId,
            Type = Type,
            _seq = _seq,
        };

        /// <summary>加一个节点，返回它的 id。</summary>
        public string Add(string op, string type, params (string key, object val)[] args)
        {
            var n = new ShaderNodeDef { Id = $"{_prefix}_{_seq++}", Op = op };
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].val != null) n.Args[args[i].key] = args[i].val;
            }
            n.Args["ty"] = type;
            Nodes.Add(n);
            return n.Id;
        }

        /// <summary>
        /// 并入一段上游子图。按 id 去重 —— 同一个上游接两次（比如 c0 同时给 mix 的 a 和 b），
        /// 它的节点只能出现一份，否则运行时会看到重复 id。
        /// </summary>
        public void Absorb(ShaderExprDef src)
        {
            if (src == null) return;
            var have = new HashSet<string>(Nodes.Select(n => n.Id));
            foreach (var n in src.Nodes)
            {
                if (!have.Contains(n.Id)) Nodes.Add(n.Duplicate());
            }
        }

        public string Key() =>
            string.Join(";", Nodes.Select(n => n.Key())) + "=>" + OutId + ":" + Type;

        /// <summary>结构键：剔除 uniform 值。拖滑条不该让结构键变。</summary>
        public string StructKey() =>
            string.Join(";", Nodes.Select(n => n.StructKey())) + "=>" + OutId + ":" + Type;

        /// <summary>uniform 名 → (类型, 值)。增量 diff 靠它算出"哪个值变了"。</summary>
        public Dictionary<string, (string type, object val)> UniformMap()
        {
            var d = new Dictionary<string, (string, object)>();
            foreach (var n in Nodes)
            {
                if (n.Op != "uniform") continue;
                if (!(n.Args.GetValueOrDefault("name") is string name) || string.IsNullOrEmpty(name)) continue;
                var type = n.Args.GetValueOrDefault("type") as string ?? "f";
                d[name] = (type, n.Args.GetValueOrDefault("value"));
            }
            return d;
        }

        public override string ToString() =>
            IsValid ? $"{Type} · {Nodes.Count} 节点 → {OutId}" : "<empty>";

        public Dictionary<string, object> ToIR() => new Dictionary<string, object>
        {
            ["v"] = 1,
            ["nodes"] = Nodes.Select(n => n.ToPayload()).ToList(),
            ["out"] = OutId,
        };
    }

    public class Colugo_ShaderExpr : GH_Goo<ShaderExprDef>
    {
        public Colugo_ShaderExpr() { }
        public Colugo_ShaderExpr(ShaderExprDef def) { Value = def; }

        public override bool IsValid => Value != null && Value.IsValid;
        public override string TypeName => "Colugo ShaderExpr";
        public override string TypeDescription => "A shader subgraph: node table plus output id";

        public override IGH_Goo Duplicate()
        {
            if (Value == null) return new Colugo_ShaderExpr();
            return new Colugo_ShaderExpr(Value.Duplicate());
        }

        public override string ToString() => Value == null ? "<null>" : Value.ToString();

        /// <summary>
        /// 把一个数字变成只有 const 节点的子图。
        /// id 里带值本身而不是序号 —— 同值必须同 id，否则每次求解哈希都变、材质重建。
        /// </summary>
        public static ShaderExprDef Const(double v)
        {
            var e = new ShaderExprDef("k");
            var id = "k_" + v.ToString("R").Replace("-", "m").Replace(".", "_");
            e.Nodes.Add(new ShaderNodeDef
            {
                Id = id,
                Op = "const",
                Args = { ["v"] = v, ["ty"] = "f" },
            });
            e.OutId = id;
            e.Type = "f";
            return e;
        }

        public override bool CastFrom(object source)
        {
            if (source is ShaderExprDef d) { Value = d; return true; }
            if (source is Colugo_ShaderExpr g) { Value = g.Value?.Duplicate(); return Value != null; }
            // 直接接一个数字：省掉"常量也要拉一只电池"的麻烦。
            // sin 的入参乘个系数是最常见的用法，让用户为此连一只电池是设计缺陷。
            if (source is double dv) { Value = Const(dv); return true; }
            if (source is int iv) { Value = Const(iv); return true; }
            if (source is Grasshopper.Kernel.Types.GH_Number gn) { Value = Const(gn.Value); return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q) == typeof(ShaderExprDef) && Value != null)
            {
                target = (Q)(object)Value;
                return true;
            }
            return false;
        }
    }

    public class Param_Colugo_ShaderExpr : GH_Param<Colugo_ShaderExpr>
    {
        public Param_Colugo_ShaderExpr()
            : base("Shader Expr", "SE", "Shader subgraph", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000da");
        protected override Bitmap Internal_Icon_24x24 => Icons.DataShader;
    }
}
