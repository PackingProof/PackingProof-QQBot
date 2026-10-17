using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PackingProof.QQBot;

/// <summary>
/// QQBot 插件的托盘图标。插件版由宿主通过 DOTNET_ROOT 复用共享运行时，
/// 因此这里不能依赖 WinForms，只用 Windows 原生的 Shell_NotifyIcon。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint MF_STRING = 0x00000000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_TRAYICON = 0x8001;
    private const uint TrayIconId = 1;
    private const uint MenuOpen = 1;
    private const uint MenuExit = 2;
    private const int IDI_APPLICATION = 32512;

    private readonly Window _window;
    private readonly Action _openRequested;
    private readonly Action _exitRequested;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly uint _taskbarCreatedMessage;
    private NOTIFYICONDATA _data;
    private bool _disposed;

    public TrayIconService(Window window, Action openRequested, Action exitRequested)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _openRequested = openRequested ?? throw new ArgumentNullException(nameof(openRequested));
        _exitRequested = exitRequested ?? throw new ArgumentNullException(nameof(exitRequested));

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        (_icon, _ownsIcon) = LoadApplicationIcon();
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = "PackingProof QQBot"
        };
        Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    private static (IntPtr Icon, bool OwnsIcon) LoadApplicationIcon()
    {
        string? executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                if (ExtractIconEx(executablePath, 0, out IntPtr large, out IntPtr small, 1) > 0)
                {
                    if (small != IntPtr.Zero) return (small, true);
                    if (large != IntPtr.Zero) return (large, true);
                }
            }
            catch
            {
                // 取不到 exe 图标时退回系统默认图标。
            }
        }

        return (LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION)), false);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
            return IntPtr.Zero;

        if (_taskbarCreatedMessage != 0 && msg == unchecked((int)_taskbarCreatedMessage))
        {
            // Explorer 重启后重新注册托盘图标。
            Shell_NotifyIcon(NIM_ADD, ref _data);
            return IntPtr.Zero;
        }

        if (msg != WM_TRAYICON)
            return IntPtr.Zero;

        int notification = lParam.ToInt32() & 0xFFFF;
        if (notification == WM_LBUTTONDBLCLK)
        {
            _openRequested();
            handled = true;
        }
        else if (notification is WM_RBUTTONUP or WM_CONTEXTMENU)
        {
            ShowContextMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MF_STRING, new UIntPtr(MenuOpen), "打开管理界面");
            AppendMenuW(menu, MF_STRING, new UIntPtr(MenuExit), "退出 QQBot");
            if (!GetCursorPos(out POINT point))
                return;

            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen) _openRequested();
            else if (command == MenuExit) _exitRequested();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        _source?.RemoveHook(WndProc);
        if (_ownsIcon && _icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
