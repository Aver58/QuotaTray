using CodexQuotaCore;

namespace CodexQuotaTray;

public partial class AntigravityPanel : System.Windows.Controls.UserControl
{
    public AntigravityPanel() => InitializeComponent();

    public void Apply(CodexQuotaSnapshot snapshot, string status)
    {
        var peak = Math.Max(1, snapshot.LastSevenDays.Select(day => day.TotalTokens).DefaultIfEmpty().Max());
        DataContext = new
        {
            Groups = new[]
            {
                Group("Gemini", snapshot.Primary, snapshot.Secondary),
                Group("Claude + GPT", snapshot.Daily, snapshot.Monthly)
            },
            Days = snapshot.LastSevenDays.Select(day => new
            {
                Date = day.Date.ToString("MM/dd"),
                Tokens = day.TotalTokens.ToString("N0"),
                Fraction = day.TotalTokens / (double)peak
            }).ToArray(),
            Notice = snapshot.TokenHistoryNotice,
            Status = status
        };
    }

    private static object Group(string name, QuotaBucket fiveHour, QuotaBucket week) =>
        new { Name = name, Windows = new[] { Window("5 小时", fiveHour), Window("周额度", week) } };

    private static object Window(string period, QuotaBucket quota) => new
    {
        Period = period,
        Reset = quota.ResetsAt is { } reset ? $"重置 {reset.LocalDateTime:MM/dd HH:mm}" : "重置 --",
        Remaining = quota.IsAvailable ? $"{quota.RemainingPercent:0.##}%" : "--",
        Used = quota.IsAvailable ? $"{quota.UsedPercent:0.##}%" : "--",
        Fraction = quota.IsAvailable ? Math.Clamp(quota.RemainingPercent / 100, 0, 1) : 0
    };
}
