using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using RH = Rhino.Geometry;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Transform —— 指令 + 每个指令自己的 Xform，一一对应。
    ///
    /// 语义（这是本电池唯一的语义）：
    ///   Cmds 里第 i 条指令，配 Xform 里第 i 个 Transform。
    ///   运行时拿到后**自己把这个变换演出来**：从它原来的位置/角度/缩放，
    ///   在 duration 秒内变到 Xform 给出的状态。
    ///   所以没有任何 progress 输入 —— 变换本身**就是**那段动画。
    ///
    /// Xform 给 1 个 = 所有指令共用同一个；给 N 个 = 按序对应（多出来的指令沿用最后一个）。
    ///
    /// 坐标系说明（这是本电池存在的唯一理由）：
    ///   Xform 定义在 **Rhino 世界坐标**里，而运行时画在 **页面坐标**里。
    ///   两者之间隔着 Path Input 的相机映射 P：px = s*X + ox, py = -s*Y + oy。
    ///   所以世界里的 M 要在页面上等价成立，必须是 P·M·P⁻¹ —— 不是 F·M·F⁻¹（只翻 Y）。
    ///   只翻 Y 的版本会把"绕几何中心转"变成"绕页面原点转"，锚点偏出去几百像素，
    ///   这就是你在画布上看到的"位置不对"。
    /// </summary>
    public class TransformComponent : GH_Component
    {
        public TransformComponent()
            : base("Transform", "PSR", "给每条指令配一个 Xform；浏览器把它们之间的变化演出来", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ad");
        protected override Bitmap Icon => Icons.Transform;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "要变换的指令", GH_ParamAccess.list);
            p.AddParameter(new Grasshopper.Kernel.Parameters.Param_Transform(), "Xform", "X",
                "每个指令对应的 Transform（旋转/缩放/位移在里面合成好）。给 1 个则全部共用，给 N 个按序对应",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Command(), "Commands", "Cmds", "带变换的指令", GH_ParamAccess.list);
        }

        /// <summary>
        /// 纯数值：世界仿射 → 页面仿射（Canvas 2D 语义 [a,b,c,d,e,f]，x' = a x + c y + e）。
        ///
        /// 世界仿射写作 X' = a·X + b·Y + tx，Y' = c·X + d·Y + ty。
        /// 相机 P：px = s·X + ox，py = -s·Y + oy。于是 P·M·P⁻¹ 展开为：
        ///
        ///   px' =  a·px − b·py + (−a·ox + b·oy + s·tx + ox)
        ///   py' = −c·px + d·py + ( c·ox − d·oy − s·ty + oy)
        ///
        /// 把页面中心代进去可验证它是不动点 —— 这就是"绕自己中心转"落得住的充要条件。
        /// 故意不碰任何 Rhino 类型：Transform / Point3d 在 Rhino 进程外不可信（会抛 rhcommon_c
        /// 或给出半初始化的垃圾值），而这个函数必须能被离线数值验证。
        /// </summary>
        internal static double[] ComposePageMatrix(
            double a, double b, double c, double d, double tx, double ty, double[] proj)
        {
            if (proj == null || proj.Length < 3)
            {
                // 没有相机信息（例如 Path 来自别处）：只能纠正 Y 轴朝向，锚点无法校正
                return new[] { a, -c, -b, d, tx, -ty };
            }

            double s = proj[0], ox = proj[1], oy = proj[2];
            return new[]
            {
                a,
                -c,
                -b,
                d,
                -a * ox + b * oy + s * tx + ox,
                c * ox - d * oy - s * ty + oy,
            };
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Command>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0) return;

            var xforms = new List<RH.Transform>();
            if (!DA.GetDataList(1, xforms) || xforms.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "缺少 Xform，指令原样输出");
                DA.SetDataList(0, cmds);
                return;
            }

            // 1 个 xform 全体共用；N 个按序对应；不够的沿用最后一个（GH 的 longest-list 习惯）
            if (xforms.Count > 1 && xforms.Count != cmds.Count)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{cmds.Count} 条指令 × {xforms.Count} 个 Xform，按序对应，多出来的指令沿用最后一个");

            var outCmds = new List<GHCE_Command>(cmds.Count);
            int noProj = 0;
            double lastRot = 0, lastK = 1;

            for (int i = 0; i < cmds.Count; i++)
            {
                var src = cmds[i]?.Value;
                if (src == null) continue;

                var xf = xforms[Math.Min(i, xforms.Count - 1)];
                if (xf.IsZero)
                {
                    outCmds.Add(new GHCE_Command(src.Duplicate()));
                    continue;
                }

                // 用三个基点的像反推世界仿射分量，不依赖 Rhino 的矩阵行列布局约定
                var p0 = new RH.Point3d(0, 0, 0); p0.Transform(xf);
                var ex = new RH.Point3d(1, 0, 0); ex.Transform(xf);
                var ey = new RH.Point3d(0, 1, 0); ey.Transform(xf);

                double a = ex.X - p0.X, b = ey.X - p0.X;
                double c = ex.Y - p0.Y, d = ey.Y - p0.Y;
                double tx = p0.X, ty = p0.Y;

                var proj = src.Path?.Proj;
                if (proj == null) noProj++;

                var copy = src.Duplicate();
                copy.Matrix = ComposePageMatrix(a, b, c, d, tx, ty, proj);
                // 终态就是 1；"从原位变过去"由运行时的 tween 负责，GH 不逐帧发包
                copy.Progress = 1.0;
                outCmds.Add(new GHCE_Command(copy));

                double det = a * d - b * c;
                lastRot = Math.Atan2(c, a) * 180.0 / Math.PI;
                lastK = Math.Sqrt(Math.Abs(det));
            }

            if (noProj > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{noProj} 条指令没有相机信息，只能纠 Y 轴朝向，绕中心旋转的锚点会偏。" +
                    "指令的 Path 来自 Path Input 时会自带相机参数。");
            }

            // Message 只给能判读的信息：旋转 / 缩放比矩阵六个数有用得多
            Message = (xforms.Count > 1 ? $"{xforms.Count} Xform · " : "")
                      + $"转 {lastRot:0.#}° · 缩放 {lastK:0.###}";

            DA.SetDataList(0, outCmds);
        }
    }
}
