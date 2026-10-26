using System.Text.Json;
using LuckyCat.Contracts;
using LuckyCat.Models;
using LuckyCat.Services;
using Xunit;

namespace LuckyCat.Tests;

public class ProposalRiskAdapterTests
{
    [Fact]
    public void Long_proposal_is_mapped_to_legacy_buy_contract()
    {
        var proposal = new AgentTradeProposal(ProposalAction.Open, "BTC/USDT", TradeSide.Long,
            0.72m, 100m, 95m, 110m, 5m, 10m, "test", "invalid", null);
        using var doc = JsonDocument.Parse(ProposalRiskAdapter.ToLegacyDecision(proposal).GetRawText());
        Assert.Equal("buy", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(5m, doc.RootElement.GetProperty("stop_loss_pct").GetDecimal());
        Assert.Equal(10m, doc.RootElement.GetProperty("take_profit_pct").GetDecimal());
        Assert.Equal(72m, doc.RootElement.GetProperty("confidence").GetDecimal());
    }

    [Fact]
    public void Short_proposal_maps_to_sell_and_take_profit_below_entry()
    {
        var proposal = new AgentTradeProposal(ProposalAction.Open, "BTC/USDT", TradeSide.Short,
            0.6m, 100m, 105m, 90m, 3m, 5m, "test", "invalid", null);
        using var doc = JsonDocument.Parse(ProposalRiskAdapter.ToLegacyDecision(proposal).GetRawText());
        Assert.Equal("sell", doc.RootElement.GetProperty("action").GetString());
        // 空头止损在入场价上方，按绝对价差换算：(105-100)/100 = 5%。
        Assert.Equal(5m, doc.RootElement.GetProperty("stop_loss_pct").GetDecimal());
        Assert.Equal(10m, doc.RootElement.GetProperty("take_profit_pct").GetDecimal());
    }

    [Theory]
    [InlineData(ProposalAction.Close)]
    [InlineData(ProposalAction.Hold)]
    public void Non_open_proposal_is_rejected_without_plan(ProposalAction action)
    {
        var proposal = new AgentTradeProposal(action, "BTC/USDT", TradeSide.Long,
            0.9m, 100m, 95m, 110m, 5m, 3m, "test", "invalid", null);
        var error = ProposalRiskAdapter.ValidateOpen(proposal, new RiskControlConfig(), Context, out var plan);
        Assert.Equal("仅支持开仓提案", error);
        Assert.Null(plan);
    }

    [Fact]
    public void Entity_overload_maps_to_executable_plan()
    {
        var entity = Entity();

        var contract = ProposalRiskAdapter.ToContract(entity);
        Assert.Equal(ProposalAction.Open, contract.Action);
        Assert.Equal(TradeSide.Long, contract.Side);
        Assert.Equal(new List<string> { "检查方向", "检查止损" }, contract.ReviewChecklist);

        var error = ProposalRiskAdapter.ValidateOpen(entity, new RiskControlConfig(), Context, out var plan);
        Assert.Null(error);
        Assert.NotNull(plan);
        Assert.Equal(95m, plan.StopLossPrice);
        Assert.Equal(110m, plan.TakeProfitPrice);
    }

    [Theory]
    [InlineData(TradeSide.Long, 101, 110, "开仓提案止损方向错误")] // 多单止损在入场价上方
    [InlineData(TradeSide.Long, 95, 99, "开仓提案止盈方向错误")]   // 多单止盈在入场价下方
    [InlineData(TradeSide.Short, 99, 90, "开仓提案止损方向错误")]  // 空单止损在入场价下方
    [InlineData(TradeSide.Short, 105, 101, "开仓提案止盈方向错误")] // 空单止盈在入场价上方
    public void Wrong_side_protection_is_rejected(TradeSide side, decimal stop, decimal take, string expected)
    {
        var proposal = Proposal(side, stop, take);
        var error = ProposalRiskAdapter.ValidateOpen(proposal, new RiskControlConfig(), Context, out var plan);
        Assert.Equal(expected, error);
        Assert.Null(plan);
    }

    [Theory]
    [InlineData(TradeSide.Long, 100, 110, "开仓提案止损方向错误")]  // 止损等于入场价不算保护
    [InlineData(TradeSide.Long, 95, 100, "开仓提案止盈方向错误")]   // 止盈等于入场价不算目标
    [InlineData(TradeSide.Short, 100, 90, "开仓提案止损方向错误")]
    [InlineData(TradeSide.Short, 105, 100, "开仓提案止盈方向错误")]
    public void Protection_at_entry_price_is_rejected(TradeSide side, decimal stop, decimal take, string expected)
    {
        var error = ProposalRiskAdapter.ValidateOpen(Proposal(side, stop, take), new RiskControlConfig(), Context, out var plan);
        Assert.Equal(expected, error);
        Assert.Null(plan);
    }

    [Fact]
    public void Missing_stop_loss_is_rejected()
    {
        var proposal = Proposal(TradeSide.Long, null, 110m);
        var error = ProposalRiskAdapter.ValidateOpen(proposal, new RiskControlConfig(), Context, out var plan);
        Assert.Equal("开仓提案缺少止损价", error);
        Assert.Null(plan);
    }

    [Fact]
    public void Missing_take_profit_is_rejected()
    {
        var proposal = Proposal(TradeSide.Long, 95m, null);
        var error = ProposalRiskAdapter.ValidateOpen(proposal, new RiskControlConfig(), Context, out var plan);
        Assert.Equal("开仓提案缺少止盈价", error);
        Assert.Null(plan);
    }

    [Fact]
    public void Open_without_side_is_rejected()
    {
        var proposal = new AgentTradeProposal(ProposalAction.Open, "BTC/USDT", null,
            0.9m, 100m, 95m, 110m, 3m, 3m, "test", "invalid", null);
        var error = ProposalRiskAdapter.ValidateOpen(proposal, new RiskControlConfig(), Context, out var plan);
        Assert.Equal("开仓提案缺少方向（long/short）", error);
        Assert.Null(plan);
    }

    [Fact]
    public void Open_without_side_maps_to_hold_not_short()
    {
        var proposal = new AgentTradeProposal(ProposalAction.Open, "BTC/USDT", null,
            0.9m, 100m, 95m, 110m, 3m, 3m, "test", "invalid", null);
        using var doc = JsonDocument.Parse(ProposalRiskAdapter.ToLegacyDecision(proposal).GetRawText());
        Assert.Equal("hold", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("hold", doc.RootElement.GetProperty("trigger").GetString());
    }

    [Theory]
    [InlineData(0.729, 72)]
    [InlineData(0.999, 99)]
    [InlineData(0.72, 72)]
    [InlineData(1.0, 100)]
    public void Confidence_is_floored_never_rounded_up(decimal confidence, decimal expected)
    {
        var proposal = new AgentTradeProposal(ProposalAction.Open, "BTC/USDT", TradeSide.Long,
            confidence, 100m, 95m, 110m, 5m, 10m, "test", "invalid", null);
        using var doc = JsonDocument.Parse(ProposalRiskAdapter.ToLegacyDecision(proposal).GetRawText());
        Assert.Equal(expected, doc.RootElement.GetProperty("confidence").GetDecimal());
    }

    [Theory]
    [InlineData(ProposalAction.Close, "close", null)]
    [InlineData(ProposalAction.Hold, "hold", "hold")]
    public void Close_and_hold_omit_open_only_fields(ProposalAction action, string expectedAction, string? expectedTrigger)
    {
        var proposal = new AgentTradeProposal(action, "BTC/USDT", TradeSide.Long,
            0.9m, 100m, 95m, 110m, 5m, 10m, "test", "invalid", null);
        using var doc = JsonDocument.Parse(ProposalRiskAdapter.ToLegacyDecision(proposal).GetRawText());
        Assert.Equal(expectedAction, doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(expectedTrigger, doc.RootElement.GetProperty("trigger").GetString());
        foreach (var key in new[] { "entry_price", "stop_loss_pct", "take_profit_pct", "position_pct", "leverage" })
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty(key).ValueKind);
    }

    [Fact]
    public void ToContract_does_not_throw_on_invalid_action_and_validate_returns_error()
    {
        var entity = Entity();
        entity.Action = "moon";

        var contract = ProposalRiskAdapter.ToContract(entity);
        Assert.Equal(ProposalAction.Hold, contract.Action);

        Assert.False(ProposalRiskAdapter.TryToContract(entity, out _, out var conversionError));
        Assert.NotNull(conversionError);

        var error = ProposalRiskAdapter.ValidateOpen(entity, new RiskControlConfig(), Context, out var plan);
        Assert.Equal(conversionError, error);
        Assert.Null(plan);
    }

    [Fact]
    public void ToContract_does_not_throw_on_invalid_side()
    {
        var entity = Entity();
        entity.Side = "sideways";

        Assert.False(ProposalRiskAdapter.TryToContract(entity, out _, out var error));
        Assert.Equal("提案方向无效：sideways", error);
        Assert.Equal(ProposalAction.Hold, ProposalRiskAdapter.ToContract(entity).Action);
        Assert.NotNull(ProposalRiskAdapter.ValidateOpen(entity, new RiskControlConfig(), Context, out _));
    }

    [Fact]
    public void ToContract_does_not_throw_on_invalid_checklist_json()
    {
        var entity = Entity();
        entity.ReviewChecklistJson = "{not-json";

        Assert.False(ProposalRiskAdapter.TryToContract(entity, out _, out var error));
        Assert.Equal("提案复核清单 JSON 无效", error);
        Assert.Null(ProposalRiskAdapter.ToContract(entity).ReviewChecklist);
        // 宽松转换路径同样不得抛出。
        _ = ProposalRiskAdapter.ToLegacyDecision(entity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Empty_or_whitespace_checklist_is_normalized_to_null(string checklistJson)
    {
        var entity = Entity();
        entity.ReviewChecklistJson = checklistJson;

        Assert.True(ProposalRiskAdapter.TryToContract(entity, out var contract, out var error));
        Assert.Null(error);
        Assert.Null(contract!.ReviewChecklist);

        var validationError = ProposalRiskAdapter.ValidateOpen(entity, new RiskControlConfig(), Context, out var plan);
        Assert.Null(validationError);
        Assert.NotNull(plan);
    }

    private static AgentTradeProposal Proposal(TradeSide side, decimal? stop, decimal? take) => new(
        ProposalAction.Open, "BTC/USDT", side, 0.9m, 100m, stop, take, 3m, 3m, "test", "invalid", null);

    private static TradeProposal Entity() => new()
    {
        Id = "p1", CycleId = "c1", Action = "open", Symbol = "BTC/USDT", Side = "long",
        Confidence = 0.8m, EntryPrice = 100m, StopLossPrice = 95m, TakeProfitPrice = 110m,
        PositionPct = 3m, Leverage = 3m, Thesis = "test", Invalidation = "invalid",
        ReviewChecklistJson = """["检查方向","检查止损"]""", RawJson = "{}"
    };

    private static OpenRiskContext Context => new(1000, 1000, 0, 0, false, 100, 20, 0.01m, 0.01m, 5,
        TradingRiskPolicy.EvaluateDiscipline([], new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
}
