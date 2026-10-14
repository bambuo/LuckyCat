using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuckyCat.Contracts;

public record VerificationEvent(
    string Type,
    string? Check = null,
    string? Status = null,
    long? ElapsedMs = null,
    string? Detail = null,
    string? Evidence = null,
    int? Count = null,
    long? TtftMs = null,
    int? Passed = null,
    int? Failed = null);

public static class VerificationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
