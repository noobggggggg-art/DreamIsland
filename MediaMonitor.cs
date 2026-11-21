using System;
using System.IO;
using System.Security.Cryptography;
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
        public bool Video;           // идёт видео (а не музыка)
        public string App = "";      // приложение-источник
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
        private string _artHash = "";   // хэш обложки, которую показываем сейчас
        private int _artGen;            // номер текущего трека — отменяет устаревшие проверки обложки

        private static readonly string[] VideoExt =
            { ".mov", ".mp4", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".mts", ".3gp" };
        private static readonly string[] VideoApps =
            { "vlc", "mpc-hc", "mpc-be", "potplayer", "zunevideo", "wmplayer", "video.ui", "mpv", "kmplayer", "gom",
              "movies", "kodi", "plex", "jellyfin", "smplayer", "haruna", "stremio", "netflix", "primevideo", "disney" };

        // подсказки по названию страницы в браузере (там у плеера нет файла, только заголовок вкладки)
        private static readonly string[] VideoTitleHints =
            { "- youtube", "twitch", "netflix", "rutube", "vk видео", "кинопоиск", "okko", "ivi.ru" };

        // плееры, которые пользователь сам отметил как видео-плееры (меню острова), хранятся в settings.txt
        private static string Norm(string app) { return (app ?? "").Trim().ToLowerInvariant(); }

        public static bool IsUserVideoApp(string app)
        {
            string a = Norm(app);
            if (a.Length == 0) return false;
            foreach (var x in AppSettings.GetString("videoapps", "").Split(';'))
                if (x.Length > 0 && x == a) return true;
            return false;
        }

        public static void SetUserVideoApp(string app, bool on)
        {
            string a = Norm(app);
            if (a.Length == 0) return;

            var list = new System.Collections.Generic.List<string>();
            foreach (var x in AppSettings.GetString("videoapps", "").Split(';'))
                if (x.Length > 0 && x != a) list.Add(x);
            if (on) list.Add(a);

            AppSettings.SetString("videoapps", string.Join(";", list));
        }

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
                _session.MediaPropertiesChanged -= OnPropsEvent;
                _session.PlaybackInfoChanged -= OnPlaybackEvent;
            }

            _session = _mgr.GetCurrentSession();

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnPropsEvent;
                _session.PlaybackInfoChanged += OnPlaybackEvent;
            }

            _ = RefreshAsync(false);
        }

        private void OnPropsEvent(GlobalSystemMediaTransportControlsSession s, object e)
        {
            _ = RefreshAsync(true);   // метаданные изменились — часто это приехала новая обложка
        }

        private void OnPlaybackEvent(GlobalSystemMediaTransportControlsSession s, object e)
        {
            _ = RefreshAsync(false);
        }

        private async Task RefreshAsync(bool propsEvent)
        {
            int pollGen = -1;
            string staleHash = "";

            await _lock.WaitAsync();
            try
            {
                var s = _session;
                if (s == null)
                {
                    _lastKey = "";
                    _lastThumb = null;
                    _artHash = "";
                    _artGen++;
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

                if (!trackChanged && playing == _lastPlaying)
                {
                    // название то же, но у плеера могли обновиться метаданные — проверим, не сменилась ли обложка
                    if (propsEvent)
                    {
                        pollGen = _artGen;
                        staleHash = _artHash;
                    }
                }
                else
                {
                    _lastPlaying = playing;

                    if (trackChanged)
                    {
                        // Новая песня: плеер часто ещё отдаёт обложку ПРОШЛОЙ песни.
                        // Поэтому старую не показываем, а настоящую ждём (см. PollArtAsync).
                        _artGen++;
                        pollGen = _artGen;
                        staleHash = _artHash;
                        _lastKey = key;
                        _lastThumb = null;
                        _artHash = "";
                    }

                    Raise(new MediaInfo
                    {
                        Title = title,
                        Artist = artist,
                        Playing = playing,
                        Thumb = _lastThumb,
                        Video = IsVideo(props, s, title),
                        App = s.SourceAppUserModelId ?? ""
                    }, trackChanged);
                }
            }
            catch
            {
                // плеер может внезапно закрыться — просто игнорируем
            }
            finally
            {
                _lock.Release();
            }

            if (pollGen >= 0) _ = PollArtAsync(pollGen, staleHash);
        }

        /// <summary>
        /// Несколько раз за ~3 секунды перечитывает обложку и принимает её, когда она отличается от обложки
        /// прошлого трека (или когда прошло достаточно времени — например, тот же альбом).
        /// </summary>
        private async Task PollArtAsync(int gen, string staleHash)
        {
            int[] pauses = { 0, 300, 400, 600, 900, 1300 };

            for (int i = 0; i < pauses.Length; i++)
            {
                if (pauses[i] > 0) await Task.Delay(pauses[i]);
                if (gen != _artGen) return;

                var session = _session;
                if (session == null) return;

                byte[] bytes;
                try
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    bytes = await ReadThumbAsync(props);
                }
                catch
                {
                    continue;
                }

                if (gen != _artGen) return;
                if (bytes == null) continue;

                string h = Hash(bytes);
                if (h == _artHash) return;   // эту обложку уже показываем

                bool isNew = h != staleHash;
                bool lateEnough = i >= 3;    // ~1,3 с: обложка могла совпасть с прошлой (тот же альбом)
                if (!isNew && !lateEnough) continue;

                _artHash = h;
                _lastThumb = bytes;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => ArtChanged?.Invoke(bytes)));
                return;
            }
        }

        private static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(data));
        }

        private static bool IsVideo(GlobalSystemMediaTransportControlsSessionMediaProperties props,
                                    GlobalSystemMediaTransportControlsSession s, string title)
        {
            try
            {
                if (props.PlaybackType.HasValue && props.PlaybackType.Value == Windows.Media.MediaPlaybackType.Video)
                    return true;
            }
            catch { }

            try
            {
                var pt = s.GetPlaybackInfo()?.PlaybackType;
                if (pt.HasValue && pt.Value == Windows.Media.MediaPlaybackType.Video)
                    return true;
            }
            catch { }

            string t = (title ?? "").Trim().ToLowerInvariant();
            foreach (var ext in VideoExt)
                if (t.EndsWith(ext)) return true;

            if (!t.Contains("youtube music"))
                foreach (var h in VideoTitleHints)
                    if (t.Contains(h)) return true;

            if (IsUserVideoApp(s.SourceAppUserModelId)) return true;

            string app = (s.SourceAppUserModelId ?? "").ToLowerInvariant();
            foreach (var a in VideoApps)
                if (app.Contains(a)) return true;

            return false;
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
