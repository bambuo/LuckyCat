using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using LuckyCat.Models;

namespace LuckyCat.Services;

public class ChatClientFactory
{
    public virtual IChatClient Create(Provider provider)
    {
        var thinking = ThinkingParameter(provider);
        var inner = provider.Type switch
        {
            ProviderType.OpenAICompatible => CreateOpenAICompatible(provider, thinking),
            ProviderType.AzureOpenAI => CreateAzureOpenAI(provider, thinking),
            ProviderType.Ollama => CreateOllama(provider, thinking),
            _ => throw new NotSupportedException($"未知提供商类型：{provider.Type}"),
        };

        // M.E.AI 官方 OpenTelemetry 埋点（GenAI 语义约定）：显式指定源名，与 Program.cs 的 AddSource/AddMeter 对齐
        // （默认源名为 "Experimental.Microsoft.Extensions.AI"，不显式指定会与注册的监听源不匹配而静默不产出）
        return new ChatClientBuilder(inner).UseOpenTelemetry(sourceName: Telemetry.MeaiName).Build();
    }

    /// <summary>思维链三态映射为端点参数：default 不发送（避免向不支持的端点发送未知字段）；
    /// Ollama 端点用 think，OpenAI 兼容 / Azure 端点用 enable_thinking。</summary>
    public static KeyValuePair<string, object>? ThinkingParameter(Provider provider)
    {
        if (provider.ThinkingMode is not ("on" or "off")) return null;
        var key = provider.Type == ProviderType.Ollama ? "think" : "enable_thinking";
        return new KeyValuePair<string, object>(key, provider.ThinkingMode == "on");
    }

    private static IChatClient CreateOpenAICompatible(Provider provider, KeyValuePair<string, object>? thinking)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl) };
        ApplyThinkingPatch(options, thinking);
        var client = new OpenAIClient(
            new ApiKeyCredential(string.IsNullOrEmpty(provider.ApiKey) ? "not-needed" : provider.ApiKey),
            options);
        return client.GetChatClient(provider.Model).AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAI(Provider provider, KeyValuePair<string, object>? thinking)
    {
        // ApiVersion 支持 "2024-10-21" 与 "V2024_10_21" 两种写法；留空或无法识别时用 SDK 默认版本
        var options = TryParseServiceVersion(provider.ApiVersion, out var version)
            ? new AzureOpenAIClientOptions(version)
            : new AzureOpenAIClientOptions();
        ApplyThinkingPatch(options, thinking);
        var client = new AzureOpenAIClient(
            new Uri(provider.BaseUrl), new AzureKeyCredential(provider.ApiKey ?? ""), options);
        return client.GetChatClient(provider.Model).AsIChatClient();
    }

    private static IChatClient CreateOllama(Provider provider, KeyValuePair<string, object>? thinking)
    {
        var endpoint = new Uri(provider.BaseUrl);
        if (thinking is not { } patch)
        {
            return new OllamaApiClient(endpoint, provider.Model);
        }

        var http = new HttpClient(new JsonBodyPatchHandler(patch.Key, patch.Value)) { BaseAddress = endpoint };
        return new OllamaApiClient(http, provider.Model);
    }

    // MEAI 的 ChatOptions.AdditionalProperties 不会被 OpenAI SDK 落到请求体（实测确认），
    // 因此在传输层对 JSON body 打补丁；仅当提供商显式配置了思维链「开启/关闭」时启用
    private static void ApplyThinkingPatch(ClientPipelineOptions options, KeyValuePair<string, object>? thinking)
    {
        if (thinking is not { } patch) return;
        var http = new HttpClient(new JsonBodyPatchHandler(patch.Key, patch.Value));
        options.Transport = new HttpClientPipelineTransport(http);
    }

    private static bool TryParseServiceVersion(string? value, out AzureOpenAIClientOptions.ServiceVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = "V" + value.Trim().TrimStart('v', 'V').Replace('-', '_');
        return Enum.TryParse(normalized, ignoreCase: true, out version);
    }
}

/// <summary>把指定字段并入请求 JSON 的传输层补丁（用于思维链参数；仅显式配置时挂载）。</summary>
internal sealed class JsonBodyPatchHandler(string key, object value) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var json = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var patch = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (patch is not null)
                {
                    patch[key] = JsonSerializer.SerializeToElement(value);
                    request.Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json");
                }
            }
            catch (JsonException)
            {
                // 非 JSON 请求体：原样转发
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
