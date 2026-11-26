using CodexQuotaCore;

namespace CodexQuotaTray;

internal sealed partial class App
{
    private static readonly CodexQuotaSnapshot Loading = CodexQuotaSnapshot.Empty("正在读取额度…");
    private static readonly CodexQuotaSnapshot UnconfiguredVolcano = CodexQuotaSnapshot.Empty("点击此行，在设置中填写火山引擎访问密钥。");
    private readonly AgentPlanUsageReader _agentPlanUsageReader = new();
    private readonly VolcanoAccountBalanceReader _accountBalanceReader = new();
    private readonly AntigravityUsageReader _antigravityUsageReader = new();
    private readonly AntigravityTokenHistory _antigravityHistory = new();
    private readonly HashSet<QuotaSource> _reading = [];
    private readonly Dictionary<QuotaSource, CodexQuotaSnapshot> _snapshots = [];
    private AgentPlanTokenHistory? _volcanoTokenHistory;
    private bool _readingVolcanoTokenHistory;

    private void RefreshSnapshots(bool all = false)
    {
        RefreshVolcanoTokenHistory(force: all);
        if (all || _hud.IsVisible)
        {
            foreach (var source in ProviderIcons.Sources)
            {
                RefreshSource(source);
            }
            return;
        }

        RefreshSource(_settings.Source);
        if (_details.IsVisible)
        {
            RefreshSource(_details.Source);
        }
    }

    private async void RefreshVolcanoTokenHistory(bool force = false)
    {
        var settings = _settings;
        if (!_details.IsVisible || _details.Source != QuotaSource.VolcengineAgentPlan ||
            !settings.HasVolcengineCredentials || _readingVolcanoTokenHistory || _exit.IsCancellationRequested ||
            (!force && _volcanoTokenHistory is { } saved && saved.RetrievedAt > DateTimeOffset.UtcNow.AddMinutes(-5)))
        {
            return;
        }

        AgentPlanTokenHistory history;
        _readingVolcanoTokenHistory = true;
        try
        {
            history = await Task.Run(() => _agentPlanUsageReader.ReadTokenHistory(
                settings.VolcengineAccessKeyId!, settings.VolcengineSecretAccessKey!), _exit.Token);
        }
        catch (OperationCanceledException) when (_exit.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _readingVolcanoTokenHistory = false;
        }

        if (_exit.IsCancellationRequested)
        {
            return;
        }
        if (!SameQuotaInputs(QuotaSource.VolcengineAgentPlan, settings, _settings))
        {
            RefreshVolcanoTokenHistory();
            return;
        }

        _volcanoTokenHistory = history;
        if (_details.IsVisible && _details.Source == QuotaSource.VolcengineAgentPlan)
        {
            _details.ApplySnapshot(DetailsSnapshot(_details.Source));
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
        _hud.ApplySnapshot(source, snapshot);
        if (source == _settings.Source)
        {
            UpdateTrayIcon(snapshot);
        }
        if (_details.IsVisible && source == _details.Source)
        {
            _details.ApplySnapshot(DetailsSnapshot(_details.Source));
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
        var snapshot = _agentPlanUsageReader.ReadSnapshot(id, secret);
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

    private CodexQuotaSnapshot DetailsSnapshot(QuotaSource source)
    {
        var snapshot = GetSnapshot(source);
        if (source == QuotaSource.VolcengineAgentPlan && _volcanoTokenHistory is { } history)
        {
            return snapshot with
            {
                LastSevenDays = history.LastSevenDays,
                Last24Hours = history.Last24Hours,
                TokenHistoryNotice = history.Notice
            };
        }
        return snapshot;
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
        foreach (var source in ProviderIcons.Sources)
        {
            _hud.ApplySnapshot(source, GetSnapshot(source));
        }
        UpdateTrayIcon(GetSnapshot(_settings.Source));
        if (_details.IsVisible)
        {
            _details.ApplySnapshot(DetailsSnapshot(_details.Source));
        }
    }
}
