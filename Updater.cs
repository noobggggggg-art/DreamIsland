using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace DreamIsland
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string Tag;
        public string PageUrl;
        public string DownloadUrl;
        public long Size;
        public string Sha256;
    }

    /// <summary>
    /// Обновление с GitHub Releases: берёт последнюю версию, скачивает DreamIsland.exe
    /// и подменяет им работающий файл (старый переименовывается в .old и удаляется при следующем запуске).
    /// Автоматическая установка работает только для собранного exe (publish.bat), не для «dotnet run».
    /// </summary>
    public static class Updater
    {
        /// <summary>Репозиторий по умолчанию. Можно поменять здесь или в меню острова («Репозиторий обновлений»).</summary>
        public const string DefaultRepo = "YOUR_GITHUB_NAME/DreamIsland";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DreamIsland", "1.0"));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return c;
        }

        // ---------- версия ----------

        private static Version Norm(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        public static Version Current
        {
            get
            {
                var asm = Assembly.GetEntryAssembly();
                var v = asm != null ? asm.GetName().Version : null;
                return Norm(v ?? new Version(0, 0, 0, 0));
            }
        }

        public static string CurrentText
        {
            get { var v = Current; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        /// <summary>true — программа запущена как один exe-файл (из publish.bat).</summary>
        public static bool IsSingleFile
        {
            get
            {
                var asm = Assembly.GetEntryAssembly();
                return asm != null && string.IsNullOrEmpty(asm.Location);
            }
        }

        // ---------- репозиторий ----------

        public static string NormalizeRepo(string s)
        {
            s = (s ?? "").Trim();

            const string prefix = "https://github.com/";
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(prefix.Length);

            s = s.Trim('/');
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);

            var parts = s.Split('/');
            if (parts.Length >= 2) s = parts[0] + "/" + parts[1];
            return s;
        }

        public static bool IsRepoConfigured(string repo)
        {
            repo = NormalizeRepo(repo);
            return repo.Contains("/") && !repo.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- проверка ----------

        /// <returns>описание новой версии или null, если установлена последняя</returns>
        public static async Task<UpdateInfo> CheckAsync(string repo)
        {
            repo = NormalizeRepo(repo);
            string json = await Http.GetStringAsync("https://api.github.com/repos/" + repo + "/releases/latest");

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                Version parsed;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out parsed)) return null;

                var latest = Norm(parsed);
                if (latest <= Current) return null;

                string url = null, sha = null;
                long size = 0;

                JsonElement assets;
                if (root.TryGetProperty("assets", out assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                        url = asset.GetProperty("browser_download_url").GetString();

                        JsonElement szEl, dgEl;
                        size = asset.TryGetProperty("size", out szEl) && szEl.ValueKind == JsonValueKind.Number
                            ? szEl.GetInt64() : 0;

                        sha = null;
                        if (asset.TryGetProperty("digest", out dgEl) && dgEl.ValueKind == JsonValueKind.String)
                        {
                            string d = dgEl.GetString();
                            if (d != null && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                sha = d.Substring(7);
                        }

                        if (name.Equals("DreamIsland.exe", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }

                if (url == null) return null;

                JsonElement pageEl;
                string page = root.TryGetProperty("html_url", out pageEl) ? pageEl.GetString() : null;

                return new UpdateInfo
                {
                    Version = latest,
                    Tag = tag,
                    PageUrl = page ?? ("https://github.com/" + repo + "/releases/latest"),
                    DownloadUrl = url,
                    Size = size,
                    Sha256 = sha
                };
            }
        }

        // ---------- загрузка ----------

        private static string UpdateDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamIsland", "update");
            }
        }

        public static async Task<string> DownloadAsync(UpdateInfo info)
        {
            var uri = new Uri(info.DownloadUrl);
            bool trusted = uri.Scheme == Uri.UriSchemeHttps &&
                           (uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase));
            if (!trusted) throw new InvalidOperationException("Ненадёжный адрес загрузки");

            Directory.CreateDirectory(UpdateDir);
            string file = Path.Combine(UpdateDir, "DreamIsland-" + info.Tag.TrimStart('v', 'V') + ".exe");
            string part = file + ".part";

            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/octet-stream");

            using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst);
            }

            try
            {
                // проверки целостности
                if (info.Size > 0 && new FileInfo(part).Length != info.Size)
                    throw new InvalidDataException("Размер файла не совпал");

                var head = new byte[2];
                using (var fs = File.OpenRead(part))
                    fs.Read(head, 0, 2);
                if (head[0] != (byte)'M' || head[1] != (byte)'Z')
                    throw new InvalidDataException("Это не exe-файл");

                if (info.Sha256 != null)
                {
                    string actual;
                    using (var fs = File.OpenRead(part))
                    using (var sha = SHA256.Create())
                        actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");

                    if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма не совпала");
                }
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }

            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
            return file;
        }

        // ---------- «ожидает установки» ----------

        public static void SetPending(string file, Version v)
        {
            AppSettings.SetString("update_pending", file);
            AppSettings.SetString("update_pending_ver", v.ToString());
        }

        private static void ClearPending()
        {
            AppSettings.SetString("update_pending", "");
            AppSettings.SetString("update_pending_ver", "");
        }

        public static bool HasPending()
        {
            string file = AppSettings.GetString("update_pending", "");
            Version v;
            return !string.IsNullOrEmpty(file) && File.Exists(file) &&
                   Version.TryParse(AppSettings.GetString("update_pending_ver", ""), out v) &&
                   Norm(v) > Current;
        }

        public static string PendingFile()
        {
            return AppSettings.GetString("update_pending", "");
        }

        // ---------- установка ----------

        /// <summary>
        /// Подменяет работающий exe новым и запускает его. После вызова приложение должно завершиться.
        /// Переименовать работающий exe Windows разрешает (а перезаписать — нет), поэтому
        /// старый файл становится «DreamIsland.exe.old» и удаляется при следующем запуске.
        /// </summary>
        public static void ApplyAndRestart(string newExe)
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(newExe))
                throw new InvalidOperationException("Нечего устанавливать");

            string old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }

            File.Move(exe, old);
            try
            {
                File.Copy(newExe, exe, true);
            }
            catch
            {
                try { File.Move(old, exe); } catch { }   // не вышло — возвращаем как было
                throw;
            }

            ClearPending();
            try { File.Delete(newExe); } catch { }

            Process.Start(new ProcessStartInfo(exe, "--after-update " + Environment.ProcessId)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
        }

        /// <summary>Вызывается при запуске: если обновление уже скачано — ставим его.</summary>
        /// <returns>true — запущена новая версия, эту копию надо закрыть</returns>
        public static bool TryApplyPendingAtStartup()
        {
            try
            {
                if (!IsSingleFile) return false;

                if (!HasPending())
                {
                    string stale = PendingFile();
                    if (!string.IsNullOrEmpty(stale))
                    {
                        ClearPending();
                        try { File.Delete(stale); } catch { }
                    }
                    return false;
                }

                ApplyAndRestart(PendingFile());
                return true;
            }
            catch
            {
                try { ClearPending(); } catch { }
                return false;
            }
        }

        /// <summary>Удаляет старую копию, оставшуюся после обновления.</summary>
        public static void CleanupOld()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                string old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }
    }
}
