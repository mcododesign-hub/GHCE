using System;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

class Probe
{
    // Rhino 安装位置不写死：换机器、换盘符都要能跑
    static readonly string RH = FindRhino();

    static string FindRhino()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Rhino 8",
            @"D:\Program Files\Rhino 8",
            @"E:\Program Files\Rhino 8",
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8"),
        };
        foreach (var c in candidates)
            if (System.IO.File.Exists(System.IO.Path.Combine(c, @"System\netcore\RhinoCommon.dll")))
                return c;
        return candidates[0];   // 找不到也返回一个，让后面的 FileNotFound 里带出路径
    }

    // 可用 GHCE_BIN 指向别处，比如真正安装到 Libraries 的那份，用来验"要发布的产物"而不是构建输出。
    // 缺省值从探针自己的输出目录反推 gh/，不再依赖项目所在盘符。
    static readonly string BIN = Environment.GetEnvironmentVariable("GHCE_BIN")
        ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "GHCE", "bin", "Release", "net7.0-windows"));

    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        string path = name switch
        {
            "RhinoCommon" => RH + @"\System\netcore\RhinoCommon.dll",
            "Grasshopper" => RH + @"\Plug-ins\Grasshopper\Grasshopper.dll",
            "GH_IO" => RH + @"\Plug-ins\Grasshopper\GH_IO.dll",
            "GHCE" => BIN + @"\GHCE.dll",
            _ => null
        };
        return path != null ? Assembly.LoadFrom(path) : null;
    }

    static bool _serve;

    static int Main(string[] args)
    {
        _serve = args != null && args.Contains("--serve");
        _clock = args != null && args.Contains("--clock");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        return Run();
    }

    static bool _clock;

    // 查 GH 自带的时钟类电池：Rhino 8 的 UI 里到底有 Timer 还是只有 Trigger，
    // 以及它们的端口长什么样。不靠记忆，直接读程序集。
    static void DumpClock()
    {
        var asm = typeof(GH_Component).Assembly;
        Console.WriteLine($"grasshopper: {asm.GetName().Version}  {asm.Location}");
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

        foreach (var t in types.Where(t => typeof(IGH_DocumentObject).IsAssignableFrom(t) && !t.IsAbstract))
        {
            if (t.Name.IndexOf("Timer", StringComparison.OrdinalIgnoreCase) < 0 &&
                t.Name.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) < 0 &&
                t.Name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) < 0) continue;

            IGH_DocumentObject obj;
            try { obj = (IGH_DocumentObject)Activator.CreateInstance(t); }
            catch (Exception ex) { Console.WriteLine($"  ! {t.FullName}: {ex.GetType().Name}"); continue; }
            if (obj is not IGH_Component comp) continue;

            Console.WriteLine($"  {comp.Name,-16} [{comp.NickName}]  {t.FullName}");
            Console.WriteLine($"      cat: {comp.Category} › {comp.SubCategory}");
            foreach (var p in comp.Params.Input)
                Console.WriteLine($"      in : {p.Name,-14} {p.TypeName,-22} optional={p.Optional}");
            foreach (var p in comp.Params.Output)
                Console.WriteLine($"      out: {p.Name,-14} {p.TypeName,-22}");
        }

        // 第二遍：Params › Util 下到底有哪些电池（用户说 Rhino 8 里只有 Trigger）
        Console.WriteLine("  --- Params › Util 全家 ---");
        foreach (var t in types.Where(t => typeof(IGH_Component).IsAssignableFrom(t) && !t.IsAbstract))
        {
            IGH_Component c;
            try { c = (IGH_Component)Activator.CreateInstance(t); } catch { continue; }
            if (c.Category != "Params") continue;
            if (c.SubCategory.IndexOf("Util", StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine($"  {c.Name,-18} [{c.NickName}]  {t.FullName}");
            foreach (var p in c.Params.Input)
                Console.WriteLine($"      in : {p.Name,-14} {p.TypeName,-22} optional={p.Optional}");
            foreach (var p in c.Params.Output)
                Console.WriteLine($"      out: {p.Name,-14} {p.TypeName,-22}");
        }

        // 第三遍：名字里带 Timer/Trigger 的所有类型（不管是不是电池），看它们到底继承谁
        Console.WriteLine("  --- 名字命中 Timer/Trigger 的所有类型 ---");
        foreach (var t in types.Where(t =>
                     t.Name.IndexOf("Timer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.Name.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0))
            Console.WriteLine($"  {t.FullName}  : {t.BaseType?.Name}");

        // 第四遍：直接盯 GH_Timer，看它到底有没有端口、能不能停止/设间隔
        Console.WriteLine("  --- GH_Timer 详情 ---");
        var timerType = types.FirstOrDefault(t => t.Name == "GH_Timer");
        if (timerType == null) Console.WriteLine("  (不在程序集里)");
        else
        {
            Console.WriteLine($"  {timerType.FullName} : {timerType.BaseType?.Name}");
            Console.WriteLine("      接口: " + string.Join(", ", timerType.GetInterfaces().Select(i => i.Name)));
            foreach (var f in timerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Console.WriteLine($"      field {f.FieldType.Name,-22} {f.Name}");
        }
    }

    // 单独一个方法：Main 里只要出现 Grasshopper 类型，JIT 编译 Main 时就会去解析程序集，
    // 早于 AssemblyResolve 注册。分开之后解析器才来得及插手。
    static int Run()
    {
        if (_clock) { DumpClock(); return 0; }

        int fail = 0;
        var icons = new System.Collections.Generic.List<(string name, System.Drawing.Bitmap icon)>();

        try
        {
            // 构建输出里叫 GHCE.dll，装到 Libraries 里的叫 GHCE.gha，两种都要能读
            var dllPath = System.IO.File.Exists(BIN + @"\GHCE.dll") ? BIN + @"\GHCE.dll" : BIN + @"\GHCE.gha";
            Console.WriteLine($"artifact: {dllPath}");
            var asm = Assembly.LoadFrom(dllPath);
            Console.WriteLine($"loaded: {asm.GetName().Name} {asm.GetName().Version}");
            Console.WriteLine($"runtime: {Environment.Version}");

            var infra = asm.GetType("GHCE.GHCEInfo");
            if (infra != null)
            {
                var info = (GH_AssemblyInfo)Activator.CreateInstance(infra);
                Console.WriteLine($"assembly info: {info.Name} / {info.Id}");
            }

            foreach (var t in asm.GetTypes().Where(t => typeof(GH_Component).IsAssignableFrom(t) && !t.IsAbstract))
            {
                try
                {
                    var c = (GH_Component)Activator.CreateInstance(t);
                    var iconProp = t.GetProperty("Icon", BindingFlags.Instance | BindingFlags.NonPublic);
                    var icon = iconProp?.GetValue(c) as System.Drawing.Bitmap;
                    icons.Add((c.Name, icon));
                    Console.WriteLine($"  OK  {c.Name,-14} {c.ComponentGuid}  in={c.Params.Input.Count} out={c.Params.Output.Count} icon={(icon == null ? "NULL" : icon.Width + "x" + icon.Height)}");
                    foreach (var p in c.Params.Input)
                        Console.WriteLine($"        in : {p.Name,-12} {p.TypeName,-22} access={p.Access} optional={p.Optional}");
                    foreach (var p in c.Params.Output)
                        Console.WriteLine($"        out: {p.Name,-12} {p.TypeName}");
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine($"  FAIL {t.Name}: {Unwrap(ex).Message}");
                }
            }

            // GUID 必须互不相同：GH 用 ComponentGuid 做组件身份，撞了会互相顶掉
            // （表现为"某只电池拖着拖着变成另一只"，且不报错）。这条断言是 v0.10 加的，
            // 因为新增四只电池时手写 GUID 撞了四个而没人发现。
            {
                var guids = asm.GetTypes()
                    .Where(t => typeof(GH_Component).IsAssignableFrom(t) && !t.IsAbstract)
                    .Select(t => ((GH_Component)Activator.CreateInstance(t)).ComponentGuid)
                    .ToList();
                guids.AddRange(asm.GetTypes()
                    .Where(t => typeof(IGH_Param).IsAssignableFrom(t) && !t.IsAbstract)
                    .Select(t => ((IGH_Param)Activator.CreateInstance(t)).ComponentGuid));
                var dupes = guids.GroupBy(g => g).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
                Console.WriteLine($"  guids: {guids.Count} 个，唯一 {guids.Distinct().Count()} 个");
                if (dupes.Count > 0)
                {
                    fail++;
                    Console.WriteLine("  FAIL 重复的 GUID：" + string.Join(", ", dupes));
                }
            }

            // 参数类型（数据）也会出现在 GH 标签页里，同样必须有图标
            foreach (var t in asm.GetTypes().Where(t =>
                         typeof(Grasshopper.Kernel.IGH_Param).IsAssignableFrom(t) && !t.IsAbstract))
            {
                try
                {
                    var p = (Grasshopper.Kernel.IGH_Param)Activator.CreateInstance(t);
                    // GH 画参数图标用的是 Icon_24x24（final，内部再读 Internal_Icon_24x24）；
                    // Icon 这个属性对参数恒为 null，别拿它判断。
                    var iconProp = t.GetProperty("Icon_24x24", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var icon = iconProp?.GetValue(p) as System.Drawing.Bitmap;
                    icons.Add(("data: " + p.Name, icon));
                    Console.WriteLine($"  OK  param {p.Name,-12} {p.TypeName,-14} icon={(icon == null ? "NULL" : icon.Width + "x" + icon.Height)}");
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine($"  FAIL param {t.Name}: {Unwrap(ex).Message}");
                }
            }

            if (icons.Any(i => i.icon == null))
            {
                fail++;
                Console.WriteLine("  有图标为 null：" + string.Join(",", icons.Where(i => i.icon == null).Select(i => i.name)));
            }

            // 把图标放大 8 倍拼成一张对照图，省得为了看图标去开 GH
            try
            {
                const int S = 8, Pad = 20, Label = 34;
                int cell = 24 * S;
                int w = Pad + icons.Count * (cell + Pad);
                int h = cell + Label + Pad * 2;
                using (var sheet = new System.Drawing.Bitmap(w, h))
                using (var g = System.Drawing.Graphics.FromImage(sheet))
                {
                    g.Clear(System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0F));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using (var font = new System.Drawing.Font("Consolas", 13f))
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xE8, 0xE6, 0xDF)))
                    {
                        for (int i = 0; i < icons.Count; i++)
                        {
                            int x = Pad + i * (cell + Pad);
                            g.DrawImage(icons[i].icon, new System.Drawing.Rectangle(x, Pad, cell, cell));
                            g.DrawString(icons[i].name, font, brush, x, Pad + cell + 8);
                        }
                    }
                    // 不再写死盘符：从产物目录上溯 5 级就是仓库根。
                    // GHCE_BIN 指向别处（如 Libraries 目录）时，落回临时目录，绝不能因此让探针失败。
                    var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(BIN, "..", "..", "..", "..", ".."));
                    var shotsDir = System.IO.Path.Combine(root, "docs", "shots");
                    var outPath = System.IO.Directory.Exists(shotsDir)
                        ? System.IO.Path.Combine(shotsDir, "icons-preview.png")
                        : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-icons-preview.png");
                    sheet.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"  icons sheet: {outPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  icons sheet failed: " + Unwrap(ex).Message);
                fail++;
            }

            // 自定义 Param / Goo 往返
            var paramType = asm.GetType("GHCE.Types.Param_GHCE_Path");
            var gooType = asm.GetType("GHCE.Types.GHCE_Path");
            var defType = asm.GetType("GHCE.Types.PathDef");
            var param = (IGH_Param)Activator.CreateInstance(paramType);
            var def = Activator.CreateInstance(defType);
            defType.GetField("Id").SetValue(def, "g_test");
            defType.GetField("XY").SetValue(def, new double[] { 0, 0, 10, 10 });
            var goo = (IGH_Goo)Activator.CreateInstance(gooType, def);
            Console.WriteLine($"  goo: valid={goo.IsValid} str={goo} type={goo.TypeName}");

            // Path → Draw Command 的隐式转换（用户在 Render Output 里直接接 Path 依赖这条）
            var cmdGooType = asm.GetType("GHCE.Types.GHCE_Command");
            var cmdGoo = (IGH_Goo)Activator.CreateInstance(cmdGooType);
            var castOk = cmdGoo.CastFrom(goo);
            Console.WriteLine($"  cast Path→Command: ok={castOk} valid={cmdGoo.IsValid} str={cmdGoo}");
            if (!castOk || !cmdGoo.IsValid) fail++;
            Console.WriteLine($"  param: {param.Name} access={param.Access}");
            var dup = goo.Duplicate();
            Console.WriteLine($"  duplicate: {dup}");

            // ---------- 变换数学 P·M·P⁻¹（纯数值，刻意不碰 Rhino 类型）----------
            // 这条是本轮的核心断言：世界里的旋转，在页面上必须绕"该点映射后的位置"转。
            var trType = asm.GetType("GHCE.Components.TransformComponent");
            var cp = trType?.GetMethod("ComposePageMatrix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (cp == null)
            {
                fail++;
                Console.WriteLine("  FAIL 找不到 ComposePageMatrix");
            }
            else
            {
                Func<double[], double, double, double[]> apply = (m, x, y) =>
                    new[] { m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5] };
                var proj = new double[] { 2, 100, 700 };   // 相机：s=2，原点落在页面 (100,700)

                // 世界仿射 = 绕 (30,40) 逆时针 90°：a=0 b=-1 c=1 d=0 tx=70 ty=10
                var m1 = (double[])cp.Invoke(null, new object[] { 0.0, -1.0, 1.0, 0.0, 70.0, 10.0, proj });
                var c1 = apply(m1, 160, 620);   // 世界中心 (30,40) → 页面 (160,620)
                var q1 = apply(m1, 360, 620);   // 世界 (130,40) → 页面 (360,620)，绕中心转 90° 后应到 (160,420)
                bool anchorOk = Math.Abs(c1[0] - 160) < 1e-9 && Math.Abs(c1[1] - 620) < 1e-9;
                bool orbitOk = Math.Abs(q1[0] - 160) < 1e-9 && Math.Abs(q1[1] - 420) < 1e-9;
                Console.WriteLine($"  transform anchor: ({c1[0]:0.##},{c1[1]:0.##}) expect (160,620) ok={anchorOk}");
                Console.WriteLine($"  transform orbit : ({q1[0]:0.##},{q1[1]:0.##}) expect (160,420) ok={orbitOk}");
                if (!anchorOk || !orbitOk) fail++;

                // 世界平移 (10,20) → 页面位移 (20,40)：缩放要跟上，Y 要反向
                var m2 = (double[])cp.Invoke(null, new object[] { 1.0, 0.0, 0.0, 1.0, 10.0, 20.0, proj });
                var t2 = apply(m2, 100, 700);
                bool shiftOk = Math.Abs(t2[0] - 120) < 1e-9 && Math.Abs(t2[1] - 660) < 1e-9;
                Console.WriteLine($"  transform shift : ({t2[0]:0.##},{t2[1]:0.##}) expect (120,660) ok={shiftOk}");
                if (!shiftOk) fail++;

                // 没有相机信息时退回 F 共轭，不能崩
                var m3 = (double[])cp.Invoke(null, new object[] { 0.0, -1.0, 1.0, 0.0, 70.0, 10.0, null });
                bool fallbackOk = Math.Abs(m3[4] - 70) < 1e-9 && Math.Abs(m3[5] + 10) < 1e-9;
                Console.WriteLine($"  transform fallback: e={m3[4]:0.##} f={m3[5]:0.##} expect 70,-10 ok={fallbackOk}");
                if (!fallbackOk) fail++;
            }

            // ---------- 相机数学（纯 double，不碰 Rhino 类型）----------
            // 这是 v0.10 把投影从 Path Input 抽成相机之后必须钉住的东西：
            // 拟合比例、中心对齐、Y 翻转、固定缩放，四条都是"错了就整张图偏掉"的量。
            {
                var camType = asm.GetType("GHCE.Types.CameraMath");
                var fitM = camType.GetMethod("Fit");
                var toPageM = camType.GetMethod("ToPage");

                // 100×100 世界 → 1000×1000 页面，无边距：s 应为 10，世界中心落在页面中心
                var pFit = (double[])fitM.Invoke(null, new object[] { new double[] { 0, 0, 100, 100 }, 1000, 1000, 0.0, 0.0 });
                object[] a1 = { pFit, 50.0, 50.0, 0.0, 0.0 };
                toPageM.Invoke(null, a1);
                bool sOk = Math.Abs(pFit[0] - 10) < 1e-9;
                bool cOk = Math.Abs((double)a1[3] - 500) < 1e-9 && Math.Abs((double)a1[4] - 500) < 1e-9;
                Console.WriteLine($"  camera fit s : {pFit[0]:0.###} expect 10 ok={sOk}");
                Console.WriteLine($"  camera center: ({(double)a1[3]:0.##},{(double)a1[4]:0.##}) expect (500,500) ok={cOk}");
                if (!sOk || !cOk) fail++;

                // Y 翻转：世界左上角 (0,100) 必须落到页面左上角 (0,0)
                object[] a2 = { pFit, 0.0, 100.0, 0.0, 0.0 };
                toPageM.Invoke(null, a2);
                bool flipOk = Math.Abs((double)a2[3]) < 1e-9 && Math.Abs((double)a2[4]) < 1e-9;
                Console.WriteLine($"  camera flip  : ({(double)a2[3]:0.##},{(double)a2[4]:0.##}) expect (0,0) ok={flipOk}");
                if (!flipOk) fail++;

                // 固定缩放（动画全程共用一套框的用法）：s 被强制为 5，中心仍对齐
                var pFixed = (double[])fitM.Invoke(null, new object[] { new double[] { 0, 0, 100, 100 }, 1000, 1000, 0.0, 5.0 });
                object[] a3 = { pFixed, 50.0, 50.0, 0.0, 0.0 };
                toPageM.Invoke(null, a3);
                bool fixedOk = Math.Abs(pFixed[0] - 5) < 1e-9
                               && Math.Abs((double)a3[3] - 500) < 1e-9 && Math.Abs((double)a3[4] - 500) < 1e-9;
                Console.WriteLine($"  camera fixed : s {pFixed[0]:0.###} center ({(double)a3[3]:0.##},{(double)a3[4]:0.##}) expect 5 / (500,500) ok={fixedOk}");
                if (!fixedOk) fail++;

                // 非正方包围盒 + 内边距：s = min(360/100, 160/30) = 3.6，且中心对齐
                var pOff = (double[])fitM.Invoke(null, new object[] { new double[] { -20, 10, 80, 40 }, 400, 200, 20.0, 0.0 });
                object[] a4 = { pOff, 30.0, 25.0, 0.0, 0.0 };
                toPageM.Invoke(null, a4);
                bool offOk = Math.Abs(pOff[0] - 3.6) < 1e-9
                             && Math.Abs((double)a4[3] - 200) < 1e-9 && Math.Abs((double)a4[4] - 100) < 1e-9;
                Console.WriteLine($"  camera margin: s {pOff[0]:0.###} center ({(double)a4[3]:0.##},{(double)a4[4]:0.##}) expect 3.6 / (200,100) ok={offOk}");
                if (!offOk) fail++;
            }

            // PatchBuilder 首帧 / 次帧
            var pbType = asm.GetType("GHCE.Bridge.PatchBuilder");
            var pb = Activator.CreateInstance(pbType);
            var canvasDef = Activator.CreateInstance(asm.GetType("GHCE.Types.CanvasDef"));
            var cmdDef = Activator.CreateInstance(asm.GetType("GHCE.Types.CommandDef"));
            var cmdType = asm.GetType("GHCE.Types.CommandDef");
            cmdType.GetField("Id").SetValue(cmdDef, "c_test");
            cmdType.GetField("GeomId").SetValue(cmdDef, "g_test");
            cmdType.GetField("Path").SetValue(cmdDef, def);
            cmdType.GetField("Layer").SetValue(cmdDef, "main");

            // 显式图层定义（Layer 电池）：顺序 / 不透明度 / 混合模式必须原样进协议
            var layerType = asm.GetType("GHCE.Types.LayerDef");
            var layerDef = Activator.CreateInstance(layerType);
            layerType.GetField("Name").SetValue(layerDef, "main");
            layerType.GetField("Order").SetValue(layerDef, 42.0);
            layerType.GetField("Opacity").SetValue(layerDef, 0.5);
            layerType.GetField("Blend").SetValue(layerDef, "multiply");
            cmdType.GetField("LayerDef").SetValue(cmdDef, layerDef);
            var stroke = Activator.CreateInstance(asm.GetType("GHCE.Types.StrokeDef"));
            asm.GetType("GHCE.Types.StrokeDef").GetField("Dash").SetValue(stroke, new double[] { 4, 3 });
            cmdType.GetField("Stroke").SetValue(cmdDef, stroke);
            var fill = Activator.CreateInstance(asm.GetType("GHCE.Types.FillDef"));
            asm.GetType("GHCE.Types.FillDef").GetField("Color").SetValue(fill, "#112233");
            cmdType.GetField("Fill").SetValue(cmdDef, fill);

            // 给指令挂一条时间轨（v0.16）：帧序列由 GH 算一次，浏览器按 Duration/Loop/Ease 播
            var trackType = asm.GetType("GHCE.Types.TrackDef");
            var track = Activator.CreateInstance(trackType);
            // 两帧 identity 与绕 (100,100) 转 90° 的页面矩阵
            trackType.GetField("N").SetValue(track, 2);
            trackType.GetField("Frames").SetValue(track, new double[]
            {
                1, 0, 0, 1, 0, 0,
                0, 1, -1, 0, 200, 0,
            });
            trackType.GetField("Reveal").SetValue(track, new double[] { 0, 1 });
            trackType.GetField("Duration").SetValue(track, 2.5);
            trackType.GetField("Loop").SetValue(track, true);
            trackType.GetField("EaseX").SetValue(track, new double[] { 0, 0.5, 1 });
            trackType.GetField("EaseY").SetValue(track, new double[] { 0, 0.75, 1 });
            cmdType.GetField("Track").SetValue(cmdDef, track);

            // 2D 仿射矩阵（来自 GH 的 Transform 对象）。这里用已知值，验协议与运行时消费
            cmdType.GetField("Matrix").SetValue(cmdDef, new double[] { 0, -1, 1, 0, 10, -20 });
            // 变换进度（v0.11）：0.5 = 从原位长到一半，浏览器按 TRS 分解插值
            cmdType.GetField("Progress").SetValue(cmdDef, 0.5);
            var gooCmd = Activator.CreateInstance(asm.GetType("GHCE.Types.GHCE_Command"), cmdDef);

            var build = pbType.GetMethod("Build");
            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(gooCmd.GetType());
            var cmdList = (System.Collections.IList)Activator.CreateInstance(listType);
            cmdList.Add(gooCmd);

            // 阶段 2：原生 Curve → Path。
            // 注意：在 Rhino 进程外无法构造 Rhino 几何（rhcommon_c 是原生库），
            // 这条只能在 Rhino 里验，探针这里只确认"调用不会炸"。
            IGH_Goo crvPathGoo = null;
            try
            {
                var line = new Rhino.Geometry.LineCurve(
                    new Rhino.Geometry.Point3d(0, 0, 0), new Rhino.Geometry.Point3d(120, 60, 0));
                crvPathGoo = (IGH_Goo)Activator.CreateInstance(gooType);
                var castCurve = crvPathGoo.CastFrom(line);
                Console.WriteLine($"  cast Curve→Path: ok={castCurve} valid={crvPathGoo.IsValid} str={crvPathGoo}");
                if (!castCurve || !crvPathGoo.IsValid) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  SKIP Curve→Path（需要 Rhino 原生库）：" + Unwrap(ex).GetType().Name);
            }

            // 蓝图：用已有的 PathDef 造一个实体定义
            var entType = asm.GetType("GHCE.Types.EntityDef");
            var entDef = Activator.CreateInstance(entType);
            entType.GetField("Id").SetValue(entDef, "e_test");
            entType.GetField("Path").SetValue(entDef, def);
            entType.GetField("Count").SetValue(entDef, 50);
            var entGooCtor = asm.GetType("GHCE.Types.GHCE_Entity").GetConstructor(new[] { entType });
            var entGoo = (IGH_Goo)entGooCtor.Invoke(new[] { entDef });
            Console.WriteLine($"  entity: valid={entGoo.IsValid} str={entGoo}");

            var entListType = typeof(System.Collections.Generic.List<>).MakeGenericType(entGoo.GetType());
            var entList = (System.Collections.IList)Activator.CreateInstance(entListType);
            entList.Add(entGoo);

            // 事件触发（v0.12）：事件挂在**指令上**，Targets 指明作用于哪几条
            var evtType = asm.GetType("GHCE.Types.EventDef");
            var evtDef = Activator.CreateInstance(evtType);
            evtType.GetField("Id").SetValue(evtDef, "ev_test");
            evtType.GetField("Event").SetValue(evtDef, "click");
            evtType.GetField("Action").SetValue(evtDef, "toggle");
            evtType.GetField("Detail").SetValue(evtDef, "");
            evtType.GetField("Targets").SetValue(evtDef, new[] { "c_test" });
            var evtListType = typeof(System.Collections.Generic.List<>).MakeGenericType(evtType);
            var evtList = (System.Collections.IList)Activator.CreateInstance(evtListType);
            evtList.Add(evtDef);
            cmdType.GetField("Events").SetValue(cmdDef, evtList);

            var args = new object[] { canvasDef, cmdList, entList, null };
            var json = (string)build.Invoke(pb, args);
            Console.WriteLine($"  patch1: {args[3]}  len={(json?.Length ?? -1)}");
            if (json != null && json.IndexOf("entity.upsert", StringComparison.Ordinal) < 0)
            {
                fail++;
                Console.WriteLine("  FAIL: patch 里没有 entity.upsert");
            }
            // v0.16：时间轨不是一个独立的 op 流，它挂在 cmd 的值里（跟着指令一起增删）
            if (json != null)
            {
                bool trkOk = json.IndexOf("\"track\":{\"n\":2", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"duration\":2.5", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"loop\":true", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"reveal\":[0,1]", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"easeX\":[0,0.5,1]", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"frames\":\"", StringComparison.Ordinal) >= 0;
                Console.WriteLine($"  track in patch: {trkOk}");
                if (!trkOk)
                {
                    fail++;
                    Console.WriteLine("  FAIL: 时间轨没进协议（n/duration/loop/reveal/ease/frames）");
                }
            }
            if (json != null)
            {
                bool blendOk = json.IndexOf("\"blend\":\"multiply\"", StringComparison.Ordinal) >= 0;
                bool orderOk = json.IndexOf("\"order\":42", StringComparison.Ordinal) >= 0;
                bool lopOk = json.IndexOf("\"opacity\":0.5", StringComparison.Ordinal) >= 0;
                Console.WriteLine($"  layer in patch: order42={orderOk} multiply={blendOk} opacity0.5={lopOk}");
                if (!blendOk || !orderOk || !lopOk)
                {
                    fail++;
                    Console.WriteLine("  FAIL: 显式图层定义没进协议");
                }

                // v0.11：变换进度 / 事件触发（振荡器已随 v0.16 删除）
                bool progOk = json.IndexOf("\"progress\":0.5", StringComparison.Ordinal) >= 0;
                bool evtOk = json.IndexOf("event.upsert", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"targets\":[\"c_test\"]", StringComparison.Ordinal) >= 0;
                bool noOscOk = json.IndexOf("\"osc\"", StringComparison.Ordinal) < 0;
                Console.WriteLine($"  v0.16 协议: progress0.5={progOk} event+targets={evtOk} 无osc={noOscOk}");
                if (!progOk || !evtOk || !noOscOk)
                {
                    fail++;
                    Console.WriteLine("  FAIL: progress / event 没进协议，或已删除的 osc 还在");
                }
            }
            if (json != null)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-csharp-patch.json"), json);
                Console.WriteLine("  wrote: ghce-csharp-patch.json");
            }
            var args2 = new object[] { canvasDef, cmdList, entList, null };
            var json2 = (string)build.Invoke(pb, args2);
            Console.WriteLine($"  patch2 (expect null): {(json2 == null ? "null" : "NOT NULL")}  {args2[3]}");
            if (json2 != null) fail++;
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("FATAL: " + Unwrap(ex));
        }

        Console.WriteLine(fail == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({fail})");

        if (Environment.GetEnvironmentVariable("REFLECT") == "1") { ProbeIcons.Run(); return 0; }
        if (_serve)
        {
            ServeMode();
            return fail == 0 ? 0 : 1;
        }
        return fail == 0 ? 0 : 1;
    }

    static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
        return ex;
    }

    /// <summary>
    /// --serve：用插件自己的 BridgeServer 在 8799 上同时提供内嵌页面和 WS，
    /// 再按真实 PatchBuilder 持续推帧。用来验证"不装 node 也能跑"。
    /// </summary>
    static void ServeMode()
    {
        const int port = 8777;
        var err = GHCE.Bridge.BridgeServer.StartAuto(port);
        if (err != null)
        {
            Console.WriteLine("SERVE FAIL: " + err);
            return;
        }
        Console.WriteLine($"actual port: {GHCE.Bridge.BridgeServer.Port}  (preferred {port})");
        Console.WriteLine($"preview url: {GHCE.Bridge.BridgeServer.PreviewUrl}");

        var canvas = new GHCE.Types.CanvasDef { W = 1200, H = 800, Bg = "#0b0b0f" };
        var paths = new System.Collections.Generic.List<GHCE.Types.PathDef>();
        var cmds = new System.Collections.Generic.List<GHCE.Types.GHCE_Command>();

        for (int i = 0; i < 60; i++)
        {
            var p = new GHCE.Types.PathDef { Id = "g_s" + i, Closed = false, XY = new double[2 * 81] };
            paths.Add(p);
            cmds.Add(new GHCE.Types.GHCE_Command(new GHCE.Types.CommandDef
            {
                Id = "c_s" + i,
                GeomId = p.Id,
                Path = p,
                Layer = i % 10 == 0 ? "accent" : "main",
                Order = i,
                Stroke = new GHCE.Types.StrokeDef
                {
                    Width = 0.9,
                    Alpha = 0.65,
                    Color = i % 10 == 0 ? "#FD7AB4" : "#E8E6DF"
                },
                Fill = new GHCE.Types.FillDef()
            }));
        }

        var pb = new GHCE.Bridge.PatchBuilder();
        Console.WriteLine($"READY {GHCE.Bridge.BridgeServer.PreviewUrl}");
        Console.Out.Flush();

        for (int tick = 0; tick < 240; tick++)
        {
            double sec = tick * 0.5;
            for (int i = 0; i < paths.Count; i++)
            {
                var xy = paths[i].XY;
                for (int s = 0; s <= 80; s++)
                {
                    double u = s / 80.0;
                    xy[s * 2] = 60 + u * 1080;
                    xy[s * 2 + 1] = 400 + Math.Sin(u * Math.PI * 2 + sec + i * 0.12) * (110 + i * 2);
                }
            }

            var json = pb.Build(canvas, cmds, null, out var summary);
            GHCE.Bridge.BridgeServer.Broadcast(json);
            if (tick % 10 == 0)
                Console.WriteLine($"  tick {tick}: {summary}  clients={GHCE.Bridge.BridgeServer.ClientCount}");
            System.Threading.Thread.Sleep(500);
        }

        GHCE.Bridge.BridgeServer.Stop();
    }
}
