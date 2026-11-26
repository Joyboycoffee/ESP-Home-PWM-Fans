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

        private Color _activeTrackColor = Color.FromRgb(184, 155, 184);
        private Color _inactiveTrackColor = Color.FromRgb(27, 23, 30);
        private Color _activeBorderColor = Color.FromRgb(216, 180, 216);
        private Color _inactiveBorderColor = Color.FromRgb(46, 41, 51);

        public void SetTheme(Color activeTrack, Color inactiveTrack, Color activeBorder, Color inactiveBorder, Color knobColor)
        {
            _activeTrackColor = activeTrack;
            _inactiveTrackColor = inactiveTrack;
            _activeBorderColor = activeBorder;
            _inactiveBorderColor = inactiveBorder;
            if (Knob != null) Knob.Fill = new SolidColorBrush(knobColor);
            UpdateVisualState(false);
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
            Color trackColor = IsChecked ? _activeTrackColor : _inactiveTrackColor;
            Color borderColor = IsChecked ? _activeBorderColor : _inactiveBorderColor;

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
