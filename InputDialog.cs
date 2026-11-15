using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DreamIsland
{
    /// <summary>Маленькое окно «введите значение».</summary>
    public static class InputDialog
    {
        /// <returns>введённый пароль или null, если окно закрыли</returns>
        public static string AskPassword(string title, string prompt, string current)
        {
            return Ask(title, prompt, current, true);
        }

        /// <returns>введённый текст или null, если окно закрыли</returns>
        public static string AskText(string title, string prompt, string current)
        {
            return Ask(title, prompt, current, false);
        }

        private static string Ask(string title, string prompt, string current, bool password)
        {
            string result = null;

            var w = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E))
            };

            var text = new TextBlock { Text = prompt, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };

            FrameworkElement input;
            Func<string> read;

            if (password)
            {
                var pb = new PasswordBox
                {
                    Password = current ?? "",
                    Margin = new Thickness(0, 10, 0, 14),
                    Padding = new Thickness(6)
                };
                pb.Loaded += (s, e) => pb.Focus();
                input = pb;
                read = () => pb.Password;
            }
            else
            {
                var tb = new TextBox
                {
                    Text = current ?? "",
                    Margin = new Thickness(0, 10, 0, 14),
                    Padding = new Thickness(6)
                };
                tb.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
                input = tb;
                read = () => tb.Text;
            }

            var ok = new Button
            {
                Content = "Сохранить",
                IsDefault = true,
                Padding = new Thickness(18, 6, 18, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ok.Click += (s, e) => { result = read(); w.DialogResult = true; };

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(text);
            panel.Children.Add(input);
            panel.Children.Add(ok);

            w.Content = panel;
            w.ShowDialog();

            return result;
        }
    }
}
