using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;
using GHCE.Types;
using RH = Rhino.Geometry;

namespace GHCE.Components
{
    /// <summary>
    /// Points straight to a draw command, skipping Point 3D.
    ///
    /// Point 3D exists so points can go through Transform 3D; this one is for the common case
    /// where you just want them drawn. Two components instead of one, because a battery that
    /// both converts and styles has to guess which of the two you meant.
    /// </summary>
    public class DrawPointsComponent : GH_Component
    {
        public DrawPointsComponent()
            : base("Draw Points", "DrawPts", "Points with a colour and size, forming a 3D draw command",
                   "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a8");
        protected override Bitmap Icon => Icons.DrawPoints;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Points", "P", "Points in world space", GH_ParamAccess.list);
            p.AddColourParameter("Colour", "C", "Point colour", GH_ParamAccess.item, Color.FromArgb(240, 180, 41));
            p.AddNumberParameter("Size", "Size", "Point diameter in pixels", GH_ParamAccess.item, 2.5);
            p.AddNumberParameter("Opacity", "A", "0-1", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Cmd3D(), "Commands", "Cmds", "3D draw commands, feed into Scene 3D",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var pts = new List<Point3d>();
            if (!DA.GetDataList(0, pts) || pts.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No points");
                return;
            }

            Color colour = Color.FromArgb(240, 180, 41);
            double size = 2.5, opacity = 1;
            DA.GetData(1, ref colour);
            DA.GetData(2, ref size);
            DA.GetData(3, ref opacity);

            if (size <= 0) size = 1;
            opacity = Math.Clamp(opacity, 0, 1);

            var pos = new double[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++)
            {
                pos[i * 3] = pts[i].X;
                pos[i * 3 + 1] = pts[i].Y;
                pos[i * 3 + 2] = pts[i].Z;
            }

            var def = new MeshDef
            {
                Id = "p_" + MeshInputComponent.Fingerprint(pos),
                Kind = "points",
                Positions = pos,
                Indices = null,
            };

            var cmd = new Cmd3DDef
            {
                Id = "c3_" + def.Id,
                MeshId = def.Id,
                Mesh = def,
                Xform = new Transform3DDef(),
                Material = new Material3DDef
                {
                    Mode = 3,
                    Color = $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}",
                    Size = size,
                    Opacity = opacity,
                },
            };

            Message = $"{pts.Count} points · {size:0.#}px";
            DA.SetDataList(0, new[] { new GHCE_Cmd3D(cmd) });
        }
    }

    /// <summary>
    /// Points to a 3D point cloud. Same vertex buffer as a mesh, just without indices —
    /// the runtime builds a Points object and reads Size as pixel diameter.
    /// </summary>
    public class Point3DComponent : GH_Component
    {
        public Point3DComponent()
            : base("Point 3D", "Point3D", "Rhino points to a 3D point cloud", "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a6");
        protected override Bitmap Icon => Icons.Point3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Points", "P", "Points in world space", GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Objects", "O", "Point cloud, feed into Draw 3D",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var pts = new List<Point3d>();
            if (!DA.GetDataList(0, pts) || pts.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No points");
                return;
            }

            var pos = new double[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++)
            {
                pos[i * 3] = pts[i].X;
                pos[i * 3 + 1] = pts[i].Y;
                pos[i * 3 + 2] = pts[i].Z;
            }

            var def = new MeshDef
            {
                Id = "p_" + MeshInputComponent.Fingerprint(pos),
                Kind = "points",
                Positions = pos,
                Indices = null,
            };

            Message = $"{pts.Count} points";
            DA.SetData(0, new GHCE_Mesh(def));
        }
    }

    /// <summary>
    /// Curves to 3D lines. Each curve is sampled into a polyline and turned into index
    /// pairs; several curves share one buffer so a whole wiring diagram is one object.
    ///
    /// Sampling by normalised length rather than by count keeps the spacing even on curves
    /// whose parameterisation is uneven (most nurbs curves are).
    /// </summary>
    public class Curve3DComponent : GH_Component
    {
        public Curve3DComponent()
            : base("Curve 3D", "Curve3D", "Rhino curves to 3D lines", "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a7");
        protected override Bitmap Icon => Icons.Curve3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Curve
            {
                Name = "Curves",
                NickName = "C",
                Description = "Curves in world space. Closed curves are joined back to the start",
                Access = GH_ParamAccess.list,
            });
            p.AddIntegerParameter("Samples", "N", "Points per curve, 2-4096", GH_ParamAccess.item, 64);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Objects", "O", "Lines, feed into Draw 3D",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var curves = new List<Curve>();
            if (!DA.GetDataList(0, curves) || curves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No curves");
                return;
            }

            int samples = 64;
            DA.GetData(1, ref samples);
            samples = Math.Clamp(samples, 2, 4096);

            // 每条曲线一个对象，不合并。
            // 合并成一个的话，整组只能共用一个样式，而且在 Rhino 里也看不出"这是几根"。
            // 一条一条来，Draw 3D 的输出就能和输入一一对应。
            var outObjs = new List<GHCE_Mesh>(curves.Count);
            int totalSegments = 0;
            int skipped = 0;

            foreach (var c in curves)
            {
                if (c == null || !c.IsValid) { skipped++; continue; }

                bool closed = c.IsClosed;
                var pos = new List<double>();
                var idx = new List<int>();

                for (int i = 0; i < samples; i++)
                {
                    double t = closed ? (double)i / samples : (double)i / (samples - 1);
                    var p = c.PointAtNormalizedLength(t);
                    if (!p.IsValid) { pos.Add(0); pos.Add(0); pos.Add(0); continue; }
                    pos.Add(p.X);
                    pos.Add(p.Y);
                    pos.Add(p.Z);
                }

                for (int i = 0; i < samples - 1; i++)
                {
                    idx.Add(i);
                    idx.Add(i + 1);
                }
                if (closed)
                {
                    idx.Add(samples - 1);
                    idx.Add(0);
                }

                var posArray = pos.ToArray();
                outObjs.Add(new GHCE_Mesh(new MeshDef
                {
                    Id = "l_" + MeshInputComponent.Fingerprint(posArray),
                    Kind = "lines",
                    Positions = posArray,
                    Indices = idx.ToArray(),
                }));
                totalSegments += samples - 1 + (closed ? 1 : 0);
            }

            if (outObjs.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No usable curves");
                return;
            }
            if (skipped > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{skipped} curve(s) were invalid");

            Message = $"{outObjs.Count} curves · {totalSegments} segments · {samples}/curve";
            DA.SetDataList(0, outObjs);
        }
    }
}
