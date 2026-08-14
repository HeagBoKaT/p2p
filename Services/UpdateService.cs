using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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

        // cmd.exe разбирает .cmd-файл в системной ANSI/OEM-кодовой странице, а не в UTF-8.
        // Если путь (например, к профилю пользователя) содержит кириллицу, многобайтовые
        // символы ломаются при парсинге и портят всю строку — move получает мусорный путь
        // и молча ничего не делает. Короткие 8.3-имена (C:\Users\ABCDEF~1\...) состоят только
        // из ASCII и этой проблемы не имеют — используем их для всего, что попадает в текст скрипта.
        var target = ToShortPathSafe(currentExe);
        var source = ToShortPathSafe(newExePath);
        var shortWorkDir = ToShortPathSafe(workDir);
        var logPath = Path.Combine(shortWorkDir, "apply_update.log");
        var pid = Environment.ProcessId;

        // Аргументы запуска (--data-dir/--port) нужно передать перезапущенному процессу как есть —
        // иначе релонч тихо откатится на аккаунт и порт по умолчанию вместо тех, с которыми
        // приложение реально было запущено. Кириллица (например, в --data-dir из-под имени
        // пользователя) точно так же ломается парсером cmd.exe, как и пути TARGET/SOURCE/LOG выше,
        // поэтому каждый непустой ASCII аргумент тоже переводится в короткую форму.
        var relaunchArgs = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(SanitizeArgForBatch));

        // Два ретрай-цикла, не один: после того как процесс пропал из tasklist, Windows иногда
        // ещё долю секунды держит файл образа заблокированным (memory-mapped image), и move,
        // выполненный слишком рано, молча проваливается — тогда скрипт просто перезапускал
        // СТАРЫЙ exe, ничего не заменив. Теперь move тоже повторяется, пока файл не станет
        // свободен, с ограничением попыток и логом на случай, если он никогда не освободится.
        var script = $@"@echo off
setlocal
set TARGET=""{target}""
set SOURCE=""{source}""
set LOG=""{logPath}""
set ARGS={relaunchArgs}

:wait
tasklist /FI ""PID eq {pid}"" | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)

set /a TRIES=0
:trymove
set /a TRIES+=1
move /y %SOURCE% %TARGET% >%LOG% 2>&1
if errorlevel 1 (
    if %TRIES% GEQ 15 (
        echo update failed after %TRIES% attempts >>%LOG%
        start """" %TARGET% %ARGS%
        del ""%~f0""
        exit /b 1
    )
    timeout /t 1 /nobreak >nul
    goto trymove
)

start """" %TARGET% %ARGS%
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

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;

    /// <summary>Если аргумент похож на путь (содержит не-ASCII символы и существует на диске —
    /// на момент вызова --data-dir уже точно создан текущим запуском), переводит его в короткое
    /// 8.3-имя по той же причине, что и TARGET/SOURCE/LOG.</summary>
    private static string SanitizeArgForBatch(string arg)
    {
        if (arg.Any(c => c > 127) && Directory.Exists(arg))
            arg = ToShortPathSafe(arg);

        return QuoteArg(arg);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

    /// <summary>Короткое 8.3-имя пути (чистый ASCII) — требуется, чтобы путь пережил разбор
    /// в системной кодовой странице cmd.exe. Работает только для уже существующих на диске
    /// путей; если по какой-то причине короткое имя получить не удалось, возвращает исходный
    /// путь — тогда скрипт может не сработать на нестандартных путях, но это лучше исключения.</summary>
    private static string ToShortPathSafe(string longPath)
    {
        try
        {
            var buffer = new StringBuilder(short.MaxValue);
            var length = GetShortPathName(longPath, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString(0, length) : longPath;
        }
        catch
        {
            return longPath;
        }
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
