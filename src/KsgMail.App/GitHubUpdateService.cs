using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace KsgMail.App;

public sealed record UpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string Tag,
    string ReleaseUrl,
    string InstallerName,
    string InstallerUrl,
    string ChecksumsUrl)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

public sealed class GitHubUpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Pawel-sp9pw/ksg-mail/releases/latest";
    private readonly HttpClient http;

    public GitHubUpdateService()
    {
        http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KsgMail", CurrentVersion.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version == null
                ? new Version(0, 0, 0)
                : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(LatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("GitHub Release nie zawiera tagu wersji.");
        var versionText = tag.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var latest))
            throw new InvalidOperationException($"Nie można rozpoznać wersji GitHub Release: {tag}.");
        latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));

        var releaseUrl = root.GetProperty("html_url").GetString() ?? "";
        string? installerName = null;
        string? installerUrl = null;
        string? checksumsUrl = null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";

            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                checksumsUrl = url;

            if (name.Equals($"KsgMail-Setup-{versionText}-win-x64.exe", StringComparison.OrdinalIgnoreCase) ||
                (installerUrl == null &&
                 name.StartsWith("KsgMail-Setup-", StringComparison.OrdinalIgnoreCase) &&
                 name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)))
            {
                installerName = name;
                installerUrl = url;
            }
        }

        if (installerName == null || installerUrl == null)
            throw new InvalidOperationException("Najnowszy GitHub Release nie zawiera instalatora KSG Mail dla Windows x64.");
        if (checksumsUrl == null)
            throw new InvalidOperationException("Najnowszy GitHub Release nie zawiera pliku SHA256SUMS.txt.");

        return new UpdateInfo(CurrentVersion, latest, tag, releaseUrl, installerName, installerUrl, checksumsUrl);
    }

    public async Task<string> DownloadVerifiedInstallerAsync(
        UpdateInfo update,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KsgMail", "Updates", update.LatestVersion.ToString(3));
        Directory.CreateDirectory(root);

        var installerPath = Path.Combine(root, update.InstallerName);
        var tempPath = installerPath + ".download";

        progress?.Report("Pobieranie sum kontrolnych aktualizacji…");
        var checksums = await http.GetStringAsync(update.ChecksumsUrl, cancellationToken);
        var expectedHash = ParseExpectedHash(checksums, update.InstallerName);

        progress?.Report($"Pobieranie KSG Mail {update.LatestVersion.ToString(3)}…");
        using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }

        progress?.Report("Weryfikacja SHA-256 instalatora…");
        var actualHash = await ComputeSha256Async(tempPath, cancellationToken);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new InvalidOperationException(
                "Suma SHA-256 pobranego instalatora nie zgadza się z plikiem opublikowanym w GitHub Release.");
        }

        File.Move(tempPath, installerPath, true);
        return installerPath;
    }

    public static void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true
        });
    }

    internal static string ParseExpectedHash(string checksums, string fileName)
    {
        foreach (var line in checksums.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOfAny([' ', '\t']);
            if (split <= 0) continue;

            var hash = trimmed[..split].Trim();
            var name = trimmed[split..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                hash.Length == 64 &&
                hash.All(Uri.IsHexDigit))
                return hash.ToLowerInvariant();
        }

        throw new InvalidOperationException($"Brak sumy SHA-256 dla {fileName}.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
