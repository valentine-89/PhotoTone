using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PhotoTone.Controls;
using PhotoTone.Core;

namespace PhotoTone;

public sealed class EditWindow : Window
{
    private readonly TabControl tabs = new() { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBox prompt = new() { Height = 76, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { Margin = new Thickness(6), Foreground = System.Windows.Media.Brushes.Tomato };
    private readonly List<(string Path, FileStamp Stamp, ImageCanvas Canvas)> references = new();
    private readonly ImageCanvas target;
    private readonly bool editing;
    private readonly int maxReferences;
    private readonly Button submit;
    private bool adding;
    public EditDraft? Draft { get; private set; }
    public IEnumerable<ImageCanvas> Canvases => new[] { target }.Concat(references.Select(r => r.Canvas));
    public EditWindow(string path, bool editing = false, int maxReferences = 1, EditDraft? initial = null)
    {
        this.editing = editing; this.maxReferences = maxReferences;
        Style = (Style)FindResource(typeof(Window));
        Title = editing ? "Chỉnh bổ sung · " + Path.GetFileName(path) : Path.GetFileName(path);
        Width = 1200; Height = 850; MinWidth = 850; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(12) }; Content = layout;
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        AddButton(toolbar, "Vừa cửa sổ", () => Active()?.Fit());
        AddButton(toolbar, "100%", () => Active()?.ActualPixels());
        if (editing)
        {
            AddButton(toolbar, "Vẽ / chọn box", () => { if (Active() is { } canvas) canvas.DrawMode = true; });
            AddButton(toolbar, "Di chuyển ảnh", () => { if (Active() is { } canvas) canvas.DrawMode = false; });
            AddButton(toolbar, "Xóa box", () => Active()?.DeleteRegion());
            AddButton(toolbar, "Thêm ảnh", AddReference);
            AddButton(toolbar, "Bỏ ảnh", RemoveReference);
        }
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        submit = new Button { Content = "Xử lý", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        submit.Click += (_, _) => Submit();
        if (editing) { bottom.Children.Add(prompt); bottom.Children.Add(status); bottom.Children.Add(submit); prompt.Text = initial?.Prompt ?? ""; }
        layout.Children.Add(tabs);
        target = new ImageCanvas { Editing = editing, Prefix = "T" }; target.Load(path);
        if (initial is not null) target.Regions.AddRange(initial.Targets);
        target.RegionsChanged += UpdateCount;
        tabs.Items.Add(new TabItem { Header = editing ? "Kết quả · T" : "Ảnh", Content = target }); tabs.SelectedIndex = 0;
        if (initial is not null)
            foreach (var reference in initial.References) Attach(reference.Path, reference.Stamp, reference.Regions);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        UpdateCount();
    }
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text }; button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private ImageCanvas? Active() => (tabs.SelectedItem as TabItem)?.Content as ImageCanvas;
    private async void AddReference()
    {
        if (adding) return;
        var picker = new OpenFileDialog { Filter = PhotoImporter.Filter, Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        adding = true; submit.IsEnabled = false;
        try
        {
            foreach (var path in picker.FileNames)
            {
                try
                {
                    var stamp = await Task.Run(() => { var value = FileStamp.Read(path); ImageFiles.Size(path); return value; });
                    Attach(path, stamp, []);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không thêm được ảnh", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
        }
        finally { adding = false; UpdateCount(); }
    }
    private void Attach(string path, FileStamp stamp, ImageRegion[] regions)
    {
        var canvas = new ImageCanvas { Editing = true, Prefix = $"A{references.Count + 1}." }; canvas.Load(path); canvas.Regions.AddRange(regions); canvas.RegionsChanged += UpdateCount;
        references.Add((path, stamp, canvas));
        tabs.Items.Add(new TabItem { Header = $"A{references.Count} · {Path.GetFileName(path)}", Content = canvas });
        UpdateCount();
    }
    private void RemoveReference()
    {
        if (tabs.SelectedIndex < 1 || adding) return;
        var index = tabs.SelectedIndex - 1; references.RemoveAt(index); tabs.Items.RemoveAt(index + 1);
        for (int i = 0; i < references.Count; i++)
        {
            references[i].Canvas.Prefix = $"A{i + 1}."; references[i].Canvas.InvalidateVisual();
            ((TabItem)tabs.Items[i + 1]).Header = $"A{i + 1} · {Path.GetFileName(references[i].Path)}";
        }
        UpdateCount();
    }
    private EditDraft Capture() => new(prompt.Text.Trim(), target.Regions.ToArray(), references.Select(r => new ReferenceSelection(r.Path, r.Stamp, r.Canvas.Regions.ToArray())).ToArray());
    private void UpdateCount()
    {
        if (!editing || target is null) return;
        var count = EditComposer.ReferenceCount(Capture());
        Message(count > maxReferences ? $"{count} ảnh đầu vào / giới hạn {maxReferences}" : "");
        submit.IsEnabled = !adding && count <= maxReferences;
    }
    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(prompt.Text)) { Message("Nhập yêu cầu chỉnh ảnh."); prompt.Focus(); return; }
        if (target.LoadError is not null || references.Any(r => r.Canvas.LoadError is not null)) { Message("Không đọc được ảnh. Kiểm tra lại file đã chọn."); return; }
        Draft = Capture();
        try { foreach (var r in Draft.Targets.Concat(Draft.References.SelectMany(r => r.Regions))) r.Validate(); }
        catch (Exception ex) { Message(ex.Message); return; }
        DialogResult = true;
    }
    private void Message(string text) { status.Text = text; status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
}
