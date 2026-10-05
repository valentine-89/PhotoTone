namespace PhotoTone.Core;

public static class StaWork
{
    public static Task<T> Run<T>(Func<T> action, CancellationToken token = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { token.ThrowIfCancellationRequested(); done.TrySetResult(action()); }
            catch (OperationCanceledException) { done.TrySetCanceled(token); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "PhotoTone image preparation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
