using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexQuotaTray;

internal sealed record ReleasePackage(Version Version, string Tag, string AssetName, string Digest, long Size);

internal static class ReleaseUpdater
{
    private static readonly HttpClient Feed = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false
    })
    {
        BaseAddress = new Uri("https://codex-quota-tray-aver58.pages.dev/"),
        MaxResponseContentBufferSize = 16384,
        Timeout = TimeSpan.FromSeconds(45)
    };
    private const string ExeName = "CodexQuotaTray.exe";
    internal static readonly Version Current = Version.Parse(typeof(App).Assembly.GetName().Version!.ToString(3));
    internal static string FailureFile => Path.Combine(Path.GetDirectoryName(TraySettings.SettingsPath)!, "last-update-failure.txt");

    public static async Task<ReleasePackage?> FindNewRelease(CancellationToken token)
    {
        using var response = await Feed.GetAsync("latest.json", token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidDataException("更新站暂未发布稳定版。");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var release = document.RootElement;
        var tag = release.GetProperty("tag").GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 ||
            version.Revision >= 0 || tag != $"v{version}")
            throw new InvalidDataException("Release 标签需要使用 v主版本.次版本.修订号。");
        if (version <= Current) return null;

        var name = $"CodexQuotaTray-{tag}-win-x64.zip";
        var digest = release.GetProperty("sha256").GetString();
        var size = release.GetProperty("size").GetInt64();
        if (release.GetProperty("asset").GetString() != name || digest is not { Length: 64 } ||
            !digest.All(Uri.IsHexDigit) || size is <= 0 or > 536870912)
            throw new InvalidDataException("更新清单中的文件名、大小或 SHA-256 无效。");
        return new(version, tag, name, digest, size);
    }

    // Returns true only after the helper has opened a handle to this process and is waiting for it to exit.
    public static async Task<bool> Install(ReleasePackage release, Func<bool> canRestart, CancellationToken token)
    {
        var target = Environment.ProcessPath!;
        var folder = Path.GetDirectoryName(target)!;
        if (!string.Equals(Path.GetFileName(target), ExeName, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(folder, "CodexQuotaTray.dll")))
            throw new InvalidOperationException("请从 Windows x64 便携版运行自动更新。");
        var id = Guid.NewGuid().ToString("N");
        var stage = Directory.CreateDirectory(Path.Combine(folder, ".codex-update-" + id)).FullName;
        var zip = Path.Combine(stage, "package.zip");
        var payload = Path.Combine(stage, ExeName);
        var handedOff = false;
        try
        {
            await DownloadPackage(release, zip, token);
            await Task.Run(() => ExtractVerified(zip, payload, release), token);
            File.Delete(zip);
            token.ThrowIfCancellationRequested();
            if (!canRestart()) return false;

            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
            using var script = new StreamReader(typeof(App).Assembly.GetManifestResourceStream("CodexQuotaTray.ApplyUpdate.ps1")!);
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script.ReadToEnd() }) start.ArgumentList.Add(arg);
            start.Environment["CQT_UPDATE_ID"] = id;
            start.Environment["CQT_UPDATE_PID"] = Environment.ProcessId.ToString();
            start.Environment["CQT_UPDATE_TAG"] = release.Tag;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, $"Local\\CQT-helper-{id}");
            using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
            handedOff = await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(15)));
            if (!handedOff) throw new IOException("更新助手未就绪，当前版本继续运行。");
            return true;
        }
        finally
        {
            if (!handedOff)
            {
                File.Delete(zip);
                File.Delete(payload);
                Directory.Delete(stage);
            }
        }
    }

    private static void ExtractVerified(string zip, string payload, ReleasePackage release)
    {
        using var stream = File.OpenRead(zip);
        if (stream.Length != release.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载包校验失败，已取消更新。");
        stream.Position = 0;
        using var archive = new ZipArchive(stream);
        var executable = archive.Entries.SingleOrDefault(entry => entry.FullName == ExeName);
        if (executable is null || executable.Length is <= 0 or > 536870912)
            throw new InvalidDataException("更新包内的程序无效。");
        executable.ExtractToFile(payload);
        var info = FileVersionInfo.GetVersionInfo(payload);
        if (info.ProductName != "CodexQuotaTray" || new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != release.Version)
            throw new InvalidDataException("程序版本与 Release 标签不一致。");
    }

    private static async Task DownloadPackage(ReleasePackage release, string destination, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await Feed.GetAsync("downloads/" + release.AssetName,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("更新包大小与清单不符。");
        using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Position + read > release.Size) throw new InvalidDataException("更新包超出清单大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
    }
}
