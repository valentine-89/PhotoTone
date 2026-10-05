using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoTone.Core;

// Mutations are serialized by the UI dispatcher; image processing never mutates the manifest.
public sealed class WorkspaceStore : IDisposable
{
    private readonly FileStream lease;
    private readonly Dictionary<string, int> pins = new(StringComparer.OrdinalIgnoreCase);
    public string Root { get; }
    public string ManifestPath => Path.Combine(Root, "workspace.json");
    public string? CleanupWarning { get; private set; }
    private sealed record Manifest(int Version, PhotoJob[] Jobs);
    public WorkspaceStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(AppFiles.Root, "Workspace"));
        Directory.CreateDirectory(Root);
        CheckRoot();
        try { lease = new FileStream(Path.Combine(Root, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Bàn làm việc đang được mở trong một PhotoTone khác."); }
    }
    private void CheckRoot()
    {
        for (DirectoryInfo? directory = new(Root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cache không được nằm trong thư mục liên kết.");
    }
    public bool Owns(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(Path.GetFileName(full), @"^r-[a-f0-9]{32}\.png(\.[a-f0-9]{32}\.partial)?$");
    }
    public string NewResultPath() => Path.Combine(Root, "r-" + Guid.NewGuid().ToString("N") + ".png");
    public PhotoJob[] Load()
    {
        if (!File.Exists(ManifestPath)) return [];
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath)) ?? throw new InvalidDataException("Không đọc được bàn làm việc.");
        if (manifest.Version != 1) throw new InvalidDataException("Phiên bản bàn làm việc chưa được hỗ trợ.");
        foreach (var job in manifest.Jobs)
        {
            foreach (var version in new[] { job.Current, job.Previous }.OfType<ResultVersion>())
                if (!Owns(version.Path)) throw new InvalidDataException("Đường dẫn kết quả nằm ngoài cache.");
            if (job.Current is not null && !File.Exists(job.Current.Path)) { job.Current = null; job.State = "Thiếu kết quả"; }
            if (job.Previous is not null && !File.Exists(job.Previous.Path)) job.Previous = null;
            if (job.LastAttempt?.Status is "queued" or "running")
            {
                job.LastAttempt.Status = "interrupted"; job.State = "Bị gián đoạn";
                job.Detail = "Bấm xử lý để chạy lại.";
            }
            if (!File.Exists(job.SourcePath)) { job.State = "Thiếu ảnh nguồn"; job.Detail = job.SourcePath; }
        }
        return manifest.Jobs;
    }
    public void Save(IEnumerable<PhotoJob> jobs) { CheckRoot(); AppFiles.AtomicJson(ManifestPath, new Manifest(1, jobs.ToArray())); }
    public void Commit(PhotoJob job, ResultVersion result, IEnumerable<PhotoJob> jobs)
    {
        if (!Owns(result.Path) || !File.Exists(result.Path)) throw new IOException("Kết quả chưa được lưu vào cache.");
        var old = job.Current; var older = job.Previous; var state = job.State; var detail = job.Detail;
        try
        {
            job.Previous = old; job.Current = result; job.State = "Hoàn tất"; job.Detail = "";
            Save(jobs);
        }
        catch { job.Current = old; job.Previous = older; job.State = state; job.Detail = detail; throw; }
        Prune(jobs);
    }
    public void Undo(PhotoJob job, IEnumerable<PhotoJob> jobs)
    {
        if (!job.CanUndo) return;
        var old = job.Current; var previous = job.Previous; var state = job.State; var detail = job.Detail;
        job.Current = previous; job.Previous = null; job.State = "Đã hoàn tác"; job.Detail = "";
        try { Save(jobs); } catch { job.Current = old; job.Previous = previous; job.State = state; job.Detail = detail; throw; }
        Prune(jobs);
    }
    public IDisposable Pin(IEnumerable<string> paths)
    {
        var held = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (held.Any(path => !Owns(path))) throw new IOException("Không thể giữ file ngoài cache.");
        foreach (var path in held)
        {
            pins[path] = pins.GetValueOrDefault(path) + 1;
        }
        return new Release(() => { foreach (var path in held) if (--pins[path] == 0) pins.Remove(path); });
    }
    public void DeleteOwned(string path)
    {
        CheckRoot();
        if (!Owns(path) || pins.ContainsKey(path)) throw new IOException("File không thuộc cache hoặc đang được sử dụng.");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Không xóa file liên kết.");
            File.Delete(path);
        }
    }
    public void Prune(IEnumerable<PhotoJob> jobs)
    {
        var retained = jobs.SelectMany(j => new[] { j.Current?.Path, j.Previous?.Path }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        CleanupWarning = null;
        try
        {
            CheckRoot();
            foreach (var file in Directory.GetFiles(Root))
            {
                var pinKey = Regex.Replace(file, @"\.[a-f0-9]{32}\.partial$", "");
                if (pins.ContainsKey(pinKey) || retained.Contains(file)) continue;
                try
                {
                    if (Owns(file)) DeleteOwned(file);
                    else if (Regex.IsMatch(Path.GetFileName(file), @"^workspace\.json\.[a-f0-9]{32}\.tmp$") && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) File.Delete(file);
                }
                catch (IOException ex) { CleanupWarning ??= ex.Message; }
                catch (UnauthorizedAccessException ex) { CleanupWarning ??= ex.Message; }
            }
        }
        catch (IOException ex) { CleanupWarning = ex.Message; }
        catch (UnauthorizedAccessException ex) { CleanupWarning = ex.Message; }
    }
    public void Dispose() => lease.Dispose();
    private sealed class Release(Action action) : IDisposable { private Action? release = action; public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
}
