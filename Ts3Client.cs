using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;

namespace DreamIsland
{
    /// <summary>Что сейчас происходит на сервере TeamSpeak 3.</summary>
    public sealed class Ts3State
    {
        public bool Connected;
        public string Server = "";
        public string Me = "";
        public bool MicMuted, SpeakersMuted;
        public List<string> Talking = new List<string>();
        public int Clients;

        public string Key()
        {
            return Connected + "|" + Server + "|" + Me + "|" + MicMuted + "|" + SpeakersMuted + "|" +
                   string.Join(",", Talking) + "|" + Clients;
        }
    }

    /// <summary>
    /// TeamSpeak 3 через плагин ClientQuery (он встроен в клиент, работает только на твоём компьютере):
    /// кто говорит, включены ли микрофон и звук, к какому серверу подключён.
    /// Включить плагин: Tools → Options → Addons → Plugins → ClientQuery; API-ключ — кнопка Settings там же.
    /// </summary>
    public sealed class Ts3Client : IDisposable
    {
        public string ApiKey = "";
        public int Port = 25639;

        /// <summary>Понятное описание, на каком этапе связь с TeamSpeak (для проверки подключения).</summary>
        public string Status = "Слежение не запущено.";

        /// <summary>0 — не запущено, 1 — нет связи с плагином, 2 — неверный ключ, 3 — вы не на сервере, 4 — всё работает.</summary>
        public int StatusCode;

        /// <summary>Этап связи изменился. Вызывается в UI-потоке.</summary>
        public event Action<int, string> StatusChanged;

        /// <summary>Состояние изменилось. Вызывается в UI-потоке.</summary>
        public event Action<Ts3State> Changed;

        /// <summary>TeamSpeak отклонил API-ключ. Вызывается в UI-потоке.</summary>
        public event Action AuthFailed;

        private Thread _thread;
        private volatile bool _stop;
        private TcpClient _client;
        private string _lastKey;
        private bool _authReported;
        private volatile string _stage = "";

        public void Start()
        {
            Stop();
            _stop = false;
            _authReported = false;
            _lastKey = null;

            SetStatus(1, "Подключаюсь к плагину ClientQuery…");
            _thread = new Thread(Run) { IsBackground = true, Name = "DreamIsland.TS3" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { if (_client != null) _client.Close(); } catch { }

            var t = _thread;
            _thread = null;
            if (t != null) t.Join(1500);
        }

        private void Run()
        {
            while (!_stop)
            {
                try { Session(); }
                catch (Exception ex)
                {
                    // неверный ключ уже отмечен; во всех остальных случаях связи нет
                    if (StatusCode != 2)
                        SetStatus(1, "Нет связи с плагином ClientQuery (порт " + Port + ").\n" +
                                     "Причина: " + ex.GetType().Name + ": " + ex.Message + "\n" +
                                     "Этап: " + _stage);
                }

                if (_stop) break;

                Publish(new Ts3State());   // TeamSpeak закрыт или нет связи

                // повторная попытка: реже, если ключ неверный
                int wait = _authReported ? 20000 : 4000;
                for (int t = 0; t < wait && !_stop; t += 100) Thread.Sleep(100);
            }

            Publish(new Ts3State());
        }

        private void Session()
        {
            using (var client = new TcpClient())
            {
                _client = client;
                client.NoDelay = true;
                _stage = "подключение к 127.0.0.1:" + Port;
                client.Connect("127.0.0.1", Port);
                _stage = "приветствие";

                var io = new Io(client);
                io.Drain(600); // приветственные строки TeamSpeak

                int err;
                _stage = "auth";
                io.Command("auth apikey=" + (ApiKey ?? ""), out err);
                if (err != 0)
                {
                    ReportAuthFailed();
                    throw new InvalidOperationException("Неверный API-ключ");
                }

                _stage = "clientnotifyregister";
                io.Command("clientnotifyregister schandlerid=0 event=any", out err);
                SetStatus(3, "Плагин ClientQuery отвечает, ключ принят. Жду подключения к серверу…");

                long lastRefresh = 0;
                io.Dirty = true;

                while (!_stop)
                {
                    // сообщения от TeamSpeak («кто-то заговорил» и т.п.)
                    string line;
                    while (io.TryReadLine(40, out line))
                        if (line.StartsWith("notify", StringComparison.Ordinal)) io.Dirty = true;

                    long now = Environment.TickCount64;
                    if ((io.Dirty && now - lastRefresh > 150) || now - lastRefresh > 2000)
                    {
                        io.Dirty = false;
                        lastRefresh = now;
                        _stage = "опрос состояния";
                        Refresh(io);
                    }
                }
            }
        }

        private void Refresh(Io io)
        {
            int err;
            var ids = new List<int>();

            // текущая вкладка TeamSpeak — в приоритете
            var cur = io.Command("currentschandlerid", out err);
            if (err == 0 && cur.Count > 0) AddId(ids, Parse(cur[0]));

            var all = io.Command("serverconnectionhandlerlist", out err);
            if (err == 0 && all.Count > 0)
                foreach (var rec in all[0].Split('|')) AddId(ids, Parse(rec));

            Ts3State state = null;
            foreach (int id in ids)
            {
                io.Command("use schandlerid=" + id, out err);
                if (err != 0) continue;

                var who = io.Command("whoami", out err);
                if (err != 0 || who.Count == 0) continue;   // эта вкладка ни к чему не подключена

                var w = Parse(who[0]);
                string clid;
                if (!w.TryGetValue("clid", out clid)) continue;

                state = BuildState(io, clid);
                break;
            }

            Publish(state ?? new Ts3State());
        }

        private static void AddId(List<int> ids, Dictionary<string, string> rec)
        {
            string s;
            int v;
            if (rec.TryGetValue("schandlerid", out s) && int.TryParse(s, out v) && !ids.Contains(v))
                ids.Add(v);
        }

        private static Ts3State BuildState(Io io, string myClid)
        {
            var st = new Ts3State { Connected = true };
            int err;

            var sv = io.Command("servervariable virtualserver_name", out err);
            if (err == 0 && sv.Count > 0)
            {
                var r = Parse(sv[0]);
                string name;
                if (r.TryGetValue("virtualserver_name", out name)) st.Server = name;
            }

            var list = io.Command("clientlist -voice", out err);
            if (err == 0 && list.Count > 0)
            {
                foreach (var rec in list[0].Split('|'))
                {
                    var c = Parse(rec);
                    if (Get(c, "client_type") != "0") continue;   // только живые клиенты, без служебных

                    st.Clients++;
                    string nick = Get(c, "client_nickname");

                    if (Get(c, "clid") == myClid)
                    {
                        st.Me = nick;
                        st.MicMuted = Get(c, "client_input_muted") == "1";
                        st.SpeakersMuted = Get(c, "client_output_muted") == "1";
                    }

                    if (Get(c, "client_flag_talking") == "1") st.Talking.Add(nick);
                }
            }

            return st;
        }

        private static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        // ---------- разбор ответов ----------

        private static Dictionary<string, string> Parse(string rec)
        {
            var d = new Dictionary<string, string>();
            foreach (var token in rec.Split(' '))
            {
                if (token.Length == 0) continue;
                int eq = token.IndexOf('=');
                if (eq < 0) d[token] = "";
                else d[token.Substring(0, eq)] = Unescape(token.Substring(eq + 1));
            }
            return d;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    switch (s[i])
                    {
                        case 's': sb.Append(' '); break;
                        case 'p': sb.Append('|'); break;
                        case '/': sb.Append('/'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(' '); break;   // \n \t и прочие служебные
                    }
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ---------- результат ----------

        private void SetStatus(int code, string text)
        {
            if (code == StatusCode && text == Status) return;
            StatusCode = code;
            Status = text;

            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var h = StatusChanged;
                    if (h != null) h(code, text);
                }));
            }
        }

        private void Publish(Ts3State state)
        {
            if (state.Connected)
                SetStatus(4, "Всё работает. Сервер: " + (string.IsNullOrEmpty(state.Server) ? "(без названия)" : state.Server) + ".");
            else if (StatusCode == 4)
                SetStatus(3, "Плагин ClientQuery отвечает, но ты не подключён к серверу.");

            string key = state.Key();
            if (key == _lastKey) return;
            _lastKey = key;

            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var h = Changed;
                    if (h != null) h(state);
                }));
            }
        }

        private void ReportAuthFailed()
        {
            SetStatus(2, "TeamSpeak отклонил API-ключ. Скопируй ключ заново: Tools → Options → Addons → Plugins → ClientQuery → Settings.");
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

        public void Dispose()
        {
            Stop();
        }

        // ---------- строки по сети ----------

        private sealed class Io
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _s;
            private readonly Decoder _dec = Encoding.UTF8.GetDecoder();
            private readonly byte[] _tmp = new byte[4096];
            private readonly char[] _chars = new char[4096];
            private readonly StringBuilder _acc = new StringBuilder();
            private readonly Queue<string> _lines = new Queue<string>();

            public bool Dirty;

            public Io(TcpClient tcp)
            {
                _tcp = tcp;
                _s = tcp.GetStream();
            }

            private void Fill(int timeoutMs)
            {
                int waited = 0;
                while (!_s.DataAvailable && waited < timeoutMs)
                {
                    // соединение закрыто с той стороны?
                    if (_tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0)
                        throw new IOException("Соединение закрыто");
                    Thread.Sleep(5);
                    waited += 5;
                }
                if (!_s.DataAvailable) return;

                int n = _s.Read(_tmp, 0, _tmp.Length);
                if (n <= 0) throw new IOException("Соединение закрыто");

                int c = _dec.GetChars(_tmp, 0, n, _chars, 0);
                _acc.Append(_chars, 0, c);

                int idx;
                while ((idx = IndexOfNewline()) >= 0)
                {
                    string line = _acc.ToString(0, idx).Trim('\r', '\n');   // TeamSpeak заканчивает строки как \n\r, поэтому \r может стоять и в начале следующей
                    _acc.Remove(0, idx + 1);
                    if (line.Length > 0) _lines.Enqueue(line);
                }
            }

            private int IndexOfNewline()
            {
                for (int i = 0; i < _acc.Length; i++)
                    if (_acc[i] == '\n') return i;
                return -1;
            }

            public bool TryReadLine(int timeoutMs, out string line)
            {
                if (_lines.Count == 0) Fill(timeoutMs);
                if (_lines.Count > 0)
                {
                    line = _lines.Dequeue();
                    return true;
                }
                line = null;
                return false;
            }

            /// <summary>Выбрасывает всё, что приходит в течение timeoutMs (приветствие сервера).</summary>
            public void Drain(int timeoutMs)
            {
                string l;
                while (TryReadLine(timeoutMs, out l)) { }
            }

            /// <summary>Отправляет команду и собирает ответ до строки «error id=…».</summary>
            public List<string> Command(string cmd, out int errId)
            {
                var bytes = Encoding.UTF8.GetBytes(cmd + "\n");
                _s.Write(bytes, 0, bytes.Length);

                var data = new List<string>();
                errId = -1;
                long deadline = Environment.TickCount64 + 4000;

                while (Environment.TickCount64 < deadline)
                {
                    string line;
                    if (!TryReadLine(100, out line)) continue;

                    if (line.StartsWith("error ", StringComparison.Ordinal))
                    {
                        errId = ParseErrId(line);
                        return data;
                    }
                    if (line.StartsWith("notify", StringComparison.Ordinal))
                    {
                        Dirty = true;
                        continue;
                    }
                    data.Add(line);
                }

                throw new TimeoutException("TeamSpeak не ответил");
            }

            private static int ParseErrId(string line)
            {
                int i = line.IndexOf("id=", StringComparison.Ordinal);
                if (i < 0) return -1;
                i += 3;
                int j = i;
                while (j < line.Length && char.IsDigit(line[j])) j++;
                int v;
                return int.TryParse(line.Substring(i, j - i), out v) ? v : -1;
            }
        }
    }
}
