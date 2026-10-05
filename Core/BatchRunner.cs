namespace PhotoTone.Core;

// Called on the UI dispatcher: one FIFO shared by initial processing and refinements.
public sealed class BatchRunner(Func<WorkItem, CancellationToken, Task> execute)
{
    private readonly Queue<WorkItem> pending = new();
    private readonly HashSet<string> reserved = new();
    private CancellationTokenSource cancellation = new();
    private TaskCompletionSource idle = Completed();
    public int Concurrency { get; set; } = 1;
    public int Active { get; private set; }
    public int Pending => pending.Count;
    public bool IsBusy => Active + Pending > 0;
    public bool Stopping { get; private set; }
    public int Finished { get; private set; }
    public int Total { get; private set; }
    public Task WhenIdle => idle.Task;
    public event Action? Changed;
    public event Action<WorkItem, Exception>? Failed;
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    public bool Contains(string id) => reserved.Contains(id);
    public void Enqueue(IReadOnlyList<WorkItem> items)
    {
        if (Stopping) throw new InvalidOperationException("Đang dừng hàng đợi.");
        if (items.Select(i => i.Job.Id).Distinct().Count() != items.Count || items.Any(i => reserved.Contains(i.Job.Id)))
            throw new InvalidOperationException("Ảnh đã có lượt xử lý trong hàng đợi.");
        if (items.Count == 0) return;
        if (!IsBusy)
        {
            cancellation.Dispose(); cancellation = new(); Finished = Total = 0;
            idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        foreach (var item in items) { reserved.Add(item.Job.Id); pending.Enqueue(item); Total++; }
        Pump();
    }
    private void Pump()
    {
        while (!Stopping && Active < Math.Clamp(Concurrency, 1, 4) && pending.TryDequeue(out var item))
        {
            Active++; _ = RunOne(item, cancellation.Token);
        }
        Changed?.Invoke();
    }
    private async Task RunOne(WorkItem item, CancellationToken token)
    {
        await Task.Yield();
        try { token.ThrowIfCancellationRequested(); await execute(item, token); }
        catch (Exception ex) { Failed?.Invoke(item, ex); }
        finally
        {
            Active--; Finished++; reserved.Remove(item.Job.Id);
            if (!IsBusy) { Stopping = false; idle.TrySetResult(); }
            Pump();
        }
    }
    public WorkItem[] Stop()
    {
        Stopping = true; cancellation.Cancel();
        var removed = pending.ToArray(); pending.Clear();
        foreach (var item in removed) { reserved.Remove(item.Job.Id); Finished++; }
        if (!IsBusy) { Stopping = false; idle.TrySetResult(); }
        Changed?.Invoke(); return removed;
    }
}
