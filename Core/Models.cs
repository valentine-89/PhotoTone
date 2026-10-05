using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
    public string Name => System.IO.Path.GetFileName(SourcePath);
    public int Width { get; init; }
    public int Height { get; init; }
    public string Dimensions => $"{Width} × {Height}";
    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    private string state = "Sẵn sàng";
    public string State { get => state; set => Set(ref state, value); }
    private string detail = "";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private string? outputPath;
    public string? OutputPath { get => outputPath; set => Set(ref outputPath, value); }
    private decimal? cost;
    public decimal? Cost { get => cost; set { if (Set(ref cost, value)) Changed(nameof(CostLabel)); } }
    public string CostLabel => Cost is {} amount ? $"${amount:0.0000}" : "";
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
}

public sealed record ImageSize(int Width, int Height)
{
    public long Pixels => (long)Width * Height;
    public override string ToString() => $"{Width} × {Height}";
}
public sealed record ImageResult(byte[] Bytes, decimal? Cost, string Model, string? RequestId);
public sealed record BatchOptions(string ApiBase, string Key, ImageModel Model, string Resolution,
    int Concurrency, string Format, string ReferencePath, string Prompt, string OutputDirectory);
