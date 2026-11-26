using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray;

internal enum QuotaSource
{
    Codex,
    VolcengineAgentPlan,
    Antigravity
}

internal enum ResetTimeWindow
{
    FiveHour,
    Week
}

internal sealed record TraySettings(
    int RefreshSeconds,
    string? CodexHome,
    QuotaSource Source,
    string? VolcengineAccessKeyId,
    string? VolcengineSecretAccessKey,
    ResetTimeWindow TooltipResetWindow,
    bool AutomaticUpdates = true,
    bool AlwaysOnTop = true)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool HasVolcengineCredentials =>
        !string.IsNullOrWhiteSpace(VolcengineAccessKeyId) && !string.IsNullOrWhiteSpace(VolcengineSecretAccessKey);

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTray",
        "settings.json");

    public static TraySettings Load()
    {
        var settingsPath = SettingsPath;
        bool userProfile = File.Exists(settingsPath);
        if (!userProfile)
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "CodexQuotaTray.settings.json");
            if (!File.Exists(legacyPath))
            {
                return Defaults;
            }

            settingsPath = legacyPath;
        }

        try
        {
            using var stream = File.OpenRead(settingsPath);
            var dto = JsonSerializer.Deserialize<SettingsFile>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var source = Enum.TryParse<QuotaSource>(dto?.Source, true, out var parsedSource) && Enum.IsDefined(parsedSource)
                ? parsedSource
                : QuotaSource.Codex;
            var tooltipResetWindow = Enum.TryParse<ResetTimeWindow>(dto?.TooltipResetWindow, true, out var parsedResetWindow)
                ? parsedResetWindow
                : ResetTimeWindow.Week;
            return new TraySettings(
                Math.Clamp(dto?.RefreshSeconds ?? 15, 3, 3600),
                string.IsNullOrWhiteSpace(dto?.CodexHome) ? null : dto.CodexHome,
                source,
                userProfile ? dto?.VolcengineAccessKeyId : null,
                userProfile ? Unprotect(dto?.ProtectedVolcengineSecret) : null,
                tooltipResetWindow,
                dto?.AutomaticUpdates ?? true,
                dto?.AlwaysOnTop ?? true);
        }
        catch
        {
            return Defaults;
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var dto = new SettingsFile
        {
            RefreshSeconds = Math.Clamp(RefreshSeconds, 3, 3600),
            CodexHome = CodexHome,
            Source = Source.ToString(),
            AutomaticUpdates = AutomaticUpdates,
            AlwaysOnTop = AlwaysOnTop,
            TooltipResetWindow = TooltipResetWindow.ToString(),
            VolcengineAccessKeyId = VolcengineAccessKeyId,
            ProtectedVolcengineSecret = Protect(VolcengineSecretAccessKey)
        };

        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(dto, JsonOptions), Encoding.UTF8);
        File.Move(temporaryPath, SettingsPath, true);
    }

    private static TraySettings Defaults => new(15, null, QuotaSource.Codex, null, null, ResetTimeWindow.Week);

    private static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(Transform(secretBytes, protect: true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return null;
        }

        try
        {
            var secretBytes = Transform(Convert.FromBase64String(protectedSecret), protect: false);
            try
            {
                return Encoding.UTF8.GetString(secretBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPointer, input.Length);
        var inputBlob = new DataBlob(input.Length, inputPointer);
        DataBlob outputBlob = default;

        try
        {
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
            if (!success)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            var output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, inputPointer, input.Length);
            Marshal.FreeHGlobal(inputPointer);
            if (outputBlob.Data != IntPtr.Zero)
            {
                LocalFree(outputBlob.Data);
            }
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int length, IntPtr data)
    {
        public int Length = length;
        public IntPtr Data = data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class SettingsFile
    {
        public int? RefreshSeconds { get; set; }
        public string? CodexHome { get; set; }
        public string? Source { get; set; }
        public bool? AutomaticUpdates { get; set; }
        public bool? AlwaysOnTop { get; set; }
        public string? TooltipResetWindow { get; set; }
        public string? VolcengineAccessKeyId { get; set; }
        public string? ProtectedVolcengineSecret { get; set; }
    }
}
