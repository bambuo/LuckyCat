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

    [Fact]
    public void Missing_action_deserializes_to_hold_not_open()
    {
        const string json = """
        {
          "symbol": "BTC/USDT",
          "side": "long",
          "confidence": 0.68,
          "entryPrice": 68120.50,
          "thesis": "突破回踩"
        }
        """;
        var proposal = JsonSerializer.Deserialize<AgentTradeProposal>(json, AgentJson.Options)!;
        Assert.Equal(ProposalAction.Hold, proposal.Action);
    }

    [Fact]
    public void Missing_decision_deserializes_to_rejected_not_approved()
    {
        const string json = """
        { "reasons": ["杠杆过高"], "maxLeverage": 5 }
        """;
        var verdict = JsonSerializer.Deserialize<AgentRiskVerdict>(json, AgentJson.Options)!;
        Assert.Equal(RiskDecision.Rejected, verdict.Decision);
    }

    [Fact]
    public void Api_json_rejects_unknown_request_fields()
    {
        const string valid = """
        {"symbol":"BTC/USDT","quantity":"1.5","orderType":"market","price":"68120.50","reason":"手动平仓"}
        """;
        var request = JsonSerializer.Deserialize<ManualCloseRequest>(valid, ApiJson.Options)!;
        Assert.Equal("1.5", request.Quantity);
        Assert.Equal("68120.50", request.Price);

        const string withExtra = """
        {"symbol":"BTC/USDT","quantity":"1.5","orderType":"market","price":"68120.50","reason":"手动平仓","extra":true}
        """;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ManualCloseRequest>(withExtra, ApiJson.Options));
    }

    [Fact]
    public void Agent_json_maps_camel_case_enum_strings_and_numbers_and_skips_unknown_members()
    {
        const string json = """
        {
          "action": "open",
          "symbol": "BTC/USDT",
          "side": "long",
          "confidence": "0.68",
          "entryPrice": "68120.50",
          "thesis": "突破回踩",
          "unknownField": { "anything": true }
        }
        """;
        var proposal = JsonSerializer.Deserialize<AgentTradeProposal>(json, AgentJson.Options)!;
        Assert.Equal(ProposalAction.Open, proposal.Action);
        Assert.Equal(TradeSide.Long, proposal.Side);
        Assert.Equal(0.68m, proposal.Confidence);
        Assert.Equal(68120.50m, proposal.EntryPrice);
        Assert.Equal("突破回踩", proposal.Thesis);

        var verdict = JsonSerializer.Deserialize<AgentRiskVerdict>(
            """{"decision":"approved","reasons":[]}""", AgentJson.Options)!;
        Assert.Equal(RiskDecision.Approved, verdict.Decision);
    }
}
