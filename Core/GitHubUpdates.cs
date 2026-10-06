using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoTone.Core;

public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);
public sealed record AppRelease(Version Version, string Tag, Uri Page, string Notes, ReleaseAsset? Package, ReleaseAsset? Checksums);

public sealed class GitHubUpdates : IDisposable
{
    public const string Repository = "valentine-89/PhotoTone";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public static Version CurrentVersion
    {
        get { var version = Assembly.GetExecutingAssembly().GetName().Version!; return new(version.Major, version.Minor, version.Build); }
    }
    private readonly HttpClient http;
    public GitHubUpdates(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhotoTone/" + CurrentVersion);
    }
    public static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        return new(major, minor, patch);
    }
    public async Task<AppRelease?> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new HttpRequestException("GitHub đang giới hạn lượt kiểm tra. Thử lại sau.");
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBounded(response.Content, 1024 * 1024, timeout.Token);
        return ParseRelease(bytes);
    }
    public static AppRelease? ParseRelease(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? ""; var version = ParseVersion(tag);
        if (version is null) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var assets = new List<ReleaseAsset>();
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("state").GetString() != "uploaded") continue;
            var name = item.GetProperty("name").GetString() ?? "";
            if (name != $"PhotoTone-{version}-win-x64.zip" && name != "SHA256SUMS.txt") continue;
            var url = new Uri(item.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
                || url.AbsolutePath != $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}")
                throw new InvalidDataException("Đường dẫn gói cập nhật không thuộc PhotoTone trên GitHub.");
            var size = item.GetProperty("size").GetInt64();
            var digest = item.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
            var sha = digest is not null && Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ? digest[7..].ToUpperInvariant() : null;
            if (assets.Any(a => a.Name == name)) throw new InvalidDataException("Bản phát hành có tên gói trùng nhau.");
            assets.Add(new(name, url, size, sha));
        }
        return new(version, tag, new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"), notes[..Math.Min(notes.Length, 6000)],
            assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.Ordinal)), assets.FirstOrDefault(a => a.Name == "SHA256SUMS.txt"));
    }
    public async Task DownloadAsync(ReleaseAsset asset, string path, long limit, IProgress<double>? progress, CancellationToken token)
    {
        if (asset.Size <= 0 || asset.Size > limit || asset.Sha256 is null) throw new InvalidDataException("Gói cập nhật thiếu kích thước hoặc SHA-256 hợp lệ.");
        var uri = asset.Url;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Máy chủ tải cập nhật không hợp lệ.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("Thiếu đường dẫn tải cập nhật."); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != asset.Size) throw new InvalidDataException("Kích thước gói tải không khớp release.");
            bool created = false;
            try
            {
                var outputFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); created = true;
                await using (var output = outputFile)
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long total = 0; int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        total += count; if (total > asset.Size || total > limit) throw new InvalidDataException("Gói cập nhật vượt kích thước công bố.");
                        hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
                        progress?.Report(100.0 * total / asset.Size);
                    }
                    if (total != asset.Size || Convert.ToHexString(hash.GetHashAndReset()) != asset.Sha256) throw new InvalidDataException("SHA-256 của gói cập nhật không khớp.");
                }
                return;
            }
            catch { if (created && File.Exists(path)) File.Delete(path); throw; }
        }
        throw new InvalidDataException("Quá nhiều chuyển hướng tải cập nhật.");
    }
    internal static async Task<byte[]> ReadBounded(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Dữ liệu cập nhật vượt giới hạn.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var result = new MemoryStream(); var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > maximum) throw new InvalidDataException("Dữ liệu cập nhật vượt giới hạn.");
            await result.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return result.ToArray();
    }
    public void Dispose() => http.Dispose();
}
