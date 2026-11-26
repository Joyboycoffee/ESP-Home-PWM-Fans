using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ESPHomeFanController.Models;

namespace ESPHomeFanController.Controls
{
    public partial class FanCurveGraph : UserControl
    {
        private List<CurvePoint> _thresholds = new();
        private float? _currentTemp;
        private int? _draggedIndex;
        private int? _hoveredIndex;

        public event Action<List<CurvePoint>>? ThresholdsChanged;
        public event Action<List<CurvePoint>>? ThresholdsCommitted;

        private const double MinTemp = 20.0;
        private const double MaxTemp = 90.0;
        private const double MarginLeft = 45.0;
        private const double MarginRight = 25.0;
        private const double MarginTop = 25.0;
        private const double MarginBottom = 30.0;

        private Color _curveColor = Color.FromRgb(184, 155, 184);
        private Color _gridColor = Color.FromArgb(35, 154, 146, 161);
        private Color _axisTextColor = Color.FromRgb(154, 146, 161);
        private Color _tooltipBg = Color.FromArgb(235, 27, 23, 30);
        private Color _tooltipBorder = Color.FromRgb(86, 64, 105);
        private Color _tooltipText = Color.FromRgb(244, 242, 245);

        public void SetTheme(Color curveColor, Color gridColor, Color axisTextColor, Color tooltipBg, Color tooltipBorder, Color tooltipText)
        {
            _curveColor = curveColor;
            _gridColor = gridColor;
            _axisTextColor = axisTextColor;
            _tooltipBg = tooltipBg;
            _tooltipBorder = tooltipBorder;
            _tooltipText = tooltipText;
            Redraw();
        }

        public FanCurveGraph()
        {
            InitializeComponent();
        }

        public void SetData(List<CurvePoint> thresholds, float? currentTemp = null)
        {
            if (!_draggedIndex.HasValue)
            {
                _thresholds = thresholds.OrderBy(p => p.Temperature).ToList();
            }
            _currentTemp = currentTemp;
            Redraw();
        }

        private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Redraw();
        }

        private (double plotLeft, double plotTop, double plotWidth, double plotHeight) GetPlotBounds()
        {
            double w = Math.Max(10, GraphCanvas.ActualWidth - MarginLeft - MarginRight);
            double h = Math.Max(10, GraphCanvas.ActualHeight - MarginTop - MarginBottom);
            return (MarginLeft, MarginTop, w, h);
        }

        private double MapX(double temp, double plotLeft, double plotWidth)
        {
            double ratio = (Math.Clamp(temp, MinTemp, MaxTemp) - MinTemp) / (MaxTemp - MinTemp);
            return plotLeft + ratio * plotWidth;
        }

        private double MapY(double speed, double plotTop, double plotHeight)
        {
            double ratio = Math.Clamp(speed, 0, 100) / 100.0;
            return plotTop + plotHeight - (ratio * plotHeight);
        }

        private int UnmapX(double x, double plotLeft, double plotWidth)
        {
            if (plotWidth <= 0) return (int)MinTemp;
            double ratio = (x - plotLeft) / plotWidth;
            double temp = MinTemp + ratio * (MaxTemp - MinTemp);
            return (int)Math.Round(Math.Clamp(temp, MinTemp, MaxTemp));
        }

        private int UnmapY(double y, double plotTop, double plotHeight)
        {
            if (plotHeight <= 0) return 0;
            double ratio = (plotTop + plotHeight - y) / plotHeight;
            double speed = ratio * 100.0;
            int rounded = (int)Math.Round(Math.Clamp(speed, 0, 100));
            // Snap to 0 and 100 near graph edges to prevent accidental 1% or 99% offsets
            if (rounded <= 2) return 0;
            if (rounded >= 98) return 100;
            return rounded;
        }

        public void Redraw()
        {
            if (GraphCanvas.ActualWidth < 50 || GraphCanvas.ActualHeight < 50) return;

            GraphCanvas.Children.Clear();

            var (plotLeft, plotTop, plotWidth, plotHeight) = GetPlotBounds();

            // 1. Draw Grid Lines & Labels
            DrawGrid(plotLeft, plotTop, plotWidth, plotHeight);

            if (_thresholds == null || _thresholds.Count == 0) return;

            // 2. Build Step-Curve Path Geometry
            var stepGeometry = new PathGeometry();
            var fillGeometry = new PathGeometry();

            var sorted = _thresholds.OrderBy(p => p.Temperature).ToList();

            double zeroY = MapY(0, plotTop, plotHeight);
            Point firstPoint = new Point(plotLeft, zeroY);
            var stepFigure = new PathFigure { StartPoint = firstPoint, IsClosed = false };
            var fillFigure = new PathFigure { StartPoint = new Point(plotLeft, plotTop + plotHeight), IsClosed = true };
            fillFigure.Segments.Add(new LineSegment(firstPoint, true));

            double firstNodeX = MapX(sorted[0].Temperature, plotLeft, plotWidth);
            double firstNodeY = MapY(sorted[0].Speed, plotTop, plotHeight);

            // Horizontal line along 0% from MinTemp until first threshold (e.g. 55°C)
            stepFigure.Segments.Add(new LineSegment(new Point(firstNodeX, zeroY), true));
            fillFigure.Segments.Add(new LineSegment(new Point(firstNodeX, zeroY), true));

            // Vertical jump at first threshold up to its speed (e.g. 50%)
            stepFigure.Segments.Add(new LineSegment(new Point(firstNodeX, firstNodeY), true));
            fillFigure.Segments.Add(new LineSegment(new Point(firstNodeX, firstNodeY), true));

            for (int i = 1; i < sorted.Count; i++)
            {
                double px = MapX(sorted[i].Temperature, plotLeft, plotWidth);
                double py = MapY(sorted[i].Speed, plotTop, plotHeight);

                // Step horizontal line, then vertical jump
                double prevPy = MapY(sorted[i - 1].Speed, plotTop, plotHeight);
                stepFigure.Segments.Add(new LineSegment(new Point(px, prevPy), true));
                fillFigure.Segments.Add(new LineSegment(new Point(px, prevPy), true));

                stepFigure.Segments.Add(new LineSegment(new Point(px, py), true));
                fillFigure.Segments.Add(new LineSegment(new Point(px, py), true));
            }

            // Extend curve to max temperature
            double lastX = MapX(MaxTemp, plotLeft, plotWidth);
            double lastY = MapY(sorted[^1].Speed, plotTop, plotHeight);
            stepFigure.Segments.Add(new LineSegment(new Point(lastX, lastY), true));
            fillFigure.Segments.Add(new LineSegment(new Point(lastX, lastY), true));
            fillFigure.Segments.Add(new LineSegment(new Point(lastX, plotTop + plotHeight), true));

            stepGeometry.Figures.Add(stepFigure);
            fillGeometry.Figures.Add(fillFigure);

            // 3. Render Shaded Area under curve
            var fillBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(60, _curveColor.R, _curveColor.G, _curveColor.B), 0.0));
            fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(5, _curveColor.R, _curveColor.G, _curveColor.B), 1.0));

            var fillPath = new Path
            {
                Data = fillGeometry,
                Fill = fillBrush
            };
            GraphCanvas.Children.Add(fillPath);

            // 4. Render Step-Curve Line
            var curveLine = new Path
            {
                Data = stepGeometry,
                Stroke = new SolidColorBrush(_curveColor),
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            GraphCanvas.Children.Add(curveLine);

            // 5. Draw Current Temperature Indicator (Vertical dashed line and glowing dot)
            if (_currentTemp.HasValue && _currentTemp.Value >= 0.0 && _currentTemp.Value <= 120.0)
            {
                double curX = MapX(_currentTemp.Value, plotLeft, plotWidth);
                int activeSpeed = CalculateStepSpeed(_currentTemp.Value, sorted);
                double curY = MapY(activeSpeed, plotTop, plotHeight);

                // Vertical dashed marker line
                var tempLine = new Line
                {
                    X1 = curX,
                    Y1 = plotTop,
                    X2 = curX,
                    Y2 = plotTop + plotHeight,
                    Stroke = new SolidColorBrush(_curveColor),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                GraphCanvas.Children.Add(tempLine);

                // Operating point glowing dot
                var halo = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromArgb(80, _curveColor.R, _curveColor.G, _curveColor.B))
                };
                Canvas.SetLeft(halo, curX - 8);
                Canvas.SetTop(halo, curY - 8);
                GraphCanvas.Children.Add(halo);

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(Colors.White),
                    Stroke = new SolidColorBrush(_curveColor),
                    StrokeThickness = 2
                };
                Canvas.SetLeft(dot, curX - 4);
                Canvas.SetTop(dot, curY - 4);
                GraphCanvas.Children.Add(dot);

                // Floating Tooltip Badge above Operating Point (Matching Bento Reference Card)
                var badgeBorder = new Border
                {
                    Background = new SolidColorBrush(_tooltipBg),
                    BorderBrush = new SolidColorBrush(_tooltipBorder),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 4, 8, 4)
                };

                var badgeStack = new StackPanel { Orientation = Orientation.Horizontal };
                badgeStack.Children.Add(new TextBlock
                {
                    Text = $"{_currentTemp.Value:F1}°C",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(_tooltipText),
                    Margin = new Thickness(0, 0, 6, 0)
                });
                badgeStack.Children.Add(new TextBlock
                {
                    Text = activeSpeed == 0 ? "Off (0%)" : $"{activeSpeed}% Speed",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(_tooltipText)
                });

                badgeBorder.Child = badgeStack;
                badgeBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double badgeW = badgeBorder.DesiredSize.Width > 0 ? badgeBorder.DesiredSize.Width : 90;
                double badgeH = badgeBorder.DesiredSize.Height > 0 ? badgeBorder.DesiredSize.Height : 24;

                double badgeLeft = Math.Clamp(curX - (badgeW / 2.0), plotLeft, plotLeft + plotWidth - badgeW);
                double badgeTop = Math.Max(0, curY - badgeH - 10);

                Canvas.SetLeft(badgeBorder, badgeLeft);
                Canvas.SetTop(badgeBorder, badgeTop);
                GraphCanvas.Children.Add(badgeBorder);
            }

            // 6. Draw Threshold Control Points
            for (int i = 0; i < sorted.Count; i++)
            {
                double px = MapX(sorted[i].Temperature, plotLeft, plotWidth);
                double py = MapY(sorted[i].Speed, plotTop, plotHeight);

                bool isHovered = _hoveredIndex == i || _draggedIndex == i;

                if (isHovered)
                {
                    // Outer glow ring
                    var glow = new Ellipse
                    {
                        Width = 20,
                        Height = 20,
                        Fill = new SolidColorBrush(Color.FromArgb(80, _curveColor.R, _curveColor.G, _curveColor.B))
                    };
                    Canvas.SetLeft(glow, px - 10);
                    Canvas.SetTop(glow, py - 10);
                    GraphCanvas.Children.Add(glow);

                    // Tooltip text above the point
                    string label = $"{sorted[i].Temperature}°C : {sorted[i].Speed}%";
                    var tb = new TextBlock
                    {
                        Text = label,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(_tooltipText),
                        Background = new SolidColorBrush(_tooltipBg),
                        Padding = new Thickness(6, 3, 6, 3)
                    };
                    Canvas.SetLeft(tb, Math.Clamp(px - 30, plotLeft, plotLeft + plotWidth - 60));
                    Canvas.SetTop(tb, Math.Max(0, py - 26));
                    GraphCanvas.Children.Add(tb);
                }

                // Center Point Handle
                var handle = new Ellipse
                {
                    Width = isHovered ? 12 : 10,
                    Height = isHovered ? 12 : 10,
                    Fill = isHovered ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.White),
                    Stroke = new SolidColorBrush(_curveColor),
                    StrokeThickness = 2.5
                };
                Canvas.SetLeft(handle, px - (isHovered ? 6 : 5));
                Canvas.SetTop(handle, py - (isHovered ? 6 : 5));
                GraphCanvas.Children.Add(handle);
            }
        }

        private static int CalculateStepSpeed(float cpuTemp, List<CurvePoint> sorted)
        {
            if (sorted.Count == 0) return 50;
            // Below first threshold (e.g. < 55°C): 0% speed (Fan OFF)
            if (cpuTemp < sorted[0].Temperature) return 0;
            if (cpuTemp >= sorted[^1].Temperature) return sorted[^1].Speed;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (cpuTemp >= sorted[i].Temperature && cpuTemp < sorted[i + 1].Temperature)
                {
                    return sorted[i].Speed;
                }
            }
            return sorted[^1].Speed;
        }

        private void DrawGrid(double plotLeft, double plotTop, double plotWidth, double plotHeight)
        {
            var gridPen = new SolidColorBrush(_gridColor);
            var textBrush = new SolidColorBrush(_axisTextColor);

            // Speed Horizontal Lines (0%, 25%, 50%, 75%, 100%)
            for (int s = 0; s <= 100; s += 25)
            {
                double y = MapY(s, plotTop, plotHeight);

                var line = new Line
                {
                    X1 = plotLeft,
                    Y1 = y,
                    X2 = plotLeft + plotWidth,
                    Y2 = y,
                    Stroke = gridPen,
                    StrokeThickness = 1
                };
                GraphCanvas.Children.Add(line);

                var label = new TextBlock
                {
                    Text = $"{s}%",
                    FontSize = 10,
                    Foreground = textBrush,
                    TextAlignment = TextAlignment.Right,
                    Width = 35
                };
                Canvas.SetLeft(label, plotLeft - 40);
                Canvas.SetTop(label, y - 7);
                GraphCanvas.Children.Add(label);
            }

            // Temperature Vertical Lines (20°C, 30°C, ..., 90°C)
            for (int t = 20; t <= 90; t += 10)
            {
                double x = MapX(t, plotLeft, plotWidth);

                var line = new Line
                {
                    X1 = x,
                    Y1 = plotTop,
                    X2 = x,
                    Y2 = plotTop + plotHeight,
                    Stroke = gridPen,
                    StrokeThickness = 1
                };
                GraphCanvas.Children.Add(line);

                var label = new TextBlock
                {
                    Text = $"{t}°",
                    FontSize = 10,
                    Foreground = textBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Canvas.SetLeft(label, x - 9);
                Canvas.SetTop(label, plotTop + plotHeight + 6);
                GraphCanvas.Children.Add(label);
            }
        }

        private void GraphCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(GraphCanvas);
            var (plotLeft, plotTop, plotWidth, plotHeight) = GetPlotBounds();

            int? clickedIndex = null;
            double minDistance = 20.0;

            for (int i = 0; i < _thresholds.Count; i++)
            {
                double px = MapX(_thresholds[i].Temperature, plotLeft, plotWidth);
                double py = MapY(_thresholds[i].Speed, plotTop, plotHeight);
                double dist = Math.Sqrt(Math.Pow(pos.X - px, 2) + Math.Pow(pos.Y - py, 2));

                if (dist < minDistance)
                {
                    minDistance = dist;
                    clickedIndex = i;
                }
            }

            if (clickedIndex.HasValue)
            {
                _draggedIndex = clickedIndex;
                GraphCanvas.CaptureMouse();
                Cursor = Cursors.Hand;
                Redraw();
            }
        }

        private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point pos = e.GetPosition(GraphCanvas);
            var (plotLeft, plotTop, plotWidth, plotHeight) = GetPlotBounds();

            if (_draggedIndex.HasValue)
            {
                int idx = _draggedIndex.Value;
                int newTemp = UnmapX(pos.X, plotLeft, plotWidth);
                int newSpeed = UnmapY(pos.Y, plotTop, plotHeight);

                // Prevent points from crossing adjacent points
                int minAllowedTemp = (int)MinTemp;
                int maxAllowedTemp = (int)MaxTemp;

                if (idx > 0)
                {
                    minAllowedTemp = _thresholds[idx - 1].Temperature + 1;
                }
                if (idx < _thresholds.Count - 1)
                {
                    maxAllowedTemp = _thresholds[idx + 1].Temperature - 1;
                }

                newTemp = Math.Clamp(newTemp, minAllowedTemp, maxAllowedTemp);

                _thresholds[idx].Temperature = newTemp;
                _thresholds[idx].Speed = newSpeed;

                ThresholdsChanged?.Invoke(_thresholds);
                Redraw();
            }
            else
            {
                int? hovered = null;
                for (int i = 0; i < _thresholds.Count; i++)
                {
                    double px = MapX(_thresholds[i].Temperature, plotLeft, plotWidth);
                    double py = MapY(_thresholds[i].Speed, plotTop, plotHeight);
                    double dist = Math.Sqrt(Math.Pow(pos.X - px, 2) + Math.Pow(pos.Y - py, 2));

                    if (dist < 14.0)
                    {
                        hovered = i;
                        break;
                    }
                }

                if (_hoveredIndex != hovered)
                {
                    _hoveredIndex = hovered;
                    Cursor = hovered.HasValue ? Cursors.Hand : Cursors.Arrow;
                    Redraw();
                }
            }
        }

        private void GraphCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggedIndex.HasValue)
            {
                _draggedIndex = null;
                GraphCanvas.ReleaseMouseCapture();
                Cursor = Cursors.Arrow;
                ThresholdsCommitted?.Invoke(_thresholds);
                Redraw();
            }
        }

        private void GraphCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_draggedIndex.HasValue && _hoveredIndex.HasValue)
            {
                _hoveredIndex = null;
                Cursor = Cursors.Arrow;
                Redraw();
            }
        }
    }
}
