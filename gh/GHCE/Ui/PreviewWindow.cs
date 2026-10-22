using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GHCE.Ui
{
    /// <summary>
    /// 内嵌预览窗口（WPF + WebView2）。
    /// Rhino 8 自带 Microsoft.Web.WebView2.Wpf.dll，所以不必额外打包浏览器内核；
    /// 真正的 Chromium 由机器上的 WebView2 Runtime 提供（Win10/11 基本都有，Rhino 8 自己也依赖它）。
    ///
    /// 失败时降级为"用系统默认浏览器打开"，绝不因为窗口起不来就吞掉错误：
    /// 错误会写进 LastError，由 Render Output 在下一次求解时挂到电池上。
    /// </summary>
    internal static class PreviewWindow
    {
        static Window _win;
        static WebView2 _view;

        public static string LastError;
        public static bool IsOpen => _win != null;

        /// <summary>打开（或激活已开的）预览窗口。必须在 UI 线程调用</summary>
        public static void Open(string url)
        {
            LastError = null;
            try
            {
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() => OpenCore(url)));
            }
            catch (Exception ex)
            {
                Fail(url, ex);
            }
        }

        public static void Close()
        {
            var w = _win;
            if (w == null) return;
            try
            {
                Rhino.RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    try { w.Close(); } catch { }
                    _win = null;
                    _view = null;
                }));
            }
            catch { }
        }

        /// <summary>窗口被用户点掉后，让组件知道预览已关闭</summary>
        public static event Action ClosedByUser;

        static async void OpenCore(string url)
        {
            try
            {
                if (_win != null)
                {
                    _win.Activate();
                    return;
                }

                var win = new Window
                {
                    Title = "GHCE Preview · " + url,
                    Width = 1180,
                    Height = 780,
                    MinWidth = 480,
                    MinHeight = 360,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x06, 0x06, 0x0A)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    // 置顶：Rhino 一激活就盖住预览窗，等于每次都要重新把它拖出来。
                    // 这是"运行窗"而不是普通面板，置顶才是它该有的行为。
                    Topmost = true,   // 注意 WPF 是小写 m，WinForms 才叫 TopMost
                };

                var view = new WebView2
                {
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                };

                win.Content = view;
                win.Closed += (s, e) =>
                {
                    _win = null;
                    _view = null;
                    try { view.Dispose(); } catch { }
                    try { ClosedByUser?.Invoke(); } catch { }
                };

                _win = win;
                _view = view;
                win.Show();

                var userData = Path.Combine(Path.GetTempPath(), "GHCE-WebView2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await view.EnsureCoreWebView2Async(env);

                // 预览窗里不需要右键菜单/开发者工具常驻，但保留 F12 方便查协议问题
                if (view.CoreWebView2 != null)
                {
                    view.CoreWebView2.Settings.AreDevToolsEnabled = true;
                    view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                }

                view.Source = new Uri(url);
            }
            catch (Exception ex)
            {
                Fail(url, ex);
            }
        }

        static void Fail(string url, Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            try
            {
                Rhino.RhinoApp.WriteLine("[GHCE] preview window failed: " + LastError + " → 改用系统浏览器");
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                LastError += "（已改用系统浏览器打开）";
            }
            catch (Exception ex2)
            {
                LastError += "，降级也失败：" + ex2.Message;
            }

            try { _win?.Close(); } catch { }
            _win = null;
            _view = null;
        }
    }
}
