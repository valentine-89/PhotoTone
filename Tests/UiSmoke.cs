using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoTone.Controls;
using PhotoTone.Core;

namespace PhotoTone.Tests;

internal static class UiSmoke
{
    public static async Task RunAsync(MainWindow window, string output)
    {
        await Ready();
        await WaitPreviews(window);
        Save(window, output, 1); Save(window, Path.ChangeExtension(output, ".150.png"), 1.5);
        window.Width = 980; window.Height = 600; await Ready();
        Save(window, Path.ChangeExtension(output, ".compact.png"), 1);
        window.Width = 1380; window.Height = 900; await Ready();
        var list = Find<ListBox>(window).Single();
        var prototype = window.Jobs[0];
        for (int i = window.Jobs.Count; i < 500; i++) window.Jobs.Add(new PhotoJob { SourcePath = prototype.SourcePath, Width = prototype.Width, Height = prototype.Height, Current = prototype.Current });
        await Ready(); await WaitPreviews(window);
        int firstRealized = Find<ListBoxItem>(list).Count();
        if (firstRealized > 8) throw new Exception($"Virtualization disabled: {firstRealized} rows realized.");
        list.ScrollIntoView(window.Jobs[^1]); await Ready(); await WaitPreviews(window);
        int lastRealized = Find<ListBoxItem>(list).Count();
        if (lastRealized > 8 || Find<AsyncPreview>(list).Count(i => i.Source is not null) > 16) throw new Exception("Preview loads are not bounded.");
        var source = Path.Combine(AppFiles.Samples, "ok.jpg");
        var draft = new EditDraft("Bổ sung mây A1.1 vào cửa sổ T1", [new(.78, .13, .17, .7)], [new(Path.Combine(AppFiles.Samples, "raw.jpg"), FileStamp.Read(Path.Combine(AppFiles.Samples, "raw.jpg")), [new(.1, .15, .4, .35)])]);
        var edit = new EditWindow(source, true, 14, draft) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        edit.Show(); await Ready(); await edit.Canvases.First().Loading;
        Save(edit, Path.ChangeExtension(output, ".edit.png"), 1); Save(edit, Path.ChangeExtension(output, ".edit.150.png"), 1.5);
        var tabs = Find<TabControl>(edit).Single(); tabs.SelectedIndex = 1; await Ready(); await edit.Canvases.Last().Loading;
        Save(edit, Path.ChangeExtension(output, ".reference.png"), 1);
        edit.Close();
        var viewer = new EditWindow(source) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        viewer.Show(); await Ready(); await viewer.Canvases.First().Loading;
        viewer.Canvases.First().ActualPixels();
        Save(viewer, Path.ChangeExtension(output, ".zoom.png"), 1); Save(viewer, Path.ChangeExtension(output, ".zoom.150.png"), 1.5); viewer.Close();
        var settings = new Settings { ReferencePath = source, Prompt = AppFiles.DefaultPrompt };
        var configFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "settings-smoke-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new SettingsWindow(settings, "", saved => AppFiles.SaveSettings(saved, configFile)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        config.Show(); await Ready();
        if (Find<TextBox>(config).Any(t => t.Text == OpenRouterClient.ApiBase) || !Find<Expander>(config).Single().IsExpanded) throw new Exception("Editable endpoint or missing first-use guidance.");
        Save(config, Path.ChangeExtension(output, ".settings.png"), 1); Save(config, Path.ChangeExtension(output, ".settings.150.png"), 1.5);
        config.Close();
        if (File.Exists(configFile)) throw new Exception("Closing configuration saved changes.");
        const string testKey = "test-only-not-a-real-key";
        try
        {
            var saveConfig = new SettingsWindow(settings, testKey, saved => AppFiles.SaveSettings(saved, configFile)) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            saveConfig.Loaded += (_, _) => Find<Button>(saveConfig).Single(b => b.Content?.ToString() == "Lưu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (saveConfig.ShowDialog() != true) throw new Exception("Configuration save did not finish.");
            var restored = AppFiles.LoadSettings(configFile);
            var reopen = new SettingsWindow(restored, AppFiles.LoadApiKey(restored), _ => throw new Exception("Reopen must not save")) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            reopen.Show(); await Ready();
            if (Find<PasswordBox>(reopen).Single().Password != testKey || Find<Expander>(reopen).Single().IsExpanded || File.ReadAllText(configFile).Contains(testKey)) throw new Exception("Key did not persist privately across dialogs.");
            Save(reopen, Path.ChangeExtension(output, ".settings-saved.png"), 1); reopen.Close();
        }
        finally { if (File.Exists(configFile)) File.Delete(configFile); }
        var update = new UpdateWindow(GitHubUpdates.ParseRelease(UpdateTests.ReleaseJson("9.9.9"))!) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        update.Show(); await Ready();
        if (Find<Button>(update).Single(b => b.Content?.ToString() == "Cập nhật và mở lại").IsEnabled != UpdateInstaller.CanInstall) throw new Exception("Update installation availability incorrect.");
        Save(update, Path.ChangeExtension(output, ".update.png"), 1); Save(update, Path.ChangeExtension(output, ".update.150.png"), 1.5);
        Find<Expander>(update).Single().IsExpanded = true; await Ready();
        Save(update, Path.ChangeExtension(output, ".update-notes.png"), 1);
        update.Close();
        if (update.Prepared is not null) throw new Exception("Closing update prompt initiated an update.");
        AppFiles.AtomicJson(output + ".json", new { loaded = true, jobs = window.Jobs.Count, firstRealized, lastRealized, updatePrompt = true, apiKeySavedAndReopened = true, fixedOpenRouterEndpoint = true, renderDpi = new[] { 96, 144 }, billableRequests = 0 });
    }
    private static async Task Ready() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(70); }
    private static async Task WaitPreviews(DependencyObject root)
    {
        for (int i = 0; i < 150; i++)
        {
            var images = Find<AsyncPreview>(root).Where(p => !string.IsNullOrEmpty(p.FilePath)).ToArray();
            if (images.Length > 0 && images.All(p => p.Source is not null)) return;
            await Task.Delay(40);
        }
        throw new Exception("Preview did not load.");
    }
    internal static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
    private static void Save(Window window, string path, double scale)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var render = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var dc = surface.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        render.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path); png.Save(stream);
    }
}
