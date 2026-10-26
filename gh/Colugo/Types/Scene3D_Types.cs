using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace Colugo.Types
{
    // ---------------------------------------------------------------------
    // 三维数据类型（阶段 5）
    //
    // 与二维的分工完全同构：
    //   二维  Path  ─► Transform/Morph ─► Draw Path ─► Command ─► Track ─► Render Output
    //   三维  Mesh  ─► Transform 3D   ─► Draw 3D   ─► Cmd3D   ─► Scene 3D ─► Render Output
    //
    // 差别只有一个：三维不需要"投影"这一步，相机在浏览器里由 Three.js 完成。
    // 所以 PathDef 的 Proj 字段在这里没有对应物。
    // ---------------------------------------------------------------------

    /// <summary>
    /// A triangulated mesh in world coordinates. Vertices are xyz triples;
    /// indices are triangle triples. Normals and UVs are optional.
    /// </summary>
    public sealed class MeshDef
    {
        public string Id;                // stable, derived from a vertex fingerprint

        /// <summary>
        /// What the vertex buffer represents: "mesh" (triangles), "points" (loose vertices),
        /// or "lines" (index pairs). Same buffer layout for all three — only how the runtime
        /// interprets the indices differs, so one type covers meshes, point clouds and curves.
        /// </summary>
        public string Kind = "mesh";
        public double[] Positions;       // 3 per vertex, world space
        public int[] Indices;            // 3 per triangle
        public double[] Normals;         // 3 per vertex, may be null
        public double[] Uvs;             // 2 per vertex, may be null

        /// <summary>
        /// Transform carried by the mesh itself, so a mesh can animate without being
        /// converted into a command first. Same idea as PathDef.Matrix / MatrixFrames
        /// in 2D: geometry and animation stay one layer, style is the next.
        /// </summary>
        public Transform3DDef Xform;

        public int VertexCount => Positions == null ? 0 : Positions.Length / 3;
        public int TriCount => Kind == "mesh" && Indices != null ? Indices.Length / 3 : 0;
        public int SegmentCount => Kind == "lines" && Indices != null ? Indices.Length / 2 : 0;

        public MeshDef Duplicate()
        {
            var m = (MeshDef)MemberwiseClone();
            if (Positions != null) m.Positions = (double[])Positions.Clone();
            if (Indices != null) m.Indices = (int[])Indices.Clone();
            if (Normals != null) m.Normals = (double[])Normals.Clone();
            if (Uvs != null) m.Uvs = (double[])Uvs.Clone();
            m.Xform = Xform?.Duplicate();
            return m;
        }
    }

    /// <summary>
    /// Material preset. Mode picks which Three.js material is built:
    /// 1 solid, 2 wireframe, 3 points, 4 normals, 5 unlit.
    /// </summary>
    public sealed class Material3DDef
    {
        public int Mode = 1;
        public string Color = "#e8e6df";
        public double Size = 2.0;        // points: pixel size / wireframe: line width
        public double Glow = 0.0;        // emissive intensity, 0..2
        public double Opacity = 1.0;

        /// <summary>
        /// 0.34：colorNode 的 Shader 子图。null 时走原来的常量色路径 ——
        /// 旧定义一行都不用改，这是 TSL 接入"最小侵入"的前提。
        /// </summary>
        public ShaderExprDef Shader;

        /// <summary>
        /// 0.36：顶点偏移（局部空间，vec3）。运行时算的是 positionLocal + 这张图，
        /// 不是直接替换 positionNode —— 直接替换的话用户接一个 sin 波，几何会整体塌到原点。
        /// 语义定成"偏移"，接上去就是膨胀/波动，符合直觉。
        /// </summary>
        public ShaderExprDef ShaderDisplace;

        public Material3DDef Duplicate() => new Material3DDef
        {
            Mode = Mode,
            Color = Color,
            Size = Size,
            Glow = Glow,
            Opacity = Opacity,
            Shader = Shader?.Duplicate(),
            ShaderDisplace = ShaderDisplace?.Duplicate(),
        };

        public string Key() =>
            $"{Mode}|{Color}|{Size:0.####}|{Glow:0.####}|{Opacity:0.####}" +
            (Shader == null ? "" : "|S:" + Shader.Key()) +
            (ShaderDisplace == null ? "" : "|D:" + ShaderDisplace.Key());

        /// <summary>结构键：shader 部分剔除 uniform 值，其余同上。</summary>
        public string StructKey() =>
            $"{Mode}|{Color}|{Size:0.####}|{Glow:0.####}|{Opacity:0.####}" +
            (Shader == null ? "" : "|S:" + Shader.StructKey()) +
            (ShaderDisplace == null ? "" : "|D:" + ShaderDisplace.StructKey());
    }

    /// <summary>
    /// Transform for one mesh: a single 4x4 matrix, or a frame sequence played by the runtime.
    /// Matrices are stored the Three.js way (column-major 16 doubles) so no transpose happens
    /// in the browser. The conversion from Rhino's row-vector convention happens here, once.
    /// </summary>
    public sealed class Transform3DDef
    {
        public double[] Matrix;          // 16, column-major (Three.js order); null = identity
        public double[] Frames;          // N*16, column-major; null = static
        public int N;                    // frame count when Frames != null
        /// <summary>
        /// 帧序列一圈多长（秒）。以前写死在 scene3d.js 里是 1.5，
        /// 结果三维和二维的时间轴永远对不上拍子 —— 这里把它变成参数。
        /// </summary>
        public double Duration = 1.5;

        public Transform3DDef Duplicate()
        {
            var t = (Transform3DDef)MemberwiseClone();
            if (Matrix != null) t.Matrix = (double[])Matrix.Clone();
            if (Frames != null) t.Frames = (double[])Frames.Clone();
            return t;
        }

        public string Key()
        {
            if (Frames != null && N >= 2)
                return $"seq{N}@{Duration:0.###}:" + Fingerprint(Frames);
            if (Matrix != null)
                return "mat:" + Fingerprint(Matrix);
            return "-";
        }

        static string Fingerprint(double[] a)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < a.Length; i++)
                {
                    h ^= (ulong)BitConverter.DoubleToInt64Bits(a[i]);
                    h *= 1099511628211UL;
                }
                return h.ToString("x8");
            }
        }
    }

    /// <summary>One drawable in the 3D scene: a mesh, a material and a transform.</summary>
    public sealed class Cmd3DDef
    {
        public string Id;
        public string MeshId;
        public MeshDef Mesh;             // carried by reference so Draw 3D can pass it along
        public Material3DDef Material = new Material3DDef();
        public Transform3DDef Xform = new Transform3DDef();
        public bool Visible = true;
        public int Order;

        public Cmd3DDef Duplicate()
        {
            var c = (Cmd3DDef)MemberwiseClone();
            c.Material = Material?.Duplicate();
            c.Xform = Xform?.Duplicate();
            return c;
        }
    }

    /// <summary>
    /// Camera. Everything is optional: when Position or Target is missing the runtime
    /// frames the whole scene by its bounding box and keeps the bounding sphere centred.
    /// </summary>
    public sealed class Camera3DDef
    {
        public bool HasPosition;
        public double Px, Py, Pz;
        public bool HasTarget;
        public double Tx, Ty, Tz;
        public double Fov = 45;          // degrees, used by the perspective camera
        public bool Orthographic;        // true = orthographic
        public double Zoom = 1;          // orthographic zoom factor

        public Camera3DDef Duplicate() => (Camera3DDef)MemberwiseClone();

        public string Key() =>
            $"{(HasPosition ? $"{Px:0.####},{Py:0.####},{Pz:0.####}" : "-")}|" +
            $"{(HasTarget ? $"{Tx:0.####},{Ty:0.####},{Tz:0.####}" : "-")}|" +
            $"{Fov:0.####}|{(Orthographic ? 1 : 0)}|{Zoom:0.####}";
    }

    /// <summary>The whole 3D scene handed to Render Output.</summary>
    public sealed class Scene3DDef
    {
        public List<Cmd3DDef> Cmds = new List<Cmd3DDef>();
        public Camera3DDef Camera;       // null = auto frame
        public string Background = "#07070a";
        public bool Lights = true;       // built-in three-point rig
        public double Ambient = 1.0;     // intensity multiplier for the rig

        public Scene3DDef Duplicate()
        {
            var s = (Scene3DDef)MemberwiseClone();
            s.Cmds = new List<Cmd3DDef>(Cmds.Count);
            foreach (var c in Cmds) s.Cmds.Add(c?.Duplicate());
            s.Camera = Camera?.Duplicate();
            return s;
        }
    }

    // ---------------------------------------------------------------------
    // Goo wrappers
    // ---------------------------------------------------------------------

    public class Colugo_Mesh : GH_Goo<MeshDef>
    {
        public Colugo_Mesh() { }
        public Colugo_Mesh(MeshDef v) { Value = v; }

        public override bool IsValid =>
            Value != null && Value.VertexCount > 0
            && (Value.Kind == "points" || (Value.Indices != null && Value.Indices.Length > 0));
        public override string TypeName => "Colugo Mesh";
        public override string TypeDescription => "Triangulated mesh in world space";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Mesh() : new Colugo_Mesh(Value.Duplicate());

        public override string ToString() =>
            Value == null
                ? "<null>"
                : Value.Kind == "points"
                    ? $"{Value.VertexCount} points"
                    : Value.Kind == "lines"
                        ? $"{Value.VertexCount} verts · {Value.SegmentCount} segments"
                        : $"{Value.VertexCount} verts · {Value.TriCount} tris";

        public override bool CastFrom(object source)
        {
            if (source is MeshDef d) { Value = d; return true; }
            if (source is Colugo_Mesh g) { Value = g.Value; return true; }
            return false;
        }

        public override bool CastTo<Q>(ref Q target)
        {
            if (typeof(Q).IsAssignableFrom(typeof(MeshDef))) { target = (Q)(object)Value; return true; }
            return false;
        }
    }

    public class Colugo_Cmd3D : GH_Goo<Cmd3DDef>
    {
        public Colugo_Cmd3D() { }
        public Colugo_Cmd3D(Cmd3DDef v) { Value = v; }

        public override bool IsValid => Value != null && Value.Mesh != null;
        public override string TypeName => "Colugo Draw Command 3D";
        public override string TypeDescription => "Mesh plus material plus transform";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Cmd3D() : new Colugo_Cmd3D(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : $"mode {Value.Material?.Mode} · {Value.Mesh?.TriCount} tris";

        public override bool CastFrom(object source)
        {
            if (source is Cmd3DDef d) { Value = d; return true; }
            if (source is Colugo_Cmd3D g) { Value = g.Value; return true; }
            return false;
        }
    }

    public class Colugo_Camera3D : GH_Goo<Camera3DDef>
    {
        public Colugo_Camera3D() { }
        public Colugo_Camera3D(Camera3DDef v) { Value = v; }

        public override bool IsValid => Value != null;
        public override string TypeName => "Colugo Camera 3D";
        public override string TypeDescription => "Camera for the 3D scene; empty means auto frame";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Camera3D() : new Colugo_Camera3D(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : (Value.HasPosition ? "explicit" : "auto frame");

        public override bool CastFrom(object source)
        {
            if (source is Camera3DDef d) { Value = d; return true; }
            if (source is Colugo_Camera3D g) { Value = g.Value; return true; }
            return false;
        }
    }

    public class Colugo_Scene3D : GH_Goo<Scene3DDef>
    {
        public Colugo_Scene3D() { }
        public Colugo_Scene3D(Scene3DDef v) { Value = v; }

        public override bool IsValid => Value != null && Value.Cmds != null && Value.Cmds.Count > 0;
        public override string TypeName => "Colugo Scene 3D";
        public override string TypeDescription => "3D scene: draw commands plus camera";

        public override IGH_Goo Duplicate() =>
            Value == null ? new Colugo_Scene3D() : new Colugo_Scene3D(Value.Duplicate());

        public override string ToString() =>
            Value == null ? "<null>" : $"{Value.Cmds.Count} objects · {(Value.Camera == null ? "auto frame" : "camera")}";

        public override bool CastFrom(object source)
        {
            if (source is Scene3DDef d) { Value = d; return true; }
            if (source is Colugo_Scene3D g) { Value = g.Value; return true; }
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Param wrappers
    // ---------------------------------------------------------------------

    public class Param_Colugo_Mesh : GH_Param<Colugo_Mesh>
    {
        public Param_Colugo_Mesh() : base("Meshes", "Meshes", "Triangulated meshes in world space", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000d1");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataMesh;
    }

    public class Param_Colugo_Cmd3D : GH_Param<Colugo_Cmd3D>
    {
        public Param_Colugo_Cmd3D() : base("Commands 3D", "Cmds3D", "Mesh plus material plus transform", "Colugo", "Data", GH_ParamAccess.list) { }
        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000d2");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCmd3D;
    }

    public class Param_Colugo_Camera3D : GH_Param<Colugo_Camera3D>
    {
        public Param_Colugo_Camera3D() : base("Camera", "Cam", "3D camera. Empty means auto frame", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000d3");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataCamera3D;
    }

    public class Param_Colugo_Scene3D : GH_Param<Colugo_Scene3D>
    {
        public Param_Colugo_Scene3D() : base("Scene", "Scene", "3D scene handed to Render Output", "Colugo", "Data", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("7b2c4d10-3333-4c03-9f03-0000000000d4");
        protected override System.Drawing.Bitmap Internal_Icon_24x24 => Icons.DataScene3D;
    }
}
