namespace LuckyCat.Models;

public enum ProviderType
{
    OpenAICompatible,
    AzureOpenAI,
    Ollama,
}

public class Provider
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required ProviderType Type { get; set; }
    public required string BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public required string Model { get; set; }
    public string? ApiVersion { get; set; }

    // —— 模型能力配置（新增/编辑提供商时维护；详见策略工作室的 AI 测试接线）——
    /// <summary>思维链三态：default（不发送参数）| on | off</summary>
    public string ThinkingMode { get; set; } = "default";
    /// <summary>输入上下文大小（tokens；用于提示词超限估算）</summary>
    public int ContextTokens { get; set; } = 128000;
    /// <summary>输出上限（tokens；AI 测试的实际输出上限，0 表示取默认 16384）</summary>
    public int MaxOutputTokens { get; set; } = 16384;
    /// <summary>提示词超出上下文大小时是否自动裁剪行情数据（关闭则仅警示）</summary>
    public bool AutoCompressContext { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
