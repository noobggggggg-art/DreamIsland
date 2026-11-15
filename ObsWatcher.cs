using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DreamIsland
{
    /// <summary>
    /// Следит за OBS Studio через OBS WebSocket (протокол v5, встроен в OBS 28+):
    /// идёт ли запись (REC) и трансляция (LIVE) и сколько они длятся.
    /// Работает в фоне; если OBS не запущен, раз в 5 секунд тихо пробует подключиться.
    /// </summary>
    public sealed class ObsWatcher
    {
        private CancellationTokenSource _cts;
        private bool _rec, _str, _lastRec, _lastStr, _authReported;
        private DateTime _recStart, _strStart;

        public string Password = "";
        public int Port = 4455;

        /// <summary>(идёт запись, её длительность, идёт эфир, его длительность). Вызывается в UI-потоке.</summary>
        public event Action<bool, TimeSpan, bool, TimeSpan> Changed;

        /// <summary>OBS отклонил пароль. Вызывается в UI-потоке.</summary>
        public event Action AuthFailed;

        public void Start()
        {
            Stop();
            _authReported = false;

            var cts = new CancellationTokenSource();
            _cts = cts;
            Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop()
        {
            var c = _cts;
            _cts = null;
            if (c != null)
            {
                try { c.Cancel(); } catch { }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await SessionAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch { }

                if (ct.IsCancellationRequested) break;

                Reset(); // соединение оборвалось: OBS закрыли или перезапустили
                try { await Task.Delay(_authReported ? 30000 : 5000, ct); }
                catch { break; }
            }
            Reset();
        }

        private async Task SessionAsync(CancellationToken ct)
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + Port), ct);

                // 1. приветствие сервера (там же данные для пароля, если он включён)
                string auth = null;
                using (var hello = await ReceiveAsync(ws, ct))
                {
                    var d = hello.RootElement.GetProperty("d");
                    JsonElement a;
                    if (d.TryGetProperty("authentication", out a))
                    {
                        auth = ComputeAuth(Password ?? "",
                                           a.GetProperty("salt").GetString(),
                                           a.GetProperty("challenge").GetString());
                    }
                }

                // 2. представляемся; 64 = подписка на события вывода (запись и трансляция)
                var id = new StringBuilder();
                id.Append("{\"op\":1,\"d\":{\"rpcVersion\":1,\"eventSubscriptions\":64");
                if (auth != null) id.Append(",\"authentication\":\"").Append(auth).Append("\"");
                id.Append("}}");
                await SendAsync(ws, id.ToString(), ct);

                // 3. ждём подтверждения (при неверном пароле OBS закроет соединение)
                using (var ident = await ReceiveAsync(ws, ct))
                {
                    if (ident.RootElement.GetProperty("op").GetInt32() != 2)
                        throw new InvalidOperationException("OBS не подтвердил подключение");
                }

                // 4. текущее состояние записи и эфира
                await SendAsync(ws, "{\"op\":6,\"d\":{\"requestType\":\"GetRecordStatus\",\"requestId\":\"rec\"}}", ct);
                await SendAsync(ws, "{\"op\":6,\"d\":{\"requestType\":\"GetStreamStatus\",\"requestId\":\"str\"}}", ct);

                // 5. слушаем события
                while (!ct.IsCancellationRequested)
                {
                    using (var doc = await ReceiveAsync(ws, ct))
                        Handle(doc.RootElement);
                }
            }
        }

        private void Handle(JsonElement root)
        {
            JsonElement opEl, d;
            if (!root.TryGetProperty("op", out opEl)) return;
            if (!root.TryGetProperty("d", out d)) return;

            int op = opEl.GetInt32();
            var now = DateTime.UtcNow;

            if (op == 7) // ответ на запрос
            {
                JsonElement ri, rd;
                string reqId = d.TryGetProperty("requestId", out ri) ? ri.GetString() : "";

                if (d.TryGetProperty("responseData", out rd) && rd.ValueKind == JsonValueKind.Object)
                {
                    JsonElement a, du;
                    bool active = rd.TryGetProperty("outputActive", out a) && a.ValueKind == JsonValueKind.True;
                    long ms = rd.TryGetProperty("outputDuration", out du) && du.ValueKind == JsonValueKind.Number
                        ? du.GetInt64() : 0;

                    if (reqId == "rec") { _rec = active; _recStart = now - TimeSpan.FromMilliseconds(ms); }
                    else if (reqId == "str") { _str = active; _strStart = now - TimeSpan.FromMilliseconds(ms); }
                    Raise();
                }
            }
            else if (op == 5) // событие
            {
                JsonElement et, ed, oa;
                string type = d.TryGetProperty("eventType", out et) ? et.GetString() : "";
                if (!d.TryGetProperty("eventData", out ed) || ed.ValueKind != JsonValueKind.Object) return;

                bool active = ed.TryGetProperty("outputActive", out oa) && oa.ValueKind == JsonValueKind.True;

                if (type == "RecordStateChanged")
                {
                    if (active && !_rec) _recStart = now;
                    _rec = active;
                    Raise();
                }
                else if (type == "StreamStateChanged")
                {
                    if (active && !_str) _strStart = now;
                    _str = active;
                    Raise();
                }
            }
        }

        private void Reset()
        {
            _rec = false;
            _str = false;
            if (_lastRec || _lastStr) Raise();
        }

        private void Raise()
        {
            var now = DateTime.UtcNow;
            bool r = _rec, s = _str;
            TimeSpan re = r ? now - _recStart : TimeSpan.Zero;
            TimeSpan se = s ? now - _strStart : TimeSpan.Zero;
            _lastRec = r;
            _lastStr = s;

            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var h = Changed;
                    if (h != null) h(r, re, s, se);
                }));
            }
        }

        private void ReportAuthFailed()
        {
            if (_authReported) return;
            _authReported = true;

            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var h = AuthFailed;
                    if (h != null) h();
                }));
            }
        }

        private static string ComputeAuth(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        private static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        private async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        // код 4009 = неверный пароль
                        if ((int?)ws.CloseStatus == 4009) ReportAuthFailed();
                        throw new WebSocketException("Соединение закрыто");
                    }

                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);

                ms.Position = 0;
                return JsonDocument.Parse(ms);
            }
        }
    }
}
