using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Colugo.Bridge;
using Colugo.Types;
using Colugo.Ui;

namespace Colugo.Components
{
    /// <summary>
    /// Render Output —— 编译 + 推送，并负责弹出预览窗。
    /// 它不持有任何绘制状态，所以 GH 重算不会重启浏览器里的运行时（M0 验收项）。
    ///
    /// 端口不是输入参数：端口属于环境配置，不该出现在作品图里。
    /// 8777 被占时自动往后退（StartAuto），当前实际端口见右键菜单。
    /// </summary>
    public class RenderOutputComponent : GH_Component
    {
        const int PreferredPort = 8777;

        readonly PatchBuilder _builder = new PatchBuilder();
        bool _previewWasOff = true;

        public RenderOutputComponent()
            : base("Render Output", "Render", "Compiles commands into patches and pushes them to the preview window", "Colugo", "Output")
        {
            // 预览页面比第一次 patch 晚连上来是常态：登记一下，
            // 客户端接入时把自己 expire 一次，补发整帧快照（见 ResyncTicker）。
            ResyncTicker.Subscribe(this);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            ResyncTicker.Unsubscribe(this);
            base.RemovedFromDocument(document);
        }

        public override Guid ComponentGuid => new Guid("5a0c7e10-2222-4b02-8d02-0000000000a4");
        protected override Bitmap Icon => Icons.RenderOutput;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddParameter(new Param_Colugo_Canvas(), "Canvas", "Canvas", "Canvas definition", GH_ParamAccess.item);
            p.AddParameter(new Param_Colugo_Command(), "Commands", "Cmds",
                "Draw commands. Paths can connect directly and receive a default white stroke", GH_ParamAccess.list);
            p.AddParameter(new Param_Colugo_Entity(), "Entities", "Ent",
                "Entity blueprints from Entity Look", GH_ParamAccess.list);
            p.AddBooleanParameter("Enabled", "On", "Stops pushing and releases the port when disabled", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Preview", "Preview", "Opens the embedded preview window", GH_ParamAccess.item, true);
            p.AddParameter(new Param_Colugo_Scene3D(), "Scene", "S3",
                "3D scene from Scene 3D. Drawn on a separate layer above the 2D canvas", GH_ParamAccess.item);

            p[1].Optional = true;
            p[2].Optional = true;
            p[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Status", "S", "Compilation summary for the last frame", GH_ParamAccess.item);
            p.AddTextParameter("Url", "Url", "Preview URL, also usable in an external browser", GH_ParamAccess.item);
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            var ver = GetType().Assembly.GetName().Version;
            Menu_AppendItem(menu, $"Colugo v{ver.Major}.{ver.Minor}.{ver.Build} · port {(BridgeServer.IsRunning ? BridgeServer.Port.ToString() : "未启动")}",
                null, false, false);
            Menu_AppendItem(menu, "Open in external browser", (s, e) =>
            {
                if (!BridgeServer.IsRunning) return;
                try { Process.Start(new ProcessStartInfo(BridgeServer.PreviewUrl) { UseShellExecute = true }); } catch { }
            }, BridgeServer.IsRunning, false);
            Menu_AppendItem(menu, "Icon self-check...", (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo(Icons.DumpSheet()) { UseShellExecute = true }); } catch { }
            }, true, false);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool enabled = true, preview = true;
            DA.GetData(3, ref enabled);
            DA.GetData(4, ref preview);

            if (!enabled)
            {
                PreviewWindow.Close();
                _previewWasOff = true;
                BridgeServer.Stop();
                _builder.Invalidate();
                Message = "off";
                DA.SetData(0, "disabled");
                DA.SetData(1, "");
                return;
            }

            var canvasGoo = new Colugo_Canvas();
            if (!DA.GetData(0, ref canvasGoo) || canvasGoo.Value == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Missing Canvas definition");
                return;
            }

            var cmds = new List<Colugo_Command>();
            DA.GetDataList(1, cmds);

            var entities = new List<Colugo_Entity>();
            DA.GetDataList(2, entities);

            var sceneGoo = new Colugo_Scene3D();
            DA.GetData(5, ref sceneGoo);
            var scene3d = sceneGoo?.Value;

            if (cmds.Count == 0 && entities.Count == 0 && scene3d == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Commands, Entities and Scene are all empty");
                DA.SetData(0, "nothing to draw");
                return;
            }
            var err = BridgeServer.StartAuto(PreferredPort);
            if (err != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, err);
                DA.SetData(0, err);
                return;
            }

            // 预览开关：只在 false→true 的那一次开窗
            if (preview && _previewWasOff)
            {
                _previewWasOff = false;
                PreviewWindow.Open(BridgeServer.PreviewUrl);
            }
            else if (!preview && !_previewWasOff)
            {
                _previewWasOff = true;
                PreviewWindow.Close();
            }

            if (!string.IsNullOrEmpty(PreviewWindow.LastError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, PreviewWindow.LastError);
                PreviewWindow.LastError = null;
            }

            string summary;
            var json = _builder.Build(canvasGoo.Value, cmds, entities, scene3d, out summary);
            BridgeServer.Broadcast(json);

            Message = $"{BridgeServer.ClientCount} client · {summary}";
            DA.SetData(0, summary);
            DA.SetData(1, BridgeServer.PreviewUrl);
        }
    }
}
