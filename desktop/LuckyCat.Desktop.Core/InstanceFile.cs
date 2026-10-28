using System.Text.Json;

namespace LuckyCat.Desktop.Core;

public sealed record InstanceInfo(string Url, int Pid);

public static class InstanceFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(string path, InstanceInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(info, JsonOptions));
    }

    public static InstanceInfo? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static async Task<bool> TryActivateAsync(InstanceInfo info, HttpClient client, CancellationToken ct = default)
    {
        // instance.json 可能被手工改坏：先校验绝对 http/https 地址，不能让坏数据在启动路径上抛异常。
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            return false;

        try
        {
            // instance.json 里保存的是 Uri.ToString()，带尾部斜杠；直接拼接会得到双斜杠。
            var url = $"{info.Url.TrimEnd('/')}/internal/activation";
            using var response = await client.PostAsync(url, null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or UriFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
