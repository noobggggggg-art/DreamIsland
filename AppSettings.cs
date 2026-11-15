using System;
using System.Collections.Generic;
using System.IO;

namespace DreamIsland
{
    /// <summary>Мини-настройки в %AppData%\DreamIsland\settings.txt (строки вида ключ=0/1).</summary>
    internal static class AppSettings
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamIsland");
        private static readonly string FilePath = Path.Combine(Dir, "settings.txt");
        private static readonly string LegacyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynamicIsland", "settings.txt");

        private static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>();
            try
            {
                // если настроек под новым названием ещё нет — читаем старые
                string path = File.Exists(FilePath) ? FilePath : (File.Exists(LegacyFilePath) ? LegacyFilePath : null);
                if (path != null)
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var p = line.Split(new[] { '=' }, 2); // значение может содержать «=»
                        if (p.Length == 2) d[p[0]] = p[1];
                    }
                }
            }
            catch { }
            return d;
        }

        public static string GetString(string key, string def = "")
        {
            var d = Load();
            return d.TryGetValue(key, out var v) ? v : def;
        }

        public static int GetInt(string key, int def)
        {
            int n;
            return int.TryParse(GetString(key, ""), out n) ? n : def;
        }

        public static void SetString(string key, string value)
        {
            try
            {
                var d = Load();
                d[key] = (value ?? "").Replace("\r", "").Replace("\n", "");
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }

        public static bool GetBool(string key)
        {
            var d = Load();
            return d.TryGetValue(key, out var v) && v == "1";
        }

        public static void SetBool(string key, bool value)
        {
            try
            {
                var d = Load();
                d[key] = value ? "1" : "0";
                Directory.CreateDirectory(Dir);
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }
    }
}
