using System;

namespace GHCE.Types
{
    /// <summary>
    /// Matrix conversion between Rhino and Three.js conventions.
    ///
    /// Rhino transforms a point as p' = p * M (row vector), so translation sits in the last
    /// ROW: M30 M31 M32.  Three.js stores Matrix4.elements column-major, and its translation
    /// also sits at indices 12..14.  Read the 16 numbers out in order and the two layouts
    /// already agree — there is NO transpose.
    ///
    /// This is the kind of thing that looks like it needs a transpose ("row vector vs column
    /// vector") and does not. The probe pins it down numerically: rotating (1,0,0) by 90°
    /// about Z and translating by (10,20,5) must give (10,21,5) under both conventions.
    /// </summary>
    public static class Transform3DMath
    {
        /// <summary>
        /// Rhino's 16 values → the order Three.js expects in Matrix4.elements.
        /// Same sequence; kept as a named function so the call sites read as a conversion
        /// rather than an accidental no-op.
        /// </summary>
        public static double[] ToThreeOrder(double[] rhino16)
        {
            if (rhino16 == null || rhino16.Length != 16) return Identity();
            var o = new double[16];
            Array.Copy(rhino16, o, 16);
            return o;
        }

        public static double[] Identity()
        {
            return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        /// <summary>Apply with Rhino's row-vector convention. Test-only; proves the transpose.</summary>
        public static double[] ApplyRowVector(double[] rowMajor, double x, double y, double z)
        {
            var m = rowMajor;
            return new[]
            {
                x * m[0] + y * m[4] + z * m[8] + m[12],
                x * m[1] + y * m[5] + z * m[9] + m[13],
                x * m[2] + y * m[6] + z * m[10] + m[14],
            };
        }

        /// <summary>Apply with Three.js' column-vector convention on a column-major array.</summary>
        public static double[] ApplyColumnVector(double[] columnMajor, double x, double y, double z)
        {
            var m = columnMajor;
            return new[]
            {
                m[0] * x + m[4] * y + m[8] * z + m[12],
                m[1] * x + m[5] * y + m[9] * z + m[13],
                m[2] * x + m[6] * y + m[10] * z + m[14],
            };
        }

        /// <summary>
        /// Rotate 90° about Z then translate by (tx, ty, tz), written in Rhino's row-major order.
        /// Used by the probe as a known-good fixture.
        /// </summary>
        public static double[] RotationZ90ThenTranslate(double tx, double ty, double tz)
        {
            // p * M with p = (x,y,z,1):  x' = -y + tx,  y' = x + ty,  z' = z + tz
            return new double[]
            {
                 0,  1,  0,  0,
                -1,  0,  0,  0,
                 0,  0,  1,  0,
                tx, ty, tz, 1,
            };
        }
    }
}
