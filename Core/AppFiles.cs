using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PhotoTone.Core;

public static class AppFiles
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTone");
    public static string Samples => Path.Combine(Root, "Samples");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Initialize()
    {
        Directory.CreateDirectory(Samples);
        foreach (var name in new[] { "ok.jpg", "raw.jpg", "raw1.jpg", "raw2.jpg" })
        {
            var target = Path.Combine(Samples, name);
            if (File.Exists(target)) continue;
            using var resource = Resource(name);
            using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            resource.CopyTo(file);
        }
    }
    public static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream($"PhotoTone.Assets.{name}")
        ?? throw new FileNotFoundException($"Thiếu dữ liệu nhúng: {name}");
    public static string DefaultPrompt { get { using var reader = new StreamReader(Resource("default-prompt.txt")); return reader.ReadToEnd(); } }
    public static Settings LoadSettings()
    {
        var file = Path.Combine(Root, "settings.json");
        var settings = File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new() : new Settings();
        if (string.IsNullOrWhiteSpace(settings.Prompt)) settings.Prompt = DefaultPrompt;
        if (string.IsNullOrWhiteSpace(settings.ReferencePath)) settings.ReferencePath = Path.Combine(Samples, "ok.jpg");
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory)) settings.OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhotoTone");
        settings.Concurrency = Math.Clamp(settings.Concurrency, 1, 4);
        return settings;
    }
    public static void SaveSettings(Settings settings) => AtomicJson(Path.Combine(Root, "settings.json"), settings);
    public static void AtomicJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
