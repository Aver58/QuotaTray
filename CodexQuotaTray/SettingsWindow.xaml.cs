using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CodexQuotaTray;

public partial class SettingsWindow : Window
{
    private readonly TraySettings _initialSettings;

    internal SettingsWindow(TraySettings settings)
    {
        InitializeComponent();
        _initialSettings = settings;
        SourceCombo.SelectedIndex = settings.Source switch
        {
            QuotaSource.VolcengineAgentPlan => 1,
            QuotaSource.Antigravity => 2,
            _ => 0
        };
        RefreshSecondsBox.Text = settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TooltipResetWindowCombo.SelectedIndex = settings.TooltipResetWindow == ResetTimeWindow.Week ? 1 : 0;
        CodexHomeBox.Text = settings.CodexHome ?? string.Empty;
        AccessKeyIdBox.Text = settings.VolcengineAccessKeyId ?? string.Empty;
        SecretAccessKeyBox.Password = settings.VolcengineSecretAccessKey ?? string.Empty;
    }

    internal TraySettings? SavedSettings { get; private set; }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshSecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refreshSeconds))
        {
            System.Windows.MessageBox.Show(this, "Enter a refresh interval between 3 and 3600 seconds.", "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        refreshSeconds = Math.Clamp(refreshSeconds, 3, 3600);
        var source = SourceCombo.SelectedIndex switch
        {
            1 => QuotaSource.VolcengineAgentPlan,
            2 => QuotaSource.Antigravity,
            _ => QuotaSource.Codex
        };
        var accessKeyId = AccessKeyIdBox.Text.Trim();
        var secretAccessKey = SecretAccessKeyBox.Password;
        if (source == QuotaSource.VolcengineAgentPlan &&
            (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey)))
        {
            System.Windows.MessageBox.Show(this, "Enter both the Volcano Engine Access Key ID and Secret Access Key to use Agent Plan.", "Credentials required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SavedSettings = _initialSettings with
        {
            RefreshSeconds = refreshSeconds,
            TooltipResetWindow = TooltipResetWindowCombo.SelectedIndex == 1 ? ResetTimeWindow.Week : ResetTimeWindow.FiveHour,
            CodexHome = string.IsNullOrWhiteSpace(CodexHomeBox.Text) ? null : CodexHomeBox.Text.Trim(),
            Source = source,
            VolcengineAccessKeyId = string.IsNullOrWhiteSpace(accessKeyId) ? null : accessKeyId,
            VolcengineSecretAccessKey = string.IsNullOrEmpty(secretAccessKey) ? null : secretAccessKey
        };
        DialogResult = true;
    }
}
