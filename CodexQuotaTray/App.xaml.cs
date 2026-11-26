using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaCore;

namespace CodexQuotaTray;

public partial class App : System.Windows.Application
{
    private static readonly QuotaSource[] Sources = [QuotaSource.Codex, QuotaSource.VolcengineAgentPlan, QuotaSource.Antigravity];
    private static readonly CodexQuotaSnapshot Loading = CodexQuotaSnapshot.Empty("正在读取额度…");
    private static readonly CodexQuotaSnapshot UnconfiguredVolcano = CodexQuotaSnapshot.Empty("点击此行，在设置中填写火山引擎访问密钥。");

    private TraySettings _settings = TraySettings.Load();
    private readonly AgentPlanUsageReader _agentPlanUsageReader = new();
    private readonly VolcanoAccountBalanceReader _accountBalanceReader = new();
    private readonly AntigravityUsageReader _antigravityUsageReader = new();
    private readonly AntigravityTokenHistory _antigravityHistory = new();
    private readonly HashSet<QuotaSource> _reading = [];
    private readonly Dictionary<QuotaSource, CodexQuotaSnapshot> _snapshots = [];
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly CancellationTokenSource _exit = new();
    private ToolStripMenuItem? _checkUpdate;
    private bool _checkingUpdate;
    private bool _editingSettings;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _codexSourceOption;
    private ToolStripMenuItem? _agentPlanSourceOption;
    private ToolStripMenuItem? _antigravitySourceOption;
    private QuotaHud? _hud;
    private QuotaWindow? _window;
    private QuotaSource _detailSource;
    private QuotaHud? _detailAnchor;
    private bool _placingDetails;
    private (int Percent, bool Available)? _trayReading;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _hud = new QuotaHud();
        _hud.DetailsRequested += source => ShowDetails(source, _hud);
        _window = new QuotaWindow();
        _window.RequestHide += (_, _) => _window.Hide();
        _hud.LocationChanged += (_, _) => PlaceAttachedDetails();
        _hud.SizeChanged += (_, _) => PlaceAttachedDetails();
        _hud.Deactivated += (_, _) => QueueDetailDismissal();
        _hud.ContextMenu.Closed += (_, _) => QueueDetailDismissal();
        _hud.IsVisibleChanged += (_, _) =>
        {
            if (!_hud.IsVisible && _detailAnchor == _hud)
            {
                _window.Hide();
            }
        };
        _window.SizeChanged += (_, _) => PlaceAttachedDetails();
        _window.Deactivated += (_, _) => QueueDetailDismissal();
        _window.IsVisibleChanged += (_, _) =>
        {
            if (!_window.IsVisible)
            {
                var anchor = _detailAnchor;
                _detailAnchor = null;
                anchor?.SetDetailsOpen(false);
            }
        };

        _trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = "额度组件",
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.MouseClick += TrayIconOnMouseClick;
        ApplyCachedSnapshots();

        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _timer.Tick += (_, _) => RefreshSnapshots();
        _timer.Start();

        _updateTimer.Tick += (_, _) => CheckForUpdate(manual: false);
        _updateTimer.Start();
        if (e.Args is ["--update-ready", var id] && Guid.TryParseExact(id, "N", out var updateId)
            && EventWaitHandle.TryOpenExisting($"Local\\CQT-boot-{updateId:N}", out var ready))
        {
            using (ready) ready.Set();
        }
        CheckForUpdate(manual: false);
        if (e.Args is ["--show"])
        {
            _hud.IsPinned = true;
            ShowHud();
        }
        else
        {
            RefreshSnapshots();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer.Stop();
        _updateTimer.Stop();
        _exit.Cancel();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }

        base.OnExit(e);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开浮窗", null, (_, _) => ShowHud());
        menu.Items.Add("立即刷新", null, (_, _) => RefreshSnapshots(all: true));
        var displayOptions = new ToolStripMenuItem("托盘图标来源");
        _codexSourceOption = new ToolStripMenuItem("Codex")
        {
            Checked = _settings.Source == QuotaSource.Codex
        };
        _codexSourceOption.Click += (_, _) => SwitchQuotaSource(QuotaSource.Codex);
        _agentPlanSourceOption = new ToolStripMenuItem("火山引擎 · Agent Plan")
        {
            Checked = _settings.Source == QuotaSource.VolcengineAgentPlan
        };
        _agentPlanSourceOption.Click += (_, _) => SwitchQuotaSource(QuotaSource.VolcengineAgentPlan);
        _antigravitySourceOption = new ToolStripMenuItem("Antigravity")
        {
            Checked = _settings.Source == QuotaSource.Antigravity
        };
        _antigravitySourceOption.Click += (_, _) => SwitchQuotaSource(QuotaSource.Antigravity);
        displayOptions.DropDownItems.Add(_codexSourceOption);
        displayOptions.DropDownItems.Add(_agentPlanSourceOption);
        displayOptions.DropDownItems.Add(_antigravitySourceOption);
        menu.Items.Add(displayOptions);
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        _checkUpdate = new ToolStripMenuItem($"检查更新 · v{ReleaseUpdater.Current}", null, (_, _) => CheckForUpdate(manual: true));
        menu.Items.Add(_checkUpdate);
        var automatic = new ToolStripMenuItem("自动更新") { Checked = _settings.AutomaticUpdates };
        automatic.Click += (_, _) =>
        {
            try
            {
                var settings = _settings with { AutomaticUpdates = !_settings.AutomaticUpdates };
                settings.Save();
                _settings = settings;
                automatic.Checked = settings.AutomaticUpdates;
                if (automatic.Checked) CheckForUpdate(manual: false);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "无法保存自动更新设置"); }
        };
        menu.Items.Add(automatic);
        menu.Items.Add("图标来源与许可", null, (_, _) => ShowProviderNotices());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        return menu;
    }

    private static void ShowProviderNotices()
    {
        using var resource = typeof(App).Assembly.GetManifestResourceStream("CodexQuotaTray.ProviderNotices.txt")!;
        using var reader = new StreamReader(resource);
        var text = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            Text = reader.ReadToEnd(),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Padding = new Thickness(12),
            FontSize = 13
        };
        var dialog = new Window
        {
            Title = "QuotaTray 图标来源与许可",
            Content = text,
            Width = 650,
            Height = 500,
            MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        dialog.ShowDialog();
    }

    private async void CheckForUpdate(bool manual)
    {
        if (_checkingUpdate || (!manual && (!_settings.AutomaticUpdates || _editingSettings))) return;
        _checkingUpdate = true;
        var item = _checkUpdate!;
        item.Enabled = false;
        item.Text = "正在检查更新…";
        try
        {
            var release = await ReleaseUpdater.FindNewRelease(_exit.Token);
            if (release == null)
            {
                if (manual) System.Windows.MessageBox.Show($"当前已是最新稳定版 v{ReleaseUpdater.Current}。", "检查更新");
                return;
            }
            if (!manual && File.Exists(ReleaseUpdater.FailureFile) &&
                File.ReadAllText(ReleaseUpdater.FailureFile) == release.Tag)
            {
                item.ToolTipText = $"{release.Tag} 上次安装失败，点击可重试。";
                return;
            }
            item.Text = $"正在下载 {release.Tag}…";
            if (await ReleaseUpdater.Install(release,
                () => !_editingSettings && (manual || _settings.AutomaticUpdates), _exit.Token))
            {
                Shutdown();
            }
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested) { }
        catch (Exception ex)
        {
            item.ToolTipText = ex.Message;
            if (manual && !_exit.IsCancellationRequested) System.Windows.MessageBox.Show(ex.Message, "更新未完成");
        }
        finally
        {
            _checkingUpdate = false;
            if (!_exit.IsCancellationRequested)
            {
                item.Text = $"检查更新 · v{ReleaseUpdater.Current}";
                item.Enabled = true;
            }
        }
    }

    private void TrayIconOnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_hud?.IsVisible == true)
            {
                _hud.Hide();
            }
            else
            {
                ShowHud();
            }
        }
    }

    private void ShowHud()
    {
        if (_hud is null)
        {
            return;
        }

        _window?.Hide();
        var firstShow = !_hud.IsLoaded;
        _hud.Show();
        _hud.RestorePosition(firstShow);
        _hud.Activate();
        RefreshSnapshots();
    }

    private void ShowDetails(QuotaSource source, QuotaHud? anchor = null)
    {
        if (_window is null || !EnsureSourceReady(source))
        {
            return;
        }

        _detailSource = source;
        _detailAnchor = anchor?.IsVisible == true ? anchor : null;
        _window.IsAttached = _detailAnchor is not null;
        _hud?.SetDetailsOpen(_window.IsAttached);
        ApplyDetails();
        var area = SystemParameters.WorkArea;
        if (!_window.IsAttached)
        {
            _window.Fit(area.Width - 24, area.Height - 24);
        }
        if (!_window.IsVisible)
        {
            _window.Opacity = 0;
            _window.Show();
        }
        if (_window.IsAttached)
        {
            PlaceAttachedDetails();
        }
        else
        {
            _window.UpdateLayout();
            _window.Left = Math.Max(area.Left + 12, area.Right - _window.ActualWidth - 24);
            _window.Top = Math.Max(area.Top + 12, area.Bottom - _window.ActualHeight - 24);
        }
        _window.Opacity = 1;
        _window.Activate();
        RefreshSnapshots();
    }

    private void PlaceAttachedDetails()
    {
        if (_placingDetails || _detailAnchor is not { IsVisible: true } anchor || _window is not { IsVisible: true } detail)
        {
            return;
        }

        _placingDetails = true;
        try
        {
            var bounds = anchor.PanelBounds();
            var workArea = Screen.FromRectangle(bounds).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(anchor);
            var gap = (int)Math.Round(5 * dpi.DpiScaleX);
            detail.Fit(HudPlacement.SpaceForDetails(workArea, bounds, anchor.LayoutEdge, gap), dpi);
            detail.UpdateLayout();
            var expectedSize = new System.Drawing.Size(
                (int)Math.Round(detail.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(detail.ActualHeight * dpi.DpiScaleY));
            HudPlacement.Move(detail, HudPlacement.PlaceDetails(workArea, bounds, expectedSize, anchor.LayoutEdge, gap));
            var currentSize = HudPlacement.Bounds(detail).Size;
            if (currentSize != expectedSize)
            {
                HudPlacement.Move(detail, HudPlacement.PlaceDetails(workArea, bounds, currentSize, anchor.LayoutEdge, gap));
            }
        }
        finally
        {
            _placingDetails = false;
        }
    }

    private void QueueDetailDismissal() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_window is { IsVisible: true, IsActive: false } && _detailAnchor?.IsActive != true &&
            _detailAnchor?.ContextMenu?.IsOpen != true)
        {
            _window.Hide();
        }
    });

    private void RefreshSnapshots(bool all = false)
    {
        if (all || _hud?.IsVisible == true)
        {
            foreach (var source in Sources)
            {
                RefreshSource(source);
            }
            return;
        }

        RefreshSource(_settings.Source);
        if (_window?.IsVisible == true)
        {
            RefreshSource(_detailSource);
            if (_detailSource == QuotaSource.VolcengineAgentPlan)
            {
                RefreshSource(QuotaSource.Codex);
            }
        }
    }

    private async void RefreshSource(QuotaSource source)
    {
        var settings = _settings;
        if (_exit.IsCancellationRequested || (source == QuotaSource.VolcengineAgentPlan && !settings.HasVolcengineCredentials)
            || !_reading.Add(source))
        {
            return;
        }

        CodexQuotaSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => ReadSource(source, settings), _exit.Token);
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            snapshot = CodexQuotaSnapshot.Empty($"读取额度失败。{ex.Message}");
        }
        finally
        {
            _reading.Remove(source);
        }

        if (_exit.IsCancellationRequested)
        {
            return;
        }
        if (!SameQuotaInputs(source, settings, _settings))
        {
            RefreshSource(source);
            return;
        }

        _snapshots[source] = snapshot;
        _hud?.ApplySnapshot(source, snapshot);
        if (source == _settings.Source)
        {
            UpdateTrayIcon(snapshot);
        }
        if (_window?.IsVisible == true &&
            (source == _detailSource || (_detailSource == QuotaSource.VolcengineAgentPlan && source == QuotaSource.Codex)))
        {
            ApplyDetails();
        }
    }

    private CodexQuotaSnapshot ReadSource(QuotaSource source, TraySettings settings) =>
        source switch
        {
            QuotaSource.VolcengineAgentPlan => ReadVolcano(settings),
            QuotaSource.Antigravity => _antigravityHistory.AddTo(_antigravityUsageReader.ReadSnapshot()),
            _ => new CodexQuotaReader(settings.CodexHome).ReadSnapshot()
        };

    private CodexQuotaSnapshot ReadVolcano(TraySettings settings)
    {
        var id = settings.VolcengineAccessKeyId!;
        var secret = settings.VolcengineSecretAccessKey!;
        var snapshot = _agentPlanUsageReader.ReadSnapshot(id, secret, Loading);
        try
        {
            return snapshot with { VolcanoBalance = _accountBalanceReader.Read(id, secret) };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
                                   or System.Security.Cryptography.CryptographicException or InvalidOperationException or OperationCanceledException)
        {
            return snapshot with { VolcanoBalanceError = ex.Message };
        }
    }

    private CodexQuotaSnapshot GetSnapshot(QuotaSource source)
    {
        if (source == QuotaSource.VolcengineAgentPlan && !_settings.HasVolcengineCredentials)
        {
            return UnconfiguredVolcano;
        }
        return _snapshots.GetValueOrDefault(source, Loading);
    }

    private void ApplyDetails()
    {
        var snapshot = GetSnapshot(_detailSource);
        if (_detailSource == QuotaSource.VolcengineAgentPlan)
        {
            var codex = GetSnapshot(QuotaSource.Codex);
            snapshot = snapshot with
            {
                TodayTokens = codex.TodayTokens,
                LastSevenDays = codex.LastSevenDays,
                Last24Hours = codex.Last24Hours,
                TokenHistoryNotice = codex.TokenHistoryNotice
            };
        }
        _window?.ApplySnapshot(snapshot, _detailSource);
    }

    private static bool SameQuotaInputs(QuotaSource source, TraySettings before, TraySettings after) =>
        source switch
        {
            QuotaSource.Codex => before.CodexHome == after.CodexHome,
            QuotaSource.VolcengineAgentPlan => before.VolcengineAccessKeyId == after.VolcengineAccessKeyId
                && before.VolcengineSecretAccessKey == after.VolcengineSecretAccessKey,
            _ => true
        };

    private void ApplyCachedSnapshots()
    {
        foreach (var source in Sources)
        {
            _hud?.ApplySnapshot(source, GetSnapshot(source));
        }
        UpdateTrayIcon(GetSnapshot(_settings.Source));
        if (_window?.IsVisible == true)
        {
            ApplyDetails();
        }
    }

    private bool ShowSettings(QuotaSource? selected = null)
    {
        if (_editingSettings)
        {
            return false;
        }
        var settingsWindow = new SettingsWindow(selected.HasValue ? _settings with { Source = selected.Value } : _settings);
        _editingSettings = true;
        bool? saved;
        try { saved = settingsWindow.ShowDialog(); }
        finally { _editingSettings = false; }
        if (saved != true || settingsWindow.SavedSettings is not { } savedSettings)
        {
            return false;
        }

        try
        {
            savedSettings = savedSettings with { AutomaticUpdates = _settings.AutomaticUpdates };
            savedSettings.Save();
            foreach (var source in Sources)
            {
                if (!SameQuotaInputs(source, _settings, savedSettings))
                {
                    _snapshots.Remove(source);
                }
            }

            _settings = savedSettings;
            UpdateSourceMenu();
            _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
            ApplyCachedSnapshots();
            RefreshSnapshots();
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存设置失败。{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool EnsureSourceReady(QuotaSource source) =>
        source != QuotaSource.VolcengineAgentPlan || _settings.HasVolcengineCredentials
        || (ShowSettings(source) && _settings.HasVolcengineCredentials);

    private void SwitchQuotaSource(QuotaSource source)
    {
        if (!EnsureSourceReady(source))
        {
            return;
        }

        try
        {
            if (_settings.Source != source)
            {
                var updatedSettings = _settings with { Source = source };
                updatedSettings.Save();
                _settings = updatedSettings;
                UpdateSourceMenu();
                UpdateTrayIcon(GetSnapshot(source));
            }
            ShowDetails(source);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"切换来源失败。{ex.Message}", "托盘图标来源", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateSourceMenu()
    {
        _codexSourceOption!.Checked = _settings.Source == QuotaSource.Codex;
        _agentPlanSourceOption!.Checked = _settings.Source == QuotaSource.VolcengineAgentPlan;
        _antigravitySourceOption!.Checked = _settings.Source == QuotaSource.Antigravity;
    }

    private void UpdateTrayIcon(CodexQuotaSnapshot snapshot)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var displayBucket = snapshot.Primary.IsAvailable ? snapshot.Primary : snapshot.Secondary;
        var reading = (Percent: (int)Math.Round(displayBucket.RemainingPercent), Available: displayBucket.IsAvailable);
        if (_trayReading != reading)
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = TrayIconFactory.Create(reading.Percent, reading.Available);
            _trayReading = reading;
            oldIcon?.Dispose();
        }

        _trayIcon.Text = BuildTooltip(snapshot, _settings.TooltipResetWindow, _settings.Source);
    }

    private static string BuildTooltip(CodexQuotaSnapshot snapshot, ResetTimeWindow resetTimeWindow, QuotaSource source)
    {
        var sourceName = source switch
        {
            QuotaSource.VolcengineAgentPlan => "火山引擎",
            QuotaSource.Antigravity => "Antigravity",
            _ => "Codex"
        };
        var hasFiveHourReset = snapshot.Primary.IsAvailable && snapshot.Primary.ResetsAt is not null;
        var primary = hasFiveHourReset
            ? $"5小时 {snapshot.Primary.RemainingPercent:0}%" + (resetTimeWindow == ResetTimeWindow.FiveHour
                ? $" 重置 {snapshot.Primary.ResetsAt!.Value.LocalDateTime:HH:mm}"
                : string.Empty)
            : string.Empty;
        var week = snapshot.Secondary.IsAvailable
            ? $"周 {snapshot.Secondary.RemainingPercent:0}%" + (resetTimeWindow == ResetTimeWindow.Week && snapshot.Secondary.ResetsAt is not null
                ? $" 重置 {snapshot.Secondary.ResetsAt.Value.LocalDateTime:MM/dd HH:mm}"
                : string.Empty)
            : string.Empty;
        var resetCards = !hasFiveHourReset && source == QuotaSource.Codex && snapshot.AvailableResetCredits is int credits
            ? $"重置卡 {credits}"
            : string.Empty;
        var details = string.Join(" ", new[] { primary, week, resetCards }.Where(value => value.Length > 0));
        var today = source == QuotaSource.Antigravity && snapshot.LastSevenDays.Count == 0 ? "--" : FormatTokens(snapshot.TodayTokens);
        var text = $"{sourceName} {details} 今日 {today}";
        return text.Length <= 63 ? text : text[..63];
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.#}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}K";
        }

        return tokens.ToString("0");
    }
}
