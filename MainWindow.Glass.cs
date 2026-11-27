using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DreamIsland
{
    /// <summary>
    /// Стеклянный остров: под чёрной тонировкой виден размытый кусочек экрана, который лежит прямо за островом.
    /// Окно острова прозрачное (слоистое) и в снимок экрана не попадает, поэтому снимается именно то, что за ним.
    /// Снимок маленький (область острова, уменьшенная в 4 раза) и обновляется дважды в секунду.
    /// </summary>
    public partial class MainWindow
    {
        private bool _glassOn;
        private Grid _glassLayer;
        private Image _glassImg;
        private readonly DispatcherTimer _glassTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        private void InitGlass()
        {
            _glassTick.Tick += (s, e) => GlassTick();
            ApplyGlass(!AppSettings.GetBool("noglass"));
        }

        /// <summary>Переключатель из окна настроек.</summary>
        private void SetIslandGlass(bool on)
        {
            AppSettings.SetBool("noglass", !on);
            ApplyGlass(on);
        }

        private void ApplyGlass(bool on)
        {
            if (AppSettings.GetBool("lowgpu")) on = false;   // экономия видеопамяти
            _glassOn = on;

            if (on)
            {
                if (_glassLayer == null && !BuildGlassLayer()) { _glassOn = false; return; }
                _glassLayer.Visibility = Visibility.Visible;
                _glassTick.Start();
                GlassTick();
            }
            else
            {
                _glassTick.Stop();
                if (_glassLayer != null) _glassLayer.Visibility = Visibility.Collapsed;
            }
        }

        private bool BuildGlassLayer()
        {
            var host = Island.Child as Grid;
            if (host == null) return false;

            _glassImg = new Image
            {
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 8, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }
            };
            RenderOptions.SetBitmapScalingMode(_glassImg, BitmapScalingMode.HighQuality);

            // тонировка: остров остаётся тёмным, но не глухо чёрным
            var tint = new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0xC4, 0x14, 0x0E, 0x26), Color.FromArgb(0xDC, 0x04, 0x04, 0x08), 90)
            };

            // блик по верхней кромке: свет падает сверху
            var sheen = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(14, 0, 14, 0),
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(0x60, 255, 255, 255), 0.5),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                    }, new Point(0, 0), new Point(1, 0))
            };

            _glassLayer = new Grid { IsHitTestVisible = false };
            _glassLayer.Children.Add(_glassImg);
            _glassLayer.Children.Add(tint);
            _glassLayer.Children.Add(sheen);
            Grid.SetRowSpan(_glassLayer, 99);
            Grid.SetColumnSpan(_glassLayer, 99);

            // скругляем по форме острова (скругление меняется вместе с размером)
            _glassLayer.SizeChanged += (s, e) =>
            {
                double r = Island.CornerRadius.TopLeft;
                _glassLayer.Clip = new RectangleGeometry(new Rect(0, 0, _glassLayer.ActualWidth, _glassLayer.ActualHeight), r, r);
            };

            host.Children.Insert(0, _glassLayer);
            return true;
        }

        private void GlassTick()
        {
            if (!_glassOn || _glassImg == null) return;
            try
            {
                if (Island.ActualWidth < 4 || Island.ActualHeight < 4) return;

                // область острова на экране в физических пикселях
                Point tl = Island.PointToScreen(new Point(0, 0));
                Point br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
                int w = (int)(br.X - tl.X), h = (int)(br.Y - tl.Y);
                if (w < 8 || h < 8) return;

                var shot = Backdrop.CaptureRect((int)tl.X, (int)tl.Y, w, h, 4);
                if (shot != null) _glassImg.Source = shot;
            }
            catch { }
        }
    }
}
