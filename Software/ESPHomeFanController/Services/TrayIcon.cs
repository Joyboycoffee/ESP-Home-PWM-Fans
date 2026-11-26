using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ESPHomeFanController.Services
{
    public class TrayIcon : IDisposable
    {
        private const int WM_USER = 0x0400;
        private const int WM_TRAYICON = WM_USER + 101;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const int CPU_ICON_ID = 1001;
        private const int GPU_ICON_ID = 1002;

        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        private static readonly Color CpuWhite = Color.FromArgb(255, 255, 255);
        private static readonly Color GpuGreen = Color.FromArgb(0x22, 0xc5, 0x5e); // #22c55e vibrant green

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public uint uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int RegisterWindowMessage(string lpString);

        private readonly Window _window;
        private readonly object _lock = new();
        private HwndSource? _hwndSource;
        private readonly int _wmTaskbarCreated;

        private IntPtr _cpuHIcon = IntPtr.Zero;
        private IntPtr _gpuHIcon = IntPtr.Zero;
        private bool _cpuCreated;
        private bool _gpuCreated;

        private string _cpuTooltip = "ESPHome Fan Controller - CPU";
        private string _gpuTooltip = "ESPHome Fan Controller - GPU";
        private string _lastCpuText = "";
        private string _lastGpuText = "";

        private static readonly string PreferredFont = ResolveFontName();

        public ContextMenu? ContextMenu { get; set; }
        public event Action? DoubleClick;

        public TrayIcon(Window window)
        {
            _window = window;
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }

        public void Initialize()
        {
            var helper = new WindowInteropHelper(_window);
            IntPtr hwnd = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            lock (_lock)
            {
                CreateOrUpdateIcons(hwnd);
            }
        }

        private void CreateOrUpdateIcons(IntPtr hwnd)
        {
            // Initial CPU icon (uID = 1001)
            if (_cpuHIcon == IntPtr.Zero)
            {
                _lastCpuText = "--";
                _cpuHIcon = CreateTextIcon(_lastCpuText, CpuWhite);
            }

            var cpuData = CreateNotifyData(hwnd, CPU_ICON_ID, _cpuHIcon, _cpuTooltip);
            if (!_cpuCreated)
            {
                _cpuCreated = Shell_NotifyIcon(NIM_ADD, ref cpuData);
            }

            // Initial GPU icon (uID = 1002)
            if (_gpuHIcon == IntPtr.Zero)
            {
                _lastGpuText = "--";
                _gpuHIcon = CreateTextIcon(_lastGpuText, GpuGreen);
            }

            var gpuData = CreateNotifyData(hwnd, GPU_ICON_ID, _gpuHIcon, _gpuTooltip);
            if (!_gpuCreated)
            {
                _gpuCreated = Shell_NotifyIcon(NIM_ADD, ref gpuData);
            }
        }

        public void UpdateCpu(float temp, int fanSpeed)
        {
            lock (_lock)
            {
                string text = Math.Round(temp).ToString("0");
                string fanStr = fanSpeed == 0 ? "Off (0%)" : $"{fanSpeed}%";
                string tooltip = $"CPU: {temp:F1}°C | Fan: {fanStr}";
                if (tooltip.Length > 127) tooltip = tooltip.Substring(0, 127);

                bool textChanged = text != _lastCpuText;
                bool tipChanged = tooltip != _cpuTooltip;

                _cpuTooltip = tooltip;
                _lastCpuText = text;

                if (_hwndSource == null) return;
                IntPtr hwnd = _hwndSource.Handle;

                if (!_cpuCreated)
                {
                    _cpuHIcon = CreateTextIcon(text, CpuWhite);
                    var data = CreateNotifyData(hwnd, CPU_ICON_ID, _cpuHIcon, _cpuTooltip);
                    _cpuCreated = Shell_NotifyIcon(NIM_ADD, ref data);
                    return;
                }

                if (textChanged)
                {
                    TriggerIconFade(CPU_ICON_ID, text, CpuWhite, _cpuTooltip);
                }
                else if (tipChanged)
                {
                    var data = CreateNotifyData(hwnd, CPU_ICON_ID, _cpuHIcon, _cpuTooltip);
                    data.uFlags = NIF_TIP;
                    Shell_NotifyIcon(NIM_MODIFY, ref data);
                }
            }
        }

        public void UpdateGpu(float temp, string? gpuName = null)
        {
            lock (_lock)
            {
                string text = Math.Round(temp).ToString("0");
                string name = !string.IsNullOrWhiteSpace(gpuName) ? gpuName : "NVIDIA RTX 3070";
                string tooltip = $"GPU: {temp:F1}°C ({name})";
                if (tooltip.Length > 127) tooltip = tooltip.Substring(0, 127);

                bool textChanged = text != _lastGpuText;
                bool tipChanged = tooltip != _gpuTooltip;

                _gpuTooltip = tooltip;
                _lastGpuText = text;

                if (_hwndSource == null) return;
                IntPtr hwnd = _hwndSource.Handle;

                if (!_gpuCreated)
                {
                    _gpuHIcon = CreateTextIcon(text, GpuGreen);
                    var data = CreateNotifyData(hwnd, GPU_ICON_ID, _gpuHIcon, _gpuTooltip);
                    _gpuCreated = Shell_NotifyIcon(NIM_ADD, ref data);
                    return;
                }

                if (textChanged)
                {
                    TriggerIconFade(GPU_ICON_ID, text, GpuGreen, _gpuTooltip);
                }
                else if (tipChanged)
                {
                    var data = CreateNotifyData(hwnd, GPU_ICON_ID, _gpuHIcon, _gpuTooltip);
                    data.uFlags = NIF_TIP;
                    Shell_NotifyIcon(NIM_MODIFY, ref data);
                }
            }
        }

        public void UpdateTooltip(string text)
        {
            lock (_lock)
            {
                _cpuTooltip = text.Length > 127 ? text.Substring(0, 127) : text;
                if (_cpuCreated && _hwndSource != null)
                {
                    var data = CreateNotifyData(_hwndSource.Handle, CPU_ICON_ID, _cpuHIcon, _cpuTooltip);
                    data.uFlags = NIF_TIP;
                    Shell_NotifyIcon(NIM_MODIFY, ref data);
                }
            }
        }

        private void TriggerIconFade(int iconId, string text, Color color, string tooltip)
        {
            if (_hwndSource == null) return;
            IntPtr hwnd = _hwndSource.Handle;

            System.Threading.Tasks.Task.Run(async () =>
            {
                float[] steps = [0.5f, 0.8f, 1.0f];
                foreach (float alpha in steps)
                {
                    lock (_lock)
                    {
                        IntPtr newIcon = CreateTextIcon(text, color, alpha);
                        IntPtr oldIcon = IntPtr.Zero;

                        if (iconId == CPU_ICON_ID)
                        {
                            oldIcon = _cpuHIcon;
                            _cpuHIcon = newIcon;
                        }
                        else
                        {
                            oldIcon = _gpuHIcon;
                            _gpuHIcon = newIcon;
                        }

                        var data = CreateNotifyData(hwnd, iconId, newIcon, tooltip);
                        data.uFlags = NIF_ICON | NIF_TIP;
                        Shell_NotifyIcon(NIM_MODIFY, ref data);

                        if (oldIcon != IntPtr.Zero)
                        {
                            DestroyIcon(oldIcon);
                        }
                    }
                    await System.Threading.Tasks.Task.Delay(35);
                }
            });
        }

        private static IntPtr CreateTextIcon(string text, Color textColor, float opacity = 1.0f)
        {
            int width = GetSystemMetrics(SM_CXSMICON);
            int height = GetSystemMetrics(SM_CYSMICON);
            if (width <= 0 || height <= 0)
            {
                width = 16;
                height = 16;
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                int alpha = (int)Math.Clamp(opacity * 255f, 0f, 255f);
                var blendedColor = Color.FromArgb(alpha, textColor.R, textColor.G, textColor.B);

                // Matches exact size of Windows 11 taskbar time text (~11px glyph height)
                float fontSize = text.Length <= 2 ? (height * 0.84f) : (height * 0.62f);
                using var font = new Font(PreferredFont, fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(blendedColor);
                using var sf = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                };

                // Vertical baseline alignment with taskbar clock
                var rect = new RectangleF(0, -0.5f, width, height);

                g.DrawString(text, font, brush, rect, sf);
            }

            return bitmap.GetHicon();
        }

        private static string ResolveFontName()
        {
            string[] preferred = ["Segoe UI", "Bahnschrift", "Arial"];
            foreach (var name in preferred)
            {
                try
                {
                    using var font = new Font(name, 12f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                    if (font.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
                catch { }
            }

            return "Arial";
        }

        private static NOTIFYICONDATA CreateNotifyData(IntPtr hwnd, int uID, IntPtr hIcon, string tooltip)
        {
            return new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = uID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = hIcon,
                szTip = tooltip
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int mouseMsg = (int)lParam;
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    DoubleClick?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                    handled = true;
                }
            }
            else if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
            {
                lock (_lock)
                {
                    _cpuCreated = false;
                    _gpuCreated = false;
                    CreateOrUpdateIcons(hwnd);
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            if (ContextMenu == null) return;

            var helper = new WindowInteropHelper(_window);
            SetForegroundWindow(helper.Handle);

            ContextMenu.PlacementTarget = _window;
            ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            ContextMenu.IsOpen = true;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_hwndSource != null)
                {
                    IntPtr hwnd = _hwndSource.Handle;
                    if (_cpuCreated)
                    {
                        var data = CreateNotifyData(hwnd, CPU_ICON_ID, IntPtr.Zero, string.Empty);
                        Shell_NotifyIcon(NIM_DELETE, ref data);
                        _cpuCreated = false;
                    }
                    if (_gpuCreated)
                    {
                        var data = CreateNotifyData(hwnd, GPU_ICON_ID, IntPtr.Zero, string.Empty);
                        Shell_NotifyIcon(NIM_DELETE, ref data);
                        _gpuCreated = false;
                    }

                    _hwndSource.RemoveHook(WndProc);
                    _hwndSource = null;
                }

                if (_cpuHIcon != IntPtr.Zero)
                {
                    DestroyIcon(_cpuHIcon);
                    _cpuHIcon = IntPtr.Zero;
                }

                if (_gpuHIcon != IntPtr.Zero)
                {
                    DestroyIcon(_gpuHIcon);
                    _gpuHIcon = IntPtr.Zero;
                }
            }
        }
    }
}
