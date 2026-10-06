using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PhotoTone.Core;

namespace PhotoTone;

public sealed class UpdateWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Button install;
    private readonly Button later;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ProgressBar progress = new() { Height = 5, Maximum = 100, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private bool downloading;
    public PreparedUpdate? Prepared { get; private set; }
    public UpdateWindow(AppRelease release)
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Cập nhật PhotoTone"; Width = 490;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "PhotoTone " + release.Version + " đã có bản mới", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Đang dùng " + GitHubUpdates.CurrentVersion, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 12) });
        if (!string.IsNullOrWhiteSpace(release.Notes)) panel.Children.Add(new Expander { Header = "Nội dung bản phát hành", Foreground = Foreground, Content = new TextBox { Text = release.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) } });
        panel.Children.Add(progress); panel.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; panel.Children.Add(buttons);
        var page = new Button { Content = "Xem trên GitHub" };
        page.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { ShowStatus(ex.Message); } }; buttons.Children.Add(page);
        later = new Button { Content = "Để sau", IsCancel = true }; buttons.Children.Add(later);
        install = new Button { Content = "Cập nhật và mở lại", Style = (Style)FindResource("Primary"), Margin = new Thickness(0) }; buttons.Children.Add(install);
        install.IsEnabled = UpdateInstaller.CanInstall && release.Package?.Sha256 is not null && release.Checksums?.Sha256 is not null;
        if (!install.IsEnabled) ShowStatus(UpdateInstaller.CanInstall ? "Release chưa có gói cập nhật hợp lệ." : "Bản chạy phát triển: tải ZIP trên GitHub để cài.");
        install.Click += async (_, _) =>
        {
            downloading = true; install.IsEnabled = false; later.Content = "Hủy tải"; progress.Visibility = Visibility.Visible;
            ShowStatus("Đang tải bản cập nhật…"); cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                Prepared = await UpdateInstaller.PrepareAsync(release, Environment.ProcessPath!, new Progress<double>(value => progress.Value = value), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested(); downloading = false; DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                if (Prepared is not null) { UpdateInstaller.Cleanup(Prepared.Directory); Prepared = null; }
                downloading = false; Close();
            }
            catch (Exception ex) { downloading = false; ShowStatus("Không cập nhật được: " + ex.Message); install.IsEnabled = true; later.Content = "Để sau"; }
        };
        Closing += ClosingWindow; Closed += (_, _) => cancellation.Dispose();
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (!downloading) return;
        e.Cancel = true; cancellation.Cancel(); ShowStatus("Đang hủy tải…");
    }
    private void ShowStatus(string text) { status.Text = text; status.Visibility = Visibility.Visible; }
}
