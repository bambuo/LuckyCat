using System.Text.Json;
using LuckyCat.Contracts;
using Xunit;

namespace LuckyCat.Tests;

public class ContractSerializationTests
{
    [Fact]
    public void Enums_are_camel_case()
    {
        var json = JsonSerializer.Serialize(new { action = ProposalAction.Open }, ApiJson.Options);
        Assert.Contains("\"open\"", json);
    }

    [Fact]
    public void Api_dto_uses_string_decimals_and_camel_case()
    {
        var dto = new TradeProposalDto
        {
            Id = "01J", Action = "open", Symbol = "BTC/USDT", Side = "long",
            Confidence = "0.68", EntryPrice = "68120.50", PositionPct = "5.00", Leverage = "10.00",
            Thesis = "突破回踩"
        };
        var json = JsonSerializer.Serialize(ApiResponse<TradeProposalDto>.Ok(dto), ApiJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.GetProperty("data");
        Assert.Equal("68120.50", root.GetProperty("entryPrice").GetString());
        Assert.Equal("open", root.GetProperty("action").GetString());
    }

    [Fact]
    public void Error_envelope_has_code_and_message()
    {
        var json = JsonSerializer.Serialize(ApiResponse<object>.Fail("provider_capability_missing", "未验证"), ApiJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("provider_capability_missing", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
