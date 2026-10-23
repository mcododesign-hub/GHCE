using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using GHCE.Types;
using RH = Rhino.Geometry;

namespace GHCE.Components
{
    /// <summary>
    /// Rhino geometry to GHCE Mesh.
    ///
    /// Accepts Mesh, SubD and Brep. The last two are converted here rather than in the
    /// runtime, because meshing is a Rhino concern — the browser should never have to know
    /// what a Brep is.
    /// </summary>
    public class MeshInputComponent : GH_Component
    {
        public MeshInputComponent()
            : base("Mesh Input", "Mesh", "Rhino Mesh, SubD or Brep to a triangulated mesh", "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a1");
        protected override Bitmap Icon => Icons.MeshInput;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            var geo = new Param_Geometry
            {
                Name = "Geometry",
                NickName = "Geo",
                Description = "Meshes, SubD or Brep. Brep and SubD are meshed automatically",
                Access = GH_ParamAccess.list,
            };
            p.AddParameter(geo);
            p.AddNumberParameter("Density", "D", "SubD display density, 1-4. Brep uses default meshing parameters",
                GH_ParamAccess.item, 2.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Meshes", "M", "Triangulated meshes in world space",
                GH_ParamAccess.list);
            p.AddTextParameter("Info", "I", "Vertex and triangle counts", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var items = new List<GeometryBase>();
            if (!DA.GetDataList(0, items) || items.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No geometry");
                return;
            }

            double density = 2;
            DA.GetData(1, ref density);
            int dens = Math.Clamp((int)Math.Round(density), 1, 4);

            var outMeshes = new List<GHCE_Mesh>(items.Count);
            int verts = 0, tris = 0, skipped = 0;

            foreach (var g in items)
            {
                var raw = ToRhinoMesh(g, dens);
                if (raw == null || raw.Vertices.Count == 0 || raw.Faces.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var def = Build(raw);
                if (def == null) { skipped++; continue; }

                outMeshes.Add(new GHCE_Mesh(def));
                verts += def.VertexCount;
                tris += def.TriCount;
            }

            if (outMeshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Nothing could be converted to a mesh");
                return;
            }
            if (skipped > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{skipped} input(s) produced no triangles");

            Message = $"{outMeshes.Count} meshes · {verts} v · {tris} tri";
            DA.SetDataList(0, outMeshes);
            DA.SetData(1, Message);
        }

        static RH.Mesh ToRhinoMesh(GeometryBase g, int density)
        {
            switch (g)
            {
                case RH.Mesh m:
                    return m.DuplicateMesh();

                case SubD sd:
                    // Returns a single Mesh, unlike CreateFromBrep which returns one per face.
                    return RH.Mesh.CreateFromSubD(sd, density);

                case RH.Brep b:
                {
                    var parts = RH.Mesh.CreateFromBrep(b, MeshingParameters.Default);
                    return Join(parts);
                }

                default:
                    return null;
            }
        }

        static RH.Mesh Join(RH.Mesh[] parts)
        {
            if (parts == null || parts.Length == 0) return null;
            if (parts.Length == 1) return parts[0];
            var m = new RH.Mesh();
            foreach (var p in parts) m.Append(p);
            return m;
        }

        /// <summary>
        /// Flatten to a MeshDef. Identity is a fingerprint of the vertex positions, so
        /// moving a control point changes the id and the runtime rebuilds the buffer,
        /// while a pure re-solve with identical geometry reuses what it already has.
        /// </summary>
        static MeshDef Build(RH.Mesh m)
        {
            // Quads must become triangles: the runtime speaks triangles only.
            if (m.Faces.QuadCount > 0)
            {
                m = m.DuplicateMesh();
                m.Faces.ConvertQuadsToTriangles();
            }

            int nv = m.Vertices.Count;
            var pos = new double[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                var p = m.Vertices[i];
                pos[i * 3] = p.X;
                pos[i * 3 + 1] = p.Y;
                pos[i * 3 + 2] = p.Z;
            }

            var idx = new int[m.Faces.Count * 3];
            for (int i = 0; i < m.Faces.Count; i++)
            {
                var f = m.Faces[i];
                idx[i * 3] = f.A;
                idx[i * 3 + 1] = f.B;
                idx[i * 3 + 2] = f.C;
            }

            double[] nor = null;
            if (m.Normals.Count == nv)
            {
                nor = new double[nv * 3];
                for (int i = 0; i < nv; i++)
                {
                    var v = m.Normals[i];
                    nor[i * 3] = v.X;
                    nor[i * 3 + 1] = v.Y;
                    nor[i * 3 + 2] = v.Z;
                }
            }

            double[] uv = null;
            if (m.TextureCoordinates.Count == nv)
            {
                uv = new double[nv * 2];
                for (int i = 0; i < nv; i++)
                {
                    var t = m.TextureCoordinates[i];
                    uv[i * 2] = t.X;
                    uv[i * 2 + 1] = t.Y;
                }
            }

            return new MeshDef
            {
                Id = "m_" + Fingerprint(pos),
                Positions = pos,
                Indices = idx,
                Normals = nor,
                Uvs = uv,
            };
        }

        internal static string Fingerprint(double[] a)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < a.Length; i++)
                {
                    h ^= (ulong)BitConverter.DoubleToInt64Bits(a[i]);
                    h *= 1099511628211UL;
                }
                return h.ToString("x8");
            }
        }
    }

    /// <summary>
    /// Same role as the 2D Transform: one Xform is a static matrix, N Xforms are a
    /// frame sequence the runtime plays. Matrices are converted to the Three.js
    /// column-major convention here, so the browser never sees Rhino's row-vector layout.
    /// </summary>
    public class Transform3DComponent : GH_Component
    {
        public Transform3DComponent()
            : base("Transform 3D", "Xform3D", "Affine transform for meshes: one matrix is static, many form a frame sequence",
                   "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a2");
        protected override Bitmap Icon => Icons.Transform3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Meshes", "M", "Meshes to transform", GH_ParamAccess.list);

            var xf = new Param_Transform
            {
                Name = "Xform",
                NickName = "X",
                Description = "One matrix is static; N become a frame sequence in time order",
                Access = GH_ParamAccess.list,
            };
            p.AddParameter(xf);
            p.AddNumberParameter("Duration", "Dur",
                "How long one pass through the frames takes, in seconds. Ignored for a single matrix",
                GH_ParamAccess.item, 1.5);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Meshes", "M", "Meshes carrying the transform", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var meshes = new List<GHCE_Mesh>();
            if (!DA.GetDataList(0, meshes) || meshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Meshes");
                return;
            }

            var xfs = new List<Transform>();
            DA.GetDataList(1, xfs);

            double duration = 1.5;
            DA.GetData(2, ref duration);
            if (!(duration > 1e-4)) duration = 1.5;

            if (xfs.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Xform, meshes pass through unchanged");
                DA.SetDataList(0, meshes);
                return;
            }

            // 4x4 → column-major 16, one per Xform
            var flat = new double[xfs.Count * 16];
            for (int k = 0; k < xfs.Count; k++)
            {
                var row = RowMajor(xfs[k]);
                var col = Transform3DMath.ToThreeOrder(row);
                Array.Copy(col, 0, flat, k * 16, 16);
            }

            var outMeshes = new List<GHCE_Mesh>(meshes.Count);
            foreach (var g in meshes)
            {
                var def = g?.Value;
                if (def == null) continue;

                var copy = def.Duplicate();
                copy.Xform = new Transform3DDef { Duration = duration };

                if (xfs.Count == 1)
                {
                    copy.Xform.Matrix = new double[16];
                    Array.Copy(flat, 0, copy.Xform.Matrix, 0, 16);
                }
                else
                {
                    copy.Xform.Frames = flat;
                    copy.Xform.N = xfs.Count;
                }

                outMeshes.Add(new GHCE_Mesh(copy));
            }

            Message = xfs.Count == 1 ? "static" : $"{xfs.Count} frames · {duration:0.##}s";
            DA.SetDataList(0, outMeshes);
        }

        /// <summary>
        /// Reads Rhino's Transform as row-major 16. Rhino stores the matrix so that
        /// a point transforms as p' = p * M, which puts translation in M03/M13/M23.
        /// </summary>
        static double[] RowMajor(Transform t)
        {
            return new double[]
            {
                t.M00, t.M01, t.M02, t.M03,
                t.M10, t.M11, t.M12, t.M13,
                t.M20, t.M21, t.M22, t.M23,
                t.M30, t.M31, t.M32, t.M33,
            };
        }
    }

    /// <summary>
    /// Material preset plus its uniforms. Mode is a number, not a string:
    /// 1 solid, 2 wireframe, 3 points, 4 normals, 5 unlit.
    /// </summary>
    public class Draw3DComponent : GH_Component
    {
        readonly string[] Modes = { "solid", "wireframe", "points", "normals", "unlit" };

        public Draw3DComponent()
            : base("Draw 3D", "Draw3D", "Mesh plus a material preset, forming 3D draw commands", "GHCE", "3D") { }

        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000a3");
        protected override Bitmap Icon => Icons.Draw3D;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Mesh(), "Meshes", "M", "Meshes from Mesh Input or Transform 3D",
                GH_ParamAccess.list);
            p.AddIntegerParameter("Mode", "Mode",
                "1 solid · 2 wireframe · 3 points · 4 normals · 5 unlit", GH_ParamAccess.item, 1);
            p.AddColourParameter("Colour", "C", "Material colour", GH_ParamAccess.item, Color.FromArgb(232, 230, 223));
            p.AddNumberParameter("Size", "Size", "Point size for mode 3, line width for mode 2 (pixels)",
                GH_ParamAccess.item, 2.0);
            p.AddNumberParameter("Glow", "Glow", "Emissive intensity, 0-2. Solid and unlit only", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("Opacity", "A", "0-1", GH_ParamAccess.item, 1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddParameter(new Param_GHCE_Cmd3D(), "Commands", "Cmds", "3D draw commands, feed into Scene 3D",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var meshes = new List<GHCE_Mesh>();
            if (!DA.GetDataList(0, meshes) || meshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Missing Meshes");
                return;
            }

            int mode = 1;
            Color colour = Color.FromArgb(232, 230, 223);
            double size = 2, glow = 0, opacity = 1;
            DA.GetData(1, ref mode);
            DA.GetData(2, ref colour);
            DA.GetData(3, ref size);
            DA.GetData(4, ref glow);
            DA.GetData(5, ref opacity);

            mode = Math.Clamp(mode, 1, 5);
            if (size <= 0) size = 1;
            opacity = Math.Clamp(opacity, 0, 1);
            glow = Math.Clamp(glow, 0, 2);

            if (mode == 4)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Mode 4 ignores Colour");

            string hex = $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}";

            var outCmds = new List<GHCE_Cmd3D>(meshes.Count);
            for (int i = 0; i < meshes.Count; i++)
            {
                var def = meshes[i]?.Value;
                if (def == null) continue;

                outCmds.Add(new GHCE_Cmd3D(new Cmd3DDef
                {
                    Id = "c3_" + def.Id,
                    MeshId = def.Id,
                    Mesh = def,
                    // 几何层的动画必须传下去，否则 Transform 3D 在三维链路里等于没接
                    Xform = def.Xform?.Duplicate() ?? new Transform3DDef(),
                    Order = i,
                    Material = new Material3DDef
                    {
                        Mode = mode,
                        Color = hex,
                        Size = size,
                        Glow = glow,
                        Opacity = opacity,
                    },
                }));
            }

            var kind = string.IsNullOrEmpty(meshes[0]?.Value?.Kind) ? "mesh" : meshes[0].Value.Kind;
            Message = kind == "mesh"
                ? $"{outCmds.Count} × {Modes[mode - 1]}"
                : $"{outCmds.Count} × {kind}";
            DA.SetDataList(0, outCmds);
        }
    }
}
