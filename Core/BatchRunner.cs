using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhotoTone.Core;

public sealed record JobUpdate(PhotoJob Job, string State, string Detail = "", string? OutputPath = null, decimal? Cost = null);
public sealed class BatchRunner(OpenRouterClient client)
{
    public async Task RunAsync(IReadOnlyList<PhotoJob> jobs, BatchOptions options, IProgress<JobUpdate> progress, CancellationToken token)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        // Full-resolution reference encoded once per batch; never substitute the UI thumbnail.
        var reference = ImageFiles.DataUrl(options.ReferencePath);
        using var gate = new SemaphoreSlim(Math.Clamp(options.Concurrency, 1, 4));
        var batchId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var receipts = new System.Collections.Concurrent.ConcurrentBag<object>();
        await Task.WhenAll(jobs.Select(async job =>
        {
            bool entered = false;
            decimal? charged = null;
            try
            {
                await gate.WaitAsync(token); entered = true;
                token.ThrowIfCancellationRequested();
                progress.Report(new(job, "Đang gửi", ""));
                var size = ImageFiles.Size(job.SourcePath);
                var source = ImageFiles.DataUrl(job.SourcePath);
                progress.Report(new(job, "AI đang xử lý", ""));
                var result = await client.EditAsync(options, size, source, reference, token);
                charged = result.Cost;
                token.ThrowIfCancellationRequested();
                progress.Report(new(job, "Kiểm tra pixel", "", Cost: charged));
                var fileName = Path.GetFileNameWithoutExtension(job.SourcePath) + "_edited_" + batchId + "_" + job.Id[..6] + (options.Format == "PNG" ? ".png" : ".jpg");
                var output = Path.Combine(options.OutputDirectory, fileName);
                var providerSize = ImageFiles.Export(result.Bytes, size, output, options.Format);
                var receipt = new
                {
                    source = job.SourcePath, output, original = size, provider = providerSize, exported = ImageFiles.Size(output),
                    model = result.Model, resolution = options.Resolution, requestId = result.RequestId,
                    prompt = options.Prompt, promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Prompt))),
                    reference = options.ReferencePath, sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(job.SourcePath))),
                    costUsd = result.Cost, completedUtc = DateTimeOffset.UtcNow, status = "completed"
                };
                AppFiles.AtomicJson(Path.ChangeExtension(output, ".json"), receipt);
                receipts.Add(receipt);
                progress.Report(new(job, "Hoàn tất", $"Gốc {size} · Model {providerSize} · Xuất {size}", output, charged));
            }
            catch (OperationCanceledException)
            {
                var state = token.IsCancellationRequested ? "Đã dừng" : "Hết thời gian";
                var detail = token.IsCancellationRequested ? "Đã hủy yêu cầu phía app; kiểm tra chi phí trên nhà cung cấp." : "Yêu cầu quá 12 phút. Không tự gửi lại để tránh tính phí trùng.";
                receipts.Add(new { source = job.SourcePath, status = state, detail, costUsd = charged });
                progress.Report(new(job, state, detail, Cost: charged));
            }
            catch (Exception ex)
            {
                receipts.Add(new { source = job.SourcePath, status = "failed", detail = ex.Message, costUsd = charged });
                progress.Report(new(job, "Lỗi", ex.Message, Cost: charged));
            }
            finally { if (entered) gate.Release(); }
        }));
        AppFiles.AtomicJson(Path.Combine(options.OutputDirectory, "batch_" + batchId + ".json"), new { batchId, model = options.Model.Id, jobs = receipts.ToArray() });
    }
}
