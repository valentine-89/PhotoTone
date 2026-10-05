using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTone.Core;

public static class EditComposer
{
    public static int ReferenceCount(EditDraft? draft) => draft is null ? 2 : 1 + (draft.Targets.Length > 0 ? 1 : 0) + draft.References.Sum(r => Math.Max(1, r.Regions.Length));
    public static void Validate(WorkItem item)
    {
        OpenRouterClient.Endpoint(item.Options.ApiBase, "images");
        ResolutionPlan.Create(item.Options.Model, item.Options.Resolution, new(item.Job.Width, item.Job.Height));
        OpenRouterClient.ValidateReferences(item.Options.Model, ReferenceCount(item.Draft));
        if (string.IsNullOrWhiteSpace(item.Draft?.Prompt ?? item.Options.Prompt)) throw new InvalidOperationException("Nhập prompt trước khi xử lý.");
        item.Job.SourceStamp?.Verify(item.Job.SourcePath);
        item.InputStamp.Verify(item.InputPath);
        if (item.Draft is { } draft)
        {
            foreach (var region in draft.Targets) region.Validate();
            foreach (var reference in draft.References)
            {
                reference.Stamp.Verify(reference.Path);
                foreach (var region in reference.Regions) region.Validate();
            }
        }
        else
        {
            if (!File.Exists(item.Options.ReferencePath)) throw new IOException("Cần ảnh mẫu hợp lệ.");
            item.InitialReferenceStamp?.Verify(item.Options.ReferencePath);
        }
    }
    public static PreparedEdit Compose(WorkItem item)
    {
        Validate(item);
        var images = new List<RequestImage> { new(ImageFiles.DataUrl(item.InputPath), "Ảnh chính sạch, dùng làm nền cho kết quả") };
        if (item.Draft is not { } draft)
        {
            images.Add(new(ImageFiles.DataUrl(item.Options.ReferencePath), "Mẫu màu/ánh sáng"));
            return new(item.Options.Prompt + "\nẢnh 1 là ảnh cần chỉnh. Ảnh 2 chỉ là mẫu màu/ánh sáng. Giữ nguyên cảnh vật.", images);
        }
        var legend = new StringBuilder("Ảnh 1: kết quả hiện tại cần chỉnh bổ sung.\n");
        if (draft.Targets.Length > 0)
        {
            images.Add(new(Guide(item.InputPath, draft.Targets), "Bản hướng dẫn vùng đích T"));
            legend.AppendLine("Ảnh 2: bản hướng dẫn của ảnh 1; box T1, T2… chỉ đánh dấu vị trí, không phải nội dung cần đưa vào ảnh.");
            for (int i = 0; i < draft.Targets.Length; i++)
            {
                var rect = draft.Targets[i].Pixels(new(item.Job.Width, item.Job.Height));
                legend.AppendLine($"T{i + 1}: x={rect.X}, y={rect.Y}, rộng={rect.Width}, cao={rect.Height} pixel trên ảnh 1.");
            }
        }
        for (int r = 0; r < draft.References.Length; r++)
        {
            var reference = draft.References[r]; var bitmap = ImageFiles.Load(reference.Path);
            if (reference.Regions.Length == 0)
            {
                images.Add(new(ImageFiles.DataUrl(bitmap), $"A{r + 1}"));
                legend.AppendLine($"Ảnh {images.Count}: A{r + 1}, toàn bộ ảnh tham chiếu.");
            }
            else for (int b = 0; b < reference.Regions.Length; b++)
            {
                var cropped = new CroppedBitmap(bitmap, reference.Regions[b].Pixels(new(bitmap.PixelWidth, bitmap.PixelHeight))); cropped.Freeze();
                var label = $"A{r + 1}.{b + 1}";
                images.Add(new(ImageFiles.DataUrl(cropped), label));
                legend.AppendLine($"Ảnh {images.Count}: vùng nguồn {label}, đã cắt chính xác từ ảnh tham chiếu A{r + 1}.");
            }
        }
        return new("CHỈNH BỔ SUNG\n" + draft.Prompt + "\n\nQUY CHIẾU ẢNH VÀ VÙNG\n" + legend
            + "\nBox là chỉ dẫn vị trí cho yêu cầu trên, không phải mask cứng. Hoàn thiện cả ảnh theo yêu cầu; giữ các chi tiết không cần thay đổi. Không sao chép box hoặc nhãn từ ảnh hướng dẫn.", images);
    }
    public static string Guide(string path, IReadOnlyList<ImageRegion> regions)
    {
        var bitmap = ImageFiles.Load(path, 1500);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
            for (int i = 0; i < regions.Count; i++)
            {
                var pixels = regions[i].Pixels(new(bitmap.PixelWidth, bitmap.PixelHeight));
                var rect = new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height);
                dc.DrawRectangle(null, new Pen(Brushes.Red, 4), rect);
                var label = new FormattedText($"T{i + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 24, Brushes.White, 1);
                dc.DrawRectangle(Brushes.DarkRed, null, new Rect(rect.X, rect.Y, label.Width + 10, label.Height + 4));
                dc.DrawText(label, new Point(rect.X + 5, rect.Y + 2));
            }
        }
        var render = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual); render.Freeze(); return ImageFiles.DataUrl(render);
    }
}
