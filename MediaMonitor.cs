using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Media.Control;

namespace DreamIsland
{
    public sealed class MediaInfo
    {
        public string Title = "";
        public string Artist = "";
        public bool Playing;
        public byte[] Thumb;
    }

    /// <summary>
    /// Следит за тем, что играет в Windows (Spotify, Яндекс Музыка, браузер, плееры и т.д.).
    /// Работает по событиям, без опроса, поэтому почти не грузит систему.
    /// </summary>
    public sealed class MediaMonitor
    {
        private GlobalSystemMediaTransportControlsSessionManager _mgr;
        private GlobalSystemMediaTransportControlsSession _session;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private string _lastKey = "";
        private bool _lastPlaying;
        private byte[] _lastThumb;

        /// <summary>(инфо, сменился ли трек)</summary>
        public event Action<MediaInfo, bool> Changed;

        /// <summary>Обложка пришла позже, чем название трека.</summary>
        public event Action<byte[]> ArtChanged;

        public async Task StartAsync()
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (s, e) => Attach();
            Attach();
        }

        public void TogglePlayPause()
        {
            try { _ = _session?.TryTogglePlayPauseAsync(); } catch { }
        }

        public void Next()
        {
            try { _ = _session?.TrySkipNextAsync(); } catch { }
        }

        public void Previous()
        {
            try { _ = _session?.TrySkipPreviousAsync(); } catch { }
        }

        public void SeekTo(TimeSpan position)
        {
            try { _ = _session?.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
        }

        /// <summary>Текущая позиция и длительность трека (позиция досчитывается на лету).</summary>
        public bool TryGetTimeline(out TimeSpan position, out TimeSpan duration)
        {
            position = TimeSpan.Zero;
            duration = TimeSpan.Zero;
            try
            {
                var s = _session;
                if (s == null) return false;

                var tl = s.GetTimelineProperties();
                if (tl == null) return false;

                duration = tl.EndTime - tl.StartTime;
                position = tl.Position - tl.StartTime;

                if (_lastPlaying)
                {
                    double rate = s.GetPlaybackInfo()?.PlaybackRate ?? 1.0;
                    position += TimeSpan.FromSeconds((DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds * rate);
                }

                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (duration > TimeSpan.Zero && position > duration) position = duration;

                return duration > TimeSpan.Zero;
            }
            catch
            {
                return false;
            }
        }

        private void Attach()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnEvent;
                _session.PlaybackInfoChanged -= OnEvent;
            }

            _session = _mgr.GetCurrentSession();

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnEvent;
                _session.PlaybackInfoChanged += OnEvent;
            }

            _ = RefreshAsync();
        }

        private void OnEvent(GlobalSystemMediaTransportControlsSession s, object e)
        {
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            string retryKey = null;

            await _lock.WaitAsync();
            try
            {
                var s = _session;
                if (s == null)
                {
                    _lastKey = "";
                    _lastThumb = null;
                    Raise(new MediaInfo(), false);
                    return;
                }

                var props = await s.TryGetMediaPropertiesAsync();
                var playing = s.GetPlaybackInfo()?.PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var title = props.Title ?? "";
                var artist = props.Artist ?? "";
                var key = title + "|" + artist;
                var trackChanged = key != _lastKey;

                if (!trackChanged && playing == _lastPlaying) return;

                if (trackChanged)
                    _lastThumb = await ReadThumbAsync(props);

                _lastKey = key;
                _lastPlaying = playing;

                // сразу сообщаем интерфейсу (без ожидания обложки)
                Raise(new MediaInfo
                {
                    Title = title,
                    Artist = artist,
                    Playing = playing,
                    Thumb = _lastThumb
                }, trackChanged);

                if (trackChanged && _lastThumb == null) retryKey = key;
            }
            catch
            {
                // плеер может внезапно закрыться — просто игнорируем
            }
            finally
            {
                _lock.Release();
            }

            // у некоторых плееров (Spotify) обложка появляется чуть позже — ждём уже БЕЗ блокировки
            if (retryKey != null) _ = RetryArtAsync(retryKey);
        }

        private async Task RetryArtAsync(string key)
        {
            try
            {
                await Task.Delay(700);

                var s = _session;
                if (s == null || _lastKey != key) return;

                var props = await s.TryGetMediaPropertiesAsync();
                var thumb = await ReadThumbAsync(props);
                if (thumb == null || _lastKey != key) return;

                _lastThumb = thumb;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => ArtChanged?.Invoke(thumb)));
            }
            catch { }
        }

        private static async Task<byte[]> ReadThumbAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                if (props.Thumbnail == null) return null;
                var rs = await props.Thumbnail.OpenReadAsync();
                using (var st = rs.AsStreamForRead())
                using (var ms = new MemoryStream())
                {
                    await st.CopyToAsync(ms);
                    return ms.Length > 0 ? ms.ToArray() : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private void Raise(MediaInfo info, bool trackChanged)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(info, trackChanged)));
        }
    }
}
