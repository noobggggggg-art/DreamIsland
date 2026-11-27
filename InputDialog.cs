using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

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

        private static Brush Lin(Color a, Color b)
        {
            var br = new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
            br.Freeze();
            return br;
        }

        private static string Ask(string title, string prompt, string current, bool password)
        {
            string result = null;
            bool fancy = !AppSettings.GetBool("lowgpu");

            var violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
            var edge = Lin(Color.FromArgb(0x42, 255, 255, 255), Color.FromArgb(0x1C, 0xA7, 0x8B, 0xFA));
            var edgeHot = Lin(Color.FromRgb(0xC4, 0xB5, 0xFD), violet);

            // окно прозрачное: форму и свечение рисует содержимое (Luminous UI)
            var w = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI")
            };

            var text = new TextBlock { Text = prompt, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14 };

            // поле ввода на тёмной вставке, фокус подсвечивает кромку
            var fieldBox = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.2),
                BorderBrush = edge,
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 12, 0, 16)
            };

            Func<string> read;
            Control input;
            if (password)
            {
                var pb = new PasswordBox { Password = current ?? "", FontSize = 14, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), CaretBrush = Brushes.White };
                input = pb;
                read = () => pb.Password;
            }
            else
            {
                var tb = new TextBox { Text = current ?? "", FontSize = 14, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), CaretBrush = Brushes.White, SelectionBrush = new SolidColorBrush(violet) };
                input = tb;
                read = () => tb.Text;
                tb.Loaded += (s, e) => tb.SelectAll();
            }
            input.Loaded += (s, e) => input.Focus();
            input.GotKeyboardFocus += (s, e) => fieldBox.BorderBrush = edgeHot;
            input.LostKeyboardFocus += (s, e) => fieldBox.BorderBrush = edge;
            fieldBox.Child = input;

            Func<string, bool, Border> button = (label, primary) =>
            {
                var b = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1.2),
                    Padding = new Thickness(18, 7, 18, 7),
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Focusable = true,
                    BorderBrush = primary ? edgeHot : edge,
                    Background = primary
                        ? Lin(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x5B, 0x21, 0xB6))
                        : new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
                    Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold }
                };
                b.MouseEnter += (s, e) => b.BorderBrush = edgeHot;
                b.MouseLeave += (s, e) => b.BorderBrush = primary ? edgeHot : edge;
                b.MouseLeftButtonDown += (s, e) => e.Handled = true;   // нажатие не уходит выше, к перетаскиванию окна
                return b;
            };

            var ok = button("Сохранить", true);
            var cancel = button("Отмена", false);
            Action save = () => { result = read(); w.DialogResult = true; };
            ok.MouseLeftButtonUp += (s, e) => save();
            cancel.MouseLeftButtonUp += (s, e) => w.DialogResult = false;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            panel.Children.Add(text);
            panel.Children.Add(fieldBox);
            panel.Children.Add(buttons);

            // стеклянная карточка: тёмная основа, свет сверху-слева, свечение вокруг
            var card = new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1.4),
                BorderBrush = Lin(Color.FromArgb(0xB8, 255, 255, 255), Color.FromArgb(0x33, 0x8B, 0x5C, 0xF6)),
                Background = new LinearGradientBrush(Color.FromRgb(0x14, 0x0E, 0x28), Color.FromRgb(0x08, 0x08, 0x0E), 70),
                Margin = new Thickness(24),
                Child = panel
            };
            if (fancy)
                card.Effect = new DropShadowEffect { Color = Color.FromRgb(0x7C, 0x3A, 0xED), BlurRadius = 40, ShadowDepth = 0, Opacity = 0.45 };

            card.MouseLeftButtonDown += (s, e) =>
            {
                // тянем окно только за саму карточку и текст: DragMove на кнопке перехватывает мышь, и клик не срабатывает
                if (ReferenceEquals(e.OriginalSource, card) || ReferenceEquals(e.OriginalSource, text)) { try { w.DragMove(); } catch { } }
            };
            w.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) w.DialogResult = false;
                else if (e.Key == Key.Enter) save();
            };

            w.Content = card;
            w.ShowDialog();
            return result;
        }
    }
}
