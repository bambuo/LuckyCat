using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LuckyCat.Contracts;
using LuckyCat.Data;
using LuckyCat.Endpoints;
using LuckyCat.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LuckyCat.Tests;

public class StrategyHttpTests
{
    [Fact]
    public async Task RealHttpPreviewReflectsCostBudgetAndRejectsInvalidRisk()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        builder.Services.AddHttpClient();
        builder.Services.AddScoped<ChatClientFactory>();
        builder.Services.AddScoped<CoinRankingService>();
        builder.Services.AddScoped<MarketSnapshotService>();
        builder.Services.AddScoped<MarketSentimentService>();
        builder.Services.AddScoped<ExternalDataSourceService>();
        builder.Services.AddScoped<StrategyTestRunner>();
        builder.Services.AddScoped<DecisionMemoryService>();
        builder.Services.AddSingleton<SseEventHub>();
        // 独立 HTTP 宿主只注册策略端点，没有任何交易员/同步器后台服务。
        await using var app = builder.Build();
        app.MapStrategies();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var config = new StrategyConfig(); config.RiskControl.EstimatedFeePct = 0.12m;
            var response = await client.PostAsJsonAsync("/api/strategies/preview-prompt", new PromptPreviewRequest(config));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("手续费 0.12%", body.GetProperty("systemPrompt").GetString());
            Assert.Contains("方向周期=15m", body.GetProperty("systemPrompt").GetString());
            config.RiskControl.MaxMarginUsage = 101;
            response = await client.PostAsJsonAsync("/api/strategies/preview-prompt", new PromptPreviewRequest(config));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("保证金使用率", await response.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }
}
