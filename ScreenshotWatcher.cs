using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DreamIsland
{
    /// <summary>
    /// Замечает новые скриншоты в папке «Изображения\Screenshots»
    /// (туда их сохраняет Win+PrintScreen) и готовит маленькую миниатюру.
    /// </summary>
    public sealed class ScreenshotWatcher : IDisposable
    {
        private FileSystemWatcher _fsw;
        private string _lastPath = "";
        private DateTime _lastAt = DateTime.MinValue;

        /// <summary>(путь к файлу, миниатюра). Вызывается в UI-потоке.</summary>
        public event Action<string, BitmapSource> Captured;

        public void Start()
        {
            Stop();

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                _fsw = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _fsw.Created += OnFile;
                _fsw.Renamed += OnFile;
            }
            catch
            {
                // нет доступа к папке — просто не следим
            }
        }

        public void Stop()
        {
            if (_fsw != null)
            {
                try
                {
                    _fsw.EnableRaisingEvents = false;
                    _fsw.Dispose();
                }
                catch { }
                _fsw = null;
            }
        }

        private void OnFile(object sender, FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp") return;

            // Created и Renamed могут прийти оба — берём одно из них
            if (e.FullPath == _lastPath && (DateTime.UtcNow - _lastAt).TotalSeconds < 3) return;
            _lastPath = e.FullPath;
            _lastAt = DateTime.UtcNow;

            string path = e.FullPath;
            _ = Task.Run(() => LoadAsync(path));
        }

        private async Task LoadAsync(string path)
        {
            // файл ещё дописывается — пробуем несколько раз
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                try
                {
                    BitmapImage bmp;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 64;
                        bmp.StreamSource = fs;
                        bmp.EndInit();
                        bmp.Freeze();
                    }

                    var app = Application.Current;
                    if (app != null)
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var h = Captured;
                            if (h != null) h(path, bmp);
                        }));
                    }
                    return;
                }
                catch
                {
                    // ещё не готов
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
