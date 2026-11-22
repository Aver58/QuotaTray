using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodexQuotaCore;

namespace CodexQuotaTray;

public partial class QuotaWindow : Window
{
    private const double BarWidth = 238;

    public QuotaWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? RequestHide;

    public void ApplySnapshot(CodexQuotaSnapshot snapshot)
    {
        var previousHeight = Height;
        var wasVisible = IsVisible;
        var isAgentPlan = snapshot.Status.Contains("Agent Plan", StringComparison.OrdinalIgnoreCase);
        var isAntigravity = snapshot.Status.Contains("Antigravity", StringComparison.OrdinalIgnoreCase);
        var secondaryRemaining = Math.Clamp(snapshot.Secondary.RemainingPercent, 0, 100);
        var primaryRemaining = Math.Clamp(snapshot.Primary.RemainingPercent, 0, 100);
        var dailyRemaining = Math.Clamp(snapshot.Daily.RemainingPercent, 0, 100);
        var monthlyRemaining = Math.Clamp(snapshot.Monthly.RemainingPercent, 0, 100);
        var showResetCards = !snapshot.Primary.IsAvailable && snapshot.AvailableResetCredits.HasValue;

        SourceTitleText.Text = isAgentPlan ? "Agent Plan" : isAntigravity ? "Antigravity" : "Codex";
        Height = isAntigravity ? 410 : isAgentPlan ? 460 : 360;
        if (wasVisible && Math.Abs(previousHeight - Height) > 0.1)
        {
            var workArea = SystemParameters.WorkArea;
            Top = Math.Clamp(Top + previousHeight - Height, workArea.Top + 12, workArea.Bottom - Height - 12);
        }
        ModelUsage.Visibility = isAntigravity ? Visibility.Visible : Visibility.Collapsed;
        StandardQuotaPanel.Visibility = HistoryPanel.Visibility = isAntigravity ? Visibility.Collapsed : Visibility.Visible;
        var status = snapshot.LatestEventAt is null
            ? snapshot.Status
            : $"Updated {snapshot.LatestEventAt.Value.LocalDateTime:MM/dd HH:mm}  {snapshot.Status}";
        if (isAntigravity)
        {
            ModelUsage.Apply(snapshot, status);
            return;
        }
        AgentPlanExtraWindows.Visibility = isAgentPlan ? Visibility.Visible : Visibility.Collapsed;
        DailyQuotaPanel.Visibility = snapshot.Daily.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        MonthlyQuotaPanel.Visibility = snapshot.Monthly.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        VolcanoBalancePanel.Visibility = isAgentPlan ? Visibility.Visible : Visibility.Collapsed;
        AvailableBalanceText.Text = snapshot.VolcanoBalance is { } accountBalance
            ? $"Available balance: {accountBalance.AvailableBalance:N2} {accountBalance.Currency}"
            : "Available balance: unavailable";
        CashBalanceText.Text = snapshot.VolcanoBalance is { } cashBalance
            ? $"Cash balance: {cashBalance.CashBalance:N2} {cashBalance.Currency}"
            : "Cash balance: unavailable";
        BalanceErrorText.Text = snapshot.VolcanoBalanceError ?? string.Empty;
        BalanceErrorText.Visibility = snapshot.VolcanoBalanceError is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryLabelText.Text = showResetCards ? "Cards" : "5h";
        PrimaryPercentText.Text = showResetCards
            ? snapshot.AvailableResetCredits!.Value.ToString(CultureInfo.InvariantCulture)
            : snapshot.Primary.IsAvailable ? $"{primaryRemaining:0}%" : "--";
        PrimaryProgressDetails.Visibility = showResetCards ? Visibility.Collapsed : Visibility.Visible;
        SecondaryPercentText.Text = snapshot.Secondary.IsAvailable ? $"{secondaryRemaining:0}%" : "--";
        DailyPercentText.Text = snapshot.Daily.IsAvailable ? $"{dailyRemaining:0}%" : "--";
        MonthlyPercentText.Text = snapshot.Monthly.IsAvailable ? $"{monthlyRemaining:0}%" : "--";
        PrimaryFill.Width = snapshot.Primary.IsAvailable ? BarWidth * primaryRemaining / 100.0 : 0;
        SecondaryFill.Width = snapshot.Secondary.IsAvailable ? BarWidth * secondaryRemaining / 100.0 : 0;
        DailyFill.Width = snapshot.Daily.IsAvailable ? BarWidth * dailyRemaining / 100.0 : 0;
        MonthlyFill.Width = snapshot.Monthly.IsAvailable ? BarWidth * monthlyRemaining / 100.0 : 0;

        PrimaryResetText.Text = FormatReset(snapshot.Primary);
        SecondaryResetText.Text = FormatReset(snapshot.Secondary);
        DailyResetText.Text = FormatReset(snapshot.Daily);
        MonthlyResetText.Text = FormatReset(snapshot.Monthly);
        TodayTokensText.Text = $"Codex Today {FormatTokens(snapshot.TodayTokens)}";
        Chart.Items = snapshot.LastSevenDays;
        ClockText.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        StatusText.Text = status;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        RequestHide?.Invoke(this, EventArgs.Empty);
    }

    private void Window_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The drag can be canceled if the window loses capture while starting.
        }
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        Hide();
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static string FormatReset(QuotaBucket bucket)
    {
        if (!bucket.IsAvailable)
        {
            return "not provided by selected source";
        }

        var amount = bucket.TotalAmount is double total && bucket.UsedAmount is double used && !string.IsNullOrWhiteSpace(bucket.Unit)
            ? $"{FormatQuotaAmount(Math.Max(0, total - used))}/{FormatQuotaAmount(total)} {bucket.Unit} · "
            : string.Empty;
        if (bucket.ResetsAt is null)
        {
            return $"{amount}reset --";
        }

        return $"{amount}reset {bucket.ResetsAt.Value.LocalDateTime:MM/dd HH:mm}";
    }

    private static string FormatQuotaAmount(double amount) =>
        amount.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.##}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}K";
        }

        return tokens.ToString("0", CultureInfo.InvariantCulture);
    }
}
