using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>
    /// Проценты загрузки из Steam и Epic Games Launcher: остров показывает всплывашку с названием игры,
    /// процентами и скоростью при начале загрузки, на каждые 10 % и по окончании.
    /// </summary>
    public partial class MainWindow
    {
        private readonly DownloadWatcher _dl = new DownloadWatcher();
        private bool _dlEnabled = true;
        private List<DownloadInfo> _dlNow = new List<DownloadInfo>();
        private readonly Dictionary<string, int> _dlStep = new Dictionary<string, int>();
        private readonly Dictionary<string, DownloadInfo> _dlLast = new Dictionary<string, DownloadInfo>();

        private static readonly Color DlSteam = Color.FromRgb(0x66, 0xC0, 0xF4);
        private static readonly Color DlEpic = Color.FromRgb(0xE5, 0xE5, 0xEA);
        private static readonly Color DlDone = Color.FromRgb(0x30, 0xD1, 0x58);

        private void InitDownloads()
        {
            _dlEnabled = !AppSettings.GetBool("nodl");
            SetMenu("dl", _dlEnabled);

            _dl.Updated += list => Dispatcher.BeginInvoke(new Action(() => OnDownloads(list)));
            if (_dlEnabled) _dl.Start();
        }

        private void DisposeDownloads()
        {
            _dl.Dispose();
        }

        private void Downloads_Toggle(bool on)
        {
            _dlEnabled = on;
            AppSettings.SetBool("nodl", !on);

            if (on) _dl.Start();
            else
            {
                _dl.Stop();
                _dlNow = new List<DownloadInfo>();
                _dlStep.Clear();
                _dlLast.Clear();
            }
        }

        private void OnDownloads(List<DownloadInfo> list)
        {
            if (!_dlEnabled) return;
            _dlNow = list;

            var ids = new HashSet<string>(list.Select(d => d.Id));

            // то, что пропало из списка почти докачавшись, — готово; пауза или отмена на полпути молчат
            foreach (var id in _dlLast.Keys.ToList())
            {
                if (ids.Contains(id)) continue;

                var last = _dlLast[id];
                _dlLast.Remove(id);
                _dlStep.Remove(id);
                if (last.Percent >= 90) ShowDownloadDone(last);
            }

            foreach (var d in list)
            {
                _dlLast[d.Id] = d;

                int step = (int)(d.Percent / 10);
                int prev;
                if (!_dlStep.TryGetValue(d.Id, out prev) || step > prev)
                {
                    _dlStep[d.Id] = step;
                    ShowDownloadToast(d);
                }
            }
        }

        private static string FormatSpeed(double bps)
        {
            if (bps <= 0) return "";
            double mb = bps / 1048576.0;
            return mb >= 1 ? mb.ToString("0.0") + " МБ/с" : (bps / 1024.0).ToString("0") + " КБ/с";
        }

        private void ShowDownloadToast(DownloadInfo d)
        {
            string value = Math.Round(d.Percent) + "%";
            string sp = FormatSpeed(d.SpeedBps);
            if (sp.Length > 0) value += " · " + sp;

            ShowToast("\uE896", false, d.Source == "Steam" ? DlSteam : DlEpic,
                      d.Name, value, Color.FromRgb(255, 255, 255), -1, 3500);
        }

        private void ShowDownloadDone(DownloadInfo d)
        {
            ShowToast("\uE73E", false, DlDone, d.Name, "Готово", DlDone, -1, 4500);
        }

        /// <summary>Пункт меню «Загрузки: показать сейчас».</summary>
        private void DownloadNow_Click(object sender, RoutedEventArgs e)
        {
            if (!_dlEnabled)
            {
                ShowToast("\uE896", false, Gray, "Загрузки Steam / Epic", "выключены", Gray, -1, 2500);
                return;
            }

            if (_dlNow.Count == 0)
            {
                ShowToast("\uE896", false, Gray, "Загрузок не вижу", "Steam / Epic", Gray, -1, 2500);
                return;
            }

            // если качается несколько — самая быстрая
            ShowDownloadToast(_dlNow.OrderByDescending(d => d.SpeedBps).First());
        }
    }
}
