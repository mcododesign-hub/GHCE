using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Colugo.Types;

namespace Colugo.Components
{
    /// <summary>
    /// The 3D camera. Every input is optional on purpose: leave Position and Target
    /// unwired and the runtime frames the whole scene by its bounding box, keeping the
    /// bounding sphere centred. That is the default because it is what you want 90% of
    /// the time — an explicit camera is for when you need a specific angle.
    /// </summary>
    public class Camera3DComponent : GH_Component
    {
        public Camera3DComponent()
            : base("Camera 3D", "Cam3D", "Camera for the 3D scene. Leave empty to frame the scene automatically",
                   "Colugo", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a4");
        protected override Bitmap Icon => Icons.Camera3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Position", "Pos", "Camera position in world space. Empty means auto frame",
                GH_ParamAccess.item);
            p.AddPointParameter("Target", "Tgt", "Look-at point. Empty means the scene centre",
                GH_ParamAccess.item);
            p.AddNumberParameter("Fov", "Fov", "Vertical field of view in degrees (perspective only)",
                GH_ParamAccess.item, 45.0);
            p.AddIntegerParameter("Projection", "Proj", "1 perspective · 2 orthographic", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Zoom", "Zoom", "Orthographic zoom factor", GH_ParamAccess.item, 1.0);

            for (int i = 0; i < 5; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Camera3D(), "Camera", "Cam", "Camera for Scene 3D", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var pos = Point3d.Unset;
            var tgt = Point3d.Unset;
            double fov = 45, zoom = 1;
            int proj = 1;

            DA.GetData(0, ref pos);
            DA.GetData(1, ref tgt);
            DA.GetData(2, ref fov);
            DA.GetData(3, ref proj);
            DA.GetData(4, ref zoom);

            var def = new Camera3DDef
            {
                HasPosition = pos.IsValid,
                Px = pos.IsValid ? pos.X : 0,
                Py = pos.IsValid ? pos.Y : 0,
                Pz = pos.IsValid ? pos.Z : 0,
                HasTarget = tgt.IsValid,
                Tx = tgt.IsValid ? tgt.X : 0,
                Ty = tgt.IsValid ? tgt.Y : 0,
                Tz = tgt.IsValid ? tgt.Z : 0,
                Fov = Math.Clamp(fov, 5, 150),
                Orthographic = proj == 2,
                Zoom = zoom <= 0 ? 1 : zoom,
            };

            Message = def.HasPosition
                ? $"explicit · {def.Fov:0.#}°"
                : "auto frame";

            DA.SetData(0, new Colugo_Camera3D(def));
        }
    }

    /// <summary>
    /// Collects 3D draw commands plus a camera into one scene.
    /// This is the 3D counterpart of Render Output's Commands input — the thing that
    /// finally gets pushed to the browser.
    /// </summary>
    public class Scene3DComponent : GH_Component
    {
        public Scene3DComponent()
            : base("Scene 3D", "Scene3D", "Collects 3D draw commands and a camera into one scene", "Colugo", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a5");
        protected override Bitmap Icon => Icons.Scene3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Cmd3D(), "Commands", "Cmds", "From Draw 3D", GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_Camera3D(), "Camera", "Cam", "Optional. Empty means auto frame",
                GH_ParamAccess.item);
            p.AddColourParameter("Background", "Bg", "Scene background", GH_ParamAccess.item,
                Color.FromArgb(7, 7, 10));
            p.AddBooleanParameter("Lights", "Lit", "Built-in three-point light rig", GH_ParamAccess.item, true);

            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Scene3D(), "Scene", "S", "Feed into Render Output", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<Colugo_Cmd3D>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No 3D commands");
                return;
            }

            var cam = new Colugo_Camera3D();
            Color bg = Color.FromArgb(7, 7, 10);
            bool lights = true;
            DA.GetData(1, ref cam);
            DA.GetData(2, ref bg);
            DA.GetData(3, ref lights);

            var scene = new Scene3DDef
            {
                Camera = cam?.Value,
                Background = $"#{bg.R:x2}{bg.G:x2}{bg.B:x2}",
                Lights = lights,
            };

            int tris = 0;
            foreach (var c in cmds)
            {
                if (c?.Value == null) continue;
                scene.Cmds.Add(c.Value);
                tris += c.Value.Mesh?.TriCount ?? 0;
            }

            if (scene.Cmds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "All commands were empty");
                return;
            }

            Message = $"{scene.Cmds.Count} objects · {tris} tri · {(scene.Camera == null ? "auto" : "camera")}";
            DA.SetData(0, new Colugo_Scene3D(scene));
        }
    }

    /// <summary>
    /// S5：给 Scene 3D 挂后处理。
    /// 做成独立电池而不是给 Scene 3D 加三个端口 —— 旧定义一个字都不用改，
    /// 也不用把 4 个输入变成 7 个（那会顶到"≤6"的约定）。
    /// </summary>
    public class PostFXComponent : GH_Component
    {
        public PostFXComponent()
            : base("Post FX", "FX", "Add bloom to a 3D scene", "Colugo", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000dd");
        protected override Bitmap Icon => Icons.PostFX;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Scene3D(), "Scene", "S", "Scene from Scene 3D", GH_ParamAccess.item);
            p.AddNumberParameter("Bloom", "B", "Glow strength. 0 = off, 0.5-1.5 is the useful range",
                GH_ParamAccess.item, 0.8);
            p.AddNumberParameter("Threshold", "T", "Luminance above which pixels glow, 0-1",
                GH_ParamAccess.item, 0.55);
            p.AddNumberParameter("Radius", "R", "Blur radius multiplier", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddParameter(new Param_Colugo_Scene3D(), "Scene", "S",
                "Scene carrying post FX, feed into Render Output", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Colugo_Scene3D goo = null;
            if (!DA.GetData(0, ref goo) || goo?.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Scene");
                return;
            }
            double bloom = 0.8, threshold = 0.55, radius = 1.0;
            DA.GetData(1, ref bloom);
            DA.GetData(2, ref threshold);
            DA.GetData(3, ref radius);
            if (bloom <= 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Bloom = 0，后处理关闭，走原来的直接渲染");

            var s = goo.Value.Duplicate();
            s.Bloom = Math.Clamp(bloom, 0, 4);
            s.BloomThreshold = Math.Clamp(threshold, 0, 1);
            s.BloomRadius = Math.Clamp(radius, 0.1, 8);
            DA.SetData(0, new Colugo_Scene3D(s));
        }
    }

}
