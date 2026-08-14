using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Serialization;

namespace p2p.Services;

public record UpdateInfo(Version Version, string TagName, string DownloadUrl, string ReleaseUrl);

/// <summary>Проверка и установка обновлений через GitHub Releases: приложение своего сервера
/// не требует — единственный источник новых версий — https://github.com/HeagBoKaT/p2p/releases.
/// Скачивание и подстановка запускаются только по явному клику пользователя в баннере.</summary>
public class UpdateService
{
    private const string RepoApiUrl = "https://api.github.com/repos/HeagBoKaT/p2p/releases/latest";
    private static readonly HttpClient Http = BuildClient();

    public Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static HttpClient BuildClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API отвечает 403 без User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("p2p-messenger", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(RepoApiUrl, ct);
            var release = JsonUtil.Deserialize<GitHubRelease>(json);
            if (release is null || string.IsNullOrEmpty(release.TagName))
                return null;

            var versionText = release.TagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var remoteVersion))
                return null;

            if (remoteVersion <= CurrentVersion)
                return null;

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                return null;

            return new UpdateInfo(remoteVersion, release.TagName, asset.DownloadUrl, release.HtmlUrl);
        }
        catch
        {
            // Нет сети, репозиторий недоступен, лимит GitHub API и т.д. — обновление не критично,
            // молча пробуем в следующий раз.
            return null;
        }
    }

    /// <summary>Скачивает новый exe и готовит замену. Windows не даёт перезаписать работающий
    /// файл, поэтому пишется маленький bat-скрипт: ждёт завершения текущего процесса, подменяет
    /// exe, запускает новую версию и удаляет сам себя. Возвращает управление ПЕРЕД перезапуском —
    /// вызывающая сторона должна закрыть приложение сама (Application.Shutdown), чтобы файл
    /// освободился и скрипт мог его заменить.</summary>
    public async Task ApplyUpdateAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExe))
            throw new InvalidOperationException("Не удалось определить путь к текущему exe.");

        var workDir = Path.Combine(Path.GetTempPath(), "p2p_update");
        Directory.CreateDirectory(workDir);
        var newExePath = Path.Combine(workDir, $"p2p_{info.TagName}.exe");

        await DownloadFileAsync(info.DownloadUrl, newExePath, progress, ct);

        if (new FileInfo(newExePath).Length < 1024 * 1024)
            throw new InvalidOperationException("Скачанный файл подозрительно мал — возможно, обновление повреждено.");

        var scriptPath = Path.Combine(workDir, "apply_update.cmd");
        var pid = Environment.ProcessId;
        var script = $@"@echo off
setlocal
set TARGET=""{currentExe}""
set SOURCE=""{newExePath}""

:wait
tasklist /FI ""PID eq {pid}"" | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)

move /y %SOURCE% %TARGET% >nul
start """" %TARGET%
del ""%~f0""
";
        await File.WriteAllTextAsync(scriptPath, script, ct);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0)
                progress?.Report((double)readTotal / total);
        }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}
