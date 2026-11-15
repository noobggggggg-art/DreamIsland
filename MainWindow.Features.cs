using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Новые возможности: живой эквалайзер, OBS, буфер обмена, скриншоты, точка записи.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Color Snow = Color.FromRgb(255, 255, 255);
        private static readonly Color MicDotColor = Color.FromRgb(0x64, 0xD2, 0xFF);   // микрофон — голубой кружок
        private static readonly Color CamDotColor = Color.FromRgb(0xBF, 0x5A, 0xF2);   // камера — фиолетовый ромб

        // ---------- запуск и остановка ----------

        private void SetMenu(string tag, bool on)
        {
            var mi = FindMenu(tag);
            if (mi != null) mi.IsChecked = on;
        }

        private void InitFeatures()
        {
            _clipRows = new[] { ClipRow0, ClipRow1, ClipRow2, ClipRow3, ClipRow4 };
            _clipTexts = new[] { ClipText0, ClipText1, ClipText2, ClipText3, ClipText4 };

            _liveEqEnabled = !AppSettings.GetBool("noliveeq"); SetMenu("liveeq", _liveEqEnabled);
            _clipEnabled = !AppSettings.GetBool("noclip"); SetMenu("clip", _clipEnabled);
            _shotsEnabled = !AppSettings.GetBool("noshots"); SetMenu("shots", _shotsEnabled);
            _privEnabled = !AppSettings.GetBool("nopriv"); SetMenu("priv", _privEnabled);
            bool obsOn = AppSettings.GetBool("obs"); SetMenu("obs", obsOn);
            _filesEnabled = !AppSettings.GetBool("nofiles"); SetMenu("files", _filesEnabled);
            _scVolEnabled = !AppSettings.GetBool("noscvol"); SetMenu("scvol", _scVolEnabled);

            _eqTick.Tick += (s, e) => EqTick();
            _obsTick.Tick += (s, e) => UpdateObsText();
            _obs.Changed += OnObsChanged;
            _obs.AuthFailed += OnObsAuthFailed;
            _clipboard.Changed += OnClipboardChanged;
            _shots.Captured += OnShotCaptured;
            _priv.Changed += OnPrivChanged;
            _explorer.Selected += OnFilesSelected;
            _explorer.Removed += OnFilesRemoved;

            if (_clipEnabled) _clipboard.Start(this);
            if (_shotsEnabled) _shots.Start();
            if (_privEnabled) _priv.Start();
            if (obsOn) StartObs();
            if (_filesEnabled) _explorer.Start();

            // свечение кэшируется как картинка: пульсация не заставляет заново считать размытие
            GlowBack.CacheMode = new BitmapCache();

            // обновления
            _autoUpdate = !AppSettings.GetBool("noautoupdate"); SetMenu("autoupdate", _autoUpdate);
            var verItem = FindMenu("version");
            if (verItem != null) verItem.Header = "DreamIsland v" + Updater.CurrentText;
            _updateTimer.Tick += (s, e) => AutoCheckUpdate();
            _updateTimer.Start();
            var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            firstCheck.Tick += (s, e) => { firstCheck.Stop(); AutoCheckUpdate(); };
            firstCheck.Start();

            Apply();
        }

        private void DisposeFeatures()
        {
            _eqTick.Stop();
            _obsTick.Stop();
            _levels.Dispose();
            _obs.Stop();
            _clipboard.Dispose();
            _shots.Dispose();
            _priv.Dispose();
            _explorer.Dispose();
        }

        private void FeatureToggle_Click(object sender, RoutedEventArgs e)
        {
            var mi = (MenuItem)sender;
            bool on = mi.IsChecked;
            string tag = mi.Tag as string;

            switch (tag)
            {
                case "liveeq":
                    _liveEqEnabled = on;
                    AppSettings.SetBool("noliveeq", !on);
                    Apply();
                    UpdateLiveEq();
                    break;

                case "clip":
                    _clipEnabled = on;
                    AppSettings.SetBool("noclip", !on);
                    if (on) _clipboard.Start(this);
                    else { _clipboard.Stop(); _clipHistory.Clear(); RefreshClipPanel(); }
                    break;

                case "shots":
                    _shotsEnabled = on;
                    AppSettings.SetBool("noshots", !on);
                    if (on) _shots.Start(); else _shots.Stop();
                    break;

                case "priv":
                    _privEnabled = on;
                    AppSettings.SetBool("nopriv", !on);
                    if (on) _priv.Start();
                    else { _priv.Stop(); ApplyPriv(new List<string>(), new List<string>()); }
                    break;

                case "autoupdate":
                    _autoUpdate = on;
                    AppSettings.SetBool("noautoupdate", !on);
                    break;

                case "files":
                    _filesEnabled = on;
                    AppSettings.SetBool("nofiles", !on);
                    if (on) _explorer.Start(); else _explorer.Stop();
                    break;

                case "scvol":
                    _scVolEnabled = on;
                    AppSettings.SetBool("noscvol", !on);
                    break;

                case "obs":
                    AppSettings.SetBool("obs", on);
                    if (on) StartObs(); else StopObs();
                    break;
            }
        }

        // ---------- ширина острова с учётом всех индикаторов ----------

        private const double ObsSingleExtra = 84;
        private const double ObsBothExtra = 118;
        private const double PrivDotExtra = 14;

        private int _obsKind;                 // 0 — ничего, 1 — REC или LIVE, 2 — оба
        private bool _privMicOn, _privCamOn;

        private double ExtraFor(IslandState st)
        {
            bool music = st == IslandState.Compact || st == IslandState.Expanded;
            bool idle = st == IslandState.Idle || st == IslandState.IdleHover;
            if (!music && !idle) return 0;

            double e = 0;
            if (music && _tk != TimerKind.None) e += TimerExtra;
            if (_obsKind == 1) e += ObsSingleExtra;
            else if (_obsKind == 2) e += ObsBothExtra;
            if (_privMicOn) e += PrivDotExtra;
            if (_privCamOn) e += PrivDotExtra;
            return e;
        }

        private double BaseWidth(IslandState st)
        {
            switch (st)
            {
                case IslandState.Expanded: return ExpandedSize.W;
                case IslandState.Compact: return CompactSize.W;
                case IslandState.IdleHover: return IdleHoverSize.W;
                case IslandState.Toast: return ToastSize.W;
                case IslandState.Clip: return ClipSizeNow().W;
                default: return IdleSize.W;
            }
        }

        private void RefreshWidth()
        {
            if (_state == null) return;
            var st = _state.Value;
            if (st == IslandState.Toast || st == IslandState.Clip) return;

            Island.BeginAnimation(WidthProperty, Spring(BaseWidth(st) + ExtraFor(st)));
            if (st == IslandState.Compact) RefreshMarquee();
        }

        // ---------- живой эквалайзер ----------

        private readonly AudioLevelMonitor _levels = new AudioLevelMonitor();
        private readonly DispatcherTimer _eqTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private bool _liveEqEnabled = true, _eqRunning;
        private float _pkBass = 0.02f, _pkMid = 0.02f, _pkHigh = 0.01f;
        private float _sBass, _sMid, _sHigh;

        private void UpdateLiveEq()
        {
            bool want = _liveEqEnabled && _playing &&
                        (_state == IslandState.Compact || _state == IslandState.Expanded);
            if (want == _eqRunning) return;
            _eqRunning = want;

            if (want)
            {
                _levels.Start();
                _eqTick.Start();
            }
            else
            {
                _eqTick.Stop();
                _levels.Stop();
                _sBass = _sMid = _sHigh = 0;
                Bar1.ScaleY = Bar2.ScaleY = Bar3.ScaleY = 0.3;
                GlowBack.Opacity = 1;
            }
        }

        private void EqTick()
        {
            float b, m, h;
            _levels.GetLevels(out b, out m, out h);

            // автоусиление: подстраиваемся под громкость трека, пик медленно «забывается»
            _pkBass = Math.Max(b, _pkBass * 0.997f);
            _pkMid = Math.Max(m, _pkMid * 0.997f);
            _pkHigh = Math.Max(h, _pkHigh * 0.997f);

            float nb = Math.Min(1f, b / Math.Max(_pkBass, 0.01f));
            float nm = Math.Min(1f, m / Math.Max(_pkMid, 0.01f));
            float nh = Math.Min(1f, h / Math.Max(_pkHigh, 0.005f));

            // быстро вверх, плавно вниз
            _sBass = nb > _sBass ? nb : _sBass * 0.82f + nb * 0.18f;
            _sMid = nm > _sMid ? nm : _sMid * 0.82f + nm * 0.18f;
            _sHigh = nh > _sHigh ? nh : _sHigh * 0.82f + nh * 0.18f;

            Bar1.ScaleY = 0.2 + 0.8 * _sBass;
            Bar2.ScaleY = 0.2 + 0.8 * _sMid;
            Bar3.ScaleY = 0.2 + 0.8 * _sHigh;

            // подсветка острова пульсирует в такт басам
            GlowBack.Opacity = 0.35 + 0.65 * _sBass;
        }

        // ---------- OBS ----------

        private readonly ObsWatcher _obs = new ObsWatcher();
        private readonly DispatcherTimer _obsTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool _obsRec, _obsStream;
        private DateTime _obsRecStart, _obsStreamStart;

        private void StartObs()
        {
            _obs.Password = AppSettings.GetString("obs_password");
            _obs.Port = AppSettings.GetInt("obs_port", 4455);
            _obs.Start();
        }

        private void StopObs()
        {
            _obs.Stop();
            _obsRec = false;
            _obsStream = false;
            _obsTick.Stop();
            UpdateObsText();
        }

        private void OnObsChanged(bool rec, TimeSpan recEl, bool stream, TimeSpan strEl)
        {
            bool wasRec = _obsRec, wasStr = _obsStream;
            _obsRec = rec;
            _obsStream = stream;
            _obsRecStart = DateTime.UtcNow - recEl;
            _obsStreamStart = DateTime.UtcNow - strEl;

            if (rec != wasRec)
                ShowToast("\u25CF", true, rec ? Red : Gray, "OBS · запись",
                          rec ? "Старт" : "Стоп", rec ? Red : Gray, -1, 2500);
            else if (stream != wasStr)
                ShowToast("\u25CF", true, stream ? Red : Gray, "OBS · эфир",
                          stream ? "Старт" : "Стоп", stream ? Red : Gray, -1, 2500);

            if (rec || stream) _obsTick.Start(); else _obsTick.Stop();
            UpdateObsText();
        }

        private void OnObsAuthFailed()
        {
            ShowToast("\uE7BA", false, Red, "OBS", "Пароль?", Red, -1, 3500);
        }

        private void UpdateObsText()
        {
            string t = "";
            int kind = 0;

            if (_obsRec || _obsStream)
            {
                var parts = new List<string>();
                if (_obsStream) parts.Add("LIVE");
                if (_obsRec) parts.Add("REC");

                DateTime start;
                if (_obsRec && _obsStream) start = _obsRecStart < _obsStreamStart ? _obsRecStart : _obsStreamStart;
                else start = _obsRec ? _obsRecStart : _obsStreamStart;

                TimeSpan el = DateTime.UtcNow - start;
                if (el < TimeSpan.Zero) el = TimeSpan.Zero;

                t = "\u25CF " + string.Join("+", parts) + " " + Fmt(el);
                kind = _obsRec && _obsStream ? 2 : 1;
            }

            ObsText.Text = t;
            IdleObsText.Text = t;
            var vis = t.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ObsText.Visibility = vis;
            IdleObsText.Visibility = vis;

            if (kind != _obsKind)
            {
                _obsKind = kind;
                RefreshWidth();
            }
        }

        private void ObsPassword_Click(object sender, RoutedEventArgs e)
        {
            string pw = InputDialog.AskPassword(
                "DreamIsland — OBS",
                "Пароль OBS WebSocket (в OBS: Инструменты → Настройки сервера WebSocket). " +
                "Если пароль в OBS отключён, оставь поле пустым.",
                AppSettings.GetString("obs_password"));

            if (pw == null) return;

            AppSettings.SetString("obs_password", pw);
            var mi = FindMenu("obs");
            if (mi != null && mi.IsChecked) StartObs();
        }

        // ---------- буфер обмена ----------

        private readonly ClipboardWatcher _clipboard = new ClipboardWatcher();
        private readonly List<string> _clipHistory = new List<string>();
        private bool _clipEnabled = true, _clipForceExit;
        private DateTime _clipUntil = DateTime.MinValue;
        private string _selfSetText;
        private DateTime _selfSetAt = DateTime.MinValue;
        private DateTime _lastShotToast = DateTime.MinValue;
        private Border[] _clipRows;
        private TextBlock[] _clipTexts;

        /// <summary>Список буфера показывается, если навести мышь в течение 8 секунд после копирования.</summary>
        private bool WantClip()
        {
            return _clipEnabled && !_clipForceExit && _hover && _clipHistory.Count > 0 &&
                   (DateTime.UtcNow < _clipUntil || _state == IslandState.Clip);
        }

        private (double W, double H, double R, double Art) ClipSizeNow()
        {
            int n = Math.Min(5, Math.Max(1, _clipHistory.Count));
            return (360, 44 + 30 * n, 26, 22);
        }

        private async void OnClipboardChanged()
        {
            // программа, скопировавшая данные, может ещё держать буфер — пробуем несколько раз
            for (int attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(attempt == 0 ? 40 : 100);
                try
                {
                    if (HandleClipboard()) return;
                }
                catch { }
            }
        }

        private static bool IsClipboardExcluded()
        {
            // менеджеры паролей помечают скопированное как «не сохранять» — уважаем это
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;

                if (Clipboard.ContainsData("CanIncludeInClipboardHistory"))
                {
                    var ms = Clipboard.GetData("CanIncludeInClipboardHistory") as System.IO.MemoryStream;
                    if (ms != null)
                    {
                        var b = ms.ToArray();
                        if (b.Length > 0 && b[0] == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private bool HandleClipboard()
        {
            if (!_clipEnabled) return true;
            if (IsClipboardExcluded()) return true;

            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText();
                if (string.IsNullOrEmpty(t)) return true;

                // это мы сами положили текст при выборе из списка
                if (_selfSetText != null && t == _selfSetText && (DateTime.UtcNow - _selfSetAt).TotalSeconds < 3)
                    return true;

                _clipHistory.Remove(t);
                _clipHistory.Insert(0, t);
                while (_clipHistory.Count > 5) _clipHistory.RemoveAt(_clipHistory.Count - 1);

                _clipUntil = DateTime.UtcNow.AddSeconds(8);
                RefreshClipPanel();

                ShowToast("\uE8C8", false, Snow, "Скопировано", t.Length + " симв.", Gray, -1, 1400);
                return true;
            }

            if (Clipboard.ContainsImage())
            {
                // если только что был скриншот-файл, второй раз не показываем
                if ((DateTime.UtcNow - _lastShotToast).TotalSeconds < 2.5) return true;

                var img = Clipboard.GetImage();
                BitmapSource thumb = img == null ? null : MakeThumb(img, 56);
                ShowToast("\uE8B9", false, Snow, "Изображение", "Скопировано", Green, -1, 2000, thumb);
                return true;
            }

            return true;
        }

        private static BitmapSource MakeThumb(BitmapSource src, double target)
        {
            try
            {
                double sc = target / Math.Max(src.PixelWidth, src.PixelHeight);
                var tb = new TransformedBitmap(src, new ScaleTransform(sc, sc));
                try { tb.Freeze(); } catch { }
                return tb;
            }
            catch
            {
                return null;
            }
        }

        private static string Preview(string t)
        {
            t = t.Replace("\r", " ").Replace("\n", " \u21B5 ").Trim();
            return t.Length > 60 ? t.Substring(0, 60) + "\u2026" : t;
        }

        private void RefreshClipPanel()
        {
            if (_clipRows == null) return;

            for (int i = 0; i < 5; i++)
            {
                bool has = i < _clipHistory.Count;
                _clipRows[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                if (has) _clipTexts[i].Text = Preview(_clipHistory[i]);
            }

            if (_state == IslandState.Clip)
            {
                var s = ClipSizeNow();
                Island.BeginAnimation(WidthProperty, Spring(s.W));
                Island.BeginAnimation(HeightProperty, Spring(s.H));
            }
        }

        private void Clip_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            var fe = sender as FrameworkElement;
            int idx;
            if (fe == null || !int.TryParse(fe.Tag as string, out idx) || idx < 0 || idx >= _clipHistory.Count) return;

            string text = _clipHistory[idx];
            try
            {
                _selfSetText = text;
                _selfSetAt = DateTime.UtcNow;
                Clipboard.SetText(text);
            }
            catch
            {
                return;
            }

            _clipHistory.RemoveAt(idx);
            _clipHistory.Insert(0, text);
            RefreshClipPanel();

            // закрываем список и вставляем текст в то окно, где ты работал
            _clipForceExit = true;
            _clipUntil = DateTime.MinValue;
            Apply();

            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            t.Tick += (s, a) => { t.Stop(); ClipboardWatcher.PasteCtrlV(); };
            t.Start();
        }

        private void ClipClear_Click(object sender, RoutedEventArgs e)
        {
            _clipHistory.Clear();
            RefreshClipPanel();
            Apply();
        }

        // ---------- скриншоты ----------

        private readonly ScreenshotWatcher _shots = new ScreenshotWatcher();
        private bool _shotsEnabled = true;

        private void OnShotCaptured(string path, BitmapSource thumb)
        {
            if (!_shotsEnabled) return;
            _lastShotToast = DateTime.UtcNow;
            ShowToast("\uE91B", false, Snow, "Скриншот", "Сохранено", Green, -1, 2500, thumb);
        }

        // ---------- обновления с GitHub ----------

        private bool _autoUpdate = true, _updating;
        private readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };

        /// <summary>Автопроверка: не чаще раза в 12 часов.</summary>
        private void AutoCheckUpdate()
        {
            if (!_autoUpdate) return;

            long last;
            if (long.TryParse(AppSettings.GetString("update_last", "0"), out last) && last > 0)
            {
                var when = new DateTime(last, DateTimeKind.Utc);
                if ((DateTime.UtcNow - when).TotalHours < 12) return;
            }

            _ = CheckUpdateAsync(false);
        }

        private async Task CheckUpdateAsync(bool manual)
        {
            if (_updating) return;
            _updating = true;

            try
            {
                string repo = AppSettings.GetString("update_repo", Updater.DefaultRepo);
                if (!Updater.IsRepoConfigured(repo))
                {
                    if (manual) ShowToast("\uE7BA", false, Gray, "Обновления", "нужен репозиторий", Gray, -1, 2500);
                    return;
                }

                // обновление уже скачано раньше — ставим сразу
                if (manual && Updater.IsSingleFile && Updater.HasPending())
                {
                    InstallPendingAndExit();
                    return;
                }

                if (manual) ShowToast("\uE895", false, Snow, "Обновления", "проверка…", Gray, -1, 1500);

                UpdateInfo info = await Updater.CheckAsync(repo);
                AppSettings.SetString("update_last", DateTime.UtcNow.Ticks.ToString());

                if (info == null)
                {
                    if (manual) ShowToast("\uE73E", false, Green, "Обновления", "версия последняя", Green, -1, 2000);
                    return;
                }

                string ver = "v" + info.Version.Major + "." + info.Version.Minor + "." + info.Version.Build;

                // запуск из исходников (dotnet run): ставить нечего, просто сообщаем
                if (!Updater.IsSingleFile)
                {
                    ShowToast("\uE895", false, Orange, "Новая версия", ver, Orange, -1, 3000);
                    if (manual)
                        Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
                    return;
                }

                ShowToast("\uE895", false, Orange, "Скачиваю", ver, Orange, -1, 2500);
                string file = await Updater.DownloadAsync(info);
                Updater.SetPending(file, info.Version);

                if (manual)
                    InstallPendingAndExit();
                else
                    ShowToast("\uE73E", false, Green, "Обновление " + ver, "при запуске", Green, -1, 4000);
            }
            catch
            {
                if (manual) ShowToast("\uE7BA", false, Red, "Обновления", "ошибка", Red, -1, 2500);
            }
            finally
            {
                _updating = false;
            }
        }

        private void InstallPendingAndExit()
        {
            try
            {
                Updater.ApplyAndRestart(Updater.PendingFile());
                Application.Current.Shutdown();
            }
            catch
            {
                ShowToast("\uE7BA", false, Red, "Обновления", "не удалось", Red, -1, 2500);
            }
        }

        private void UpdateCheck_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckUpdateAsync(true);
        }

        private void UpdateRepo_Click(object sender, RoutedEventArgs e)
        {
            string current = AppSettings.GetString("update_repo", Updater.DefaultRepo);
            string v = InputDialog.AskText(
                "DreamIsland — обновления",
                "Репозиторий GitHub в виде «логин/DreamIsland» или ссылка на него.",
                current);

            if (v == null) return;
            AppSettings.SetString("update_repo", Updater.NormalizeRepo(v));
        }

        // ---------- файлы в проводнике: выделено, удалено, перемещено ----------

        private readonly ExplorerWatcher _explorer = new ExplorerWatcher();
        private bool _filesEnabled = true;

        private void OnFilesSelected(int n)
        {
            if (!_filesEnabled) return;
            ShowToast("\u2611", true, Snow, "Выделено", n + " шт.", Snow, -1, 1500);
        }

        private void OnFilesRemoved(int n, bool deleted)
        {
            if (!_filesEnabled) return;

            if (deleted)
                ShowToast("\uE74D", false, Red, "Удалено", n + " шт.", Red, -1, 1500);
            else
                ShowToast("\u2192", true, Orange, "Перемещено", n + " шт.", Orange, -1, 1500);
        }

        // ---------- Shift + колёсико: громкость SoundCloud ----------

        private bool _scVolEnabled = true;
        private static readonly Color ScOrange = Color.FromRgb(0xFF, 0x55, 0x00);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsShiftDown()
        {
            return (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT
        }

        private void AdjustSoundCloud(double delta)
        {
            string app;
            int pct;

            if (AppVolume.TryAdjust(delta, out app, out pct))
            {
                string icon = pct == 0 ? "\uE74F" : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
                ShowToast(icon, false, ScOrange, "", pct + "%", ScOrange, pct / 100.0, 1400);
            }
            else
            {
                ShowToast("\uE7BA", false, Gray, "SoundCloud", "не найден", Gray, -1, 1500);
            }
        }

        // ---------- точка записи: кто использует микрофон / камеру ----------

        private readonly PrivacyWatcher _priv = new PrivacyWatcher();
        private bool _privEnabled = true;
        private List<string> _privMicApps = new List<string>();
        private List<string> _privCamApps = new List<string>();

        private void OnPrivChanged(List<string> mic, List<string> cam)
        {
            if (!_privEnabled) return;

            foreach (var a in mic)
                if (!_privMicApps.Contains(a))
                    ShowToast("\uE720", false, MicDotColor, a, "Микрофон", MicDotColor, -1, 2500);

            foreach (var a in cam)
                if (!_privCamApps.Contains(a))
                    ShowToast("\uE722", false, CamDotColor, a, "Камера", CamDotColor, -1, 2500);

            ApplyPriv(mic, cam);
        }

        private void ApplyPriv(List<string> mic, List<string> cam)
        {
            _privMicApps = mic;
            _privCamApps = cam;

            bool m = mic.Count > 0, c = cam.Count > 0;
            bool changed = m != _privMicOn || c != _privCamOn;
            _privMicOn = m;
            _privCamOn = c;

            foreach (UIElement el in new UIElement[] { PrivMic, IdlePrivMic })
            {
                el.Visibility = m ? Visibility.Visible : Visibility.Collapsed;
                Pulse(el, m);
            }
            foreach (UIElement el in new UIElement[] { PrivCam, IdlePrivCam })
            {
                el.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
                Pulse(el, c);
            }

            if (changed) RefreshWidth();
        }

        private static void Pulse(UIElement el, bool on)
        {
            if (on)
            {
                var a = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(900))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Timeline.SetDesiredFrameRate(a, 10);
                el.BeginAnimation(OpacityProperty, a);
            }
            else
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 1;
            }
        }
    }
}
