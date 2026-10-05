using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTone.Core;

public static class ImageFiles
{
    public const long MaxFileBytes = 40L * 1024 * 1024;
    public const long MaxPixels = 100_000_000;
    public static BitmapSource Load(string path, int previewWidth = 0)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Ảnh vượt giới hạn 40 MB.");
        using var stream = File.OpenRead(path);
        if (previewWidth > 0)
        {
            var preview = new BitmapImage();
            preview.BeginInit(); preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.DecodePixelWidth = previewWidth; preview.StreamSource = stream; preview.EndInit(); preview.Freeze();
            // Preview orientation follows the same metadata convention as upload/export.
            return Orient(preview, Orientation(path));
        }
        return Decode(stream, true);
    }
    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        return Decode(stream, true);
    }
    private static BitmapSource Decode(Stream stream, bool orient)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new InvalidDataException("Ảnh vượt giới hạn 100 megapixel.");
        BitmapSource pixels = frame;
        if (frame.ColorContexts is { Count: > 0 })
        {
            pixels = new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            pixels.Freeze();
        }
        var result = orient ? Orient(pixels, Orientation(frame.Metadata as BitmapMetadata)) : pixels;
        result.Freeze(); return result;
    }
    public static ImageSize Size(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var orientation = Orientation(frame.Metadata as BitmapMetadata);
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new InvalidDataException("Ảnh vượt giới hạn 100 megapixel.");
        return orientation >= 5 ? new(frame.PixelHeight, frame.PixelWidth) : new(frame.PixelWidth, frame.PixelHeight);
    }
    private static int Orientation(string path)
    {
        using var stream = File.OpenRead(path);
        return Orientation(BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].Metadata as BitmapMetadata);
    }
    private static int Orientation(BitmapMetadata? metadata)
    {
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try { if (metadata?.GetQuery(query) is object value) return Convert.ToInt32(value); }
            catch (NotSupportedException) { }
        }
        return 1;
    }
    private static BitmapSource Orient(BitmapSource source, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1), 3 => new RotateTransform(180), 4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0), 6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0), 8 => new RotateTransform(270), _ => null
        };
        if (transform is null) { source.Freeze(); return source; }
        var bitmap = new TransformedBitmap(source, transform); bitmap.Freeze(); return bitmap;
    }
    public static string DataUrl(string path)
    {
        // Normalize orientation without changing the native pixel dimensions. Strip EXIF from outbound pixels.
        return DataUrl(Load(path));
    }
    public static string DataUrl(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(SrgbFrame(source)); encoder.Save(stream);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Ảnh PNG gửi đi vượt 40 MB. Không tự giảm độ phân giải.");
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }
    public static void ValidateSize(ImageSize source, ImageSize result)
    {
        if (result.Width < source.Width || result.Height < source.Height || result.Pixels < source.Pixels)
            throw new InvalidDataException($"Model trả {result}; cần ít nhất {source}. Không phóng lớn bù. Hãy chọn model/độ phân giải khác.");
        var relativeAspect = (double)result.Width / result.Height / ((double)source.Width / source.Height);
        if (Math.Abs(relativeAspect - 1) > .0075)
            throw new InvalidDataException($"Sai tỷ lệ ảnh: model trả {result}, ảnh gốc {source}. Không kéo méo hoặc crop lớn.");
    }
    public static ImageSize Export(byte[] bytes, ImageSize expected, string output, string format)
    {
        var bitmap = Decode(bytes);
        var rawSize = new ImageSize(bitmap.PixelWidth, bitmap.PixelHeight);
        ValidateSize(expected, rawSize);
        BitmapSource final = bitmap;
        if (rawSize != expected)
        {
            // Only downsample a genuinely larger provider result to the ORIGINAL dimensions; never upscale.
            double ratio = Math.Max((double)expected.Width / rawSize.Width, (double)expected.Height / rawSize.Height);
            var resized = new TransformedBitmap(bitmap, new ScaleTransform(ratio, ratio)); resized.Freeze();
            int x = Math.Max(0, (resized.PixelWidth - expected.Width) / 2);
            int y = Math.Max(0, (resized.PixelHeight - expected.Height) / 2);
            final = new CroppedBitmap(resized, new Int32Rect(x, y, expected.Width, expected.Height)); final.Freeze();
        }
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            BitmapEncoder encoder = format == "PNG" ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 96 };
            encoder.Frames.Add(SrgbFrame(final));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
            if (Size(temporary) != expected) throw new InvalidDataException("Kích thước file xuất không khớp ảnh gốc.");
            File.Move(temporary, output, false);
            return rawSize;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static BitmapFrame SrgbFrame(BitmapSource source) => BitmapFrame.Create(source, null, null,
        new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(PixelFormats.Bgra32) }));
}
