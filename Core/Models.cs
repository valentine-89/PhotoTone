using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoTone.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

public sealed class PhotoJob : Observable
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string SourcePath { get; init; }
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(SourcePath);
    public int Width { get; init; }
    public int Height { get; init; }
    [JsonIgnore] public string Dimensions => $"{Width} × {Height}";
    public FileStamp? SourceStamp { get; set; }
    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    private string state = "Sẵn sàng";
    public string State { get => state; set => Set(ref state, value); }
    private string detail = "";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private ResultVersion? current;
    public ResultVersion? Current { get => current; set { if (Set(ref current, value)) RefreshResult(); } }
    private ResultVersion? previous;
    public ResultVersion? Previous { get => previous; set { if (Set(ref previous, value)) RefreshResult(); } }
    public AttemptInfo? LastAttempt { get; set; }
    [JsonIgnore] public string? OutputPath => Current?.Path;
    private bool busy;
    [JsonIgnore] public bool Busy { get => busy; set { if (Set(ref busy, value)) RefreshResult(); } }
    [JsonIgnore] public bool CanEdit => Current is not null && !Busy;
    [JsonIgnore] public bool CanUndo => Previous is not null && !Busy;
    [JsonIgnore] public string ExportLabel => Current is null ? "" : Current.Exported ? "Đã xuất" : "Chưa xuất";
    public void RefreshResult() { Changed(nameof(OutputPath)); Changed(nameof(CanEdit)); Changed(nameof(CanUndo)); Changed(nameof(ExportLabel)); }
    private decimal? cost;
    public decimal? Cost { get => cost; set { if (Set(ref cost, value)) Changed(nameof(CostLabel)); } }
    [JsonIgnore] public string CostLabel => Cost is {} amount ? $"${amount:0.0000}" : "";
}

public sealed class Settings
{
    public string ApiBase { get; set; } = "https://openrouter.ai/api/v1";
    public string EncryptedKey { get; set; } = "";
    public string KeyEndpoint { get; set; } = "";
    public string Model { get; set; } = "google/gemini-3.1-flash-image";
    public string Resolution { get; set; } = "4K";
    public int Concurrency { get; set; } = 1;
    public string Format { get; set; } = "JPEG";
    public string ReferencePath { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
}

public sealed record ImageModel(string Id, string Name, JsonElement Parameters)
{
    public override string ToString() => Id;
    public bool Supports(string parameter) => Parameters.ValueKind == JsonValueKind.Object && Parameters.TryGetProperty(parameter, out _);
    public string[] Values(string parameter) => Supports(parameter) && Parameters.GetProperty(parameter).TryGetProperty("values", out var values)
        ? values.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
    public int MaxReferences => Supports("input_references") && Parameters.GetProperty("input_references").TryGetProperty("max", out var value) ? value.GetInt32() : 0;
    public int MinReferences => Supports("input_references") && Parameters.GetProperty("input_references").TryGetProperty("min", out var value) ? value.GetInt32() : 0;
}

public sealed record ImageSize(int Width, int Height)
{
    public long Pixels => (long)Width * Height;
    public override string ToString() => $"{Width} × {Height}";
}
public sealed record ImageResult(byte[] Bytes, decimal? Cost, string Model, string? RequestId);
public sealed record BatchOptions(string ApiBase, string Key, ImageModel Model, string Resolution,
    int Concurrency, string Format, string ReferencePath, string Prompt, string OutputDirectory);

public sealed record ImageRegion(double X, double Y, double Width, double Height)
{
    public void Validate()
    {
        if (!double.IsFinite(X + Y + Width + Height) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1.0000001 || Y + Height > 1.0000001)
            throw new InvalidOperationException("Vùng chọn nằm ngoài ảnh hoặc không có diện tích.");
    }
    public System.Windows.Int32Rect Pixels(ImageSize size)
    {
        Validate();
        int x = Math.Clamp((int)Math.Floor(X * size.Width), 0, size.Width - 1);
        int y = Math.Clamp((int)Math.Floor(Y * size.Height), 0, size.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling((X + Width) * size.Width), x + 1, size.Width);
        int bottom = Math.Clamp((int)Math.Ceiling((Y + Height) * size.Height), y + 1, size.Height);
        return new(x, y, right - x, bottom - y);
    }
}
public sealed record ReferenceSelection(string Path, FileStamp Stamp, ImageRegion[] Regions);
public sealed record EditDraft(string Prompt, ImageRegion[] Targets, ReferenceSelection[] References);
public sealed record RequestImage(string DataUrl, string Role);
public sealed record PreparedEdit(string Prompt, IReadOnlyList<RequestImage> Images);
public sealed record WorkItem(PhotoJob Job, BatchOptions Options, EditDraft? Draft, string InputPath, FileStamp InputStamp, string? ParentVersion, FileStamp? InitialReferenceStamp = null);
public sealed class AttemptInfo
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "initial";
    public string Status { get; set; } = "queued";
    public string Model { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string Prompt { get; init; } = "";
    public EditDraft? Draft { get; init; }
    public string? ParentVersion { get; init; }
    public string? InputSha256 { get; init; }
    public string? ReferencePath { get; init; }
    public FileStamp? ReferenceStamp { get; init; }
    public string? RequestId { get; set; }
    public decimal? Cost { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
}
public sealed class ResultVersion
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Path { get; init; }
    public bool Exported { get; set; }
    public required ImageSize ProviderSize { get; init; }
    public required AttemptInfo Attempt { get; init; }
}
