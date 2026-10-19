using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ESPHomeFanController.Controls
{
    public partial class ToggleSwitch : UserControl
    {
        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(ToggleSwitch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ToggleSwitch)d).UpdateVisualState(true)));

        public event Action<bool>? CheckedChanged;

        public bool IsChecked
        {
            get => (bool)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

        public ToggleSwitch()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateVisualState(false);
        }

        private void UserControl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                IsChecked = !IsChecked;
                CheckedChanged?.Invoke(IsChecked);
            }
        }

        private void UpdateVisualState(bool animate)
        {
            if (TrackBorder == null || Knob == null) return;

            double targetX = IsChecked ? 26 : 4;
            Color trackColor = IsChecked ? Color.FromRgb(16, 185, 129) : Color.FromRgb(51, 65, 85);
            Color borderColor = IsChecked ? Color.FromRgb(52, 211, 153) : Color.FromRgb(71, 85, 105);

            if (animate)
            {
                var moveAnim = new DoubleAnimation
                {
                    To = targetX,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Knob.BeginAnimation(Canvas.LeftProperty, moveAnim);

                var colorAnim = new ColorAnimation
                {
                    To = trackColor,
                    Duration = TimeSpan.FromMilliseconds(180)
                };
                TrackBorder.Background = new SolidColorBrush(((SolidColorBrush)TrackBorder.Background).Color);
                TrackBorder.Background.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);

                var borderAnim = new ColorAnimation
                {
                    To = borderColor,
                    Duration = TimeSpan.FromMilliseconds(180)
                };
                TrackBorder.BorderBrush = new SolidColorBrush(((SolidColorBrush)TrackBorder.BorderBrush).Color);
                TrackBorder.BorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
            }
            else
            {
                Canvas.SetLeft(Knob, targetX);
                TrackBorder.Background = new SolidColorBrush(trackColor);
                TrackBorder.BorderBrush = new SolidColorBrush(borderColor);
            }
        }
    }
}
