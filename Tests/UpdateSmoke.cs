using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PhotoTone.Core;

namespace PhotoTone.Tests;

// Exercises the actual helper/parent exit/relaunch using an isolated copy, with no workspace access.
internal static class UpdateSmoke
{
    private const string ReportVariable = "PHOTOTONE_UPDATE_SMOKE_REPORT";
    private const string TargetVariable = "PHOTOTONE_UPDATE_SMOKE_TARGET";
    public static bool IsChild => Environment.GetEnvironmentVariable(ReportVariable) is not null
        && string.Equals(Environment.GetEnvironmentVariable(TargetVariable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    public static async Task<int> ParentAsync(string directory)
    {
        var candidate = Path.Combine(directory, "candidate.exe");
        await UpdateInstaller.LaunchAsync(new(directory, candidate, UpdateInstaller.Hash(candidate), GitHubUpdates.CurrentVersion));
        return 0;
    }
    public static int Finish(string plan)
    {
        var outcome = UpdateInstaller.ReadOutcome(plan);
        var info = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(plan))!;
        var actualHash = UpdateInstaller.Hash(Environment.ProcessPath!);
        bool passed = outcome?.Success == true && actualHash == info.CandidateSha256 && actualHash != info.OriginalSha256;
        AppFiles.AtomicJson(Environment.GetEnvironmentVariable(ReportVariable)!, new { passed, helperRelaunched = true, binaryReplaced = actualHash != info.OriginalSha256, version = GitHubUpdates.CurrentVersion.ToString(), actualHash, outcome, workspaceAccessed = false });
        return passed ? 0 : 1;
    }
    public static async Task<int> RunAsync(string output)
    {
        if (!UpdateInstaller.CanInstall) throw new InvalidOperationException("Updater smoke requires the published EXE.");
        output = Path.GetFullPath(output);
        if (File.Exists(output)) File.Delete(output);
        var folder = Path.Combine(Path.GetDirectoryName(output)!, "update-smoke-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(UpdateInstaller.Root, Guid.NewGuid().ToString("N"));
        var childReport = Path.Combine(folder, "result.json");
        Directory.CreateDirectory(folder); Directory.CreateDirectory(stage);
        var target = Path.Combine(folder, "PhotoTone.exe");
        try
        {
            File.Copy(Environment.ProcessPath!, target);
            using (var append = new FileStream(target, FileMode.Append, FileAccess.Write)) append.Write("PhotoTone isolated old binary"u8);
            File.Copy(Environment.ProcessPath!, Path.Combine(stage, "candidate.exe"));
            var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--update-smoke-parent"); start.ArgumentList.Add(stage);
            start.Environment[ReportVariable] = childReport; start.Environment[TargetVariable] = target;
            using var parent = Process.Start(start) ?? throw new IOException("Cannot start smoke parent.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
            if (parent.ExitCode != 0) throw new IOException("Smoke parent did not hand off update.");
            while (!File.Exists(childReport)) await Task.Delay(100, timeout.Token);
            using var report = JsonDocument.Parse(File.ReadAllText(childReport));
            if (!report.RootElement.GetProperty("passed").GetBoolean()) throw new IOException("Smoke relaunch failed.");
            File.Copy(childReport, output);
            return 0;
        }
        finally
        {
            // The relaunched probe and helper can still be returning from Shutdown.
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    UpdateInstaller.Cleanup(stage);
                    foreach (var path in new[] { target, childReport }) if (File.Exists(path)) File.Delete(path);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
                    if (!Directory.Exists(folder) && !Directory.Exists(stage)) break;
                }
                catch (IOException) { }
                await Task.Delay(100);
            }
        }
    }
}
