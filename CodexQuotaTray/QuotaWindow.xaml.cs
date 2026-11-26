using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CodexQuotaCore;
using DrawingSize = System.Drawing.Size;

namespace CodexQuotaTray;

public partial class QuotaWindow : Window
{
    private const double PreferredWidth = 480;
    private CodexQuotaSnapshot? _snapshot;
    private QuotaSource _source;

    public QuotaWindow()
    {
        InitializeComponent();
    }

    internal bool IsAttached { get; set; }
    public event EventHandler? RequestHide;

    internal void Fit(DrawingSize area, DpiScale dpi)
    {
        Fit(area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    internal void Fit(double width, double height)
    {
        MaxWidth = Math.Max(1, width);
        MaxHeight = Math.Max(1, height);
        Width = Math.Min(PreferredWidth, MaxWidth);
    }

    internal void ApplySnapshot(CodexQuotaSnapshot snapshot, QuotaSource source)
    {
        _snapshot = snapshot;
        _source = source;
        var appearance = ProviderAppearance.For(source);
        SourceTitleText.Text = appearance.Name;
        SourceIcon.Source = appearance.Icon;
        QuotaHeading.Text = source == QuotaSource.VolcengineAgentPlan ? "Agent Plan · AFP" : "额度";
        QuotaRows.ItemsSource = QuotaDetails.Rows(snapshot, source).Select(row => new QuotaRow(
            row.Name, row.Used, row.Remaining, row.Reset, row.Fraction.GetValueOrDefault(),
            ProviderAppearance.StatusBrush(row.Fraction), row.Note)).ToArray();

        var volcano = source == QuotaSource.VolcengineAgentPlan;
        VolcanoBalancePanel.Visibility = volcano ? Visibility.Visible : Visibility.Collapsed;
        if (volcano)
        {
            AvailableBalanceText.Text = snapshot.VolcanoBalance is { } balance
                ? $"可用余额 {balance.AvailableBalance:N2} {balance.Currency}"
                : "可用余额 —";
            CashBalanceText.Text = snapshot.VolcanoBalance is { } cash
                ? $"现金余额 {cash.CashBalance:N2} {cash.Currency}"
                : "现金余额 —";
            BalanceErrorText.Text = snapshot.VolcanoBalanceError;
            BalanceErrorText.ToolTip = snapshot.VolcanoBalanceError;
            BalanceErrorText.Visibility = string.IsNullOrEmpty(snapshot.VolcanoBalanceError)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        ChartTitleText.Text = volcano ? "火山引擎 Token 消耗" : "分时 Token 消耗";
        var historyNotice = snapshot.TokenHistoryNotice;
        var status = string.IsNullOrEmpty(historyNotice) || (volcano && snapshot.LastSevenDays.Count == 0)
            ? snapshot.Status : $"{snapshot.Status} · {historyNotice}";
        StatusText.Text = status;
        StatusText.ToolTip = status;
        UpdatedText.Text = snapshot.LatestEventAt is { } at ? $"更新 {at.LocalDateTime:MM/dd HH:mm}" : "";
        UpdateChart();
    }

    private void Range_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not null)
        {
            Chart.ClearHover();
            UpdateChart();
        }
    }

    private void UpdateChart()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var week = WeekRange.IsChecked == true;
        var volcano = _source == QuotaSource.VolcengineAgentPlan;
        var points = QuotaDetails.Chart(snapshot, week, volcano);
        Chart.SetItems(points);
        var visibility = volcano && points.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Chart.Visibility = visibility;
        TokenRangeOptions.Visibility = visibility;
        ChartSummaryText.Text = QuotaDetails.ChartSummary(snapshot, points, week, volcano);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => RequestHide?.Invoke(this, EventArgs.Empty);

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestHide?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsAttached || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Capture can be lost before Windows starts the drag.
        }
    }

    private sealed record QuotaRow(
        string Name, string Used, string Remaining, string Reset, double RemainingFraction,
        SolidColorBrush Tone, string? Note);
}
