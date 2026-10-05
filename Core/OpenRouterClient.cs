using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PhotoTone.Core;

public sealed class OpenRouterClient : IDisposable
{
    private readonly HttpClient http;
    private const long MaxResponseBytes = 90L * 1024 * 1024;
    public OpenRouterClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(12);
    }
    public static Uri Endpoint(string apiBase, string relative)
    {
        if (!Uri.TryCreate(apiBase.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("API URL phải dùng HTTPS, không chứa tài khoản, query hoặc fragment.");
        return new Uri(uri, relative);
    }
    public async Task<IReadOnlyList<ImageModel>> ModelsAsync(string apiBase, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(apiBase, "images/models"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var text = await ReadBounded(response.Content, 8 * 1024 * 1024, cancellationToken);
        if (!response.IsSuccessStatusCode) throw ApiError(response, text, "");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => item.TryGetProperty("architecture", out var architecture)
                && architecture.GetProperty("input_modalities").EnumerateArray().Any(v => v.GetString() == "image")
                && architecture.GetProperty("output_modalities").EnumerateArray().Any(v => v.GetString() == "image")
                && item.TryGetProperty("supported_parameters", out var parameters)
                && parameters.TryGetProperty("input_references", out var references)
                && references.TryGetProperty("max", out var max) && max.GetInt32() >= 1)
            .Select(item => new ImageModel(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, item.GetProperty("supported_parameters").Clone()))
            .OrderBy(item => item.Id).ToArray();
    }
    public static string ClosestAspect(ImageSize size, string[] ratios)
    {
        var supported = ratios.Where(r => r != "auto").Select(r => (Label: r, Parts: r.Split(':')))
            .Where(r => r.Parts.Length == 2 && double.TryParse(r.Parts[0], out _) && double.TryParse(r.Parts[1], out _))
            .Select(r => (r.Label, Value: double.Parse(r.Parts[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(r.Parts[1], System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(r => Math.Abs(Math.Log(r.Value / ((double)size.Width / size.Height)))).ToArray();
        if (supported.Length == 0) throw new InvalidOperationException("Model không công bố tỷ lệ ảnh được hỗ trợ.");
        if (Math.Abs(supported[0].Value / ((double)size.Width / size.Height) - 1) > .0075)
            throw new InvalidOperationException("Model không hỗ trợ tỷ lệ ảnh gốc. Hãy chọn model khác.");
        return supported[0].Label;
    }
    public static Dictionary<string, object> RequestBody(BatchOptions options, ImageSize size, string source, string reference)
        => RequestBody(options, size, new PreparedEdit(options.Prompt + "\nẢnh 1 là ảnh cần chỉnh. Ảnh 2 chỉ là mẫu màu/ánh sáng. Giữ nguyên cảnh vật.", [new(source, "Ảnh chính"), new(reference, "Ảnh mẫu")]));
    public static Dictionary<string, object> RequestBody(BatchOptions options, ImageSize size, PreparedEdit edit)
    {
        var plan = ResolutionPlan.Create(options.Model, options.Resolution, size);
        ValidateReferences(options.Model, edit.Images.Count);
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model.Id,
            ["prompt"] = edit.Prompt + $"\n\nĐầu ra phải cùng tỷ lệ và ít nhất {size.Width}x{size.Height} pixel. Trả đúng một ảnh hoàn chỉnh, không trả ảnh hướng dẫn, không vẽ box/nhãn lên kết quả.",
            ["n"] = 1,
            ["input_references"] = edit.Images.Select(i => new { type = "image_url", image_url = new { url = i.DataUrl } }).ToArray(),
            ["provider"] = new { allow_fallbacks = false }
        };
        body[plan.Parameter] = plan.Value;
        if (plan.AspectRatio is not null) body["aspect_ratio"] = plan.AspectRatio;
        if (options.Model.Values("quality").Contains("high")) body["quality"] = "high";
        return body;
    }
    public static void ValidateReferences(ImageModel model, int count)
    {
        if (count < Math.Max(1, model.MinReferences) || count > model.MaxReferences) throw new InvalidOperationException($"Cần {count} ảnh đầu vào; {model.Id} nhận {Math.Max(1, model.MinReferences)}–{model.MaxReferences} ảnh.");
    }
    public async Task<ImageResult> EditAsync(BatchOptions options, ImageSize size, string source, string reference, CancellationToken token)
        => await EditAsync(options, size, new PreparedEdit(options.Prompt + "\nẢnh 1 là ảnh cần chỉnh. Ảnh 2 chỉ là mẫu màu/ánh sáng. Giữ nguyên cảnh vật.", [new(source, "Ảnh chính"), new(reference, "Ảnh mẫu")]), token);
    public async Task<ImageResult> EditAsync(BatchOptions options, ImageSize size, PreparedEdit edit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options.ApiBase, "images"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Key.Trim());
        request.Headers.Add("X-Title", "PhotoTone");
        request.Content = new StringContent(JsonSerializer.Serialize(RequestBody(options, size, edit)), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var text = await ReadBounded(response.Content, MaxResponseBytes, token);
        if (!response.IsSuccessStatusCode) throw ApiError(response, text, options.Key);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            throw new InvalidDataException("Model không trả ảnh. Không tự đổi model hoặc gửi lại.");
        var item = data[0];
        if (!item.TryGetProperty("b64_json", out var b64) || string.IsNullOrWhiteSpace(b64.GetString()))
            throw new InvalidDataException("API không trả dữ liệu ảnh base64 theo Image API.");
        decimal? cost = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("cost", out var amount) && amount.TryGetDecimal(out var value) ? value : null;
        return new(Convert.FromBase64String(b64.GetString()!), cost, options.Model.Id,
            response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null);
    }
    private static Exception ApiError(HttpResponseMessage response, string body, string key)
    {
        string message;
        try
        {
            using var json = JsonDocument.Parse(body);
            message = json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? response.ReasonPhrase ?? "Lỗi API";
        }
        catch { message = response.ReasonPhrase ?? "Lỗi API"; }
        if (key.Length > 0) message = message.Replace(key, "[đã ẩn]", StringComparison.Ordinal);
        return new HttpRequestException($"HTTP {(int)response.StatusCode}: {message[..Math.Min(message.Length, 600)]}");
    }
    private static async Task<string> ReadBounded(HttpContent content, long max, CancellationToken token)
    {
        if (content.Headers.ContentLength > max) throw new InvalidDataException("Phản hồi API vượt dung lượng cho phép.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > max) throw new InvalidDataException("Phản hồi API vượt dung lượng cho phép.");
            await memory.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
    public void Dispose() => http.Dispose();
}
