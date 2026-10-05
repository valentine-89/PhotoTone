namespace PhotoTone.Core;

public sealed record ResolutionPlan(string Parameter, string Value, string? AspectRatio = null)
{
    // Normalized tiers are a conservative request choice, not a guarantee of provider pixel dimensions.
    // Export still validates the decoded result against the full original dimensions.
    private static readonly Dictionary<string, int> TierEdges = new(StringComparer.OrdinalIgnoreCase)
    { ["512"] = 512, ["768"] = 768, ["1K"] = 1024, ["1.5K"] = 1536, ["2K"] = 2048, ["4K"] = 4096 };

    public static ResolutionPlan Create(ImageModel model, string selection, ImageSize original)
    {
        if (selection != "Gốc")
        {
            if (!model.Values("resolution").Contains(selection))
                throw new InvalidOperationException($"{model.Id} không hỗ trợ {selection}. Chọn độ phân giải được liệt kê hoặc model có 4K.");
            return new("resolution", selection, OpenRouterClient.ClosestAspect(original, model.Values("aspect_ratio")));
        }

        var tier = model.Values("resolution")
            .Where(t => TierEdges.TryGetValue(t, out var edge) && edge >= Math.Max(original.Width, original.Height))
            .OrderBy(t => TierEdges[t]).FirstOrDefault();
        if (tier is not null)
            return new("resolution", tier, OpenRouterClient.ClosestAspect(original, model.Values("aspect_ratio")));

        // Prefer an explicit size allowlist when the provider advertises one.
        var sizes = model.Values("size");
        // OpenRouter's current catalog omits the fixed size enum for this model.
        // These three sizes come from the provider's observed HTTP 400 response (2026-10-05).
        if (sizes.Length == 0 && model.Id == "openai/gpt-5-image")
            sizes = ["1024x1024", "1024x1536", "1536x1024"];
        var candidate = sizes.Select(TrySize).Where(s => s is not null).Cast<ImageSize>()
            .Where(s => s.Width >= original.Width && s.Height >= original.Height
                && Math.Abs((double)s.Width / s.Height / ((double)original.Width / original.Height) - 1) <= .0075)
            .OrderBy(s => s.Pixels).FirstOrDefault();
        if (candidate is not null) return new("size", $"{candidate.Width}x{candidate.Height}");

        if (sizes.Length > 0)
            throw new InvalidOperationException($"{model.Id} chỉ hỗ trợ {string.Join(", ", sizes.Where(s => s != "auto"))}; không đủ cho ảnh {original}. Chọn model có 4K để giữ số pixel gốc.");
        throw new InvalidOperationException($"{model.Id} không công bố kích thước đủ cho ảnh {original}. Chọn model có 4K; app không gửi kích thước tùy ý hoặc phóng lớn bù.");
    }

    private static ImageSize? TrySize(string value)
    {
        var parts = value.ToLowerInvariant().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height) && width > 0 && height > 0
            ? new(width, height) : null;
    }
}
