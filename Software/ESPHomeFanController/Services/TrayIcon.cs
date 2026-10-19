using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

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
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly Window _window;
        private HwndSource? _hwndSource;
        private IntPtr _hIcon = IntPtr.Zero;
        private bool _isCreated;
        private string _tooltip = "ESPHome Fan Controller";

        public ContextMenu? ContextMenu { get; set; }
        public event Action? DoubleClick;

        public TrayIcon(Window window)
        {
            _window = window;
        }

        public void Initialize()
        {
            var helper = new WindowInteropHelper(_window);
            IntPtr hwnd = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            LoadIcon();

            var data = CreateNotifyData(hwnd);
            _isCreated = Shell_NotifyIcon(NIM_ADD, ref data);
        }

        private void LoadIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_icon.ico");
                if (File.Exists(iconPath))
                {
                    const uint IMAGE_ICON = 1;
                    const uint LR_LOADFROMFILE = 0x00000010;
                    _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                }
            }
            catch { }
        }

        public void UpdateTooltip(string text)
        {
            _tooltip = text.Length > 127 ? text.Substring(0, 127) : text;
            if (_isCreated && _hwndSource != null)
            {
                var data = CreateNotifyData(_hwndSource.Handle);
                data.uFlags = NIF_TIP;
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
        }

        private NOTIFYICONDATA CreateNotifyData(IntPtr hwnd)
        {
            return new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = _tooltip
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
            if (_isCreated && _hwndSource != null)
            {
                var data = CreateNotifyData(_hwndSource.Handle);
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _isCreated = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
    }
}
