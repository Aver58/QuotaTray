using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexQuotaTray;

internal sealed class NativeSettings : IDisposable
{
    private const uint DialogStyle = Win32.Popup | Win32.Caption | Win32.SystemMenu | Win32.ClipChildren;
    private const uint OwnerDrawButton = 0xB;
    private const uint EditAutoScroll = 0x80, EditPassword = 0x20, EditNumber = 0x2000;
    private static readonly Color Back = Color.FromArgb(0x12, 0x17, 0x20);
    private static readonly Color Field = Color.FromArgb(0x1D, 0x26, 0x33);
    private readonly NativeWindow _window;
    private readonly TraySettings _initial;
    private readonly int _dpi;
    private readonly nint _font, _titleFont, _background, _fieldBrush;
    private readonly Dictionary<nint, string> _buttons = [];
    private readonly nint[] _sourceButtons = new nint[3];
    private readonly nint[] _keyFields;
    private readonly nint _intro, _homeLabel, _home, _replace, _access, _secret, _interval, _reset, _error, _save, _cancel;
    private QuotaSource _selected;
    private bool _finished, _replacing;
    private TraySettings? _result;

    private NativeSettings(nint owner, TraySettings settings)
    {
        _initial = settings;
        _selected = settings.Source;
        _replacing = !settings.HasVolcengineCredentials;
        _window = new NativeWindow("QuotaTray 设置", Win32.ExToolWindow, DialogStyle, owner);
        var workArea = Win32.WorkArea(Win32.Cursor);
        _window.Move(new Point(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2));
        _dpi = Win32.Dpi(_window.Handle);
        _font = MakeFont(13, 400);
        _titleFont = MakeFont(23, 600);
        _background = Win32.CreateSolidBrush(Win32.ColorRef(Back));
        _fieldBrush = Win32.CreateSolidBrush(Win32.ColorRef(Field));
        var dark = 1;
        if (Win32.DwmSetWindowAttribute(_window.Handle, 20, ref dark, 4) != 0)
            Win32.DwmSetWindowAttribute(_window.Handle, 19, ref dark, 4);

        Control("STATIC", "额度设置", 20, 13, 460, 33, font: _titleFont);
        Control("STATIC", "托盘图标与账号", 20, 57, 460, 20);
        for (var i = 0; i < _sourceButtons.Length; i++)
            _sourceButtons[i] = Button((uint)(20 + i), ProviderIcons.Name(ProviderIcons.Sources[i]), 20 + i * 156, 82, 148, 34);
        _intro = Control("STATIC", "", 21, 126, 458, 42);
        _homeLabel = Control("STATIC", "Codex 自定义目录（可留空）", 21, 177, 458, 19);
        _home = Edit(settings.CodexHome ?? "", 20, 200, 458, 29);
        _replace = Button(31, "", 20, 171, 458, 26);
        var idLabel = Control("STATIC", "Access Key ID", 21, 207, 458, 19);
        _access = Edit("", 20, 230, 458, 29);
        var secretLabel = Control("STATIC", "Secret Access Key", 21, 268, 458, 19);
        _secret = Edit("", 20, 291, 458, 29, EditPassword);
        Win32.SendMessage(_secret, 0xCC, 0x25CF, 0);
        var notice = Control("STATIC", "Secret 由当前 Windows 用户加密保存在本机。", 21, 327, 458, 18);
        _keyFields = [idLabel, _access, secretLabel, _secret, notice];

        _intervalLabel = Control("STATIC", "刷新间隔（秒）", 21, 0, 245, 22);
        _interval = Edit(settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture), 386, 0, 92, 28, EditNumber);
        _resetLabel = Control("STATIC", "托盘提示重置时间", 21, 0, 245, 22);
        _reset = Control("COMBOBOX", "", 324, 0, 154, 130, 0x0003 | Win32.TabStop | Win32.VerticalScroll);
        Win32.SendText(_reset, 0x0143, 0, "5 小时");
        Win32.SendText(_reset, 0x0143, 0, "周额度");
        Win32.SendMessage(_reset, 0x014E, settings.TooltipResetWindow == ResetTimeWindow.Week ? 1u : 0u, 0);
        _error = Control("STATIC", "", 21, 0, 456, 20);
        _cancel = Button(2, "取消", 298, 0, 84, 34);
        _save = Button(1, "保存", 394, 0, 84, 34);
        _window.MessageReceived = OnMessage;
        Layout(workArea);
    }

    private readonly nint _intervalLabel, _resetLabel;
    private nint Handle => _window.Handle;

    internal static TraySettings? Edit(nint owner, TraySettings settings)
    {
        using var dialog = new NativeSettings(owner, settings);
        if (owner != 0) Win32.EnableWindow(owner, false);
        try
        {
            dialog._window.Show();
            Win32.SetForegroundWindow(dialog.Handle);
            Win32.SetFocus(dialog._sourceButtons[(int)settings.Source]);
            NativeWindow.Run(dialog.Handle, () => !dialog._finished);
            return dialog._result;
        }
        finally
        {
            if (owner != 0)
            {
                Win32.EnableWindow(owner, true);
                Win32.SetForegroundWindow(owner);
            }
        }
    }

    private nint MakeFont(int height, int weight) =>
        Win32.CreateFont(-P(height), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    private int P(int value) => Win32.Px(value, _dpi);

    private nint Control(string kind, string text, int x, int y, int width, int height, uint style = 0,
        uint exStyle = 0, uint id = 0, nint font = 0)
    {
        var handle = Win32.CreateWindow(exStyle, kind, text, Win32.Visible | Win32.Child | style,
            P(x), P(y), P(width), P(height), Handle, (nint)id, Win32.Instance, 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception();
        Win32.SendMessage(handle, 0x30, (nuint)(font != 0 ? font : _font), 1);
        return handle;
    }

    private nint Edit(string text, int x, int y, int width, int height, uint flags = 0)
    {
        var handle = Control("EDIT", text, x, y, width, height, Win32.TabStop | EditAutoScroll | flags, 0x200);
        Win32.SendMessage(handle, 0xD3, 3, P(6) | (P(6) << 16));
        return handle;
    }

    private nint Button(uint id, string title, int x, int y, int width, int height)
    {
        var handle = Control("BUTTON", title, x, y, width, height, Win32.TabStop | OwnerDrawButton, id: id);
        _buttons[handle] = title;
        return handle;
    }

    private void Move(nint hwnd, int x, int y, int width, int height) =>
        Win32.SetWindowPos(hwnd, 0, P(x), P(y), P(width), P(height), Win32.NoZOrder | Win32.NoActivate);

    private void Layout(Rectangle workArea)
    {
        var volcano = _selected == QuotaSource.VolcengineAgentPlan;
        var codex = _selected == QuotaSource.Codex;
        var showKeys = volcano && _replacing;
        Win32.SetText(_intro, _selected switch
        {
            QuotaSource.VolcengineAgentPlan => "个人版 Agent Plan。使用有方舟额度与用量读取权限的 IAM 密钥。查询账户余额需要 billing:QueryBalanceAcct。",
            QuotaSource.Antigravity => "登录并运行本机 Antigravity 桌面端。额度来自当前客户端，Token 用量来自本机会话记录。",
            _ => "使用这台电脑的 Codex 登录。仅在使用自定义目录时填写路径。"
        });
        Win32.ShowWindow(_home, codex ? 5 : 0);
        Win32.ShowWindow(_homeLabel, codex ? 5 : 0);
        Win32.ShowWindow(_replace, volcano && _initial.HasVolcengineCredentials ? 5 : 0);
        if (_initial.HasVolcengineCredentials)
        {
            var text = _replacing ? "✓ 更换已保存的密钥" : "更换已保存的密钥";
            _buttons[_replace] = text;
            Win32.SetText(_replace, text);
        }
        foreach (var control in _keyFields) Win32.ShowWindow(control, showKeys ? 5 : 0);

        var top = showKeys ? 359 : codex ? 242 : volcano ? 213 : 180;
        Move(_intervalLabel, 21, top + 3, 245, 22);
        Move(_interval, 386, top, 92, 28);
        Move(_resetLabel, 21, top + 42, 245, 22);
        Move(_reset, 324, top + 39, 154, 130);
        Move(_error, 21, top + 79, 456, 20);
        Move(_cancel, 298, top + 104, 84, 34);
        Move(_save, 394, top + 104, 84, 34);

        var outer = new Win32.Rect { Right = P(500), Bottom = P(top + 155) };
        Win32.AdjustWindowRectExForDpi(ref outer, DialogStyle, false, Win32.ExToolWindow, (uint)_dpi);
        var size = outer.Rectangle.Size;
        var center = new Point(workArea.Left + (workArea.Width - size.Width) / 2,
            Math.Max(workArea.Top, workArea.Top + (workArea.Height - size.Height) / 2));
        Win32.SetWindowPos(Handle, Win32.Topmost, center.X, center.Y, size.Width, size.Height, Win32.NoActivate);
        Win32.InvalidateRect(Handle, 0, true);
        foreach (var button in _buttons.Keys) Win32.InvalidateRect(button, 0, false);
    }

    private nint? OnMessage(uint message, nuint parameter, nint data)
    {
        switch (message)
        {
            case Win32.EraseBackground:
                if (Win32.GetClientRect(Handle, out var client))
                    Win32.FillRect((nint)parameter, ref client, _background);
                return 1;
            case Win32.ControlStatic:
                Win32.SetTextColor((nint)parameter, Win32.ColorRef(data == _error ? Color.Salmon : Surface.TextColor));
                Win32.SetBkMode((nint)parameter, 1);
                return _background;
            case Win32.ControlEdit:
            case Win32.ControlList:
                Win32.SetTextColor((nint)parameter, Win32.ColorRef(Surface.TextColor));
                Win32.SetBkColor((nint)parameter, Win32.ColorRef(Field));
                return _fieldBrush;
            case Win32.DrawItem:
                var item = Marshal.PtrToStructure<DrawItem>(data);
                if (!_buttons.TryGetValue(item.ItemWindow, out var caption)) return null;
                DrawButton(item, caption);
                return 1;
            case Win32.Close:
            case Win32.KeyDown when parameter == 27:
                _finished = true;
                return 0;
            case Win32.Command:
                Command(Win32.Low(parameter));
                return 0;
            default:
                return null;
        }
    }

    private void DrawButton(DrawItem item, string text)
    {
        using var g = Graphics.FromHdc(item.Dc);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Back);
        var rect = item.Bounds.Rectangle;
        rect.Inflate(-1, -1);
        var selected = item.Id == (uint)(20 + (int)_selected);
        var primary = item.Id == 1;
        var focused = (item.State & 16) != 0;
        var fill = primary ? Color.FromArgb(0x26, 0x65, 0xB0) : selected ? Color.FromArgb(0x30, 0x42, 0x5A) : Field;
        Surface.Rounded(g, rect, P(7), fill, focused ? Color.FromArgb(0x87, 0x9B, 0xB9) : Surface.Divider);
        Surface.Text(g, text, rect, P(13), Surface.TextColor, selected || primary, StringAlignment.Center);
    }

    private void Command(int id)
    {
        if (id == 2)
        {
            _finished = true;
            return;
        }
        if (id == 1)
        {
            Save();
            return;
        }
        if (id is >= 20 and <= 22) _selected = ProviderIcons.Sources[id - 20];
        else if (id == 31) _replacing = !_replacing;
        else return;
        Win32.SetText(_error, "");
        Layout(Win32.WorkArea(_window.Bounds.Location));
    }

    private void Save()
    {
        if (!int.TryParse(Win32.Text(_interval), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 3 or > 3600)
        {
            Validation("刷新间隔须为 3 至 3600 之间的整数。", _interval);
            return;
        }
        var home = Win32.Text(_home).Trim();
        var settings = _initial with
        {
            Source = _selected, RefreshSeconds = seconds,
            CodexHome = home.Length == 0 ? null : home,
            TooltipResetWindow = Win32.SendMessage(_reset, 0x0147, 0, 0) == 1 ? ResetTimeWindow.Week : ResetTimeWindow.FiveHour
        };
        if (_selected == QuotaSource.VolcengineAgentPlan && _replacing)
        {
            var id = Win32.Text(_access).Trim();
            if (id.Length == 0)
            {
                Validation("请填写火山引擎的 Access Key ID。", _access);
                return;
            }
            var secret = Win32.Text(_secret);
            if (string.IsNullOrWhiteSpace(secret))
            {
                Validation("请填写与密钥 ID 配对的 Secret Access Key。", _secret);
                return;
            }
            settings = settings with { VolcengineAccessKeyId = id, VolcengineSecretAccessKey = secret };
        }
        _result = settings;
        _finished = true;
    }

    private void Validation(string text, nint field)
    {
        Win32.SetText(_error, text);
        Win32.SetFocus(field);
    }

    public void Dispose()
    {
        Win32.SetText(_secret, "");
        _window.Dispose();
        Win32.DeleteObject(_font);
        Win32.DeleteObject(_titleFont);
        Win32.DeleteObject(_background);
        Win32.DeleteObject(_fieldBrush);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawItem
    {
        internal uint Type, Id, ItemId, Action, State;
        internal nint ItemWindow, Dc;
        internal Win32.Rect Bounds;
        internal nuint Data;
    }
}
