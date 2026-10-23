using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace GHCE.Bridge
{
    /// <summary>
    /// 桥：一个端口同时干两件事。
    ///   1. GET /、/src/*.js  → 返回内嵌在 .gha 里的运行时页面（不需要 node、不需要额外文件夹）
    ///   2. Upgrade: websocket → 升级成 WS，走 /ghce 推 patch
    ///
    /// 为什么不用 HttpListener：Windows 上它需要 URL ACL（或管理员权限），
    /// 会让"扔个 .gha 进 Libraries 就能用"变成要改系统配置。
    /// </summary>
    public static class BridgeServer
    {
        const string WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        static TcpListener _listener;
        static CancellationTokenSource _cts;
        static readonly List<Client> _clients = new List<Client>();
        static readonly object _lock = new object();

        public static int Port { get; private set; }
        public static bool IsRunning => _listener != null;
        public static int ClientCount { get { lock (_lock) return _clients.Count; } }
        public static string BaseUrl => $"http://127.0.0.1:{Port}/";

        /// <summary>
        /// 给预览窗用的地址：必须显式带 ?ws=，因为端口是自动避让出来的，
        /// 而页面里 WebSocket 的默认值写死是 8777 —— 端口一漂向导就会连错地方。
        /// </summary>
        public static string PreviewUrl => $"http://127.0.0.1:{Port}/?ws=ws://127.0.0.1:{Port}/ghce";

        /// <summary>启动。返回 null 表示成功，否则返回错误描述。</summary>
        public static string Start(int port)
        {
            if (IsRunning && Port == port) return null;
            if (IsRunning) Stop();

            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listener = l;
                Port = port;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                Task.Run(() => AcceptLoop(l, ct));
                Log($"[GHCE] bridge on http://127.0.0.1:{port}/  (ws: /ghce)");
                return null;
            }
            catch (SocketException ex)
            {
                _listener = null;
                return $"Cannot listen on port {port}: {ex.Message}";
            }
        }

        /// <summary>
        /// 从 preferred 起自动找一个空闲端口。
        /// 端口是环境配置，不该让用户在电池图上填 —— 8777 被占时自己往后退。
        /// </summary>
        public static string StartAuto(int preferred)
        {
            if (IsRunning) return null;
            string last = null;
            for (int p = preferred; p < preferred + 12; p++)
            {
                last = Start(p);
                if (last == null) return null;
            }
            return last;
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            lock (_lock)
            {
                foreach (var c in _clients) c.Dispose();
                _clients.Clear();
            }
            _listener = null;
            Log("[GHCE] bridge stopped");
        }
        static void Log(string s)
        {
            // 探针在 Rhino 外面跑，RhinoApp 不可用
            try { RhinoApp.WriteLine(s); } catch { Console.WriteLine(s); }
        }

        static async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { return; }
                catch (Exception) { return; }

                _ = Task.Run(() => HandleClient(tcp, ct));
            }
        }

        static async Task HandleClient(TcpClient tcp, CancellationToken ct)
        {
            Client client = null;
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var head = await ReadRequestHead(stream);

                if (!IsWebSocketUpgrade(head))
                {
                    await ServeHttp(stream, head);
                    return;
                }

                var key = ExtractHeader(head, "Sec-WebSocket-Key");
                if (key == null) return;

                var accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + WS_GUID)));

                var response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                var rb = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(rb, 0, rb.Length, ct);

                var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(15));

                client = new Client(ws);
                lock (_lock) _clients.Add(client);
                Log($"[GHCE] client connected ({ClientCount})");

                RequestResync();      // 新客户端必须拿整帧快照
                // 光置位不够：置位只在"下一次 Build"生效，而页面刚连上时 GH 并不会重算 ——
                // 所以要在 UI 线程上把 Render Output expire 一次，让它立刻发一帧快照。
                GHCE.Ui.ResyncTicker.Poke();
                await client.Pump(ct, OnInbound);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log("[GHCE] client error: " + ex.Message);
            }
            finally
            {
                if (client != null)
                {
                    lock (_lock) _clients.Remove(client);
                    client.Dispose();
                }
                try { tcp.Close(); } catch { }
            }
        }

        /* ---------------- 静态资源（内嵌） ---------------- */

        static readonly Assembly _asm = typeof(BridgeServer).Assembly;
        static readonly Dictionary<string, string> _mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
        };

        /// <summary>路径 → 内嵌资源名。根路径映射到 index.html</summary>
        static string ResourceName(string path)
        {
            var p = path.Split('?')[0];
            if (p == "/" || p.Length == 0) p = "/index.html";
            if (!p.StartsWith("/")) p = "/" + p;
            if (p.Contains("..")) return null;
            return "rt" + p;
        }

        static async Task ServeHttp(NetworkStream stream, string head)
        {
            string path = "/";
            var first = head.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            var parts = first.Split(' ');
            if (parts.Length >= 2) path = parts[1];

            if (path == "/favicon.ico")
            {
                var none = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(none, 0, none.Length);
                return;
            }

            var name = ResourceName(path);
            using (var res = name == null ? null : _asm.GetManifestResourceStream(name))
            {
                if (res == null)
                {
                    var body = Encoding.UTF8.GetBytes("404 not found: " + path);
                    var h = $"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(h);
                    await stream.WriteAsync(hb, 0, hb.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                    return;
                }

                using (var ms = new MemoryStream())
                {
                    await res.CopyToAsync(ms);
                    var body = ms.ToArray();
                    var ext = Path.GetExtension(name);
                    var mime = _mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
                    var h =
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: " + mime + "\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(h);
                    await stream.WriteAsync(hb, 0, hb.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                }
            }
        }

        static bool IsWebSocketUpgrade(string head)
        {
            if (string.IsNullOrEmpty(head)) return false;
            if (head.IndexOf("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return ExtractHeader(head, "Sec-WebSocket-Key") != null;
        }

        static async Task<string> ReadRequestHead(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var buf = new byte[2048];
            while (sb.Length < 65536)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
            }
            return sb.ToString();
        }

        static string ExtractHeader(string head, string name)
        {
            foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(name.Length + 1).Trim();
            }
            return null;
        }

        static void OnInbound(string json, Client client)
        {
            // 状态回传（阶段 7）：浏览器 → GH 的单向反馈，只读，不触发求解
            if (json.IndexOf("\"stat\"", StringComparison.Ordinal) >= 0)
            {
                RuntimeStat.Update(json);
                return;
            }
            // 只处理保活与回执；业务状态不回灌 GH（阶段 0 不做双向闭环）
            if (json.IndexOf("\"pong\"", StringComparison.Ordinal) >= 0) return;
            if (json.IndexOf("\"ping\"", StringComparison.Ordinal) >= 0)
                client.Enqueue("{\"v\":\"0.1\",\"t\":\"pong\"}");
            if (json.IndexOf("\"hello\"", StringComparison.Ordinal) >= 0)
            {
                client.Enqueue("{\"v\":\"0.1\",\"t\":\"welcome\",\"doc\":\"ghce\",\"rev\":0}");
                // 页面自己报到时也补一帧：预览窗口冷启动时可能比第一次握手还晚
                RequestResync();
                GHCE.Ui.ResyncTicker.Poke();
            }
        }

        /// <summary>向所有客户端推送一帧。整帧无变化时传 null，会被忽略。</summary>
        /// <summary>
        /// Pushes a signal definition to every client. Called every solve by Signal to Float —
        /// about a hundred bytes at 30 Hz, which buys a stateless runtime: no registry to keep,
        /// nothing to clean up when a component disappears.
        /// </summary>
        public static void PublishSignal(GHCE.Types.SignalDef s)
        {
            if (!IsRunning || s == null || string.IsNullOrEmpty(s.Id)) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["v"] = "0.1",
                    ["t"] = "signal",
                    ["id"] = s.Id,
                    ["kind"] = s.Kind,
                    ["seed"] = s.Seed,
                    ["rate"] = s.Rate,
                    ["args"] = s.Args,
                });
                Broadcast(json);
            }
            catch { }
        }

        public static void Broadcast(string json)
        {
            if (json == null) return;
            lock (_lock)
            {
                foreach (var c in _clients) c.Enqueue(json);
            }
        }

        // 新客户端/重连后补发快照的信标（由 RenderOutput 轮询）
        static int _resyncTick;
        public static void RequestResync() => Interlocked.Exchange(ref _resyncTick, 1);
        public static bool ConsumeResync() => Interlocked.Exchange(ref _resyncTick, 0) == 1;

        sealed class Client : IDisposable
        {
            readonly WebSocket _ws;
            readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
            readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            readonly CancellationTokenSource _cts = new CancellationTokenSource();

            public Client(WebSocket ws)
            {
                _ws = ws;
                Task.Run(WriteLoop);
            }

            public void Enqueue(string json)
            {
                if (_ws.State != WebSocketState.Open) return;
                _queue.Enqueue(json);
                try { _signal.Release(); } catch (SemaphoreFullException) { }
            }

            async Task WriteLoop()
            {
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    try { await _signal.WaitAsync(ct); }
                    catch (OperationCanceledException) { return; }

                    while (_queue.TryDequeue(out var msg))
                    {
                        var bytes = Encoding.UTF8.GetBytes(msg);
                        try
                        {
                            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                        }
                        catch (Exception)
                        {
                            return;   // 断线后由 HandleClient 的 finally 清理
                        }
                    }
                }
            }

            public async Task Pump(CancellationToken ct, Action<string, Client> onMessage)
            {
                var buf = new byte[8192];
                var sb = new StringBuilder();
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r;
                    try { r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct); }
                    catch (OperationCanceledException) { return; }
                    catch (WebSocketException) { return; }

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); } catch { }
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;

                    var text = sb.ToString();
                    sb.Clear();
                    onMessage?.Invoke(text, this);
                }
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _ws.Dispose(); } catch { }
            }
        }
    }
}
