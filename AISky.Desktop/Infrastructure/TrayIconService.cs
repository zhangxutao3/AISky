using System.Runtime.InteropServices;

namespace AISky_Desktop.Infrastructure;

public enum TrayIconState
{
    Normal,
    Syncing,
    Error,
}

public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 121;
    private const int GwlpWndProc = -4;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint MfString = 0x00000000;
    private const uint MfGray = 0x00000001;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;
    private const int IdiInformation = 32516;
    private const int IdiError = 32513;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;
    private const uint NiifError = 0x00000003;
    private const uint CmdOpen = 1001;
    private const uint CmdSync = 1002;
    private const uint CmdToggleAutoSync = 1003;
    private const uint CmdCheckUpdates = 1004;
    private const uint CmdExit = 1005;

    private readonly nint _windowHandle;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _applicationIcon;
    private readonly nint _informationIcon;
    private readonly nint _errorIcon;
    private nint _previousWindowProcedure;
    private bool _autoSyncEnabled;
    private string _statusText = "本地数据服务就绪";
    private bool _disposed;

    public TrayIconService(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProcedure = WindowMessageHandler;
        _applicationIcon = LoadImage(
            0,
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            ImageIcon,
            0,
            0,
            LrLoadFromFile | LrDefaultSize);
        _informationIcon = LoadIcon(0, MakeIntResource(IdiInformation));
        _errorIcon = LoadIcon(0, MakeIntResource(IdiError));
        _previousWindowProcedure = SetWindowProcedure(
            _windowHandle,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (_previousWindowProcedure == 0)
        {
            throw new InvalidOperationException(
                $"无法接管主窗口消息，Win32 错误码 {Marshal.GetLastWin32Error()}。");
        }

        var data = CreateData(NifMessage | NifIcon | NifTip, ResolveIcon(TrayIconState.Normal));
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            RestoreWindowProcedure();
            throw new InvalidOperationException(
                $"无法创建通知区图标，Win32 错误码 {Marshal.GetLastWin32Error()}。");
        }

        data.VersionOrTimeout = NotifyIconVersion4;
        ShellNotifyIcon(NimSetVersion, ref data);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? SyncRequested;
    public event EventHandler? ToggleAutoSyncRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? ExitRequested;

    public void UpdateState(
        TrayIconState state,
        string statusText,
        bool autoSyncEnabled)
    {
        if (_disposed)
        {
            return;
        }

        _autoSyncEnabled = autoSyncEnabled;
        _statusText = string.IsNullOrWhiteSpace(statusText)
            ? "本地数据服务就绪"
            : statusText.Trim();
        var data = CreateData(NifIcon | NifTip, ResolveIcon(state));
        ShellNotifyIcon(NimModify, ref data);
    }

    public void ShowNotification(
        string title,
        string message,
        TrayIconState state = TrayIconState.Normal)
    {
        if (_disposed)
        {
            return;
        }

        var data = CreateData(NifInfo, ResolveIcon(state));
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = state switch
        {
            TrayIconState.Error => NiifError,
            TrayIconState.Syncing => NiifWarning,
            _ => NiifInfo,
        };
        ShellNotifyIcon(NimModify, ref data);
    }

    private nint WindowMessageHandler(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            if (notification is WmLButtonDoubleClick or NinSelect)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return 0;
            }
            if (notification is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return CallWindowProc(_previousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString | MfGray, 0, Truncate(_statusText, 44));
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CmdOpen, "打开 AISky");
            AppendMenu(menu, MfString, CmdSync, "立即同步最新数据");
            AppendMenu(
                menu,
                MfString,
                CmdToggleAutoSync,
                _autoSyncEnabled ? "暂停自动同步" : "开启自动同步");
            AppendMenu(menu, MfString, CmdCheckUpdates, "检查软件更新");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, CmdExit, "退出 AISky");

            GetCursorPos(out var point);
            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCommand,
                point.X,
                point.Y,
                0,
                _windowHandle,
                0);
            switch (command)
            {
                case CmdOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdSync:
                    SyncRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdToggleAutoSync:
                    ToggleAutoSyncRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdCheckUpdates:
                    CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData(uint flags, nint icon) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = icon,
        Tip = Truncate($"AISky · {_statusText}", 127),
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = Guid.Empty,
    };

    private nint ResolveIcon(TrayIconState state) => state switch
    {
        TrayIconState.Syncing => _informationIcon != 0 ? _informationIcon : _applicationIcon,
        TrayIconState.Error => _errorIcon != 0 ? _errorIcon : _applicationIcon,
        _ => _applicationIcon != 0 ? _applicationIcon : _informationIcon,
    };

    private void RestoreWindowProcedure()
    {
        if (_previousWindowProcedure == 0)
        {
            return;
        }

        SetWindowProcedure(_windowHandle, _previousWindowProcedure);
        _previousWindowProcedure = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = CreateData(0, 0);
        ShellNotifyIcon(NimDelete, ref data);
        RestoreWindowProcedure();
        if (_applicationIcon != 0)
        {
            DestroyIcon(_applicationIcon);
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static nint MakeIntResource(int value) => (nint)value;

    private static nint SetWindowProcedure(nint windowHandle, nint procedure) =>
        nint.Size == 8
            ? SetWindowLongPtr(windowHandle, GwlpWndProc, procedure)
            : new nint(SetWindowLong(windowHandle, GwlpWndProc, procedure.ToInt32()));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(
        uint message,
        ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) =>
        Shell_NotifyIcon(message, ref data);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(
        nint previousProcedure,
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int width,
        int height,
        uint load);

    [DllImport("user32.dll")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nuint itemId,
        string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint hwnd,
        nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
