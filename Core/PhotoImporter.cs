using System.IO;
using System.Security.Cryptography;

namespace PhotoTone.Core;

public sealed record FileStamp(long Length, long WriteTicks, string Sha256)
{
    public static FileStamp Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Không tìm thấy ảnh.", path);
        if (info.Length > ImageFiles.MaxFileBytes) throw new InvalidDataException("Ảnh vượt 40 MB.");
        using var stream = File.OpenRead(path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(stream)));
    }
    public void Verify(string path)
    {
        if (Read(path) != this) throw new IOException($"Ảnh đã thay đổi: {Path.GetFileName(path)}. Thêm lại ảnh để tiếp tục.");
    }
}
public sealed record ImportResult(IReadOnlyList<PhotoJob> Jobs, IReadOnlyList<string> Errors);
public static class PhotoImporter
{
    public const string Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp";
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };
    public static ImportResult Import(IEnumerable<string> paths, IEnumerable<string> existing, CancellationToken token = default)
    {
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var jobs = new List<PhotoJob>(); var errors = new List<string>();
        foreach (var supplied in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(supplied);
                var files = Directory.Exists(path) ? Enumerate(path, errors, token).OrderBy(p => p, StringComparer.OrdinalIgnoreCase) : new[] { path }.AsEnumerable();
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(file)) continue;
                    try
                    {
                        if (!Extensions.Contains(Path.GetExtension(file))) throw new InvalidDataException("Định dạng chưa hỗ trợ.");
                        var stamp = FileStamp.Read(file); var size = ImageFiles.Size(file);
                        jobs.Add(new PhotoJob { SourcePath = file, SourceStamp = stamp, Width = size.Width, Height = size.Height });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{supplied}: {ex.Message}"); }
        }
        return new(jobs, errors);
    }
    private static IEnumerable<string> Enumerate(string directory, List<string> errors, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) continue;
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (Exception ex) { errors.Add($"{current}: {ex.Message}"); continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) { errors.Add($"{entry}: {ex.Message}"); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }
}
