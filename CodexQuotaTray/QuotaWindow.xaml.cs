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
        QuotaRows.ItemsSource = MakeQuotaRows(snapshot, source);

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
        var tickInterval = volcano ? 6 : 24;
        TokenChartBar[] points = week
            ? snapshot.LastSevenDays.Select(day => new TokenChartBar(
                $"{day.Date:yyyy/MM/dd}", $"{day.Date:MM/dd}", day.TotalTokens)).ToArray()
            : snapshot.Last24Hours.Select((slot, index) =>
            {
                var start = volcano ? slot.Start : slot.Start.ToLocalTime();
                var end = volcano ? slot.End : slot.End.ToLocalTime();
                return new TokenChartBar(FormatPeriod(start, end), index % tickInterval == 0 ? $"{start:HH:mm}" : null, slot.TotalTokens);
            }).ToArray();
        Chart.SetItems(points);
        var visibility = volcano && points.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Chart.Visibility = visibility;
        TokenRangeOptions.Visibility = visibility;
        if (points.Length == 0)
        {
            ChartSummaryText.Text = volcano ? snapshot.TokenHistoryNotice ?? "正在读取火山引擎用量明细…" : "本地会话记录";
            return;
        }

        var interval = week ? "每日一柱" : volcano ? "每小时一柱" : "每 15 分钟一柱";
        var timeZone = volcano ? " · 北京时间" : "";
        var total = FormatTokens(points.Sum(point => (decimal)point.Tokens));
        ChartSummaryText.Text = $"近 {(week ? "7 天" : "24 小时")}合计 {total} Token · {interval}{timeZone}";
    }

    private static string FormatPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        if (start.Offset != end.Offset)
        {
            return $"{start:MM/dd HH:mm zzz} – {end:MM/dd HH:mm zzz}";
        }
        return start.Date == end.Date
            ? $"{start:MM/dd HH:mm} – {end:HH:mm}"
            : $"{start:MM/dd HH:mm} – {end:MM/dd HH:mm}";
    }

    private static IReadOnlyList<QuotaRow> MakeQuotaRows(CodexQuotaSnapshot snapshot, QuotaSource source)
    {
        if (source == QuotaSource.Antigravity)
        {
            return
            [
                QuotaRow.For("Gemini · 5 小时", snapshot.Primary),
                QuotaRow.For("Gemini · 周", snapshot.Secondary),
                QuotaRow.For("Claude + GPT · 5 小时", snapshot.Daily),
                QuotaRow.For("Claude + GPT · 周", snapshot.Monthly)
            ];
        }

        var rows = new List<QuotaRow>(4);
        if (source == QuotaSource.Codex && snapshot.Primary.ResetsAt is null && snapshot.AvailableResetCredits is int cards)
        {
            rows.Add(new QuotaRow("重置卡", "—", $"{cards} 张", "—", 0,
                ProviderAppearance.StatusBrush(null), null));
        }
        else if (snapshot.Primary.IsAvailable)
        {
            rows.Add(QuotaRow.For("5 小时额度", snapshot.Primary));
        }
        if (snapshot.Secondary.IsAvailable || rows.Count == 0)
        {
            rows.Add(QuotaRow.For("周额度", snapshot.Secondary));
        }
        if (source == QuotaSource.VolcengineAgentPlan)
        {
            if (snapshot.Daily.IsAvailable)
            {
                rows.Add(QuotaRow.For("日额度", snapshot.Daily));
            }
            if (snapshot.Monthly.IsAvailable)
            {
                rows.Add(QuotaRow.For("月额度", snapshot.Monthly));
            }
        }
        return rows;
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

    private static string FormatTokens(decimal tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000:0.##}M",
        >= 1_000 => $"{tokens / 1_000:0.#}K",
        _ => $"{tokens:0}"
    };

    private sealed record QuotaRow(
        string Name, string Used, string Remaining, string Reset, double RemainingFraction,
        SolidColorBrush Tone, string? Note)
    {
        internal static QuotaRow For(string name, QuotaBucket bucket)
        {
            var available = bucket.IsAvailable && double.IsFinite(bucket.RemainingPercent) && double.IsFinite(bucket.UsedPercent);
            var fraction = available ? Math.Clamp(bucket.RemainingPercent / 100, 0, 1) : (double?)null;
            var note = bucket.TotalAmount is { } total && bucket.UsedAmount is { } used && bucket.Unit is { } unit
                ? $"剩余 {Math.Max(0, total - used):0.##} / {total:0.##} {unit}" : null;
            return new QuotaRow(name,
                available ? $"{Math.Clamp(bucket.UsedPercent, 0, 100):0.#}%" : "—",
                fraction is { } remaining ? $"{remaining * 100:0.#}%" : "—",
                bucket.ResetsAt is { } reset ? $"{reset.LocalDateTime:MM/dd HH:mm}" : "—",
                fraction.GetValueOrDefault(), ProviderAppearance.StatusBrush(fraction), note);
        }
    }
}
