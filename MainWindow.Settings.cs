using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DreamIsland
{
    /// <summary>
    /// Правый клик на остров открывает интерфейс DreamIslands (SettingsWindow) вместо обычного меню.
    /// Само контекстное меню осталось в XAML как «движок»: окно управляет его пунктами.
    /// </summary>
    public partial class MainWindow
    {
        private SettingsWindow _settings;

        private void Island_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;   // стандартное меню не показываем
            OpenSettings();
        }

        private void OpenSettings()
        {
            if (_settings != null)
            {
                try { _settings.Activate(); } catch { }
                return;
            }

            _settings = new SettingsWindow(
                FindMenuDeep,
                mi => mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, mi)),
                "v" + Updater.CurrentText)
            {
                Owner = this,
                TrackedApps = GetTracked,
                SetTracked = SetTracked,
                RemoveTracked = RemoveTracked,
                AddTracked = AddTracked,
                RunningApps = GetRunningApps,
                BreakMinutes = () => _breakMinutes,
                SetBreakMinutes = SetBreakMinutes,
                ExitRequested = () => { var exit = FindMenuDeep("exit"); if (exit != null) exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, exit)); }
            };
            _settings.Closed += (s, e) => _settings = null;
            _settings.Show();
        }
    }
}
