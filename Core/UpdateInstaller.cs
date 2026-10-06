using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoTone.Core;

public sealed record PreparedUpdate(string Directory, string Candidate, string CandidateSha256, Version Version);
public sealed record UpdatePlan(int Schema, string Target, string CandidateSha256, string OriginalSha256, string Version, int ParentId, long ParentStartTicks);
public sealed record UpdateOutcome(bool Success, string Message);

public static class UpdateInstaller
{
    public static string Root => Path.Combine(AppFiles.Root, "Updates");
    private static readonly string[] OwnedFiles = ["release.zip", "checksums.txt", "candidate.exe", "helper.exe", "ready.json", "plan.json", "result.json"];
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty Location is intentionally used to require the portable single-file build.")]
    public static bool CanInstall => Assembly.GetExecutingAssembly().Location.Length == 0 && Environment.ProcessPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true;
    public static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    public static void CheckNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Không cập nhật qua đường dẫn liên kết.");
    }
    public static async Task<PreparedUpdate> PrepareAsync(AppRelease release, string target, IProgress<double>? progress, CancellationToken token)
    {
        if (release.Package is null || release.Checksums is null) throw new InvalidDataException("Release chưa có đủ gói Windows và checksum.");
        CheckNoLinks(target);
        // Test write access before the running app is closed; never request elevation.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".phototone-write-" + Guid.NewGuid().ToString("N"));
        try { using var output = new FileStream(probe, FileMode.CreateNew, FileAccess.Write); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        CheckNoLinks(directory); System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var client = new GitHubUpdates();
            var zip = Path.Combine(directory, "release.zip"); var checksums = Path.Combine(directory, "checksums.txt");
            await client.DownloadAsync(release.Checksums, checksums, 16384, null, token);
            await client.DownloadAsync(release.Package, zip, 256L * 1024 * 1024, progress, token);
            var candidate = Path.Combine(directory, "candidate.exe");
            var hash = await Task.Run(() => ExtractVerified(zip, File.ReadAllText(checksums), release.Package.Name, release.Version, candidate), token);
            File.Delete(zip); File.Delete(checksums);
            return new(directory, candidate, hash, release.Version);
        }
        catch { Cleanup(directory); throw; }
    }
    internal static string ExtractVerified(string zip, string checksums, string packageName, Version expectedVersion, string candidate)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in checksums.Split('\n'))
        {
            var match = Regex.Match(line.Trim().TrimStart('\uFEFF'), @"^([a-fA-F0-9]{64})\s+\*?([^\s]+)$");
            if (!match.Success) continue;
            if (!hashes.TryAdd(match.Groups[2].Value, match.Groups[1].Value.ToUpperInvariant())) throw new InvalidDataException("Checksum trùng tên.");
        }
        if (!hashes.TryGetValue(packageName, out var zipHash) || zipHash != Hash(zip) || !hashes.TryGetValue("PhotoTone.exe", out var exeHash)) throw new InvalidDataException("Checksum của ZIP/EXE không khớp.");
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is < 1 or > 3 || archive.Entries.Any(e => e.FullName is not ("PhotoTone.exe" or "README.md" or "LICENSE"))
            || archive.Entries.Select(e => e.FullName).Distinct().Count() != archive.Entries.Count)
            throw new InvalidDataException("Cấu trúc ZIP cập nhật không hợp lệ.");
        var entry = archive.GetEntry("PhotoTone.exe") ?? throw new InvalidDataException("ZIP thiếu PhotoTone.exe.");
        if (entry.Length <= 0 || entry.Length > 256L * 1024 * 1024) throw new InvalidDataException("EXE cập nhật vượt kích thước cho phép.");
        using (var input = entry.Open()) using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write))
        {
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                total += count; if (total > entry.Length) throw new InvalidDataException("EXE giải nén vượt kích thước công bố.");
                output.Write(buffer, 0, count);
            }
            if (total != entry.Length) throw new InvalidDataException("EXE giải nén không đủ dữ liệu.");
        }
        if (Hash(candidate) != exeHash) throw new InvalidDataException("Checksum EXE không khớp.");
        ValidateExecutable(candidate, expectedVersion);
        return exeHash;
    }
    private static void ValidateExecutable(string path, Version expected)
    {
        var actual = FileVersionInfo.GetVersionInfo(path);
        if (actual.ProductName != "PhotoTone" || GitHubUpdates.ParseVersion(actual.ProductVersion ?? "") != expected) throw new InvalidDataException("Phiên bản EXE không khớp release.");
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        if (pe.PEHeaders.IsDll || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus) throw new InvalidDataException("Gói cập nhật không phải EXE Windows x64.");
    }
    public static async Task LaunchAsync(PreparedUpdate update)
    {
        if (!CanInstall) throw new InvalidOperationException("Dùng bản PhotoTone.exe trong ZIP để cập nhật trong app.");
        ValidateDirectory(update.Directory); CheckNoLinks(Environment.ProcessPath!);
        if (!string.Equals(Path.GetFullPath(update.Candidate), Path.Combine(update.Directory, "candidate.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("File cập nhật không thuộc lượt tải này.");
        if (Hash(update.Candidate) != update.CandidateSha256) throw new InvalidDataException("File cập nhật đã thay đổi.");
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan(1, Environment.ProcessPath!, update.CandidateSha256, Hash(Environment.ProcessPath!), update.Version.ToString(), parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        var path = Path.Combine(update.Directory, "plan.json"); AppFiles.AtomicJson(path, plan);
        var helper = Path.Combine(update.Directory, "helper.exe"); File.Copy(Environment.ProcessPath!, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = update.Directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Không khởi động được trình cập nhật.");
        // The app stays open until the helper has validated the plan and candidate.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(Path.Combine(update.Directory, "ready.json")))
            {
                if (process.HasExited) throw new IOException("Trình cập nhật không khởi động được. Bản hiện tại vẫn đang mở.");
                await Task.Delay(100, timeout.Token);
            }
        }
        catch
        {
            // Only stop the helper created by this call; it has not replaced the running app.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            throw;
        }
    }
    public static async Task<int> RunHelperAsync(string path)
    {
        string? directory = null; UpdatePlan? plan = null; bool parentExited = false;
        try
        {
            var requestedDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!; ValidateDirectory(requestedDirectory); directory = requestedDirectory;
            CheckNoLinks(path);
            if (Path.GetFileName(path) != "plan.json" || new FileInfo(path).Length > 16384) throw new InvalidDataException("Kế hoạch cập nhật không hợp lệ.");
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path)) ?? throw new InvalidDataException("Thiếu kế hoạch cập nhật.");
            if (plan.Schema != 1 || !Path.IsPathFullyQualified(plan.Target) || !plan.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đích cập nhật không hợp lệ.");
            CheckNoLinks(plan.Target);
            if (Hash(Environment.ProcessPath!) != plan.OriginalSha256 || Hash(plan.Target) != plan.OriginalSha256) throw new IOException("Bản app đang cài đã thay đổi.");
            Process? parent = null;
            try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { }
            if (parent is not null)
            {
                using (parent)
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks) throw new IOException("Tiến trình app đã thay đổi.");
                    ValidateCandidate();
                    AppFiles.AtomicJson(Path.Combine(directory, "ready.json"), new { ready = true });
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)); await parent.WaitForExitAsync(timeout.Token);
                }
            }
            else ValidateCandidate();
            parentExited = true;
            ApplyTransaction(plan.Target, Path.Combine(directory, "candidate.exe"), plan.OriginalSha256, plan.CandidateSha256, directory, target =>
            {
                AppFiles.AtomicJson(Path.Combine(directory, "result.json"), new UpdateOutcome(true, "Đã cập nhật lên " + plan.Version));
                StartTarget(target, path);
            });
            return 0;
            void ValidateCandidate()
            {
                var candidate = Path.Combine(directory, "candidate.exe"); CheckNoLinks(candidate);
                var expectedVersion = GitHubUpdates.ParseVersion(plan.Version) ?? throw new InvalidDataException("Phiên bản cập nhật không hợp lệ.");
                if (Hash(candidate) != plan.CandidateSha256 || expectedVersion < GitHubUpdates.CurrentVersion) throw new InvalidDataException("Bản cập nhật không hợp lệ hoặc cũ hơn app.");
                ValidateExecutable(candidate, expectedVersion);
            }
        }
        catch (Exception ex)
        {
            if (directory is not null)
            {
                try { AppFiles.AtomicJson(Path.Combine(directory, "result.json"), new UpdateOutcome(false, "Không cập nhật được: " + ex.Message)); } catch { }
            }
            if (parentExited && plan is not null && File.Exists(plan.Target))
                try { StartTarget(plan.Target, path); } catch { }
            return 1;
        }
    }
    internal static void ApplyTransaction(string target, string candidate, string originalHash, string candidateHash, string directory, Action<string> restart)
    {
        CheckNoLinks(target); CheckNoLinks(candidate);
        if (Hash(target) != originalHash || Hash(candidate) != candidateHash) throw new IOException("File app đã thay đổi trước khi thay thế.");
        var id = Path.GetFileName(directory);
        var staged = Path.Combine(Path.GetDirectoryName(target)!, ".phototone-" + id + ".new");
        var previous = Path.Combine(Path.GetDirectoryName(target)!, ".phototone-" + id + ".old");
        if (File.Exists(staged) || File.Exists(previous)) throw new IOException("Còn file của lượt cập nhật trước trong thư mục app.");
        bool completed = false;
        try
        {
            File.Copy(candidate, staged, false);
            if (Hash(staged) != candidateHash) throw new IOException("File chép cập nhật không khớp.");
            File.Replace(staged, target, previous);
            try { restart(target); completed = true; }
            catch { File.Replace(previous, target, null); throw; }
        }
        finally
        {
            if (File.Exists(staged))
                try { File.Delete(staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // Keep a rollback file if restoration itself failed; never delete the only good copy.
            if (completed && File.Exists(previous))
                try { File.Delete(previous); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static void StartTarget(string target, string plan)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--update-result"); start.ArgumentList.Add(plan);
        using var process = Process.Start(start) ?? throw new IOException("Không mở lại được PhotoTone.");
    }
    private static void ValidateDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("Thư mục cập nhật không hợp lệ.");
        CheckNoLinks(full);
    }
    public static UpdateOutcome? ReadOutcome(string plan)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(plan))!; ValidateDirectory(directory);
            if (Path.GetFileName(plan) != "plan.json") return null;
            var info = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(plan));
            if (!string.Equals(info?.Target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return null;
            return JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(Path.Combine(directory, "result.json")));
        }
        catch { return null; }
    }
    public static void Cleanup(string directory)
    {
        ValidateDirectory(directory);
        foreach (var name in OwnedFiles.Where(n => n is not ("plan.json" or "result.json")))
        {
            var path = Path.Combine(directory, name);
            try { if (File.Exists(path)) { CheckNoLinks(path); File.Delete(path); } }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!OwnedFiles.Where(n => n is not ("plan.json" or "result.json")).Any(n => File.Exists(Path.Combine(directory, n))))
            foreach (var name in new[] { "plan.json", "result.json" })
            {
                var path = Path.Combine(directory, name);
                try { if (File.Exists(path)) { CheckNoLinks(path); File.Delete(path); } } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        if (System.IO.Directory.Exists(directory) && !System.IO.Directory.EnumerateFileSystemEntries(directory).Any()) System.IO.Directory.Delete(directory, false);
    }
    public static void CleanupFinished()
    {
        if (!System.IO.Directory.Exists(Root)) return;
        CheckNoLinks(Root);
        foreach (var directory in System.IO.Directory.GetDirectories(Root))
        {
            try
            {
                ValidateDirectory(directory);
                if (File.Exists(Path.Combine(directory, "result.json")) || (!File.Exists(Path.Combine(directory, "plan.json")) && System.IO.Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))) Cleanup(directory);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
