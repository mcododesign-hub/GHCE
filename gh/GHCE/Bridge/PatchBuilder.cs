using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using GHCE.Types;

namespace GHCE.Bridge
{
    /// <summary>
    /// 把一次求解的结果编译成一帧 patch。
    /// 本类是项目的核心资产：它决定"上游改一个 slider，下游传多少字节"。
    /// </summary>
    public sealed class PatchBuilder
    {
        readonly Dictionary<string, string> _geomFp = new Dictionary<string, string>();
        readonly Dictionary<string, string> _cmdKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _layerKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _entKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _evtKey = new Dictionary<string, string>();
        string _canvasKey;
        int _rev;
        int _seq;
        bool _hasState;

        /// <summary>强制下一帧走整帧快照（新客户端接入 / 重连 / 用户点 Reset）</summary>
        public void Invalidate() => _hasState = false;

        /// <summary>
        /// 返回要发送的 JSON；整帧无变化时返回 null（不发包）。
        /// 几何由指令携带的 Path 引用推导（去重后发送），因此电池图里
        /// Paths 不需要额外接一条线到 Render Output。
        /// 图层由指令的 Layer 字段推导；若指令上挂了 LayerDef（Layer 电池），则用显式的
        /// order / opacity / blend / visible，不再按出现顺序猜。
        /// </summary>
        public string Build(CanvasDef canvas, IList<GHCE_Command> commands, IList<GHCE_Entity> entities, out string summary)
        {
            var sw = Stopwatch.StartNew();
            var ops = new List<object>(Math.Max(8, commands.Count * 2));
            bool snapshot = !_hasState || BridgeServer.ConsumeResync();

            entities = entities ?? new List<GHCE_Entity>();

            // ---- 事件规则由指令携带（Event Trigger 是管道电池）----
            // 同一条规则可能挂在多条指令上：按 id 合并，把 targets 并起来，
            // 这样"25 个方块里挑 8 个"只发一条规则 + 8 个目标，而不是 8 条重复规则。
            var events = CollectEvents(commands);

            // ---- 先按引用收集几何（同一份几何被多条指令/多个蓝图共享时只发一次）----
            var pathSet = new List<PathDef>();
            var pathSeen = new HashSet<string>();
            foreach (var gc in commands)
            {
                var c = gc.Value;
                if (c == null || c.Path == null || string.IsNullOrEmpty(c.Path.Id)) continue;
                if (pathSeen.Add(c.Path.Id)) pathSet.Add(c.Path);
            }
            foreach (var ge in entities)
            {
                var e = ge.Value;
                if (e == null || e.Path == null || string.IsNullOrEmpty(e.Path.Id)) continue;
                if (pathSeen.Add(e.Path.Id)) pathSet.Add(e.Path);
            }

            // ---- 画布 ----
            var canvasKey = $"{canvas.W}x{canvas.H}|{canvas.Bg}|{canvas.Dpr}|{canvas.Transparent}";
            if (snapshot || canvasKey != _canvasKey)
            {
                var cv = new Dictionary<string, object>
                {
                    ["w"] = canvas.W,
                    ["h"] = canvas.H,
                    ["bg"] = canvas.Transparent ? null : canvas.Bg,
                    ["dpr"] = canvas.Dpr > 0 ? (object)canvas.Dpr : "auto"
                };
                ops.Add(snapshot
                    ? new Dictionary<string, object> { ["op"] = "doc.reset", ["canvas"] = cv }
                    : new Dictionary<string, object> { ["op"] = "canvas.set", ["value"] = cv });
                _canvasKey = canvasKey;
            }

            // ---- 图层 ----
            // v0.10 起支持显式定义（Layer 电池）：同名图层取显式 order / opacity / blend / visible。
            // 没有显式定义的图层仍按"首次出现序号 × 10"推导 —— 老定义的观感不变。
            var layers = new Dictionary<string, LayerDef>();

            void Note(string name, LayerDef explicitDef)
            {
                if (!layers.TryGetValue(name, out var def))
                {
                    def = new LayerDef { Name = name, Order = layers.Count * 10 };
                    layers[name] = def;
                }
                if (explicitDef == null) return;
                def.Order = explicitDef.Order;
                def.Opacity = explicitDef.Opacity;
                def.Blend = explicitDef.Blend;
                def.Visible = explicitDef.Visible;
            }

            foreach (var c in commands)
            {
                var d = c.Value;
                if (d == null) continue;
                Note(string.IsNullOrEmpty(d.Layer) ? "main" : d.Layer, d.LayerDef);
            }
            foreach (var ge in entities)
            {
                var d = ge.Value;
                if (d == null) continue;
                Note(string.IsNullOrEmpty(d.Layer) ? "particles" : d.Layer, null);
            }

            var seenLayers = new HashSet<string>();
            foreach (var kv in layers)
            {
                seenLayers.Add(kv.Key);
                var def = kv.Value;
                var key = $"{def.Order}|{def.Opacity}|{def.Blend}|{def.Visible}";
                if (snapshot || !_layerKey.TryGetValue(kv.Key, out var prev) || prev != key)
                {
                    ops.Add(new Dictionary<string, object>
                    {
                        ["op"] = "layer.upsert",
                        ["value"] = new Dictionary<string, object>
                        {
                            ["id"] = kv.Key,
                            ["name"] = kv.Key,
                            ["order"] = def.Order,
                            ["opacity"] = def.Opacity,
                            ["blend"] = def.Blend,
                            ["visible"] = def.Visible
                        }
                    });
                    _layerKey[kv.Key] = key;
                }
            }
            foreach (var old in new List<string>(_layerKey.Keys))
            {
                if (seenLayers.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "layer.remove", ["id"] = old });
                _layerKey.Remove(old);
            }

            // ---- 几何 ----
            var seenGeom = new HashSet<string>();
            foreach (var p in pathSet)
            {
                if (p.XY == null || p.XY.Length < 2) continue;
                seenGeom.Add(p.Id);

                var b64 = ToBase64F32(p.XY);
                var fp = Fnv(b64).ToString("x8");
                if (!snapshot && _geomFp.TryGetValue(p.Id, out var prevFp) && prevFp == fp) continue;

                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = "geom.upsert",
                    ["id"] = p.Id,
                    ["value"] = new Dictionary<string, object>
                    {
                        ["kind"] = p.IsPoints ? "points" : (p.Closed ? "polygon" : "polyline"),
                        ["enc"] = "f32",
                        ["coords"] = b64
                    }
                });
                _geomFp[p.Id] = fp;
            }
            foreach (var old in new List<string>(_geomFp.Keys))
            {
                if (seenGeom.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "geom.remove", ["id"] = old });
                _geomFp.Remove(old);
            }

            // ---- 绘图指令 ----
            var seenCmd = new HashSet<string>();
            foreach (var gc in commands)
            {
                var c = gc.Value;
                if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                seenCmd.Add(c.Id);

                var key = c.GeomId + "|" + c.StyleKey()
                    + "|" + c.MoveX.ToString("0.####") + "," + c.MoveY.ToString("0.####")
                    + "|" + c.Scale.ToString("0.####") + "|" + c.Rotation.ToString("0.####")
                    + "|" + c.AnchorX.ToString("0.####") + "," + c.AnchorY.ToString("0.####")
                    + "|" + c.Progress.ToString("0.####")
                    + "|" + (c.Matrix == null ? "-" : string.Join(",", c.Matrix.Select(x => x.ToString("0.#####"))))
                    + "|" + TrackKey(c.Track);
                if (!snapshot && _cmdKey.TryGetValue(c.Id, out var prevKey) && prevKey == key) continue;

                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = "cmd.upsert",
                    ["value"] = new Dictionary<string, object>
                    {
                        ["id"] = c.Id,
                        ["geom"] = c.GeomId,
                        ["layer"] = string.IsNullOrEmpty(c.Layer) ? "main" : c.Layer,
                        ["order"] = c.Order,
                        ["visible"] = c.Visible,
                        ["opacity"] = c.Opacity,
                        ["style"] = new Dictionary<string, object>
                        {
                            ["stroke"] = new Dictionary<string, object>
                            {
                                ["color"] = c.Stroke.Color,
                                ["width"] = c.Stroke.Width,
                                ["cap"] = c.Stroke.Cap,
                                ["join"] = c.Stroke.Join,
                                ["dash"] = c.Stroke.Dash,
                                ["alpha"] = c.Stroke.Alpha
                            },
                            ["fill"] = new Dictionary<string, object>
                            {
                                ["color"] = c.Fill.Color,
                                ["alpha"] = c.Fill.Alpha,
                                ["rule"] = c.Fill.Rule
                            },
                                ["pointSize"] = c.PointSize,
                                ["reveal"] = Math.Max(0, Math.Min(1, c.Reveal))
                            },
                            ["transform"] = new Dictionary<string, object>
                            {
                                ["matrix"] = c.Matrix,
                                // 0 = 原位，1 = 矩阵终态。浏览器按 TRS 分解插值，让 PSR "演"出来
                                ["progress"] = c.Progress,
                                ["translate"] = new[] { c.MoveX, c.MoveY },
                                ["rotate"] = c.Rotation,          // 度
                                ["scale"] = new[] { c.Scale, c.Scale },
                                ["pivot"] = new[] { c.AnchorX, c.AnchorY }
                            },
                            ["track"] = TrackPayload(c.Track)
                    }
                });
                _cmdKey[c.Id] = key;
            }
            foreach (var old in new List<string>(_cmdKey.Keys))
            {
                if (seenCmd.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "cmd.remove", ["id"] = old });
                _cmdKey.Remove(old);
            }

            // ---- 动态对象蓝图（阶段 2）----
            // diff 用整份 JSON：外观变化也要发（运行时靠"定义指纹"决定是否重启实例，
            // 指纹只含 count/seed/behavior/speed/lifetime/trail，所以换颜色不会重置 age）。
            var seenEnt = new HashSet<string>();
            foreach (var ge in entities)
            {
                var e = ge.Value;
                if (e == null || e.Path == null || string.IsNullOrEmpty(e.Id)) continue;
                seenEnt.Add(e.Id);

                var value = new Dictionary<string, object>
                {
                    ["geom"] = e.Path.Id,
                    ["layer"] = string.IsNullOrEmpty(e.Layer) ? "particles" : e.Layer,
                    ["order"] = 0,
                    ["count"] = e.Count,
                    ["speed"] = e.Speed,
                    ["lifetime"] = e.Lifetime,
                    ["phase"] = e.Phase,
                    ["trail"] = e.Trail,
                    ["size"] = e.Size,
                    ["shape"] = e.Shape == null ? null : ShapePayload(e.Shape),
                    ["width"] = e.Width,
                    ["color"] = e.Color,
                    ["fade"] = e.Fade,
                    ["opacity"] = e.Opacity
                };

                var key = JsonSerializer.Serialize(value);
                if (!snapshot && _entKey.TryGetValue(e.Id, out var prevEnt) && prevEnt == key) continue;

                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = "entity.upsert",
                    ["id"] = e.Id,
                    ["value"] = value
                });
                _entKey[e.Id] = key;
            }
            foreach (var old in new List<string>(_entKey.Keys))
            {
                if (seenEnt.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "entity.remove", ["id"] = old });
                _entKey.Remove(old);
            }

            // ---- 事件触发（阶段 3 D）----
            // 规则只发一次；命中后由浏览器执行，不产生后续 patch。
            var seenEvt = new HashSet<string>();
            foreach (var e in events)
            {
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                seenEvt.Add(e.Id);

                var value = new Dictionary<string, object>
                {
                    ["event"] = e.Event,
                    ["action"] = e.Action,
                    ["detail"] = e.Detail == null ? "" : e.Detail,
                    ["targets"] = e.Targets ?? new string[0]
                };
                var key = JsonSerializer.Serialize(value);
                if (!snapshot && _evtKey.TryGetValue(e.Id, out var prevEvt) && prevEvt == key) continue;

                ops.Add(new Dictionary<string, object> { ["op"] = "event.upsert", ["id"] = e.Id, ["value"] = value });
                _evtKey[e.Id] = key;
            }
            foreach (var old in new List<string>(_evtKey.Keys))
            {
                if (seenEvt.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "event.remove", ["id"] = old });
                _evtKey.Remove(old);
            }

            _hasState = true;
            if (ops.Count == 0)
            {
                summary = "no-op";
                return null;   // 整帧无变化：一个字节都不发
            }

            _rev++;
            var msg = new Dictionary<string, object>
            {
                ["v"] = "0.1",
                ["t"] = "patch",
                ["seq"] = ++_seq,
                ["rev"] = _rev,
                ["ops"] = ops,
                ["stats"] = new Dictionary<string, object>
                {
                    ["geoms"] = _geomFp.Count,
                    ["cmds"] = _cmdKey.Count,
                    ["buildMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
                }
            };
            if (snapshot) msg["snapshot"] = true;

            summary = $"rev {_rev}: {ops.Count} ops, {sw.Elapsed.TotalMilliseconds:F2} ms";
            return JsonSerializer.Serialize(msg);
        }

        /// <summary>
        /// 从指令上收集事件规则，按 id 合并 targets。
        /// 一条规则挂在 8 条指令上 → 只发一条规则 + 8 个目标（而不是 8 条一模一样的规则）。
        /// </summary>
        static List<EventDef> CollectEvents(IList<GHCE_Command> commands)
        {
            var byId = new Dictionary<string, EventDef>(StringComparer.Ordinal);
            var order = new List<string>();
            for (int i = 0; i < commands.Count; i++)
            {
                var c = commands[i]?.Value;
                if (c == null || c.Events == null || c.Events.Count == 0) continue;
                foreach (var e in c.Events)
                {
                    if (e == null || string.IsNullOrEmpty(e.Id)) continue;
                    if (!byId.TryGetValue(e.Id, out var agg))
                    {
                        agg = new EventDef
                        {
                            Id = e.Id, Event = e.Event, Action = e.Action, Detail = e.Detail,
                            Targets = new string[0]
                        };
                        byId[e.Id] = agg;
                        order.Add(e.Id);
                    }
                    if (e.Targets != null && e.Targets.Length > 0)
                    {
                        var set = new HashSet<string>(agg.Targets, StringComparer.Ordinal);
                        var merged = new List<string>(agg.Targets);
                        foreach (var t in e.Targets) if (set.Add(t)) merged.Add(t);
                        agg.Targets = merged.ToArray();
                    }
                }
            }

            var list = new List<EventDef>(order.Count);
            foreach (var id in order) list.Add(byId[id]);
            return list;
        }

        // ------------------------------------------------------------------
        // 稳定 ID：几何指纹，不依赖数据树分支路径或列表索引
        // 与 runtime/src/geom.js 的 fingerprint() 保持同一算法，便于双侧互校
        // ------------------------------------------------------------------
        public static string StableId(string prefix, double[] xy)
        {
            var b64 = ToBase64F32(xy);
            return prefix + Fnv(b64).ToString("x8");
        }

        static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var ch in s)
            {
                h ^= (byte)ch;
                h *= 16777619;
            }
            return h;
        }

        /// <summary>时间轨的 diff 指纹：帧序列用 base64 的 FNV，避免每帧重发几万个数字</summary>
        static string TrackKey(TrackDef t)
        {
            if (t == null || t.N < 2 || (t.Frames == null && t.Reveal == null)) return "-";
            var ease = t.EaseY == null
                ? "-"
                : Fnv(string.Join(",", t.EaseY.Select(x => x.ToString("0.####")))).ToString("x8");
            var fr = t.Frames == null ? "-" : Fnv(ToBase64F32(t.Frames)).ToString("x8");
            var rv = t.Reveal == null
                ? "-"
                : Fnv(string.Join(",", t.Reveal.Select(x => x.ToString("0.####")))).ToString("x8");
            return $"{t.N}|{t.Duration:0.####}|{(t.Loop ? 1 : 0)}|{fr}|{rv}|{ease}";
        }

        /// <summary>
        /// 时间轨负载。帧序列走 f32+base64 —— 25 条指令 × 60 帧 × 6 个分量是 9000 个数，
        /// 走 JSON 数字数组会让一帧 patch 涨到几百 KB。
        /// </summary>
        static Dictionary<string, object> TrackPayload(TrackDef t)
        {
            if (t == null || t.N < 2) return null;
            bool hasFrames = t.Frames != null && t.Frames.Length >= t.N * 6;
            bool hasReveal = t.Reveal != null && t.Reveal.Length >= t.N;
            if (!hasFrames && !hasReveal) return null;
            return new Dictionary<string, object>
            {
                ["n"] = t.N,
                ["frames"] = hasFrames ? ToBase64F32(t.Frames) : null,
                ["reveal"] = hasReveal ? t.Reveal : null,
                ["duration"] = t.Duration <= 1e-6 ? 1.0 : t.Duration,
                ["loop"] = t.Loop,
                ["easeX"] = t.EaseX,
                ["easeY"] = t.EaseY
            };
        }

        /// <summary>
        /// 粒子形状（局部坐标，已归一化）。跟几何用同一套 f32+base64 编码，
        /// 因为形状可能上千个点，走 JSON 数字数组会把带宽吃掉。
        /// </summary>
        static Dictionary<string, object> ShapePayload(PathDef def)
        {
            if (def == null || def.XY == null || def.XY.Length < 4) return null;
            return new Dictionary<string, object>
            {
                ["kind"] = def.Closed ? "polygon" : "polyline",
                ["enc"] = "f32",
                ["coords"] = ToBase64F32(def.XY)
            };
        }

        /// <summary>页面坐标 double[] → Float32 little-endian → base64</summary>
        public static string ToBase64F32(double[] xy)
        {
            var floats = new float[xy.Length];
            for (int i = 0; i < xy.Length; i++) floats[i] = (float)xy[i];
            var bytes = new byte[floats.Length * 4];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }
    }
}
