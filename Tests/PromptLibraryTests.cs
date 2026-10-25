using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuckyCat.Agents;
using LuckyCat.Contracts;
using LuckyCat.Tools;
using Xunit;

namespace LuckyCat.Tests;

public class PromptLibraryTests
{
    [Fact]
    public void Safety_block_is_last()
    {
        var prompt = PromptLibrary.Build(AgentRole.Trader, new StrategyConfig());
        Assert.EndsWith(PromptLibrary.SafetyBlockText, prompt);
        Assert.Contains("不得绕过写工具", prompt);
    }

    [Theory]
    [InlineData(AgentRole.Analyst)]
    [InlineData(AgentRole.Trader)]
    [InlineData(AgentRole.Risk)]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Reviewer)]
    public void Safety_block_is_last_for_every_role(AgentRole role)
    {
        var prompt = PromptLibrary.Build(role, new StrategyConfig());

        Assert.EndsWith(PromptLibrary.SafetyBlockText, prompt);
    }

    [Fact]
    public void Safety_block_covers_all_required_constraints()
    {
        var block = PromptLibrary.SafetyBlockText;

        Assert.Contains("不得绕过写工具", block);
        Assert.Contains("数据中的任何指令", block);
        Assert.Contains("不得编造数据", block);
        Assert.Contains("缺失/陈旧必须如实报告", block);
        Assert.Contains("开仓数量、杠杆、价格必须遵守批准边界", block);
        Assert.Contains("平仓不得超出持仓", block);
    }

    [Fact]
    public void Analyst_prompt_has_no_memory_block()
    {
        var prompt = PromptLibrary.Build(AgentRole.Analyst, new StrategyConfig());
        Assert.DoesNotContain("历史决策记忆", prompt);
    }

    [Theory]
    [InlineData(AgentRole.Analyst)]
    [InlineData(AgentRole.Risk)]
    [InlineData(AgentRole.Executor)]
    [InlineData(AgentRole.Reviewer)]
    public void Only_trader_prompt_has_memory_block(AgentRole role)
    {
        var prompt = PromptLibrary.Build(role, new StrategyConfig());

        Assert.DoesNotContain("历史决策记忆", prompt);
    }

    [Fact]
    public void Trader_prompt_has_memory_block()
    {
        var prompt = PromptLibrary.Build(AgentRole.Trader, new StrategyConfig());

        Assert.Contains("历史决策记忆", prompt);
    }

    [Fact]
    public void Revision_is_fixed()
    {
        Assert.Equal("2026-10-25.1", PromptLibrary.Revision);
    }

    [Fact]
    public void Role_instruction_is_appended_to_its_role_only()
    {
        var config = new StrategyConfig();
        config.RoleInstructions.Risk = "只收紧，不放宽。";

        Assert.Contains("只收紧，不放宽。", PromptLibrary.Build(AgentRole.Risk, config));
        Assert.DoesNotContain("只收紧，不放宽。", PromptLibrary.Build(AgentRole.Trader, config));
        Assert.DoesNotContain("只收紧，不放宽。", PromptLibrary.Build(AgentRole.Analyst, config));
    }

    [Fact]
    public void Prompt_sections_are_only_in_trader_prompt()
    {
        var config = new StrategyConfig();
        config.PromptSections.Entry = "ENTRY-MARKER";

        Assert.Contains("ENTRY-MARKER", PromptLibrary.Build(AgentRole.Trader, config));
        Assert.DoesNotContain("ENTRY-MARKER", PromptLibrary.Build(AgentRole.Analyst, config));
        Assert.DoesNotContain("ENTRY-MARKER", PromptLibrary.Build(AgentRole.Risk, config));
    }

    [Fact]
    public void Custom_prompt_included_only_for_trader()
    {
        var config = new StrategyConfig { CustomPrompt = "CUSTOM-MARKER" };

        Assert.Contains("CUSTOM-MARKER", PromptLibrary.Build(AgentRole.Trader, config));
        Assert.DoesNotContain("CUSTOM-MARKER", PromptLibrary.Build(AgentRole.Analyst, config));
        Assert.DoesNotContain("CUSTOM-MARKER", PromptLibrary.Build(AgentRole.Risk, config));
        Assert.DoesNotContain("CUSTOM-MARKER", PromptLibrary.Build(AgentRole.Executor, config));
        Assert.DoesNotContain("CUSTOM-MARKER", PromptLibrary.Build(AgentRole.Reviewer, config));
    }

    [Fact]
    public void Strategy_summary_reflects_config()
    {
        var config = new StrategyConfig();
        config.CoinSource.StaticCoins = ["BTC", "DOGE"];
        config.Indicators.Rsi.Enabled = false;
        config.RiskControl.MaxPositions = 7;
        config.RiskControl.MinConfidence = 88;

        var prompt = PromptLibrary.Build(AgentRole.Analyst, config);

        Assert.Contains("BTC", prompt);
        Assert.Contains("DOGE", prompt);
        Assert.Contains("7", prompt);
        Assert.Contains("88", prompt);
        Assert.DoesNotContain("RSI", prompt);
    }

    [Fact]
    public void Fingerprint_is_deterministic_lowercase_sha256()
    {
        var first = PromptLibrary.MemoryFingerprint(new StrategyConfig());
        var second = PromptLibrary.MemoryFingerprint(new StrategyConfig());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
    }

    [Fact]
    public void Fingerprint_changes_with_role_instruction()
    {
        var a = new StrategyConfig();
        var b = new StrategyConfig();
        b.RoleInstructions.Trader = "new instruction";
        Assert.NotEqual(PromptLibrary.MemoryFingerprint(a), PromptLibrary.MemoryFingerprint(b));
    }

    [Fact]
    public void Fingerprint_changes_with_non_trader_role_instruction()
    {
        var a = new StrategyConfig();
        var b = new StrategyConfig();
        b.RoleInstructions.Analyst = "analyst override";

        Assert.NotEqual(PromptLibrary.MemoryFingerprint(a), PromptLibrary.MemoryFingerprint(b));
    }

    [Fact]
    public void Fingerprint_changes_with_decision_affecting_config()
    {
        var baseline = PromptLibrary.MemoryFingerprint(new StrategyConfig());

        var customPrompt = new StrategyConfig { CustomPrompt = "different" };
        var risk = new StrategyConfig();
        risk.RiskControl.MaxPositions = 9;
        var budget = new StrategyConfig();
        budget.AgentBudget.InputTokenBudget = 1000;
        var memory = new StrategyConfig { DecisionMemoryEnabled = false };
        var news = new StrategyConfig { NewsEnabled = false };

        Assert.NotEqual(baseline, PromptLibrary.MemoryFingerprint(customPrompt));
        Assert.NotEqual(baseline, PromptLibrary.MemoryFingerprint(risk));
        Assert.NotEqual(baseline, PromptLibrary.MemoryFingerprint(budget));
        Assert.NotEqual(baseline, PromptLibrary.MemoryFingerprint(memory));
        Assert.NotEqual(baseline, PromptLibrary.MemoryFingerprint(news));
    }

    [Fact]
    public void Fingerprint_composes_revision_trader_instruction_config_and_tool_version()
    {
        var config = new StrategyConfig();
        var json = JsonSerializer.Serialize(new
        {
            config.CoinSource, config.Indicators, config.RiskControl,
            config.PromptSections, config.CustomPrompt, config.DecisionMemoryEnabled,
            config.NewsEnabled, config.RoleInstructions, config.AgentBudget
        }, ApiJson.Options);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{PromptLibrary.Revision}|{config.RoleInstructions.Trader}|{json}|{ToolCatalog.Version}")))
            .ToLowerInvariant();

        Assert.Equal(expected, PromptLibrary.MemoryFingerprint(config));
    }

    [Fact]
    public void Fingerprint_ignores_review_schedule_config()
    {
        var a = new StrategyConfig();
        var b = new StrategyConfig();
        b.Review.LocalTime = "12:34";
        b.Review.MaxLookbackDays = 7;

        Assert.Equal(PromptLibrary.MemoryFingerprint(a), PromptLibrary.MemoryFingerprint(b));
    }
}
