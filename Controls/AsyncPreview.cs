using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoTone.Core;

namespace PhotoTone.Controls;

public sealed class AsyncPreview : Image
{
    private static readonly SemaphoreSlim Workers = new(2);
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(nameof(FilePath), typeof(string), typeof(AsyncPreview), new PropertyMetadata(null, Changed));
    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    private CancellationTokenSource? cancellation;
    public AsyncPreview()
    {
        Stretch = Stretch.Uniform;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => { cancellation?.Cancel(); Source = null; };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AsyncPreview)d).Refresh();
    private async void Refresh()
    {
        cancellation?.Cancel(); Source = null;
        if (!IsLoaded || string.IsNullOrWhiteSpace(FilePath)) return;
        var request = new CancellationTokenSource(); cancellation = request; var path = FilePath;
        try
        {
            await Workers.WaitAsync(request.Token);
            try
            {
                request.Token.ThrowIfCancellationRequested();
                var source = await Task.Run(() => ImageFiles.Load(path, 900), request.Token);
                if (!request.IsCancellationRequested && IsLoaded) { Source = source; ToolTip = null; }
            }
            finally { Workers.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) ToolTip = ex.Message; }
        finally { if (ReferenceEquals(cancellation, request)) cancellation = null; request.Dispose(); }
    }
}
