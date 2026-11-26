using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaTray;

internal static class Win32
{
    internal const uint Destroy = 0x0002, Paint = 0x000F, Close = 0x0010, EraseBackground = 0x0014,
        DisplayChange = 0x007E, KeyDown = 0x0100, Command = 0x0111, Timer = 0x0113,
        ContextMenu = 0x007B, DrawItem = 0x002B, ControlEdit = 0x0133, ControlStatic = 0x0138,
        ControlList = 0x0134, MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202,
        RightUp = 0x0205, Wheel = 0x020A, CaptureChanged = 0x0215, DpiChanged = 0x02E0,
        MouseHover = 0x02A1, MouseLeave = 0x02A3, App = 0x8000;
    internal const uint ExTopmost = 0x8, ExTransparent = 0x20, ExToolWindow = 0x80,
        ExLayered = 0x80000, ExNoActivate = 0x08000000;
    internal const uint Popup = 0x80000000, Child = 0x40000000, Visible = 0x10000000,
        ClipChildren = 0x02000000, Caption = 0x00C00000, SystemMenu = 0x00080000,
        TabStop = 0x00010000, VerticalScroll = 0x00200000;
    internal const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 0x10;
    internal static readonly nint Topmost = -1, NotTopmost = -2;

    internal static nint Instance => GetModuleHandle(null);
    internal static Point Cursor => GetCursorPos(out var point) ? point : Point.Empty;
    internal static Point MessagePosition
    {
        get
        {
            var position = GetMessagePos();
            return new Point(unchecked((short)position), unchecked((short)(position >> 16)));
        }
    }
    internal static int Low(nuint value) => (int)(value & 0xffff);
    internal static int High(nuint value) => (int)((value >> 16) & 0xffff);
    internal static int Dpi(nint window) => Math.Max(96, (int)GetDpiForWindow(window));
    internal static int Px(float value, int dpi) => (int)Math.Round(value * dpi / 96f);
    internal static PointF LogicalPoint(Point screen, Rectangle bounds, int dpi) =>
        new((screen.X - bounds.Left) * 96f / dpi, (screen.Y - bounds.Top) * 96f / dpi);

    internal static Rectangle WorkArea(Point point)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromPoint(point, 2), ref info)
            ? info.Work.Rectangle : new Rectangle(0, 0, Metric(0), Metric(1));
    }

    internal static void TrackMouse(nint hwnd, bool hover = true)
    {
        var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Window = hwnd, Flags = hover ? 3u : 2u, HoverTime = 230 };
        TrackMouseEvent(ref tracking);
    }

    internal static string Text(nint hwnd)
    {
        var buffer = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static void Error(nint owner, string message, string title) => MessageBox(owner, message, title, 0x10);
    internal static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Hwnd;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName, ClassName;
        internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        internal uint Size, Flags;
        internal nint Window;
        internal uint HoverTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageSize;
        internal int XPelsPerMeter, YPelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blend
    {
        internal byte Operation, Flags, Alpha, Format;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TrayIcon
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindow(uint extendedStyle, string className, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint Default(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "IsDialogMessageW")] internal static extern bool IsDialogMessage(nint dialog, ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam = 0, nint lParam = 0);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] internal static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] internal static extern nint SendText(nint hwnd, uint message, nuint wParam, string text);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(nint hwnd, bool enable);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool AdjustWindowRectExForDpi(ref Rect rectangle, uint style, bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] internal static extern int Metric(int index);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTracking tracking);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint procedure = 0);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] internal static extern nint LoadCursor(nint instance, nint id);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] internal static extern uint RegisterMessage(string name);
    [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] internal static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] internal static extern int MessageBox(nint hwnd, string text, string title, uint type);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] internal static extern bool SetText(nint hwnd, string text);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, ref Point destination, ref Size size, nint sourceDc, ref Point source, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] internal static extern int SetBkColor(nint dc, int color);
    [DllImport("gdi32.dll")] internal static extern int SetTextColor(nint dc, int color);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)] internal static extern nint CreateFont(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string family);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] internal static extern bool NotifyIcon(uint message, ref TrayIcon data);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

internal sealed class NativeWindow : IDisposable
{
    private const string WindowClassName = "CodexQuotaTray.NativeWindow";
    private static readonly Dictionary<nint, NativeWindow> Windows = [];
    private static readonly WindowProcedure WindowProc = Dispatch;
    private static ushort _class;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam);

    internal NativeWindow(string title, uint exStyle = 0, uint style = Win32.Popup, nint owner = 0)
    {
        if (_class == 0)
        {
            var windowClass = new Win32.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Win32.WindowClass>(), Instance = Win32.Instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(WindowProc), ClassName = WindowClassName,
                Cursor = Win32.LoadCursor(0, 32512)
            };
            _class = Win32.RegisterClass(ref windowClass);
            if (_class == 0) throw new Win32Exception();
        }

        Handle = Win32.CreateWindow(exStyle, WindowClassName, title, style, 0, 0, 1, 1, owner, 0, Win32.Instance, 0);
        if (Handle == 0) throw new Win32Exception();
        Windows.Add(Handle, this);
    }

    internal Func<uint, nuint, nint, nint?>? MessageReceived { get; set; }
    internal nint Handle { get; private set; }
    internal bool IsVisible => Handle != 0 && Win32.IsWindowVisible(Handle);
    internal Rectangle Bounds => Win32.GetWindowRect(Handle, out var rect) ? rect.Rectangle : Rectangle.Empty;
    internal void Show(bool activate = true) => Win32.ShowWindow(Handle, activate ? 5 : 8);
    internal void Hide() => Win32.ShowWindow(Handle, 0);
    internal void Move(Point point) => Win32.SetWindowPos(Handle, 0, point.X, point.Y, 0, 0, Win32.NoSize | Win32.NoZOrder | Win32.NoActivate);
    internal void SetTopmost(bool value) => Win32.SetWindowPos(Handle, value ? Win32.Topmost : Win32.NotTopmost, 0, 0, 0, 0, Win32.NoMove | Win32.NoSize | Win32.NoActivate);

    internal static void Run(nint dialog = 0, Func<bool>? continueLoop = null)
    {
        while (continueLoop?.Invoke() != false)
        {
            var code = Win32.GetMessage(out var message, 0, 0, 0);
            if (code == 0)
            {
                if (dialog != 0) Win32.PostQuitMessage((int)message.WParam);
                return;
            }
            if (code < 0) throw new Win32Exception();
            if (dialog != 0 && Win32.IsDialogMessage(dialog, ref message)) continue;
            Win32.TranslateMessage(ref message);
            Win32.DispatchMessage(ref message);
        }
    }

    private static nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out var window))
        {
            try
            {
                var result = window.MessageReceived?.Invoke(message, wParam, lParam);
                if (result.HasValue) return result.Value;
            }
            catch (Exception ex)
            {
                Win32.Error(hwnd, ex.Message, "QuotaTray");
            }
        }
        return Win32.Default(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Win32.DestroyWindow(Handle);
        Windows.Remove(Handle);
        Handle = 0;
    }
}

internal sealed class PopupMenu : IDisposable
{
    private readonly nint _handle = Win32.CreatePopupMenu();
    private bool _ownsHandle = true;

    internal void Add(uint id, string label, bool check = false, bool enabled = true) =>
        Win32.AppendMenu(_handle, (check ? 8u : 0) | (enabled ? 0 : 1u), id, label);

    internal void Separator() => Win32.AppendMenu(_handle, 0x800, 0, null);

    internal void AddSubmenu(string label, PopupMenu submenu)
    {
        if (Win32.AppendMenu(_handle, 0x10, (nuint)submenu._handle, label)) submenu._ownsHandle = false;
    }

    internal uint Show(nint owner, Point position)
    {
        Win32.SetForegroundWindow(owner);
        var choice = Win32.TrackPopupMenuEx(_handle, 0x102, position.X, position.Y, owner, 0);
        Win32.PostMessage(owner, 0);
        return choice;
    }

    public void Dispose()
    {
        if (_ownsHandle) Win32.DestroyMenu(_handle);
    }
}
