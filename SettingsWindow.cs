using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PhotoTone.Core;

namespace PhotoTone;

public sealed class SettingsWindow : Window
{
    private readonly TextBox endpoint = new();
    private readonly PasswordBox key = new();
    private readonly ComboBox model = new() { IsEditable = true, IsTextSearchEnabled = false };
    private readonly ComboBox resolution = new();
    private readonly ComboBox concurrency = new() { ItemsSource = new[] { 1, 2, 3, 4 } };
    private readonly TextBox reference = new() { IsReadOnly = true };
    private readonly TextBox prompt = new() { Height = 160, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    public bool AddSamples { get; private set; }
    public string ApiKey => key.Password.Trim();
    public Settings SavedSettings { get; private set; } = new();
    public SettingsWindow(Settings settings, string apiKey)
    {
        // A cancelled/failed save must not change the endpoint while retaining the old in-memory key.
        settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        Style = (Style)FindResource(typeof(Window));
        Title = "Cấu hình PhotoTone"; Width = 610; Height = 780; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        endpoint.Text = settings.ApiBase; key.Password = apiKey; model.Text = settings.Model;
        resolution.ItemsSource = new[] { "Gốc", "4K", "2K", "1K", "512" }; resolution.SelectedItem = settings.Resolution;
        concurrency.SelectedItem = settings.Concurrency; reference.Text = settings.ReferencePath; prompt.Text = settings.Prompt;
        Label(panel, "API URL", endpoint); Label(panel, "API key", key); Label(panel, "Model", model);
        var refresh = new Button { Content = "Tải model", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(refresh);
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false; var requestedEndpoint = endpoint.Text.Trim();
            try
            {
                using var client = new OpenRouterClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var models = await client.ModelsAsync(requestedEndpoint, timeout.Token);
                if (requestedEndpoint != endpoint.Text.Trim()) return;
                var selected = model.Text; model.ItemsSource = models; model.SelectedItem = models.FirstOrDefault(m => m.Id == selected); model.Text = selected;
                status.Text = $"{models.Count} model";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { refresh.IsEnabled = true; }
        };
        endpoint.TextChanged += (_, _) => { key.Clear(); model.ItemsSource = null; };
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedItem is not ImageModel selected) return;
            var old = resolution.SelectedItem as string; var values = new[] { "Gốc" }.Concat(selected.Values("resolution")).Distinct().ToArray();
            resolution.ItemsSource = values; resolution.SelectedItem = values.Contains(old) ? old : null;
        };
        Label(panel, "Độ phân giải AI", resolution); Label(panel, "Số ảnh đồng thời", concurrency); Label(panel, "Ảnh mẫu hoàn thiện", reference);
        var choose = new Button { Content = "Đổi ảnh mẫu", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) }; panel.Children.Add(choose);
        choose.Click += (_, _) => { var picker = new OpenFileDialog { Filter = PhotoImporter.Filter }; if (picker.ShowDialog(this) == true) reference.Text = picker.FileName; };
        Label(panel, "Prompt ban đầu", prompt);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(actions);
        var reset = new Button { Content = "Prompt mặc định" }; reset.Click += (_, _) => prompt.Text = AppFiles.DefaultPrompt; actions.Children.Add(reset);
        var samples = new Button { Content = "Thêm bộ ảnh mẫu" }; samples.Click += (_, _) => { AddSamples = true; status.Text = "Bộ mẫu sẽ được thêm khi lưu."; }; actions.Children.Add(samples);
        var save = new Button { Content = "Lưu", Style = (Style)FindResource("Primary") }; actions.Children.Add(save); panel.Children.Add(status);
        save.Click += (_, _) =>
        {
            try
            {
                OpenRouterClient.Endpoint(endpoint.Text, "images");
                if (resolution.SelectedItem is not string selected) throw new InvalidOperationException("Chọn độ phân giải.");
                var protectedKey = SecretStore.Protect(ApiKey);
                settings.ApiBase = endpoint.Text.Trim().TrimEnd('/'); settings.KeyEndpoint = settings.ApiBase; settings.EncryptedKey = protectedKey;
                settings.Model = model.Text.Trim(); settings.Resolution = selected; settings.Concurrency = (int)(concurrency.SelectedItem ?? 1);
                settings.ReferencePath = reference.Text; settings.Prompt = prompt.Text;
                AppFiles.SaveSettings(settings); SavedSettings = settings; DialogResult = true;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
    }
    private static void Label(Panel panel, string title, UIElement input)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 9, 0, 5) }); panel.Children.Add(input);
    }
}
