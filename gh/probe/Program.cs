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

    /// <summary>构建输出里叫 GHCE.dll，装到 Libraries 里的叫 GHCE.gha；两种都得能读。</summary>
    static string ArtifactPath() =>
        System.IO.File.Exists(BIN + @"\GHCE.dll") ? BIN + @"\GHCE.dll" : BIN + @"\GHCE.gha";

    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        string path = name switch
        {
            "RhinoCommon" => RH + @"\System\netcore\RhinoCommon.dll",
            "Grasshopper" => RH + @"\Plug-ins\Grasshopper\Grasshopper.dll",
            "GH_IO" => RH + @"\Plug-ins\Grasshopper\GH_IO.dll",
            "GHCE" => ArtifactPath(),
            _ => null
        };
        return path != null ? Assembly.LoadFrom(path) : null;
    }

    static bool _serve;
    static bool _signal;

    static int Main(string[] args)
    {
        _serve = args != null && args.Contains("--serve");
        _signal = args != null && args.Contains("--signal");
        _clock = args != null && args.Contains("--clock");
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        // 这条必须在 AssemblyResolve 之后：它要解析 RhinoCommon
        if (args != null && args.Contains("--text-api")) { TextApi(); return 0; }
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
        Console.WriteLine("  icon palette: dark chip + light stroke (fixed, panel-independent)");

        try
        {
            // 构建输出里叫 GHCE.dll，装到 Libraries 里的叫 GHCE.gha，两种都要能读
            var dllPath = ArtifactPath();
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
                    Console.WriteLine($"  OK  param {p.Name,-12} [{p.Category}/{p.SubCategory}] {(icon == null ? "NULL" : icon.Width + "x" + icon.Height)}");
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

            // 图标自反差的硬断言。
            //
            // 来由：v0.28 只统计"某个数据图标"的明暗像素占比就宣布通过，
            // 但另一只数据图标当时是"不透明黑底 + 深色字形"，统计口径根本照不到它，
            // 用户在 GH 里看到的是整组黑块。所以这里逐只图标量：底色取不透明像素的众数，
            // 再看有多少像素明显偏离底色 —— 偏离面积太小就是"字被埋了"。
            {
                double minCover = 1.0;
                int minDelta = 255;
                string worstCoverName = "-", worstDeltaName = "-";
                Console.WriteLine("  --- icon contrast（底色 / 字形覆盖率 / 最大亮差）---");
                foreach (var (name, bmp) in icons)
                {
                    if (bmp == null) continue;
                    var hist = new int[256];
                    int opaque = 0;
                    for (int y = 0; y < 24; y++)
                        for (int x = 0; x < 24; x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (c.A < 250) continue;
                            hist[(c.R * 299 + c.G * 587 + c.B * 114) / 1000]++;
                            opaque++;
                        }
                    if (opaque < 100) { fail++; Console.WriteLine($"  FAIL {name}: 不透明像素只有 {opaque}，图标没有自带底"); continue; }

                    int bg = 0;
                    for (int i = 1; i < 256; i++) if (hist[i] > hist[bg]) bg = i;

                    int marked = 0, total = 0, maxDev = 0;
                    for (int y = 0; y < 24; y++)
                        for (int x = 0; x < 24; x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (c.A < 160) continue;
                            total++;
                            int dev = Math.Abs((c.R * 299 + c.G * 587 + c.B * 114) / 1000 - bg);
                            if (dev > 60) marked++;
                            if (dev > maxDev) maxDev = dev;
                        }
                    double cover = total == 0 ? 0 : (double)marked / total;
                    Console.WriteLine($"  {name,-19} bg={bg,3} cover={cover * 100,5:F1}%  maxΔ={maxDev,3}");

                    if (cover < minCover) { minCover = cover; worstCoverName = name; }
                    if (maxDev < minDelta) { minDelta = maxDev; worstDeltaName = name; }
                    if (cover < 0.04) { fail++; Console.WriteLine($"  FAIL {name}: 字形覆盖 {cover * 100:F1}% < 4%"); }
                    if (maxDev < 80) { fail++; Console.WriteLine($"  FAIL {name}: 与底色反差 maxΔ={maxDev} < 80"); }
                }
                Console.WriteLine($"  最弱覆盖：{worstCoverName} {minCover * 100:F1}%（下限 4%）");
                Console.WriteLine($"  最弱反差：{worstDeltaName} maxΔ={minDelta}（下限 80）");
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
                    // 背景跟着主题走，否则对照图看不出"浅色主题下能不能看清"
                    bool lightSheet = Environment.GetEnvironmentVariable("GHCE_ICON_THEME") == "light";
                    g.Clear(lightSheet
                        ? System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF6)
                        : System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0F));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using (var font = new System.Drawing.Font("Consolas", 13f))
                    using (var brush = new System.Drawing.SolidBrush(lightSheet
                        ? System.Drawing.Color.FromArgb(0x2C, 0x2C, 0x36)
                        : System.Drawing.Color.FromArgb(0xE8, 0xE6, 0xDF)))
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

            // ---------- v0.31 Mark 数学层（纯数值）----------
            fail += TestMarks();

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

            // 给指令挂一条时间轨（v0.18）：帧序列由 GH 算一次，浏览器按全局时间轴播
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
            // 轨道只报自己的区间；时长与循环归 Timeline
            trackType.GetField("Start").SetValue(track, 1.0);
            trackType.GetField("End").SetValue(track, 3.5);
            trackType.GetField("Hidden").SetValue(track, false);
            trackType.GetField("EaseX").SetValue(track, new double[] { 0, 0.5, 1 });
            trackType.GetField("EaseY").SetValue(track, new double[] { 0, 0.75, 1 });
            cmdType.GetField("Track").SetValue(cmdDef, track);

            var tlType = asm.GetType("GHCE.Types.TimelineDef");
            var tlDef = Activator.CreateInstance(tlType);
            tlType.GetField("Duration").SetValue(tlDef, 6.0);
            tlType.GetField("Loop").SetValue(tlDef, true);
            cmdType.GetField("Timeline").SetValue(cmdDef, tlDef);

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

            // v0.30：表达式绑定 + 事件写变量
            // 目的不是"能跑"，而是确认这三样真的进了 patch：
            //   cmd.bind（属性 → 表达式树）、event.writes（事件 → 变量）、var.upsert（变量声明）
            var exprType = asm.GetType("GHCE.Types.RuntimeExprDef");
            var varNode = exprType.GetMethod("Var").Invoke(null, new object[] { "v_w", 0.0 });
            var constSix = exprType.GetMethod("Const").Invoke(null, new object[] { 6.0 });
            var widthExpr = exprType.GetMethod("Bin").Invoke(null, new object[] { "mul", varNode, constSix });

            var bindDictType = typeof(System.Collections.Generic.Dictionary<,>)
                .MakeGenericType(typeof(string), exprType);
            var bindDict = (System.Collections.IDictionary)Activator.CreateInstance(bindDictType);
            bindDict["width"] = widthExpr;
            cmdType.GetField("Binds").SetValue(cmdDef, bindDict);

            var writeType = asm.GetType("GHCE.Types.WriteDef");
            var write = Activator.CreateInstance(writeType);
            writeType.GetField("Var").SetValue(write, "v_w");
            writeType.GetField("Op").SetValue(write, "toggle");
            writeType.GetField("Value").SetValue(write, 1.0);
            var writeListType = typeof(System.Collections.Generic.List<>).MakeGenericType(writeType);
            var writeList = (System.Collections.IList)Activator.CreateInstance(writeListType);
            writeList.Add(write);
            evtType.GetField("Writes").SetValue(evtDef, writeList);
            evtType.GetField("Action").SetValue(evtDef, null);

            var args = new object[] { canvasDef, cmdList, entList, null, null };
            var json = (string)build.Invoke(pb, args);
            Console.WriteLine($"  patch1: {args[3]}  len={(json?.Length ?? -1)}");
            if (json != null && json.IndexOf("entity.upsert", StringComparison.Ordinal) < 0)
            {
                fail++;
                Console.WriteLine("  FAIL: patch 里没有 entity.upsert");
            }
            if (json != null)
            {
                bool hasBind = json.IndexOf("\"bind\"", StringComparison.Ordinal) >= 0;
                bool hasWrites = json.IndexOf("\"writes\"", StringComparison.Ordinal) >= 0;
                bool hasVar = json.IndexOf("var.upsert", StringComparison.Ordinal) >= 0;
                Console.WriteLine($"  v0.30 绑定: bind={hasBind} writes={hasWrites} var={hasVar}");
                if (!hasBind || !hasWrites || !hasVar) fail++;
            }
            // v0.18：轨道挂在 cmd 的值里（跟着指令增删），只报自己的区间；
            // 时长与循环是文档级的 timeline.set，独立一条 op
            if (json != null)
            {
                bool trkOk = json.IndexOf("\"track\":{\"n\":2", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"start\":1", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"end\":3.5", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"hidden\":false", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"reveal\":[0,1]", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"easeX\":[0,0.5,1]", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"frames\":\"", StringComparison.Ordinal) >= 0
                             && json.IndexOf("\"duration\":2.5", StringComparison.Ordinal) < 0;
                Console.WriteLine($"  track in patch: {trkOk}");
                if (!trkOk)
                {
                    fail++;
                    Console.WriteLine("  FAIL: 轨道没进协议（start/end/hidden/reveal/ease/frames），或旧 duration 仍在");
                }

                bool tlOk = json.IndexOf("\"op\":\"timeline.set\"", StringComparison.Ordinal) >= 0
                            && json.IndexOf("\"duration\":6", StringComparison.Ordinal) >= 0;
                Console.WriteLine($"  timeline.set in patch: {tlOk}");
                if (!tlOk)
                {
                    fail++;
                    Console.WriteLine("  FAIL: 全局时间轴没进协议（timeline.set）");
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
            var args2 = new object[] { canvasDef, cmdList, entList, null, null };
            var json2 = (string)build.Invoke(pb, args2);
            Console.WriteLine($"  patch2 (expect null): {(json2 == null ? "null" : "NOT NULL")}  {args2[3]}");
            if (json2 != null) fail++;

            // ---------- v0.31 Mark → patch（体积与去重）----------
            fail += TestMarkPatch();

            // ---------- v0.32 状态程序 → patch ----------
            fail += TestEntityProgram();

            // ---------- v0.33 文字 / 图片 / 裁剪 → patch ----------
            fail += TestComposition();
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("FATAL: " + Unwrap(ex));
        }

        // ---- RuntimeStat 解析：喂运行时真实形状的 stat，读回信号值 ----
        // 这一段不依赖网络，能单独证明"回传解析"这一环是否成立。
        const string statJson =
            "{\"v\":\"0.1\",\"t\":\"stat\",\"ptr\":[10,20],\"inside\":true,\"down\":false,\"clicks\":3," +
            "\"entities\":0,\"alive\":0,\"rev\":5,\"fps\":60,\"drawMs\":1.25,\"stepMs\":0.4,\"time\":2.5," +
            "\"keys\":\"w\",\"signals\":{\"a1b2c3d4\":0.4375}}";
        GHCE.Bridge.RuntimeStat.Update(statJson);
        bool hasSig = GHCE.Bridge.RuntimeStat.HasSignal("a1b2c3d4");
        double sigVal = GHCE.Bridge.RuntimeStat.SignalValue("a1b2c3d4");
        Console.WriteLine($"  stat parse: has={hasSig} value={sigVal:0.####} clicks={GHCE.Bridge.RuntimeStat.Clicks} rev={GHCE.Bridge.RuntimeStat.Rev}");
        if (!hasSig || Math.Abs(sigVal - 0.4375) > 1e-9)
        {
            fail++;
            Console.WriteLine("  FAIL: stat 里的 signals 没被解析出来");
        }

        // ---- 三维：矩阵约定转换（纯 double，不需要 Rhino 原生库）----
        // Rhino 是行向量约定（p' = p*M），Three.js 是列向量约定（p' = M*p），差一个转置。
        // 这里用"绕 Z 转 90° 再平移"当已知答案，两边必须把同一个点送到同一个位置。
        {
            var rowMajor = GHCE.Types.Transform3DMath.RotationZ90ThenTranslate(10, 20, 5);
            var col = GHCE.Types.Transform3DMath.ToThreeOrder(rowMajor);
            var viaRow = GHCE.Types.Transform3DMath.ApplyRowVector(rowMajor, 1, 0, 0);
            var viaCol = GHCE.Types.Transform3DMath.ApplyColumnVector(col, 1, 0, 0);
            bool same = Math.Abs(viaRow[0] - viaCol[0]) < 1e-12
                        && Math.Abs(viaRow[1] - viaCol[1]) < 1e-12
                        && Math.Abs(viaRow[2] - viaCol[2]) < 1e-12;
            // (1,0,0) 绕 Z 转 90° → (0,1,0)，再平移 (10,20,5) → (10,21,5)
            bool expected = Math.Abs(viaRow[0] - 10) < 1e-12
                            && Math.Abs(viaRow[1] - 21) < 1e-12
                            && Math.Abs(viaRow[2] - 5) < 1e-12;
            Console.WriteLine($"  matrix3d: row=({viaRow[0]:0.##},{viaRow[1]:0.##},{viaRow[2]:0.##}) col=({viaCol[0]:0.##},{viaCol[1]:0.##},{viaCol[2]:0.##}) transpose-ok={same} expected-ok={expected}");
            if (!same || !expected)
            {
                fail++;
                Console.WriteLine("  FAIL: 三维矩阵的转置换算不对");
            }
        }

        // ---- 三维：patch 序列化 ----
        {
            var mesh = new GHCE.Types.MeshDef
            {
                Id = "m_probe",
                Positions = new double[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                Indices = new int[] { 0, 1, 2 },
                Xform = new GHCE.Types.Transform3DDef
                {
                    Matrix = GHCE.Types.Transform3DMath.ToThreeOrder(
                        GHCE.Types.Transform3DMath.RotationZ90ThenTranslate(1, 2, 3)),
                },
            };
            var cmd3d = new GHCE.Types.Cmd3DDef
            {
                Id = "c3_probe",
                MeshId = mesh.Id,
                Mesh = mesh,
                Material = new GHCE.Types.Material3DDef { Mode = 3, Color = "#07a954", Size = 4, Glow = 0.5 },
                // 静态矩阵：这一段是给回放测试用的（矩阵平移必须落在 (1,2,3)）
                Xform = mesh.Xform,
            };
            var scene = new GHCE.Types.Scene3DDef { Background = "#101010", Lights = true };
            scene.Cmds.Add(cmd3d);

            var pb3 = new GHCE.Bridge.PatchBuilder();
            var canvas3 = new GHCE.Types.CanvasDef { W = 800, H = 600 };
            var j3 = pb3.Build(canvas3, new System.Collections.Generic.List<GHCE.Types.GHCE_Command>(), null, scene, out var sum3);

            bool hasMesh = j3 != null && j3.Contains("\"op\":\"mesh.upsert\"");
            bool hasCmd = j3 != null && j3.Contains("\"op\":\"cmd3d.upsert\"");
            bool hasScene = j3 != null && j3.Contains("\"op\":\"scene3d.set\"");
            bool hasMatrix = j3 != null && j3.Contains("\"matrix\":\"");
            bool hasMode = j3 != null && j3.Contains("\"mode\":3");
            bool hasKind = j3 != null && j3.Contains("\"kind\":\"mesh\"");
            // v0.30.1：帧序列自带的时长必须进协议 —— 否则三维只能按写死的 1.5s 循环。
            // 单独算一份带帧序列的 patch：上面那份保持静态矩阵，回放测试还要用。
            var savedX = cmd3d.Xform;
            cmd3d.Xform = new GHCE.Types.Transform3DDef
            {
                N = 2,
                Duration = 2.5,
                Frames = new double[]
                {
                    1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1,
                    1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  10, 0, 0, 1,
                },
            };
            var jDur = new GHCE.Bridge.PatchBuilder()
                .Build(canvas3, new System.Collections.Generic.List<GHCE.Types.GHCE_Command>(), null, scene, out _);
            cmd3d.Xform = savedX;

            bool hasDuration = jDur != null && jDur.Contains("\"duration\":2.5");
            Console.WriteLine($"  patch3d: mesh={hasMesh} cmd={hasCmd} scene={hasScene} matrix={hasMatrix} mode={hasMode} kind={hasKind} dur={hasDuration}  ({sum3})");
            if (j3 != null)
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-csharp-patch3d.json"), j3);
                Console.WriteLine("  wrote: ghce-csharp-patch3d.json");
            }

            if (!hasMesh || !hasCmd || !hasScene || !hasMatrix || !hasMode || !hasKind || !hasDuration)
            {
                fail++;
                Console.WriteLine("  FAIL: 三维 patch 缺少字段");
                Console.WriteLine("    " + (j3 == null ? "null" : j3.Substring(0, Math.Min(400, j3.Length))));
            }

            // 同样的输入再算一次必须为 null —— "无变化不发包"在三维上也要成立
            var j3b = pb3.Build(canvas3, new System.Collections.Generic.List<GHCE.Types.GHCE_Command>(), null, scene, out _);
            Console.WriteLine($"  patch3d idle (expect null): {(j3b == null ? "null" : "NOT NULL")}");
            if (j3b != null) fail++;
        }

        Console.WriteLine(fail == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({fail})");

        if (Environment.GetEnvironmentVariable("REFLECT") == "1") { ProbeIcons.Run(); return 0; }
        if (_signal)
        {
            SignalMode();
            return 0;
        }
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
    /// --signal：只广播 signal 定义，用来抓包验证 C# 发送侧的载荷格式。
    /// 运行时按这个格式解析，格式错一个字段整条链路就静默失效。
    /// </summary>
    static void SignalMode()
    {
        const int port = 8788;
        var err = GHCE.Bridge.BridgeServer.StartAuto(port);
        if (err != null) { Console.WriteLine("SIGNAL FAIL: " + err); return; }
        Console.WriteLine($"actual port: {GHCE.Bridge.BridgeServer.Port}");

        var def = new GHCE.Types.SignalDef
        {
            Id = "a1b2c3d4", Kind = "noise1", Seed = 7, Rate = 0.6, Args = new[] { 0.0 }
        };

        Console.WriteLine("READY");
        Console.Out.Flush();

        for (int i = 0; i < 200; i++)
        {
            GHCE.Bridge.BridgeServer.PublishSignal(def);
            if (i % 5 == 0)
                Console.WriteLine($"  pub {i} clients={GHCE.Bridge.BridgeServer.ClientCount}");
            System.Threading.Thread.Sleep(200);
        }
        GHCE.Bridge.BridgeServer.Stop();
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

            var json = pb.Build(canvas, cmds, null, null, out var summary);
            GHCE.Bridge.BridgeServer.Broadcast(json);
            if (tick % 10 == 0)
                Console.WriteLine($"  tick {tick}: {summary}  clients={GHCE.Bridge.BridgeServer.ClientCount}");
            System.Threading.Thread.Sleep(500);
        }

        GHCE.Bridge.BridgeServer.Stop();
    }

    // ================= v0.31 Mark =================
    // 三只 Mark 电池都只是端口包装，算法全在 MarkMath 里。所以这里绕开 GH 的
    // IGH_DataAccess 直接断言数值 —— "标记放对了没有" 不该靠肉眼开 GH 看。

    static int TestMarks()
    {
        int fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {name,-26} ok={ok}  {detail}");
            if (!ok) fail++;
        }

        // --- 印章形状：大小按"最大外接范围"定义，五个形状并排看一样大 ---
        var dot = GHCE.Components.MarkMath.Stamp(1, 20, 1);
        var dash = GHCE.Components.MarkMath.Stamp(2, 20, 2);
        var square = GHCE.Components.MarkMath.Stamp(3, 20, 1);

        Check("stamp dot", dot.Length == 48 && BBox(dot).maxX <= 10.0001 && BBox(dot).minX >= -10.0001,
            $"{dot.Length / 2} pts, x [{BBox(dot).minX:0.##},{BBox(dot).maxX:0.##}]");
        Check("stamp dash aspect", dash.Length == 4 && Math.Abs(dash[0] + 20) < 1e-9 && Math.Abs(dash[2] - 20) < 1e-9,
            $"({dash[0]:0.##},{dash[1]:0.##}) → ({dash[2]:0.##},{dash[3]:0.##})");
        Check("stamp square closed", square.Length == 10 && Math.Abs(square[0] - square[8]) < 1e-9,
            $"{square.Length / 2} pts");

        // --- 自定义印章归一化：100×100 的方块缩到 size=20，且居中 ---
        var norm = GHCE.Components.MarkMath.Normalize(
            new double[] { 0, 0, 100, 0, 100, 100, 0, 100, 0, 0 }, 20, 1, 0);
        var nb = BBox(norm);
        Check("normalize 100→20", Math.Abs(nb.minX + 10) < 1e-6 && Math.Abs(nb.maxY - 10) < 1e-6,
            $"x [{nb.minX:0.##},{nb.maxX:0.##}] y [{nb.minY:0.##},{nb.maxY:0.##}]");

        // --- 沿路径采样：600px 直线 / 10px 间距 = 61 个点（含两端） ---
        var line = new GHCE.Types.PathDef { Id = "g_line", XY = new double[] { 0, 0, 600, 0 } };
        var s1 = GHCE.Components.MarkMath.Sample(line, 10, GHCE.Components.MarkMath.MaxMarks);
        Check("sample open path", s1.Count == 61
            && Math.Abs(s1[0].U) < 1e-9 && Math.Abs(s1[60].U - 1) < 1e-9
            && Math.Abs(s1[30].X - 300) < 1e-6 && Math.Abs(s1[30].Angle) < 1e-9,
            $"{s1.Count} marks, mid x={s1[30].X:0.##} u={s1[30].U:0.##}");

        // --- 闭合路径：100×100 方框周长 400 / 25 = 16 个，首点不重复 ---
        var box = new GHCE.Types.PathDef
        {
            Id = "g_box", Closed = true,
            XY = new double[] { 0, 0, 100, 0, 100, 100, 0, 100, 0, 0 },
        };
        var s2 = GHCE.Components.MarkMath.Sample(box, 25, GHCE.Components.MarkMath.MaxMarks);
        bool wrapOk = s2.Count == 16
            && Math.Abs(s2[0].X) < 1e-6 && Math.Abs(s2[0].Y) < 1e-6
            && Math.Abs(s2[4].X - 100) < 1e-6    // 第 5 个点落在右上角
            && Math.Abs(s2[15].X) < 1e-6 && Math.Abs(s2[15].Y - 25) < 1e-6;  // 末点绕回左边
        Check("sample closed path", wrapOk, $"{s2.Count} marks, p4=({s2[4].X:0.##},{s2[4].Y:0.##}) p15=({s2[15].X:0.##},{s2[15].Y:0.##})");

        // --- 放置矩阵：转 90° 后 (1,0) 必须落到 (100,51) ---
        var m = GHCE.Components.MarkMath.Placement(100, 50, Math.PI / 2);
        var p1 = Apply(m, 1, 0);
        Check("placement rotate+move", Math.Abs(p1[0] - 100) < 1e-9 && Math.Abs(p1[1] - 51) < 1e-9,
            $"({p1[0]:0.###},{p1[1]:0.###}) expect (100,51)");

        // --- 旋转线性部分：位置不能动 ---
        var m2 = GHCE.Components.MarkMath.Placement(100, 50, Math.PI / 2);
        GHCE.Components.MarkMath.RotateLinear(m2, -Math.PI / 2);
        var p2 = Apply(m2, 1, 0);
        Check("rotate keeps position", Math.Abs(m2[4] - 100) < 1e-9 && Math.Abs(m2[5] - 50) < 1e-9
            && Math.Abs(p2[0] - 101) < 1e-9 && Math.Abs(p2[1] - 50) < 1e-9,
            $"({p2[0]:0.###},{p2[1]:0.###}) origin ({m2[4]:0.##},{m2[5]:0.##})");

        // --- 缩放线性部分：位置同样不能动 ---
        var m3 = GHCE.Components.MarkMath.Placement(30, 40, 0);
        GHCE.Components.MarkMath.ScaleLinear(m3, 3);
        Check("scale keeps position", Math.Abs(m3[4] - 30) < 1e-9 && Math.Abs(m3[5] - 40) < 1e-9
            && Math.Abs(Apply(m3, 1, 0)[0] - 33) < 1e-9, $"x(1)→{Apply(m3, 1, 0)[0]:0.##}");

        // --- 映射与伽马 ---
        double mid = GHCE.Components.MarkMath.Map(0.5, 0, 1, 1, 5, 1);
        double bias = GHCE.Components.MarkMath.Map(0.5, 0, 1, 1, 5, 2);
        double clamp = GHCE.Components.MarkMath.Map(-3, 0, 1, 1, 5, 1);
        Check("map + bias", Math.Abs(mid - 3) < 1e-9 && Math.Abs(bias - 2) < 1e-9 && Math.Abs(clamp - 1) < 1e-9,
            $"mid={mid:0.##} bias2={bias:0.##} clamp={clamp:0.##}");

        // --- 属性写入：尺寸改矩阵、不透明度乘算、线宽乘算 ---
        var cmd = new GHCE.Types.CommandDef
        {
            Matrix = GHCE.Components.MarkMath.Placement(10, 20, 0),
            Opacity = 1,
            Stroke = new GHCE.Types.StrokeDef { Width = 2 },
        };
        GHCE.Components.MarkMath.Apply(cmd, 1, 2.5);
        GHCE.Components.MarkMath.Apply(cmd, 3, 0.4);
        GHCE.Components.MarkMath.Apply(cmd, 4, 3);
        bool applyOk = Math.Abs(cmd.Matrix[0] - 2.5) < 1e-9 && Math.Abs(cmd.Matrix[4] - 10) < 1e-9
                       && Math.Abs(cmd.Opacity - 0.4) < 1e-9 && Math.Abs(cmd.Stroke.Width - 6) < 1e-9;
        Check("apply properties", applyOk,
            $"scale={cmd.Matrix[0]:0.##} pos={cmd.Matrix[4]:0.##} opacity={cmd.Opacity:0.##} width={cmd.Stroke.Width:0.##}");

        return fail;
    }

    /// <summary>Mark → patch：几何只发一次，指令一条一只，体积有上限。</summary>
    static int TestMarkPatch()
    {
        int fail = 0;
        var brush = new GHCE.Types.PathDef
        {
            Id = "g_brush_probe",
            Closed = true,
            XY = GHCE.Components.MarkMath.Stamp(1, 8, 1),
        };
        var src = new GHCE.Types.PathDef { Id = "g_probe_line", XY = new double[] { 100, 100, 700, 100 } };
        var samples = GHCE.Components.MarkMath.Sample(src, 6, GHCE.Components.MarkMath.MaxMarks);

        var cmds = new System.Collections.Generic.List<GHCE.Types.GHCE_Command>(samples.Count);
        for (int i = 0; i < samples.Count; i++)
        {
            var sm = samples[i];
            cmds.Add(new GHCE.Types.GHCE_Command(new GHCE.Types.CommandDef
            {
                Id = GHCE.Bridge.PatchBuilder.StableId("c_mk_", $"probe|{src.Id}|{i}"),
                GeomId = brush.Id,
                Path = brush,
                Layer = "main",
                Order = i,
                Matrix = GHCE.Components.MarkMath.Placement(sm.X, sm.Y, sm.Angle),
                MarkU = sm.U,
            }));
        }

        var canvas = new GHCE.Types.CanvasDef { W = 1440, H = 900, Bg = "#000000" };
        var json = new GHCE.Bridge.PatchBuilder().Build(canvas, cmds, null, null, out var summary);

        int geoms = Count(json, "geom.upsert");
        int ups = Count(json, "cmd.upsert");
        bool dedupe = geoms == 1;
        bool oneEach = ups == samples.Count;
        // 每只标记约 300–500 字节；500 只标记的整帧在 200KB 上下是可接受的，
        // 超过 400KB 就说明有人在每个标记上又塞了一份几何。
        bool sizeOk = json.Length < 400_000;

        Console.WriteLine($"  mark patch: {samples.Count} marks · geom.upsert={geoms} · cmd.upsert={ups} · {json.Length} bytes  {summary}");
        if (!dedupe) { fail++; Console.WriteLine("  FAIL: 印章几何没有去重（每个标记都发了一份）"); }
        if (!oneEach) { fail++; Console.WriteLine("  FAIL: 标记数量与 cmd.upsert 数量不一致"); }
        if (!sizeOk) { fail++; Console.WriteLine("  FAIL: 整帧体积超过 400KB"); }

        // 矩阵真的进了协议：第一个标记的平移必须是路径起点
        bool firstAtStart = json.Replace(" ", "").IndexOf("\"matrix\":[1,0,0,1,100,100]", StringComparison.Ordinal) >= 0;
        Console.WriteLine($"  mark matrix at start: {firstAtStart}");
        if (!firstAtStart) fail++;

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-mark-patch.json"), json);
        return fail;
    }

    static int Count(string s, string needle)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static double[] Apply(double[] m, double x, double y) =>
        new[] { m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5] };

    static (double minX, double minY, double maxX, double maxY) BBox(double[] xy)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < xy.Length; i += 2)
        {
            if (xy[i] < minX) minX = xy[i];
            if (xy[i] > maxX) maxX = xy[i];
            if (xy[i + 1] < minY) minY = xy[i + 1];
            if (xy[i + 1] > maxY) maxY = xy[i + 1];
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// 状态程序进协议：steps / spawn / destroy 三块都要在，
    /// `state` 与 `dt` 两种表达式节点也要能被序列化 —— 缺一样，运行时那边就是"程序到了但不执行"。
    /// </summary>
    static int TestEntityProgram()
    {
        int fail = 0;
        var path = new GHCE.Types.PathDef { Id = "g_prog", XY = new double[] { 0, 0, 100, 100 } };
        var def = new GHCE.Types.EntityDef { Id = "e_prog", Path = path, Count = 4, Speed = 0 };

        var prog = new GHCE.Types.EntityProgramDef();
        prog.Steps.Add(new GHCE.Types.EntityStepDef
        {
            Slot = "vx",
            Op = "add",
            Expr = GHCE.Types.RuntimeExprDef.Bin("mul",
                GHCE.Types.RuntimeExprDef.State("x"),
                GHCE.Types.RuntimeExprDef.DeltaTime()),
        });
        prog.SpawnWhen = GHCE.Types.RuntimeExprDef.Bin("gt",
            GHCE.Types.RuntimeExprDef.State("age"), GHCE.Types.RuntimeExprDef.Const(1));
        prog.SpawnCount = 2;
        prog.MaxGen = 2;
        prog.DestroyWhen = GHCE.Types.RuntimeExprDef.Bin("gt",
            GHCE.Types.RuntimeExprDef.State("age"), GHCE.Types.RuntimeExprDef.Const(4));
        prog.Keep = 8;
        def.Program = prog;

        var keyBefore = prog.Key();
        prog.Steps[0].Slot = "vy";
        var keyAfter = prog.Key();
        Console.WriteLine($"  program key changes with slot: {keyBefore != keyAfter}");
        if (keyBefore == keyAfter) fail++;

        // 深拷贝：改副本不能影响原对象（管道电池靠这个不互相污染）
        var copy = def.Duplicate();
        copy.Program.Steps[0].Slot = "aux";
        bool deep = def.Program.Steps[0].Slot == "vy" && copy.Program.Steps[0].Slot == "aux";
        Console.WriteLine($"  program deep copy: {deep}");
        if (!deep) fail++;

        var canvas = new GHCE.Types.CanvasDef { W = 1440, H = 900, Bg = "#000000" };
        var list = new System.Collections.Generic.List<GHCE.Types.GHCE_Entity>
        {
            new GHCE.Types.GHCE_Entity(def),
        };
        var json = new GHCE.Bridge.PatchBuilder().Build(
            canvas, new System.Collections.Generic.List<GHCE.Types.GHCE_Command>(), list, null, out var summary);
        var flat = json == null ? "" : json.Replace(" ", "");

        bool hasProgram = flat.Contains("\"program\"");
        bool hasStep = flat.Contains("\"slot\":\"vy\"") && flat.Contains("\"op\":\"add\"");
        bool hasState = flat.Contains("\"o\":\"state\"") && flat.Contains("\"n\":\"x\"");
        bool hasDt = flat.Contains("\"o\":\"dt\"");
        bool hasSpawn = flat.Contains("\"spawn\"") && flat.Contains("\"maxGen\":2") && flat.Contains("\"count\":2");
        bool hasDestroy = flat.Contains("\"destroy\"") && flat.Contains("\"keep\":8");

        Console.WriteLine($"  program in patch: program={hasProgram} step={hasStep} state={hasState} dt={hasDt} spawn={hasSpawn} destroy={hasDestroy}  ({summary})");
        if (!hasProgram || !hasStep || !hasState || !hasDt || !hasSpawn || !hasDestroy)
        {
            fail++;
            Console.WriteLine("  FAIL: 状态程序没有完整进入 patch");
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-entity-program.json"), json);
        return fail;
    }

    /// <summary>--text-api：打一份 TextEntity 的可用接口，用来定 Text 电池怎么转曲线。</summary>
    static void TextApi()
    {
        var t = typeof(Rhino.Geometry.Curve).Assembly.GetType("Rhino.Geometry.TextEntity");
        if (t == null) { Console.WriteLine("TextEntity not found"); return; }
        Console.WriteLine("type: " + t.FullName);

        Console.WriteLine("static:");
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (m.Name.IndexOf("Create", StringComparison.Ordinal) < 0 &&
                m.Name.IndexOf("Curve", StringComparison.Ordinal) < 0 &&
                m.Name.IndexOf("Explode", StringComparison.Ordinal) < 0) continue;
            Console.WriteLine("  " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
        }

        Console.WriteLine("instance:");
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (m.Name.IndexOf("Curve", StringComparison.Ordinal) < 0 &&
                m.Name.IndexOf("Explode", StringComparison.Ordinal) < 0 &&
                m.Name.IndexOf("Duplicate", StringComparison.Ordinal) < 0) continue;
            Console.WriteLine("  " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
        }

        // DimensionStyle：文字的高度与字体从哪里设
        var ds = typeof(Rhino.Geometry.Curve).Assembly.GetType("Rhino.DocObjects.DimensionStyle")
                 ?? typeof(Rhino.Geometry.Curve).Assembly.GetType("Rhino.Geometry.DimensionStyle");
        if (ds != null)
        {
            Console.WriteLine("DimensionStyle ctors:");
            foreach (var c in ds.GetConstructors())
                Console.WriteLine("  .ctor(" + string.Join(", ", c.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
            Console.WriteLine("DimensionStyle members:");
            foreach (var m in ds.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (m.Name.IndexOf("Font", StringComparison.Ordinal) < 0 &&
                    m.Name.IndexOf("Text", StringComparison.Ordinal) < 0 &&
                    m.Name.IndexOf("Height", StringComparison.Ordinal) < 0) continue;
                if (m.MemberType != System.Reflection.MemberTypes.Property) continue;
                var pi = (System.Reflection.PropertyInfo)m;
                Console.WriteLine($"  {pi.PropertyType.Name} {pi.Name} canWrite={pi.CanWrite}");
            }
        }

        // Rhino.DocObjects.Font：怎么从字体名构造
        var fnt = typeof(Rhino.Geometry.Curve).Assembly.GetType("Rhino.DocObjects.Font");
        if (fnt != null)
        {
            Console.WriteLine("Font ctors:");
            foreach (var c in fnt.GetConstructors())
                Console.WriteLine("  .ctor(" + string.Join(", ", c.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
        }
    }

    // ================= v0.33 文字 / 图片 / 裁剪 =================

    const string TINY_PNG =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAaUlEQVR42u3XoQ0AIAxFwU5SzYiMzQgsUFQNhEuQFZx6+bHmKF9m/W67DwAAAACAFuCVj57uAQAAAAB6ACUGAAAAsAeUGAAAAMAeUGIAAAAAe0CJAQAAAOwBJQYAAACwB5QYAAAA4BvABjgwoOIgZTMsAAAAAElFTkSuQmCC";

    /// <summary>
    /// 图片与裁剪进协议。两件事最容易在某处断掉：
    ///   1. 图片几何必须自己发一次 geom.upsert（它不属于任何 Path）；
    ///   2. 裁剪轮廓也必须单独发 —— 否则运行时拿不到模具，clip 变成静默无效。
    /// </summary>
    static int TestComposition()
    {
        int fail = 0;

        // --- 四只电池在不在、端口数是否守规矩 ---
        var expected = new (string type, string name, int ins, int outs)[]
        {
            ("GHCE.Components.TextComponent", "Text", 6, 2),
            ("GHCE.Components.ImageComponent", "Image", 2, 2),
            ("GHCE.Components.DrawImageComponent", "Draw Image", 6, 2),
            ("GHCE.Components.ClipComponent", "Clip", 3, 2),
        };
        foreach (var e in expected)
        {
            var t = typeof(GHCE.Components.TextComponent).Assembly.GetType(e.type);
            bool ok = t != null;
            int ins = 0, outs = 0;
            string shown = "missing";
            if (ok)
            {
                var inst = (Grasshopper.Kernel.GH_Component)Activator.CreateInstance(t);
                ins = inst.Params.Input.Count;
                outs = inst.Params.Output.Count;
                shown = inst.Name;
                ok = shown == e.name && ins == e.ins && outs == e.outs && ins <= 6;
            }
            Console.WriteLine($"  battery {e.name,-12} ok={ok}  in={ins} out={outs}");
            if (!ok) fail++;
        }

        // --- patch：图片几何 + 裁剪 ---
        var img = new GHCE.Types.ImageDef
        {
            Id = "g_probe_img",
            Src = "data:image/png;base64," + TINY_PNG,
            W = 64,
            H = 64,
        };
        var clipPath = new GHCE.Types.PathDef
        {
            Id = "g_probe_clip",
            Closed = true,
            XY = new double[] { 0, 0, 100, 0, 100, 100, 0, 100, 0, 0 },
        };
        var def = new GHCE.Types.CommandDef
        {
            Id = "c_probe_img",
            GeomId = img.Id,
            Image = img,
            Layer = "main",
            Order = 0,
            Matrix = new[] { 200.0, 0, 0, 200, 50, 60 },
            Clip = new GHCE.Types.ClipDef { GeomId = clipPath.Id, Invert = true, Path = clipPath },
        };

        var cmdList = new System.Collections.Generic.List<GHCE.Types.GHCE_Command>
        {
            new GHCE.Types.GHCE_Command(def),
        };
        var canvas = new GHCE.Types.CanvasDef { W = 1440, H = 900, Bg = "#000000" };
        var json = new GHCE.Bridge.PatchBuilder().Build(canvas, cmdList, null, null, out var summary);
        var flat = json == null ? "" : json.Replace(" ", "");

        bool hasImageGeom = flat.Contains("\"kind\":\"image\"") && flat.Contains("\"id\":\"g_probe_img\"");
        bool hasClipGeom = flat.Contains("\"id\":\"g_probe_clip\"");
        bool hasClipField = flat.Contains("\"clip\":{\"geom\":\"g_probe_clip\",\"invert\":true}");
        bool hasMatrix = flat.Contains("\"matrix\":[200,0,0,200,50,60]");
        bool oneEach = Count(json, "geom.upsert") == 2;

        Console.WriteLine($"  composition patch: image={hasImageGeom} clipGeom={hasClipGeom} clipField={hasClipField} matrix={hasMatrix} geoms={Count(json, "geom.upsert")}  ({summary})");
        if (!hasImageGeom) { fail++; Console.WriteLine("  FAIL: 图片几何没有单独发 geom.upsert"); }
        if (!hasClipGeom) { fail++; Console.WriteLine("  FAIL: 裁剪轮廓没有进 patch（运行时拿不到模具）"); }
        if (!hasClipField) { fail++; Console.WriteLine("  FAIL: cmd 上没有 clip 字段"); }
        if (!hasMatrix) { fail++; Console.WriteLine("  FAIL: 图片矩阵没进协议"); }
        if (!oneEach) { fail++; Console.WriteLine("  FAIL: 几何条数不对（应为图片 + 裁剪轮廓各一条）"); }

        // 同一张图被两条指令引用时只能发一次
        var second = def.Duplicate();
        second.Id = "c_probe_img2";
        second.Clip = null;
        cmdList.Add(new GHCE.Types.GHCE_Command(second));
        var json2 = new GHCE.Bridge.PatchBuilder()
            .Build(canvas, cmdList, null, null, out _)
            .Replace(" ", "");
        int imgGeoms = Count(json2, "\"kind\":\"image\"");
        Console.WriteLine($"  image dedupe: geoms(kind=image)={imgGeoms}");
        if (imgGeoms != 1) { fail++; Console.WriteLine("  FAIL: 图片几何按指令重复发送了"); }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-composition.json"), json);
        return fail;
    }

}
