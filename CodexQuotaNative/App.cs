using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using CodexQuotaCore;

namespace CodexQuotaTray;

internal sealed partial class App : IDisposable
{
    private const uint TrayMessage = Win32.App + 1;
    private const uint DispatchMessage = Win32.App + 2;
    private const nuint RefreshTimer = 1, UpdateTimer = 2;
    private readonly NativeWindow _receiver = new("QuotaTray");
    private readonly ProviderIcons _icons = new();
    private readonly NativeHud _hud;
    private readonly NativeDetails _details;
    private readonly CancellationTokenSource _exit = new();
    private readonly MessageContext _context;
    private readonly uint _taskbarCreated = Win32.RegisterMessage("TaskbarCreated");
    private TraySettings _settings = TraySettings.Load();
    private bool _editingSettings, _checkingUpdate, _trayAdded, _stopping;
    private string? _updateStatus;
    private Icon? _trayIcon;
    private (int Percent, bool Available)? _trayReading;

    private App()
    {
        _context = new MessageContext(_receiver.Handle);
        SynchronizationContext.SetSynchronizationContext(_context);
        _receiver.MessageReceived = OnMessage;
        _hud = new NativeHud(_icons, _settings.AlwaysOnTop);
        _details = new NativeDetails(_icons, _settings.AlwaysOnTop);
        _hud.Hidden += _details.Hide;
        _hud.PositionChanged += () => _details.Place();
        _hud.DetailsRequested += source => ShowDetails(source, _hud);
        _hud.TopmostRequested += ToggleTopmost;
        _details.Hidden += () => _hud.SetDetailsOpen(false);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            using var app = new App();
            app.Run(args);
            return 0;
        }
        catch (Exception ex)
        {
            Win32.Error(0, ex.Message, "QuotaTray 无法启动");
            return 1;
        }
    }

    private void Run(string[] args)
    {
        ApplyCachedSnapshots();
        SetRefreshTimer();
        Win32.SetTimer(_receiver.Handle, UpdateTimer, (uint)TimeSpan.FromHours(6).TotalMilliseconds);
        if (args is ["--update-ready", var id] && Guid.TryParseExact(id, "N", out var updateId)
            && EventWaitHandle.TryOpenExisting($"Local\\CQT-boot-{updateId:N}", out var ready))
        {
            using (ready) ready.Set();
        }

        CheckForUpdate(manual: false);
        if (args is ["--show"]) ShowHud();
        else RefreshSnapshots();
        NativeWindow.Run();
    }

    private nint? OnMessage(uint message, nuint parameter, nint data)
    {
        if (message == _taskbarCreated)
        {
            _trayAdded = false;
            UpdateTrayIcon(GetSnapshot(_settings.Source));
            return 0;
        }
        switch (message)
        {
            case TrayMessage when data == Win32.LeftUp:
                if (_hud.IsVisible) _hud.Hide();
                else ShowHud();
                return 0;
            case TrayMessage when data == Win32.RightUp || data == Win32.ContextMenu:
                ShowTrayMenu();
                return 0;
            case DispatchMessage:
                _context.Drain();
                return 0;
            case Win32.Timer when parameter == RefreshTimer:
                RefreshSnapshots();
                return 0;
            case Win32.Timer when parameter == UpdateTimer:
                CheckForUpdate(manual: false);
                return 0;
            case Win32.Close:
                Stop();
                return 0;
            default:
                return null;
        }
    }

    private void ShowHud()
    {
        _details.Hide();
        _hud.Show();
        RefreshSnapshots();
    }

    private void ShowDetails(QuotaSource source, NativeHud? anchor = null)
    {
        if (!EnsureSourceReady(source)) return;
        _hud.SetDetailsOpen(anchor is { IsVisible: true });
        _details.Open(source, DetailsSnapshot(source), anchor);
        RefreshSnapshots();
    }

    private void ShowTrayMenu()
    {
        using var menu = new PopupMenu();
        using var sources = new PopupMenu();
        menu.Add(1, "打开浮窗");
        menu.Add(2, "立即刷新");
        for (var i = 0; i < ProviderIcons.Sources.Length; i++)
        {
            var source = ProviderIcons.Sources[i];
            var label = source == QuotaSource.VolcengineAgentPlan ? "火山引擎 · Agent Plan" : ProviderIcons.Name(source);
            sources.Add((uint)(10 + i), label, _settings.Source == source);
        }
        menu.AddSubmenu("托盘图标来源", sources);
        menu.Add(3, "置顶浮窗", _settings.AlwaysOnTop);
        menu.Add(4, "设置");
        menu.Separator();
        menu.Add(5, _updateStatus ?? $"检查更新 · v{ReleaseUpdater.Current}", enabled: !_checkingUpdate);
        menu.Add(6, "自动更新", _settings.AutomaticUpdates);
        menu.Add(7, "图标来源与许可");
        menu.Add(8, "退出");
        var choice = menu.Show(_receiver.Handle, Win32.Cursor);
        switch (choice)
        {
            case 1: ShowHud(); break;
            case 2: RefreshSnapshots(all: true); break;
            case 3: ToggleTopmost(); break;
            case 4: ShowSettings(); break;
            case 5: CheckForUpdate(manual: true); break;
            case 6:
                if (SaveSettings(_settings with { AutomaticUpdates = !_settings.AutomaticUpdates }, "无法保存自动更新设置")
                    && _settings.AutomaticUpdates)
                    CheckForUpdate(manual: false);
                break;
            case 7: ShowProviderNotices(); break;
            case 8: Stop(); break;
            case >= 10 and <= 12: SwitchQuotaSource(ProviderIcons.Sources[(int)choice - 10]); break;
        }
    }

    private void UpdateTrayIcon(CodexQuotaSnapshot snapshot)
    {
        var displayBucket = snapshot.Primary.IsAvailable ? snapshot.Primary : snapshot.Secondary;
        var reading = (Percent: (int)Math.Round(displayBucket.RemainingPercent), Available: displayBucket.IsAvailable);
        var icon = _trayIcon;
        if (icon is null || _trayReading != reading) icon = TrayIconFactory.Create(reading.Percent, reading.Available);
        var data = TrayData();
        data.Flags = 1 | 2 | 4;
        data.Callback = TrayMessage;
        data.Icon = icon.Handle;
        data.Tip = QuotaText.Tooltip(snapshot, _settings.TooltipResetWindow, _settings.Source);
        if (Win32.NotifyIcon(_trayAdded ? 1u : 0u, ref data))
        {
            if (icon != _trayIcon) _trayIcon?.Dispose();
            (_trayIcon, _trayReading, _trayAdded) = (icon, reading, true);
        }
        else
        {
            _trayAdded = false;
            if (icon != _trayIcon) icon.Dispose();
        }
    }

    private Win32.TrayIcon TrayData() =>
        new() { Size = (uint)Marshal.SizeOf<Win32.TrayIcon>(), Window = _receiver.Handle, Id = 1, Tip = "", Info = "", InfoTitle = "" };

    private void SetRefreshTimer()
    {
        Win32.KillTimer(_receiver.Handle, RefreshTimer);
        Win32.SetTimer(_receiver.Handle, RefreshTimer, (uint)(_settings.RefreshSeconds * 1000));
    }

    private void ToggleTopmost()
    {
        if (SaveSettings(_settings with { AlwaysOnTop = !_settings.AlwaysOnTop }, "无法保存置顶设置"))
        {
            _hud.SetTopmost(_settings.AlwaysOnTop);
            _details.SetTopmost(_settings.AlwaysOnTop);
        }
    }

    private bool SaveSettings(TraySettings settings, string errorTitle)
    {
        try
        {
            settings.Save();
            _settings = settings;
            return true;
        }
        catch (Exception ex)
        {
            Win32.Error(_receiver.Handle, ex.Message, errorTitle);
            return false;
        }
    }

    private bool ShowSettings(QuotaSource? source = null)
    {
        if (_editingSettings) return false;
        _editingSettings = true;
        TraySettings? saved;
        try
        {
            var owner = _details.IsVisible ? _details.Handle : _hud.IsVisible ? _hud.Handle : _receiver.Handle;
            saved = NativeSettings.Edit(owner, source.HasValue ? _settings with { Source = source.Value } : _settings);
        }
        finally
        {
            _editingSettings = false;
        }
        if (saved is null) return false;
        var before = _settings;
        saved = saved with { AutomaticUpdates = before.AutomaticUpdates, AlwaysOnTop = before.AlwaysOnTop };
        if (!SaveSettings(saved, "保存设置失败")) return false;
        foreach (var item in ProviderIcons.Sources)
        {
            if (!SameQuotaInputs(item, before, saved)) _snapshots.Remove(item);
        }
        if (!SameQuotaInputs(QuotaSource.VolcengineAgentPlan, before, saved)) _volcanoTokenHistory = null;
        SetRefreshTimer();
        ApplyCachedSnapshots();
        RefreshSnapshots();
        return true;
    }

    private bool EnsureSourceReady(QuotaSource source) =>
        source != QuotaSource.VolcengineAgentPlan || _settings.HasVolcengineCredentials
        || (ShowSettings(source) && _settings.HasVolcengineCredentials);

    private void SwitchQuotaSource(QuotaSource source)
    {
        if (!EnsureSourceReady(source)) return;
        if (_settings.Source != source && !SaveSettings(_settings with { Source = source }, "切换来源失败")) return;
        UpdateTrayIcon(GetSnapshot(source));
        ShowDetails(source);
    }

    private async void CheckForUpdate(bool manual)
    {
        if (_checkingUpdate || _stopping || (!manual && (!_settings.AutomaticUpdates || _editingSettings))) return;
        _checkingUpdate = true;
        _updateStatus = "正在检查更新…";
        try
        {
            var version = await ReleaseUpdater.FindNewRelease(_exit.Token);
            if (version is null)
            {
                if (manual) Win32.MessageBox(_receiver.Handle, $"当前已是最新稳定版 v{ReleaseUpdater.Current}。", "检查更新", 0x40);
                return;
            }
            if (!manual && File.Exists(ReleaseUpdater.FailureFile) && File.ReadAllText(ReleaseUpdater.FailureFile) == version.Tag)
            {
                _updateStatus = $"检查更新 · {version.Tag} 可重试";
                return;
            }
            _updateStatus = $"正在下载 {version.Tag}…";
            if (await ReleaseUpdater.Install(version, () => !_editingSettings && (manual || _settings.AutomaticUpdates), _exit.Token))
                Stop();
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (manual && !_exit.IsCancellationRequested) Win32.Error(_receiver.Handle, ex.Message, "更新未完成");
        }
        finally
        {
            _checkingUpdate = false;
            if (_updateStatus?.StartsWith("检查更新", StringComparison.Ordinal) != true) _updateStatus = null;
        }
    }

    private void ShowProviderNotices()
    {
        using var data = typeof(App).Assembly.GetManifestResourceStream("CodexQuotaTray.ProviderNotices.txt")!;
        using var text = new StreamReader(data);
        using var dialog = new NativeWindow("QuotaTray 图标来源与许可", Win32.ExToolWindow,
            Win32.Popup | Win32.Caption | Win32.SystemMenu, _receiver.Handle);
        var area = Win32.WorkArea(Win32.Cursor);
        var width = Math.Min(area.Width - 16, 650);
        var height = Math.Min(area.Height - 16, 510);
        Win32.SetWindowPos(dialog.Handle, Win32.Topmost, area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height, Win32.NoActivate);
        Win32.GetClientRect(dialog.Handle, out var client);
        var edit = Win32.CreateWindow(0, "EDIT", text.ReadToEnd().ReplaceLineEndings("\r\n"),
            Win32.Child | Win32.Visible | Win32.VerticalScroll | Win32.TabStop | 0x0804,
            0, 0, client.Right, client.Bottom, dialog.Handle, 0, Win32.Instance, 0);
        var font = Win32.CreateFont(-Win32.Px(13, Win32.Dpi(dialog.Handle)), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        Win32.SendMessage(edit, 0x30, (nuint)font, 1);
        var closed = false;
        dialog.MessageReceived = (message, parameter, _) =>
        {
            if (message != Win32.Close && !(message == Win32.KeyDown && parameter == 27)) return null;
            closed = true;
            return 0;
        };
        dialog.Show();
        try { NativeWindow.Run(dialog.Handle, () => !closed); }
        finally
        {
            dialog.Dispose();
            Win32.DeleteObject(font);
        }
    }

    private void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        Win32.PostQuitMessage(0);
    }

    public void Dispose()
    {
        _exit.Cancel();
        _context.Stop();
        Win32.KillTimer(_receiver.Handle, RefreshTimer);
        Win32.KillTimer(_receiver.Handle, UpdateTimer);
        if (_trayAdded)
        {
            var data = TrayData();
            Win32.NotifyIcon(2, ref data);
        }
        _trayIcon?.Dispose();
        _details.Dispose();
        _hud.Dispose();
        _icons.Dispose();
        _receiver.Dispose();
        _exit.Dispose();
    }

    private sealed class MessageContext(nint window) : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Run, object? State)> _queue = new();
        private volatile bool _stopped;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (_stopped) return;
            _queue.Enqueue((callback, state));
            Win32.PostMessage(window, DispatchMessage);
        }
        internal void Drain()
        {
            while (_queue.TryDequeue(out var work)) work.Run(work.State);
        }
        internal void Stop() => _stopped = true;
    }
}
