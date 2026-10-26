using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.DocObjects;
using Rhino.Geometry;
using GHCE.Bridge;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Text —— 文字直接变成曲线。
    ///
    /// 为什么不在浏览器里 fillText：那样导出会依赖字体。用户机器上少一个字体，
    /// SVG 和 PNG 就长得不一样，而这类差异只在别人的电脑上出现。
    /// 在 GH 里转成曲线之后，下游只认路径 —— 运行时不需要字体，导出也永远一致。
    ///
    /// 代价是改字号要重新求解。文字在作品里是版式决定，不是每帧变量，这个代价可以接受。
    /// </summary>
    public class TextComponent : GH_Component
    {
        public TextComponent()
            : base("Text", "Text", "Text as curves: font, size, bold, italic. One curve list per line", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c5");
        protected override Bitmap Icon => Icons.Text;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Text", "Text", "Text to convert. Use \\n for line breaks", GH_ParamAccess.item, "GHCE");
            p.AddTextParameter("Font", "Font", "Installed font family name, e.g. Arial or Helvetica", GH_ParamAccess.item, "Arial");
            p.AddNumberParameter("Size", "Size", "Text height in model units", GH_ParamAccess.item, 72.0);
            p.AddBooleanParameter("Bold", "Bold", "Bold weight", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Italic", "Italic", "Italic style", GH_ParamAccess.item, false);
            p.AddNumberParameter("Line Spacing", "LS", "Line spacing as a multiple of Size", GH_ParamAccess.item, 1.2);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Curves", "C", "Glyph outlines, one entry per contour", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Line and curve count", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string text = null, font = "Arial";
            double size = 72, spacing = 1.2;
            bool bold = false, italic = false;

            DA.GetData(0, ref text);
            DA.GetData(1, ref font);
            DA.GetData(2, ref size);
            DA.GetData(3, ref bold);
            DA.GetData(4, ref italic);
            DA.GetData(5, ref spacing);

            if (string.IsNullOrEmpty(text)) return;
            if (size <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Size must be positive. Fell back to 72");
                size = 72;
            }
            if (spacing < 0.5) spacing = 1.2;

            // 这两个枚举是 Rhino.DocObjects.Font 的嵌套类型，不是命名空间级类型 ——
            // 写成 Rhino.DocObjects.FontStyle 会直接 CS0234。
            var weight = bold ? Rhino.DocObjects.Font.FontWeight.Bold : Rhino.DocObjects.Font.FontWeight.Normal;
            var slant = italic ? Rhino.DocObjects.Font.FontStyle.Italic : Rhino.DocObjects.Font.FontStyle.Upright;

            DimensionStyle style;
            try
            {
                style = new DimensionStyle
                {
                    TextHeight = size,
                    Font = new Rhino.DocObjects.Font(string.IsNullOrWhiteSpace(font) ? "Arial" : font.Trim(),
                                                    weight, slant, false, false),
                };
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cannot build text style: " + ex.Message);
                return;
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var curves = new List<Curve>();
            int failed = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;

                // 每行一个平面：原点往下挪一行。用平面而不是 Transform，
                // 是因为 TextEntity.Create 只认平面，而且这样字距由字体自己决定。
                var plane = Plane.WorldXY;
                plane.Origin = new Point3d(0, -i * size * spacing, 0);

                TextEntity entity;
                try
                {
                    entity = TextEntity.Create(lines[i], plane, style, false, 0, 0);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Line {i + 1} failed: {ex.Message}");
                    failed++;
                    continue;
                }
                if (entity == null) { failed++; continue; }

                Curve[] part;
                try
                {
                    part = entity.CreateCurves(style, true, 1.0, 1.0);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Line {i + 1} outline failed: {ex.Message}");
                    failed++;
                    continue;
                }

                if (part != null) curves.AddRange(part);
            }

            if (curves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No curves produced. Check the font name — a missing font usually falls back silently");
                return;
            }
            if (failed > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{failed} line(s) produced no geometry");

            DA.SetDataList(0, curves);
            string info = $"{lines.Length} line(s) · {curves.Count} curve(s) · {size:0.#} / {font}";
            Message = info;
            DA.SetData(1, info);
        }
    }

    /// <summary>
    /// Image —— 把图片读成内嵌 data URL。
    ///
    /// 在 GH 侧就降采样：一张 4000px 的照片直接进 patch，首帧会变成十几 MB。
    /// 缓存按"路径 + 修改时间 + Max Size"命中，拖滑条不会反复读盘。
    /// </summary>
    public class ImageComponent : GH_Component
    {
        public ImageComponent()
            : base("Image", "Image", "Reads an image file and embeds it as a data URL, downscaled to Max Size", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c7");
        protected override Bitmap Icon => Icons.Image;

        /// <summary>单张上限：超过 4MB 的 data URL 会让首帧明显卡顿。</summary>
        const int MaxBytes = 4 * 1024 * 1024;

        static readonly Dictionary<string, ImageDef> Cache = new Dictionary<string, ImageDef>(StringComparer.OrdinalIgnoreCase);

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_FilePath
            {
                Name = "File",
                NickName = "File",
                Description = "Image file (png / jpg / bmp / gif)",
                Access = GH_ParamAccess.item,
            });
            p.AddIntegerParameter("Max Size", "Max", "Longest edge in pixels after downscaling. 0 = keep original", GH_ParamAccess.item, 1024);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Image(), "Image", "Img", "Embedded image", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Size and payload", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string path = null;
            int max = 1024;
            if (!DA.GetData(0, ref path) || string.IsNullOrWhiteSpace(path)) return;
            DA.GetData(1, ref max);

            if (!File.Exists(path))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "File not found: " + path);
                return;
            }
            max = Math.Max(0, Math.Min(8192, max));

            var info = new FileInfo(path);
            string key = $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{max}";

            if (Cache.TryGetValue(key, out var cached))
            {
                Emit(DA, cached);
                return;
            }

            ImageDef def;
            try
            {
                def = Load(path, max);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cannot read image: " + ex.Message);
                return;
            }

            if (def.Src.Length > MaxBytes)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Embedded image is {def.Src.Length / 1024 / 1024} MB (limit 4 MB). Lower Max Size");
                return;
            }

            // 只留最近 8 张，防止拖一堆文件把内存占满
            if (Cache.Count > 8) Cache.Clear();
            Cache[key] = def;
            Emit(DA, def);
        }

        void Emit(IGH_DataAccess DA, ImageDef def)
        {
            DA.SetData(0, new GHCE_Image(def));
            string info = $"{def.W}×{def.H} · {def.Src.Length / 1024} KB";
            Message = info;
            DA.SetData(1, info);
        }

        static ImageDef Load(string path, int max)
        {
            using (var src = System.Drawing.Image.FromFile(path))
            {
                int w = src.Width, h = src.Height;
                double scale = 1.0;
                if (max > 0 && Math.Max(w, h) > max)
                    scale = max / (double)Math.Max(w, h);

                int tw = Math.Max(1, (int)Math.Round(w * scale));
                int th = Math.Max(1, (int)Math.Round(h * scale));

                using (var bmp = new Bitmap(tw, th))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, tw, th);
                    }

                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        string b64 = Convert.ToBase64String(ms.ToArray());
                        return new ImageDef
                        {
                            // 身份 = 文件（路径/时间/大小）+ 目标尺寸：换图就是另一个 geom
                            Id = PatchBuilder.StableId("g_img_", $"{path}|{max}|{tw}x{th}"),
                            Src = "data:image/png;base64," + b64,
                            W = tw,
                            H = th,
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draw Image —— 把图片放到页面上。
    ///
    /// 尺寸只给 Width（0 = 原始像素尺寸，1 = 等比缩到 1 页宽），高度按比例算。
    /// 不透明度不在这里：要淡入就接 Bind 把 opacity 绑到表达式上，规则和别的指令一样。
    /// </summary>
    public class DrawImageComponent : GH_Component
    {
        public DrawImageComponent()
            : base("Draw Image", "DrawImg", "Places an image on the page: position, width, layer and order", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c8");
        protected override Bitmap Icon => Icons.DrawImage;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Image(), "Image", "Img", "Image from the Image component", GH_ParamAccess.item);
            p.AddTextParameter("Layer", "Layer", "Layer name. Same name shares one layer", GH_ParamAccess.item, "main");
            p.AddIntegerParameter("Order", "Order", "Draw order within the layer", GH_ParamAccess.item, 0);
            p.AddNumberParameter("X", "X", "Left edge in page pixels", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Y", "Y", "Top edge in page pixels", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Width", "W", "Target width in page pixels. 0 = natural pixel size", GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Image draw command", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Placement summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GHCE_Image img = null;
            if (!DA.GetData(0, ref img) || img?.Value == null) return;

            string layer = "main";
            int order = 0;
            double x = 0, y = 0, width = 0;
            DA.GetData(1, ref layer);
            DA.GetData(2, ref order);
            DA.GetData(3, ref x);
            DA.GetData(4, ref y);
            DA.GetData(5, ref width);

            var image = img.Value;
            if (image.W <= 0 || image.H <= 0) return;

            double scale = width > 0 ? width / image.W : 1.0;
            double w = image.W * scale, h = image.H * scale;

            var def = new CommandDef
            {
                Id = PatchBuilder.StableId("c_img_", image.Id),
                GeomId = image.Id,
                Image = image,
                Layer = string.IsNullOrWhiteSpace(layer) ? "main" : layer.Trim(),
                Order = order,
                Visible = true,
                Opacity = 1.0,
                Progress = 1.0,
                // 几何是单位方块 (0,0)-(1,1)，所以矩阵的线性部分直接就是像素尺寸：
                // [w, 0, 0, h, x, y]。写成两个 scale 会让 4:3 的图变成正方形。
                Matrix = new[] { w, 0, 0, h, x, y },
            };

            DA.SetDataList(0, new List<GHCE_Command> { new GHCE_Command(def) });

            string info = $"{w:0}×{h:0} @ ({x:0},{y:0})";
            Message = info;
            DA.SetData(1, info);
        }
    }

    /// <summary>
    /// Clip —— 给一批指令挂一个裁剪模具。
    ///
    /// 模具是**页面坐标**：运行时在指令自己的变换**之前**落剪，所以图片旋转了，裁剪框不会跟着转。
    /// 海报里的"把照片裁进这个形状"就是它。
    ///
    /// 一条指令只保留一个 clip：后接的覆盖前面的。可预期，不叠加。
    /// </summary>
    public class ClipComponent : GH_Component
    {
        public ClipComponent()
            : base("Clip", "Clip", "Clips commands with a closed path. 1 keep inside 2 keep outside", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000c9");
        protected override Bitmap Icon => Icons.Clip;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands to clip", GH_ParamAccess.list);
            p.AddParameter(new Param_GHCE_Path(), "Clip Path", "Clip", "Closed path in page coordinates", GH_ParamAccess.item);
            p.AddIntegerParameter("Mode", "Mode", "1 keep inside · 2 keep outside", GH_ParamAccess.item, 1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "Commands carrying the clip", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Clip summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            GHCE_Path clipGoo = null;
            int mode = 1;

            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;
            if (!DA.GetData(1, ref clipGoo) || clipGoo?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Clip Path is required");
                return;
            }
            DA.GetData(2, ref mode);

            var path = clipGoo.Value;
            if (path.XY == null || path.XY.Length < 6)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Clip path needs at least 3 points");
                return;
            }

            bool invert = mode == 2;
            var outList = new List<GHCE_Command>(cmds.Count);
            foreach (var g in cmds)
            {
                if (g?.Value == null) continue;
                var c = g.Value.Duplicate();
                c.Clip = new ClipDef { GeomId = path.Id, Invert = invert, Path = path };
                outList.Add(new GHCE_Command(c));
            }

            DA.SetDataList(0, outList);
            string info = invert ? "keep outside" : "keep inside";
            Message = info;
            DA.SetData(1, info);
        }
    }
}
