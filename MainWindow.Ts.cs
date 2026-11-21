using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Остров TeamSpeak 3: кто говорит, микрофон и звук TeamSpeak, название сервера, эффекты, Alt + колёсико.
    /// Пока ты подключён к серверу, этот остров заменяет музыкальный.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly Color TsGreen = Color.FromRgb(0x30, 0xD1, 0x58);
        private static readonly Color TsBlue = Color.FromRgb(0x3B, 0x82, 0xF6);

        private readonly Ts3Client _ts3 = new Ts3Client();
        private bool _ts3Enabled, _tsTalking, _tsFx;
        private Ts3State _ts = new Ts3State();
        private readonly DispatcherTimer _tsTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        private bool TsActive { get { return _ts3Enabled && _ts != null && _ts.Connected; } }

        private void InitTs3()
        {
            _ts3Enabled = AppSettings.GetBool("ts3");
            SetMenu("ts3", _ts3Enabled);

            _ts3.Changed += OnTs3Changed;
            _ts3.AuthFailed += OnTs3AuthFailed;
            _ts3.StatusChanged += OnTs3Status;
            _tsTick.Tick += (s, e) => RefreshTsMore();

            if (_ts3Enabled) StartTs3();
            UpdateTsUi();
        }

        private void StartTs3()
        {
            _ts3.ApiKey = AppSettings.GetString("ts3_key");
            _ts3.Port = AppSettings.GetInt("ts3_port", 25639);
            _ts3.Start();
        }

        // ---------- состояние ----------

        private void OnTs3Changed(Ts3State st)
        {
            if (!_ts3Enabled) return;

            bool was = TsActive;
            _ts = st;
            _tsTalking = st.Connected && st.Talking.Count > 0;

            if (TsActive != was)
            {
                ShowToast("\uE7F6", false, TsActive ? TsGreen : Gray, TsLabel(),
                          TsActive ? "подключён" : "отключён", TsActive ? TsGreen : Gray, -1, 1800);
            }

            UpdateTsUi();
            UpdateTsEffects();
            Apply();
        }

        /// <summary>Коротко сообщает, на каком этапе остановилась связь (кроме «всё работает» — это видно по острову).</summary>
        private void OnTs3Status(int code, string text)
        {
            if (!_ts3Enabled) return;

            if (code == 1) ShowToast("\uE7BA", false, Gray, "TeamSpeak", "нет связи", Gray, -1, 2500);
            else if (code == 3) ShowToast("\uE7F6", false, Gray, "TeamSpeak", "не на сервере", Gray, -1, 2500);
        }

        private void Ts3Check_Click(object sender, RoutedEventArgs e)
        {
            if (!_ts3Enabled)
            {
                MessageBox.Show("Слежение за TeamSpeak 3 выключено. Включи пункт «Следить за TeamSpeak 3» в меню острова.",
                                "DreamIsland — TeamSpeak 3", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string clientState = AppVolume.IsTeamSpeakRunning()
                ? "Клиент TeamSpeak 3 запущен."
                : "Клиент TeamSpeak 3 НЕ запущен (процесс ts3client не найден).";

            string key = AppSettings.GetString("ts3_key");
            string keyState = string.IsNullOrEmpty(key) ? "API-ключ не задан." : "API-ключ задан (" + key.Length + " символов).";

            MessageBox.Show(clientState + "\n" + keyState + "\n\n" + _ts3.Status,
                            "DreamIsland — TeamSpeak 3", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnTs3AuthFailed()
        {
            ShowToast("\uE7BA", false, Red, "TeamSpeak", "API-ключ?", Red, -1, 3500);
        }

        // ---------- название сервера (своё имя для удобства) ----------

        private static string NameKey(string name)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((name ?? "").ToLowerInvariant()));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private string TsLabel()
        {
            string name = _ts.Server ?? "";
            string custom = AppSettings.GetString("ts3name_" + NameKey(name), "");
            if (custom.Length > 0) return custom;
            return name.Length > 0 ? name : "TeamSpeak";
        }

        private string TalkText()
        {
            if (_ts.Talking.Count == 0) return "Тихо";

            var names = new List<string>();
            foreach (var n in _ts.Talking) names.Add(n == _ts.Me ? "Вы" : n);

            if (names.Count <= 2) return string.Join(", ", names);
            return names[0] + ", " + names[1] + " +" + (names.Count - 2);
        }

        // ---------- вид острова ----------

        private void UpdateTsUi()
        {
            TsServer.Text = TsLabel();
            TsTalk.Text = TalkText();
            TsTalk.Foreground = _tsTalking ? GreenBrush : Brushes.White;
            TsIcon.Foreground = _tsTalking ? GreenBrush : GrayBrush;

            // микрофон TeamSpeak — всегда виден (зелёный / красный)
            TsMicIcon.Foreground = _ts.MicMuted ? RedBrush : GreenBrush;
            TsMicIcon.Opacity = _ts.MicMuted ? 1 : 0.7;

            // звук TeamSpeak и микрофон Windows — только когда выключены
            TsSpkIcon.Visibility = _ts.SpeakersMuted ? Visibility.Visible : Visibility.Collapsed;
            TsWinMic.Visibility = _micMuted ? Visibility.Visible : Visibility.Collapsed;

            // раскладка остаётся; VPN и эквалайзер в этом острове скрыты
            TsLayout.Text = _layoutCode;

            TsWave.Visibility = _tsTalking ? Visibility.Visible : Visibility.Collapsed;
            AnimateBar(TsS1, 380, _tsTalking);
            AnimateBar(TsS2, 520, _tsTalking);
            AnimateBar(TsS3, 450, _tsTalking);

            RefreshTsMore();
        }

        /// <summary>Строки, которые видны при наведении: дата и время, оба микрофона, VPN, раскладка.</summary>
        private void RefreshTsMore()
        {
            if (!TsActive) return;

            TsDateTime.Text = Cap(DateTime.Now.ToString("dddd, d MMMM  \u00B7  HH:mm:ss", Ru));

            string me = string.IsNullOrEmpty(_ts.Me) ? "" : " \u2014 " + _ts.Me;
            TsLine1.Text = TsLabel() + me + "  \u00B7  " + _ts.Clients + " в сети";
            // вместо слов — значки: зелёный = включено, красный = выключено
            TsHMic.Foreground = _ts.MicMuted ? RedBrush : GreenBrush;
            TsHSpk.Text = _ts.SpeakersMuted ? "\uE74F" : "\uE767";
            TsHSpk.Foreground = _ts.SpeakersMuted ? RedBrush : GreenBrush;
            TsHWinMic.Foreground = _micMuted ? RedBrush : GreenBrush;
            TsHVpn.Foreground = _vpnOn ? GreenBrush : GrayBrush;
            TsHLay.Text = _layoutCode;
        }

        private void UpdateTsTick()
        {
            if (_state == IslandState.TsHover)
            {
                if (!_tsTick.IsEnabled) _tsTick.Start();
                RefreshTsMore();
            }
            else
            {
                _tsTick.Stop();
            }
        }

        /// <summary>Когда кто-то говорит: свечение острова становится зелёным и «дышит».</summary>
        private void UpdateTsEffects()
        {
            bool talking = TsActive && _tsTalking;
            if (talking == _tsFx) return;
            _tsFx = talking;

            if (talking)
            {
                _glow.BeginAnimation(DropShadowEffect.ColorProperty,
                    new ColorAnimation(TsGreen, TimeSpan.FromMilliseconds(150)));

                var pulse = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Timeline.SetDesiredFrameRate(pulse, 20);
                GlowBack.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                GlowBack.BeginAnimation(OpacityProperty, null);
                GlowBack.Opacity = 1;
                _glow.BeginAnimation(DropShadowEffect.ColorProperty,
                    new ColorAnimation(_curAccent, TimeSpan.FromMilliseconds(300)));
            }

            UpdateGlow();
        }

        // ---------- меню ----------

        private void Ts3Key_Click(object sender, RoutedEventArgs e)
        {
            string key = InputDialog.AskPassword(
                "DreamIsland — TeamSpeak 3",
                "API-ключ плагина ClientQuery. В TeamSpeak: Tools → Options → Addons → Plugins → " +
                "ClientQuery → Settings. Плагин должен быть включён.",
                AppSettings.GetString("ts3_key"));

            if (key == null) return;

            AppSettings.SetString("ts3_key", key.Trim());

            // после ввода ключа слежение включается само (раньше нужно было ещё отметить пункт в меню)
            if (!_ts3Enabled)
            {
                _ts3Enabled = true;
                AppSettings.SetBool("ts3", true);
                SetMenu("ts3", true);
            }
            StartTs3();
        }

        private void Ts3Name_Click(object sender, RoutedEventArgs e)
        {
            if (!TsActive)
            {
                string why = !_ts3Enabled
                    ? "Слежение за TeamSpeak 3 выключено. Включи пункт «Следить за TeamSpeak 3» в меню острова."
                    : (AppVolume.IsTeamSpeakRunning() ? "Клиент TeamSpeak 3 запущен." : "Клиент TeamSpeak 3 не запущен.") +
                      "\n\n" + _ts3.Status;
                MessageBox.Show("Название можно задать, когда остров видит сервер.\n\n" + why,
                                "DreamIsland — TeamSpeak 3", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string server = _ts.Server ?? "";
            string name = InputDialog.AskText(
                "DreamIsland — название сервера",
                "Своё короткое название для этого сервера (пусто — вернуть настоящее: «" + server + "»).",
                TsLabel());

            if (name == null) return;

            AppSettings.SetString("ts3name_" + NameKey(server), name.Trim());
            UpdateTsUi();
        }

        // ---------- Alt + колёсико: громкость TeamSpeak ----------

        private static bool IsAltDown()
        {
            return (GetAsyncKeyState(0x12) & 0x8000) != 0; // VK_MENU
        }

        private static string VolIcon(int pct)
        {
            return pct == 0 ? "\uE74F" : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
        }

        private void AdjustTs3(double delta)
        {
            string app;
            int pct;

            if (AppVolume.TryAdjust(AppVolume.IsTeamSpeak, delta, out app, out pct))
                ShowToast(VolIcon(pct), false, TsBlue, "", pct + "%", TsBlue, pct / 100.0, 1400);
            else
                ShowToast("\uE7BA", false, Gray, "TeamSpeak",
                          AppVolume.IsTeamSpeakRunning() ? "нет звука" : "не запущен", Gray, -1, 2000);
        }
    }
}
