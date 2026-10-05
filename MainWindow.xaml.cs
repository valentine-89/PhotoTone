using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PhotoTone.Core;

namespace PhotoTone;

public partial class MainWindow : Window
{
    public ObservableCollection<PhotoJob> Jobs { get; } = [];
    private readonly OpenRouterClient client = new();
    private readonly Settings settings;
    private IReadOnlyList<ImageModel> models = [];
    private CancellationTokenSource? batchCancellation;
    private bool showingResult;
    private bool initialized;
    private bool loadingModels;
    private string referencePath;
    private string credentialEndpoint;
    private bool closeAfterCancel;

    public MainWindow()
    {
        InitializeComponent();
        AppFiles.Initialize();
        settings = AppFiles.LoadSettings();
        referencePath = settings.ReferencePath;
        credentialEndpoint = settings.ApiBase.TrimEnd('/');
        ApiBaseBox.Text = settings.ApiBase;
        try { if (settings.KeyEndpoint == credentialEndpoint) KeyBox.Password = SecretStore.Unprotect(settings.EncryptedKey); }
        catch { StatusLabel.Text = "Không đọc được API key đã lưu. Vui lòng nhập lại."; }
        ModelBox.Text = settings.Model;
        ResolutionBox.ItemsSource = new[] { "Gốc", "4K", "2K", "1K" }; ResolutionBox.SelectedItem = settings.Resolution;
        ConcurrencyBox.ItemsSource = new[] { 1, 2, 3, 4 }; ConcurrencyBox.SelectedItem = settings.Concurrency;
        FormatBox.ItemsSource = new[] { "JPEG", "PNG" }; FormatBox.SelectedItem = settings.Format;
        PromptBox.Text = settings.Prompt; OutputBox.Text = settings.OutputDirectory;
        QueueList.ItemsSource = Jobs;
        LoadReference(); AddSamples();
        initialized = true;
    }
    private void SetStatus(string text) => StatusLabel.Text = text;
    private void EndpointChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        if (ApiBaseBox.Text.TrimEnd('/') != credentialEndpoint)
        {
            KeyBox.Clear(); credentialEndpoint = ApiBaseBox.Text.TrimEnd('/');
            models = []; ModelBox.ItemsSource = null;
            SetStatus("API URL đã đổi. Nhập key cho endpoint này và tải lại model.");
        }
    }
    private void AddImages(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }
    public void AddPaths(IEnumerable<string> paths)
    {
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path) || Jobs.Any(j => string.Equals(j.SourcePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
                if (!new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    throw new InvalidDataException("Định dạng ảnh chưa được hỗ trợ.");
                if (new FileInfo(path).Length > ImageFiles.MaxFileBytes) throw new InvalidDataException("Ảnh vượt 40 MB.");
                var size = ImageFiles.Size(path);
                Jobs.Add(new PhotoJob { SourcePath = Path.GetFullPath(path), Width = size.Width, Height = size.Height });
            }
            catch (Exception ex) { errors.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        CountLabel.Text = Jobs.Count.ToString();
        if (QueueList.SelectedItem is null && Jobs.Count > 0) QueueList.SelectedIndex = 0;
        SetStatus(errors.Count > 0 ? string.Join(" · ", errors.Take(3)) : $"{Jobs.Count} ảnh · Giữ nguyên kích thước ảnh gốc");
    }
    private void AddSamples() => AddPaths(new[] { "raw.jpg", "raw1.jpg", "raw2.jpg" }.Select(n => Path.Combine(AppFiles.Samples, n)));
    private void LoadSamples(object sender, RoutedEventArgs e) => AddSamples();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { showingResult = (QueueList.SelectedItem as PhotoJob)?.OutputPath is not null; UpdatePreview(); }
    private void ShowBefore(object sender, RoutedEventArgs e) { showingResult = false; UpdatePreview(); }
    private void ShowAfter(object sender, RoutedEventArgs e) { showingResult = true; UpdatePreview(); }
    private void UpdatePreview()
    {
        if (QueueList.SelectedItem is not PhotoJob job) { PreviewImage.Source = null; EmptyPreview.Visibility = Visibility.Visible; return; }
        var path = showingResult ? job.OutputPath : job.SourcePath;
        if (path is null) { SetStatus("Ảnh này chưa có kết quả."); showingResult = false; path = job.SourcePath; }
        try
        {
            PreviewImage.Source = ImageFiles.Load(path, 1500);
            EmptyPreview.Visibility = Visibility.Collapsed;
            PreviewTitle.Text = job.Name;
            PixelLabel.Text = ImageFiles.Size(path) + " px";
            ModeLabel.Text = showingResult ? "Kết quả" : "Ảnh gốc";
            BeforeButton.Opacity = showingResult ? .6 : 1;
            AfterButton.Opacity = showingResult ? 1 : .6;
        }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void LoadReference()
    {
        try { ReferenceImage.Source = ImageFiles.Load(referencePath, 560); ReferenceLabel.Text = Path.GetFileName(referencePath); }
        catch (Exception ex) { ReferenceImage.Source = null; ReferenceLabel.Text = "Chưa có ảnh mẫu"; SetStatus(ex.Message); }
    }
    private void ChangeReference(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp" };
        if (dialog.ShowDialog(this) == true) { referencePath = dialog.FileName; LoadReference(); }
    }
    private void ResetReference(object sender, RoutedEventArgs e) { referencePath = Path.Combine(AppFiles.Samples, "ok.jpg"); LoadReference(); }
    private void ResetPrompt(object sender, RoutedEventArgs e) => PromptBox.Text = AppFiles.DefaultPrompt;
    private void ChooseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục xuất ảnh", Multiselect = false };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }
    private void OpenOutput(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(OutputBox.Text); Process.Start(new ProcessStartInfo(OutputBox.Text) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void OpenSelected(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is not PhotoJob job) return;
        var path = showingResult && job.OutputPath is not null ? job.OutputPath : job.SourcePath;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void SelectAll(object sender, RoutedEventArgs e) { bool value = Jobs.Any(j => !j.Selected); foreach (var job in Jobs) job.Selected = value; }
    private void RemoveJob(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is PhotoJob job) Jobs.Remove(job);
        CountLabel.Text = Jobs.Count.ToString();
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = batchCancellation is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e) { if (batchCancellation is null && e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); }
    private async void RefreshModels(object sender, RoutedEventArgs e)
    {
        try { await LoadModelsAsync(); } catch (Exception ex) { SetStatus(ex.Message); }
    }
    private async Task LoadModelsAsync()
    {
        if (loadingModels) return;
        loadingModels = true; RefreshButton.IsEnabled = false;
        var selected = ModelBox.Text;
        var api = ApiBaseBox.Text.Trim();
        SetStatus("Đang tải danh sách model chỉnh ảnh…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var loaded = await client.ModelsAsync(api, timeout.Token);
            if (ApiBaseBox.Text.Trim() != api) return;
            models = loaded; ModelBox.ItemsSource = models; ModelBox.Text = selected;
            var match = models.FirstOrDefault(m => m.Id == selected);
            if (match is not null) ModelBox.SelectedItem = match;
            SetStatus($"{models.Count} model nhận ảnh gốc và ảnh mẫu.");
        }
        finally { loadingModels = false; RefreshButton.IsEnabled = true; }
    }
    private void ModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionBox is null || ModelBox.SelectedItem is not ImageModel model) return;
        var selected = ResolutionBox.SelectedItem as string ?? settings?.Resolution ?? "4K";
        var choices = new[] { "Gốc" }.Concat(model.Values("resolution")).Distinct().ToList();
        // Keep an explicit previous selection visible; preflight reports unsupported values instead of silently downgrading.
        if (!choices.Contains(selected)) choices.Add(selected);
        ResolutionBox.ItemsSource = choices; ResolutionBox.SelectedItem = selected;
    }
    private void CaptureSettings()
    {
        OpenRouterClient.Endpoint(ApiBaseBox.Text, "images");
        settings.ApiBase = ApiBaseBox.Text.Trim().TrimEnd('/');
        settings.Model = ModelBox.Text.Trim();
        settings.KeyEndpoint = settings.ApiBase;
        settings.EncryptedKey = SecretStore.Protect(KeyBox.Password.Trim());
        settings.Resolution = ResolutionBox.SelectedItem as string ?? "4K";
        settings.Concurrency = ConcurrencyBox.SelectedItem is int number ? number : 1;
        settings.Format = FormatBox.SelectedItem as string ?? "JPEG";
        settings.ReferencePath = referencePath; settings.Prompt = PromptBox.Text; settings.OutputDirectory = OutputBox.Text.Trim();
        AppFiles.SaveSettings(settings);
    }
    private void SaveConfig(object sender, RoutedEventArgs e)
    {
        try { CaptureSettings(); SetStatus("Đã lưu cấu hình. API key được mã hóa theo tài khoản Windows."); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void Busy(bool busy)
    {
        SettingsPanel.IsEnabled = AddButton.IsEnabled = SampleButton.IsEnabled = RemoveButton.IsEnabled = ToggleButton.IsEnabled = StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }
    private async void StartBatch(object sender, RoutedEventArgs e)
    {
        if (batchCancellation is not null) return;
        var selected = Jobs.Where(j => j.Selected && j.OutputPath is null).ToArray();
        if (selected.Length == 0) { SetStatus("Chọn ảnh chưa có kết quả để xử lý."); return; }
        if (string.IsNullOrWhiteSpace(KeyBox.Password)) { SetStatus("Nhập API key của bạn trước khi xử lý."); KeyBox.Focus(); return; }
        if (string.IsNullOrWhiteSpace(PromptBox.Text) || !File.Exists(referencePath)) { SetStatus("Cần prompt và ảnh mẫu hợp lệ."); return; }
        batchCancellation = new CancellationTokenSource(); Busy(true);
        try
        {
            if (models.Count == 0) await LoadModelsAsync();
            var model = models.FirstOrDefault(m => m.Id == ModelBox.Text.Trim()) ?? throw new InvalidOperationException("Model chưa có trong danh sách Image API. Chọn model và tải lại danh sách.");
            CaptureSettings();
            if (!Path.IsPathFullyQualified(settings.OutputDirectory)) throw new InvalidOperationException("Thư mục xuất phải là đường dẫn đầy đủ.");
            var options = new BatchOptions(settings.ApiBase, KeyBox.Password.Trim(), model, settings.Resolution, settings.Concurrency,
                settings.Format, referencePath, settings.Prompt, settings.OutputDirectory);
            foreach (var job in selected)
            {
                OpenRouterClient.RequestBody(options, new(job.Width, job.Height), "", ""); // capability preflight, no billable request
                job.State = "Trong hàng đợi"; job.Detail = ""; job.Cost = null;
            }
            int finished = 0;
            var progress = new Progress<JobUpdate>(update =>
            {
                update.Job.State = update.State; update.Job.Detail = update.Detail;
                if (update.Cost is not null) update.Job.Cost = update.Cost;
                if (update.OutputPath is not null) update.Job.OutputPath = update.OutputPath;
                if (update.State is "Hoàn tất" or "Lỗi" or "Đã dừng" or "Hết thời gian") finished++;
                BatchProgress.Value = 100.0 * finished / selected.Length;
                SetStatus($"{finished}/{selected.Length} ảnh · {update.Job.Name}: {update.State}");
                if (update.OutputPath is not null && QueueList.SelectedItem == update.Job) { showingResult = true; UpdatePreview(); }
            });
            SetStatus($"Đang xử lý {selected.Length} ảnh qua {model.Id}…");
            var token = batchCancellation.Token;
            await Task.Run(() => new BatchRunner(client).RunAsync(selected, options, progress, token));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var completed = selected.Count(j => j.OutputPath is not null);
            var cost = selected.Where(j => j.Cost is not null).Sum(j => j.Cost ?? 0);
            SetStatus($"Hoàn tất {completed}/{selected.Length} ảnh · Chi phí API đã báo: ${cost:0.0000} · {settings.OutputDirectory}");
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally
        {
            batchCancellation.Dispose(); batchCancellation = null; Busy(false);
            if (closeAfterCancel) Close();
        }
    }
    private void StopBatch(object sender, RoutedEventArgs e) { batchCancellation?.Cancel(); StopButton.IsEnabled = false; SetStatus("Đang dừng…"); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (batchCancellation is not null) { e.Cancel = true; closeAfterCancel = true; batchCancellation.Cancel(); return; }
        try { if (initialized) CaptureSettings(); } catch { /* Do not overwrite valid saved settings on a failed save. */ }
        client.Dispose();
    }
}
