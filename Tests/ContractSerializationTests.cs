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
    public void Dashboard_dto_accepts_and_emits_all_amounts_as_strings_without_order_success()
    {
        // 看板响应的金额/数量/百分比一律字符串（LC-STD-03 §1.3），订单不再携带 success（§2.2）。
        // 用契约形状的 JSON 反序列化再序列化：DTO 一旦回退为 decimal/double，这里先失败。
        const string json = """
        {
          "trader": {
            "id": 1, "name": "t", "providerName": "p", "model": "m", "exchangeLabel": "GateIo",
            "accountName": "a", "strategyName": "s", "isRunning": true,
            "executionMode": "dry", "isCrossMargin": false, "scanIntervalMinutes": 3,
            "initialBalance": "1000", "startedAt": null, "lastCycleAt": null, "lastCycleStatus": null,
            "capabilityCanTrade": true, "capabilityNotes": [], "positionMode": null
          },
          "account": {
            "success": true, "error": null, "equity": "1000.5", "available": "900",
            "totalPnl": "10.5", "pnlPct": "1.05", "positionCount": 1, "marginUsedPct": "5",
            "at": "2026-01-01T00:00:00Z"
          },
          "samples": [{ "at": "2026-01-01T00:00:00Z", "equity": "1000" }],
          "positions": [{
            "symbol": "BTC/USDT", "side": "long", "quantity": "0.1", "averageOpenPrice": "100",
            "unrealizedPnl": "1", "liquidationPrice": "80", "leverage": "5"
          }],
          "history": {
            "success": true, "error": null,
            "metrics": {
              "totalTrades": 1, "winRate": "100", "totalPnl": "5", "totalFee": "0.1",
              "netPnl": "4.9", "profitFactor": "2", "avgWin": "5", "avgLoss": "-1",
              "maxDrawdownPct": "0", "sharpe": "1.5"
            },
            "directions": [{ "side": "long", "count": 1, "winRate": "100", "totalPnl": "5", "avgPnl": "5" }],
            "symbols": [{ "symbol": "BTC/USDT", "count": 1, "winRate": "100", "totalPnl": "5" }],
            "rows": [{
              "symbol": "BTC/USDT", "side": "long", "entryPrice": "100", "exitPrice": "110",
              "quantity": "0.1", "leverage": "5", "realizedPnl": "5", "fee": "0.1",
              "closeTime": "2026-01-01T00:00:00Z"
            }],
            "note": null
          },
          "recentOrders": [{
            "id": 1, "at": "2026-01-01T00:00:00Z", "symbol": "BTC/USDT", "action": "open",
            "quantity": "0.1", "price": "100", "note": null, "error": null
          }]
        }
        """;

        var dto = JsonSerializer.Deserialize<TraderDashboardDto>(json, ApiJson.Options)!;
        var root = JsonSerializer.SerializeToElement(dto, ApiJson.Options);

        AssertString(root.GetProperty("trader").GetProperty("initialBalance"));
        var account = root.GetProperty("account");
        foreach (var field in new[] { "equity", "available", "totalPnl", "pnlPct", "marginUsedPct" })
        {
            AssertString(account.GetProperty(field));
        }

        AssertString(root.GetProperty("samples")[0].GetProperty("equity"));
        var position = root.GetProperty("positions")[0];
        foreach (var field in new[] { "quantity", "averageOpenPrice", "unrealizedPnl", "liquidationPrice", "leverage" })
        {
            AssertString(position.GetProperty(field));
        }

        var metrics = root.GetProperty("history").GetProperty("metrics");
        foreach (var field in new[] { "winRate", "totalPnl", "totalFee", "netPnl", "profitFactor",
                     "avgWin", "avgLoss", "maxDrawdownPct", "sharpe" })
        {
            AssertString(metrics.GetProperty(field));
        }

        var direction = root.GetProperty("history").GetProperty("directions")[0];
        AssertString(direction.GetProperty("winRate"));
        AssertString(direction.GetProperty("totalPnl"));
        AssertString(direction.GetProperty("avgPnl"));
        var symbolStat = root.GetProperty("history").GetProperty("symbols")[0];
        AssertString(symbolStat.GetProperty("winRate"));
        AssertString(symbolStat.GetProperty("totalPnl"));

        var row = root.GetProperty("history").GetProperty("rows")[0];
        foreach (var field in new[] { "entryPrice", "exitPrice", "quantity", "leverage", "realizedPnl", "fee" })
        {
            AssertString(row.GetProperty(field));
        }

        var order = root.GetProperty("recentOrders")[0];
        AssertString(order.GetProperty("quantity"));
        AssertString(order.GetProperty("price"));
        Assert.False(order.TryGetProperty("success", out _));
    }

    private static void AssertString(JsonElement value) => Assert.Equal(JsonValueKind.String, value.ValueKind);

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
