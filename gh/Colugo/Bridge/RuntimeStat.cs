using System;
using System.Text.Json;

namespace Colugo.Bridge
{
    /// <summary>
    /// 浏览器回传的运行时状态（阶段 7 双向闭环的第一步）。
    /// 数据源是运行时每 200ms 发一次的 stat；GH 侧只读，不参与求解。
    ///
    /// 坐标系提醒：ptr 是**弹出画布的页面坐标**（左上原点、Y 向下），不是 Rhino 世界坐标。
    /// 想在 Rhino 里用它，需要自己决定怎么映射回去 —— 这一步不该由插件偷偷替你做。
    /// </summary>
    public static class RuntimeStat
    {
        public static double PtrX { get; private set; }
        public static double PtrY { get; private set; }
        public static bool Inside { get; private set; }
        public static bool Down { get; private set; }
        public static int Clicks { get; private set; }
        public static int Entities { get; private set; }
        public static int Alive { get; private set; }
        public static int Rev { get; private set; }
        public static double Fps { get; private set; }
        public static double DrawMs { get; private set; }
        public static double StepMs { get; private set; }
        public static double Time { get; private set; }
        public static string Keys { get; private set; } = "";

        static readonly Dictionary<string, double> _signals = new Dictionary<string, double>();

        /// <summary>Latest value the browser reported for a signal id. 0 when unknown.</summary>
        public static double SignalValue(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            lock (_signals) return _signals.TryGetValue(id, out var v) ? v : 0;
        }

        public static bool HasSignal(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (_signals) return _signals.ContainsKey(id);
        }
        public static DateTime At { get; private set; } = DateTime.MinValue;

        /// <summary>2 秒内收到过 stat 才算新鲜，否则说明窗口关了或断线</summary>
        public static bool Fresh => (DateTime.UtcNow - At).TotalSeconds < 2.0;

        public static void Update(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;

                if (r.TryGetProperty("ptr", out var ptr) && ptr.ValueKind == JsonValueKind.Array && ptr.GetArrayLength() >= 2)
                {
                    PtrX = ptr[0].GetDouble();
                    PtrY = ptr[1].GetDouble();
                }
                Inside = Bool(r, "inside");
                Down = Bool(r, "down");
                Clicks = Int(r, "clicks");
                Entities = Int(r, "entities");
                Alive = Int(r, "alive");
                Rev = Int(r, "rev");
                Fps = Num(r, "fps");
                DrawMs = Num(r, "drawMs");
                StepMs = Num(r, "stepMs");
                Time = Num(r, "time");
                Keys = r.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : "";

                if (r.TryGetProperty("signals", out var sg) && sg.ValueKind == JsonValueKind.Object)
                {
                    lock (_signals)
                    {
                        _signals.Clear();
                        foreach (var prop in sg.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Number)
                                _signals[prop.Name] = prop.Value.GetDouble();
                        }
                    }
                }
                At = DateTime.UtcNow;
            }
            catch
            {
                // 状态回传不是关键路径，坏了就坏一帧，不打断绘图
            }
        }

        public static void Reset()
        {
            At = DateTime.MinValue;
            Keys = "";
        }

        static double Num(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        static int Int(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        static bool Bool(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();
    }
}
