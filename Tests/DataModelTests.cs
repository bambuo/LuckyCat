using LuckyCat.Data;
using LuckyCat.Models;
using LuckyCat.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LuckyCat.Tests;

public class DataModelTests
{
    private static async Task<(SqliteConnection, AppDbContext)> DbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    [Fact]
    public void Ulid_is_26_chars_and_sortable()
    {
        var a = Ids.NewUlid();
        Thread.Sleep(2);
        var b = Ids.NewUlid();
        Assert.Equal(26, a.Length);
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }

    [Fact]
    public async Task Order_client_id_is_unique_per_cycle()
    {
        var (connection, db) = await DbAsync();
        await using var _ = connection;
        await using var __ = db;
        db.TradingCycles.Add(new TradingCycle
        {
            Id = Ids.NewUlid(), StrategyId = 1, Trigger = "schedule", Mode = "dry",
            Status = "running", StartedAt = DateTime.UtcNow, MemoryFingerprint = "f", StrategyConfigJson = "{}"
        });
        await db.SaveChangesAsync();
        var cycle = await db.TradingCycles.SingleAsync();
        db.TraderOrders.Add(Order(cycle.Id, "lc-1"));
        await db.SaveChangesAsync();
        db.TraderOrders.Add(Order(cycle.Id, "lc-1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static TraderOrder Order(string cycleId, string clientOrderId) => new()
    {
        TraderId = 1, At = DateTime.UtcNow, Action = "open", Side = "long", Symbol = "BTC/USDT",
        Quantity = 1, Price = 1, ClientOrderId = clientOrderId, CycleId = cycleId,
        ExchangeAccountId = 1, Status = "submitted", Simulated = true
    };
}
