using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using Colugo.Types;

namespace Colugo.Bridge
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
        readonly Dictionary<string, string> _meshKey = new Dictionary<string, string>();
        readonly Dictionary<string, string> _cmd3dKey = new Dictionary<string, string>();
        // S2：存"上一次的结构键"和"上一次的 uniform 值"。
        // 结构没变、只有 uniform 值变了，就不发整条 cmd3d，只发一条 shader.uniform。
        readonly Dictionary<string, string> _cmd3dStruct = new Dictionary<string, string>();
        readonly Dictionary<string, Dictionary<string, (string type, object val)>> _cmd3dUniforms
            = new Dictionary<string, Dictionary<string, (string type, object val)>>();
        string _scene3dKey;
        bool _has3D;
        string _canvasKey;
        string _timelineKey;
        int _rev;
        int _seq;
        bool _hasState;

        /// <summary>图片 id → 指纹（尺寸 + 载荷长度）。</summary>
        readonly Dictionary<string, string> _imgKey = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>变量 → 初值指纹。初值是"文档属性"，改了才重发。</summary>
        readonly Dictionary<string, string> _varKey = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>强制下一帧走整帧快照（新客户端接入 / 重连 / 用户点 Reset）</summary>
        public void Invalidate() => _hasState = false;

        /// <summary>
        /// 返回要发送的 JSON；整帧无变化时返回 null（不发包）。
        /// 几何由指令携带的 Path 引用推导（去重后发送），因此电池图里
        /// Paths 不需要额外接一条线到 Render Output。
        /// 图层由指令的 Layer 字段推导；若指令上挂了 LayerDef（Layer 电池），则用显式的
        /// order / opacity / blend / visible，不再按出现顺序猜。
        /// </summary>
        public string Build(CanvasDef canvas, IList<Colugo_Command> commands, IList<Colugo_Entity> entities,
                            Scene3DDef scene3d, out string summary)
        {
            var sw = Stopwatch.StartNew();
            var ops = new List<object>(Math.Max(8, commands.Count * 2));
            bool snapshot = !_hasState || BridgeServer.ConsumeResync();

            entities = entities ?? new List<Colugo_Entity>();

            // ---- 事件规则由指令携带（Event Trigger 是管道电池）----
            // 同一条规则可能挂在多条指令上：按 id 合并，把 targets 并起来，
            // 这样"25 个方块里挑 8 个"只发一条规则 + 8 个目标，而不是 8 条重复规则。
            var events = CollectEvents(commands);

            // ---- 先按引用收集几何（同一份几何被多条指令/多个蓝图共享时只发一次）----
            var pathSet = new List<PathDef>();
            var pathSeen = new HashSet<string>();
            var imageSet = new List<ImageDef>();
            var imageSeen = new HashSet<string>();
            foreach (var gc in commands)
            {
                var c = gc.Value;
                if (c == null) continue;

                if (c.Path != null && !string.IsNullOrEmpty(c.Path.Id) && pathSeen.Add(c.Path.Id))
                    pathSet.Add(c.Path);

                // 裁剪轮廓不是任何指令的 Path，必须单独收 —— 否则运行时找不到模具
                if (c.Clip?.Path != null && !string.IsNullOrEmpty(c.Clip.GeomId) && pathSeen.Add(c.Clip.GeomId))
                    pathSet.Add(c.Clip.Path);

                if (c.Image != null && !string.IsNullOrEmpty(c.Image.Id) && imageSeen.Add(c.Image.Id))
                    imageSet.Add(c.Image);
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

            // ---- 全局时间轴 ----
            // Track 只带 start/end；"整段多长、要不要循环"是文档级的，这里统一发一条。
            // 搭在 cmd 上只是为了少一个电池端口，语义上属于 doc。
            TimelineDef tl = null;
            foreach (var gc in commands)
            {
                if (gc?.Value?.Timeline != null) { tl = gc.Value.Timeline; break; }
            }
            var tlKey = tl == null ? "-" : $"{tl.Duration:0.####}|{(tl.Loop ? 1 : 0)}";
            if (snapshot || tlKey != _timelineKey)
            {
                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = "timeline.set",
                    ["duration"] = tl == null ? 8.0 : Math.Max(1e-3, tl.Duration),
                    ["loop"] = tl == null || tl.Loop,
                });
                _timelineKey = tlKey;
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

            // ---- 图片几何（v0.33）----
            // src 是 data URL，几 MB 一條；按 ImageDef.Key() 去重，不随每帧重发。
            var seenImage = new HashSet<string>();
            foreach (var img in imageSet)
            {
                seenImage.Add(img.Id);
                var key = img.Key();
                if (!snapshot && _imgKey.TryGetValue(img.Id, out var prevImg) && prevImg == key) continue;

                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = "geom.upsert",
                    ["id"] = img.Id,
                    ["value"] = new Dictionary<string, object>
                    {
                        ["kind"] = "image",
                        ["src"] = img.Src,
                        ["w"] = img.W,
                        ["h"] = img.H,
                    }
                });
                _imgKey[img.Id] = key;
            }
            foreach (var old in new List<string>(_imgKey.Keys))
            {
                if (seenImage.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "geom.remove", ["id"] = old });
                _imgKey.Remove(old);
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
                    + "|" + TrackKey(c.Track)
                    + "|" + MorphKey(c.Morph)
                    + "|" + BindKey(c.Binds)
                    + "|" + (c.Clip?.Key() ?? "-");
                if (!snapshot && _cmdKey.TryGetValue(c.Id, out var prevKey) && prevKey == key) continue;

                // 空字段不写：track / morph / bind 绝大多数指令都没有，
                // 三个 null 在几百条标记上就是十几 KB 的无意义负载。
                // 运行时的 normalizeCmd 对缺失字段本来就有缺省值。
                var payload = new Dictionary<string, object>
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
                };

                if (c.Clip != null && !string.IsNullOrEmpty(c.Clip.GeomId))
                {
                    payload["clip"] = new Dictionary<string, object>
                    {
                        ["geom"] = c.Clip.GeomId,
                        ["invert"] = c.Clip.Invert,
                    };
                }

                var trackPayload = TrackPayload(c.Track);
                if (trackPayload != null) payload["track"] = trackPayload;
                var morphPayload = MorphPayload(c.Morph);
                if (morphPayload != null) payload["morph"] = morphPayload;
                var bindPayload = BindPayload(c.Binds);
                if (bindPayload != null) payload["bind"] = bindPayload;

                ops.Add(new Dictionary<string, object> { ["op"] = "cmd.upsert", ["value"] = payload });
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
                // 状态程序（v0.32）：null 表示"沿用沿曲线跑"，与旧作品完全兼容
                if (e.Program != null && !e.Program.IsEmpty)
                    value["program"] = e.Program.ToJson();

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
                if (e.Writes != null && e.Writes.Count > 0)
                {
                    var ws = new List<Dictionary<string, object>>(e.Writes.Count);
                    foreach (var w in e.Writes)
                    {
                        if (w == null || string.IsNullOrEmpty(w.Var)) continue;
                        ws.Add(new Dictionary<string, object>
                        {
                            ["var"] = w.Var,
                            ["op"] = w.Op ?? "set",
                            ["value"] = w.Value
                        });
                    }
                    if (ws.Count > 0) value["writes"] = ws;
                }
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

            // ---- 运行时变量（v0.30）----
            // 变量只有"被用到"才进文档：写在 Bind 的表达式里，或写在事件规则的 writes 里。
            // 初值取表达式树的声明值；事件写了但没人读的变量也给个 0，免得运行时拿到 undefined。
            var vars = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var gc in commands)
            {
                var binds = gc?.Value?.Binds;
                if (binds == null) continue;
                foreach (var kv in binds) kv.Value?.CollectVars(vars);
            }
            foreach (var e in events)
            {
                if (e?.Writes == null) continue;
                foreach (var w in e.Writes)
                {
                    if (w != null && !string.IsNullOrEmpty(w.Var) && !vars.ContainsKey(w.Var)) vars[w.Var] = 0;
                }
            }
            var seenVar = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in vars)
            {
                seenVar.Add(kv.Key);
                var vKey = kv.Value.ToString("0.#####");
                if (!snapshot && _varKey.TryGetValue(kv.Key, out var prevVar) && prevVar == vKey) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "var.upsert", ["id"] = kv.Key, ["value"] = kv.Value });
                _varKey[kv.Key] = vKey;
            }
            foreach (var old in new List<string>(_varKey.Keys))
            {
                if (seenVar.Contains(old)) continue;
                ops.Add(new Dictionary<string, object> { ["op"] = "var.remove", ["id"] = old });
                _varKey.Remove(old);
            }

            // ---- 三维场景（阶段 5）----
            // 网格与材质走增量；场景级参数（相机 / 背景 / 对象顺序）整包发。
            // 后者只有几十字节，为省这点去算差量不划算，而它一改就必须立刻生效。
            if (scene3d != null && scene3d.Cmds != null && scene3d.Cmds.Count > 0)
            {
                var seenMesh = new HashSet<string>();
                var seenCmd3 = new HashSet<string>();

                foreach (var c in scene3d.Cmds)
                {
                    if (c?.Mesh == null || string.IsNullOrEmpty(c.Mesh.Id)) continue;

                    var m = c.Mesh;
                    seenMesh.Add(m.Id);
                    var mk = MeshKey(m);
                    if (snapshot || !_meshKey.TryGetValue(m.Id, out var pm) || pm != mk)
                    {
                        ops.Add(new Dictionary<string, object>
                        {
                            ["op"] = "mesh.upsert",
                            ["id"] = m.Id,
                            ["value"] = MeshPayload(m)
                        });
                        _meshKey[m.Id] = mk;
                    }

                    seenCmd3.Add(c.Id);
                    var ck = Cmd3DKey(c);
                    var sk2 = Cmd3DStructKey(c);
                    var uNow = c.Material?.Shader?.UniformMap()
                               ?? new Dictionary<string, (string type, object val)>();

                    if (!snapshot && _cmd3dKey.TryGetValue(c.Id, out var pc) && pc == ck) continue;

                    // 结构没变、只有 uniform 值变了 → 只发增量。
                    // 这是 S2 的全部价值：拖一根滑条不该重传 13 个节点的 IR。
                    if (!snapshot
                        && _cmd3dKey.ContainsKey(c.Id)
                        && _cmd3dStruct.TryGetValue(c.Id, out var ps) && ps == sk2)
                    {
                        _cmd3dUniforms.TryGetValue(c.Id, out var uOld);
                        foreach (var kv in uNow)
                        {
                            if (uOld != null && uOld.TryGetValue(kv.Key, out var ov)
                                && Equals(ov.val, kv.Value.val)) continue;
                            ops.Add(new Dictionary<string, object>
                            {
                                ["op"] = "shader.uniform",
                                ["name"] = kv.Key,
                                ["type"] = kv.Value.type,
                                ["value"] = kv.Value.val,
                            });
                        }
                        // uniform 被删掉的情况走 upsert（图结构已经不同了，只是这里兜底）
                        bool removed = uOld != null && uOld.Keys.Any(n => !uNow.ContainsKey(n));
                        if (removed)
                        {
                            ops.Add(new Dictionary<string, object>
                            {
                                ["op"] = "cmd3d.upsert",
                                ["id"] = c.Id,
                                ["value"] = Cmd3DPayload(c)
                            });
                        }
                        _cmd3dKey[c.Id] = ck;
                        _cmd3dStruct[c.Id] = sk2;
                        _cmd3dUniforms[c.Id] = uNow;
                        continue;
                    }

                    ops.Add(new Dictionary<string, object>
                    {
                        ["op"] = "cmd3d.upsert",
                        ["id"] = c.Id,
                        ["value"] = Cmd3DPayload(c)
                    });
                    _cmd3dKey[c.Id] = ck;
                    _cmd3dStruct[c.Id] = sk2;
                    _cmd3dUniforms[c.Id] = uNow;
                }

                foreach (var old in new List<string>(_meshKey.Keys))
                {
                    if (seenMesh.Contains(old)) continue;
                    ops.Add(new Dictionary<string, object> { ["op"] = "mesh.remove", ["id"] = old });
                    _meshKey.Remove(old);
                }
                foreach (var old in new List<string>(_cmd3dKey.Keys))
                {
                    if (seenCmd3.Contains(old)) continue;
                    ops.Add(new Dictionary<string, object> { ["op"] = "cmd3d.remove", ["id"] = old });
                    _cmd3dKey.Remove(old);
                    _cmd3dStruct.Remove(old);
                    _cmd3dUniforms.Remove(old);
                }

                var sk = Scene3DKey(scene3d);
                if (snapshot || _scene3dKey != sk)
                {
                    ops.Add(new Dictionary<string, object> { ["op"] = "scene3d.set", ["value"] = Scene3DPayload(scene3d) });
                    _scene3dKey = sk;
                }
                _has3D = true;
            }
            else if (_has3D)
            {
                ops.Add(new Dictionary<string, object> { ["op"] = "scene3d.clear" });
                _meshKey.Clear();
                _cmd3dKey.Clear();
                _scene3dKey = null;
                _has3D = false;
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
        static List<EventDef> CollectEvents(IList<Colugo_Command> commands)
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
                            Targets = new string[0], Writes = e.Writes
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

        /// <summary>按文本身份生成稳定 ID（Mark 用：源路径 id + 序号）。</summary>
        public static string StableId(string prefix, string identity)
        {
            return prefix + Fnv(identity ?? string.Empty).ToString("x8");
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
        /// <summary>
        /// 信号负载：只发函数描述，不发采样值。
        /// 几十字节换 60 Hz 的实时随机，这是整个协议里最省的一次交换。
        /// </summary>
        static string MorphKey(MorphDef m)
        {
            if (m == null || m.N < 2 || m.Count < 2 || m.Frames == null) return "-";
            return $"{m.N}|{m.Count}|{m.Step:0.###}|{(m.Closed ? 1 : 0)}|{Fnv(ToBase64F32(m.Frames)).ToString("x8")}";
        }

        /// <summary>
        /// 几何 morph 负载：N 组同拓扑点缓冲，走 f32+base64。
        /// 12 帧 × 96 点 × 2 = 2304 个数，JSON 数字数组会到 20 KB 以上，base64 约 1/3。
        /// </summary>
        static Dictionary<string, object> MorphPayload(MorphDef m)
        {
            if (m == null || m.N < 2 || m.Count < 2 || m.Frames == null) return null;
            if (m.Frames.Length < (long)m.N * m.Count * 2) return null;
            return new Dictionary<string, object>
            {
                ["n"] = m.N,
                ["count"] = m.Count,
                ["step"] = m.Step <= 1e-6 ? 12.0 : m.Step,
                ["closed"] = m.Closed,
                ["frames"] = ToBase64F32(m.Frames),
            };
        }

        // ------------------------------------------------------------------
        // 三维序列化
        // ------------------------------------------------------------------

        /// <summary>Vertex fingerprint plus attribute sizes: a mesh whose normals appeared must be resent.</summary>
        static string MeshKey(MeshDef m) =>
            $"{m.Id}|{m.Kind}|{m.Normals?.Length ?? 0}|{m.Uvs?.Length ?? 0}";

        static Dictionary<string, object> MeshPayload(MeshDef m)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = string.IsNullOrEmpty(m.Kind) ? "mesh" : m.Kind,
                ["positions"] = ToBase64F32(m.Positions),
                ["indices"] = m.Indices == null || m.Indices.Length == 0 ? null : ToBase64U32(m.Indices),
                ["normals"] = m.Normals == null ? null : ToBase64F32(m.Normals),
                ["uvs"] = m.Uvs == null ? null : ToBase64F32(m.Uvs),
                ["count"] = m.VertexCount,
                ["tris"] = m.TriCount,
            };
        }

        static string Cmd3DStructKey(Cmd3DDef c) =>
            $"{c.MeshId}|{c.Material?.StructKey() ?? "-"}|{c.Xform?.Key() ?? "-"}|{(c.Visible ? 1 : 0)}|{c.Order}";

        static string Cmd3DKey(Cmd3DDef c) =>
            $"{c.MeshId}|{c.Material?.Key() ?? "-"}|{c.Xform?.Key() ?? "-"}|{(c.Visible ? 1 : 0)}|{c.Order}";

        static Dictionary<string, object> Cmd3DPayload(Cmd3DDef c)
        {
            var x = c.Xform;
            return new Dictionary<string, object>
            {
                ["mesh"] = c.MeshId,
                ["material"] = new Dictionary<string, object>
                {
                    ["mode"] = c.Material?.Mode ?? 1,
                    ["color"] = c.Material?.Color ?? "#e8e6df",
                    ["size"] = c.Material?.Size ?? 2,
                    ["glow"] = c.Material?.Glow ?? 0,
                    ["opacity"] = c.Material?.Opacity ?? 1,
                    // null 时不发这个键的语义由运行时决定：它用 if (m.shader) 判断，
                    // 所以老作品（没有 shader）走的还是常量色那条路
                    ["shader"] = c.Material?.Shader?.ToIR(),
                    // 顶点偏移图。null 时运行时不动 positionNode，走原来的静态几何
                    ["shaderDisplace"] = c.Material?.ShaderDisplace?.ToIR(),
                },
                ["xform"] = new Dictionary<string, object>
                {
                    ["matrix"] = x?.Matrix == null ? null : ToBase64F32(x.Matrix),
                    ["frames"] = x?.Frames == null ? null : ToBase64F32(x.Frames),
                    ["n"] = x?.N ?? 0,
                    ["duration"] = x?.Duration ?? 1.5,
                },
                ["visible"] = c.Visible,
                ["order"] = c.Order,
            };
        }

        static string Scene3DKey(Scene3DDef s)
        {
            var ids = new StringBuilder();
            foreach (var c in s.Cmds) ids.Append(c.Id).Append(',');
            return $"{s.Background}|{(s.Lights ? 1 : 0)}|{s.Ambient:0.###}|{s.Camera?.Key() ?? "auto"}|{ids}";
        }

        static Dictionary<string, object> Scene3DPayload(Scene3DDef s)
        {
            var ids = new List<string>(s.Cmds.Count);
            foreach (var c in s.Cmds) ids.Add(c.Id);

            var cam = s.Camera;
            object camPayload = cam == null
                ? null
                : new Dictionary<string, object>
                {
                    ["hasPos"] = cam.HasPosition,
                    ["pos"] = new[] { cam.Px, cam.Py, cam.Pz },
                    ["hasTarget"] = cam.HasTarget,
                    ["target"] = new[] { cam.Tx, cam.Ty, cam.Tz },
                    ["fov"] = cam.Fov,
                    ["ortho"] = cam.Orthographic,
                    ["zoom"] = cam.Zoom,
                };

            return new Dictionary<string, object>
            {
                ["bg"] = s.Background,
                ["lights"] = s.Lights,
                ["ambient"] = s.Ambient,
                ["camera"] = camPayload,
                ["objects"] = ids,
            };
        }

        static string ToBase64U32(int[] a)
        {
            if (a == null || a.Length == 0) return null;
            var bytes = new byte[a.Length * 4];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>绑定指纹：属性名 + 表达式树结构。空绑定按 "-" 处理。</summary>
        static string BindKey(Dictionary<string, RuntimeExprDef> binds)
        {
            if (binds == null || binds.Count == 0) return "-";
            var parts = new List<string>(binds.Count);
            foreach (var kv in binds) parts.Add($"{kv.Key}={kv.Value?.Key()}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }

        static Dictionary<string, object> BindPayload(Dictionary<string, RuntimeExprDef> binds)
        {
            if (binds == null || binds.Count == 0) return null;
            var d = new Dictionary<string, object>(binds.Count);
            foreach (var kv in binds)
            {
                if (kv.Value == null) continue;
                d[kv.Key] = kv.Value.ToJson();
            }
            return d.Count == 0 ? null : d;
        }

        static string TrackKey(TrackDef t)
        {
            if (t == null || t.N < 2) return "-";
            var ease = t.EaseY == null
                ? "-"
                : Fnv(string.Join(",", t.EaseY.Select(x => x.ToString("0.####")))).ToString("x8");
            var fr = t.Frames == null ? "-" : Fnv(ToBase64F32(t.Frames)).ToString("x8");
            var rv = t.Reveal == null
                ? "-"
                : Fnv(string.Join(",", t.Reveal.Select(x => x.ToString("0.####")))).ToString("x8");
            return $"{t.N}|{t.Start:0.####}|{t.End:0.####}|{(t.Hidden ? 1 : 0)}|{fr}|{rv}|{ease}";
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
            // 注意：两者都没有也照样发 —— morph 模式下轨道只提供区间与缓动，帧由 cmd.morph 自带
            return new Dictionary<string, object>
            {
                ["n"] = t.N,
                ["frames"] = hasFrames ? ToBase64F32(t.Frames) : null,
                ["reveal"] = hasReveal ? t.Reveal : null,
                // 轨道只报自己的位置；时长与循环由 timeline.set 统一给
                ["start"] = t.Start,
                ["end"] = t.End,              // < = start 表示占满全局时长
                ["hidden"] = t.Hidden,
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
