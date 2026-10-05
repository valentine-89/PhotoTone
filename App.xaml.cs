using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTone;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = await Tests.SelfTests.RunAsync(e.Args[1]); Shutdown(code); return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--check-models")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                using var client = new Core.OpenRouterClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var models = await client.ModelsAsync("https://openrouter.ai/api/v1", timeout.Token);
                var selected = models.First(m => m.Id == "google/gemini-3.1-flash-image");
                Core.OpenRouterClient.RequestBody(new("https://openrouter.ai/api/v1", "", selected, "4K", 1, "JPEG", "", "", ""), new(2560, 1709), "", "");
                Core.AppFiles.AtomicJson(e.Args[1], new { liveCatalog = true, modelCount = models.Count, defaultModel = selected.Id, resolutions = selected.Values("resolution"), billableRequests = 0 });
                Shutdown(0);
            }
            catch (Exception ex) { Core.AppFiles.AtomicJson(e.Args[1], new { error = ex.Message }); Shutdown(1); }
            return;
        }
        try
        {
            var window = new MainWindow(); MainWindow = window;
            if (e.Args.Length >= 2 && e.Args[0] == "--smoke-test")
            {
                window.ShowActivated = false; window.Left = -10000; window.Top = -10000;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Loaded += async (_, _) =>
                {
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    try
                    {
                        window.UpdateLayout();
                        var content = (FrameworkElement)window.Content;
                        var rendered = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        var surface = new DrawingVisual();
                        using (var drawing = surface.RenderOpen()) { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); }
                        rendered.Render(surface);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(rendered));
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                        using (var stream = File.Create(e.Args[1])) png.Save(stream);
                        File.WriteAllText(e.Args[1] + ".json", System.Text.Json.JsonSerializer.Serialize(new { loaded = true, width = window.ActualWidth, height = window.ActualHeight, samples = window.Jobs.Count }));
                        window.Close();
                    }
                    catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); Shutdown(1); }
                };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Core.AppFiles.Root);
            File.WriteAllText(Path.Combine(Core.AppFiles.Root, "startup-error.txt"), ex.ToString());
            MessageBox.Show(ex.Message, "PhotoTone", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
