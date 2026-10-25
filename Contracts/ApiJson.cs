using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuckyCat.Contracts;

// REST/SSE 统一序列化：camelCase 属性与枚举值。
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 请求体不得包含多余字段（LC-STD-03 §1.1）；仅影响反序列化。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 把 ASP.NET Core HTTP JSON（请求绑定与结果序列化）对齐到 <see cref="Options"/> 约定：
    /// camelCase、camelCase 枚举字符串、请求体禁止未映射字段。生产 Program 与测试宿主共用本入口，
    /// 保证「测试证明的绑定行为」与「运行时绑定行为」同源。
    /// </summary>
    public static void Configure(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = Options.PropertyNamingPolicy;
        target.DictionaryKeyPolicy = Options.DictionaryKeyPolicy;
        target.PropertyNameCaseInsensitive = Options.PropertyNameCaseInsensitive;
        target.UnmappedMemberHandling = Options.UnmappedMemberHandling;
        target.DefaultIgnoreCondition = Options.DefaultIgnoreCondition;
        foreach (var converter in Options.Converters)
        {
            target.Converters.Add(converter);
        }
    }
}

// 模型结构化输出反序列化：camelCase、大小写不敏感、忽略未映射字段（模型可能返回额外字段）。
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
