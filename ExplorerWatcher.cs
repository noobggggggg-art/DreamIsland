using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace DreamIsland
{
    /// <summary>
    /// Следит за окном «Проводника», пока оно активно:
    ///  — сколько файлов выделено;
    ///  — сколько файлов удалено или перемещено из открытой папки.
    /// Работает в отдельном потоке; когда проводник не в фокусе, почти ничего не делает.
    /// </summary>
    public sealed class ExplorerWatcher : IDisposable
    {
        private sealed class Entry
        {
            public FileSystemWatcher Watcher;
            public long LastActive;
        }

        private Thread _thread;
        private volatile bool _stop;
        private dynamic _shell;     // Shell.Application, живёт в потоке слежения

        private readonly Dictionary<string, Entry> _watchers =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // удаления / перемещения
        private readonly object _lock = new object();
        private int _removedCount;
        private long _removedFirst, _removedLast;
        private int _binBaseline = -1;
        private long _binBaselineAt;

        // выделение
        private IntPtr _lastHwnd;
        private string _lastPath;
        private int _pendingSel = -1, _emittedSel = -1;
        private long _pendingSince;

        /// <summary>Сколько файлов выделено (только 2 и больше). Вызывается в UI-потоке.</summary>
        public event Action<int> Selected;

        /// <summary>(сколько, true — удалено / false — перемещено). Вызывается в UI-потоке.</summary>
        public event Action<int, bool> Removed;

        public void Start()
        {
            if (_thread != null) return;

            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "DreamIsland.Explorer" };
            _thread.SetApartmentState(ApartmentState.STA);   // COM проводника требует STA
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null) t.Join(1000);
        }

        private void Run()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                _shell = Activator.CreateInstance(type);
            }
            catch
            {
                return;
            }

            while (!_stop)
            {
                try { Tick(); } catch { }
                Thread.Sleep(300);
            }

            Cleanup();
        }

        private void Tick()
        {
            long now = Environment.TickCount64;

            FlushRemoved(now);
            DropOldWatchers(now);

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || !IsExplorer(fg)) return;

            string path;
            int sel;
            if (!TryRead(fg, out path, out sel)) return;

            if (path != null) EnsureWatcher(path, now);

            // «базовая линия» корзины: по ней потом понимаем, ушли файлы в корзину или куда-то ещё
            bool pending;
            lock (_lock) { pending = _removedCount > 0; }
            if (!pending && (_binBaseline < 0 || now - _binBaselineAt > 4000))
            {
                _binBaseline = BinCount();
                _binBaselineAt = now;
            }

            // выделение: показываем, когда число «устоялось» (не при каждом движении рамки)
            if (fg != _lastHwnd || path != _lastPath)
            {
                _lastHwnd = fg;
                _lastPath = path;
                _pendingSel = sel;
                _emittedSel = sel;
                _pendingSince = now;
            }
            else if (sel != _pendingSel)
            {
                _pendingSel = sel;
                _pendingSince = now;
            }
            else if (sel != _emittedSel && now - _pendingSince >= 450)
            {
                _emittedSel = sel;
                if (sel >= 2)
                {
                    int n = sel;
                    Post(() => { var h = Selected; if (h != null) h(n); });
                }
            }
        }

        private bool TryRead(IntPtr fg, out string path, out int sel)
        {
            path = null;
            sel = 0;

            dynamic wins = null;
            try
            {
                wins = _shell.Windows();
                int n = wins.Count;
                long target = fg.ToInt64();

                for (int i = 0; i < n; i++)
                {
                    dynamic w = null;
                    try
                    {
                        w = wins.Item(i);
                        if (w == null) continue;
                        if (Convert.ToInt64(w.HWND) != target) continue;

                        dynamic doc = w.Document;
                        if (doc == null) continue;

                        sel = (int)doc.SelectedItems().Count;
                        try { path = (string)doc.Folder.Self.Path; } catch { path = null; }
                        return true;
                    }
                    catch { }
                    finally { Release(w); }
                }
            }
            catch { }
            finally { Release(wins); }

            return false;
        }

        private void EnsureWatcher(string path, long now)
        {
            Entry e;
            if (_watchers.TryGetValue(path, out e))
            {
                e.LastActive = now;
                return;
            }

            if (!Directory.Exists(path)) return; // «Этот компьютер», корзина и т.п.

            try
            {
                var w = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = true
                };
                // файл ушёл из папки (удалён или перемещён) — для наблюдателя это «Deleted»
                w.Deleted += (s, a) => OnRemoved();
                w.Error += (s, a) => { };

                _watchers[path] = new Entry { Watcher = w, LastActive = now };
            }
            catch { }
        }

        private void DropOldWatchers(long now)
        {
            if (_watchers.Count == 0) return;

            var old = new List<string>();
            foreach (var kv in _watchers)
                if (now - kv.Value.LastActive > 60000) old.Add(kv.Key);

            // не держим больше 4 папок
            while (_watchers.Count - old.Count > 4)
            {
                string oldest = null;
                long best = long.MaxValue;
                foreach (var kv in _watchers)
                {
                    if (old.Contains(kv.Key)) continue;
                    if (kv.Value.LastActive < best) { best = kv.Value.LastActive; oldest = kv.Key; }
                }
                if (oldest == null) break;
                old.Add(oldest);
            }

            foreach (var key in old)
            {
                try
                {
                    var w = _watchers[key].Watcher;
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
                _watchers.Remove(key);
            }
        }

        private void OnRemoved()
        {
            long now = Environment.TickCount64;
            lock (_lock)
            {
                if (_removedCount == 0) _removedFirst = now;
                _removedCount++;
                _removedLast = now;
            }
        }

        /// <summary>Когда поток событий затих (0,5 с) — подводим итог: удалено или перемещено.</summary>
        private void FlushRemoved(long now)
        {
            int n;
            long first;
            lock (_lock)
            {
                if (_removedCount == 0 || now - _removedLast < 500) return;
                n = _removedCount;
                first = _removedFirst;
                _removedCount = 0;
            }

            // 1) только что нажимали Delete (в том числе Shift+Delete без корзины)
            long key = Interlocked.Read(ref KeyWatcher.LastDeleteKeyTick);
            bool deleted = key != 0 && first - key >= -200 && first - key < 4000;

            // 2) в корзине стало больше файлов (удаление через меню, кнопку или перетаскивание в корзину)
            int bin = BinCount();
            if (!deleted && bin >= 0 && _binBaseline >= 0 && bin > _binBaseline) deleted = true;
            if (bin >= 0)
            {
                _binBaseline = bin;
                _binBaselineAt = now;
            }

            // иначе файлы просто ушли в другую папку — «перемещено»
            bool del = deleted;
            Post(() => { var h = Removed; if (h != null) h(n, del); });
        }

        private int BinCount()
        {
            dynamic bin = null, items = null;
            try
            {
                bin = _shell.NameSpace(10);      // 10 = корзина
                items = bin.Items();
                return (int)items.Count;
            }
            catch
            {
                return -1;
            }
            finally
            {
                Release(items);
                Release(bin);
            }
        }

        private static bool IsExplorer(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString() == "CabinetWClass";
        }

        private static void Post(Action a)
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(a);
        }

        private static void Release(object o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }

        private void Cleanup()
        {
            foreach (var kv in _watchers)
            {
                try
                {
                    kv.Value.Watcher.EnableRaisingEvents = false;
                    kv.Value.Watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();

            Release(_shell);
            _shell = null;
        }

        public void Dispose()
        {
            Stop();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
