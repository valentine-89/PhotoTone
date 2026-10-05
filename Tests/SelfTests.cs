using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoTone.Core;

namespace PhotoTone.Tests;

internal static class SelfTests
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var checks = new List<object>();
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true, detail = "" }); }
            catch (Exception ex) { checks.Add(new { name, passed = false, detail = ex.ToString() }); }
        }
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action, string text)
        {
            try { action(); } catch (Exception ex) when (ex.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) { return; }
            throw new Exception("Expected rejection: " + text);
        }
        using var parametersDoc = JsonDocument.Parse("""{"resolution":{"type":"enum","values":["2K","4K"]},"aspect_ratio":{"type":"enum","values":["1:1","3:2","2:3"]},"input_references":{"type":"range","min":0,"max":14}}""");
        var model = new ImageModel("test/image", "Mock image model", parametersDoc.RootElement.Clone());
        byte[] asset;
        using (var resource = AppFiles.Resource("raw2.jpg")) { using var buffer = new MemoryStream(); resource.CopyTo(buffer); asset = buffer.ToArray(); }
        var sourcePath = Path.Combine(root, "source.jpg"); File.WriteAllBytes(sourcePath, asset);
        var reference = Path.Combine(root, "reference.jpg");
        using (var resource = AppFiles.Resource("ok.jpg")) using (var file = File.Create(reference)) resource.CopyTo(file);
        var options = new BatchOptions("https://unit-test.invalid/api/v1", "test-secret-not-real", model, "4K", 2, "JPEG", reference, AppFiles.DefaultPrompt, root);

        await Check("Embedded originals: all 2560x1709, default prompt present", () =>
        {
            foreach (var name in new[] { "raw.jpg", "raw1.jpg", "raw2.jpg", "ok.jpg" })
            {
                using var resource = AppFiles.Resource(name); using var buffer = new MemoryStream(); resource.CopyTo(buffer);
                var image = ImageFiles.Decode(buffer.ToArray()); Assert(image.PixelWidth == 2560 && image.PixelHeight == 1709, name);
            }
            Assert(AppFiles.DefaultPrompt.Contains("Không giảm kích thước"), "Missing resolution instruction"); return Task.CompletedTask;
        });
        await Check("Reject smaller dimensions, fewer pixels and aspect mismatch", () =>
        {
            Reject(() => ImageFiles.ValidateSize(new(2560, 1709), new(1534, 1025)), "Không phóng lớn");
            Reject(() => ImageFiles.ValidateSize(new(2560, 1709), new(4000, 1500)), "Không phóng lớn");
            Reject(() => ImageFiles.ValidateSize(new(2560, 1709), new(4096, 4096)), "Sai tỷ lệ");
            ImageFiles.ValidateSize(new(2560, 1709), new(4096, 2736)); return Task.CompletedTask;
        });
        await Check("Request uses 4K, two full-size references and disables fallback", () =>
        {
            var url = ImageFiles.DataUrl(sourcePath);
            var bytes = Convert.FromBase64String(url.Split(',')[1]); var upload = ImageFiles.Decode(bytes);
            Assert(upload.PixelWidth == 2560 && upload.PixelHeight == 1709, "Upload downsampled");
            var payload = JsonSerializer.Serialize(OpenRouterClient.RequestBody(options, new(2560, 1709), url, "data:image/png;base64,REFERENCE"));
            using var doc = JsonDocument.Parse(payload); var body = doc.RootElement;
            Assert(body.GetProperty("resolution").GetString() == "4K", "Wrong tier");
            Assert(body.GetProperty("aspect_ratio").GetString() == "3:2", "Wrong aspect");
            Assert(body.GetProperty("input_references").GetArrayLength() == 2, "Missing references");
            Assert(!body.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean(), "Fallback enabled");
            return Task.CompletedTask;
        });
        await Check("Native export uses a supported 4K tier, never arbitrary original size", () =>
        {
            var body = OpenRouterClient.RequestBody(options with { Resolution = "Gốc" }, new(2560, 1709), "s", "r");
            Assert((string)body["resolution"] == "4K" && (string)body["aspect_ratio"] == "3:2" && !body.ContainsKey("size"), "Sent unsupported original size");
            Reject(() => OpenRouterClient.RequestBody(options with { Resolution = "8K" }, new(2560, 1709), "s", "r"), "không hỗ trợ");
            return Task.CompletedTask;
        });
        using var fixedModelDoc = JsonDocument.Parse("""{"aspect_ratio":{"type":"enum","values":["1:1","3:2","2:3","auto"]},"input_references":{"type":"range","min":0,"max":16}}""");
        var fixedModel = new ImageModel("openai/gpt-5-image", "GPT-5 Image", fixedModelDoc.RootElement.Clone());
        await Check("Regression: GPT-5 Image rejects 2560x1709 before any HTTP request", async () =>
        {
            var handler = new FakeHandler(_ => new(HttpStatusCode.BadRequest));
            using var client = new OpenRouterClient(handler);
            try
            {
                await client.EditAsync(options with { Model = fixedModel, Resolution = "Gốc" }, new(2560, 1709), "s", "r", CancellationToken.None);
                throw new Exception("Unsupported size was not blocked");
            }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("1536x1024") && ex.Message.Contains("4K"), "Missing actionable size limits"); }
            Assert(handler.Calls == 0, "Sent rejected size to API");
        });
        await Check("Fixed-size model accepts smaller source using exact allowlisted size", () =>
        {
            var body = OpenRouterClient.RequestBody(options with { Model = fixedModel, Resolution = "Gốc" }, new(1024, 683), "s", "r");
            Assert((string)body["size"] == "1536x1024" && !body.ContainsKey("resolution") && !body.ContainsKey("aspect_ratio"), "Not an allowed fixed size");
            return Task.CompletedTask;
        });
        await Check("Provider size enums are honored and unknown sizes fail closed", () =>
        {
            using var supported = JsonDocument.Parse("""{"size":{"type":"enum","values":["auto","3072x2048"]}}""");
            var native = options with { Model = new("test/fixed", "Fixed", supported.RootElement.Clone()), Resolution = "Gốc" };
            var body = OpenRouterClient.RequestBody(native, new(2560, 1709), "s", "r");
            Assert((string)body["size"] == "3072x2048", "Ignored provider allowlist");
            Reject(() => OpenRouterClient.RequestBody(options with { Model = new("test/unknown", "Unknown", fixedModel.Parameters), Resolution = "Gốc" }, new(2560, 1709), "s", "r"), "không công bố");
            return Task.CompletedTask;
        });
        await Check("HTTPS endpoint validation", () =>
        {
            foreach (var url in new[] { "http://example.org/v1", "https://user:pass@example.org/v1", "https://example.org/v1?key=secret" })
                Reject(() => OpenRouterClient.Endpoint(url, "images"), "HTTPS");
            Assert(OpenRouterClient.Endpoint("https://openrouter.ai/api/v1", "images").AbsoluteUri == "https://openrouter.ai/api/v1/images", "Wrong API route");
            return Task.CompletedTask;
        });
        await Check("DPAPI key roundtrip", () =>
        {
            var encrypted = SecretStore.Protect("sample-api-key"); Assert(encrypted != "sample-api-key" && SecretStore.Unprotect(encrypted) == "sample-api-key", "Key storage failed"); return Task.CompletedTask;
        });
        await Check("JPEG/PNG export preserves original dimensions and rejects overwrite", () =>
        {
            var before = SHA256.HashData(File.ReadAllBytes(sourcePath));
            foreach (var format in new[] { "JPEG", "PNG" })
            {
                var file = Path.Combine(root, "export." + format.ToLowerInvariant());
                ImageFiles.Export(asset, new(2560, 1709), file, format);
                Assert(ImageFiles.Size(file) == new ImageSize(2560, 1709), "Wrong exported size");
            }
            try { ImageFiles.Export(asset, new(2560, 1709), sourcePath, "JPEG"); throw new Exception("Overwrote original"); } catch (IOException) { }
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourcePath))), "Original modified"); return Task.CompletedTask;
        });
        await Check("Larger provider image downsampled exactly to original; smaller rejected", () =>
        {
            var larger = BitmapSource.Create(160, 108, 96, 96, PixelFormats.Rgb24, null, new byte[160 * 108 * 3], 160 * 3);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(larger)); using var buffer = new MemoryStream(); encoder.Save(buffer);
            var file = Path.Combine(root, "scaled.png"); ImageFiles.Export(buffer.ToArray(), new(80, 54), file, "PNG");
            Assert(ImageFiles.Size(file) == new ImageSize(80, 54), "Incorrect resize");
            Reject(() => ImageFiles.Export(buffer.ToArray(), new(320, 216), Path.Combine(root, "must-not-exist.png"), "PNG"), "Không phóng lớn");
            Assert(!File.Exists(Path.Combine(root, "must-not-exist.png")), "Saved undersized result"); return Task.CompletedTask;
        });
        await Check("EXIF rotation is normalized without losing pixels", () =>
        {
            var bitmap = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Rgb24, null, new byte[80 * 40 * 3], 80 * 3);
            var metadata = new BitmapMetadata("jpg"); metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            var file = Path.Combine(root, "rotated.jpg"); using (var stream = File.Create(file)) encoder.Save(stream);
            Assert(ImageFiles.Size(file) == new ImageSize(40, 80), "Orientation dimensions");
            var normalized = ImageFiles.Decode(Convert.FromBase64String(ImageFiles.DataUrl(file).Split(',')[1]));
            Assert(normalized.PixelWidth == 40 && normalized.PixelHeight == 80, "Orientation upload"); return Task.CompletedTask;
        });
        await Check("API success parsed and error is not automatically retried", async () =>
        {
            var okHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(asset), media_type = "image/jpeg" } }, usage = new { cost = .04 } })) });
            using (var client = new OpenRouterClient(okHandler))
            {
                var result = await client.EditAsync(options, new(2560, 1709), "s", "r", CancellationToken.None);
                Assert(result.Cost == .04m && result.Bytes.Length == asset.Length, "Bad response parse");
                Assert(okHandler.Calls == 1, "Unexpected retries");
            }
            var errorHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"message":"rate limited"}}""") });
            using var failed = new OpenRouterClient(errorHandler);
            try { await failed.EditAsync(options, new(2560, 1709), "s", "r", CancellationToken.None); throw new Exception("Expected HTTP failure"); }
            catch (HttpRequestException ex) { Assert(ex.Message.Contains("429"), "Missing HTTP status"); }
            Assert(errorHandler.Calls == 1, "Billable retry attempted");
        });
        await Check("Model discovery filters text-only and single-reference models", async () =>
        {
            var item = new { id = "test/image", name = "Test", architecture = new { input_modalities = new[] { "image", "text" }, output_modalities = new[] { "image" } }, supported_parameters = model.Parameters };
            var eligible = JsonNode.Parse(JsonSerializer.Serialize(item))!;
            var textOnly = eligible.DeepClone(); textOnly["id"] = "test/text"; textOnly["architecture"]!["output_modalities"] = new JsonArray("text");
            var oneReference = eligible.DeepClone(); oneReference["id"] = "test/one-reference"; oneReference["supported_parameters"]!["input_references"]!["max"] = 1;
            var payload = new JsonObject { ["data"] = new JsonArray(eligible, textOnly, oneReference) }.ToJsonString();
            using var client = new OpenRouterClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(payload) }));
            var found = await client.ModelsAsync(options.ApiBase, CancellationToken.None); Assert(found.Count == 1 && found[0].Supports("resolution"), "Model parse");
        });
        await Check("Concurrent batch handles same filename safely and records costs", async () =>
        {
            var handler = new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(asset) } }, usage = new { cost = .04 } })) });
            using var client = new OpenRouterClient(handler);
            var jobs = Enumerable.Range(0, 3).Select(_ => new PhotoJob { SourcePath = sourcePath, Width = 2560, Height = 1709 }).ToArray();
            var updates = new System.Collections.Concurrent.ConcurrentBag<JobUpdate>();
            await Task.Run(() => new BatchRunner(client).RunAsync(jobs, options, new InlineProgress(updates.Add), CancellationToken.None));
            var done = updates.Where(u => u.State == "Hoàn tất").ToArray();
            Assert(done.Length == 3, string.Join(" | ", updates.Where(u => u.State == "Lỗi").Select(u => u.Detail)));
            Assert(done.Select(d => d.OutputPath).Distinct().Count() == 3, "Output collision");
            Assert(handler.MaxActive <= 2 && handler.Calls == 3, "Concurrency exceeded");
            Assert(done.All(d => d.Cost == .04m), "Missing cost");
        });
        await Check("Cancellation stops queued requests", async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var handler = new FakeHandler(_ => new(HttpStatusCode.OK)); using var client = new OpenRouterClient(handler);
            var jobs = Enumerable.Range(0, 3).Select(_ => new PhotoJob { SourcePath = sourcePath, Width = 2560, Height = 1709 }).ToArray();
            var updates = new System.Collections.Concurrent.ConcurrentBag<JobUpdate>();
            await Task.Run(() => new BatchRunner(client).RunAsync(jobs, options, new InlineProgress(updates.Add), cancellation.Token));
            Assert(handler.Calls == 0 && updates.Count(u => u.State == "Đã dừng") == 3, "Cancelled queue sent requests");
        });
        var json = JsonSerializer.Serialize(checks); using var checkDoc = JsonDocument.Parse(json);
        int failed = checkDoc.RootElement.EnumerateArray().Count(item => !item.GetProperty("passed").GetBoolean());
        AppFiles.AtomicJson(reportPath, new { total = checks.Count, passed = checks.Count - failed, failed, checks });
        // Delete only this run's enumerated files, then remove its now-empty directory.
        foreach (var path in Directory.GetFiles(root)) File.Delete(path);
        Directory.Delete(root, false);
        return failed == 0 ? 0 : 1;
    }
    private sealed class InlineProgress(Action<JobUpdate> action) : IProgress<JobUpdate> { public void Report(JobUpdate value) => action(value); }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls; public int MaxActive; private int active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); int current = Interlocked.Increment(ref active);
            int previous; do { previous = MaxActive; } while (current > previous && Interlocked.CompareExchange(ref MaxActive, current, previous) != previous);
            try { await Task.Delay(25, cancellationToken); return response(request); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
