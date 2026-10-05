using System.IO;

namespace PhotoTone.Core;

public sealed record ExportItem(string JobId, string SourceName, ImageSize Size, ResultVersion Version);
public sealed record ExportOutcome(ExportItem Item, string? Path, string? Error);
public static class ExportService
{
    public static ExportItem[] Snapshot(IEnumerable<PhotoJob> jobs) => jobs.Where(j => j.Current is not null)
        .Select(j => new ExportItem(j.Id, j.Name, new(j.Width, j.Height), j.Current!)).ToArray();
    public static ExportOutcome[] Export(IReadOnlyList<ExportItem> items, string directory, string format)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("Chọn thư mục xuất hợp lệ.");
        if (format is not ("JPEG" or "PNG")) throw new ArgumentException("Chọn JPEG hoặc PNG.");
        Directory.CreateDirectory(directory);
        var outcomes = new List<ExportOutcome>();
        foreach (var item in items)
        {
            try
            {
                var basename = Path.GetFileNameWithoutExtension(item.SourceName) + "_edited";
                var extension = format == "PNG" ? ".png" : ".jpg";
                for (int suffix = 0; ; suffix++)
                {
                    var path = Path.Combine(directory, basename + (suffix == 0 ? "" : "_" + suffix) + extension);
                    if (File.Exists(path)) continue;
                    try { ImageFiles.Export(File.ReadAllBytes(item.Version.Path), item.Size, path, format); outcomes.Add(new(item, path, null)); break; }
                    catch (IOException) when (File.Exists(path)) { /* A concurrent export claimed the name; choose the next one. */ }
                }
            }
            catch (Exception ex) { outcomes.Add(new(item, null, ex.Message)); }
        }
        return outcomes.ToArray();
    }
}
