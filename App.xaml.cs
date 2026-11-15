using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace DreamIsland
{
    public partial class App : Application
    {
        private Mutex _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // после обновления новая копия ждёт, пока закроется старая
            for (int i = 0; i + 1 < e.Args.Length; i++)
            {
                int pid;
                if (e.Args[i] == "--after-update" && int.TryParse(e.Args[i + 1], out pid))
                {
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                            p.WaitForExit(15000);
                    }
                    catch { }
                }
            }

            // только одна копия острова (важно для автозапуска)
            bool created;
            _mutex = new Mutex(true, "DreamIsland.SingleInstance", out created);

            if (!created)
            {
                MessageBox.Show(
                    "Остров уже запущен.\n\nЕсли ты обновил файлы, закрой старый остров " +
                    "(правый клик по нему → Выход) и запусти снова.",
                    "DreamIsland", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            Updater.CleanupOld();

            // если обновление уже скачано — ставим его и запускаем новую версию
            if (Updater.TryApplyPendingAtStartup())
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
