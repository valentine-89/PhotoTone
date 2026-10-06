using System.Windows;
using PhotoTone.Core;

namespace PhotoTone;

public partial class MainWindow
{
    private readonly CancellationTokenSource updateLifetime = new();
    private AppRelease? pendingRelease;
    private bool checkingUpdates, updateDialogOpen, updateHandoff, updateStarting;
    private bool IdleForUpdate => !runner.IsBusy && !preparing && !exporting && importing == 0 && !closing;
    private void InitializeUpdates()
    {
        Title = "PhotoTone · " + GitHubUpdates.CurrentVersion;
        UpdateButton.Content = "v" + GitHubUpdates.CurrentVersion + " · Cập nhật";
        if (previewOnly) { UpdateButton.IsEnabled = false; return; }
        Loaded += async (_, _) =>
        {
            await CheckForUpdatesAsync(false);
            try { await Task.Delay(5000, updateLifetime.Token); await Task.Run(UpdateInstaller.CleanupFinished, updateLifetime.Token); }
            catch (OperationCanceledException) { } catch (Exception) { /* A locked update file can be cleaned at the next start. */ }
        };
        Activated += (_, _) => OfferPendingUpdate();
        Closed += (_, _) => { updateLifetime.Cancel(); updateLifetime.Dispose(); };
    }
    private async void CheckUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdates || previewOnly || closing || updateHandoff) return;
        checkingUpdates = true; UpdateButton.IsEnabled = false;
        try
        {
            using var client = new GitHubUpdates();
            var release = await client.CheckAsync(updateLifetime.Token);
            if (closing || updateLifetime.IsCancellationRequested) return;
            if (release is not null && release.Version > GitHubUpdates.CurrentVersion)
            {
                pendingRelease = release;
                if (manual && !IdleForUpdate) SetStatus("Có PhotoTone " + release.Version + ". Sẽ nhắc cập nhật sau khi xong việc.");
                OfferPendingUpdate();
            }
            else if (manual) MessageBox.Show(this, "Bạn đang dùng phiên bản PhotoTone mới nhất.", "Cập nhật PhotoTone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (manual && !updateLifetime.IsCancellationRequested && !closing) MessageBox.Show(this, "Chưa kiểm tra được cập nhật. " + ex.Message, "PhotoTone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { checkingUpdates = false; if (!updateLifetime.IsCancellationRequested) UpdateButton.IsEnabled = true; }
    }
    private async void OfferPendingUpdate()
    {
        if (pendingRelease is null || updateDialogOpen || updateHandoff || !IdleForUpdate || !IsActive || OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) return;
        var release = pendingRelease; pendingRelease = null; updateDialogOpen = true;
        PreparedUpdate? prepared = null;
        try
        {
            var window = new UpdateWindow(release) { Owner = this };
            if (window.ShowDialog() != true || window.Prepared is null) return;
            prepared = window.Prepared;
            if (!IdleForUpdate) throw new InvalidOperationException("Bàn làm việc đang bận. Hãy thử cập nhật sau.");
            Persist();
            updateStarting = true; IsEnabled = false;
            await UpdateInstaller.LaunchAsync(prepared);
            updateHandoff = true; Close();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            if (prepared is not null && !updateHandoff) UpdateInstaller.Cleanup(prepared.Directory);
            MessageBox.Show(this, ex.Message, "Không cập nhật được", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { updateStarting = false; updateDialogOpen = false; }
    }
    public void ShowUpdateOutcome(UpdateOutcome outcome)
    {
        SetStatus(outcome.Message);
        if (!outcome.Success) MessageBox.Show(this, outcome.Message, "Cập nhật PhotoTone", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
