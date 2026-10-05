using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PhotoTone.Core;

namespace PhotoTone;

public partial class MainWindow : Window
{
    public ObservableCollection<PhotoJob> Jobs { get; } = [];
    private readonly OpenRouterClient client = new();
    private Settings settings;
    private readonly WorkspaceStore? workspace;
    private readonly BatchRunner runner;
    private readonly bool previewOnly;
    private string apiKey = "";
    private int importing;
    private bool preparing, exporting, closing, initialized;
    private CancellationTokenSource? preparationCancel;
    private Task exportTask = Task.CompletedTask;
    private readonly HashSet<Task> importTasks = new();
    private CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim importGate = new(1);
    public MainWindow(bool previewOnly = false)
    {
        this.previewOnly = previewOnly;
        InitializeComponent();
        runner = new BatchRunner(ProcessItemAsync); runner.Changed += UpdateControls;
        runner.Failed += (item, ex) => FailItem(item, ex);
        AppFiles.Initialize();
        settings = previewOnly ? new Settings { ReferencePath = Path.Combine(AppFiles.Samples, "ok.jpg"), Prompt = AppFiles.DefaultPrompt } : AppFiles.LoadSettings();
        if (!previewOnly)
        {
            workspace = new WorkspaceStore();
            try
            {
                foreach (var job in workspace.Load()) Jobs.Add(job);
                workspace.Save(Jobs); workspace.Prune(Jobs);
                try { if (settings.KeyEndpoint == settings.ApiBase.TrimEnd('/')) apiKey = SecretStore.Unprotect(settings.EncryptedKey); }
                catch { SetStatus("Không đọc được API key đã lưu. Nhập lại trong Cấu hình."); }
            }
            catch { workspace.Dispose(); throw; }
        }
        else
        {
            foreach (var name in new[] { "raw.jpg", "raw1.jpg", "raw2.jpg" })
            {
                var path = Path.Combine(AppFiles.Samples, name); var size = ImageFiles.Size(path);
                Jobs.Add(new PhotoJob { SourcePath = path, Width = size.Width, Height = size.Height, State = "Hoàn tất", Current = new ResultVersion { Path = Path.Combine(AppFiles.Samples, "ok.jpg"), ProviderSize = size, Attempt = new() } });
            }
        }
        QueueList.ItemsSource = Jobs;
        runner.Concurrency = settings.Concurrency; initialized = true; UpdateControls();
    }
    private void SetStatus(string text) => StatusLabel.Text = text;
    private void Persist() { if (!previewOnly) workspace!.Save(Jobs); }
    private void SafePersist() { try { Persist(); } catch (Exception ex) { SetStatus("Không lưu được bàn làm việc: " + ex.Message); } }
    private void UpdateControls()
    {
        if (!initialized) return;
        CountLabel.Text = $"{Jobs.Count} ảnh · {Jobs.Count(j => j.Current is not null)} kết quả";
        EmptyLabel.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueLabel.Text = runner.IsBusy ? $"{runner.Active} đang chạy · {runner.Pending} chờ · {runner.Finished}/{runner.Total}" : $"Tối đa {settings.Concurrency} ảnh đồng thời";
        BatchProgress.Value = runner.Total == 0 ? 0 : 100.0 * runner.Finished / runner.Total;
        StartButton.IsEnabled = !preparing && !runner.Stopping && !closing && !previewOnly;
        StopButton.IsEnabled = preparing || runner.IsBusy;
        CleanButton.IsEnabled = !runner.IsBusy && !preparing && !exporting && importing == 0 && !closing && !previewOnly;
        ConfigButton.IsEnabled = !runner.IsBusy && !preparing && !closing && !previewOnly;
        ExportButton.IsEnabled = !exporting && !closing && !previewOnly && Jobs.Any(j => j.Current is not null);
    }
    private void SelectionUpdated(object sender, RoutedEventArgs e) { if (initialized) SafePersist(); }
    private void SelectAll(object sender, RoutedEventArgs e) { bool value = Jobs.Any(j => !j.Selected); foreach (var job in Jobs) job.Selected = value; SafePersist(); }
    private void AddImages(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = PhotoImporter.Filter, Multiselect = true };
        if (picker.ShowDialog(this) == true) AddPaths(picker.FileNames);
    }
    private void AddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Chọn thư mục ảnh", Multiselect = true };
        if (picker.ShowDialog(this) == true) AddPaths(picker.FolderNames);
    }
    public async void AddPaths(IEnumerable<string> paths)
    {
        if (closing || previewOnly) return;
        var task = ImportAsync(paths.ToArray()); importTasks.Add(task);
        try { await task; } finally { importTasks.Remove(task); }
    }
    private async Task ImportAsync(string[] paths)
    {
        importing++; UpdateControls(); SetStatus("Đang đọc ảnh…"); bool entered = false;
        try
        {
            await importGate.WaitAsync(lifetime.Token); entered = true;
            var existing = Jobs.Select(j => j.SourcePath).ToArray();
            var result = await Task.Run(() => PhotoImporter.Import(paths, existing, lifetime.Token));
            var seen = Jobs.Select(j => j.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var job in result.Jobs)
            {
                if (seen.Add(job.SourcePath)) { Jobs.Add(job); added++; }
                if (added % 40 == 0) await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
            Persist();
            SetStatus($"Đã thêm {added} ảnh" + (result.Errors.Count > 0 ? $" · {result.Errors.Count} mục bị bỏ qua: {string.Join(" · ", result.Errors.Take(2))}" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { if (entered) importGate.Release(); importing--; UpdateControls(); }
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !closing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); }
    private async void Configure(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(settings, apiKey) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings = dialog.SavedSettings; apiKey = dialog.ApiKey; runner.Concurrency = settings.Concurrency; UpdateControls();
        if (dialog.AddSamples) await ImportAsync(new[] { "raw.jpg", "raw1.jpg", "raw2.jpg" }.Select(n => Path.Combine(AppFiles.Samples, n)).ToArray());
    }
    private async Task<BatchOptions> OptionsAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Nhập API key trong Cấu hình.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var models = await client.ModelsAsync(settings.ApiBase, timeout.Token);
        var model = models.FirstOrDefault(m => m.Id == settings.Model) ?? throw new InvalidOperationException("Model không có trong Image API. Chọn lại trong Cấu hình.");
        return new(settings.ApiBase, apiKey, model, settings.Resolution, settings.Concurrency, "PNG", settings.ReferencePath, settings.Prompt, workspace!.Root);
    }
    private async void StartBatch(object sender, RoutedEventArgs e)
    {
        var jobs = Jobs.Where(j => j.Selected && j.Current is null && !j.Busy).ToArray();
        if (jobs.Length == 0) { SetStatus("Chọn ảnh chưa có kết quả để xử lý."); return; }
        await PrepareAsync(jobs, null);
    }
    private async Task PrepareAsync(PhotoJob[] jobs, EditDraft? draft, BatchOptions? knownOptions = null)
    {
        if (preparing || runner.Stopping || closing || previewOnly) return;
        preparing = true; preparationCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = preparationCancel.Token; foreach (var job in jobs) job.Busy = true; UpdateControls();
        try
        {
            var options = knownOptions ?? await OptionsAsync(token);
            var items = new List<WorkItem>(); var failures = new List<string>();
            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var input = draft is null ? job.SourcePath : job.OutputPath ?? throw new IOException("Ảnh chưa có kết quả.");
                    var parent = job.Current?.Id;
                    var item = await Task.Run(() =>
                    {
                        var value = new WorkItem(job, options, draft, input, FileStamp.Read(input), parent, draft is null ? FileStamp.Read(options.ReferencePath) : null);
                        EditComposer.Validate(value); return value;
                    }, token);
                    // Preflight decoding/encoding catches damaged references and >40 MB PNG before ANY API call in this submission.
                    await StaWork.Run(() => { EditComposer.Compose(item); return true; }, token);
                    items.Add(item);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { job.State = "Chưa thể xử lý"; job.Detail = ex.Message; failures.Add(job.Name); }
            }
            token.ThrowIfCancellationRequested();
            if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} ảnh chưa hợp lệ; lượt này chưa gửi ảnh nào tới API.");
            foreach (var item in items)
            {
                item.Job.State = "Trong hàng đợi"; item.Job.Detail = "";
                item.Job.LastAttempt = new AttemptInfo { Kind = draft is null ? "initial" : "refine", Model = options.Model.Id, Resolution = options.Resolution, Prompt = draft?.Prompt ?? options.Prompt, Draft = draft, ParentVersion = item.ParentVersion,
                    InputSha256 = item.InputStamp.Sha256, ReferencePath = draft is null ? options.ReferencePath : null, ReferenceStamp = item.InitialReferenceStamp };
            }
            Persist(); runner.Enqueue(items); SetStatus($"Đã xếp {items.Count} ảnh vào hàng đợi.");
        }
        catch (OperationCanceledException) { SetStatus("Đã dừng chuẩn bị ảnh."); }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally
        {
            foreach (var job in jobs) if (!runner.Contains(job.Id)) { job.Busy = false; if (job.LastAttempt?.Status == "queued") job.LastAttempt.Status = "cancelled"; }
            preparationCancel.Dispose(); preparationCancel = null; preparing = false; SafePersist(); UpdateControls();
        }
    }
    private async Task ProcessItemAsync(WorkItem item, CancellationToken token)
    {
        var job = item.Job; var attempt = job.LastAttempt!; var path = workspace!.NewResultPath();
        using var held = workspace.Pin(new[] { path }.Concat(item.Draft is null ? [] : new[] { item.InputPath }));
        try
        {
            if (job.Current?.Id != item.ParentVersion) throw new IOException("Phiên bản ảnh đã thay đổi. Hãy mở lại chỉnh bổ sung.");
            attempt.Status = "running"; job.State = "Chuẩn bị ảnh"; Persist();
            var prepared = await StaWork.Run(() => EditComposer.Compose(item), token); token.ThrowIfCancellationRequested();
            job.State = "AI đang xử lý";
            var result = await Task.Run(() => client.EditAsync(item.Options, new(job.Width, job.Height), prepared, token), token);
            attempt.Cost = result.Cost; attempt.RequestId = result.RequestId;
            if (result.Cost is not null) job.Cost = (job.Cost ?? 0) + result.Cost;
            token.ThrowIfCancellationRequested(); job.State = "Kiểm tra pixel";
            var size = await Task.Run(() => ImageFiles.Export(result.Bytes, new(job.Width, job.Height), path, "PNG"), token);
            token.ThrowIfCancellationRequested();
            attempt.Status = "completed"; attempt.FinishedUtc = DateTimeOffset.UtcNow;
            workspace.Commit(job, new ResultVersion { Path = path, ProviderSize = size, Attempt = attempt }, Jobs);
            SetStatus($"{job.Name}: hoàn tất.");
        }
        finally { job.Busy = false; }
    }
    private void FailItem(WorkItem item, Exception ex)
    {
        item.Job.Busy = false;
        item.Job.State = ex is OperationCanceledException ? (runner.Stopping ? "Đã dừng" : "Hết thời gian") : "Lỗi";
        item.Job.Detail = ex is OperationCanceledException ? "Không tự gửi lại yêu cầu." : ex.Message;
        if (item.Job.LastAttempt is { } attempt) { attempt.Status = ex is OperationCanceledException ? "cancelled" : "failed"; attempt.FinishedUtc = DateTimeOffset.UtcNow; }
        SafePersist(); workspace?.Prune(Jobs); SetStatus($"{item.Job.Name}: {item.Job.Detail}");
    }
    private void StopBatch(object sender, RoutedEventArgs e) => Stop();
    private void Stop()
    {
        preparationCancel?.Cancel();
        foreach (var item in runner.Stop()) FailItem(item, new OperationCanceledException());
        SetStatus("Đang dừng…"); UpdateControls();
    }
    private static PhotoJob? Job(object sender) => (sender as FrameworkElement)?.DataContext as PhotoJob;
    private void ViewSource(object sender, MouseButtonEventArgs e) { if (Job(sender) is { } job) ShowImage(job.SourcePath); }
    private void ViewResult(object sender, MouseButtonEventArgs e) { if (Job(sender)?.OutputPath is { } path) ShowImage(path); }
    private void ShowImage(string path) { var view = new EditWindow(path) { Owner = this }; view.ShowDialog(); }
    private async void Refine(object sender, RoutedEventArgs e)
    {
        if (Job(sender) is not { CanEdit: true } job || preparing || previewOnly || closing) return;
        try
        {
            preparing = true; UpdateControls();
            preparationCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); preparationCancel.CancelAfter(TimeSpan.FromSeconds(25));
            var options = await OptionsAsync(preparationCancel.Token);
            preparationCancel.Token.ThrowIfCancellationRequested();
            preparationCancel.Dispose(); preparationCancel = null;
            preparing = false; UpdateControls();
            var retry = job.LastAttempt is { Status: not "completed", Draft: not null } attempt && attempt.ParentVersion == job.Current?.Id ? attempt.Draft : null;
            var dialog = new EditWindow(job.OutputPath!, true, options.Model.MaxReferences, retry) { Owner = this };
            if (dialog.ShowDialog() == true) await PrepareAsync([job], dialog.Draft!, options);
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { preparationCancel?.Dispose(); preparationCancel = null; preparing = false; UpdateControls(); }
    }
    private void Undo(object sender, RoutedEventArgs e)
    {
        if (previewOnly || Job(sender) is not { CanUndo: true } job) return;
        try { workspace!.Undo(job, Jobs); UpdateControls(); SetStatus("Đã hoàn tác " + job.Name); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void RemoveJob(object sender, RoutedEventArgs e)
    {
        if (previewOnly || Job(sender) is not { Busy: false } job || preparing || exporting || closing) return;
        if (job.Current is { Exported: false } && MessageBox.Show(this, "Ảnh này chưa xuất. Bỏ khỏi bàn làm việc?", "PhotoTone", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { workspace!.Save(Jobs.Where(j => j != job)); Jobs.Remove(job); workspace.Prune(Jobs); UpdateControls(); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void CleanWorkspace(object sender, RoutedEventArgs e)
    {
        if (!CleanButton.IsEnabled) return;
        if (Jobs.Any(j => j.Current is { Exported: false }) && MessageBox.Show(this, "Có kết quả chưa xuất. Xóa bàn làm việc?", "PhotoTone", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { workspace!.Save([]); Jobs.Clear(); workspace.Prune(Jobs); UpdateControls(); SetStatus(workspace.CleanupWarning ?? "Đã dọn bàn làm việc."); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private async void ExportAll(object sender, RoutedEventArgs e)
    {
        if (!ExportButton.IsEnabled) return;
        exportTask = ExportAsync(); await exportTask;
    }
    private async Task ExportAsync()
    {
        var snapshot = ExportService.Snapshot(Jobs); if (snapshot.Length == 0) return;
        exporting = true; UpdateControls();
        using var pin = workspace!.Pin(snapshot.Select(s => s.Version.Path));
        try
        {
            var dialog = new ExportWindow(settings.Format) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var picker = new OpenFolderDialog { Title = "Xuất tất cả kết quả", InitialDirectory = Directory.Exists(settings.OutputDirectory) ? settings.OutputDirectory : "" };
            if (picker.ShowDialog(this) != true) return;
            settings.Format = dialog.Format; settings.OutputDirectory = picker.FolderName; AppFiles.SaveSettings(settings);
            var outcomes = await Task.Run(() => ExportService.Export(snapshot, picker.FolderName, dialog.Format));
            foreach (var outcome in outcomes.Where(o => o.Error is null)) outcome.Item.Version.Exported = true;
            Persist(); foreach (var job in Jobs) job.RefreshResult();
            var errors = outcomes.Where(o => o.Error is not null).ToArray();
            SetStatus($"Đã xuất {outcomes.Length - errors.Length}/{outcomes.Length} ảnh" + (errors.Length > 0 ? " · " + errors[0].Error : " · " + picker.FolderName));
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { pin.Dispose(); workspace.Prune(Jobs); exporting = false; UpdateControls(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) { e.Cancel = true; return; }
        if (runner.IsBusy || preparing || exporting || importing > 0)
        {
            e.Cancel = true; closing = true; lifetime.Cancel(); Stop(); UpdateControls();
            await runner.WhenIdle; await exportTask;
            while (preparing || importing > 0) await Task.Delay(50);
            closing = false; Close(); return;
        }
        try { if (initialized) Persist(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); }
            SetStatus(ex.Message); UpdateControls(); return;
        }
        workspace?.Dispose(); lifetime.Dispose(); importGate.Dispose(); client.Dispose();
    }
}

internal sealed class ExportWindow : Window
{
    private readonly ComboBox format = new() { ItemsSource = new[] { "JPEG", "PNG" } };
    public string Format => format.SelectedItem as string ?? "JPEG";
    public ExportWindow(string selected)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Xuất tất cả"; Width = 280; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Định dạng", Margin = new Thickness(0, 0, 0, 8) }); format.SelectedItem = selected; panel.Children.Add(format);
        var button = new Button { Content = "Chọn thư mục", Margin = new Thickness(0, 12, 0, 0), Style = (Style)FindResource("Primary") }; button.Click += (_, _) => DialogResult = true; panel.Children.Add(button);
    }
}
