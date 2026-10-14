using CryptoExchange.Net.SharedApis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Models;
using LuckyCat.Services;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LuckyCat.Tests;

public class RoundHttpTests
{
    [Theory]
    [InlineData("contract")]
    [InlineData("provider")]
    [InlineData("ok")]
    public async Task RealHttpTestRunPersistsSuccessAndFailureWithCompleteInputs(string outcome)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddHttpClient();
        var fake = new TestChatClient(outcome);
        builder.Services.AddSingleton<ChatClientFactory>(new TestChatFactory(fake));
        builder.Services.AddScoped<CoinRankingService>();
        builder.Services.AddSingleton<MarketSnapshotService>(new TestMarket());
        builder.Services.AddScoped<MarketSentimentService>();
        builder.Services.AddScoped<ExternalDataSourceService>();
        builder.Services.AddScoped<StrategyTestRunner>();
        builder.Services.AddScoped<DecisionMemoryService>();
        builder.Services.AddSingleton<SseEventHub>();
        await using var app = builder.Build();
        app.MapStrategies(); // 无交易员后台服务，全部行情和模型调用由 fake 提供。
        var config = new StrategyConfig { NewsEnabled = false };
        config.Indicators.OpenInterest.Enabled = false;
        config.Indicators.FundingRate.Enabled = false;
        config.CoinSource.StaticCoins = ["BTC"];
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Providers.Add(new Provider { Id = 1, Name = "test", Type = ProviderType.OpenAICompatible, BaseUrl = "http://invalid", Model = "test" });
            db.Strategies.Add(new Strategy { Id = 1, Name = "test", ConfigJson = JsonSerializer.Serialize(config) });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var response = await client.PostAsJsonAsync("/api/strategies/test-run", new StrategyTestRequest(config, 1, 1));
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<StrategyTestResultDto>();
            Assert.NotNull(result);
            Assert.True(result.RequestSent);
            Assert.Contains("程序计算的逐币条件", result.UserPrompt);
            Assert.Equal(1, fake.Calls);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await db.DecisionRecords.SingleAsync();
            var round = DecisionRoundMetadata.Read(record)!;
            Assert.Equal(outcome == "ok" ? "ok" : "error", round.Status);
            Assert.Equal(result.UserPrompt, record.UserPrompt);
            Assert.Equal(result.SystemPrompt, record.SystemPrompt);
            var dto = TradersEndpoints.ToDecisionDto(record);
            Assert.Equal(round.Status, dto.Status);
            Assert.Equal(round.RawOutput, dto.RawOutput);
            Assert.Equal(PromptComposer.Revision, dto.PromptRevision);
            if (outcome != "ok")
            {
                Assert.Equal(outcome, round.FailureStage);
                Assert.False(string.IsNullOrWhiteSpace(round.Error));
                Assert.Equal("error", record.Action);
                Assert.Empty(await scope.ServiceProvider.GetRequiredService<DecisionMemoryService>().LoadRecentAsync(1, default, fingerprint: PromptComposer.MemoryFingerprint(config)));
                if (outcome == "contract") { Assert.Contains("trigger", round.Error); Assert.Equal(fake.Raw, round.RawOutput); }
                else Assert.Contains("模拟模型服务失败", round.Error);
            }
            else
            {
                Assert.Null(round.Error);
                Assert.Equal("hold", record.Action);
            }
        }
        finally { await app.StopAsync(); }
    }

    private sealed class TestChatFactory(IChatClient client) : ChatClientFactory
    {
        public override IChatClient Create(Provider provider) => client;
    }

    private sealed class TestChatClient(string outcome) : IChatClient
    {
        public int Calls { get; private set; }
        public string Raw => outcome == "contract" ? "{\"action\":\"hold\",\"confidence\":72,\"reason\":\"等待\"}" :
            "{\"action\":\"hold\",\"confidence\":72,\"reason\":\"条件未齐\",\"trigger\":\"等待方向与位置确认\"}";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (outcome == "provider") throw new HttpRequestException("模拟模型服务失败");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Raw)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestMarket : MarketSnapshotService
    {
        public override Task<MarketSnapshot> GetAsync(string exchange, SharedSymbol symbol, string timeframe, int bars, CancellationToken ct, int computeBars = 0)
        {
            var now = DateTime.UtcNow;
            var span = IndicatorCalculator.TryParseInterval(timeframe)!.Value;
            var aligned = new DateTime(now.Ticks / span.Ticks * span.Ticks, DateTimeKind.Utc);
            var data = Enumerable.Range(0, 100).Select(i => new MarketKline(aligned - span * (100 - i), 100 + i, 102 + i, 99 + i, 101 + i, 1000)).ToList();
            return Task.FromResult(new MarketSnapshot("live", $"{symbol.BaseAsset}/USDT", 201, data, AsOfUtc: now));
        }
        public override Task<MarketSnapshot?> GetBtcOverviewAsync(string exchange, CancellationToken ct) => Task.FromResult<MarketSnapshot?>(null);
    }
}
