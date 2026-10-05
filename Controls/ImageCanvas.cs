using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoTone.Core;

namespace PhotoTone.Controls;

public sealed class ImageCanvas : FrameworkElement
{
    public BitmapSource? Bitmap { get; private set; }
    public List<ImageRegion> Regions { get; } = new();
    public string Prefix { get; set; } = "T";
    public bool Editing { get; set; }
    public bool DrawMode { get; set; } = true;
    public event Action? RegionsChanged;
    private double scale = 1;
    private Vector offset;
    private bool fit = true;
    private Point down;
    private Vector oldOffset;
    private ImageRegion? original;
    private int selected = -1;
    private int corner = -1;
    private string gesture = "";
    public string? LoadError { get; private set; }
    public Task Loading { get; private set; } = Task.CompletedTask;
    private int generation;
    private string? imagePath;
    public ImageCanvas()
    {
        Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross;
        SizeChanged += (_, _) => { if (fit) Fit(); else InvalidateVisual(); };
        Loaded += (_, _) => { if (imagePath is not null && Bitmap is null) Loading = LoadAsync(imagePath, ++generation); };
        Unloaded += (_, _) => { generation++; Bitmap = null; };
    }
    public void Load(string path)
    {
        imagePath = path; Bitmap = null;
        var version = ++generation;
        if (IsLoaded) Loading = LoadAsync(path, version);
    }
    private async Task LoadAsync(string path, int version)
    {
        try
        {
            var bitmap = await Task.Run(() => ImageFiles.Load(path));
            if (version != generation) return;
            Bitmap = bitmap; LoadError = null; if (fit) Fit(); else InvalidateVisual();
        }
        catch (Exception ex) { if (version == generation) { Bitmap = null; LoadError = ex.Message; InvalidateVisual(); } }
    }
    public void Fit()
    {
        fit = true;
        if (Bitmap is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        scale = Math.Min(ActualWidth / Bitmap.PixelWidth, ActualHeight / Bitmap.PixelHeight);
        Center();
    }
    public void ActualPixels()
    {
        fit = false;
        scale = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        Center();
    }
    private void Center()
    {
        if (Bitmap is null) return;
        offset = new((ActualWidth - Bitmap.PixelWidth * scale) / 2, (ActualHeight - Bitmap.PixelHeight * scale) / 2);
        InvalidateVisual();
    }
    public static Point ToNormalized(Point point, Vector offset, double scale, ImageSize size) => new(
        Math.Clamp((point.X - offset.X) / (scale * size.Width), 0, 1), Math.Clamp((point.Y - offset.Y) / (scale * size.Height), 0, 1));
    private Point Normal(Point point) => Bitmap is null ? new() : ToNormalized(point, offset, scale, new(Bitmap.PixelWidth, Bitmap.PixelHeight));
    private Rect Display(ImageRegion r) => new(offset.X + r.X * Bitmap!.PixelWidth * scale, offset.Y + r.Y * Bitmap.PixelHeight * scale,
        r.Width * Bitmap.PixelWidth * scale, r.Height * Bitmap.PixelHeight * scale);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 15, 20)), null, new Rect(RenderSize));
        if (Bitmap is null)
        {
            var text = new FormattedText(LoadError ?? "Đang tải ảnh…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            text.MaxTextWidth = Math.Max(1, ActualWidth - 24); dc.DrawText(text, new Point(12, 12)); return;
        }
        dc.DrawImage(Bitmap, new Rect(offset.X, offset.Y, Bitmap.PixelWidth * scale, Bitmap.PixelHeight * scale));
        for (int i = 0; i < Regions.Count; i++)
        {
            var rect = Display(Regions[i]); var brush = i == selected ? Brushes.Yellow : Brushes.Tomato;
            dc.DrawRectangle(null, new Pen(brush, 2), rect);
            var text = new FormattedText(Prefix + (i + 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRectangle(Brushes.DarkRed, null, new Rect(rect.X, rect.Y, text.Width + 8, text.Height + 2)); dc.DrawText(text, new Point(rect.X + 4, rect.Y));
            if (i == selected) foreach (var p in Corners(rect)) dc.DrawRectangle(brush, null, new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
    }
    private static Point[] Corners(Rect r) => [r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft];
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Bitmap is null || IsMouseCaptured) return;
        var point = e.GetPosition(this); var pixel = (point - (Point)offset) / scale;
        var next = Math.Clamp(scale * (e.Delta > 0 ? 1.2 : 1 / 1.2), .01, 16);
        offset = (Vector)point - pixel * next; scale = next; fit = false; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (Bitmap is null) return;
        Focus(); down = e.GetPosition(this); oldOffset = offset; corner = -1;
        if (!Editing || !DrawMode || e.ChangedButton != MouseButton.Left || Keyboard.IsKeyDown(Key.Space)) gesture = "pan";
        else
        {
            var bounds = new Rect(offset.X, offset.Y, Bitmap.PixelWidth * scale, Bitmap.PixelHeight * scale);
            if (!bounds.Contains(down)) return;
            if (selected >= 0 && selected < Regions.Count)
            {
                var corners = Corners(Display(Regions[selected]));
                corner = Array.FindIndex(corners, p => (p - down).Length <= 10);
            }
            if (corner >= 0) { gesture = "resize"; original = Regions[selected]; }
            else
            {
                selected = Regions.FindLastIndex(r => Display(r).Contains(down));
                if (selected >= 0) { gesture = "move"; original = Regions[selected]; }
                else { gesture = "new"; var p = Normal(down); Regions.Add(new(p.X, p.Y, 0, 0)); selected = Regions.Count - 1; }
            }
        }
        CaptureMouse(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || Bitmap is null) return;
        var point = e.GetPosition(this);
        if (gesture == "pan") { offset = oldOffset + point - down; fit = false; }
        else
        {
            var p = Normal(point); var start = Normal(down);
            if (gesture == "new") Regions[selected] = FromPoints(start, p);
            else if (gesture == "move" && original is { } r)
                Regions[selected] = r with { X = Math.Clamp(r.X + p.X - start.X, 0, 1 - r.Width), Y = Math.Clamp(r.Y + p.Y - start.Y, 0, 1 - r.Height) };
            else if (gesture == "resize" && original is { } box)
            {
                var anchors = new[] { new Point(box.X + box.Width, box.Y + box.Height), new Point(box.X, box.Y + box.Height), new Point(box.X, box.Y), new Point(box.X + box.Width, box.Y) };
                Regions[selected] = FromPoints(anchors[corner], p);
            }
        }
        InvalidateVisual(); e.Handled = true;
    }
    public static ImageRegion FromPoints(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (selected >= 0 && Bitmap is not null && (Regions[selected].Width * Bitmap.PixelWidth < 2 || Regions[selected].Height * Bitmap.PixelHeight < 2)) { Regions.RemoveAt(selected); selected = -1; }
        if (gesture != "pan") RegionsChanged?.Invoke(); gesture = ""; InvalidateVisual(); e.Handled = true;
    }
    public void DeleteRegion()
    {
        if (selected < 0 || selected >= Regions.Count) return;
        Regions.RemoveAt(selected); selected = -1; RegionsChanged?.Invoke(); InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Delete && Editing) { DeleteRegion(); e.Handled = true; } base.OnKeyDown(e); }
}
