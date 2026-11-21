using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>
    /// Два тонких индикатора звука рядом с датой и музыкой:
    /// верхняя полоска — громкость Windows, нижняя — громкость браузера. Заметны, но не бросаются в глаза.
    /// </summary>
    public partial class MainWindow
    {
        private bool _lowGpu;
        private int _volSys = -1, _volBr = -1;
        private bool _volSysMuted, _volBrMuted;
        private System.Threading.Timer _volPoll;
        private int _volBusy;
        private const double VolBarWidth = 22;

        private void InitVol()
        {
            _volSys = _audio.Volume;
            _volSysMuted = _audio.Muted;
            UpdateVolMini();

            // громкость браузера отдельного события не даёт — спрашиваем раз в 2,5 секунды в фоне
            _volPoll = new System.Threading.Timer(_ => PollBrowserVolume(), null, 1500, 2500);
        }

        private void DisposeVol()
        {
            try { if (_volPoll != null) _volPoll.Dispose(); } catch { }
            _volPoll = null;
        }

        private void PollBrowserVolume()
        {
            if (System.Threading.Interlocked.Exchange(ref _volBusy, 1) == 1) return;
            try
            {
                int p;
                bool m;
                bool ok = AppVolume.TryReadBrowser(out p, out m);

                var app = Application.Current;
                if (app != null)
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _volBr = ok ? p : -1;
                        _volBrMuted = ok && m;
                        UpdateVolMini();
                    }));
                }
            }
            catch { }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _volBusy, 0);
            }
        }

        private void UpdateVolMini()
        {
            SetVolBar(VolSysFill, _volSys, _volSysMuted);
            SetVolBar(IdleVolSysFill, _volSys, _volSysMuted);
            SetVolBar(VolBrFill, _volBr, _volBrMuted);
            SetVolBar(IdleVolBrFill, _volBr, _volBrMuted);

            // браузера нет — его полоска остаётся бледной «пустой» дорожкой
            double track = _volBr >= 0 ? 1 : 0.35;
            VolBrTrack.Opacity = track;
            IdleVolBrTrack.Opacity = track;
        }

        private static void SetVolBar(Border fill, int percent, bool muted)
        {
            fill.Width = percent < 0 ? 0 : VolBarWidth * Math.Max(0, Math.Min(100, percent)) / 100.0;
            fill.Background = muted ? RedBrush : Brushes.White;
        }
    }
}
