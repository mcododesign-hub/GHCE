using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Colugo
{
    /// <summary>
    /// 程序集加载钩子。
    ///
    /// 为什么需要它：本插件引用了 Microsoft.Web.WebView2.Wpf / .Core（Rhino 8 自带），
    /// 但它们不在 GH 的库目录里。Grasshopper 加载 .gha 时会遍历全部类型，
    /// 只要有一个类型的字段引用了找不到的程序集，整个库就加载失败、电池一个都不出现。
    ///
    /// [ModuleInitializer] 在程序集载入时立即执行，早于 GH 的类型扫描，
    /// 因此这里是挂钩子的唯一可靠位置。
    /// </summary>
    internal static class AssemblyInit
    {
        // CA2255 说 ModuleInitializer 只该用于应用代码。但宿主导入 .gha 后第一件事就是
        // 遍历全部类型，我们必须在"被任何代码调用之前"挂上解析钩子 —— 这正是它的用途。
#pragma warning disable CA2255
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Init()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveRhinoSibling;
        }

        static Assembly ResolveRhinoSibling(object sender, ResolveEventArgs args)
        {
            string simpleName;
            try { simpleName = new AssemblyName(args.Name).Name; }
            catch { return null; }
            if (simpleName == null) return null;

            // 只处理 Rhino 同目录的那几个：WebView2 系列。别的一律放给别人。
            if (!simpleName.StartsWith("Microsoft.Web.WebView2", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(simpleName, "WebView2Loader", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    var p = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                catch { /* 下一个 */ }
            }
            return null;
        }

        static IEnumerable<string> CandidateDirs()
        {
            // RhinoCommon 在 <Rhino>\System\netcore\，WebView2 在 <Rhino>\System\ —— 取父目录就够
            string rhinoCommonDir = null;
            try { rhinoCommonDir = Path.GetDirectoryName(typeof(Rhino.RhinoApp).Assembly.Location); }
            catch { }

            if (string.IsNullOrEmpty(rhinoCommonDir)) return Array.Empty<string>();

            var parent = Path.GetDirectoryName(rhinoCommonDir);
            if (string.IsNullOrEmpty(parent)) return new[] { rhinoCommonDir };
            return new[] { rhinoCommonDir, parent };
        }
    }
}
