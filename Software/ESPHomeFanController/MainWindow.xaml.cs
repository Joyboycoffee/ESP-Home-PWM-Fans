using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ESPHomeFanController.Models;
using ESPHomeFanController.Services;

namespace ESPHomeFanController
{
    public partial class MainWindow : Window
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private readonly AppSettings _settings;
        private readonly EspHomeService _espService;
        private readonly HardwareMonitorService _hwService;
        private readonly FanSpeedController _controller;

        private TrayIcon? _trayIcon;
        private DispatcherTimer? _memTrimTimer;
        private bool _isExplicitExit;
        private float? _lastCpuTemp;
        private int _lastSpeed;

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();

            // Restore Window Geometry
            if (_settings.WindowWidth >= 800) Width = _settings.WindowWidth;
            if (_settings.WindowHeight >= 500) Height = _settings.WindowHeight;
            if (_settings.WindowLeft > 0 && _settings.WindowTop > 0)
            {
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }

            _espService = new EspHomeService();
            _hwService = new HardwareMonitorService();
            _controller = new FanSpeedController(_espService, _hwService, _settings);

            SourceInitialized += (s, e) => ApplyDarkTitleBarTheme();
            Loaded += MainWindow_Loaded;
            BindEvents();
            SetupSystemTray();

            // Populate Initial UI State
            TxtIpAddress.Text = _settings.EspHost;
            AutoSwitch.IsChecked = _settings.AutomationEnabled;
            OverrideSwitch.IsChecked = _settings.FullSpeedOverride;
            SpeedSlider.Value = _settings.ManualSpeed;
            TxtManualPercent.Text = $"{_settings.ManualSpeed}%";
            CurveGraph.SetData(_settings.Thresholds);

            // Start Services with Auth
            _espService.Start(_settings.EspHost, _settings.EspPort, _settings.EspUsername, _settings.EspPassword);
            _controller.Start();

            // Start Working Set Memory Optimizer (Trims unused RAM every 15s)
            _memTrimTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _memTrimTimer.Tick += (s, e) => TrimMemory();
            _memTrimTimer.Start();
        }

        private void ApplyDarkTitleBarTheme()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                // 1. Enable Immersive Dark Mode for Title Bar (Windows 10/11)
                int darkMode = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }

                // 2. Set Caption Background Color to exact dark background #0b0f19 (BGR: 0x00190F0B)
                int captionColor = 0x00190F0B;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                // 3. Set Title Text Color to #f8fafc (BGR: 0x00FCFAF8)
                int textColor = 0x00FCFAF8;
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));

                // 4. Set Window Border Color to #23304a (BGR: 0x004A3023)
                int borderColor = 0x004A3023;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch { }
        }

        private void TrimMemory()
        {
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, false);
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDarkTitleBarTheme();
            SetupSystemTray();
            TrimMemory();
        }

        private void BindEvents()
        {
            // EspHome Service Events
            _espService.ConnectionStatusChanged += status => Dispatcher.Invoke(() => UpdateConnectionStatus(status));
            _espService.FanStateChanged += (isOn, speed) => Dispatcher.Invoke(() => UpdateFanState(isOn, speed));
            _espService.TelemetryUpdated += (uptime, wifi) => Dispatcher.Invoke(() => UpdateTelemetry(uptime, wifi));
            _espService.ConnectionRestored += () => Dispatcher.Invoke(() =>
            {
                OverrideSwitch.IsChecked = _controller.FullSpeedOverride;
                AutoSwitch.IsChecked = _controller.AutomationEnabled;
                SpeedSlider.IsEnabled = !_controller.AutomationEnabled && !_controller.FullSpeedOverride;
            });

            // Hardware Monitor Events
            _hwService.TemperatureUpdated += temp => Dispatcher.Invoke(() => UpdateCpuTemperature(temp));
            _hwService.LhmStatusChanged += active => Dispatcher.Invoke(() => UpdateLhmStatus(active));

            // Controller Events
            _controller.LogicStatusUpdated += logic => Dispatcher.Invoke(() => TxtLogicStatus.Text = logic);
            _controller.TargetSpeedUpdated += speed => Dispatcher.Invoke(() =>
            {
                _lastSpeed = speed;
                FanGauge.Value = speed;
            });

            // Curve Graph Events
            CurveGraph.ThresholdsChanged += points => _controller.UpdateThresholds(points);
            CurveGraph.ThresholdsCommitted += points =>
            {
                _controller.UpdateThresholds(points);
                _settings.Save();
            };

            // Switch Events
            AutoSwitch.CheckedChanged += isAuto =>
            {
                _controller.AutomationEnabled = isAuto;
                SpeedSlider.IsEnabled = !isAuto && !OverrideSwitch.IsChecked;
            };

            OverrideSwitch.CheckedChanged += isOverride =>
            {
                _controller.FullSpeedOverride = isOverride;
                SpeedSlider.IsEnabled = !AutoSwitch.IsChecked && !isOverride;
            };

            SpeedSlider.IsEnabled = !_settings.AutomationEnabled && !_settings.FullSpeedOverride;
        }

        private void SetupSystemTray()
        {
            _trayIcon = new TrayIcon(this);

            var contextMenu = new ContextMenu();

            var showHideItem = new MenuItem { Header = "Show / Hide Controller" };
            showHideItem.Click += (s, e) => ToggleWindowVisibility();

            var autoItem = new MenuItem { Header = "Temperature Automation", IsCheckable = true, IsChecked = AutoSwitch.IsChecked };
            autoItem.Click += (s, e) =>
            {
                bool newState = !AutoSwitch.IsChecked;
                AutoSwitch.IsChecked = newState;
                _controller.AutomationEnabled = newState;
                autoItem.IsChecked = newState;
            };

            var fullSpeedItem = new MenuItem { Header = "100% Full Speed Override", IsCheckable = true, IsChecked = OverrideSwitch.IsChecked };
            fullSpeedItem.Click += (s, e) =>
            {
                bool newState = !OverrideSwitch.IsChecked;
                OverrideSwitch.IsChecked = newState;
                _controller.FullSpeedOverride = newState;
                fullSpeedItem.IsChecked = newState;
            };

            var reconnectItem = new MenuItem { Header = "Force Reconnect" };
            reconnectItem.Click += (s, e) =>
            {
                UpdateConnectionStatus("Connecting");
                _espService.Start(TxtIpAddress.Text.Trim(), _settings.EspPort, _settings.EspUsername, _settings.EspPassword);
            };

            var exitItem = new MenuItem { Header = "Exit Application" };
            exitItem.Click += async (s, e) => await ExitApplicationAsync();

            contextMenu.Items.Add(showHideItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(autoItem);
            contextMenu.Items.Add(fullSpeedItem);
            contextMenu.Items.Add(reconnectItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
            _trayIcon.DoubleClick += ToggleWindowVisibility;
            _trayIcon.Initialize();
        }

        private void ToggleWindowVisibility()
        {
            if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
                TrimMemory();
            }
            else
            {
                ShowInTaskbar = true;
                Visibility = Visibility.Visible;
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        }

        private void UpdateConnectionStatus(string status)
        {
            TxtEspStatus.Text = $"ESP: {status}";

            if (status == "Connected")
            {
                EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                TxtEspStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                EspBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 16, 185, 129));
            }
            else if (status == "Connecting")
            {
                EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TxtEspStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                EspBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 245, 158, 11));
            }
            else
            {
                EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtEspStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                EspBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 239, 68, 68));
            }
        }

        private void UpdateLhmStatus(bool active)
        {
            if (active)
            {
                LhmStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                TxtLhmStatus.Text = "HW Monitor: Active";
                TxtLhmStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                LhmBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 16, 185, 129));
            }
            else
            {
                LhmStatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                TxtLhmStatus.Text = "HW Monitor: Inactive";
                TxtLhmStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                LhmBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            }
        }

        private void UpdateCpuTemperature(float temp)
        {
            _lastCpuTemp = temp;
            CpuGauge.Value = temp;
            CurveGraph.SetData(_settings.Thresholds, temp);

            _trayIcon?.UpdateTooltip($"ESPHome Fan Controller\nCPU: {temp:F1}°C | Fan: {_lastSpeed}%");
        }

        private void UpdateFanState(bool isOn, int speed)
        {
            TxtFanState.Text = isOn ? $"Silent Fan (ON {speed}%)" : "Silent Fan (OFF)";
        }

        private void UpdateTelemetry(string uptime, float? wifiRssi)
        {
            TxtUptime.Text = uptime;
            TxtWifiSignal.Text = wifiRssi.HasValue ? $"{wifiRssi.Value:F0} dBm" : "-- dBm";
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int val = (int)Math.Round(e.NewValue);
            TxtManualPercent.Text = $"{val}%";
        }

        private void SpeedSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            int val = (int)Math.Round(SpeedSlider.Value);
            _controller.ManualSpeed = val;
        }

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtIpAddress.Text.Trim();
            if (!string.IsNullOrEmpty(host))
            {
                _settings.EspHost = host;
                _settings.Save();
                UpdateConnectionStatus("Connecting");
                _espService.Start(host, _settings.EspPort, _settings.EspUsername, _settings.EspPassword);
            }
        }

        private void BtnWebConsole_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtIpAddress.Text.Trim();
            if (!string.IsNullOrEmpty(host))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"http://{host}/",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open web console: {ex.Message}", "Browser Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
                TrimMemory();
            }
        }

        private async Task ExitApplicationAsync()
        {
            _isExplicitExit = true;

            try
            {
                // Dispatches 100% full speed override so fans safely spin at 100% when application is closed
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                await _espService.SetFullSpeedOverrideAsync(true, cts.Token);
                await _espService.SetFanSpeedAsync(100, cts.Token);
            }
            catch { }

            Close();
            Application.Current.Shutdown();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                TrimMemory();
                return;
            }

            // Save Window Geometry & Settings
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.Save();

            _memTrimTimer?.Stop();
            _trayIcon?.Dispose();
            _espService.Dispose();
            _controller.Dispose();
        }
    }
}