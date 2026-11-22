using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using CodexQuotaCore;

namespace CodexQuotaTray;

public partial class App : System.Windows.Application
{
    private TraySettings _settings = TraySettings.Load();
    private CodexQuotaReader _codexReader;
    private readonly AgentPlanUsageReader _agentPlanUsageReader = new();
    private readonly VolcanoAccountBalanceReader _accountBalanceReader = new();
    private readonly AntigravityUsageReader _antigravityUsageReader = new();
    private readonly AntigravityTokenHistory _antigravityHistory = new();
    private bool _readingAntigravity;
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
    private QuotaWindow? _window;
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Empty("正在启动…");

    public App()
    {
        _codexReader = new CodexQuotaReader(_settings.CodexHome);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _window = new QuotaWindow();
        _window.RequestHide += (_, _) => _window.Hide();

        _trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = "额度组件",
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.MouseClick += TrayIconOnMouseClick;

        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();

        _updateTimer.Tick += (_, _) => CheckForUpdate(manual: false);
        _updateTimer.Start();
        if (e.Args is ["--update-ready", var id] && Guid.TryParseExact(id, "N", out var updateId)
            && EventWaitHandle.TryOpenExisting($"Local\\CQT-boot-{updateId:N}", out var ready))
        {
            using (ready) ready.Set();
        }
        CheckForUpdate(manual: false);
        RefreshSnapshot();
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
        menu.Items.Add("打开面板", null, (_, _) => ShowPopup());
        menu.Items.Add("立即刷新", null, (_, _) => RefreshSnapshot());
        var displayOptions = new ToolStripMenuItem("显示来源");
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
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        return menu;
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
            if (_window?.IsVisible == true)
            {
                _window.Hide();
            }
            else
            {
                ShowPopup();
            }
        }
    }

    private void ShowPopup()
    {
        if (_window is null)
        {
            return;
        }

        _window.ApplySnapshot(_snapshot, _settings.Source);
        _window.Show();
        _window.Activate();

        var workArea = SystemParameters.WorkArea;
        _window.Left = Math.Max(workArea.Left + 12, workArea.Right - _window.Width - 24);
        _window.Top = Math.Max(workArea.Top + 12, workArea.Bottom - _window.Height - 24);
    }

    private async void RefreshSnapshot()
    {
        try
        {
            if (_settings.Source == QuotaSource.Antigravity)
            {
                if (_readingAntigravity) return;
                _readingAntigravity = true;
                try
                {
                    var result = await System.Threading.Tasks.Task.Run(
                        () => _antigravityHistory.AddTo(_antigravityUsageReader.ReadSnapshot()));
                    if (_settings.Source != QuotaSource.Antigravity) return;
                    _snapshot = result;
                }
                catch (Exception)
                {
                    if (_settings.Source != QuotaSource.Antigravity) return;
                    _snapshot = CodexQuotaSnapshot.Empty("暂时无法获取 Antigravity 用量。");
                }
                finally { _readingAntigravity = false; }
            }
            else
            {
                var localUsage = _codexReader.ReadSnapshot(includeLiveQuota: _settings.Source == QuotaSource.Codex);
                _snapshot = _settings.Source == QuotaSource.VolcengineAgentPlan
                    ? _agentPlanUsageReader.ReadSnapshot(
                        _settings.VolcengineAccessKeyId ?? string.Empty,
                        _settings.VolcengineSecretAccessKey ?? string.Empty,
                        localUsage)
                    : localUsage;
            }
            if (_settings.Source == QuotaSource.VolcengineAgentPlan)
            {
                try
                {
                    _snapshot = _snapshot with
                    {
                        VolcanoBalance = _accountBalanceReader.Read(
                            _settings.VolcengineAccessKeyId ?? string.Empty,
                            _settings.VolcengineSecretAccessKey ?? string.Empty),
                        VolcanoBalanceError = null
                    };
                }
                catch (Exception balanceError) when (balanceError is HttpRequestException or IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
                {
                    _snapshot = _snapshot with { VolcanoBalance = null, VolcanoBalanceError = balanceError.Message };
                }
            }
        }
        catch (Exception ex)
        {
            _snapshot = CodexQuotaSnapshot.Empty(ex.Message);
        }

        if (_window?.IsVisible == true)
        {
            _window.ApplySnapshot(_snapshot, _settings.Source);
        }

        UpdateTrayIcon(_snapshot);
    }

    private void ShowSettings(QuotaSource? selected = null)
    {
        var settingsWindow = new SettingsWindow(selected.HasValue ? _settings with { Source = selected.Value } : _settings);
        _editingSettings = true;
        bool? saved;
        try { saved = settingsWindow.ShowDialog(); }
        finally { _editingSettings = false; }
        if (saved != true || settingsWindow.SavedSettings is not { } savedSettings)
        {
            return;
        }

        try
        {
            savedSettings = savedSettings with { AutomaticUpdates = _settings.AutomaticUpdates };
            savedSettings.Save();
            if (_settings.Source != savedSettings.Source) _snapshot = CodexQuotaSnapshot.Empty("正在读取额度…");
            _settings = savedSettings;
            if (_codexSourceOption is not null)
            {
                _codexSourceOption.Checked = _settings.Source == QuotaSource.Codex;
            }

            if (_agentPlanSourceOption is not null)
            {
                _agentPlanSourceOption.Checked = _settings.Source == QuotaSource.VolcengineAgentPlan;
            }

            if (_antigravitySourceOption is not null)
            {
                _antigravitySourceOption.Checked = _settings.Source == QuotaSource.Antigravity;
            }

            _codexReader = new CodexQuotaReader(_settings.CodexHome);
            _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
            RefreshSnapshot();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存设置失败。{ex.Message}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SwitchQuotaSource(QuotaSource source)
    {
        if (source == QuotaSource.VolcengineAgentPlan &&
            (string.IsNullOrWhiteSpace(_settings.VolcengineAccessKeyId) || string.IsNullOrWhiteSpace(_settings.VolcengineSecretAccessKey)))
        {
            ShowSettings(source);
            if (_settings.Source == source && !string.IsNullOrWhiteSpace(_settings.VolcengineSecretAccessKey)) ShowPopup();
            return;
        }
        if (_settings.Source == source)
        {
            return;
        }

        var updatedSettings = _settings with { Source = source };

        try
        {
            updatedSettings.Save();
            _snapshot = CodexQuotaSnapshot.Empty("正在读取额度…");
            _settings = updatedSettings;
            if (_codexSourceOption is not null)
            {
                _codexSourceOption.Checked = source == QuotaSource.Codex;
            }

            if (_agentPlanSourceOption is not null)
            {
                _agentPlanSourceOption.Checked = source == QuotaSource.VolcengineAgentPlan;
            }

            if (_antigravitySourceOption is not null)
            {
                _antigravitySourceOption.Checked = source == QuotaSource.Antigravity;
            }

            RefreshSnapshot();
            ShowPopup();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"切换来源失败。{ex.Message}", "显示来源", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTrayIcon(CodexQuotaSnapshot snapshot)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var displayBucket = snapshot.Primary.IsAvailable ? snapshot.Primary : snapshot.Secondary;
        var oldIcon = _trayIcon.Icon;
        _trayIcon.Icon = TrayIconFactory.Create(
            (int)Math.Round(displayBucket.RemainingPercent),
            displayBucket.IsAvailable
        );
        oldIcon?.Dispose();

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
