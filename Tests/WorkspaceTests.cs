using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoTone.Controls;
using PhotoTone.Core;

namespace PhotoTone.Tests;

internal static class WorkspaceTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected rejection"); }
    public static byte[] SmallImage(int width = 120, int height = 80)
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 253);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
    }
    public static async Task RunAsync(string root, BatchOptions options, Func<string, Func<Task>, Task> check)
    {
        var fixture = Path.Combine(root, "fixtures"); Directory.CreateDirectory(fixture);
        var source = Path.Combine(fixture, "scene.png"); var reference = Path.Combine(fixture, "cloud.png");
        var pixels = SmallImage(); File.WriteAllBytes(source, pixels); File.WriteAllBytes(reference, pixels);
        PhotoJob NewJob(string path = "") => new() { SourcePath = path.Length == 0 ? source : path, SourceStamp = FileStamp.Read(path.Length == 0 ? source : path), Width = 120, Height = 80 };
        WorkItem Item(PhotoJob job, EditDraft? draft = null) => new(job, options with { ReferencePath = reference }, draft, source, FileStamp.Read(source), null);
        ResultVersion Version(WorkspaceStore store, string prompt = "test")
        {
            var path = store.NewResultPath(); File.WriteAllBytes(path, pixels);
            return new() { Path = path, ProviderSize = new(120, 80), Attempt = new() { Prompt = prompt, Status = "completed" } };
        }
        await check("Folder import: 500 images, nested order, duplicates and invalid files", () =>
        {
            var folder = Path.Combine(root, "import"); Directory.CreateDirectory(folder); var nested = Path.Combine(folder, "nested"); Directory.CreateDirectory(nested);
            for (int i = 0; i < 500; i++) File.WriteAllBytes(Path.Combine(i < 250 ? folder : nested, $"{i:000}.png"), pixels);
            File.WriteAllText(Path.Combine(folder, "bad.png"), "bad"); File.WriteAllText(Path.Combine(folder, "note.txt"), "note");
            var result = PhotoImporter.Import([folder, Path.Combine(folder, "000.png")], []);
            Assert(result.Jobs.Count == 500 && result.Errors.Count == 2, "Import lost files or duplicates");
            Assert(result.Jobs.Select(j => j.SourcePath).SequenceEqual(result.Jobs.Select(j => j.SourcePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)), "Folder ordering");
            return Task.CompletedTask;
        });
        await check("Source fingerprints reject changed and missing sources", () =>
        {
            var path = Path.Combine(fixture, "changed.png"); File.WriteAllBytes(path, pixels); var job = NewJob(path);
            File.WriteAllBytes(path, SmallImage(121, 80)); Reject(() => job.SourceStamp!.Verify(path)); File.Delete(path); Reject(() => job.SourceStamp!.Verify(path)); return Task.CompletedTask;
        });
        await check("Unreadable file is isolated and an explicitly too-small resolution is blocked", () =>
        {
            using (var locked = new FileStream(reference, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var imported = PhotoImporter.Import([reference, source], []);
                Assert(imported.Jobs.Count == 1 && imported.Errors.Count == 1, "Unreadable image stopped other imports");
            }
            Reject(() => ResolutionPlan.Create(options.Model, "2K", new(2560, 1709)));
            return Task.CompletedTask;
        });
        await check("Reference regions use EXIF-normalized pixels and reject source changes before sending", async () =>
        {
            var rotated = Path.Combine(root, "rotated.jpg");
            var draft = new EditDraft("Lấy A1.1", [], [new(rotated, FileStamp.Read(rotated), [new(.25, 0, .5, .5)])]);
            var request = await StaWork.Run(() => EditComposer.Compose(Item(NewJob(), draft)));
            var crop = ImageFiles.Decode(Convert.FromBase64String(request.Images[1].DataUrl.Split(',')[1]));
            Assert(crop.PixelWidth == 20 && crop.PixelHeight == 40, "EXIF crop used unrotated coordinates");
            var stale = Item(NewJob()) with { InitialReferenceStamp = new(1, 1, "invalid") };
            Reject(() => EditComposer.Validate(stale));
        });
        await check("Queue FIFO, append while running, duplicate rejection, maximum 1-4", async () =>
        {
            for (int limit = 1; limit <= 4; limit++)
            {
                var order = new List<string>(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int active = 0, peak = 0;
                var jobs = Enumerable.Range(0, 8).Select(_ => NewJob()).ToArray();
                var queue = new BatchRunner(async (item, token) => { order.Add(item.Job.Id); peak = Math.Max(peak, ++active); await release.Task.WaitAsync(token); active--; }) { Concurrency = limit };
                queue.Enqueue(jobs.Take(4).Select(j => Item(j)).ToArray());
                Reject(() => queue.Enqueue([Item(jobs[0])]));
                await Task.Delay(20); queue.Enqueue(jobs.Skip(4).Select(j => Item(j)).ToArray());
                release.SetResult(); await queue.WhenIdle;
                Assert(peak == limit && order.SequenceEqual(jobs.Select(j => j.Id)), "FIFO / concurrency mismatch");
            }
        });
        await check("Queue cancellation stops running and pending, then permits explicit restart", async () =>
        {
            int called = 0, cancelled = 0;
            var queue = new BatchRunner(async (_, token) => { called++; await Task.Delay(1000, token); }) { Concurrency = 2 };
            queue.Failed += (_, ex) => { if (ex is OperationCanceledException) cancelled++; };
            var items = Enumerable.Range(0, 5).Select(_ => Item(NewJob())).ToArray(); queue.Enqueue(items);
            await Task.Delay(20); var dropped = queue.Stop(); await queue.WhenIdle;
            Assert(called == 2 && dropped.Length == 3 && cancelled == 2, "Cancellation sent queued requests");
            queue.Enqueue([items[0]]); await queue.WhenIdle; Assert(called == 3, "Cannot restart after cancel");
        });
        await check("Cloud refinement: clean full-size base, guide and native crop with correct labels", async () =>
        {
            var original = SHA256.HashData(File.ReadAllBytes(source));
            var draft = new EditDraft("Thêm mây A1.1 vào cửa sổ T1", [new(.5, .1, .3, .5)], [new(reference, FileStamp.Read(reference), [new(.1, .2, .5, .5)])]);
            var request = await StaWork.Run(() => EditComposer.Compose(Item(NewJob(), draft)));
            Assert(request.Images.Count == 3 && request.Prompt.Contains("A1.1") && request.Prompt.Contains("T1"), "Missing source/target mapping");
            BitmapSource Decode(int n) => ImageFiles.Decode(Convert.FromBase64String(request.Images[n].DataUrl.Split(',')[1]));
            Assert(Decode(0).PixelWidth == 120 && Decode(0).PixelHeight == 80, "Main image downsampled");
            Assert(Decode(2).PixelWidth == 60 && Decode(2).PixelHeight == 40, "Crop wrong resolution");
            Assert(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Guide modified original");
            Assert(!request.Prompt.Contains(options.Prompt), "Inherited initial prompt");
        });
        await check("Wall white balance: target box works without reference; prompt-only uses one image", async () =>
        {
            var wall = new EditDraft("Cân bằng trắng tường T1", [new(.1, .1, .3, .3)], []);
            var prepared = await StaWork.Run(() => EditComposer.Compose(Item(NewJob(), wall)));
            Assert(prepared.Images.Count == 2 && prepared.Prompt.Contains("Cân bằng trắng"), "Wall edit failed");
            var text = await StaWork.Run(() => EditComposer.Compose(Item(NewJob(), new("Sáng hơn", [], []))));
            Assert(text.Images.Count == 1, "Prompt-only needs unexpected reference");
        });
        await check("Reference limit blocks before HTTP and does not drop selected regions", async () =>
        {
            using var parameters = JsonDocument.Parse("""{"input_references":{"max":2},"resolution":{"values":["4K"]},"aspect_ratio":{"values":["3:2"]}}""");
            var model = new ImageModel("test/two", "Two", parameters.RootElement.Clone());
            var handler = new RecordingHandler(pixels); using var client = new OpenRouterClient(handler);
            var edit = new PreparedEdit("test", [new("s", "main"), new("g", "guide"), new("r", "reference")]);
            try { await client.EditAsync(options with { Model = model }, new(120, 80), edit, default); throw new Exception("Expected reference rejection"); }
            catch (InvalidOperationException) { }
            Assert(handler.Calls == 0, "Over-limit request sent");
        });
        await check("Region transform handles zoom, pan, letterbox and reversed drags", () =>
        {
            var normal = ImageCanvas.ToNormalized(new Point(220, 160), new Vector(100, 80), 2, new(120, 80));
            Assert(normal == new Point(.5, .5), "Coordinates shifted");
            var region = ImageCanvas.FromPoints(new(.8, .9), new(.2, .3)); var rect = region.Pixels(new(120, 80));
            Assert(rect.X == 24 && rect.Y == 24 && rect.Width >= 72 && rect.Height >= 48, "Reverse drag failed");
            Reject(() => new ImageRegion(.9, .1, .2, .2).Validate()); Reject(() => new ImageRegion(0, 0, 0, 1).Validate()); return Task.CompletedTask;
        });
        await check("Workspace restart restores versions, exports and interrupted attempt without API", () =>
        {
            var location = Path.Combine(root, "session"); var job = NewJob(); string currentId;
            using (var store = new WorkspaceStore(location))
            {
                var first = Version(store); store.Commit(job, first, [job]);
                var second = Version(store); second.Exported = true; store.Commit(job, second, [job]); currentId = second.Id;
                job.LastAttempt = new() { Status = "running", Prompt = "retry" }; store.Save([job]);
                Assert(!File.ReadAllText(store.ManifestPath).Contains(options.Key), "Key persisted");
                Reject(() => { using var other = new WorkspaceStore(location); });
            }
            using var reloaded = new WorkspaceStore(location); var restored = reloaded.Load().Single();
            Assert(restored.Current?.Id == currentId && restored.Current.Exported && restored.Previous is not null, "Versions not restored");
            Assert(restored.LastAttempt?.Status == "interrupted" && !restored.Busy, "Interrupted job automatically resumed");
            return Task.CompletedTask;
        });
        await check("Atomic commit failure keeps previous result and a valid on-disk manifest", () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "failedcommit")); var job = NewJob(); var old = Version(store); store.Commit(job, old, [job]);
            var candidate = Version(store);
            using (var held = new FileStream(store.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read)) Reject(() => store.Commit(job, candidate, [job]));
            Assert(job.Current == old && store.Load().Single().Current?.Id == old.Id, "Failed commit lost current version");
            store.Prune([job]); Assert(!File.Exists(candidate.Path) && File.Exists(old.Path), "Orphan recovery failed"); return Task.CompletedTask;
        });
        await check("One-step undo and pruning protect pinned exports and input files", () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "undo")); var job = NewJob();
            var first = Version(store); store.Commit(job, first, [job]); using var pin = store.Pin([first.Path]);
            var second = Version(store); store.Commit(job, second, [job]); var third = Version(store); store.Commit(job, third, [job]);
            Assert(File.Exists(first.Path), "Pinned version deleted");
            store.Undo(job, [job]); Assert(job.Current == second && job.Previous is null && !File.Exists(third.Path), "Undo failed");
            pin.Dispose(); store.Prune([job]); Assert(!File.Exists(first.Path), "Unpinned orphan retained");
            Reject(() => store.DeleteOwned(source)); Assert(File.Exists(source), "Source deleted"); return Task.CompletedTask;
        });
        await check("Export snapshots latest versions, preserves dimensions, handles collisions and partial failures", () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "exportsession")); var job = NewJob(); var first = Version(store); store.Commit(job, first, [job]);
            var snapshot = ExportService.Snapshot([job]); using var held = store.Pin(snapshot.Select(s => s.Version.Path));
            store.Commit(job, Version(store), [job]); store.Commit(job, Version(store), [job]);
            var output = Path.Combine(root, "exported"); Directory.CreateDirectory(output); var existing = Path.Combine(output, "scene_edited.png"); File.WriteAllBytes(existing, pixels);
            var result = ExportService.Export(snapshot.Concat([new ExportItem("missing", "missing.png", new(120, 80), new ResultVersion { Path = Path.Combine(root, "missing.png"), ProviderSize = new(120, 80), Attempt = new() })]).ToArray(), output, "PNG");
            Assert(result[0].Path == Path.Combine(output, "scene_edited_1.png") && ImageFiles.Size(result[0].Path!) == new ImageSize(120, 80), "Export collision/size failure");
            Assert(result[1].Error is not null && snapshot[0].Version == first && !job.Current!.Exported, "Snapshot mixed revisions");
            Assert(File.ReadAllBytes(existing).SequenceEqual(pixels), "Existing output overwritten");
            store.Save([]); held.Dispose(); store.Prune([]); Assert(File.Exists(existing) && File.Exists(source), "Cleanup deleted user files"); return Task.CompletedTask;
        });
        await check("Mock end-to-end initial result, cloud refinement, wall refinement and undo", async () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "e2e")); var job = NewJob(); var handler = new RecordingHandler(pixels); using var client = new OpenRouterClient(handler);
            async Task Run(EditDraft? draft)
            {
                var input = job.OutputPath ?? source;
                var item = Item(job, draft) with { InputPath = input, InputStamp = FileStamp.Read(input), ParentVersion = job.Current?.Id };
                var request = await StaWork.Run(() => EditComposer.Compose(item));
                var result = await client.EditAsync(item.Options, new(120, 80), request, default); var path = store.NewResultPath();
                var size = ImageFiles.Export(result.Bytes, new(120, 80), path, "PNG");
                store.Commit(job, new() { Path = path, ProviderSize = size, Attempt = new() { Draft = draft, Prompt = request.Prompt, Cost = result.Cost, Status = "completed" } }, [job]);
            }
            await Run(null);
            await Run(new("Thêm mây A1.1 vào T1", [new(.4, .1, .3, .6)], [new(reference, FileStamp.Read(reference), [new(0, 0, .5, .5)])])); var cloud = job.Current;
            await Run(new("Cân bằng trắng tường T1", [new(.1, .1, .2, .2)], []));
            Assert(handler.Calls == 3 && handler.ReferenceCounts.SequenceEqual(new[] { 2, 3, 2 }), "Wrong API payloads");
            store.Undo(job, [job]); Assert(job.Current == cloud && Directory.GetFiles(store.Root, "*.png").Length == 1, "Undo retained extra revisions");
        });
        await check("Invalid provider output leaves existing result intact", () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "badoutput")); var job = NewJob(); var old = Version(store); store.Commit(job, old, [job]);
            var path = store.NewResultPath(); Reject(() => ImageFiles.Export(SmallImage(60, 40), new(120, 80), path, "PNG"));
            Assert(job.Current == old && !File.Exists(path), "Rejected output replaced previous result"); return Task.CompletedTask;
        });
        await check("Cleanup reports locked cache file, clears other files and can be retried", () =>
        {
            using var store = new WorkspaceStore(Path.Combine(root, "cleanup"));
            var locked = Version(store); var removable = Version(store);
            using (var held = new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                store.Prune([]);
                Assert(store.CleanupWarning is not null && File.Exists(locked.Path) && !File.Exists(removable.Path), "Cleanup stopped at first locked file");
            }
            store.Prune([]); Assert(store.CleanupWarning is null && !File.Exists(locked.Path), "Cleanup retry failed");
            return Task.CompletedTask;
        });
    }
    public static void DeleteTestTree(string root)
    {
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refuse test cleanup of link");
        foreach (var file in directory.GetFiles()) file.Delete();
        foreach (var child in directory.GetDirectories()) DeleteTestTree(child.FullName);
        directory.Delete(false);
    }
    private sealed class RecordingHandler(byte[] image) : HttpMessageHandler
    {
        public int Calls; public List<int> ReferenceCounts { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ReferenceCounts.Add(json.RootElement.GetProperty("input_references").GetArrayLength());
            Assert(!json.RootElement.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean(), "Fallback enabled");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(image) } }, usage = new { cost = .04m } })) };
        }
    }
}
