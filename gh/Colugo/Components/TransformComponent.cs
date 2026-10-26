using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using RH = Rhino.Geometry;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// Transform —— 几何层的仿射变形，与 Morph 同级。
    ///
    /// ```
    /// Paths ──► Transform(Xform × N) ──► Paths(带矩阵帧序列) ──► Draw Path
    /// ```
    ///
    /// 为什么输出 Paths 而不是 Commands：
    ///   Transform 只回答"这条几何在时间上怎么变"，不关心它最后是粉色虚线还是绿色实线。
    ///   样式是 Draw Path 的事。放在几何层才能和 Morph 并列，也才能串在同一条链上。
    ///
    /// 一个 Xform = 静态矩阵（走 transform.matrix，不建轨道）
    /// 多个 Xform = 帧序列（走 track.frames，由 Track 给区间与缓动）
    ///
    /// 坐标：Xform 定义在 Rhino 世界坐标里，而 Path 已经是页面坐标，
    /// 所以每个矩阵都要做 P·M·P⁻¹（P = 该 Path 的相机参数）。少了这步锚点会飞掉。
    /// </summary>
    public class TransformComponent : GH_Component
    {
        public TransformComponent()
            : base("Transform", "PSR", "Affine deformation at the geometry level. One Xform is static, many form a frame sequence",
                   "Colugo", "Animation") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000ad");
        protected override Bitmap Icon => Icons.Transform;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Path(), "Paths", "Paths", "Paths from Path Input or Morph", GH_ParamAccess.list);

            var xf = new Grasshopper.Kernel.Parameters.Param_Transform
            {
                Name = "Xform",
                NickName = "X",
                Description = "1 Xform is a static matrix; N Xforms form a frame sequence in time order",
                Access = GH_ParamAccess.list,
                Optional = true,
            };
            p.AddParameter(xf);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Path(), "Paths", "Paths", "Paths carrying a matrix, feed into Draw Path", GH_ParamAccess.list);
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
        /// 故意不碰任何 Rhino 类型：Transform / Point3d 在 Rhino 进程外不可信，
        /// 而这个函数必须能被离线数值验证。
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
            var paths = new List<Colugo_Path>();
            var frames = new List<RH.Transform>();

            if (!DA.GetDataList(0, paths) || paths.Count == 0) return;
            DA.GetDataList(1, frames);

            if (frames.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Xform, paths pass through unchanged");
                DA.SetDataList(0, paths);
                return;
            }

            // 每个 Xform 先在世界坐标里解成仿射分量，再逐 Path 投影（各 Path 的相机可能不同）
            int n = frames.Count;
            var world = new double[n][];
            for (int k = 0; k < n; k++)
            {
                var p0 = new RH.Point3d(0, 0, 0); p0.Transform(frames[k]);
                var ex = new RH.Point3d(1, 0, 0); ex.Transform(frames[k]);
                var ey = new RH.Point3d(0, 1, 0); ey.Transform(frames[k]);
                world[k] = new[]
                {
                    ex.X - p0.X, ey.X - p0.X,     // a, b
                    ex.Y - p0.Y, ey.Y - p0.Y,     // c, d
                    p0.X, p0.Y,                   // tx, ty
                };
            }

            var outPaths = new List<Colugo_Path>(paths.Count);
            foreach (var gp in paths)
            {
                var src = gp?.Value;
                if (src == null) continue;

                var copy = src.Duplicate();
                var proj = src.Proj;

                if (n == 1)
                {
                    copy.Matrix = ComposePageMatrix(
                        world[0][0], world[0][1], world[0][2], world[0][3], world[0][4], world[0][5], proj);
                    copy.MatrixFrames = null;
                    copy.MatrixN = 0;
                }
                else
                {
                    var flat = new double[n * 6];
                    for (int k = 0; k < n; k++)
                    {
                        var m = ComposePageMatrix(
                            world[k][0], world[k][1], world[k][2], world[k][3], world[k][4], world[k][5], proj);
                        for (int i = 0; i < 6; i++) flat[k * 6 + i] = m[i];
                    }
                    copy.MatrixFrames = flat;
                    copy.MatrixN = n;
                    copy.Matrix = null;
                }

                outPaths.Add(new Colugo_Path(copy));
            }

            // Message 只给能判读的信息：分解出的旋转/缩放比六个数字有用得多
            double det = world[0][0] * world[0][3] - world[0][1] * world[0][2];
            double rot = Math.Atan2(world[0][2], world[0][0]) * 180.0 / Math.PI;
            double k2 = Math.Sqrt(Math.Abs(det));
            Message = n == 1
                ? $"static · rotation {rot:0.#}° · scale {k2:0.###}"
                : $"{n} frames · rotation {rot:0.#}° to {Math.Atan2(world[n - 1][2], world[n - 1][0]) * 180.0 / Math.PI:0.#}°";

            DA.SetDataList(0, outPaths);
        }
    }
}
