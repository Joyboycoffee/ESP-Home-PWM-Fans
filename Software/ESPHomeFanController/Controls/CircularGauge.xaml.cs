using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ESPHomeFanController.Controls
{
    public partial class CircularGauge : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(CircularGauge),
                new PropertyMetadata("GAUGE", (d, e) => ((CircularGauge)d).TxtTitle.Text = (string)e.NewValue));

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(CircularGauge),
                new PropertyMetadata("", (d, e) => ((CircularGauge)d).TxtUnit.Text = (string)e.NewValue));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularGauge),
                new PropertyMetadata(0.0, (d, e) => ((CircularGauge)d).UpdateGauge()));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(CircularGauge),
                new PropertyMetadata(0.0, (d, e) => ((CircularGauge)d).UpdateGauge()));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(CircularGauge),
                new PropertyMetadata(100.0, (d, e) => ((CircularGauge)d).UpdateGauge()));

        public static readonly DependencyProperty ArcColorProperty =
            DependencyProperty.Register(nameof(ArcColor), typeof(Color), typeof(CircularGauge),
                new PropertyMetadata(Color.FromRgb(16, 185, 129), (d, e) => ((CircularGauge)d).UpdateGauge()));

        public static readonly DependencyProperty ArcEndColorProperty =
            DependencyProperty.Register(nameof(ArcEndColor), typeof(Color), typeof(CircularGauge),
                new PropertyMetadata(Color.FromRgb(52, 211, 153), (d, e) => ((CircularGauge)d).UpdateGauge()));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public Color ArcColor
        {
            get => (Color)GetValue(ArcColorProperty);
            set => SetValue(ArcColorProperty, value);
        }

        public Color ArcEndColor
        {
            get => (Color)GetValue(ArcEndColorProperty);
            set => SetValue(ArcEndColorProperty, value);
        }

        public CircularGauge()
        {
            InitializeComponent();
        }

        private void DrawingCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGauge();
        }

        public void SetSubtext(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                TxtSubtext.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtSubtext.Text = text;
                TxtSubtext.Visibility = Visibility.Visible;
            }
        }

        private void UpdateGauge()
        {
            if (DrawingCanvas == null || DrawingCanvas.ActualWidth < 10 || DrawingCanvas.ActualHeight < 10)
                return;

            DrawingCanvas.Children.Clear();

            double width = DrawingCanvas.ActualWidth;
            double height = DrawingCanvas.ActualHeight;
            double size = Math.Min(width, height);
            double strokeWidth = Math.Max(8, size * 0.08);
            double radius = (size - strokeWidth * 2) / 2;
            Point center = new Point(width / 2, height / 2);

            // Angles in degrees: Start at 135 deg (bottom-left) to 405 deg (bottom-right), total sweep 270 deg
            double startAngle = 135.0;
            double totalSweep = 270.0;

            // 1. Background Arc
            Path bgPath = CreateArcPath(center, radius, startAngle, startAngle + totalSweep, strokeWidth, new SolidColorBrush(Color.FromRgb(30, 41, 59)));
            DrawingCanvas.Children.Add(bgPath);

            // 2. Active Value Arc
            double clampedVal = Math.Clamp(Value, Minimum, Maximum);
            double range = Maximum - Minimum;
            double progressRatio = range > 0 ? (clampedVal - Minimum) / range : 0;
            double currentSweep = totalSweep * progressRatio;

            if (currentSweep > 1.0)
            {
                var gradientBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1)
                };
                gradientBrush.GradientStops.Add(new GradientStop(ArcColor, 0.0));
                gradientBrush.GradientStops.Add(new GradientStop(ArcEndColor, 1.0));

                Path activePath = CreateArcPath(center, radius, startAngle, startAngle + currentSweep, strokeWidth, gradientBrush);
                DrawingCanvas.Children.Add(activePath);
            }

            // Update text formatting
            TxtValue.Text = Unit == "°C" ? $"{Value:F1}" : $"{(int)Math.Round(Value)}";
        }

        private static Path CreateArcPath(Point center, double radius, double startAngleDeg, double endAngleDeg, double strokeThickness, Brush strokeBrush)
        {
            double startRad = startAngleDeg * Math.PI / 180.0;
            double endRad = endAngleDeg * Math.PI / 180.0;

            Point startPoint = new Point(
                center.X + radius * Math.Cos(startRad),
                center.Y + radius * Math.Sin(startRad)
            );

            Point endPoint = new Point(
                center.X + radius * Math.Cos(endRad),
                center.Y + radius * Math.Sin(endRad)
            );

            bool isLargeArc = Math.Abs(endAngleDeg - startAngleDeg) > 180.0;

            PathGeometry geom = new PathGeometry();
            PathFigure figure = new PathFigure
            {
                StartPoint = startPoint,
                IsClosed = false
            };

            ArcSegment arc = new ArcSegment
            {
                Point = endPoint,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = isLargeArc
            };

            figure.Segments.Add(arc);
            geom.Figures.Add(figure);

            return new Path
            {
                Data = geom,
                Stroke = strokeBrush,
                StrokeThickness = strokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }
    }
}
