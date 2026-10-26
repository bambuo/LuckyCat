using CryptoExchange.Net.SharedApis;
using LuckyCat.Services;
using Xunit;

namespace LuckyCat.Tests;

/// <summary>
/// KlineVolume 的构造型单测（不跑真实链路）：SharedKline 直接构造，
/// 验证优先取 <c>Volumes.QuantityInBaseAsset</c>，缺失/为 0 时退回 Gate 只回填的旧 <c>Volume</c>。
/// </summary>
public class MarketSnapshotServiceTests
{
    private static readonly SharedSymbol Symbol = new(TradingMode.PerpetualLinear, "BTC", "USDT");
    private static readonly DateTime OpenTime = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Kline_volume_prefers_base_quantity_over_legacy_volume()
    {
        var kline = Kline(new SharedOrderQuantity(baseAssetQuantity: 2.5m, quoteAssetQuantity: 250m), volume: 999m);

        // Volumes 带基准资产数量：优先于旧 Volume（两值不同，防止实现顺序被调换）。
        Assert.Equal(2.5m, MarketSnapshotService.KlineVolume(kline));
    }

    [Fact]
    public void Kline_volume_falls_back_to_legacy_volume_when_volumes_missing_or_zero()
    {
        // Gate 共享适配器只回填 obsolete 的 Volume：Volumes 为 null 时兜底。
        Assert.Equal(12.5m, MarketSnapshotService.KlineVolume(Kline(null, volume: 12.5m)));

        // Volumes 存在但基准资产数量为 0（或仅报价数量）同样兜底。
        Assert.Equal(7.25m, MarketSnapshotService.KlineVolume(
            Kline(new SharedOrderQuantity(baseAssetQuantity: 0m, quoteAssetQuantity: 0m), volume: 7.25m)));
        Assert.Equal(3.5m, MarketSnapshotService.KlineVolume(
            Kline(new SharedOrderQuantity(quoteAssetQuantity: 350m), volume: 3.5m)));
    }

    [Fact]
    public void Kline_volume_is_zero_when_both_sources_empty()
    {
        Assert.Equal(0m, MarketSnapshotService.KlineVolume(Kline(null, volume: 0m)));
        Assert.Equal(0m, MarketSnapshotService.KlineVolume(Kline(new SharedOrderQuantity(), volume: 0m)));
    }

    private static SharedKline Kline(SharedOrderQuantity? volumes, decimal volume) =>
        new(Symbol, "BTC/USDT", OpenTime,
            closePrice: 100m, highPrice: 101m, lowPrice: 99m, openPrice: 99.5m, volumes: volumes!)
        {
#pragma warning disable CS0618 // 测试必须回填 Gate 仍在用的旧字段，才能验证兜底
            Volume = volume,
#pragma warning restore CS0618
        };
}
