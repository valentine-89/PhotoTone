using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using PhotoTone.Core;

namespace PhotoTone.Tests;

internal static class UpdateTests
{
    private static void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
    internal static byte[] ReleaseJson(string version = "1.2.0") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        tag_name = "v" + version, draft = false, prerelease = false, body = "Bản mới",
        assets = new[] {
            new { name = $"PhotoTone-{version}-win-x64.zip", browser_download_url = $"https://github.com/valentine-89/PhotoTone/releases/download/v{version}/PhotoTone-{version}-win-x64.zip", state = "uploaded", size = 100, digest = "sha256:" + new string('a', 64) },
            new { name = "SHA256SUMS.txt", browser_download_url = $"https://github.com/valentine-89/PhotoTone/releases/download/v{version}/SHA256SUMS.txt", state = "uploaded", size = 176, digest = "sha256:" + new string('b', 64) }
        }
    });
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check)
    {
        await check("Update versions compare numerically and reject prerelease/invalid tags", () =>
        {
            Assert(GitHubUpdates.ParseVersion("v1.10.0") > GitHubUpdates.ParseVersion("v1.9.9"), "Lexical version comparison");
            Assert(GitHubUpdates.ParseVersion("1.2.0+build.1") == new Version(1, 2, 0), "Build metadata affected version");
            foreach (var tag in new[] { "v1.2.0-beta", "1.2", "v01.2.3", "../1.2.3", "v999999999999.0.0" }) Assert(GitHubUpdates.ParseVersion(tag) is null, "Accepted " + tag);
            return Task.CompletedTask;
        });
        await check("Update checker uses public GitHub endpoint with no API credentials or asset downloads", async () =>
        {
            var handler = new Handler(request =>
            {
                Assert(request.RequestUri?.AbsoluteUri == GitHubUpdates.LatestUrl, "Unexpected endpoint/download");
                Assert(request.Headers.Authorization is null && request.Headers.UserAgent.Count > 0, "Credential leak or missing User-Agent");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ReleaseJson()) };
            });
            using var client = new GitHubUpdates(handler); var result = await client.CheckAsync(default);
            Assert(handler.Calls == 1 && result?.Version == new Version(1, 2, 0) && result.Package?.Sha256 == new string('A', 64), "Release not parsed");
        });
        await check("Update checker ignores drafts and rejects cross-repository package URLs", () =>
        {
            foreach (var flag in new[] { "draft", "prerelease" })
            {
                var json = JsonNode.Parse(ReleaseJson())!; json[flag] = true;
                Assert(GitHubUpdates.ParseRelease(JsonSerializer.SerializeToUtf8Bytes(json)) is null, "Offered nonstable release");
            }
            var altered = JsonNode.Parse(ReleaseJson())!; altered["assets"]![0]!["browser_download_url"] = "https://github.com/other/repo/releases/download/v1.2.0/PhotoTone-1.2.0-win-x64.zip";
            try { GitHubUpdates.ParseRelease(JsonSerializer.SerializeToUtf8Bytes(altered)); throw new Exception("Accepted untrusted asset URL"); }
            catch (System.IO.InvalidDataException) { }
            return Task.CompletedTask;
        });
        await check("Update 404, rate limit, cancellation and oversized metadata do not retry", async () =>
        {
            foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
            {
                var handler = new Handler(_ => new(code)); using var client = new GitHubUpdates(handler);
                if (code == HttpStatusCode.NotFound) Assert(await client.CheckAsync(default) is null, "Missing release treated as available");
                else { try { await client.CheckAsync(default); throw new Exception("Rate limit not reported"); } catch (HttpRequestException) { } }
                Assert(handler.Calls == 1, "Retry attempted");
            }
            var large = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024 * 1024 + 1]) });
            using (var client = new GitHubUpdates(large)) { try { await client.CheckAsync(default); throw new Exception("Unbounded metadata"); } catch (System.IO.InvalidDataException) { } }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            using var cancelled = new GitHubUpdates(new Handler(_ => new(HttpStatusCode.OK)));
            try { await cancelled.CheckAsync(cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        });
        await check("Update download verifies size/SHA, follows only GitHub HTTPS and preserves existing files", async () =>
        {
            var data = new byte[] { 1, 2, 3, 4 }; var hash = Convert.ToHexString(SHA256.HashData(data));
            var asset = new ReleaseAsset("x.zip", new("https://github.com/valentine-89/PhotoTone/releases/download/v1.2.0/x.zip"), data.Length, hash);
            var path = Path.Combine(root, "download.zip");
            using (var client = new GitHubUpdates(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })))
            {
                await client.DownloadAsync(asset, path, 100, null, default);
                Assert(File.ReadAllBytes(path).SequenceEqual(data), "Download changed bytes");
                try { await client.DownloadAsync(asset, path, 100, null, default); throw new Exception("Overwrote existing file"); } catch (IOException) { }
                Assert(File.ReadAllBytes(path).SequenceEqual(data), "Existing file was deleted"); File.Delete(path);
                try { await client.DownloadAsync(asset with { Sha256 = new string('F', 64) }, path, 100, null, default); throw new Exception("Accepted wrong hash"); } catch (InvalidDataException) { }
                Assert(!File.Exists(path), "Failed download retained partial file");
                foreach (var size in new[] { 3, 5 })
                    try { await client.DownloadAsync(asset with { Size = size }, path, 100, null, default); throw new Exception("Accepted incorrect size"); } catch (InvalidDataException) { }
                using var cancellation = new CancellationTokenSource();
                try { await client.DownloadAsync(asset, path, 100, new InlineProgress(_ => cancellation.Cancel()), cancellation.Token); throw new Exception("Download cancellation ignored"); } catch (OperationCanceledException) { }
                Assert(!File.Exists(path), "Cancelled download retained partial file");
            }
            var redirect = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://untrusted.invalid/app.exe"); return response; });
            using (var client = new GitHubUpdates(redirect))
                try { await client.DownloadAsync(asset, path, 100, null, default); throw new Exception("Followed untrusted redirect"); } catch (InvalidDataException) { }
            Assert(redirect.Calls == 1 && !File.Exists(path), "Unexpected redirect request");
        });
        await check("Update ZIP rejects traversal, missing/wrong checksums and incorrect executable version", () =>
        {
            var zip = Path.Combine(root, "update.zip"); var candidate = Path.Combine(root, "candidate.exe");
            var apphost = Path.Combine(AppContext.BaseDirectory, "PhotoTone.exe"); var packageName = "PhotoTone-" + GitHubUpdates.CurrentVersion + "-win-x64.zip";
            void Zip(string entry)
            {
                if (File.Exists(zip)) File.Delete(zip);
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create); archive.CreateEntryFromFile(apphost, entry, CompressionLevel.NoCompression);
            }
            string Checksums() => UpdateInstaller.Hash(zip) + "  " + packageName + "\n" + UpdateInstaller.Hash(apphost) + "  PhotoTone.exe\n";
            Zip("../PhotoTone.exe");
            try { UpdateInstaller.ExtractVerified(zip, Checksums(), packageName, GitHubUpdates.CurrentVersion, candidate); throw new Exception("Accepted ZIP traversal"); } catch (InvalidDataException) { }
            Assert(!File.Exists(candidate), "Traversal created file");
            Zip("PhotoTone.exe");
            try { UpdateInstaller.ExtractVerified(zip, "", packageName, GitHubUpdates.CurrentVersion, candidate); throw new Exception("Accepted missing checksums"); } catch (InvalidDataException) { }
            try { UpdateInstaller.ExtractVerified(zip, Checksums(), packageName, new Version(999, 0, 0), candidate); throw new Exception("Wrong version accepted"); } catch (InvalidDataException) { }
            if (File.Exists(candidate)) File.Delete(candidate);
            var hash = UpdateInstaller.ExtractVerified(zip, Checksums(), packageName, GitHubUpdates.CurrentVersion, candidate);
            Assert(hash == UpdateInstaller.Hash(apphost), "EXE extraction changed content");
            return Task.CompletedTask;
        });
        await check("Update replacement is atomic, rolls back restart failure and leaves locked app unchanged", () =>
        {
            var folder = Path.Combine(root, "transaction"); Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "PhotoTone.exe"); var candidate = Path.Combine(folder, "candidate.exe");
            byte[] old = [1, 2, 3], next = [4, 5, 6]; File.WriteAllBytes(candidate, next);
            var oldHash = Convert.ToHexString(SHA256.HashData(old)); var newHash = UpdateInstaller.Hash(candidate);
            File.WriteAllBytes(target, old);
            UpdateInstaller.ApplyTransaction(target, candidate, oldHash, newHash, folder, path => Assert(File.ReadAllBytes(path).SequenceEqual(next), "Restarted stale app"));
            Assert(File.ReadAllBytes(target).SequenceEqual(next), "Successful update not installed");
            File.WriteAllBytes(target, old);
            try { UpdateInstaller.ApplyTransaction(target, candidate, oldHash, newHash, folder, _ => throw new IOException("Mock launch failure")); throw new Exception("Failure ignored"); } catch (IOException) { }
            Assert(File.ReadAllBytes(target).SequenceEqual(old), "Rollback lost original app");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                try { UpdateInstaller.ApplyTransaction(target, candidate, oldHash, newHash, folder, _ => throw new Exception("Restarted locked file")); throw new Exception("Replaced locked file"); } catch (IOException) { }
            Assert(File.ReadAllBytes(target).SequenceEqual(old) && Directory.GetFiles(folder, ".phototone-*").Length == 0, "Locked update damaged original/left backup");
            return Task.CompletedTask;
        });
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(response(request)); }
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
}
