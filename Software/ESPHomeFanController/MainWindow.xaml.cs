using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ESPHomeFanController.Controls;
using ESPHomeFanController.Models;
using ESPHomeFanController.Services;

namespace ESPHomeFanController
{
    public partial class MainWindow : Window
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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
        private float? _lastGpuTemp;
        private int _lastSpeed;
        private string _activeView = "Dashboard";

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

            SourceInitialized += (s, e) => ApplyTheme(_settings.Theme);
            Loaded += MainWindow_Loaded;
            BindEvents();
            SetupSystemTray();

            // Populate Initial UI State
            TxtIpAddress.Text = _settings.EspHost;
            TxtDeviceHost.Text = _settings.EspHost;
            TxtDevicePort.Text = _settings.EspPort.ToString();
            TxtDeviceUser.Text = _settings.EspUsername;
            TxtDevicePass.Text = _settings.EspPassword;

            AutoSwitch.IsChecked = _settings.AutomationEnabled;
            OverrideSwitch.IsChecked = _settings.FullSpeedOverride;
            SpeedSlider.Value = _settings.ManualSpeed;
            TxtManualPercent.Text = $"{_settings.ManualSpeed}%";

            CurveGraph.SetData(_settings.Thresholds);
            CurveGraphLarge.SetData(_settings.Thresholds);
            PopulateThresholdsEditor();

            // Start Services
            _espService.Start(_settings.EspHost, _settings.EspPort, _settings.EspUsername, _settings.EspPassword);
            _controller.Start();

            // Start Working Set Memory Optimizer (Trims unused RAM every 15s)
            _memTrimTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _memTrimTimer.Tick += (s, e) => TrimMemory();
            _memTrimTimer.Start();

            // Initialize active nav & theme
            SwitchNav("Dashboard");
            ApplyTheme(_settings.Theme);
        }

        #region Theme System (Warm Beige, Plum Obsidian, AMOLED Pitch)
        public void ApplyTheme(string themeName)
        {
            if (string.IsNullOrWhiteSpace(themeName)) themeName = "Beige";
            _settings.Theme = themeName;

            Color winBg, sideBg, sideBorder, contBg, contBorder, cardBg, cardBorder, pillBg, pillBorder;
            Color textPrimary, textMuted, textAccent, secBtnBg, secBtnBorder, secBtnText;
            Color primBtnBg, primBtnText, circBtnBg, circBtnBorder, circBtnIcon, navInactive;
            Color subBadgeBg, itemBg;
            Color cpuTrack, cpuArcStart, cpuArcEnd;
            Color gpuTrack, gpuArcStart, gpuArcEnd;
            Color toggleActive, toggleInactive, toggleActiveBorder, toggleInactiveBorder, toggleKnob;
            Color curveLine, curveGrid, curveAxis, curveTooltipBg, curveTooltipBorder, curveTooltipText;
            int dwmCaption, dwmBorder, dwmText, dwmDark;

            switch (themeName.ToLowerInvariant())
            {
                case "beige":
                    _settings.Theme = "Beige";
                    winBg = (Color)ColorConverter.ConvertFromString("#EFE8DC");
                    sideBg = (Color)ColorConverter.ConvertFromString("#D5C7B3");
                    sideBorder = (Color)ColorConverter.ConvertFromString("#C4B5A0");
                    contBg = (Color)ColorConverter.ConvertFromString("#E7DED0");
                    contBorder = (Color)ColorConverter.ConvertFromString("#D2C4B1");
                    cardBg = (Color)ColorConverter.ConvertFromString("#FAF7F2");
                    cardBorder = (Color)ColorConverter.ConvertFromString("#E3D8C8");
                    pillBg = (Color)ColorConverter.ConvertFromString("#EFE8DC");
                    pillBorder = (Color)ColorConverter.ConvertFromString("#D8CDBC");
                    textPrimary = (Color)ColorConverter.ConvertFromString("#2D2319");
                    textMuted = (Color)ColorConverter.ConvertFromString("#7D6F61");
                    textAccent = (Color)ColorConverter.ConvertFromString("#B68255");
                    secBtnBg = (Color)ColorConverter.ConvertFromString("#E2D6C5");
                    secBtnBorder = (Color)ColorConverter.ConvertFromString("#C6B6A0");
                    secBtnText = (Color)ColorConverter.ConvertFromString("#35281E");
                    primBtnBg = (Color)ColorConverter.ConvertFromString("#B68255");
                    primBtnText = Colors.White;
                    circBtnBg = (Color)ColorConverter.ConvertFromString("#D5C7B3");
                    circBtnBorder = (Color)ColorConverter.ConvertFromString("#C4B5A0");
                    circBtnIcon = (Color)ColorConverter.ConvertFromString("#35281E");
                    navInactive = (Color)ColorConverter.ConvertFromString("#6F6052");
                    subBadgeBg = (Color)ColorConverter.ConvertFromString("#EFE8DC");
                    itemBg = (Color)ColorConverter.ConvertFromString("#EFE8DC");

                    cpuTrack = (Color)ColorConverter.ConvertFromString("#EFE8DC");
                    cpuArcStart = (Color)ColorConverter.ConvertFromString("#43362A");
                    cpuArcEnd = (Color)ColorConverter.ConvertFromString("#2D2319");

                    gpuTrack = (Color)ColorConverter.ConvertFromString("#EFE8DC");
                    gpuArcStart = (Color)ColorConverter.ConvertFromString("#5E8E6F");
                    gpuArcEnd = (Color)ColorConverter.ConvertFromString("#456F53");

                    toggleActive = (Color)ColorConverter.ConvertFromString("#B68255");
                    toggleInactive = (Color)ColorConverter.ConvertFromString("#D5C7B3");
                    toggleActiveBorder = (Color)ColorConverter.ConvertFromString("#9E6F45");
                    toggleInactiveBorder = (Color)ColorConverter.ConvertFromString("#C4B5A0");
                    toggleKnob = Colors.White;

                    curveLine = (Color)ColorConverter.ConvertFromString("#A57B52");
                    curveGrid = (Color)ColorConverter.ConvertFromString("#DFD5C4");
                    curveAxis = (Color)ColorConverter.ConvertFromString("#7D6F61");
                    curveTooltipBg = Color.FromArgb(245, 74, 53, 37);
                    curveTooltipBorder = (Color)ColorConverter.ConvertFromString("#7D6F61");
                    curveTooltipText = Colors.White;

                    dwmDark = 0;             // Light title bar mode for Windows 10/11
                    dwmCaption = 0x00DCE8EF; // BGR of #EFE8DC
                    dwmBorder = 0x00A0B5C4;  // BGR of #C4B5A0
                    dwmText = 0x0019232D;    // BGR of #2D2319
                    BtnThemeToggle.ToolTip = "Theme: Warm Beige (Click to switch to Plum Obsidian)";
                    break;

                case "amoled":
                    _settings.Theme = "Amoled";
                    winBg = Colors.Black;
                    sideBg = (Color)ColorConverter.ConvertFromString("#0A0A0E");
                    sideBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    contBg = (Color)ColorConverter.ConvertFromString("#050507");
                    contBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    cardBg = (Color)ColorConverter.ConvertFromString("#0A0A0E");
                    cardBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    pillBg = (Color)ColorConverter.ConvertFromString("#0F0F14");
                    pillBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    textPrimary = (Color)ColorConverter.ConvertFromString("#EDEDED");
                    textMuted = (Color)ColorConverter.ConvertFromString("#6E6E7A");
                    textAccent = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    secBtnBg = (Color)ColorConverter.ConvertFromString("#0F0F14");
                    secBtnBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    secBtnText = (Color)ColorConverter.ConvertFromString("#EDEDED");
                    primBtnBg = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    primBtnText = (Color)ColorConverter.ConvertFromString("#0A0A0E");
                    circBtnBg = (Color)ColorConverter.ConvertFromString("#0F0F14");
                    circBtnBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    circBtnIcon = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    navInactive = (Color)ColorConverter.ConvertFromString("#6E6E7A");
                    subBadgeBg = (Color)ColorConverter.ConvertFromString("#0F0F14");
                    itemBg = (Color)ColorConverter.ConvertFromString("#0F0F14");

                    cpuTrack = (Color)ColorConverter.ConvertFromString("#181820");
                    cpuArcStart = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    cpuArcEnd = (Color)ColorConverter.ConvertFromString("#EDEDED");

                    gpuTrack = (Color)ColorConverter.ConvertFromString("#181820");
                    gpuArcStart = (Color)ColorConverter.ConvertFromString("#22C55E");
                    gpuArcEnd = (Color)ColorConverter.ConvertFromString("#4ADE80");

                    toggleActive = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    toggleInactive = (Color)ColorConverter.ConvertFromString("#0F0F14");
                    toggleActiveBorder = (Color)ColorConverter.ConvertFromString("#7A5EB8");
                    toggleInactiveBorder = (Color)ColorConverter.ConvertFromString("#181820");
                    toggleKnob = (Color)ColorConverter.ConvertFromString("#EDEDED");

                    curveLine = (Color)ColorConverter.ConvertFromString("#9D7FEA");
                    curveGrid = Color.FromArgb(30, 110, 110, 122);
                    curveAxis = (Color)ColorConverter.ConvertFromString("#6E6E7A");
                    curveTooltipBg = Color.FromArgb(240, 10, 10, 14);
                    curveTooltipBorder = (Color)ColorConverter.ConvertFromString("#7A5EB8");
                    curveTooltipText = Colors.White;

                    dwmDark = 1;
                    dwmCaption = 0x00000000;
                    dwmBorder = 0x00201818;
                    dwmText = 0x00EDEDED;
                    BtnThemeToggle.ToolTip = "Theme: AMOLED Pitch (Click to switch to Warm Beige)";
                    break;

                default: // "plum"
                    _settings.Theme = "Plum";
                    winBg = (Color)ColorConverter.ConvertFromString("#0F0D11");
                    sideBg = (Color)ColorConverter.ConvertFromString("#0F0D11");
                    sideBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    contBg = (Color)ColorConverter.ConvertFromString("#141018");
                    contBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    cardBg = (Color)ColorConverter.ConvertFromString("#0F0D11");
                    cardBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    pillBg = (Color)ColorConverter.ConvertFromString("#1B171E");
                    pillBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    textPrimary = (Color)ColorConverter.ConvertFromString("#F4F2F5");
                    textMuted = (Color)ColorConverter.ConvertFromString("#9A92A1");
                    textAccent = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    secBtnBg = (Color)ColorConverter.ConvertFromString("#1B171E");
                    secBtnBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    secBtnText = (Color)ColorConverter.ConvertFromString("#F4F2F5");
                    primBtnBg = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    primBtnText = (Color)ColorConverter.ConvertFromString("#0F0D11");
                    circBtnBg = (Color)ColorConverter.ConvertFromString("#1B171E");
                    circBtnBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    circBtnIcon = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    navInactive = (Color)ColorConverter.ConvertFromString("#9A92A1");
                    subBadgeBg = (Color)ColorConverter.ConvertFromString("#1B171E");
                    itemBg = (Color)ColorConverter.ConvertFromString("#1B171E");

                    cpuTrack = (Color)ColorConverter.ConvertFromString("#2E2933");
                    cpuArcStart = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    cpuArcEnd = (Color)ColorConverter.ConvertFromString("#F4F2F5");

                    gpuTrack = (Color)ColorConverter.ConvertFromString("#2E2933");
                    gpuArcStart = (Color)ColorConverter.ConvertFromString("#22C55E");
                    gpuArcEnd = (Color)ColorConverter.ConvertFromString("#4ADE80");

                    toggleActive = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    toggleInactive = (Color)ColorConverter.ConvertFromString("#1B171E");
                    toggleActiveBorder = (Color)ColorConverter.ConvertFromString("#D8B4D8");
                    toggleInactiveBorder = (Color)ColorConverter.ConvertFromString("#2E2933");
                    toggleKnob = (Color)ColorConverter.ConvertFromString("#F4F2F5");

                    curveLine = (Color)ColorConverter.ConvertFromString("#B89BB8");
                    curveGrid = Color.FromArgb(35, 154, 146, 161);
                    curveAxis = (Color)ColorConverter.ConvertFromString("#9A92A1");
                    curveTooltipBg = Color.FromArgb(235, 27, 23, 30);
                    curveTooltipBorder = (Color)ColorConverter.ConvertFromString("#866469");
                    curveTooltipText = Colors.White;

                    dwmDark = 1;
                    dwmCaption = 0x00110D0F;
                    dwmBorder = 0x0033292E;
                    dwmText = 0x00F4F2F5;
                    BtnThemeToggle.ToolTip = "Theme: Plum Obsidian (Click to switch to AMOLED Pitch)";
                    break;
            }

            _settings.Save();

            // 1. Update Window Dynamic Resources
            Resources["BrushWindowBg"] = new SolidColorBrush(winBg);
            Resources["BrushSidebarBg"] = new SolidColorBrush(sideBg);
            Resources["BrushSidebarBorder"] = new SolidColorBrush(sideBorder);
            Resources["BrushContainerBg"] = new SolidColorBrush(contBg);
            Resources["BrushContainerBorder"] = new SolidColorBrush(contBorder);
            Resources["BrushCardBg"] = new SolidColorBrush(cardBg);
            Resources["BrushCardBorder"] = new SolidColorBrush(cardBorder);
            Resources["BrushPillBg"] = new SolidColorBrush(pillBg);
            Resources["BrushPillBorder"] = new SolidColorBrush(pillBorder);
            Resources["BrushTextPrimary"] = new SolidColorBrush(textPrimary);
            Resources["BrushTextMuted"] = new SolidColorBrush(textMuted);
            Resources["BrushAccent"] = new SolidColorBrush(textAccent);
            Resources["BrushSecondaryBtnBg"] = new SolidColorBrush(secBtnBg);
            Resources["BrushSecondaryBtnBorder"] = new SolidColorBrush(secBtnBorder);
            Resources["BrushSecondaryBtnText"] = new SolidColorBrush(secBtnText);
            Resources["BrushPrimaryBtnBg"] = new SolidColorBrush(primBtnBg);
            Resources["BrushPrimaryBtnText"] = new SolidColorBrush(primBtnText);
            Resources["BrushCircularBtnBg"] = new SolidColorBrush(circBtnBg);
            Resources["BrushCircularBtnBorder"] = new SolidColorBrush(circBtnBorder);
            Resources["BrushCircularBtnIcon"] = new SolidColorBrush(circBtnIcon);
            Resources["BrushNavInactiveText"] = new SolidColorBrush(navInactive);
            Resources["BrushSubBadgeBg"] = new SolidColorBrush(subBadgeBg);
            Resources["BrushItemBg"] = new SolidColorBrush(itemBg);

            // 2. Direct Window elements
            Background = (Brush)Resources["BrushWindowBg"];
            PathReconnect.Fill = (Brush)Resources["BrushCircularBtnIcon"];
            IconTheme.Fill = (Brush)Resources["BrushCircularBtnIcon"];
            SpeedSlider.Foreground = (Brush)Resources["BrushAccent"];

            // 3. Child Controls Theme Propagation
            var primBrush = (Brush)Resources["BrushTextPrimary"];
            var mutedBrush = (Brush)Resources["BrushTextMuted"];

            CpuGauge.SetTheme(cpuTrack, cpuArcStart, cpuArcEnd, primBrush, mutedBrush);
            GpuGauge.SetTheme(gpuTrack, gpuArcStart, gpuArcEnd, primBrush, mutedBrush);

            AutoSwitch.SetTheme(toggleActive, toggleInactive, toggleActiveBorder, toggleInactiveBorder, toggleKnob);
            OverrideSwitch.SetTheme(toggleActive, toggleInactive, toggleActiveBorder, toggleInactiveBorder, toggleKnob);

            CurveGraph.SetTheme(curveLine, curveGrid, curveAxis, curveTooltipBg, curveTooltipBorder, curveTooltipText);
            CurveGraphLarge.SetTheme(curveLine, curveGrid, curveAxis, curveTooltipBg, curveTooltipBorder, curveTooltipText);

            // 4. Update Navigation Pill & Preset Buttons
            SwitchNav(_activeView);
            UpdatePresetButtonsState(_lastActivePreset);
            UpdateConnectionStatus(_espService.IsConnected ? "Connected" : "Disconnected");
            UpdateLhmStatus(_hwService.IsLhmRunning);
            PopulateThresholdsEditor();

            // 5. Apply DWM Title Bar Theme
            ApplyTitleBarDwm(dwmDark, dwmCaption, dwmBorder, dwmText);
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            string nextTheme = _settings.Theme switch
            {
                "Beige" => "Plum",
                "Plum" => "Amoled",
                _ => "Beige"
            };
            ApplyTheme(nextTheme);
        }

        private void ApplyTitleBarDwm(int darkMode, int captionColor, int borderColor, int textColor)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }

                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch { }
        }
        #endregion

        private void TrimMemory()
        {
            try
            {
                GC.Collect(1, GCCollectionMode.Optimized, false);
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);

                double ramMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
                Dispatcher.Invoke(() =>
                {
                    TxtTelemRam.Text = $"{ramMb:F1} MB";
                });
            }
            catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme(_settings.Theme);
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
            _hwService.CpuTemperatureUpdated += temp => Dispatcher.Invoke(() => UpdateCpuTemperature(temp));
            _hwService.GpuTemperatureUpdated += temp => Dispatcher.Invoke(() => UpdateGpuTemperature(temp));
            _hwService.LhmStatusChanged += active => Dispatcher.Invoke(() => UpdateLhmStatus(active));

            // Controller Events
            _controller.LogicStatusUpdated += logic => Dispatcher.Invoke(() =>
            {
                TxtLogicStatus.Text = logic;
                TxtTelemDecision.Text = $"Decision: {logic}";
            });
            _controller.TargetSpeedUpdated += speed => Dispatcher.Invoke(() =>
            {
                _lastSpeed = speed;
                TxtTelemFan.Text = speed == 0 ? "Commanded Speed: Off (0%)" : $"Commanded Speed: {speed}%";
                if (_lastCpuTemp.HasValue)
                {
                    _trayIcon?.UpdateCpu(_lastCpuTemp.Value, speed);
                }
            });

            // Curve Graph Events (Sync between Dashboard and Dedicated Studio)
            CurveGraph.ThresholdsChanged += points => OnThresholdsUpdated(points);
            CurveGraph.ThresholdsCommitted += points => OnThresholdsCommitted(points);

            CurveGraphLarge.ThresholdsChanged += points => OnThresholdsUpdated(points);
            CurveGraphLarge.ThresholdsCommitted += points => OnThresholdsCommitted(points);

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

        private void OnThresholdsUpdated(List<CurvePoint> points)
        {
            _controller.UpdateThresholds(points);
            CurveGraph.SetData(points, _lastCpuTemp);
            CurveGraphLarge.SetData(points, _lastCpuTemp);
            PopulateThresholdsEditor();
        }

        private void OnThresholdsCommitted(List<CurvePoint> points)
        {
            _controller.UpdateThresholds(points);
            _settings.Save();
            CurveGraph.SetData(points, _lastCpuTemp);
            CurveGraphLarge.SetData(points, _lastCpuTemp);
            PopulateThresholdsEditor();
        }

        #region Navigation & Sidebar
        private void BtnNavDashboard_Click(object sender, RoutedEventArgs e) => SwitchNav("Dashboard");
        private void BtnNavCurves_Click(object sender, RoutedEventArgs e) => SwitchNav("Curves");
        private void BtnNavDevice_Click(object sender, RoutedEventArgs e) => SwitchNav("Device");
        private void BtnNavTelemetry_Click(object sender, RoutedEventArgs e) => SwitchNav("Telemetry");

        private void SwitchNav(string viewName)
        {
            _activeView = viewName;

            ViewDashboard.Visibility = viewName == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            ViewFanCurves.Visibility = viewName == "Curves" ? Visibility.Visible : Visibility.Collapsed;
            ViewDevice.Visibility = viewName == "Device" ? Visibility.Visible : Visibility.Collapsed;
            ViewTelemetry.Visibility = viewName == "Telemetry" ? Visibility.Visible : Visibility.Collapsed;

            ResetNavButton(BtnNavDashboard, IconNavDashboard, TxtNavDashboard);
            ResetNavButton(BtnNavCurves, IconNavCurves, TxtNavCurves);
            ResetNavButton(BtnNavDevice, IconNavDevice, TxtNavDevice);
            ResetNavButton(BtnNavTelemetry, IconNavTelemetry, TxtNavTelemetry);

            switch (viewName)
            {
                case "Dashboard":
                    SetActiveNavButton(BtnNavDashboard, IconNavDashboard, TxtNavDashboard);
                    TxtHeaderTitle.Text = "Thermal Management";
                    TxtHeaderSubtitle.Text = "Ryzen 5 7600X • RTX 3070";
                    break;
                case "Curves":
                    SetActiveNavButton(BtnNavCurves, IconNavCurves, TxtNavCurves);
                    TxtHeaderTitle.Text = "Fan Curves";
                    TxtHeaderSubtitle.Text = "Step curve response thresholds";
                    CurveGraphLarge.SetData(_settings.Thresholds, _lastCpuTemp);
                    PopulateThresholdsEditor();
                    break;
                case "Device":
                    SetActiveNavButton(BtnNavDevice, IconNavDevice, TxtNavDevice);
                    TxtHeaderTitle.Text = "ESP Device";
                    TxtHeaderSubtitle.Text = "Connection & hardware testing";
                    break;
                case "Telemetry":
                    SetActiveNavButton(BtnNavTelemetry, IconNavTelemetry, TxtNavTelemetry);
                    TxtHeaderTitle.Text = "System Telemetry";
                    TxtHeaderSubtitle.Text = "Sensors & memory diagnostics";
                    TrimMemory();
                    break;
            }
        }

        private void SetActiveNavButton(Button btn, System.Windows.Shapes.Path icon, TextBlock txt)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            Color textCol;
            Color borderCol;

            if (_settings.Theme.Equals("Beige", StringComparison.OrdinalIgnoreCase))
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 229, 208), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(229, 208, 181), 1.0));
                borderCol = Color.FromRgb(212, 191, 164);
                textCol = Color.FromRgb(53, 40, 30);
            }
            else if (_settings.Theme.Equals("Amoled", StringComparison.OrdinalIgnoreCase))
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(160, 68, 48, 117), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(15, 15, 20), 1.0));
                borderCol = Color.FromRgb(122, 94, 184);
                textCol = Color.FromRgb(237, 237, 237);
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(58, 43, 71), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(30, 23, 36), 1.0));
                borderCol = Color.FromRgb(86, 64, 105);
                textCol = Color.FromRgb(244, 242, 245);
            }

            btn.Background = brush;
            btn.BorderBrush = new SolidColorBrush(borderCol);
            btn.BorderThickness = new Thickness(1);
            txt.Foreground = new SolidColorBrush(textCol);
            if (icon.Fill != null) icon.Fill = new SolidColorBrush(textCol);
            if (icon.Stroke != null) icon.Stroke = new SolidColorBrush(textCol);
        }

        private void ResetNavButton(Button btn, System.Windows.Shapes.Path icon, TextBlock txt)
        {
            btn.Background = Brushes.Transparent;
            btn.BorderBrush = Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);

            var inactiveBrush = (Brush)Resources["BrushNavInactiveText"];
            txt.Foreground = inactiveBrush;
            if (icon.Fill != null) icon.Fill = inactiveBrush;
            if (icon.Stroke != null) icon.Stroke = inactiveBrush;
        }
        #endregion

        #region Thresholds Node Editor (Dedicated View)
        private void PopulateThresholdsEditor()
        {
            if (ThresholdsEditorGrid == null) return;
            ThresholdsEditorGrid.Children.Clear();

            var itemBg = (Brush)Resources["BrushItemBg"];
            var borderBrush = (Brush)Resources["BrushPillBorder"];
            var accentBrush = (Brush)Resources["BrushAccent"];
            var mutedBrush = (Brush)Resources["BrushTextMuted"];
            var primBrush = (Brush)Resources["BrushTextPrimary"];

            var sorted = _settings.Thresholds.OrderBy(p => p.Temperature).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int idx = i;
                var pt = sorted[i];

                var card = new Border
                {
                    Background = itemBg,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(3)
                };

                var sp = new StackPanel();

                // Header
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                headerGrid.Children.Add(new TextBlock
                {
                    Text = idx == 0 ? "NODE 1 (START)" : $"NODE {idx + 1}",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = accentBrush
                });
                sp.Children.Add(headerGrid);

                // Temp row
                var tempGrid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                tempGrid.Children.Add(new TextBlock { Text = "Temp:", FontSize = 11, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center });
                var tempBox = new TextBlock
                {
                    Text = $"{pt.Temperature}°C",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = primBrush,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                tempGrid.Children.Add(tempBox);
                sp.Children.Add(tempGrid);

                // Speed row
                var speedGrid = new Grid();
                speedGrid.Children.Add(new TextBlock { Text = "Speed:", FontSize = 11, Foreground = mutedBrush, VerticalAlignment = VerticalAlignment.Center });
                var speedBox = new TextBlock
                {
                    Text = $"{pt.Speed}%",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = pt.Speed > 0 ? (Brush)new SolidColorBrush(Color.FromRgb(22, 163, 74)) : mutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                speedGrid.Children.Add(speedBox);
                sp.Children.Add(speedGrid);

                card.Child = sp;
                ThresholdsEditorGrid.Children.Add(card);
            }
        }
        #endregion

        #region Reconnection & Status
        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BtnReconnect.IsEnabled = false;

                // Animate rotating icon for immediate tactile feedback
                var rotateAnim = new DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = TimeSpan.FromSeconds(0.7),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                ReconnectRotation.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);

                UpdateConnectionStatus("Connecting");
                string host = !string.IsNullOrWhiteSpace(TxtIpAddress?.Text) 
                    ? TxtIpAddress.Text.Trim() 
                    : (!string.IsNullOrWhiteSpace(TxtDeviceHost?.Text) ? TxtDeviceHost.Text.Trim() : _settings.EspHost);
                int port = int.TryParse(TxtDevicePort.Text.Trim(), out int p) ? p : 80;

                await _espService.ForceReconnectAsync(host, port, _settings.EspUsername, _settings.EspPassword);
            }
            catch { }
            finally
            {
                ReconnectRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                ReconnectRotation.Angle = 0;
                BtnReconnect.IsEnabled = true;
            }
        }

        private void UpdateConnectionStatus(string status)
        {
            TxtEspStatus.Text = $"ESP: {status}";
            TxtDeviceState.Text = status;

            bool isBeige = _settings.Theme.Equals("Beige", StringComparison.OrdinalIgnoreCase);

            if (status == "Connected")
            {
                if (isBeige)
                {
                    EspBadge.Background = new SolidColorBrush(Color.FromRgb(125, 163, 133));
                    EspBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(105, 143, 113));
                    EspStatusDot.Fill = Brushes.White;
                    TxtEspStatus.Foreground = Brushes.White;
                }
                else
                {
                    EspBadge.Background = (Brush)Resources["BrushPillBg"];
                    EspBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 74, 222, 128));
                    EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                    TxtEspStatus.Foreground = (Brush)Resources["BrushTextPrimary"];
                }
                TxtDeviceState.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
            }
            else if (status == "Connecting")
            {
                if (isBeige)
                {
                    EspBadge.Background = new SolidColorBrush(Color.FromRgb(212, 163, 89));
                    EspBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(190, 140, 70));
                    EspStatusDot.Fill = Brushes.White;
                    TxtEspStatus.Foreground = Brushes.White;
                }
                else
                {
                    EspBadge.Background = (Brush)Resources["BrushPillBg"];
                    EspBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 251, 191, 36));
                    EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    TxtEspStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                }
                TxtDeviceState.Foreground = new SolidColorBrush(Color.FromRgb(234, 179, 8));
            }
            else
            {
                if (isBeige)
                {
                    EspBadge.Background = new SolidColorBrush(Color.FromRgb(194, 120, 99));
                    EspBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(174, 100, 80));
                    EspStatusDot.Fill = Brushes.White;
                    TxtEspStatus.Foreground = Brushes.White;
                }
                else
                {
                    EspBadge.Background = (Brush)Resources["BrushPillBg"];
                    EspBadge.BorderBrush = (Brush)Resources["BrushPillBorder"];
                    EspStatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    TxtEspStatus.Foreground = (Brush)Resources["BrushTextMuted"];
                }
                TxtDeviceState.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }

        private void UpdateLhmStatus(bool active)
        {
            bool isBeige = _settings.Theme.Equals("Beige", StringComparison.OrdinalIgnoreCase);

            if (active)
            {
                TxtSensingStatus.Text = "Sensing Active";
                if (isBeige)
                {
                    SensingBadge.Background = new SolidColorBrush(Color.FromRgb(125, 163, 133));
                    SensingBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(105, 143, 113));
                    SensingStatusDot.Fill = Brushes.White;
                    TxtSensingStatus.Foreground = Brushes.White;
                }
                else
                {
                    SensingBadge.Background = (Brush)Resources["BrushPillBg"];
                    SensingBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 74, 222, 128));
                    SensingStatusDot.Fill = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                    TxtSensingStatus.Foreground = (Brush)Resources["BrushTextPrimary"];
                }
            }
            else
            {
                TxtSensingStatus.Text = "Sensing Offline";
                SensingBadge.Background = (Brush)Resources["BrushPillBg"];
                SensingBadge.BorderBrush = (Brush)Resources["BrushPillBorder"];
                SensingStatusDot.Fill = (Brush)Resources["BrushTextMuted"];
                TxtSensingStatus.Foreground = (Brush)Resources["BrushTextMuted"];
            }
        }

        private void UpdateCpuTemperature(float temp)
        {
            _lastCpuTemp = temp;

            // Smooth animated gauge transition over 350ms
            var anim = new DoubleAnimation(temp, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            CpuGauge.BeginAnimation(CircularGauge.ValueProperty, anim);

            CurveGraph.SetData(_settings.Thresholds, temp);
            if (_activeView == "Curves") CurveGraphLarge.SetData(_settings.Thresholds, temp);

            TxtTelemCpu.Text = $"Core (Tctl/Tdie): {temp:F1}°C";
            _trayIcon?.UpdateCpu(temp, _lastSpeed);
        }

        private void UpdateGpuTemperature(float temp)
        {
            _lastGpuTemp = temp;

            // Smooth animated gauge transition over 350ms
            var anim = new DoubleAnimation(temp, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            GpuGauge.BeginAnimation(CircularGauge.ValueProperty, anim);

            TxtTelemGpu.Text = $"GPU Core: {temp:F1}°C";
            _trayIcon?.UpdateGpu(temp, _hwService.GpuName);
        }

        private void UpdateFanState(bool isOn, int speed)
        {
            TxtFanState.Text = isOn && speed > 0 ? $"Fan: {speed}%" : "Fan: OFF";
        }

        private void UpdateTelemetry(string uptime, float? wifiRssi)
        {
            TxtUptime.Text = uptime;
            TxtWifiSignal.Text = wifiRssi.HasValue ? $"{wifiRssi.Value:F0} dBm" : "-- dBm";

            TxtDeviceUptime.Text = uptime;
            TxtDeviceWifi.Text = wifiRssi.HasValue ? $"{wifiRssi.Value:F0} dBm" : "-- dBm";
        }
        #endregion

        #region Device Hub Actions
        private async void BtnDeviceSaveConnect_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtDeviceHost.Text.Trim();
            int port = int.TryParse(TxtDevicePort.Text.Trim(), out int p) ? p : 80;
            string user = TxtDeviceUser.Text.Trim();
            string pass = TxtDevicePass.Text;

            if (!string.IsNullOrEmpty(host))
            {
                _settings.EspHost = host;
                _settings.EspPort = port;
                _settings.EspUsername = user;
                _settings.EspPassword = pass;
                _settings.Save();

                TxtIpAddress.Text = host;
                UpdateConnectionStatus("Connecting");
                await _espService.ForceReconnectAsync(host, port, user, pass);
            }
        }

        private async void BtnDeviceFan100_Click(object sender, RoutedEventArgs e)
        {
            await _espService.SetFanSpeedAsync(100);
        }

        private async void BtnDeviceFan0_Click(object sender, RoutedEventArgs e)
        {
            await _espService.SetFanSpeedAsync(0);
        }
        #endregion

        #region Speed Controls & Presets
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
            string host = !string.IsNullOrWhiteSpace(TxtDeviceHost.Text) ? TxtDeviceHost.Text.Trim() : _settings.EspHost;
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

        private async void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            await ExitApplicationAsync();
        }

        private void BtnPresetSilent_Click(object sender, RoutedEventArgs e)
        {
            // Silent Profile: Remains OFF below 60°C, then steps up smoothly
            var points = new List<CurvePoint>
            {
                new(60, 40),
                new(68, 55),
                new(75, 75),
                new(82, 90),
                new(88, 100)
            };
            ApplyPreset(points, "Silent");
        }

        private void BtnPresetStandard_Click(object sender, RoutedEventArgs e)
        {
            // Standard Profile (Starting at 55°C = 50%, below 55°C = OFF)
            var points = new List<CurvePoint>
            {
                new(55, 50),
                new(62, 65),
                new(68, 80),
                new(74, 90),
                new(80, 100)
            };
            ApplyPreset(points, "Standard");
        }

        private void BtnPresetAggressive_Click(object sender, RoutedEventArgs e)
        {
            // Performance Profile: Starts at 50°C = 50%
            var points = new List<CurvePoint>
            {
                new(50, 50),
                new(58, 70),
                new(65, 85),
                new(72, 95),
                new(78, 100)
            };
            ApplyPreset(points, "Performance");
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            BtnPresetStandard_Click(sender, e);
        }

        private string _lastActivePreset = "Standard";
        private void UpdatePresetButtonsState(string activePreset)
        {
            _lastActivePreset = activePreset;
            if (BtnPresetStandard == null) return;

            var accent = (Brush)Resources["BrushAccent"];
            var primText = (Brush)Resources["BrushPrimaryBtnText"];
            var secBg = (Brush)Resources["BrushSecondaryBtnBg"];
            var secBorder = (Brush)Resources["BrushSecondaryBtnBorder"];
            var secText = (Brush)Resources["BrushSecondaryBtnText"];

            void StyleBtn(Button? btn, bool active)
            {
                if (btn == null) return;
                if (active)
                {
                    btn.Background = accent;
                    btn.BorderBrush = Brushes.Transparent;
                    btn.Foreground = primText;
                }
                else
                {
                    btn.Background = secBg;
                    btn.BorderBrush = secBorder;
                    btn.Foreground = secText;
                }
            }

            StyleBtn(BtnPresetSilent, activePreset == "Silent");
            StyleBtn(BtnPresetStandard, activePreset == "Standard");
            StyleBtn(BtnPresetAggressive, activePreset == "Performance");
        }

        private void ApplyPreset(List<CurvePoint> points, string presetName)
        {
            _settings.Thresholds = points;
            CurveGraph.SetData(points, _lastCpuTemp);
            CurveGraphLarge.SetData(points, _lastCpuTemp);
            _controller.UpdateThresholds(points);
            _settings.Save();
            PopulateThresholdsEditor();
            UpdatePresetButtonsState(presetName);
        }
        #endregion

        #region Window Lifecycle & Tray
        private void SetupSystemTray()
        {
            if (_trayIcon != null) return;
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

            var themeMenu = new MenuItem { Header = "Theme" };
            var plumItem = new MenuItem { Header = "Plum Obsidian (Dark)" };
            plumItem.Click += (s, e) => ApplyTheme("Plum");
            var beigeItem = new MenuItem { Header = "Warm Beige (Light)" };
            beigeItem.Click += (s, e) => ApplyTheme("Beige");
            var amoledItem = new MenuItem { Header = "AMOLED Pitch (Pure Black)" };
            amoledItem.Click += (s, e) => ApplyTheme("Amoled");
            themeMenu.Items.Add(plumItem);
            themeMenu.Items.Add(beigeItem);
            themeMenu.Items.Add(amoledItem);

            var exitItem = new MenuItem { Header = "Exit Application" };
            exitItem.Click += async (s, e) => await ExitApplicationAsync();

            contextMenu.Items.Add(showHideItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(autoItem);
            contextMenu.Items.Add(fullSpeedItem);
            contextMenu.Items.Add(reconnectItem);
            contextMenu.Items.Add(themeMenu);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
            _trayIcon.DoubleClick += ToggleWindowVisibility;
            _trayIcon.Initialize();
        }

        private void ToggleWindowVisibility()
        {
            if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized && IsActive)
            {
                Hide();
                ShowInTaskbar = false;
                TrimMemory();
            }
            else
            {
                ShowInTaskbar = true;
                Visibility = Visibility.Visible;
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Show();
                WindowState = WindowState.Normal;
                Activate();

                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                SetForegroundWindow(helper.Handle);
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
            _hwService.Dispose();
            _controller.Dispose();
        }
        #endregion
    }
}
