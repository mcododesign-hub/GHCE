using System;
using Grasshopper.Kernel;

namespace Colugo
{
    /// <summary>
    /// GH 库标识。只要 .gha 放进 Libraries 目录，这颗 Info 就让电池出现在 Colugo 标签页下。
    /// 刻意不做 Rhino.PlugIns.PlugIn 子类：桥在 Render Output 首次求解时惰性启动，
    /// 少一个"插件加载失败"的故障面，也不需要额外注册插件 GUID。
    /// </summary>
    public class ColugoInfo : GH_AssemblyInfo
    {
        public override string Name => "Colugo";
        public override string Description => "Grasshopper Creative Coding Engine. Authored in GH, executed in the browser";
        public override Guid Id => new Guid("3c9e0000-3333-4c03-9003-0000000000b1");
        public override string AuthorName => "mcodo design";
        public override string AuthorContact => "";
        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}
