using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace GHCE
{
    /// <summary>
    /// 电池图标：全部用 GDI+ 现画，24×24，不引入外部 PNG/SVG 资源。
    /// 好处是没有二进制资产要管、没有授权问题，改颜色只改常量；
    /// 代价是高 DPI 下会被 GH 放大插值（GH 的 Icon 属性只吃 Bitmap）。
    /// </summary>
    internal static class Icons
    {
        // 与运行时 HUD 同一套颜色
        static readonly Color Tile = Color.FromArgb(0x14, 0x14, 0x1A);
        static readonly Color Edge = Color.FromArgb(0x3A, 0x3A, 0x44);
        static readonly Color Ink = Color.FromArgb(0xE8, 0xE6, 0xDF);
        static readonly Color Green = Color.FromArgb(0x07, 0xA9, 0x54);
        static readonly Color Pink = Color.FromArgb(0xFD, 0x7A, 0xB4);
        static readonly Color Void = Color.FromArgb(0x0B, 0x0B, 0x0F);

        static readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>();
        static readonly object _lock = new object();

        public static Bitmap Canvas => Get("canvas", DrawCanvas);
        public static Bitmap PathInput => Get("pathin", DrawPathInput);
        public static Bitmap DrawPath => Get("drawpath", DrawDrawPath);
        public static Bitmap RenderOutput => Get("render", DrawRenderOutput);
        public static Bitmap EntityBlueprint => Get("entity", DrawEntityBlueprint);
        public static Bitmap EntityLook => Get("look", DrawEntityLook);
        public static Bitmap DrawProgress => Get("progress", DrawDrawProgress);
        // v0.13 起 Keyframe / Timeline 电池已删除（预设动画的来源），图标随之移除
        public static Bitmap Transform => Get("transform", DrawTransform);
        public static Bitmap Track => Get("track", DrawTrack);
        public static Bitmap Interaction => Get("interaction", DrawInteraction);
        public static Bitmap RuntimeInfo => Get("info", DrawRuntimeInfo);

        // v0.10（阶段 1 地基）
        public static Bitmap ProjectTo2D => Get("proj2d", DrawProjectTo2D);
        public static Bitmap Layer => Get("layer", DrawLayer);
        public static Bitmap StrokeStyle => Get("stroke", DrawStrokeStyle);
        public static Bitmap FillStyle => Get("fill", DrawFillStyle);

        // v0.11（阶段 3 收尾）
        public static Bitmap EventTrigger => Get("event", DrawEventTrigger);

        // 参数类型（数据）图标：GH 会把自定义 Param 也列进标签页，
        // 不给图标就是一块棋盘格。做成"数据片"的视觉，跟电池图标区分开。
        public static Bitmap DataCanvas => Get("data-canvas", DrawDataCanvas);
        public static Bitmap DataPath => Get("data-path", DrawDataPath);
        public static Bitmap DataCommand => Get("data-cmd", DrawDataCommand);
        public static Bitmap DataEntity => Get("data-ent", DrawDataEntity);
        public static Bitmap DataTrack => Get("data-track", DrawDataTrack);
        public static Bitmap DataCamera => Get("data-cam", DrawDataCamera);
        public static Bitmap DataEvent => Get("data-evt", DrawDataEvent);

        static Bitmap Get(string key, Action<Graphics> draw)
        {
            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out var master))
                {
                    master = new Bitmap(24, 24, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(master))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        draw(g);
                    }
                    _cache[key] = master;
                }

                // 每次都交出一份独立副本。
                // 把同一个 Bitmap 实例反复返回给宿主时，GH 画完会释放它，
                // 于是只有第一个拿到实例的电池有图标，其余全是空白 —— 这是踩过的坑。
                return new Bitmap(master);
            }
        }

        /// <summary>
        /// 把四个图标放大 8 倍拼成一张图并返回路径。
        /// 用途：在 Rhino 里自查"插件到底有没有画出图标"，把歧义从"GH 没显示"变成"图在不在"。
        /// </summary>
        public static string DumpSheet()
        {
            var items = new[]
            {
                (name: "Canvas", bmp: Canvas),
                (name: "Path Input", bmp: PathInput),
                (name: "Draw Path", bmp: DrawPath),
                (name: "Render Output", bmp: RenderOutput),
                (name: "data: Canvas", bmp: DataCanvas),
                (name: "data: Path", bmp: DataPath),
                (name: "data: Command", bmp: DataCommand),
                (name: "Project to 2D", bmp: ProjectTo2D),
                (name: "Layer", bmp: Layer),
                (name: "Stroke Style", bmp: StrokeStyle),
                (name: "Fill Style", bmp: FillStyle),
                (name: "data: Camera", bmp: DataCamera),
                (name: "Event Trigger", bmp: EventTrigger),
                (name: "data: Event", bmp: DataEvent),
            };

            const int S = 8, Pad = 20, Label = 34;
            int cell = 24 * S;
            int w = Pad + items.Length * (cell + Pad);
            int h = cell + Label + Pad * 2;
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ghce-icons.png");

            using (var sheet = new Bitmap(w, h))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x0B, 0x0B, 0x0F));
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var font = new Font("Consolas", 13f))
                using (var brush = new SolidBrush(Ink))
                {
                    for (int i = 0; i < items.Length; i++)
                    {
                        int x = Pad + i * (cell + Pad);
                        g.DrawImage(items[i].bmp, new Rectangle(x, Pad, cell, cell));
                        g.DrawString(items[i].name, font, brush, x, Pad + cell + 8);
                    }
                }
                sheet.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            return file;
        }

        /* ---------------- 阶段 3：变换 / 交互 / 自检 ---------------- */

        // Transform：正方形 + 旋转弧 + 位移箭头
        static void DrawTransform(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.3f))
            {
                g.DrawRectangle(pen, 8f, 9f, 8f, 8f);
            }
            using (var pen = new Pen(Pink, 1.5f))
            {
                g.DrawArc(pen, 4f, 4f, 12f, 12f, 200, 110);
            }
            using (var pen = new Pen(Green, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor })
            {
                g.DrawLine(pen, 17.5f, 19.5f, 21f, 16f);
            }
        }

        // Interaction：鼠标指针
        static void DrawInteraction(Graphics g)
        {
            Tile24(g);
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(Ink))
            {
                p.AddPolygon(new[]
                {
                    new PointF(6f, 4.5f), new PointF(6f, 18.5f), new PointF(9.8f, 14.8f),
                    new PointF(12.4f, 20f), new PointF(14.6f, 19f), new PointF(12f, 13.9f),
                    new PointF(17.2f, 13.4f),
                });
                g.FillPath(b, p);
            }
            Dot(g, 18.5f, 6.5f, 2.0f, Pink);
        }

        // Runtime Info：两根表针（帧率 / 对象数）
        static void DrawRuntimeInfo(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x66, 0x66, 0x74), 1.2f))
            {
                g.DrawArc(pen, 4f, 7f, 7.5f, 7.5f, 0, 360);
                g.DrawArc(pen, 12.5f, 7f, 7.5f, 7.5f, 0, 360);
            }
            using (var pen = new Pen(Green, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 7.7f, 10.7f, 9.6f, 9.2f);
            }
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 16.2f, 10.7f, 14.9f, 8.6f);
            }
            using (var pen = new Pen(Ink, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 6f, 18.5f, 18f, 18.5f);
            }
        }

        /* ---------------- 时间线 ---------------- */

        static void Diamond(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                g.FillPolygon(b, new[]
                {
                    new PointF(cx, cy - r), new PointF(cx + r, cy),
                    new PointF(cx, cy + r), new PointF(cx - r, cy),
                });
            }
        }

        // Keyframe / Timeline 的绘制函数随电池一起删除（v0.13）

        // Track 数据：台阶曲线（关键帧的离散感）
        static void DrawDataTrack(Graphics g)
        {
            DataFrame(g, Green);
            using (var pen = new Pen(Ink, 1.3f) { LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(6.5f, 15.5f), new PointF(10f, 15.5f),
                    new PointF(10f, 11f), new PointF(13.5f, 11f),
                    new PointF(13.5f, 7.5f), new PointF(17.5f, 7.5f),
                });
            }
            Diamond(g, 17.5f, 7.5f, 1.6f, Pink);
        }

        /* ---------------- 阶段 2：实体 ---------------- */

        // Entity Blueprint：曲线上的一串粒子沿路径排布（强调"沿几何运动"）
        static void DrawEntityBlueprint(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x66, 0x66, 0x74), 1f))
            {
                g.DrawBezier(pen, 4.5f, 17.5f, 9f, 17.5f, 9.5f, 6.5f, 19.5f, 6.5f);
            }
            for (int i = 0; i < 5; i++)
                Dot(g, 5.5f + i * 3.2f, 16.2f - i * 2.4f, 1.5f, i == 4 ? Pink : Green);
        }

        // Entity Look：同一条轨迹上大小/颜色不同的点（强调"外观可独立定义"）
        static void DrawEntityLook(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x55, 0x55, 0x62), 1f) { DashPattern = new[] { 1.2f, 1.4f } })
            {
                g.DrawBezier(pen, 4.5f, 17.5f, 9f, 17.5f, 9.5f, 6.5f, 19.5f, 6.5f);
            }
            Dot(g, 5.5f, 16.3f, 2.2f, Pink);
            Dot(g, 8.8f, 12.6f, 1.7f, Color.FromArgb(0xFF, 0xA8, 0xCC));
            Dot(g, 12.5f, 9.4f, 1.3f, Ink);
            Dot(g, 16.4f, 7.1f, 1.0f, Color.FromArgb(0x99, 0xE8, 0xE6, 0xDF));
        }

        // Track：一条时间轴 + 几个采样帧 + 播放头
        static void DrawTrack(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x66, 0x66, 0x74), 1f))
            {
                g.DrawLine(pen, 3.5f, 13f, 20.5f, 13f);
            }
            for (int i = 0; i < 4; i++)
                Dot(g, 5f + i * 4.6f, 13f, 1.3f, Color.FromArgb(0xAA, 0xE8, 0xE6, 0xDF));
            Dot(g, 5f, 13f, 1.9f, Green);
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 14.2f, 5.5f, 14.2f, 19f);
            }
        }

        // Entity Behavior 的绘制随电池一起删除（v0.15：6 个预设模式只剩"沿路径"，不需要单独电池）

        // Draw Progress：一段实线 + 一段虚线（画到这里）
        static void DrawDrawProgress(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x50, 0x50, 0x5C), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 3.5f, 17f, 9f, 17f, 9f, 7f, 20.5f, 7f);
            }
            using (var pen = new Pen(Pink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 3.5f, 17f, 7f, 17f, 8.2f, 13f, 11.5f, 10.5f);
            }
            Dot(g, 11.5f, 10.5f, 1.4f, Ink);
        }

        // Entities 数据：细边框里几个点
        static void DrawDataEntity(Graphics g)
        {
            DataFrame(g, Pink);
            Dot(g, 7.5f, 15.5f, 1.4f, Green);
            Dot(g, 11.5f, 12.5f, 1.4f, Ink);
            Dot(g, 15.5f, 9.5f, 1.4f, Pink);
            using (var pen = new Pen(Color.FromArgb(0x70, 0x70, 0x7C), 1f) { DashPattern = new[] { 1.1f, 1.3f } })
            {
                g.DrawLine(pen, 7.5f, 15.5f, 11.5f, 12.5f);
                g.DrawLine(pen, 11.5f, 12.5f, 15.5f, 9.5f);
            }
        }

        /* ---------------- 参数（数据）图标 ---------------- */

        // 数据图标统一用"细边框 + 内部一点符号"，与电池的深色实心块区分：
        // 一眼能分清"这是个动作"还是"这是份数据"。

        static void DataFrame(Graphics g, Color accent)
        {
            using (var p = RoundedRect(2.5f, 2.5f, 19f, 19f, 3.5f))
            using (var b = new SolidBrush(Color.FromArgb(0x18, 0x18, 0x20)))
            using (var pen = new Pen(Color.FromArgb(0x50, 0x50, 0x5C), 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var pen = new Pen(accent, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 2.5f, 8f, 2.5f, 5f);
                g.DrawLine(pen, 2.5f, 8f, 5.5f, 8f);
            }
        }

        // Canvas 数据：一个空画框
        static void DrawDataCanvas(Graphics g)
        {
            DataFrame(g, Green);
            using (var pen = new Pen(Color.FromArgb(0x88, 0x88, 0x94), 1f))
            using (var p = RoundedRect(7f, 6.5f, 10f, 8f, 1f))
            {
                g.DrawPath(pen, p);
            }
        }

        // Path 数据：一串点连成的折线（强调"采样点"）
        static void DrawDataPath(Graphics g)
        {
            DataFrame(g, Green);
            using (var pen = new Pen(Ink, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(6.5f, 16f), new PointF(10f, 11.5f),
                    new PointF(13.5f, 15f), new PointF(17.5f, 9f),
                });
            }
            Dot(g, 6.5f, 16f, 1.1f, Ink);
            Dot(g, 10f, 11.5f, 1.1f, Ink);
            Dot(g, 13.5f, 15f, 1.1f, Ink);
            Dot(g, 17.5f, 9f, 1.3f, Pink);
        }

        // Command 数据：路径 + 一个画笔标记（说明"带样式"）
        static void DrawDataCommand(Graphics g)
        {
            DataFrame(g, Pink);
            using (var pen = new Pen(Ink, 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 6f, 16.5f, 10f, 16.5f, 9f, 10f, 13f, 10f);
            }
            using (var b = new SolidBrush(Pink))
            {
                g.FillRectangle(b, 14.5f, 12f, 4f, 4f);
            }
        }

        /* ---------------- 阶段 1 地基（v0.10） ---------------- */

        // Project to 2D：虚线的"外接框" + 框内一条曲线 + 四角向内的绿角标（读作"把几何框进画幅"）
        static void DrawProjectTo2D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x55, 0x55, 0x60), 1f) { DashPattern = new[] { 1.3f, 1.4f } })
            using (var p = RoundedRect(3.5f, 4.5f, 17f, 15f, 1.5f))
            {
                g.DrawPath(pen, p);
            }
            using (var pen = new Pen(Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 6.5f, 15.5f, 10f, 15.5f, 11f, 8.5f, 17.5f, 8.5f);
            }
            using (var pen = new Pen(Green, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // 四角向内的角标
                g.DrawLine(pen, 2.5f, 2.5f, 2.5f, 6f); g.DrawLine(pen, 2.5f, 2.5f, 6f, 2.5f);
                g.DrawLine(pen, 21.5f, 21.5f, 21.5f, 18f); g.DrawLine(pen, 21.5f, 21.5f, 18f, 21.5f);
            }
            Dot(g, 17.5f, 8.5f, 1.4f, Pink);
        }

        // Layer：三张错位叠放的面片（上浅下深 = 顺序）
        static void DrawLayer(Graphics g)
        {
            Tile24(g);
            using (var p1 = RoundedRect(6f, 3.5f, 13f, 7.5f, 1.5f))
            using (var b1 = new SolidBrush(Pink))
                g.FillPath(b1, p1);

            using (var p2 = RoundedRect(4.5f, 8f, 13f, 7.5f, 1.5f))
            using (var b2 = new SolidBrush(Color.FromArgb(0xA8, 0xE8, 0xE6, 0xDF)))
            using (var pen = new Pen(Void, 1f))
            {
                g.FillPath(b2, p2);
                g.DrawPath(pen, p2);
            }

            using (var p3 = RoundedRect(3f, 12.5f, 13f, 7.5f, 1.5f))
            using (var b3 = new SolidBrush(Color.FromArgb(0x30, 0x30, 0x3C)))
            using (var pen = new Pen(Edge, 1f))
            {
                g.FillPath(b3, p3);
                g.DrawPath(pen, p3);
            }
        }

        // Stroke Style：同一条曲线，上细下粗（读作"改笔宽/笔头"）
        static void DrawStrokeStyle(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x66, 0x66, 0x74), 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 4f, 8f, 8f, 8f, 9f, 5.5f, 13f, 5.5f);
            }
            using (var pen = new Pen(Pink, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 5f, 16.5f, 10f, 16.5f, 10.5f, 12.5f, 17f, 12.5f);
            }
            using (var pen = new Pen(Color.FromArgb(0xFF, 0xC8, 0xDE), 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 5f, 16.5f, 10f, 16.5f, 10.5f, 12.5f, 17f, 12.5f);
            }
            Dot(g, 17f, 12.5f, 1.5f, Green);
        }

        // Fill Style：一个被填满的面片 + 一圈描边（读作"填色"）
        static void DrawFillStyle(Graphics g)
        {
            Tile24(g);
            using (var p = RoundedRect(4.5f, 6f, 15f, 12f, 3f))
            using (var b = new SolidBrush(Pink))
            using (var pen = new Pen(Ink, 1.3f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var pen = new Pen(Void, 1.2f) { DashPattern = new[] { 1.4f, 1.6f } })
            {
                g.DrawLine(pen, 7f, 15.5f, 17f, 8.5f);
            }
        }

        // Camera 数据：取景框 + 中心十字 + 圆（镜头）
        static void DrawDataCamera(Graphics g)
        {
            DataFrame(g, Green);
            using (var pen = new Pen(Ink, 1.1f))
            {
                g.DrawEllipse(pen, 8f, 8f, 8f, 8f);
            }
            using (var pen = new Pen(Pink, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 12f, 6.5f, 12f, 9f);
                g.DrawLine(pen, 12f, 17.5f, 12f, 15f);
                g.DrawLine(pen, 6.5f, 12f, 9f, 12f);
                g.DrawLine(pen, 17.5f, 12f, 15f, 12f);
            }
            Dot(g, 12f, 12f, 1.4f, Pink);
        }

        /* ---------------- 阶段 3 收尾（v0.11） ---------------- */

        // Oscillator 的绘制随电池一起删除（v0.13）

        // Event Trigger：点击指尖 + 一个闪（读作"命中即执行"）
        static void DrawEventTrigger(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // 一个向下的点击箭头
                g.DrawLine(pen, 11f, 4.5f, 11f, 12f);
                g.DrawLine(pen, 8.2f, 9.4f, 11f, 12.2f);
                g.DrawLine(pen, 13.8f, 9.4f, 11f, 12.2f);
            }
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, 5.5f, 13.5f, 11f, 6.5f, 200, 140);   // 命中后的波纹
            }
            using (var pen = new Pen(Green, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 3.5f, 19.5f, 20.5f, 19.5f);
            }
            Dot(g, 11f, 12.4f, 1.3f, Pink);
        }

        // Event 数据：细边框 + 闪电（一条规则）
        static void DrawDataEvent(Graphics g)
        {
            DataFrame(g, Pink);
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(Ink))
            {
                p.AddPolygon(new[]
                {
                    new PointF(13.2f, 5.5f), new PointF(8.4f, 12.6f), new PointF(11.4f, 12.6f),
                    new PointF(10.4f, 18.5f), new PointF(15.6f, 11.2f), new PointF(12.4f, 11.2f),
                });
                g.FillPath(b, p);
            }
        }

        /* ---------------- 公共绘制件 ---------------- */

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void Tile24(Graphics g)
        {
            using (var p = RoundedRect(0.5f, 0.5f, 23f, 23f, 4f))
            using (var b = new SolidBrush(Tile))
            using (var pen = new Pen(Edge, 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }

        static void Curve(Graphics g, Color c, float width, float dashOffset = 0f, float[] dash = null)
        {
            using (var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (dash != null) pen.DashPattern = dash;
                if (dash != null) pen.DashOffset = dashOffset;
                g.DrawBezier(pen, 4f, 18f, 9f, 18f, 8f, 7f, 13.5f, 7f);
            }
        }

        static void Dot(Graphics g, float x, float y, float r, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        }

        /* ---------------- 四个图标 ---------------- */

        // Canvas：画布框 + 绿色裁切角标
        static void DrawCanvas(Graphics g)
        {
            Tile24(g);
            using (var inner = RoundedRect(4f, 6f, 16f, 12f, 1.5f))
            using (var b = new SolidBrush(Void))
            using (var pen = new Pen(Edge, 1f))
            {
                g.FillPath(b, inner);
                g.DrawPath(pen, inner);
            }
            using (var pen = new Pen(Green, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 3f, 4f, 7f, 4f);
                g.DrawLine(pen, 3f, 4f, 3f, 8f);
                g.DrawLine(pen, 21f, 20f, 17f, 20f);
                g.DrawLine(pen, 21f, 20f, 21f, 16f);
            }
        }

        // Path Input：左侧虚线源框 + 曲线 + 两端节点
        static void DrawPathInput(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x55, 0x55, 0x60), 1f) { DashPattern = new[] { 1.2f, 1.4f } })
            {
                g.DrawLine(pen, 3.5f, 5f, 3.5f, 19f);
            }
            Curve(g, Ink, 1.6f);
            Dot(g, 4f, 18f, 1.7f, Green);
            Dot(g, 13.5f, 7f, 1.7f, Pink);
        }

        // Draw Path：曲线前半段已被"上色"，后半段还是细线 —— 读作"正在被描绘的路径"
        static void DrawDrawPath(Graphics g)
        {
            Tile24(g);
            var p0 = new PointF(4f, 18f);
            var p1 = new PointF(9f, 18f);
            var p2 = new PointF(8f, 7f);
            var p3 = new PointF(13.5f, 7f);

            Curve(g, Ink, 1.2f);

            var left = SplitBezier(p0, p1, p2, p3, 0.58f);
            using (var pen = new Pen(Pink, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawBezier(pen, left[0], left[1], left[2], left[3]);
            using (var pen = new Pen(Color.FromArgb(0xFF, 0xC8, 0xDE), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawBezier(pen, left[0], left[1], left[2], left[3]);

            Dot(g, 13.5f, 7f, 1.6f, Green);
        }

        /// <summary>de Casteljau 左半段：把三次贝塞尔在 t 处切开，取左支</summary>
        static PointF[] SplitBezier(PointF p0, PointF p1, PointF p2, PointF p3, float t)
        {
            PointF Lerp(PointF a, PointF b) => new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            var a = Lerp(p0, p1);
            var b = Lerp(p1, p2);
            var c = Lerp(p2, p3);
            var d = Lerp(a, b);
            var e = Lerp(b, c);
            var f = Lerp(d, e);
            return new[] { p0, a, d, f };
        }

        // Render Output：画面 → 出线 → 箭头穿出右边界
        static void DrawRenderOutput(Graphics g)
        {
            Tile24(g);
            using (var inner = RoundedRect(3.5f, 6f, 9f, 12f, 1.5f))
            using (var b = new SolidBrush(Void))
            using (var pen = new Pen(Edge, 1f))
            {
                g.FillPath(b, inner);
                g.DrawPath(pen, inner);
            }
            Curve(g, Ink, 1.2f);

            using (var pen = new Pen(Green, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 14.5f, 12f, 20f, 12f);
            }
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(Green))
            {
                p.AddPolygon(new[]
                {
                    new PointF(20.8f, 12f),
                    new PointF(18.2f, 10.2f),
                    new PointF(18.2f, 13.8f),
                });
                g.FillPath(b, p);
            }
        }
    }
}
