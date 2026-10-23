using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;
using GHCE.Types;

namespace GHCE.Components
{
    /// <summary>
    /// Project to 2D —— 把"世界 → 页面"这一步从 Path Input 里抽出来，做成可复用的相机。
    ///
    /// 为什么必须抽出来：相机原本硬编码在 Path Input 里，几何一改就重算，
    /// 于是"细长条转 90°"时相机不会跟着变宽，直接出框被裁（V0.9 的已知边界）。
    /// 相机独立成对象后，可以用整个动画过程的外接框（Bounds 端口）框一次，
    /// 之后几何在框里怎么转都不会被裁。
    ///
    /// 数学在 <see cref="CameraMath"/>，纯 double，可以不开 Rhino 验。
    /// </summary>
    public class ProjectTo2DComponent : GH_Component
    {
        public ProjectTo2DComponent()
            : base("Project to 2D", "Cam", "Curve bounds to camera: world space to page space", "GHCE", "Drawing") { }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000e1");
        protected override Bitmap Icon => Icons.ProjectTo2D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter("Geometry", "Geo", "Curves included in the fit. Usually every curve in the artwork", GH_ParamAccess.list);
            p.AddIntegerParameter("Page Width", "W", "Page width in pixels", GH_ParamAccess.item, 1440);
            p.AddIntegerParameter("Page Height", "H", "Page height in pixels", GH_ParamAccess.item, 900);
            p.AddNumberParameter("Margin", "M", "Page margin in pixels", GH_ParamAccess.item, 60.0);
            p.AddNumberParameter("Scale", "S", "0 = fit to the bounding box; greater than 0 = fixed scale shared by the whole animation", GH_ParamAccess.item, 0.0);
            p.AddParameter(new Param_Rectangle(), "Bounds", "Bounds",
                "Optional explicit world bounds. Use the extent of the entire animation so the camera does not drift with single frames", GH_ParamAccess.item);
            p[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Camera(), "Camera", "Cam", "Camera for the Camera input of Path Input", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Fit summary", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var geo = new List<Curve>();
            int pw = 1440, ph = 900;
            double margin = 60, scale = 0;

            DA.GetDataList(0, geo);
            DA.GetData(1, ref pw);
            DA.GetData(2, ref ph);
            DA.GetData(3, ref margin);
            DA.GetData(4, ref scale);

            if (pw <= 0 || ph <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Page size must be positive");
                return;
            }

            // ---- 包围盒：显式 Bounds 优先，否则所有曲线的联合包围盒 ----
            double[] box = null;
            bool fromBounds = false;

            var rect = Rectangle3d.Unset;
            if (DA.GetData(5, ref rect) && rect.IsValid)
            {
                box = new[] { rect.Corner(0).X, rect.Corner(0).Y, rect.Corner(2).X, rect.Corner(2).Y };
                if (box[0] > box[2]) { var t = box[0]; box[0] = box[2]; box[2] = t; }
                if (box[1] > box[3]) { var t = box[1]; box[1] = box[3]; box[3] = t; }
                fromBounds = true;
            }

            if (box == null)
            {
                var acc = CameraMath.EmptyBox();
                int used = 0;
                foreach (var c in geo)
                {
                    if (c == null || !c.IsValid) continue;
                    var bb = c.GetBoundingBox(true);
                    if (!bb.IsValid) continue;
                    CameraMath.Extend(acc, bb.Min.X, bb.Min.Y);
                    CameraMath.Extend(acc, bb.Max.X, bb.Max.Y);
                    used++;
                }
                if (CameraMath.IsEmpty(acc))
                {
                    // 不接几何也允许：这时它就是一台"按页面尺寸 + 单位框"的相机。
                    // 用 Remark 而不是 Warning —— 否则"只想定页面尺寸"的用法会一直挂着黄标。
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "No geometry and no Bounds: fitting to a 0..1 unit box. Connect Bounds or all curves to lock the frame");
                    box = new double[] { 0, 0, 1, 1 };
                }
                else
                {
                    box = acc;
                }
            }

            double[] proj = CameraMath.Fit(box, pw, ph, margin, scale);
            var def = new CameraDef { W = pw, H = ph, Margin = margin, Scale = scale };
            var cam = def.Solved(proj, box);

            DA.SetData(0, new GHCE_Camera(cam));

            string mode = scale > 0 ? "fixed" : "fit";
            DA.SetData(1, $"{pw}×{ph} · s {proj[0]:0.###} · {mode}"
                + (fromBounds ? " · bounds" : "")
                + $" · bbox ({box[0]:0.#},{box[1]:0.#})–({box[2]:0.#},{box[3]:0.#})");
        }
    }
}
