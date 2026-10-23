using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using GHCE.Types;

namespace GHCE.Components
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
                   "GHCE", "3D") { }

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
            p.AddParameter(new Param_GHCE_Camera3D(), "Camera", "Cam", "Camera for Scene 3D", GH_ParamAccess.item);
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

            DA.SetData(0, new GHCE_Camera3D(def));
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
            : base("Scene 3D", "Scene3D", "Collects 3D draw commands and a camera into one scene", "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a5");
        protected override Bitmap Icon => Icons.Scene3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Cmd3D(), "Commands", "Cmds", "From Draw 3D", GH_ParamAccess.list);
            p.AddParameter(new Param_GHCE_Camera3D(), "Camera", "Cam", "Optional. Empty means auto frame",
                GH_ParamAccess.item);
            p.AddColourParameter("Background", "Bg", "Scene background", GH_ParamAccess.item,
                Color.FromArgb(7, 7, 10));
            p.AddBooleanParameter("Lights", "Lit", "Built-in three-point light rig", GH_ParamAccess.item, true);

            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Scene3D(), "Scene", "S", "Feed into Render Output", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var cmds = new List<GHCE_Cmd3D>();
            if (!DA.GetDataList(0, cmds) || cmds.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No 3D commands");
                return;
            }

            var cam = new GHCE_Camera3D();
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
            DA.SetData(0, new GHCE_Scene3D(scene));
        }
    }
}
