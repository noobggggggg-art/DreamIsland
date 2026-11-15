using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace DreamIsland
{
    /// <summary>
    /// Следит за статусом DreamVPN: читает %APPDATA%\DreamVPN\status.json.
    /// Если файла нет — запасной вариант: ищет запущенные sing-box / xray / tun2socks.
    /// Все проверки идут в ФОНОВОМ потоке, интерфейс не подвисает.
    /// </summary>
    public sealed class VpnWatcher : IDisposable
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamVPN");
        private static readonly string FilePath = Path.Combine(Dir, "status.json");
        private static readonly string[] CoreProcesses = { "sing-box", "xray", "tun2socks" };

        private FileSystemWatcher _fsw;
        private Timer _poll;
        private Timer _debounce;
        private int _checking;
        private int _state = -1;     // -1 пока неизвестно, 0 выключен, 1 включён
        private string _lastInfo = "";

        /// <summary>(подключён ли VPN, страна или сервер для подписи). Вызывается в UI-потоке.</summary>
        public event Action<bool, string> Changed;

        public bool IsConnected { get { return Volatile.Read(ref _state) == 1; } }

        public void Start()
        {
            Check(); // первая проверка сразу, без плашки

            try
            {
                Directory.CreateDirectory(Dir);
                _fsw = new FileSystemWatcher(Dir, "status.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler h = (s, e) => Kick();
                _fsw.Changed += h;
                _fsw.Created += h;
                _fsw.Deleted += h;
                _fsw.Renamed += (s, e) => Kick();
            }
            catch
            {
                // не получилось следить за папкой — останется проверка раз в 2 секунды
            }

            _debounce = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
            _poll = new Timer(_ => Check(), null, 2000, 2000);
        }

        private void Kick()
        {
            // небольшая задержка, чтобы запись файла успела завершиться
            try { if (_debounce != null) _debounce.Change(150, Timeout.Infinite); } catch { }
        }

        private void Check()
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;

            try
            {
                bool connected = false;
                string info = "";

                if (File.Exists(FilePath))
                {
                    try
                    {
                        string json;
                        using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                            json = sr.ReadToEnd();

                        using (var doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;

                            string state = GetString(root, "state");
                            connected = state != null ? state == "connected" : GetBool(root, "connected");

                            // если приложение VPN закрылось аварийно — статус «подключён» устарел
                            if (connected && root.TryGetProperty("pid", out var pidEl) &&
                                pidEl.ValueKind == JsonValueKind.Number && pidEl.TryGetInt32(out int pid) && pid > 0)
                            {
                                if (!IsAlive(pid)) connected = false;
                            }

                            info = GetString(root, "country") ?? GetString(root, "server") ?? "";
                        }
                    }
                    catch
                    {
                        return; // файл пишется прямо сейчас или повреждён — оставляем прежнее состояние
                    }
                }
                else
                {
                    connected = AnyCoreRunning();
                }

                if (connected) _lastInfo = info;

                int prev = Volatile.Read(ref _state);
                int now = connected ? 1 : 0;

                if (prev == -1)
                {
                    Volatile.Write(ref _state, now); // первая проверка — без плашки
                    return;
                }

                if (prev == now) return;

                Volatile.Write(ref _state, now);
                string label = connected ? info : _lastInfo;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(connected, label)));
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static bool GetBool(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static bool AnyCoreRunning()
        {
            foreach (var name in CoreProcesses)
            {
                try
                {
                    var list = Process.GetProcessesByName(name);
                    bool any = list.Length > 0;
                    foreach (var p in list) p.Dispose();
                    if (any) return true;
                }
                catch { }
            }
            return false;
        }

        public void Dispose()
        {
            try { if (_poll != null) _poll.Dispose(); } catch { }
            try { if (_debounce != null) _debounce.Dispose(); } catch { }
            _poll = null;
            _debounce = null;

            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
        }
    }
}
