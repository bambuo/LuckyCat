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
        var entity = new TradeProposal
        {
            Id = "p1", CycleId = "c1", Action = "open", Symbol = "BTC/USDT", Side = "long",
            Confidence = 0.8m, EntryPrice = 100m, StopLossPrice = 95m, TakeProfitPrice = 110m,
            PositionPct = 3m, Leverage = 3m, Thesis = "test", Invalidation = "invalid",
            ReviewChecklistJson = """["检查方向","检查止损"]""", RawJson = "{}"
        };

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

    private static OpenRiskContext Context => new(1000, 1000, 0, 0, false, 100, 20, 0.01m, 0.01m, 5,
        TradingRiskPolicy.EvaluateDiscipline([], new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));
}
