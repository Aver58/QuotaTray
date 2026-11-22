using System.Globalization;
using System.Windows;

namespace CodexQuotaTray;

public partial class SettingsWindow : Window
{
    private readonly TraySettings _initialSettings;

    internal SettingsWindow(TraySettings settings)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        _initialSettings = settings;
        CodexSource.IsChecked = settings.Source == QuotaSource.Codex;
        VolcanoSource.IsChecked = settings.Source == QuotaSource.VolcengineAgentPlan;
        AntigravitySource.IsChecked = settings.Source == QuotaSource.Antigravity;
        RefreshSecondsBox.Text = settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TooltipResetWindowCombo.SelectedIndex = settings.TooltipResetWindow == ResetTimeWindow.Week ? 1 : 0;
        CodexHomeBox.Text = settings.CodexHome ?? string.Empty;
        var hasKeys = !string.IsNullOrWhiteSpace(settings.VolcengineAccessKeyId)
            && !string.IsNullOrWhiteSpace(settings.VolcengineSecretAccessKey);
        ReplaceKeys.Visibility = hasKeys ? Visibility.Visible : Visibility.Collapsed;
        ReplaceKeys.IsChecked = !hasKeys;
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, _) => ValidationMessage.Visibility = Visibility.Collapsed));
    }

    internal TraySettings? SavedSettings { get; private set; }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshSecondsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 3 or > 3600)
        {
            ShowValidation("刷新间隔须为 3 至 3600 之间的整数。", RefreshSecondsBox);
            return;
        }

        var source = VolcanoSource.IsChecked == true ? QuotaSource.VolcengineAgentPlan
            : AntigravitySource.IsChecked == true ? QuotaSource.Antigravity : QuotaSource.Codex;
        var settings = _initialSettings with
        {
            Source = source,
            RefreshSeconds = seconds,
            TooltipResetWindow = TooltipResetWindowCombo.SelectedIndex == 1 ? ResetTimeWindow.Week : ResetTimeWindow.FiveHour,
            CodexHome = string.IsNullOrWhiteSpace(CodexHomeBox.Text) ? null : CodexHomeBox.Text.Trim()
        };
        if (source == QuotaSource.VolcengineAgentPlan && ReplaceKeys.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(AccessKeyIdBox.Text))
            {
                ShowValidation("请填写火山引擎的 Access Key ID。", AccessKeyIdBox);
                return;
            }
            if (string.IsNullOrWhiteSpace(SecretAccessKeyBox.Password))
            {
                ShowValidation("请填写与密钥 ID 配对的 Secret Access Key。", SecretAccessKeyBox);
                return;
            }
            settings = settings with
            {
                VolcengineAccessKeyId = AccessKeyIdBox.Text.Trim(),
                VolcengineSecretAccessKey = SecretAccessKeyBox.Password
            };
        }
        SavedSettings = settings;
        DialogResult = true;
    }

    private void ShowValidation(string message, FrameworkElement field)
    {
        ValidationMessage.Text = message;
        ValidationMessage.Visibility = Visibility.Visible;
        field.BringIntoView();
        field.Focus();
    }
}
