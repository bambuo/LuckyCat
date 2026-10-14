using System.Text.Json;
using LuckyCat.Models;

namespace LuckyCat.Services;

/// <summary>保存在 DecisionJson._round 的轮次元数据，不引入数据库列。</summary>
public sealed record DecisionRoundMetadata(string Status, string? FailureStage, string? Error,
    string RawOutput, string PromptRevision, string? MemoryFingerprint, bool RequestSent)
{
    public static DecisionRoundMetadata? Read(DecisionRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.DecisionJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("_round", out var round)
                ? round.Deserialize<DecisionRoundMetadata>(PromptJson.Options) : null;
        }
        catch (JsonException) { return null; }
    }
}
