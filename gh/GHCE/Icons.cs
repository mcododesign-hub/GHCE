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
    public static class Icons
    {
        // ---------------------------------------------------------------
        // 图标固定"深色芯片 + 浅色笔画"，不跟随 Rhino/GH 主题。
        //
        // 为什么不做主题跟随：功能区面板始终是浅色的，图标自己带着深色底，
        // 面板是浅是深都无所谓；反过来跟随主题反而出过两次事故 ——
        // 一次是跟到深色后整排在浅面板上变成黑块（v0.28），
        // 一次是浅色档要画深字，把旧代码里写死的深底配成了黑底黑字（v0.28.1）。
        //
        // 唯一的铁律：字形只用 Ink / Muted / Faint，它们只跟 Tile / Card / Screen 配，
        // 三对颜色在下面写死，任何一对的亮度差都大于 100。
        // ---------------------------------------------------------------
        static readonly Color Tile = Color.FromArgb(0x17, 0x17, 0x1E);      // 电池图标底
        static readonly Color Edge = Color.FromArgb(0x3C, 0x3C, 0x48);      // 电池图标边
        static readonly Color Ink = Color.FromArgb(0xED, 0xEB, 0xE4);       // 主笔画

        // 数据图标是一张比电池底稍亮的卡片，靠色差区分"数据"与"动作"
        static readonly Color Card = Color.FromArgb(0x24, 0x24, 0x2E);
        static readonly Color CardEdge = Color.FromArgb(0x4C, 0x4C, 0x5A);

        // 次级笔画：同色系两级弱化
        static readonly Color Muted = Color.FromArgb(0xA8, 0xA8, 0xB4);
        static readonly Color Faint = Color.FromArgb(0x72, 0x72, 0x7E);

        // "屏幕"：比底更暗的一块显示面，内容和它反向（浅字）
        static readonly Color Screen = Color.FromArgb(0x0A, 0x0A, 0x10);
        static readonly Color ScreenInk = Color.FromArgb(0xED, 0xEB, 0xE4);

        // 强调色，深底上取偏亮的一档
        static readonly Color Green = Color.FromArgb(0x10, 0xB3, 0x63);
        static readonly Color Pink = Color.FromArgb(0xFD, 0x7A, 0xB4);

        /// <summary>给令牌加透明度，避免到处写 Color.FromArgb(a, 0xE8, 0xE6, 0xDF)。</summary>
        static Color Tint(Color c, int alpha) => Color.FromArgb(alpha, c);

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
        public static Bitmap Timeline => Get("timeline", DrawTimeline);
        public static Bitmap Morph => Get("morph", DrawMorph);
        public static Bitmap SignalRandom => Get("sig-rand", DrawSignalRandom);
        public static Bitmap SignalToFloat => Get("sig-float", DrawSignalToFloat);
        public static Bitmap Noise => Get("noise", DrawNoise);
        public static Bitmap Interaction => Get("interaction", DrawInteraction);
        public static Bitmap RuntimeInfo => Get("info", DrawRuntimeInfo);

        // v0.25（阶段 5：3D）
        public static Bitmap MeshInput => Get("mesh-in", DrawMeshInput);
        public static Bitmap Transform3D => Get("xform3d", DrawTransform3D);
        public static Bitmap Draw3D => Get("draw3d", DrawDraw3D);
        public static Bitmap Camera3D => Get("cam3d", DrawCamera3D);
        public static Bitmap Scene3D => Get("scene3d", DrawScene3D);
        public static Bitmap Point3D => Get("point3d", DrawPoint3D);
        public static Bitmap Curve3D => Get("curve3d", DrawCurve3D);
        public static Bitmap DrawPoints => Get("drawpts", DrawDrawPoints);

        // v0.10（阶段 1 地基）
        public static Bitmap ProjectTo2D => Get("proj2d", DrawProjectTo2D);
        public static Bitmap Layer => Get("layer", DrawLayer);
        public static Bitmap StrokeStyle => Get("stroke", DrawStrokeStyle);
        public static Bitmap FillStyle => Get("fill", DrawFillStyle);

        // v0.11（阶段 3 收尾）
        public static Bitmap EventTrigger => Get("event", DrawEventTrigger);

        // v0.31 Mark
        public static Bitmap Brush => Get("brush", DrawBrush);
        public static Bitmap MarkPath => Get("mark-path", DrawMarkPath);
        public static Bitmap StyleMap => Get("style-map", DrawStyleMap);

        // v0.32 行为图
        public static Bitmap ReadState => Get("read-state", DrawReadState);
        public static Bitmap WriteState => Get("write-state", DrawWriteState);
        public static Bitmap Spawn => Get("spawn", DrawSpawn);
        public static Bitmap Destroy => Get("destroy", DrawDestroy);

        // v0.30 运行时表达式
        public static Bitmap ExprVar => Get("expr-var", DrawExprVar);
        public static Bitmap ExprTime => Get("expr-time", DrawExprTime);
        public static Bitmap ExprMath => Get("expr-math", DrawExprMath);
        public static Bitmap Bind => Get("expr-bind", DrawBind);
        public static Bitmap EventRule => Get("event-rule", DrawEventRule);
        public static Bitmap ExprRemap => Get("expr-remap", DrawExprRemap);
        public static Bitmap ExprSelect => Get("expr-select", DrawExprSelect);

        // 参数类型（数据）图标：GH 会把自定义 Param 也列进标签页，
        // 不给图标就是一块棋盘格。做成"数据片"的视觉，跟电池图标区分开。
        public static Bitmap DataCanvas => Get("data-canvas", DrawDataCanvas);
        public static Bitmap DataPath => Get("data-path", DrawDataPath);
        public static Bitmap DataCommand => Get("data-cmd", DrawDataCommand);
        public static Bitmap DataEntity => Get("data-ent", DrawDataEntity);
        public static Bitmap DataTrack => Get("data-track", DrawDataTrack);
        public static Bitmap DataCamera => Get("data-cam", DrawDataCamera);
        public static Bitmap DataMesh => Get("data-mesh", DrawDataMesh);
        public static Bitmap DataCmd3D => Get("data-cmd3d", DrawDataCmd3D);
        public static Bitmap DataCamera3D => Get("data-cam3d", DrawDataCamera3D);
        public static Bitmap DataScene3D => Get("data-scene3d", DrawDataScene3D);
        public static Bitmap DataEvent => Get("data-evt", DrawDataEvent);
        public static Bitmap DataSignal => Get("data-sig", DrawDataSignal);
        public static Bitmap DataExpr => Get("data-expr", DrawDataExpr);

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
                // 对照图用功能区那片浅灰：这才是图标真正的落点
                g.Clear(Color.FromArgb(0xF0, 0xF0, 0xF0));
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
            using (var pen = new Pen(Muted, 1.2f))
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
            DataTile(g);
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
            using (var pen = new Pen(Muted, 1f))
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
            using (var pen = new Pen(Faint, 1f) { DashPattern = new[] { 1.2f, 1.4f } })
            {
                g.DrawBezier(pen, 4.5f, 17.5f, 9f, 17.5f, 9.5f, 6.5f, 19.5f, 6.5f);
            }
            Dot(g, 5.5f, 16.3f, 2.2f, Pink);
            Dot(g, 8.8f, 12.6f, 1.7f, Color.FromArgb(0xFF, 0xA8, 0xCC));
            Dot(g, 12.5f, 9.4f, 1.3f, Ink);
            Dot(g, 16.4f, 7.1f, 1.0f, Tint(Ink, 0x99));
        }

        // Track：一条时间轴 + 几个采样帧 + 播放头
        // Signal to Float: wave on the left, arrow to a number on the right
        static void DrawSignalToFloat(Graphics g)
        {
            Tile24(g);
            var pts = new PointF[12];
            for (int i = 0; i < 12; i++)
            {
                float x = 3f + i * 0.95f;
                float y = 9f - (float)(Math.Sin(i * 0.85) * 3.2);
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(Ink, 1.4f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);

            using (var pen = new Pen(Pink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, 4f, 17f, 15f, 17f);
                g.DrawLine(pen, 12.5f, 14.5f, 15.5f, 17f);
                g.DrawLine(pen, 12.5f, 19.5f, 15.5f, 17f);
            }
            Dot(g, 19.5f, 17f, 1.7f, Pink);
        }

        // Data：Signal —— 卡面里嵌一块深色"示波屏"，波形用固定浅色。
        // 屏是深的、线是浅的，两者永远反向，跟主题无关。
        static void DrawDataSignal(Graphics g)
        {
            DataTile(g);
            DataFrame(g, Green);

            using (var screen = RoundedRect(5.5f, 6.5f, 13f, 11f, 1.2f))
            using (var b = new SolidBrush(Screen))
            using (var pen = new Pen(Tint(ScreenInk, 0x88), 1f))
            {
                g.FillPath(b, screen);
                g.DrawPath(pen, screen);
            }

            var pts = new PointF[12];
            for (int i = 0; i < 12; i++)
            {
                float x = 6.5f + i * (11f / 11f);
                float y = 12f - (float)(Math.Sin(i * 0.85) * 3.4);
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(ScreenInk, 1.5f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
        }

        // Random：阶梯跳变（每 0.5 秒换一个值，格内不动）
        static void DrawSignalRandom(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.6f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(3.5f, 16f), new PointF(8f, 16f),
                    new PointF(8f, 9f), new PointF(13f, 9f),
                    new PointF(13f, 18f), new PointF(20f, 18f),
                });
            }
            Dot(g, 20f, 18f, 1.6f, Pink);
        }

        // Noise：连续波形。三个维度共用同一张图，靠名字区分
        static void DrawNoise(Graphics g)
        {
            Tile24(g);
            var pts = new PointF[24];
            for (int i = 0; i < 24; i++)
            {
                float x = 3f + i * (18f / 23f);
                float y = 12f - (float)(Math.Sin(i * 0.55) * 5.6);
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(Ink, 1.5f) { LineJoin = LineJoin.Round })
                g.DrawLines(pen, pts);
            Dot(g, 3f, pts[0].Y, 1.5f, Pink);
        }

        // Signal Apply：波形 → 箭头（信号有去处）
        // 数据图标：细框 + 波浪（与实心电池图标分层）
        // Morph：方 → 圆。中间两个渐隐的过渡形表示"同一拓扑在插值"（不是换镜头）
        static void DrawMorph(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x60), 1.2f))
                g.DrawRectangle(pen, 3.5f, 9f, 6f, 6f);
            using (var pen = new Pen(Tint(Ink, 0xA0), 1.3f))
                g.DrawRectangle(pen, 9.5f, 8f, 7f, 8f);
            using (var pen = new Pen(Pink, 1.6f))
                g.DrawEllipse(pen, 14.5f, 8.5f, 7f, 7f);
            Dot(g, 7f, 12f, 1.1f, Tint(Ink, 0xB0));
        }

        // Timeline：容器 + 三条轨 + 一条贯穿的播放头。
        // 与 Track 的区分：Track 是"一条轨"，Timeline 是"装轨的框"。
        static void DrawTimeline(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x70), 1f))
                g.DrawRectangle(pen, 3.5f, 6.5f, 17f, 11f);

            using (var pen = new Pen(Tint(Ink, 0xB0), 1.4f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 6f, 9.5f, 12f, 9.5f);
                g.DrawLine(pen, 6f, 12f, 16f, 12f);
                g.DrawLine(pen, 6f, 14.5f, 10f, 14.5f);
            }

            using (var pen = new Pen(Pink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, 14f, 4.5f, 14f, 19.5f);
            Dot(g, 14f, 4.5f, 1.6f, Pink);
        }

        static void DrawTrack(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Muted, 1f))
            {
                g.DrawLine(pen, 3.5f, 13f, 20.5f, 13f);
            }
            for (int i = 0; i < 4; i++)
                Dot(g, 5f + i * 4.6f, 13f, 1.3f, Tint(Ink, 0xAA));
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
            using (var pen = new Pen(Faint, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
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
            DataTile(g);
            DataFrame(g, Pink);
            Dot(g, 7.5f, 15.5f, 1.4f, Green);
            Dot(g, 11.5f, 12.5f, 1.4f, Ink);
            Dot(g, 15.5f, 9.5f, 1.4f, Pink);
            using (var pen = new Pen(Faint, 1f) { DashPattern = new[] { 1.1f, 1.3f } })
            {
                g.DrawLine(pen, 7.5f, 15.5f, 11.5f, 12.5f);
                g.DrawLine(pen, 11.5f, 12.5f, 15.5f, 9.5f);
            }
        }

        /* ---------------- 参数（数据）图标 ---------------- */

        // 数据图标统一用"不透明白卡 + 类别色标 + 符号"，与电池的实心块区分：
        // 一眼能分清"这是个动作"还是"这是份数据"。
        //
        // v0.28.1 修正：这段以前自己又画了一张不透明深色卡片（#181820），
        // 把 DataTile 的底和上面的深色字形一起埋掉，浅色主题下整组数据图标是纯黑块。
        // 现在卡片只在 DataTile 里画一次，DataFrame 只负责左上角的类别色标。

        static void DataFrame(Graphics g, Color accent)
        {
            using (var pen = new Pen(accent, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 4.6f, 4.6f, 9f, 4.6f);
                g.DrawLine(pen, 4.6f, 4.6f, 4.6f, 9f);
            }
        }

        // Canvas 数据：一个空画框
        static void DrawDataCanvas(Graphics g)
        {
            DataTile(g);
            DataFrame(g, Green);
            using (var pen = new Pen(Muted, 1f))
            using (var p = RoundedRect(7f, 6.5f, 10f, 8f, 1f))
            {
                g.DrawPath(pen, p);
            }
        }

        // Path 数据：一串点连成的折线（强调"采样点"）
        static void DrawDataPath(Graphics g)
        {
            DataTile(g);
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
            DataTile(g);
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
            using (var pen = new Pen(Faint, 1f) { DashPattern = new[] { 1.3f, 1.4f } })
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
            using (var b2 = new SolidBrush(Card))
            using (var pen = new Pen(CardEdge, 1f))
            {
                g.FillPath(b2, p2);
                g.DrawPath(pen, p2);
            }

            using (var p3 = RoundedRect(3f, 12.5f, 13f, 7.5f, 1.5f))
            using (var b3 = new SolidBrush(Card))
            using (var pen = new Pen(CardEdge, 1f))
            {
                g.FillPath(b3, p3);
                g.DrawPath(pen, p3);
            }
        }

        // Stroke Style：同一条曲线，上细下粗（读作"改笔宽/笔头"）
        static void DrawStrokeStyle(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Muted, 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
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
            using (var pen = new Pen(Screen, 1.2f) { DashPattern = new[] { 1.4f, 1.6f } })
            {
                g.DrawLine(pen, 7f, 15.5f, 17f, 8.5f);
            }
        }

        // Camera 数据：取景框 + 中心十字 + 圆（镜头）
        static void DrawDataCamera(Graphics g)
        {
            DataTile(g);
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
            DataTile(g);
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

        /* ---------------- v0.30：运行时表达式 ---------------- */

        // Var：标签牌 + 一个点 —— "给某个东西起个名字"
        static void DrawExprVar(Graphics g)
        {
            Tile24(g);
            var pts = new[]
            {
                new PointF(4.5f, 11.5f), new PointF(9.5f, 6.2f), new PointF(19.5f, 6.2f),
                new PointF(19.5f, 16.8f), new PointF(9.5f, 16.8f),
            };
            using (var pen = new Pen(Ink, 1.3f) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, pts);
            Dot(g, 9.5f, 11.5f, 1.5f, Pink);
            using (var pen = new Pen(Tint(Ink, 0x70), 1f))
                g.DrawLine(pen, 12.5f, 9.5f, 17f, 9.5f);
        }

        // Time：时钟。指针位置固定，不表达"几点"
        static void DrawExprTime(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.4f))
                g.DrawEllipse(pen, 4.5f, 4.5f, 15f, 15f);
            using (var pen = new Pen(Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 12f, 12f, 12f, 7.6f);
                g.DrawLine(pen, 12f, 12f, 15.6f, 13.4f);
            }
            Dot(g, 12f, 12f, 1.2f, Green);
        }

        // Math：加与乘，读作"把两个东西算起来"
        static void DrawExprMath(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 5.5f, 8.5f, 11.5f, 8.5f);
                g.DrawLine(pen, 8.5f, 5.5f, 8.5f, 11.5f);
            }
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 13.5f, 13.5f, 18.5f, 18.5f);
                g.DrawLine(pen, 18.5f, 13.5f, 13.5f, 18.5f);
            }
        }

        // Bind：两支笔画之间夹一个连接器 —— "把公式接到属性上"
        static void DrawBind(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 3.5f, 7f, 8.5f, 7f);
                g.DrawLine(pen, 15.5f, 17f, 20.5f, 17f);
            }
            using (var pen = new Pen(Pink, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 9f, 7f, 12f, 12f);
                g.DrawLine(pen, 12f, 12f, 15f, 17f);
            }
            Dot(g, 12f, 12f, 1.6f, Green);
        }

        // Event Rule：一道闪电，读作"触发"
        static void DrawEventRule(Graphics g)
        {
            Tile24(g);
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(Pink))
            {
                p.AddPolygon(new[]
                {
                    new PointF(13.5f, 3.5f), new PointF(6.5f, 13.2f), new PointF(11.2f, 13.2f),
                    new PointF(9.8f, 20.5f), new PointF(17.2f, 10.4f), new PointF(12.4f, 10.4f),
                });
                g.FillPath(b, p);
            }
        }

        // Remap：两个刻度之间拉一根箭头 —— "从一段区间搬到另一段"
        static void DrawExprRemap(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x90), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 5f, 15.5f, 5f, 8.5f);
                g.DrawLine(pen, 19f, 16.5f, 19f, 11f);
            }
            using (var pen = new Pen(Pink, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 7f, 12f, 17f, 12f);
                g.DrawLine(pen, 14.5f, 9.8f, 17f, 12f);
                g.DrawLine(pen, 14.5f, 14.2f, 17f, 12f);
            }
        }

        // Select：一条线分两岔，一条亮一条暗 —— "条件选边"
        static void DrawExprSelect(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, 4.5f, 12f, 9.5f, 12f);
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 9.5f, 12f, 15f, 7.5f);
                g.DrawLine(pen, 15f, 7.5f, 19.5f, 7.5f);
            }
            using (var pen = new Pen(Tint(Ink, 0x70), 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 9.5f, 12f, 15f, 16.5f);
                g.DrawLine(pen, 15f, 16.5f, 19.5f, 16.5f);
            }
        }

        // Data: Expression —— 卡面里一条正弦
        static void DrawDataExpr(Graphics g)
        {
            DataTile(g);
            DataFrame(g, Pink);
            using (var pen = new Pen(Ink, 1.3f) { LineJoin = LineJoin.Round })
            {
                var pts = new PointF[13];
                for (int i = 0; i < pts.Length; i++)
                {
                    float x = 5.5f + i * (13f / 12f);
                    float y = 12f - (float)(Math.Sin(i * 0.9) * 4.2);
                    pts[i] = new PointF(x, y);
                }
                g.DrawLines(pen, pts);
            }
            Dot(g, 5.5f, 12f, 1.2f, Green);
        }

        /* ---------------- v0.32：行为图四只 ---------------- */

        // Read State：三行状态 + 一条向外引出的箭头 —— "把实例里的数读出来"
        static void DrawReadState(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Ink, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 4.5f, 7.5f, 10.5f, 7.5f);
                g.DrawLine(pen, 4.5f, 12f, 9f, 12f);
                g.DrawLine(pen, 4.5f, 16.5f, 10.5f, 16.5f);
            }
            using (var pen = new Pen(Pink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 13f, 12f, 19.5f, 12f);
                g.DrawLine(pen, 16.8f, 9.6f, 19.5f, 12f);
                g.DrawLine(pen, 16.8f, 14.4f, 19.5f, 12f);
            }
        }

        // Write State：箭头指进三行状态 —— "把算出来的数写回去"
        static void DrawWriteState(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Pink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 4.5f, 12f, 10.5f, 12f);
                g.DrawLine(pen, 7.8f, 9.6f, 10.5f, 12f);
                g.DrawLine(pen, 7.8f, 14.4f, 10.5f, 12f);
            }
            using (var pen = new Pen(Ink, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 13.5f, 7.5f, 19.5f, 7.5f);
                g.DrawLine(pen, 13.5f, 12f, 18f, 12f);
                g.DrawLine(pen, 13.5f, 16.5f, 19.5f, 16.5f);
            }
        }

        // Spawn：一个父点分出两个子点 —— "一个变两个"
        static void DrawSpawn(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x90), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 12f, 9.4f, 7f, 15.5f);
                g.DrawLine(pen, 12f, 9.4f, 17f, 15.5f);
            }
            Dot(g, 12f, 7f, 2.6f, Ink);
            Dot(g, 6.6f, 17.2f, 1.7f, Pink);
            Dot(g, 17.4f, 17.2f, 1.7f, Pink);
        }

        // Destroy：一个实点向右下散成几个越来越淡的点 —— "消失，但留痕"
        static void DrawDestroy(Graphics g)
        {
            Tile24(g);
            Dot(g, 7.5f, 7.5f, 2.4f, Ink);
            Dot(g, 13f, 12f, 1.8f, Tint(Pink, 0xB0));
            Dot(g, 17f, 16.5f, 1.3f, Tint(Pink, 0x70));
            Dot(g, 19.5f, 20f, 0.9f, Tint(Pink, 0x40));
        }

        /* ---------------- v0.31：Mark 三只 ---------------- */

        // Brush：笔杆 + 实心笔尖 + 一点墨 —— "这是一枚印章"
        static void DrawBrush(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0xC0), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, 17.5f, 4.5f, 13f, 9f);

            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(Ink))
            {
                p.AddPolygon(new[]
                {
                    new PointF(12.4f, 8.4f), new PointF(15.8f, 11.8f),
                    new PointF(9.4f, 17.6f), new PointF(6.4f, 14.6f),
                });
                g.FillPath(b, p);
            }
            Dot(g, 5.4f, 18.8f, 1.7f, Pink);
        }

        // Mark Along Path：一条曲线 + 沿它排开的点，点越来越大 —— "同一路径的多次落笔"
        static void DrawMarkPath(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x70), 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawBezier(pen, 4.5f, 18.6f, 9f, 17.4f, 13f, 9.4f, 19.5f, 6.2f);

            Dot(g, 6f, 17.6f, 1.1f, Ink);
            Dot(g, 10f, 14.4f, 1.5f, Ink);
            Dot(g, 14.2f, 10.2f, 1.9f, Ink);
            Dot(g, 18.6f, 6.8f, 2.4f, Pink);
        }

        // Style Map：一排越来越大的点 + 一条基线 —— "沿路径把某个量从 A 推到 B"
        static void DrawStyleMap(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Tint(Ink, 0x50), 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, 4f, 19f, 20f, 19f);

            Dot(g, 6f, 15.4f, 1.2f, Ink);
            Dot(g, 11f, 12.6f, 2f, Ink);
            Dot(g, 17f, 8.4f, 3f, Pink);

            using (var pen = new Pen(Tint(Ink, 0x60), 1f) { DashPattern = new[] { 1.2f, 1.6f } })
                g.DrawLine(pen, 6f, 17.6f, 17f, 17.6f);
        }

        /// <summary>
        /// 数据图标的底：一张不透明的卡片。
        /// 关键是"不透明"—— 功能区底色是浅是深都不影响可读性，
        /// 卡片和字形之间的对比由 Card / Ink 这对令牌保证，不用赌宿主背景。
        /// </summary>
        static void DataTile(Graphics g)
        {
            using (var path = RoundedRect(1.5f, 1.5f, 21f, 21f, 3.5f))
            using (var b = new SolidBrush(Card))
                g.FillPath(b, path);
            using (var path = RoundedRect(1.5f, 1.5f, 21f, 21f, 3.5f))
            using (var pen = new Pen(CardEdge, 1f))
                g.DrawPath(pen, path);
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

        // Canvas：画布框 + 屏内一笔内容 + 绿色裁切角标
        // 屏里那笔是必需的：只有深色屏幕时，24px 下整只图标就是一块黑，认不出是"画布"。
        static void DrawCanvas(Graphics g)
        {
            Tile24(g);
            using (var inner = RoundedRect(4f, 6f, 16f, 12f, 1.5f))
            using (var b = new SolidBrush(Screen))
            using (var pen = new Pen(Tint(ScreenInk, 0x88), 1f))
            {
                g.FillPath(b, inner);
                g.DrawPath(pen, inner);
            }
            using (var pen = new Pen(Pink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawBezier(pen, 6.5f, 15f, 9f, 15f, 8f, 9f, 11.5f, 9f);
                g.DrawLine(pen, 11.5f, 9f, 17.5f, 13.5f);
            }
            Dot(g, 11.5f, 9f, 1.2f, Green);
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
            using (var pen = new Pen(Faint, 1f) { DashPattern = new[] { 1.2f, 1.4f } })
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
            using (var inner = RoundedRect(3.5f, 6f, 10f, 12f, 1.5f))
            using (var b = new SolidBrush(Screen))
            using (var pen = new Pen(Tint(ScreenInk, 0x88), 1f))
            {
                g.FillPath(b, inner);
                g.DrawPath(pen, inner);
            }
            Curve(g, ScreenInk, 1.2f);

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

        /* ---------------- 阶段 5：3D ---------------- */

        /// <summary>Isometric cube: three visible faces, drawn as one open polyline set.</summary>
        static void IsoCube(Graphics g, Pen pen, float cx, float cy, float r)
        {
            float w = r * 0.866f;
            var top = new PointF(cx, cy - r);
            var left = new PointF(cx - w, cy - r * 0.5f);
            var right = new PointF(cx + w, cy - r * 0.5f);
            var mid = new PointF(cx, cy);
            var bl = new PointF(cx - w, cy + r * 0.5f);
            var br = new PointF(cx + w, cy + r * 0.5f);
            var bottom = new PointF(cx, cy + r);

            g.DrawPolygon(pen, new[] { top, right, mid, left });
            g.DrawLines(pen, new[] { left, bl, bottom, br, right });
            g.DrawLine(pen, mid, bottom);
        }

        // Mesh Input: grid into a triangle, an arrow feeding it
        static void DrawMeshInput(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0xB0, Ink), 1.1f) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, new[]
                {
                    new PointF(9f, 9f), new PointF(19f, 9f),
                    new PointF(19f, 19f), new PointF(9f, 19f)
                });
                g.DrawLine(pen, 9f, 9f, 19f, 19f);
                g.DrawLine(pen, 19f, 9f, 9f, 19f);
            }
            using (var pen = new Pen(Green, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, 3.5f, 14f, 7.5f, 14f);
                g.DrawLine(pen, 5.5f, 12.2f, 7.7f, 14f);
                g.DrawLine(pen, 5.5f, 15.8f, 7.7f, 14f);
            }
        }

        // Transform 3D: cube plus a rotation arc
        static void DrawTransform3D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0xC8, Ink), 1.1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 11.5f, 12.5f, 6.4f);
            using (var pen = new Pen(Pink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, 2.5f, 3f, 17f, 15f, 200f, 110f);
                g.DrawLine(pen, 18.6f, 5.4f, 19.4f, 7.6f);
                g.DrawLine(pen, 18.6f, 5.4f, 16.4f, 5.9f);
            }
        }

        // Draw 3D: cube half filled, the filled half being the "painted" part
        static void DrawDraw3D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0x90, Ink), 1.1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 11.5f, 12f, 6.6f);
            using (var b = new SolidBrush(Color.FromArgb(0x88, Pink)))
            {
                g.FillPolygon(b, new[]
                {
                    new PointF(11.5f, 12f), new PointF(17.2f, 8.7f),
                    new PointF(17.2f, 15.3f), new PointF(11.5f, 18.6f)
                });
            }
            using (var pen = new Pen(Pink, 1.2f) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, new[]
                {
                    new PointF(11.5f, 12f), new PointF(17.2f, 8.7f),
                    new PointF(17.2f, 15.3f), new PointF(11.5f, 18.6f)
                });
        }

        // Camera 3D: lens plus a view frustum
        static void DrawCamera3D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0xC0, Ink), 1.2f) { LineJoin = LineJoin.Round })
            {
                g.DrawRectangle(pen, 3f, 8.5f, 5.5f, 7f);
                g.DrawPolygon(pen, new[]
                {
                    new PointF(8.5f, 12f), new PointF(15f, 7.5f),
                    new PointF(15f, 16.5f), new PointF(8.5f, 12f)
                });
            }
            Dot(g, 5.7f, 12f, 1.4f, Green);
            using (var pen = new Pen(Color.FromArgb(0x70, Green), 1f) { DashStyle = DashStyle.Dot })
                g.DrawLine(pen, 15f, 7.5f, 20f, 5f);
        }

        // Scene 3D: two cubes on a ground line
        static void DrawScene3D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Color.FromArgb(0xC8, Ink), 1.1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 9f, 10f, 5f);
            using (var pen = new Pen(Color.FromArgb(0x88, Pink), 1.1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 16f, 14f, 4.2f);
            using (var pen = new Pen(Color.FromArgb(0x60, Ink), 1f))
                g.DrawLine(pen, 3f, 19.5f, 21f, 19.5f);
        }

        // Data: mesh — thin frame holding a triangulated patch
        static void DrawDataMesh(Graphics g)
        {
            DataTile(g);
            using (var pen = new Pen(Color.FromArgb(0xE0, Ink), 1.2f) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, new[]
                {
                    new PointF(6f, 8f), new PointF(18f, 7f),
                    new PointF(17f, 17f), new PointF(6.5f, 16f)
                });
                g.DrawLine(pen, 6f, 8f, 17f, 17f);
                g.DrawLine(pen, 18f, 7f, 6.5f, 16f);
            }
        }

        // Data: cmd3d — thin frame with a small cube
        static void DrawDataCmd3D(Graphics g)
        {
            DataTile(g);
            using (var pen = new Pen(Color.FromArgb(0xE0, Ink), 1.1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 12f, 12f, 5.2f);
        }

        // Data: camera3d — thin frame with a frustum
        static void DrawDataCamera3D(Graphics g)
        {
            DataTile(g);
            using (var pen = new Pen(Color.FromArgb(0xE0, Ink), 1.1f) { LineJoin = LineJoin.Round })
                g.DrawPolygon(pen, new[]
                {
                    new PointF(5f, 8f), new PointF(18f, 5.5f),
                    new PointF(18f, 18.5f), new PointF(5f, 16f)
                });
            Dot(g, 6.2f, 12f, 1.3f, Pink);
        }

        // Data: scene3d — thin frame with two cubes
        static void DrawDataScene3D(Graphics g)
        {
            DataTile(g);
            using (var pen = new Pen(Color.FromArgb(0xD0, Ink), 1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 9f, 10.5f, 4.2f);
            using (var pen = new Pen(Color.FromArgb(0xD0, Pink), 1f) { LineJoin = LineJoin.Round })
                IsoCube(g, pen, 15.5f, 14.5f, 3.4f);
        }

        // Point 3D: a loose cloud, deliberately unaligned — it is not a mesh
        static void DrawPoint3D(Graphics g)
        {
            Tile24(g);
            var pts = new[]
            {
                new PointF(6f, 8f), new PointF(11f, 5.6f), new PointF(16.5f, 8.4f),
                new PointF(8.5f, 13f), new PointF(14f, 12.2f), new PointF(18.5f, 15f),
                new PointF(7f, 17.5f), new PointF(12.5f, 18f),
            };
            for (int i = 0; i < pts.Length; i++)
            {
                var c = i % 4 == 0 ? Green : Color.FromArgb(0xD0, Ink);
                Dot(g, pts[i].X, pts[i].Y, i % 4 == 0 ? 1.7f : 1.3f, c);
            }
        }

        // Curve 3D: a curve in space with its sample dots
        static void DrawCurve3D(Graphics g)
        {
            Tile24(g);
            using (var pen = new Pen(Pink, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawBezier(pen, 4f, 18.5f, 8f, 6f, 16f, 19f, 20f, 5.5f);
            using (var b = new SolidBrush(Color.FromArgb(0xC0, Ink)))
            {
                g.FillEllipse(b, 6.4f, 12.2f, 1.8f, 1.8f);
                g.FillEllipse(b, 11.4f, 12.0f, 1.8f, 1.8f);
                g.FillEllipse(b, 16.2f, 12.6f, 1.8f, 1.8f);
            }
        }

        // Draw Points: a cloud with one highlighted, i.e. the drawn result of a point set
        static void DrawDrawPoints(Graphics g)
        {
            Tile24(g);
            var pts = new[]
            {
                new PointF(7f, 9f), new PointF(13f, 7f), new PointF(17f, 11f),
                new PointF(9f, 15f), new PointF(14.5f, 17f),
            };
            foreach (var p in pts)
                Dot(g, p.X, p.Y, 1.5f, Color.FromArgb(0xA0, Ink));

            using (var b = new SolidBrush(Pink)) g.FillEllipse(b, 5f, 11f, 4.2f, 4.2f);
            using (var pen = new Pen(Color.FromArgb(0x90, Pink), 1f))
                g.DrawEllipse(pen, 3.8f, 9.8f, 6.6f, 6.6f);
        }
    }
}
