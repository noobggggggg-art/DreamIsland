using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace DreamIsland
{
    /// <summary>Автозапуск через HKCU\...\Run (права администратора не нужны).</summary>
    internal static class AutoStart
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DreamIsland";
        private const string LegacyName = "DynamicIsland"; // прежнее название

        public static string ExePath()
        {
            string p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) &&
                !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return p;

            // запуск через `dotnet run` — берём exe рядом с dll
            var asm = Assembly.GetEntryAssembly();
            if (asm == null) return null;
            string exe = Path.ChangeExtension(asm.Location, ".exe");
            return File.Exists(exe) ? exe : null;
        }

        /// <summary>Если раньше был включён автозапуск под старым названием — переносим на новое.</summary>
        public static void MigrateLegacy()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null || k.GetValue(LegacyName) == null) return;

                    k.DeleteValue(LegacyName, false);
                    string exe = ExePath();
                    if (exe != null) k.SetValue(ValueName, "\"" + exe + "\"");
                }
            }
            catch { }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    var v = k == null ? null : k.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static bool Set(bool enable)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return false;

                    if (enable)
                    {
                        string exe = ExePath();
                        if (exe == null) return false;
                        k.SetValue(ValueName, "\"" + exe + "\"");
                        k.DeleteValue(LegacyName, false);
                    }
                    else
                    {
                        k.DeleteValue(ValueName, false);
                        k.DeleteValue(LegacyName, false);
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
