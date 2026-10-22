using System;

static class ProbeIcons
{
    /// <summary>
    /// 只做定点类型查询。
    /// 教训：Transform.Rotation / Point3d.Transform 在 Rhino 外会抛 DllNotFoundException（rhcommon_c）；
    /// 而手搭 Identity 矩阵得到的坐标是 (0, 0.09) 这种不可信的垃圾值 —— 所以几何数值换算
    /// 一律不在探针里验，必须在 Rhino 内验。
    /// </summary>
    public static void Run()
    {
        var asm = typeof(Grasshopper.Kernel.GH_DocumentObject).Assembly;
        foreach (var name in new[]
        {
            "Grasshopper.Kernel.Parameters.Param_Transform",
            "Grasshopper.Kernel.Types.GH_Transform",
        })
            Console.WriteLine($"{name}: {(asm.GetType(name) != null ? "存在" : "不存在")}");
        Console.WriteLine("Rhino.Geometry.Transform: " + typeof(Rhino.Geometry.Transform).FullName);
    }
}
