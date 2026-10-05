using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dyss.App.Services;

/// <summary>An entry of the icon's menu; "-" is a separator.</summary>
internal sealed record TrayMenuItem(string Text, Action? OnClick = null, bool Enabled = true)
{
    public static TrayMenuItem Separator { get; } = new("-");
    public bool IsSeparator => Text == "-";
}

/// <summary>
/// Icon in the Windows notification area, implemented with Shell_NotifyIcon. Taken from HusqA
/// Cockpit. Must be created on the UI thread: its hidden window shares the WinUI message loop,
/// so callbacks run on the UI thread. It comes back by itself when Explorer restarts.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const string WindowClassName = "DyssCockpit.TrayWindow";
    private const uint CallbackMessage = Native.WM_APP + 1;
    private const uint IconId = 1;

    private readonly Native.WndProc _wndProc;
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menuProvider;
    private IntPtr _icon;
    private string _tooltip;
    private bool _disposed;

    public TrayIcon(string iconPath, string tooltip, Func<IReadOnlyList<TrayMenuItem>> menuProvider)
    {
        _tooltip = tooltip;
        _menuProvider = menuProvider;
        _wndProc = WindowProc; // Keep the delegate alive for the lifetime of the window.

        var instance = Native.GetModuleHandle(null);
        var windowClass = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClassName,
        };
        if (Native.RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed");
        }

        // A hidden top-level window (not message-only) so that it receives the TaskbarCreated broadcast.
        _hwnd = Native.CreateWindowEx(0, WindowClassName, "DySS Cockpit tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed");
        }
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

        _icon = LoadIcon(iconPath);
        if (!AddIcon())
        {
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed");
        }
    }

    /// <summary>Left click (or Enter) on the icon.</summary>
    public event EventHandler? Activated;

    public void Update(string? iconPath = null, string? tooltip = null)
    {
        if (_disposed)
        {
            return;
        }

        if (iconPath is not null)
        {
            var previous = _icon;
            _icon = LoadIcon(iconPath);
            if (previous != IntPtr.Zero)
            {
                Native.DestroyIcon(previous);
            }
        }
        if (tooltip is not null)
        {
            _tooltip = tooltip;
        }

        var data = CreateData(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        var data = CreateData(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero)
        {
            Native.DestroyIcon(_icon);
        }
        Native.DestroyWindow(_hwnd);
        Native.UnregisterClass(WindowClassName, Native.GetModuleHandle(null));
    }

    private static IntPtr LoadIcon(string path)
    {
        var size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        return Native.LoadImage(IntPtr.Zero, path, Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);
    }

    private bool AddIcon()
    {
        var data = CreateData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref data))
        {
            return false;
        }
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        return Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private Native.NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam holds the anchor coordinates.
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case Native.NIN_SELECT:
                case Native.NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case Native.WM_CONTEXTMENU:
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }
            return IntPtr.Zero;
        }

        if (message == _taskbarCreatedMessage && !_disposed)
        {
            // Explorer restarted: the icon must be added again.
            _ = AddIcon();
            return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menuProvider();
        var menu = Native.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var flags = item.IsSeparator ? Native.MF_SEPARATOR : Native.MF_STRING | (item.Enabled ? 0 : Native.MF_GRAYED);
                Native.AppendMenu(menu, flags, (UIntPtr)(i + 1), item.IsSeparator ? null : item.Text);
            }

            // Required so that the menu closes when the user clicks elsewhere.
            Native.SetForegroundWindow(_hwnd);
            var command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, x, y, _hwnd, IntPtr.Zero);
            Native.PostMessage(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0 && command <= items.Count)
            {
                items[command - 1].OnClick?.Invoke();
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private static class Native
    {
        public const uint WM_NULL = 0x0000;
        public const uint WM_CONTEXTMENU = 0x007B;
        public const uint WM_APP = 0x8000;
        public const uint NIN_SELECT = 0x0400;
        public const uint NIN_KEYSELECT = 0x0401;
        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;
        public const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_SEPARATOR = 0x0800;
        public const uint TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020, TPM_RETURNCMD = 0x0100;
        public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
        public const int SM_CXSMICON = 49;

        public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
